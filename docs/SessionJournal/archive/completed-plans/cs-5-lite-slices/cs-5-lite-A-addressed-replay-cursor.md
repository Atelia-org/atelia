# CS-5-lite-A: SessionJournal Addressed Replay Cursor

> 状态：Task Brief / Needs Design
> 日期：2026-07-25
> 父任务：[CS-5-lite](../cs-5-lite-sessionjournal-derived-recap-store.md)

## 目标

在 `prototypes/SessionJournal` 内提供一个从 raw SessionJournal branch 正序 replay 出 history message 的 API，
并保留每个输出 message 对应的 raw event address/range。后续 rolling summary runner 用它决定
`sourceEndInclusive` 与 `anchorRawEvent`。

## 背景

现有 `SessionJournalEngine.Project()` 已经通过 `ReadChronologicalChain` 读取 raw events，并调用
`SessionReducer.Reduce(...)` 得到 `SessionProjection.Context`。但是 `SessionProjection.Context`
只保留 `IHistoryMessage`，不保留每条 message 的来源地址。

CS-5-lite 需要 provenance：

- 本次 replay 到哪个 raw head。
- sliding prefix 覆盖的 raw event 范围。
- recap artifact 的 anchor raw event。

因此需要一个 addressed replay cursor，而不是只复用 `Project()` 的最终 context。

## 推荐输出模型

第一版可在 `Atelia.SessionJournal` 暴露类似类型：

```csharp
public sealed record AddressedSessionHistoryMessage(
    IHistoryMessage Message,
    EventAddress SourceStartInclusive,
    EventAddress SourceEndInclusive
);

public sealed record SessionHistoryReplay(
    EventAddress? SourceRawHead,
    IReadOnlyList<AddressedSessionHistoryMessage> Messages,
    SessionExecutionState ExecutionState
);
```

`ToolResultsMessage` 由一组 `tool-result-observed` 聚合而成时：

- `SourceStartInclusive` 是本组第一个 `tool-result-observed`。
- `SourceEndInclusive` 是本组最后一个 `tool-result-observed`。
- 顺序和合法性规则必须与 `SessionReducer` 一致。

## 非目标

- 不实现 tail-only projection。
- 不实现 streaming replay cursor；第一版可以返回内存列表。
- 不支持 arbitrary branch 参数；先复用 `SessionJournalEngine` 当前 main ref。
- 不把 recap/artifact 注入 projection。
- 不为 Backtest CLI 暴露 `SessionEventCodec` internal 细节。

## 设计关注点

- 尽量避免 `SessionReducer` 和 addressed replay 各自维护一份 tool-loop 状态机。
- 如果抽公共内部 reducer core，应保持 `Project()` 行为不变。
- 对未完成 tool call 的 head，addressed replay 应与现有 projection 一样：只输出已经闭合的
  `ToolResultsMessage`，未闭合工具结果留在 `ExecutionState`。
- setup / `session-created` 事件不进入 history message，但仍影响 execution/config/system prompt。

## 验收

- 新 API 在普通 observation/action/tool-result 流上输出和 `Project().Context` 内容等价。
- 每个输出 message 都有非空 `SourceStartInclusive` / `SourceEndInclusive`。
- 多 tool call 的 `ToolResultsMessage` source range 覆盖全部相关 tool result event。
- setup 与 `session-created` 不输出 history message。
- 至少补 `tests/SessionJournal.Tests` 单元测试覆盖 observation/action/tool result 地址。

## 后续消费者

- C 分片会把 addressed messages 适配成 rolling summary replay step。
- D 分片会用 fragment 最后一条 addressed message 的 `SourceEndInclusive` 生成 artifact anchor。
