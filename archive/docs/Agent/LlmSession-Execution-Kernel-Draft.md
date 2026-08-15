# LlmSession Execution Kernel Draft

> 状态：draft
> 目的：为正式版 `LlmSession` 执行引擎提供一版可实现、可恢复、可继续迭代的 MVP 建模。
> 当前取向：优先降低复杂性，先把状态边界、恢复语义和运行时纪律写清，再谈更强调度能力。

## 0. 一句话定位

这一版执行内核要解决的是：

- `LlmSession` 保存并推进自己的 durable working state
- completion 子系统提供模型调用能力
- tool 子系统提供工具调用能力
- kernel 只负责统一裁决 session 状态如何前进一步
- `StateJournal` 负责 checkpoint 与 crash recovery

更准确的说法不是“一个大而全的 AgentEngine”，而是：

- `LlmSession` = durable state machine
- kernel = coordination kernel / arbitration point
- completion subsystem = 配额受限的模型调用子系统
- tool subsystem = 带互斥与 side effect 约束的工具调用子系统
- `StateJournal` = durable session graph store

## 1. 先写死的不变量

这几条是本文最重要的部分，后面的接口和调度策略都只能在它们之内变化。

### 1.1 Session 与能力分离

`LlmSession` 是状态机，不是 executor。它不直接持有：

- `ICompletionClient`
- tool runner / tool executor
- socket / stream observer
- `CancellationTokenSource`
- in-flight `Task` handle

### 1.2 Completion 与 tool 是两个调度域

completion 与 tool 都是“session 的下一步”，但它们不是同一种资源：

- completion 受 `model-id`、provider 限流、并发额度、成本影响
- tool 受 side effect、互斥组、资源冲突、可取消性影响

所以第一版不做统一大队列。

### 1.3 所有外部完成信号都先入 kernel

completion 回包、tool 完成、timeout、cancel、persist 完成、手动恢复，都先转换成 `KernelEvent` / `WakeEvent`，再由 kernel 统一应用。

外部 callback 不直接深改 session graph。

### 1.4 Runtime overlay 不进入 durable state

不进入 `StateJournal` 的包括：

- ready queue membership
- active dispatch / active lease
- `CancellationTokenSource`
- stream observer / SSE 中间缓冲
- `Task` handle
- timer 对象
- lane 占用信息

### 1.5 MVP 明确只允许一个 active step

第一版强约束：

- 一个 session 同时最多一个 active step
- 一个 session 同时最多一个 active dispatch
- 一个 session 同时最多一个 active lease

这条规则优先于并发性能。

### 1.6 Crash recovery 必须诚实

恢复后不能伪装“半截 completion / tool 还在继续”。

不能证明安全重试的外部操作，不自动重放。

## 2. 四层边界

为了避免双真源，先把信息分成四层。

| 层 | 作用 | 典型内容 |
| --- | --- | --- |
| durable model | 重启后必须恢复的工作态 | session phase、history、pending tool call、pending operation、fault/recovery metadata |
| runtime overlay | 仅在进程内存在的调度现场 | wait reason、ready queue key、active dispatch、checkpoint dirty flag |
| mechanism | 运行时推进纪律 | mailbox、event ingress、dispatch、stale event 丢弃、checkpoint barrier |
| policy | 可调策略 | queue key、fairness、retry、timeout、mutex group、provider budget |

第一版要特别避免的，是把 mechanism 或 runtime overlay 伪装成 durable business state。

## 3. Durable 模型

### 3.1 `SessionPhase`

第一版先保持少量稳定 phase：

```csharp
public enum SessionPhase {
    CollectingInput,
    ReadyForCompletion,
    ReadyForTool,
    Suspended,
    Faulted,
    Completed
}
```

它表达的是 durable 工作阶段，而不是“当前在不在某个队列里”。

### 3.2 为什么不再把 `Running*` 放进 phase

`RunningCompletion`、`RunningTool`、`PersistPending` 更像 runtime / mechanism 视角：

- `Running*` 表达的是“已有外发实例正在飞行中”
- `PersistPending` 表达的是“当前还欠一次 checkpoint barrier”

这两类信息不适合和 durable business phase 混在同一个 enum 里。

### 3.3 `PendingOperation`

当一个 step 已经跨过“需要对外发出”这条边界时，必须有 durable 记录表达它的处境。

```csharp
public enum OperationKind {
    Completion,
    Tool
}

public enum PendingOperationState {
    Planned,
    Dispatched,
    ResultReceived,
    RecoveryUnknown
}

public enum ToolSideEffectClass {
    None,
    ReadOnly,
    IdempotentWrite,
    CompensatableWrite,
    NonCompensatableWrite
}

public enum RecoveryDisposition {
    None,
    ReadyToReplay,
    ResultUnknown,
    NeedsCompensation,
    NeedsManualResume,
    RecoveryBlocked
}

public sealed record PendingOperation(
    OperationKind Kind,
    long StepId,
    int Attempt,
    string CorrelationId,
    PendingOperationState State,
    string RequestDigest,
    string? ModelId,
    string? ToolName,
    string? ToolCallId,
    ToolSideEffectClass ToolSideEffectClass,
    RecoveryDisposition RecoveryDisposition,
    DateTimeOffset CreatedAt);
```

第一版不必把它做得很大，但必须能回答：

- 这一步是什么操作
- 是否已经可能对外发出
- 结果是否已收到
- 恢复后是否允许自动重放

### 3.4 `StepId` 只负责 session 内顺序

`StepId` 的职责是：

- 表示 session 逻辑上的第几步
- 保持审计与回放顺序

它不负责区分某一步的多次尝试，也不负责区分某一次具体外发实例。

## 4. Runtime overlay

### 4.1 `WaitReason`

`WaitReason` 是 runtime 字段，不是 durable phase 的替代品。

```csharp
public enum WaitReason {
    None,
    WaitingInput,
    WaitingCompletionLane,
    WaitingCompletionResult,
    WaitingToolLane,
    WaitingToolResult,
    WaitingPersist,
    WaitingExternalEvent,
    WaitingManualResume
}
```

### 4.2 最小 runtime overlay

```csharp
public sealed record SessionRuntimeOverlay(
    WaitReason WaitReason,
    string? ReadyQueueKey,
    DispatchToken? ActiveDispatch,
    bool CheckpointDirty,
    bool CheckpointInProgress,
    DateTimeOffset? DeadlineUtc);
```

建议第一版就明确：

- `WaitReason == None` 才表示当前可立即前进
- `ReadyQueueKey` 与 `ActiveDispatch` 不可同时存在
- `CheckpointInProgress` 只反映运行时现场，不提升为 durable phase

### 4.3 MVP 的关键合法性约束

- 一个 session 同时最多在一个 ready queue 中
- 一个 session 同时最多有一个 active dispatch
- `ActiveDispatch != null` 时，`ReadyQueueKey` 必须为 `null`
- stale result 不得修改 durable session state

## 5. 身份模型

为了让 timeout、cancel、retry、recovery 可判定，第一版就引入三层身份。

```csharp
public readonly record struct SessionStepKey(
    string SessionId,
    long StepId,
    int Attempt);

public readonly record struct DispatchToken(
    SessionStepKey StepKey,
    string DispatchId);
```

职责分工：

- `SessionId`：定位哪个 session
- `StepId`：定位逻辑步骤
- `Attempt`：定位该步骤第几次尝试
- `DispatchId`：定位某一次具体外发实例

所有与 in-flight work 绑定的事件都应至少带上 `DispatchToken`。

## 6. 统一事件入口

### 6.1 `KernelEvent`

第一版建议把一切会唤醒 session 的外部触发统一成事件。

```csharp
public enum KernelEventKind {
    InputAccepted,
    CompletionFinished,
    CompletionCancelled,
    CompletionFaulted,
    ToolFinished,
    ToolCancelled,
    ToolFaulted,
    TimeoutElapsed,
    CancelRequested,
    PersistFinished,
    ManualResumeRequested
}

public interface IKernelEvent {
    KernelEventKind Kind { get; }
    string SessionId { get; }
    DateTimeOffset OccurredAt { get; }
    DispatchToken? DispatchToken { get; }
}
```

### 6.2 Stale event 处理规则

如果一个事件带有 `DispatchToken`，但它不匹配当前 active dispatch：

- 不得直接修改 durable session state
- 只允许记日志、计数、审计，或进入显式 reconcile 路径

尤其对 tool，迟到结果不得悄悄并回主线状态机。

## 7. 最小公共 outcome envelope

completion 与 tool 可以保留不同 request / payload，但 kernel 边界需要一个最小公共 outcome 形状。

```csharp
public enum StepOutcomeKind {
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    Rejected,
    LostAfterRecovery
}

public interface IStepOutcome {
    DispatchToken DispatchToken { get; }
    StepOutcomeKind OutcomeKind { get; }
    string? LaneId { get; }
    DateTimeOffset FinishedAt { get; }
}
```

这样 kernel 至少能统一处理：

- lease retirement
- stale result 判废
- retry / fault / recovery 折叠

## 8. 顶层结构

### 8.1 Session Layer

`LlmSession`

职责：

- 持有 durable working state
- 吸收已被 kernel 接纳的事件
- 决定下一步是 completion、tool、suspend、fault 还是 complete

### 8.2 Kernel Layer

`AgentOsCoreKernel`

职责：

- 作为唯一 arbitration / commit point
- 维护 runtime overlay
- 维护 ready queue membership 与 active dispatch
- 把 step 投递给 completion 或 tool 子系统
- 应用 timeout / cancel / persist / recovery 规则

kernel 是协调面，不是“大内核总管”。

### 8.3 Completion Subsystem

职责：

- 按 queue key 管理 completion backlog
- 管理 lane capacity
- 在拿到明确 request 后调用 `ICompletionClient`
- 完成后只回投事件，不直接改 session

`ICompletionClient` 仍是低层 API adapter，不直接等于调度资源。

### 8.4 Tool Subsystem

职责：

- 按 queue key 管理 tool backlog
- 处理 mutex group / side effect 约束
- 执行 tool request
- 完成后只回投事件，不直接改 session

### 8.5 Persistence Layer

`StateJournalSessionStore`

职责：

- 保存 durable session graph
- 恢复 durable session graph
- 保存 `PendingOperation`、fault/recovery metadata

不负责：

- 维护 ready queue
- 维护 lease
- 维护 callback 栈

## 9. 调度模型

### 9.1 Completion：按 `model-id` 分队列

第一版直接采用：

- 每个 `model-id` 一个 completion ready queue
- queue 内 FIFO
- 非空 queue 间 round-robin

如果未来需要，再扩展到：

- `provider + model-id`
- `profile-id`

### 9.2 Tool：先一个全局队列

第一版优先最简单：

- 默认只用 `tool:global`

只有当互斥需求被真实证明后，再拆出：

- `tool:filesystem`
- `tool:journal-write`
- `tool:external-side-effect`

### 9.3 Fairness 的最低保真规则

第一版只保证：

- queue 内 FIFO
- queue 间 round-robin
- session 重新 runnable 时回到队尾
- 单次 kernel 处理对单 session 的事件数设上限，避免 wake storm 吞掉全局

不追求全局最优。

## 10. Kernel arbitration point

completion / tool 分开调度之后，反而更需要一个单一状态提交点。

第一版建议明确：

- scheduler 只决定“现在能不能接”
- lane 只负责“执行一次 request”
- kernel 独占以下权力：
  - 创建 active dispatch
  - retire dispatch / lease
  - 把 session 放入或移出 ready queue
  - 决定 retry / suspend / fault / complete
  - 丢弃 stale result

这样能明显减少双真源和竞态。

## 11. 持久化与恢复

### 11.1 `StateJournal` 的角色

`StateJournal` 在这里是：

- checkpoint store
- crash recovery store
- durable session graph store

它不是：

- 逐事件调度日志
- runtime call stack 镜像
- exactly-once 中间件

### 11.2 第一版的 commit boundary

为了降低复杂性，MVP 先只在语义边界做 commit，不追求逐状态切换落盘。

建议第一版至少在这些点 commit：

- 外部输入被正式接受后
- 需要对外发出 completion / tool 之前，先保存 `PendingOperation`
- 外部结果被吸收到 durable session state 后
- session 进入 `Completed` / `Faulted` / `NeedsManualResume` 之类的稳定语义节点时

第一版不要求对这些内容逐次 commit：

- ready queue 迁移
- lane 分配
- timer 注册
- 普通 wait reason 切换
- 流式 token chunk

### 11.3 恢复规则先从诚实性出发

恢复时：

- 不恢复 ready queue membership
- 不恢复 active dispatch / lease
- 不恢复 callback / observer / stream buffer

而是从 durable state 重新判断 session 该如何继续。

### 11.4 不同操作类别的默认恢复策略

#### Completion

- 默认视为“结果未知”
- 是否自动 retry 由 policy 决定
- 不把 `Attempt++` 当成唯一默认动作

#### Read-only tool

- 可默认允许自动重放

#### Idempotent write tool

- 只有工具 contract 明确提供稳定幂等键时才允许自动重放

#### Compensatable write tool

- 默认进入 `NeedsCompensation` 或等价恢复处置

#### Non-compensatable write tool

- 默认进入 `NeedsManualResume`
- 不自动重放

## 12. Agent-OS Core 与 .NET async 的关系

### 12.1 线程模型不是 OS 线程模型

当前负载以 I/O 为主，因此 MVP 不自造线程调度器。

我们自己的“线程模型”应理解为：

- `LlmSession` 是逻辑线程
- `.NET async/await + ThreadPool` 负责在等待 I/O 时释放真实线程

### 12.2 MVP 的最简单运行时形状

第一版建议直接做成：

- 一个 single-reader mailbox loop
- 多个 producer 往 mailbox 投递 `KernelEvent`
- kernel 串行消费事件并同步修改 session/runtime state
- executor 用 async I/O 执行 completion / tool

这里的关键是 single-reader，不是固定线程亲和。

### 12.3 推荐的 mailbox 技术路线

为了降低复杂性，MVP 直接用 `Channel<KernelEvent>` 即可。

原因：

- 语义直接
- 支持多 producer
- 可自然避免 lost wakeup
- 比自己拼 `AsyncAutoResetEvent` 更稳

### 12.4 流式回调的处理原则

completion 的 token/text delta 默认不直接进入 kernel 主邮箱。

第一版优先进入 kernel 的，是会改变 durable working state 的事件，例如：

- completion finished
- tool finished
- timeout elapsed
- cancel requested
- persist finished

如果 UI 需要流式显示，可以走独立 observer / telemetry 通道。

### 12.5 持久化先走更简单路线

考虑到 MVP 优先简单性，第一版可接受：

- kernel 在少数语义 barrier 上直接 `await` checkpoint

只要我们明确：

- 不在每次状态切换都持久化
- 不把队列 churn 放进 checkpoint 热路径

如果后续观测到 checkpoint 延迟明显拖慢整体吞吐，再拆 persistence worker。

## 13. 最小接口草图

```csharp
public interface ILlmSession {
    string SessionId { get; }
    SessionPhase Phase { get; }
    SessionDecision DecideNextStep();
    SessionTransition ApplyKernelEvent(IKernelEvent kernelEvent);
}

public interface IAgentOsCoreKernel {
    Task RunAsync(CancellationToken cancellationToken);
    bool Publish(IKernelEvent kernelEvent);
}

public interface ICompletionScheduler {
    ValueTask<DispatchAdmission> TryDispatchAsync(
        CompletionStepRequest request,
        DispatchToken dispatchToken,
        CancellationToken cancellationToken);
}

public interface IToolScheduler {
    ValueTask<DispatchAdmission> TryDispatchAsync(
        ToolStepRequest request,
        DispatchToken dispatchToken,
        CancellationToken cancellationToken);
}

public interface ISessionStore {
    Task SaveCheckpointAsync(string sessionId, CancellationToken cancellationToken);
    Task RestoreAsync(string sessionId, CancellationToken cancellationToken);
}
```

### 13.1 `TryDispatchAsync` 为什么不再返回 `bool`

布尔值不足以表达：

- accepted
- queue busy
- unsupported
- stale
- cancelled before dispatch

第一版建议用显式 admission result。

## 14. 实施顺序

### Phase 1

先定型这些基础类型：

- `SessionPhase`
- `WaitReason`
- `PendingOperation`
- `SessionStepKey`
- `DispatchToken`
- `IKernelEvent`
- `IStepOutcome`

### Phase 2

实现最简单 kernel：

- 单进程
- 单 mailbox loop
- completion 按 `model-id` 分队列
- tool 只用 `tool:global`
- 每个 session 同时最多一个 active step

### Phase 3

接入 `StateJournal`：

- checkpoint barrier
- `PendingOperation` 落盘
- restart recovery
- 诚实恢复而非伪 continuation

### Phase 4

只在被真实瓶颈推动时再引入：

- 更细粒度 tool mutex group
- persistence worker
- 更强 fairness
- provider-aware queue key
- session shard / partition loop

## 15. 哪些 OS 类比值得保留

值得保留的只有这些：

- ready / wait 分离
- wait reason
- wake event
- 统一 arbitration point
- completion 与 tool 分治

不建议继续加热的类比：

- `kernel = OS kernel`
- `completion lane = CPU`
- `tool queue = 完整设备驱动栈`

OS analogy 只用于解释直觉，不作为切分 durable/runtime 边界的依据。

## 16. 当前推荐结论

如果只追求一条稳妥且简单的技术路线，我推荐：

- `LlmSession` 先做成 durable state machine
- phase 与 runtime overlay 分离
- MVP 固定为 single active step per session
- completion 与 tool 分成两个子系统，不做统一大队列
- kernel 保留唯一 arbitration / commit point
- `StateJournal` 只存 durable working state 与 `PendingOperation`
- `.NET async/await + Channel` 作为底层执行机制
- checkpoint 先只做语义 barrier 持久化，不先上复杂 persistence pipeline

这条路线的优点是：

- 足够简单
- 语义清楚
- 容易 debug
- 恢复更诚实
- 以后仍有自然演进路径

## 17. 仍可后放的问题

- durable session state 是否直接用 `StateJournal` 对象图表达，还是经 DTO 中转
- completion queue key 最终是否纳入 provider / profile
- tool mutex group 是静态 metadata 还是 host policy 注入
- completion 自动 retry policy 的默认值
- 是否需要将流式 partial output 提升为一类更正式的事件
- persistence worker 的引入时机
