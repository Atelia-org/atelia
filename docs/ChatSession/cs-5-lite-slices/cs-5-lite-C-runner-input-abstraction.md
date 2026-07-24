# CS-5-lite-C: RollingSummary Runner 输入源抽象

> 状态：Task Brief
> 日期：2026-07-25
> 父任务：[CS-5-lite](../cs-5-lite-sessionjournal-derived-recap-store.md)

## 目标

把 `RollingSummaryReplayRunner` 从 legacy export 事件源解耦，让同一套 threshold/split/maintainer 逻辑可以消费：

- legacy export replay step。
- SessionJournal addressed replay step。

## 背景

当前 runner 直接持有 `ChatSessionLegacyEventSource`，在 `ApplyEvent(...)` 内识别 legacy event kind，并用
legacy ordinal/commit 填报表。SessionJournal 输入没有 legacy ordinal，也需要携带 raw address range。

## 推荐方向

引入 runner 内部输入模型，例如：

```csharp
internal sealed record RollingSummaryReplayStep(
    string SourceKind,
    string SourceId,
    IReadOnlyList<IHistoryMessage> AppendedMessages,
    EventAddress? SourceEndInclusive
);
```

legacy adapter 继续从 `ChatSessionLegacyEventSource` 产生 step；SessionJournal adapter 从 A 分片的
addressed messages 产生 step。

## 非目标

- 不改变 maintainer prompt。
- 不写 derived artifact；该工作留给 D。
- 不删除 legacy replay 命令。
- 不改变 `HistoryWindowSplitPolicy`。

## 验收

- legacy `replay-rolling-summary` 行为保持。
- 新 SessionJournal adapter 能驱动同一 runner，并在 record 中输出 source id/address。
- runner 不再依赖 `ChatSessionLegacyReplayEvent` 作为核心状态输入。
