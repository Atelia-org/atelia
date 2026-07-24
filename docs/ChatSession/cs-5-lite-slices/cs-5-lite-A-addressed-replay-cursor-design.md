# CS-5-lite-A Design: SessionJournal Addressed Replay Cursor

> 状态：Design Proposal / Ready for Implementation
> 日期：2026-07-25
> 父任务：[CS-5-lite-A](cs-5-lite-A-addressed-replay-cursor.md)

## 1. 设计结论

第一版推荐在 `prototypes/SessionJournal` 内新增一个 public addressed replay API，由
`SessionJournalEngine` 暴露，并让它与现有 `Project()` 共用同一个 `SessionReducer` reducer core。

推荐主线：

```text
SessionJournalEngine
-> ReadChronologicalChain(main head)
-> DecodeSessionEvents(...)
-> SessionReducer.Reduce(..., optional addressed message sink)
-> SessionHistoryReplay
```

不推荐让 `ChatSession.BacktestCli` 直接访问 `SessionEventCodec` / `DecodedSessionEvent`，也不推荐复制
`SessionReducer` 的 tool-loop 状态机。A 分片的关键价值不是多一个 replay 方法，而是把
`IHistoryMessage` 与 raw `EventAddress` 的映射绑定到 reducer 真正产生 message 的瞬间。

## 2. Public API

新增 public contract 放在 `SessionJournalContracts.cs`，与 `SessionProjection` 同级：

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

`SessionJournalEngine` 新增：

```csharp
public SessionHistoryReplay ReplayHistory(CancellationToken cancellationToken = default);
```

字段语义：

- `SourceRawHead`：开始本次 full replay 时从 `main` ref 观察到的 head。空仓库为 `null`。
- `Messages`：与 `Project().Context` 等价的 history message 流，但每条 message 附带 raw source range。
- `ExecutionState`：与同一 raw head 上 `Project().ExecutionState` 等价。

暂不把 `Config` / `SystemPrompt` 放进 `SessionHistoryReplay`。后续 D 分片需要 governing setup 时，应继续调用
`ResolveGoverningSetup(sourceRawHead)`；C 分片的 rolling summary runner 只需要 addressed history 与
execution state。

## 3. Internal 结构

为避免 `Project()` 和 addressed replay 双真源，推荐做一个很小的内部抽取：

```csharp
private IReadOnlyList<DecodedSessionEvent> ReadDecodedChronologicalEvents(
    EventAddress head,
    CancellationToken cancellationToken
);
```

`Project()` 改为：

```csharp
EventAddress? head = _journal.GetHead(_mainRef);
if (head is null) { return SessionReducer.Empty; }
return SessionReducer.Reduce(ReadDecodedChronologicalEvents(head.Value, cancellationToken));
```

`ReplayHistory()` 改为：

```csharp
EventAddress? head = _journal.GetHead(_mainRef);
if (head is null) { return SessionHistoryReplay.Empty; }
SessionProjection projection = SessionReducer.Reduce(
    ReadDecodedChronologicalEvents(head.Value, cancellationToken),
    addressedMessages
);
return new SessionHistoryReplay(head, addressedMessages, projection.ExecutionState);
```

具体签名可略调，但主旨是：解码遍历只抽一次，reducer 状态机只保留一份。

## 4. Reducer Hook 方案

推荐把 `SessionReducer.Reduce` 扩展为带可选 sink 的单一路径：

```csharp
public static SessionProjection Reduce(
    IReadOnlyList<DecodedSessionEvent> events,
    ICollection<AddressedSessionHistoryMessage>? addressedMessages = null
);
```

如果担心 public 类型出现在 internal reducer 签名里不够干净，也可以新增 internal adapter：

```csharp
internal interface ISessionHistoryMessageSink {
    void Add(IHistoryMessage message, EventAddress startInclusive, EventAddress endInclusive);
}
```

实现上仍建议保持一条 switch 状态机。每当原逻辑 `context.Add(message)` 时，同步向 sink 写入 address range。

地址绑定规则：

| EventKind | 输出 message | SourceStartInclusive | SourceEndInclusive |
| --- | --- | --- | --- |
| `ObservationAccepted` | `ObservationMessage` | 当前 event address | 当前 event address |
| `AgentActionProduced` | `ActionMessage` | 当前 event address | 当前 event address |
| `ToolResultObserved` 且闭合全部 tool calls | `ToolResultsMessage` | 本组第一个 `tool-result-observed` address | 当前 event address |
| setup / `SessionCreated` / `ToolExecutionStarted` | 无 | 不适用 | 不适用 |

## 5. ToolResultsMessage Source Range

`ToolResultsMessage` 的 range 应只覆盖参与聚合的 `tool-result-observed` events，不包含前置
`agent-action-produced`，也不包含 `tool-execution-started`。

原因：

- `ActionMessage` 已经由 `agent-action-produced` 单独输出并持有自己的 source address。
- `tool-execution-started` 是执行机恢复/幂等语义，不是 history message。
- 后续 rolling summary 对 history 做 prefix split 时，如果 prefix 包含一个完整 `ToolResultsMessage`，其
  `SourceEndInclusive` 就是全部被吸收 history 的 raw anchor。

实现时 reducer 需要额外记录：

```csharp
EventAddress? firstObservedToolResultAddress;
EventAddress? lastObservedToolResultAddress;
```

生命周期与 `observedResults` 一致：

- 新 `ObservationAccepted` / `AgentActionProduced` / `SessionCreated` 清空。
- 第一个合法 `ToolResultObserved` 设置 `firstObservedToolResultAddress`。
- 每个合法 `ToolResultObserved` 更新 `lastObservedToolResultAddress`。
- 当 `pendingToolCall is null` 并输出 `ToolResultsMessage` 时，用这两个地址作为 range，然后清空。

对多 tool call 场景：

- `ToolResultsMessage.Results` 的顺序继续按 `openAction.ToolCalls` 声明顺序投影。
- source range 的 start/end 按 raw observation 顺序定义。当前执行机正常会顺序产生结果；若未来出现并发或乱序输入，range 仍表示 raw chain 上覆盖本组 observed results 的连续区间，而结果内容顺序仍由 action 声明决定。

未闭合 tool call：

- 与现有 `Project()` 一致，不输出 `ToolResultsMessage`。
- 已观察但未闭合的 tool result 不进入 `Messages`。
- `ExecutionState` 继续表达当前 pending tool call / checkpoint。

## 6. RollingSummary 与 Artifact Anchor 用法

C 分片应把 `SessionHistoryReplay.Messages` 适配成 rolling summary step source：

```csharp
IReadOnlyList<IHistoryMessage> activeHistory = replay.Messages.Select(x => x.Message).ToArray();
```

当 split policy 选择 `fragment = messages[0..split]` 时：

- fragment 的 `sourceEndInclusive` = `fragment[^1].SourceEndInclusive`
- fragment 的 `anchorRawEvent` = `fragment[^1].SourceEndInclusive`
- fragment 的 `sourceStartExclusive` 来自上一版 artifact 的 `anchorRawEvent`，不是 A API 负责计算

如果 split 结果为空，不产生 artifact。若 replay 处于 `AwaitingToolExecution`，末尾未闭合工具执行不会出现在
`Messages`，因此不会被 rolling summary 误吸收。

## 7. 空仓库与 setup-only 仓库

当前 `SessionJournalEngine.Create` 会立即写入 runtime config、system prompt、`session-created` 三个事件；
正常 repo 不会是空仓库。但 public API 仍建议定义空仓库行为：

```csharp
public static SessionHistoryReplay Empty => new(
    SourceRawHead: null,
    Messages: Array.AsReadOnly(Array.Empty<AddressedSessionHistoryMessage>()),
    ExecutionState: new SessionExecutionState(SessionExecutionPhase.Empty, HeadKind: null)
);
```

setup-only / created-only repo：

- `SourceRawHead` 为实际 head。
- `Messages` 为空。
- `ExecutionState` 与 `Project()` 一致，通常是 `Empty` 或 `Idle`。

## 8. 不纳入第一版的方案

第一版不做这些扩展：

- 不提供 arbitrary branch 参数；继续只读 `SessionJournalDefaults.MainBranchName`。
- 不做 streaming cursor / `IAsyncEnumerable`。rolling summary backtest 可以先接受内存列表。
- 不做 tail-only projection。
- 不把 `SessionEventCodec` 或 `DecodedSessionEvent` 改成 public。
- 不在 addressed replay 中解析 derived recap artifact。

后续如果需要增量 replay，可以在 A 的结果模型稳定后追加：

```csharp
public SessionHistoryReplay ReplayHistoryAfter(EventAddress startExclusive, CancellationToken ct = default);
```

但这不是 CS-5-lite-A 的实施条件。

## 9. 最小测试集

建议在 `tests/SessionJournal.Tests/SessionJournalEngineTests.cs` 增加 4 个聚焦测试。

### 9.1 Observation 与 Action 地址

构造：

- create repo
- `AppendObservation("hello")`
- `AppendAgentAction(new ActionMessage(...), invocation)`

断言：

- `ReplayHistory().Messages.Count == Project().Context.Count == 2`
- message 类型与内容等价
- observation 的 start/end 等于 `AppendObservation` 返回地址
- action 的 start/end 等于 `AppendAgentAction` 返回地址
- `SourceRawHead == Project().Head`

### 9.2 Setup 与 SessionCreated 不输出 message

构造：

- create repo
- 可追加一次 idle 边界上的 runtime config setup 或 system prompt setup

断言：

- `ReplayHistory().Messages` 不包含 setup / created 对应项
- `ExecutionState` 与 `Project().ExecutionState` 一致
- `SourceRawHead` 是实际 main head

### 9.3 多 ToolResult 聚合 range

构造可用现有 fake runtime 跑一次含两个 tool calls 的 `SendAsync`，或手动用 public 方法加 observation/action 后通过 runtime 产生 tool results。

断言：

- addressed messages 顺序为 observation、action、tool results、final action
- `ToolResultsMessage.SourceStartInclusive` 等于第一个 `tool-result-observed` event address
- `ToolResultsMessage.SourceEndInclusive` 等于第二个 `tool-result-observed` event address
- `ToolResultsMessage.Results` 仍按 action 声明顺序排列

为了拿到 expected addresses，可在测试内用 `EventJournal.ReadChronologicalChain` 读取 chain，并按
`OpaqueEventKind == ToolResultObserved` 找地址；现有测试文件已经有 `ReadJournalPayloadJson` 类似 helper。

### 9.4 未闭合 tool call 不输出 ToolResultsMessage

构造：

- 使用 `SessionJournalFailpoint.AfterToolResultCommitted` 覆盖只提交第一个 tool result 的双 tool call 场景，或使用 `AfterToolStartedCommitted` 覆盖只提交 started 的场景。

断言：

- addressed replay 与 `Project().Context` 等价。
- 未闭合时没有 `ToolResultsMessage`，或只在全部 result 到齐后才出现。
- `ExecutionState.Phase` / `PendingToolCall` 与 `Project()` 一致。

注：现有 tests 已广泛覆盖 tool-loop 恢复，A 分片测试只需要补 address 与 context 等价，不必复制全部恢复矩阵。

## 10. 实施步骤建议

1. 在 `SessionJournalContracts.cs` 增加 `AddressedSessionHistoryMessage`、`SessionHistoryReplay`。
2. 在 `SessionJournalEngine` 抽出 `ReadDecodedChronologicalEvents`。
3. 扩展 `SessionReducer.Reduce`，在同一 switch 内同步写 addressed sink。
4. 新增 `SessionJournalEngine.ReplayHistory`。
5. 添加最小测试集，先证明 `ReplayHistory().Messages.Select(x => x.Message)` 与 `Project().Context` 等价，再证明关键 address range。

## 11. 残余风险

- `IHistoryMessage` 缺少结构化 equality，测试中应按具体类型与关键字段比较，不建议直接 `Assert.Equal`。
- `EventAddress` 字符串序列化属于 B 分片，但 A 的 API 必须保持强类型 `EventAddress`，不要提前降级成 string。
- 多 tool result 的 range 是 raw chain 上的连续地址范围，不表示每个 tool result 与每个 `ToolResult` 的一一地址映射。第一版足够支撑 rolling summary anchor；若未来需要逐个 tool result provenance，需要新增更细模型。
