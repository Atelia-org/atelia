# CS-5-lite 分片实施入口

> 状态：Implemented / A–E Complete
> 日期：2026-07-26
> 父任务：[CS-5-lite: SessionJournal Derived Recap Store + RollingSummary Replay](../cs-5-lite-sessionjournal-derived-recap-store.md)

## 目的

`CS-5-lite` 的原始 brief 已经具备方向性，但横跨 raw replay、derived store、Backtest CLI、LLM
调用和验收报告。为了避免一次实现范围过大，本目录把它拆成 6 个可独立推进的小切片。

这 6 个切片共同服务同一个目标：

```text
SessionJournal raw repo
-> addressed history replay
-> rolling summary sliding prefix
-> derived recap artifact
-> later tail-only projection anchor
```

## 分片列表

| 分片 | 名称 | 主要产物 | 依赖 | 状态 |
| --- | --- | --- | --- | --- |
| A | [SessionJournal Addressed Replay Cursor](cs-5-lite-A-addressed-replay-cursor.md) | 带 raw address 的 history message replay API | 现有 `SessionJournalEngine.Project()` / `SessionReducer` | 已实施 |
| B0 | [SessionJournal Memory Substrate 上移](cs-5-lite-B0-sessionjournal-memory-substrate.md) | SessionJournal-owned memory/maintainer substrate | A | 已实施 |
| B | [Derived Recap Store 最小库](cs-5-lite-B-derived-recap-store.md) | 可写、可读、可重建 latest index 的 recap artifact store | A、B0 | 已实施 |
| C | [RollingSummary Runner 输入源抽象](cs-5-lite-C-runner-input-abstraction-design.md) | legacy 与 SessionJournal 可共用的 replay step runner | A、B（复用 address codec，不读写 store） | 已实施 |
| D | [LLM 结果写入 Derived Recap Artifact](cs-5-lite-D-artifact-writing.md) | maintainer 成功后产生带 provenance 的 artifact | A、B、C | 已实施 |
| E | [CLI 与端到端验收](cs-5-lite-E-cli-e2e.md) | 新命令、文档、自动 E2E 与真实 LLM 验收 | A、B、C、D | 已实施 |

## 已完成的细化设计

- [CS-5-lite-A Design: SessionJournal Addressed Replay Cursor](cs-5-lite-A-addressed-replay-cursor-design.md)
- [CS-5-lite-B0 设计：SessionJournal Memory Substrate 上移](cs-5-lite-B0-sessionjournal-memory-substrate-design.md)
- [CS-5-lite-B 设计：Derived Recap Store 最小库](cs-5-lite-B-derived-recap-store-design.md)
- [CS-5-lite-C 设计：RollingSummary Runner 输入源抽象](cs-5-lite-C-runner-input-abstraction-design.md)
- [CS-5-lite-D：LLM 结果写入 Derived Recap Artifact](cs-5-lite-D-artifact-writing.md)
- [CS-5-lite-E：CLI 与端到端验收](cs-5-lite-E-cli-e2e.md)

## 总体实施顺序

当前 A、B0、B、C、D、E 均已实施：

1. **A** 先解决 raw event 到 `IHistoryMessage` 的可追踪投影，避免 CLI 复制 reducer 语义。
2. **B0** 把 memory substrate 的长期归属收到 `SessionJournal` 主干，避免 B/C/D 围绕旧
   `ChatSession` 类型设计正式 API。
3. **B** 定义 recap artifact 的最小持久格式和 index 语义。
4. **C** 把现有 rolling summary runner 从 legacy event source 解耦。
5. **D** 把 C 产生的成功维护结果写入 B。
6. **E** 收 CLI、README、测试与一次真实 imported repo 验收。

### E 实施结果

- 正式命令 `replay-rolling-summary-session-journal` 以 repo 目录为输入；legacy 命令继续只接受 export
  JSON，不做输入格式猜测。
- CLI 显式组装同 repo 的 addressed source 与 artifact writer；成功 JSONL 链接实际 artifact 和
  Completion call log。
- 自动 E2E 使用 injected scripted factory，覆盖 import、生成、existing-lineage preflight、raw
  不变和删除 `derived/recaps/v1/` 后再生，不依赖网络。
- `dsv4p` 真实验收在同一新 imported repo 上分别完成 autobiographical 与 world-understanding
  单 epoch；两个独立 lineage 同时存在，raw journal 未变化。

### D → E handoff

- E 的 artifact-producing SessionJournal 命令必须显式组装
  `SessionJournalRollingSummaryReplaySource + SessionJournalDerivedRecapWriter +
  RollingSummaryReplayRunner`；source 单独使用时仍是无副作用 replay。
- writer 在首次 LLM 调用前要求目标 lineage 为空。当前 runner 是 full replay + empty `MemoryPack`，
  不能把已有 latest artifact 直接接成 previous。
- SessionJournal source 与 concrete writer 必须指向同一 repo；D writer 用 derived-store exclusive
  write lock 串行化 latest check + artifact/index commit，避免并发启动产生双 root lineage。
- store 成功返回后 runner 才提交候选 `MemoryPack` 并移除 prefix；artifact operational failure 会输出
  failed record，保留候选诊断信息但不推进状态。
- replay record 的 `artifactId` / `artifactPath` / `anchorRawEvent` / `previousArtifact` 直接链接实际
  artifact；legacy 或任一失败 record 的这些字段为 null。
- E 只负责正式 CLI、帮助/README 与 smoke，不重新定义 anchor、lineage、fingerprint 或失败语义。

### C → D handoff

- record 的 `sourceId` / `eventOrdinal` / `eventCommit` 仍标识触发 threshold 检查的 source step。
- record 的 `sourceRawHead` 是本次 replay 开始时的 raw head snapshot，同次 replay 的多个 epoch 保持一致。
- record 的 `sourceStartInclusive` / `sourceEndInclusive` 来自本次 selected fragment 的首尾 addressed
  message；不再来自 trigger step。
- D 的 artifact candidate 至少必须满足 `status == "succeeded"`、`sourceKind == "session-journal"`，
  且 `sourceRawHead` / `sourceStartInclusive` / `sourceEndInclusive` 均非 null；令
  `anchorRawEvent = sourceEndInclusive`。
- runner 捕获并报告的 failed record 虽保留 attempted fragment range 供诊断，但 active prefix 未移除，
  不得据此推进 artifact lineage。source contract 违规会 fail-fast，不生成 record。

## 跨分片约束

- raw SessionJournal event chain 仍是唯一事实源；recap artifact 不写回 raw event chain。
- derived store 是可删除、可重建产物；损坏或缺失不得破坏 raw replay。
- `SessionReducer` 的 tool result 聚合规则必须保持单一真源，不能在 Backtest CLI 另写一套会分叉的 reducer。
- artifact anchor 必须能回答：本次 summary 覆盖到哪个 raw event，后续 raw suffix 从哪里继续。
- 第一版只面向 `main` branch 和离线 replay；branch/rewind 的完整 policy 留到后续 ArtifactSet/ContextPlan。

## 当前推荐语义

- `sourceRawHead`：producer 打开 repo 或开始 replay 时观察到的 raw branch head。
- `sourceStartExclusive`：上一版 artifact 的 `anchorRawEvent`；若无上一版则为 `null`。
- `sourceEndInclusive`：本次 sliding fragment 吸收的最后一个 raw event。
- `anchorRawEvent`：第一版等于 `sourceEndInclusive`。
- `previousArtifact`：同一 `profileId + targetCarrier + targetBlockId` lineage 的上一版 artifact。

这些语义可在 A/B 方案研究中继续收紧，但后续分片应避免重新定义同名字段。
