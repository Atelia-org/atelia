# CS-5-lite: SessionJournal Derived Recap Store + RollingSummary Replay

> 状态：Task Brief / Pre-Implementation Context
> 日期：2026-07-25
> 上层路线图：[ChatSession 事件源与长期上下文架构路线图](event-sourced-session-architecture-roadmap.md)
> 相关设计：[SessionJournal Configuration Access Notes](../SessionJournal/session-configuration-access-notes.md)

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
- `prototypes/ChatSession.BacktestCli/RollingSummaryReplay.cs` 目前仍从 legacy event source 读取消息，
  在内存中维护 `_activeHistory` 与 `MemoryPack`，并复用 `RewriteMemoryBlockMaintainer` 生成 rolling
  summary。

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

- [event-sourced-session-architecture-roadmap.md](event-sourced-session-architecture-roadmap.md)
  总路线图；CS-2.5 / CS-5-lite 已作为 CS-3 tail projection 前置切片记录。
- [session-configuration-access-notes.md](../SessionJournal/session-configuration-access-notes.md)
  governing setup resolver、near-head hint、recap anchor 与 tail projection 的关系。
- [session-journal-trunk-design.md](../SessionJournal/session-journal-trunk-design.md)
  SessionJournal raw event schema、EventKind、canonical JSON、执行机边界。
- [memory-backtest-cli-plan.md](memory-backtest-cli-plan.md)
  旧 backtest CLI 与 rolling summary replay 的设计背景。

主要代码入口：

- [RollingSummaryReplay.cs](../../prototypes/ChatSession.BacktestCli/RollingSummaryReplay.cs)
  当前 legacy-source rolling summary runner；应迁移或扩展为可从 SessionJournal replay。
- [Program.cs](../../prototypes/ChatSession.BacktestCli/Program.cs)
  Backtest CLI 命令入口、connection 配置、rolling summary 参数。
- [SessionJournalLegacyImporter.cs](../../prototypes/ChatSession.BacktestCli/SessionJournalLegacyImporter.cs)
  legacy export -> SessionJournal repo importer；已跳过 legacy recap / compaction。
- [SessionJournalEngine.cs](../../prototypes/SessionJournal/SessionJournalEngine.cs)
  SessionJournal open/project/append/resolver 主入口。
- [SessionReducer.cs](../../prototypes/SessionJournal/SessionReducer.cs)
  raw events -> `SessionProjection` 的纯 reducer。
- [SessionJournalContracts.cs](../../prototypes/SessionJournal/SessionJournalContracts.cs)
  `SessionEventKind`、`SessionProjection`、`SessionGoverningSetup` 等契约。
- [MemorySubstrate.cs](../../prototypes/ChatSession/MemorySubstrate.cs)
  `MemoryPack`、`RecentHistorySlice`、`ContextHeaderSnapshot`、`RewriteMemoryBlockMaintainer`。
- [ChatSessionContracts.cs](../../prototypes/ChatSession/ChatSessionContracts.cs)
  `ContextHeader`、`RecapMessage`、`RecapSourceAnchor` 等旧 ChatSession context projection 类型。
- [AutobiographicalRewriteProfiles.cs](../../prototypes/ChatSession.Memory/AutobiographicalRewriteProfiles.cs)
  后续可复用 profile 示例。
- [WorldUnderstandingRewriteProfiles.cs](../../prototypes/ChatSession.Memory/WorldUnderstandingRewriteProfiles.cs)
  后续可复用 profile 示例。

底层遍历相关：

- [EventJournal.cs](../../src/EventJournal/EventJournal.cs)
  `ReadEvent`、`ReadEventHeaderPreview`、`ReadChronologicalChain` 等。
- [EventJournal.ForwardPlan.cs](../../src/EventJournal/EventJournal.ForwardPlan.cs)
  EventJournal forward replay / cache / tail merge 现有能力。

## 6. 建议数据模型

第一版可以把 derived recap store 做在 SessionJournal repo 内的独立目录，例如：

```text
derived/recaps/v1/
  artifacts/<artifact-id>.json
  blobs/<content-hash>.txt
  indexes/latest-by-profile.json
```

具体路径可调整，但语义应保持：

- `artifacts` 是 append-only 产物记录。
- `blobs` 保存较大的 recap / MemoryPack 内容，可按 hash 去重。
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

## 9. 建议验收

实现完成后至少能证明：

- 从 `import-session-journal` 生成的 repo 运行 rolling summary replay。
- replay 不依赖 legacy export JSON 的 message stream。
- raw SessionJournal event chain 未被写入 recap / compaction event。
- derived recap artifact 能 reopen 后加载。
- 删除 derived store 后可重新生成。
- artifact provenance 中包含 source raw range、anchor、profile、previous artifact、invocation、
  governing runtime config setup 和 governing system prompt setup。
- Backtest report 能链接到产生的 artifact 和 LLM call log。

## 10. 推荐命令形态

现有命令可扩展一个 SessionJournal 输入模式，或新增命令。形状示例：

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary-session-journal \
  --input gitignore/session-journal/cyber-copy-upgraded \
  --threshold-tokens 12000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection local-deepseek \
  --output gitignore/backtest/session-journal-rolling-summary.jsonl \
  --call-log-dir gitignore/backtest/session-journal-rolling-summary-calls
```

若选择复用旧 `replay-rolling-summary` 命令，应显式区分 `--legacy-input` 与 `--session-journal-input`，
避免输入格式二义性。

## 11. 开放问题

- derived store 是否短期直接放在 SessionJournal repo 下，还是单独 artifact repo。
- `MemoryPack` 是否直接进入 artifact body，还是每个 block 单独作为 artifact。
- `ContextHeader` 的 action header 如何与 raw suffix 开头的 `ActionMessage` 拼接。
- rolling summary、autobiography、world understanding 是否在 CS-5-lite 共享同一 store schema，还是先只做
  rolling summary。
- latest index 的 branch/rewind 语义：第一版可只支持 `main`，但 artifact 必须记录 source head，避免把旁支
  产物误当当前有效链。
