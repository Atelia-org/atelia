# CS-5-lite 分片实施入口

> 状态：Task Split / Pre-Implementation
> 日期：2026-07-25
> 父任务：[CS-5-lite: SessionJournal Derived Recap Store + RollingSummary Replay](../cs-5-lite-sessionjournal-derived-recap-store.md)

## 目的

`CS-5-lite` 的原始 brief 已经具备方向性，但横跨 raw replay、derived store、Backtest CLI、LLM
调用和验收报告。为了避免一次实现范围过大，本目录把它拆成 5 个可独立推进的小切片。

这 5 个切片共同服务同一个目标：

```text
SessionJournal raw repo
-> addressed history replay
-> rolling summary sliding prefix
-> derived recap artifact
-> later tail-only projection anchor
```

## 分片列表

| 分片 | 名称 | 主要产物 | 依赖 |
| --- | --- | --- | --- |
| A | [SessionJournal Addressed Replay Cursor](cs-5-lite-A-addressed-replay-cursor.md) | 带 raw address 的 history message replay API | 现有 `SessionJournalEngine.Project()` / `SessionReducer` |
| B | [Derived Recap Store 最小库](cs-5-lite-B-derived-recap-store.md) | 可写、可读、可重建 latest index 的 recap artifact store | `EventAddress` 字符串、`MemoryPack` 序列化方案 |
| C | RollingSummary Runner 输入源抽象 | legacy 与 SessionJournal 可共用的 replay step runner | A |
| D | LLM 结果写入 Derived Recap Artifact | maintainer 成功后产生带 provenance 的 artifact | A、B、C |
| E | CLI 与端到端验收 | 新命令、文档、回归测试/手工验收命令 | A、B、C、D |

## 已完成的细化设计

- [CS-5-lite-A Design: SessionJournal Addressed Replay Cursor](cs-5-lite-A-addressed-replay-cursor-design.md)
- [CS-5-lite-B 设计：Derived Recap Store 最小库](cs-5-lite-B-derived-recap-store-design.md)

## 总体实施顺序

推荐先做 A 和 B 的方案定稿，再进入代码实施：

1. **A** 先解决 raw event 到 `IHistoryMessage` 的可追踪投影，避免 CLI 复制 reducer 语义。
2. **B** 定义 recap artifact 的最小持久格式和 index 语义。
3. **C** 把现有 rolling summary runner 从 legacy event source 解耦。
4. **D** 把 C 产生的成功维护结果写入 B。
5. **E** 收 CLI、README、测试与一次真实 imported repo 验收。

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
