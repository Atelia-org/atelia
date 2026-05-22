# 结论

草稿的大方向是对的：`LlmSession` 应该是状态机，completion/tool 应该只是能力提供者，所有外部完成信号都应先汇入 `WakeEvent` 再由 kernel 统一推进。这条边界如果守住，后续恢复、调试、测试都会轻很多。

当前最大缺口不在“还缺几个类名”，而在“同一事实被多处表示”。`LlmSessionState`、`WaitReason`、`ReadyQueueKey`、`InFlightLeaseId`、`PersistPending`、retry/recovery metadata 现在有明显重叠。如果不先收口，第一版实现很容易出现 stale wake event、错误重试串线、恢复后假装仍在运行、以及单测只能测 happy path 的情况。

建议在真正进入实现前，先把三件事定死：durable snapshot 和 runtime overlay 的边界、`StepId/Attempt/DispatchId` 的身份模型、以及 completion/tool 共用的最小 outcome envelope。

# 主要问题

- 草稿里的 `LlmSessionState` 混合了“工作阶段”和“调度处境”两条轴。`RunnableForCompletion` / `RunningCompletion` / `PersistPending` / `Suspended` 不是同一类事实，但现在被放进同一个 enum。
- `WaitReason` 又重复表达了其中一部分信息。`RunningCompletion` 几乎等价于 `WaitingCompletionResult`，`RunnableForCompletion` 与 `WaitingCompletionLane` 也高度耦合，`PersistPending` 与 `WaitingPersist` 更是直接重叠。
- `ReadyQueueKey` 与 `InFlightLeaseId` 让双真源进一步扩大。实现时只要有一次异常路径没同步更新，session 就可能同时看起来“可运行、已入队、还持有 lease”。
- `StepId` 单独存在不够稳。超时后重试、恢复后重放、重复投递 wake event、或未来一个 session 允许更复杂 tool 并发时，旧结果很容易误打到新 attempt 上。草稿提到 `Attempt` 是对的，但它还没有成为 request/result/wake event 的强制公共字段。
- executor 边界还不够收敛。`TryDispatch(...) -> bool` 无法表达“队列满”“请求过期”“模型不支持”“session 已取消”等差异，kernel 最终会被迫把调度失败原因塞进日志字符串或隐式分支。
- `CompletionStepResult` 与 `ToolStepResult` 还没有最小公共格式，kernel 因而无法对成功、失败、取消、超时、恢复丢失这几类 outcome 做统一处理。
- `Faulted` 和 `Suspended` 过于宽泛。可重试错误、永久错误、人工修复等待、人工暂停、外部事件等待、取消中的 in-flight step，都不该落到同一个粗粒度状态。
- `IWakeEvent` 目前只有 `SessionId` 太弱。没有 event identity、step identity、dispatch identity、发生时间和事件种类，无法做去重、丢弃 stale result、或构造稳定测试用例。
- `ICompletionClient.StreamCompletionAsync` 有 `observer` 侧信道，同时又返回最终 `CompletionResult`。如果 session 同时依赖两边，completion 输出会天然形成双真源。对 durable state 来说，只有最终聚合结果应该是权威输入。

# 可行接口与状态机改进建议

- 把 session 状态至少拆成“两层”，不要继续扩张单个 `LlmSessionState`。一个可行的最小拆法是：

```csharp
public enum SessionPhase {
    CollectingInput,
    ReadyForCompletion,
    ReadyForTool,
    ApplyingStepOutcome,
    AwaitingCheckpoint,
    Terminal
}

public enum SessionRunDisposition {
    Runnable,
    Waiting,
    InFlight,
    Cancelling
}
```

- `WaitReason` 只表达“为什么当前不可前进”，因此它应只在 `SessionRunDisposition != Runnable` 时有效。这样 `WaitReason` 才不会和 phase 重复编码同一事实。
- 把 `ReadyQueueKey`、`InFlightLeaseId`、`LaneId`、`CancellationTokenSource`、stream observer 这类运行时事实明确放进 runtime overlay，不进入 durable snapshot。durable state 只保留恢复所需事实，不保留“正在排队/正在流式消费”的瞬时现场。
- 为 step 引入“语义身份”和“运行身份”两层 key：

```csharp
public readonly record struct SessionStepKey(string SessionId, long StepId, int Attempt);
public readonly record struct StepDispatchId(string Value);
```

`StepId` 标识 session 逻辑上的第几步，`Attempt` 标识该步第几次尝试，`DispatchId` 标识某一次具体排队/执行实例。丢弃 stale result 时，kernel 应优先比较 `DispatchId`，恢复和审计时再使用 `SessionStepKey`。
- completion/tool 不必共用同一个 payload 类型，但应该共用同一个最小 outcome envelope。建议把统一点放在 kernel 边界，而不是强行抹平业务结果：

```csharp
public enum StepKind { Completion, Tool, Persist }
public enum StepOutcomeKind { Succeeded, Failed, Cancelled, TimedOut, Rejected, LostAfterRecovery }

public interface IStepExecutionOutcome {
    SessionStepKey StepKey { get; }
    StepDispatchId DispatchId { get; }
    StepKind Kind { get; }
    StepOutcomeKind Outcome { get; }
    string? LaneId { get; }
    DateTimeOffset FinishedAt { get; }
}
```

然后 `CompletionStepResult` 和 `ToolStepResult` 只是在此之上各自携带 `CompletionResult` 或 tool payload。
- 把 scheduler admission 从 `bool` 收口成显式结果，避免“失败原因写在注释里”：

```csharp
public enum DispatchAdmission {
    Accepted,
    RejectedBusy,
    RejectedUnsupported,
    RejectedCancelled,
    RejectedStale
}

public sealed record StepDispatchReceipt(
    DispatchAdmission Admission,
    StepDispatchId? DispatchId,
    string? QueueKey,
    string? LaneId,
    string? Reason);
```

- `WakeEvent` 不应该只是“某个 session 有事了”，而应携带因果身份。建议最少带上 `Kind`、`OccurredAt`、`EventSeq`、`SessionStepKey?`、`StepDispatchId?`。这样 kernel 才能安全地忽略重复事件和过期结果。
- `ILlmSession` 最好提供可测试的迁移输出，而不是只暴露命令式 mutation。建议让“应用事件”和“评估下一步”返回一个显式 transition/result 对象，里面同时包含新 snapshot、下一条 command、是否需要 checkpoint。这样单测能直接断言状态迁移，而不是依赖外部观察副作用。
- `observer` 只用于 UI/telemetry，不应成为 session durable state 的输入面。session 只吸收 `CompletionStepResult` 中的最终聚合结果，避免流式增量和最终结果之间出现双真源。

# 建议新增的不变量 / 枚举 / 契约

- 不变量：`SessionRunDisposition == Runnable` 时，`WaitReason` 必须为 `None`，且不得存在 `ReadyQueueKey` 之外的任何 in-flight lease。
- 不变量：`SessionRunDisposition == InFlight` 时，必须存在且只存在一个活动 `StepDispatchId`，并且 `WaitReason` 必须是某种 `...Result` 或 `WaitingPersist`。
- 不变量：第一版若坚持“一个 session 同时最多一个 in-flight step”，则 queue membership 与 lease 必须互斥；已持有 lease 的 session 不得再处于 ready queue。
- 不变量：同一 `SessionStepKey` 的 `Attempt` 只能单调递增，且同一时刻只允许一个活动 attempt。
- 不变量：`StepFinished` 类 wake event 只有在 `DispatchId` 与当前活动 dispatch 匹配时才允许落地；否则只能记日志并丢弃。
- 不变量：terminal session 不得被普通 `WakeEvent` 重新唤醒；若需要人工恢复，必须经过显式 `ManualInterventionApplied` 或等价控制事件。
- 不变量：durable snapshot 永远不保存 `Lease`、`CancellationTokenSource`、observer、task handle、provider-native 流式中间缓冲。
- 枚举建议：增加 `SessionTerminalReason`，至少区分 `Completed`、`Cancelled`、`Faulted`，不要让 `Completed/Faulted` 自己兼任终局原因。
- 枚举建议：增加 `StepFailureKind` 或 `ErrorDisposition`，至少区分 `Transient`、`Permanent`、`Timeout`、`Cancelled`、`ContractViolation`、`RecoveryAmbiguous`。
- 枚举建议：增加 `ManualInterventionKind`，至少区分 `Resume`、`RetryStep`、`PatchState`、`AbortSession`，否则人工介入只能继续走字符串协议。
- 契约建议：定义 `IWakeEvent.Kind`，不要把 `InputArrived`、`CancelRequested`、`PersistFinished`、`ManualResumeRequested` 仅当作命名约定。
- 契约建议：定义 `SessionStepDecision` 的闭集形状，例如 `NoOp`、`DispatchCompletion`、`DispatchTool`、`RequestCheckpoint`、`EnterFault`、`CompleteSession`，这样 kernel 不需要猜 session 打算干什么。

# 可延后问题

- completion queue key 最终是只用 `model-id`，还是纳入 provider/profile，这可以在 outcome envelope 定稳后再讨论。
- tool queue 是否需要从单全局队列细分到 mutex group，这属于调度策略问题，可以晚于状态契约落定。
- checkpoint 是事件驱动、时间驱动还是混合策略，目前不影响先把 `Persist` 建模成独立 step/outcome。
- kernel 是否拆 persistence worker、是否做更复杂 fairness，都可以等单进程串行事件循环跑通后再升级。
- 一个 session 是否允许多个并发 tool step，可以先明确第一版“不允许”，等 `StepDispatchId` 和 stale-result 丢弃规则成熟后再放开。
- durable state 直接用 `StateJournal` 对象图还是经 DTO 中转，这是重要但不阻塞当前状态机收口的问题。
