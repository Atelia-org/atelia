# CS-5-lite: SessionJournal Derived Recap Store + RollingSummary Replay

> 状态：Implemented / CS-5-lite Complete
> 日期：2026-07-26
> 上层路线图：[SessionJournal 事件源会话与长期上下文架构路线图](../studies/event-sourced-session-architecture-roadmap.md)
> 相关设计：[SessionJournal Configuration Access Notes](../studies/session-configuration-access-notes.md)
>
> 后续收口（2026-07-27）：本文记录 CS-5-lite 落地时的 rolling-summary 术语。
> `ChatSession.BacktestCli` 随后拆分，现行代码入口是
> `SessionJournal.Cli run-memory-maintainer`；legacy replay adapter 已删除。详见
> [CLI 拆分说明](../../../ChatSession/legacy-export-and-sessionjournal-cli-split.md)。

## 1. 文档目的

本文给后续全新 coding 会话快速恢复上下文：为什么先做一个轻量的 SessionJournal derived recap
store，如何把现有 rolling summary replay 从 legacy export 迁到新的 SessionJournal raw event replay，
以及它与后续 tail-only projection / ContextPlan 的关系。

本文不是最终 Artifact Journal wire spec，也不要求一轮实现完整 CS-5。它定义的是一个过渡但方向正确的
垂直切片：先造出真实可加载的 recap anchor，再让 tail-only reducer 基于它切分 raw suffix。

## 2. 背景

当前已经完成的基础：

- `prototypes/SessionJournal` 已经能用 EventJournal 保存 raw session events，并支持 tool-loop
  逐事件恢复。
- legacy export 已能导入新的 SessionJournal repo；导入时跳过旧 `compaction` / `recap`，因为它们
  是 derived artifacts，不是 raw facts。
- `SessionJournalEngine.ResolveGoverningSetup(head)` 已能沿 parent chain 只读 header preview，解析
  `head` as-of 最近的 `runtime-config-setup` 与 `system-prompt-setup`，再只读这两个 setup payload。
- `prototypes/SessionJournal.Cli/MemoryMaintainerRun.cs` 使用 addressed SessionJournal replay、
  `_activeHistory`、`MemoryPack`、runner-local split policy 和
  `RewriteMemoryBlockMaintainer` 主链；拆分后的产品 runner 不再接受 legacy export stream。
- `DerivedRecapStore` 与 `SessionJournalDerivedRecapWriter` 已把成功候选写成带 raw range、governing
  setup、invocation 和 call-log provenance 的 derived artifact，不修改 raw event chain。
- 正式 CLI `run-memory-maintainer` 已接通上述主链，并完成 scripted E2E 与真实
  `dsv4p` 双 preset 验收。

当前设计判断：

- tail-only reducer 的真正难点是边界语义，不是读取性能。
- 没有 recap/artifact anchor 时，只能做跛脚的硬截断 fallback；不值得把这个 fallback 过度打磨。
- 更合理的推进顺序是：先让 rolling summary 作为 derived artifact 落盘并可加载，再让 tail-only
  projection 优先从 recap anchor 切分。

## 3. 核心目标

实现一个轻量切片：

```text
SessionJournal raw repo
-> forward replay raw events
-> sliding prefix selected for rolling summary
-> RewriteMemoryBlockMaintainer updates MemoryPack / recap block
-> derived recap artifact store
-> later Context Planner uses latest recap anchor + raw suffix
```

目标包括：

- `RollingSummaryReplay` 支持以新的 SessionJournal repo 作为输入，而不是只支持 legacy export JSON。
- 建立 recap 类 derived artifact 的最小磁盘和内存结构。
- 保存 rolling summary / MemoryPack 产物及其 provenance。
- 产物可加载，可删除后重建，不污染 raw SessionJournal event chain。
- 给后续 tail-only projection 提供真实 anchor：recap 覆盖到哪个 raw event，raw suffix 应从哪里继续。

## 4. 非目标

本阶段不要做：

- 不把 recap 写回 raw SessionJournal event chain。
- 不实现完整 ArtifactSet policy、retrieval read model、向量/图索引。
- 不实现最终 Context Planner / request manifest 的完整持久化合同。
- 不迁移所有 MemoryMaintainer，只先接通 rolling summary / recap 这一类。
- 不为了没有 recap 的 bootstrap fallback 设计复杂 turn window。

这些内容属于后续 CS-5 / CS-6 / CS-7。

## 5. 关键上下文文件

设计文档：

- [event-sourced-session-architecture-roadmap.md](../studies/event-sourced-session-architecture-roadmap.md)
  总路线图；CS-2.5 / CS-5-lite 已作为 CS-3 tail projection 前置切片记录。
- [session-configuration-access-notes.md](../studies/session-configuration-access-notes.md)
  governing setup resolver、near-head hint、recap anchor 与 tail projection 的关系。
- [session-journal-trunk-design.md](../superseded/session-journal-trunk-design.md)
  SessionJournal raw event schema、EventKind、canonical JSON、执行机边界。
- [memory-backtest-cli-plan.md](../../../ChatSession/memory-backtest-cli-plan.md)
  旧 backtest CLI 与 rolling summary replay 的设计背景。

主要代码入口：

- historical path `prototypes/SessionJournal.Cli/MemoryMaintainerRun.cs`：当时的 SessionJournal
  maintainer runner、addressed source 与 run record；由 `df8e3044` 删除，无 1:1 current successor。
- historical path `prototypes/SessionJournal.Cli/MemoryMaintainerArtifactWriting.cs`：当时的
  SessionJournal Derived Recap writer、producer fingerprint、lineage preflight 与写入边界；由
  `5c4d8327` cutover删除，无 1:1 current successor。
- [Program.cs](../../../../prototypes/SessionJournal.Cli/Program.cs)
  SessionJournal CLI 命令入口、connection 配置和 maintainer profile 参数。
- [SessionJournalLegacyImporter.cs](../../../../prototypes/SessionJournal.Cli/SessionJournalLegacyImporter.cs)
  legacy export -> SessionJournal repo importer；已跳过 legacy recap / compaction。
- [SessionJournalEngine.cs](../../../../prototypes/SessionJournal/SessionJournalEngine.cs)
  SessionJournal open/project/append/resolver 主入口。
- historical path `prototypes/SessionJournal/SessionReducer.cs`：当时的 raw events ->
  `SessionProjection` 纯 reducer；由 `34ad34e7` 删除，无 1:1 current successor。
- [SessionJournalContracts.cs](../../../../prototypes/SessionJournal/SessionJournalContracts.cs)
  `SessionEventKind`、`SessionProjection`、`SessionGoverningSetup` 等契约。
- historical path `prototypes/SessionJournal/SessionMemoryContracts.cs`：当时 SessionJournal主干的
  `MemoryPack`、`SessionContextHeader`、maintainer/orchestrator substrate；由 `44e535a7` contract
  cutover删除，无 1:1 current successor。
- [ChatSession Legacy Memory Substrate 退役](../../../ChatSession/legacy-memory-substrate-retirement.md)
  记录旧 ChatSession duplicate substrate 及 session-level maintainer API 的最终删除边界。
- [ChatSessionContracts.cs](../../../../prototypes/ChatSession/ChatSessionContracts.cs)
  `ContextHeader`、`RecapMessage`、`RecapSourceAnchor` 等旧 ChatSession context projection 类型。
- historical `AutobiographicalRecapMaintainers.cs`：当时的member definition示例；该owner已在WP-08
  formal RecapGrid cutover退役。
- historical `WorldUnderstandingRecapMaintainers.cs`：当时的member definition示例；该owner已在WP-08
  formal RecapGrid cutover退役。

底层遍历相关：

- [EventJournal.cs](../../../../src/EventJournal/EventJournal.cs)
  `ReadEvent`、`ReadEventHeaderPreview`、`ReadChronologicalChain` 等。
- [EventJournal.ForwardPlan.cs](../../../../src/EventJournal/EventJournal.ForwardPlan.cs)
  EventJournal forward replay / cache / tail merge 现有能力。

## 6. 建议数据模型

第一版已把 derived recap store 放在 SessionJournal repo 内：

```text
derived/recaps/v1/
  artifacts/<artifact-id>.json
  indexes/latest-by-profile.json
```

具体路径可调整，但语义应保持：

- `artifacts` 是 append-only 产物记录。
- `indexes` 是可删除、可重建的 read model。

最小 artifact 字段建议：

```json
{
  "schema": "atelia.session-journal.derived-recap.v1",
  "artifactId": "...",
  "artifactKind": "rolling-summary",
  "profileId": "...",
  "producer": "...",
  "producerFingerprint": "...",
  "sourceRawHead": "<EventAddress>",
  "sourceStartExclusive": "<EventAddress|null>",
  "sourceEndInclusive": "<EventAddress>",
  "anchorRawEvent": "<EventAddress>",
  "governingRuntimeConfigSetup": "<EventAddress>",
  "governingSystemPromptSetup": "<EventAddress>",
  "previousArtifact": "<artifact-id|null>",
  "memoryPack": { },
  "content": "... or blob ref",
  "invocation": { },
  "status": "produced"
}
```

字段含义：

- `sourceRawHead`：producer 当时观察到的 raw branch head。
- `sourceStartExclusive` / `sourceEndInclusive`：本次被 rolling summary 吸收的 raw 范围。
- `anchorRawEvent`：后续 tail projection 可从其之后继续 replay raw suffix 的边界。
- `governingRuntimeConfigSetup` / `governingSystemPromptSetup`：由
  `ResolveGoverningSetup(sourceRawHead)` 得到，用于 provenance 和后续 request manifest。
- `previousArtifact`：同 profile / same lineage 的上一版 recap。
- `memoryPack` / `content`：第一版可二选一；若复用 `MemoryPack`，需保持其 block path 与 carrier 语义。

## 7. Replay 策略

第一版应从 SessionJournal raw events 构造 `IHistoryMessage` 流：

- `observation-accepted` -> `ObservationMessage`
- `agent-action-produced` -> `ActionMessage`
- `tool-result-observed` -> `ToolResultsMessage`，顺序规则沿用 `SessionReducer`
- setup / created 事件不进入 active history

推荐先复用 `SessionJournalEngine.Project()` 或 `ReadChronologicalChain` + `SessionEventCodec.Decode` 的现有能力，
再根据需要抽更轻的 replay cursor。不要为 backtest 重写一套与 reducer 不一致的事件投影。

rolling summary 触发策略可以继续沿用当前 backtest 的保守占位逻辑：

```text
if EstimateTokens(activeHistory) >= threshold:
    split = HistoryWindowSplitPolicy.FindHalfContextSplitPoint(activeHistory)
    fragment = activeHistory[..split]
    maintainer updates summary block
    activeHistory.RemoveRange(0, split)
```

这里的 split policy 只是 CS-5-lite 的实验触发器，不是最终 Context Planner。

## 8. 与 Tail-Only Projection 的关系

本切片完成后，tail-only projection 的推荐构造为：

```text
latest usable recap artifact
-> materialize ContextHeader observation header
-> optionally materialize Action header
-> replay raw suffix after artifact.anchorRawEvent
-> build CompletionRequest context
```

这样做的好处：

- 边界来自真实 recap anchor，而不是临时 turn 截断。
- raw events 仍是唯一事实源；derived recap 损坏或删除只影响加速和上下文质量。
- autonomous / role-play Agent 可以长期处在连续 tool-loop 中；连续性由 recap、自传、world understanding
  这类 derived context 承担，而不是强行套传统 user turn。

没有可用 recap artifact 时，tail projection 可以退回 full replay 或朴素 raw suffix fallback。fallback 是
bootstrap 工具，不是长期主要机制。

## 9. 已完成验收

自动 E2E 与真实 `dsv4p` 验收已经证明：

- 从 `import-legacy-json` 生成的 repo 运行 rolling summary replay。
- replay 不依赖 legacy export JSON 的 message stream。
- raw SessionJournal event chain 未被写入 recap / compaction event。
- derived recap artifact 能 reopen 后加载。
- 删除 derived store 后可重新生成。
- artifact provenance 中包含 source raw range、anchor、profile、previous artifact、invocation、
  governing runtime config setup 和 governing system prompt setup。
- Backtest report 能链接到产生的 artifact 和 LLM call log。

## 10. 推荐命令形态

实现采用独立命令，避免 legacy JSON 与 repo 目录发生输入格式二义性：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- run-memory-maintainer \
  --input gitignore/session-journal/cyber-copy-upgraded \
  --threshold-tokens 24000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --output gitignore/backtest/session-journal-rolling-summary.jsonl \
  --call-log-dir gitignore/backtest/session-journal-rolling-summary-calls \
  --max-epochs 1 \
  --profile autobiographical-rewrite
```

legacy `replay-rolling-summary` 已随 BacktestCli 退役；现行命令只装配 SessionJournal
addressed source + writer。

## 11. 已定决策与后续问题

- derived store 直接放在 SessionJournal repo 的 `derived/recaps/v1/` 下。
- 完整 `MemoryPack` 进入 artifact body；artifact identity 覆盖其 canonical content。
- rolling summary、autobiography、world understanding 共享 store schema，以 profile/target 区分
  lineage。
- 第一版 latest index 只面向当前 main raw history；artifact 记录 source raw head 防止 provenance
  丢失。

后续仍需解决：

- `ContextHeader` 的 action header 如何与 raw suffix 开头的 `ActionMessage` 拼接。
- 从 existing latest materialize `MemoryPack`，并只 replay `anchorRawEvent` 之后的 raw tail。
- branch/rewind 下的 usable-artifact 与 latest index policy。

## 12. 分片任务

本 brief 已拆分为 6 个更小的实施切片，入口见
[cs-5-lite-slices/README.md](cs-5-lite-slices/README.md)。

其中 A/B0/B/C/D/E 的设计和实施记录见：

- [CS-5-lite-A Design: SessionJournal Addressed Replay Cursor](cs-5-lite-slices/cs-5-lite-A-addressed-replay-cursor-design.md)
- [CS-5-lite-B0 设计：SessionJournal Memory Substrate 上移](cs-5-lite-slices/cs-5-lite-B0-sessionjournal-memory-substrate-design.md)
- [CS-5-lite-B 设计：Derived Recap Store 最小库](cs-5-lite-slices/cs-5-lite-B-derived-recap-store-design.md)
- [CS-5-lite-C 设计：RollingSummary Runner 输入源抽象](cs-5-lite-slices/cs-5-lite-C-runner-input-abstraction-design.md)
- [CS-5-lite-D：LLM 结果写入 Derived Recap Artifact](cs-5-lite-slices/cs-5-lite-D-artifact-writing.md)
- [CS-5-lite-E：CLI 与端到端验收](cs-5-lite-slices/cs-5-lite-E-cli-e2e.md)
