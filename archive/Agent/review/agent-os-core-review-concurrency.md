# 结论

这份 draft 的总体方向是对的：`LlmSession`、kernel、completion/tool scheduler、`StateJournal` 的分层很清楚，`WaitReason` / `WakeEvent` / `Lease` 这些术语也抓到了真正的问题空间。

但从“并发与调度正确性”看，当前版本还缺一层更硬的调度约束：`session 唯一 ready membership`、`session 唯一 active lease`、`wake event 与 lease 的强关联`、`取消/超时/恢复的状态坍缩规则`、以及 `kernel 作为唯一 arbitration point`。这些如果不在 MVP 前定下来，后面最容易出现的不是“性能不够”，而是重复投递、late result 覆盖新状态、ready/wait 分裂、恢复后误重放等语义错误。

completion / tool 分开调度我赞成，但这不等于去掉统一裁决点。相反，分开之后更需要 kernel 保留唯一的状态提交入口。

# 主要问题

## 1. `session` 重复投递 / 重复 dispatch 的防线还不够

参见 [draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:319)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:357)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:719)。

- 文档已经区分了 `State`、`WaitReason`、`ReadyQueueKey`、`InFlightLeaseId`，但还没有定义“同一个 session 在任意时刻最多只能有一个 ready membership”。
- `EnqueueSession`、`PublishWakeEvent`、`InputArrived`、`TimeoutElapsed`、`ManualResumeRequested` 都可能把同一个 session 再次推向 runnable；如果没有 `ready-enqueued bit` 或等价的防重机制，同一 session 很容易被重复放进 ready queue。
- `StepId` 本身也不够防重复。一个 session 如果因为重复 enqueue 被多次扫描到，可能在逻辑上还是同一个 step，却被重复 dispatch 到 completion/tool 子系统。
- 对 tool 来说，这不是“多算一次 token”的问题，而是重复 side effect。

## 2. `ready queue`、`WaitReason`、`State` 之间缺少线性化更新点

参见 [draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:315)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:337)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:447)。

- 当前 draft 把几个关键字段分开建模是对的，但还没有定义它们的“合法组合”。
- 于是会出现一类危险的中间态：`RunnableForCompletion + WaitingToolResult`、`RunningCompletion + InFlightLeaseId = null`、`Suspended + 仍在 ready queue`、`PersistPending + 又被直接投到 tool queue`。
- 单 kernel 事件循环会降低竞态概率，但不会自动保证字段组合永远合法；只要状态迁移不是经由同一个提交点完成，就仍然会出现 ready/wait 分裂。
- 当前接口里 `DecideNextStep()` 和 `ApplyWakeEvent()` 还是偏“对象自己改自己”，但调度字段实际属于 kernel 的所有权边界，这个边界还没收紧。

## 3. `WakeEvent` 与 `lease` 回收之间的竞态还没有被封口

参见 [draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:357)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:547)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:606)。

- 目前 `IWakeEvent` 只有 `SessionId`，而 `CompletionLease` / `ToolLease` 只有 `SessionId`、`StepId`、`LaneId`、`StartedAt`。
- 更关键的是，SCB 里提到了 `InFlightLeaseId`，但 `Lease` 记录本身没有显式 `LeaseId`。这意味着“当前 lease”和“旧 lease 的迟到结果”在协议层无法可靠区分。
- 典型竞态是：timeout 触发，kernel 回收 lane 并准备 retry；此时旧 completion/tool 结果迟到回来。如果 wake event 没带 lease 级别的关联标识，旧结果就可能错误地写进新 attempt 的 session。
- 对 tool 来说，这个问题比 completion 更严重，因为“本地 timeout”不代表“外部 side effect 没发生”。

## 4. 取消、超时、恢复时的状态坍缩策略还不完整

参见 [draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:365)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:497)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:603)。

- 当前 draft 说 `Running*` 恢复后可降级为 `Runnable* Attempt++` 或 `Faulted`，但没有给出选择条件。
- 也没有区分这些常见但语义完全不同的情况：`cancel requested but not delivered`、`cancel delivered but not acknowledged`、`timeout elapsed locally`、`result arrived after timeout`、`tool 可能已经造成外部副作用`。
- 如果没有明确的“状态坍缩表”，同一个竞态在不同实现者手里会被折叠成不同结果，系统行为会飘。
- 尤其是 tool：默认把 timeout / crash recovery 都折叠成 `RunnableForTool Attempt++` 风险很大，这相当于默认“可以盲重试”，而这在 side effect 语义上通常不成立。

## 5. fairness 目前还是目标，不是机制

参见 [draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:478)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:641)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:756)。

- 文档说“第一版只需保证不明显饿死”，这个方向没问题，但现在还没有一个最低可执行的公平性规则。
- 仅按 `model-id` 分 queue，再加一个 `tool:global`，并不能自然保证公平。热 session、短 completion、频繁 wake 的 session 都可能持续占据调度注意力。
- 单 kernel 事件循环还会放大另一个问题：如果一次 tick 把某个 session 的 wake storm 全吃完，其他 session 可能长期等不到仲裁。
- 没有明确的 queue 选择顺序、requeue 规则、每 tick 处理预算时，后面很难判断“这是实现 bug”还是“fairness 就是这么定义的”。

## 6. completion/tool 分离后，kernel 仍需要统一 arbitration point，但当前草图还不够明确

参见 [draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:426)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:641)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:723)。

- 文档已经明确“不搞统一大队列”，这一点我认同。
- 但“不统一 ready queue”不等于“不需要统一裁决点”。completion scheduler 和 tool scheduler 可以各管各的 lane capacity，但 session 状态提交、lease retirement、timeout/cancel 折叠、ready membership 变更，仍然必须回到 kernel 一个地方完成。
- 现在接口更像“scheduler 试着 dispatch，session 自己 `ApplyWakeEvent`”，缺少一个明确的 `kernel arbitration / commit` 概念，容易让 timeout policy、retry policy、late-result policy 不小心散落到子系统里。
- 一旦这些策略分散，completion/tool 分离带来的不是清晰，而是双份并发语义。

## 7. `single step in flight` 还停留在待决问题，但它其实是 MVP 的前置约束

参见 [draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:323)、[draft](/repos/focus/atelia/docs/Agent/LlmSession-Execution-Kernel-Draft.md:800)。

- 现在 SCB 只有一个 `InFlightLeaseId`、一个 `WaitReason`、一个 `ReadyQueueKey`，这套形状天然更适合“每个 session 同时最多一个 active step”。
- 如果 MVP 阶段允许多个 in-flight tool step，当前状态机会立刻失真：你无法用单个 `WaitReason` 表达“既在等 tool A 结果，又因为 tool B timeout 需要恢复”，也无法用单个 `InFlightLeaseId` 做正确匹配。
- 所以这不太像一个可以延后的小优化，更像一个必须先拍板的建模前提。

# 可行改进建议

## 1. 在 MVP 明确写死：`每个 session 同时最多一个 active step / lease`

- completion 和 tool 都按这个规则处理。
- 如果模型一次返回多个 tool calls，第一版可以串行 dispatch，或者把它们封成一个“tool batch step”，但不要直接放开多 lease。
- 这不是保守，而是在用最小约束换最大正确性。

## 2. 给每次 dispatch 引入强关联标识，统一用于 `lease`、`wake event`、取消、超时

建议新增一个 runtime 级标识，例如：

```csharp
public readonly record struct DispatchToken(
    string SessionId,
    long StepId,
    int Attempt,
    long LeaseId);
```

- `CompletionLease` / `ToolLease` 持有这个 token，而不是只放 `SessionId + StepId`。
- `CompletionFinished` / `ToolFinished` / `TimeoutElapsed` / `CancelAcknowledged` 这类和 in-flight step 绑定的 wake event，都必须带上同一个 token。
- `InputArrived` / `ManualResumeRequested` 这种 session 级事件可以不带 token。
- 这样 kernel 才能判断一个 wake event 是“当前 lease 的结果”还是“旧 lease 的迟到尾包”。

## 3. 补一个显式的 `Kernel Arbitration Point`

建议把当前隐含的“统一入口”进一步命名并制度化，例如叫：

- `KernelArbitrationPoint`
- `KernelCommitPoint`
- `StepArbiter`

它的职责建议明确写成：

- kernel 独占 `SessionControlBlock` 的调度字段所有权。
- kernel 独占 ready queue membership 的增删。
- kernel 独占 lease 的创建、退休、超时、取消折叠。
- completion/tool scheduler 只负责 lane capacity 和实际执行，不负责 session 状态提交。
- `ILlmSession` 负责 durable domain state 的推进，但调度态变更由 kernel 包裹提交。

一个很实用的落地方式是：`ApplyWakeEvent` 不直接修改 ready queue / lease，而是返回一个 `SessionEffect` 或 `ArbitrationDecision`，最后由 kernel 一次性提交。

## 4. 为 `State + WaitReason + ReadyQueueKey + InFlightLeaseId` 定义“合法组合表”

如果不想再引入新大类，至少把下面这些组合写成硬约束：

- `RunnableForCompletion` => `WaitReason = WaitingCompletionLane`，`ReadyQueueKey != null`，`InFlightLeaseId = null`
- `RunningCompletion` => `WaitReason = WaitingCompletionResult`，`ReadyQueueKey = null`，`InFlightLeaseId != null`
- `RunnableForTool` => `WaitReason = WaitingToolLane`，`ReadyQueueKey != null`，`InFlightLeaseId = null`
- `RunningTool` => `WaitReason = WaitingToolResult`，`ReadyQueueKey = null`，`InFlightLeaseId != null`
- `PersistPending` => `WaitReason = WaitingPersist`
- `Suspended` / `Faulted` / `Completed` => 不在任何 ready queue，且无 active lease

同时建议所有状态迁移都经过有限个 helper，例如：

- `MoveToReady(...)`
- `MoveToInFlight(...)`
- `RetireLeaseAndCollapse(...)`
- `MoveToSuspended(...)`
- `MoveToFaulted(...)`

不要让实现者随手改四五个字段拼状态。

## 5. 引入 session 级防重与 wake mailbox

这是防重复投递最省事的一层：

- 一个 runtime `ReadyEnqueued` bit，保证 session 在 ready queue 里最多出现一次。
- 一个 session 级 `WakeMailbox` 或 `PendingWakeFlags`，把高频、可合并的 wake 先折叠起来，再由 kernel 消费。
- `InputArrived` 如果 session 已经 ready 或 in-flight，不再重复入队，只记录“有新输入待吸收”。
- `CancelRequested` 也优先落 mailbox，由 kernel 判断是“尚未 dispatch，可直接取消”，还是“已有 lease，需要转成 cancel-in-flight”。

这样可以同时解决 duplicate enqueue 和 wake storm。

## 6. 给取消 / 超时 / 恢复定义一张最小“状态坍缩表”

建议至少先把 completion 与 tool 分开定义：

- completion timeout：默认是“本地等待超时”，不是“远端一定没完成”；只有 provider 支持可靠 cancel 或幂等重放时，才自动 retry。
- tool timeout：默认进入 `Faulted` 或新增 `NeedsRecovery` / `UnknownExternalEffect`，而不是自动 `RunnableForTool Attempt++`。
- crash recovery for `RunningCompletion`：若有 provider idempotency key 或结果去重能力，可重试；否则至少要带显式 `RecoveryReason`。
- crash recovery for `RunningTool`：默认按“外部效果未知”处理，除非 tool metadata 明确声明 `SafeToRetry`。
- cancel 与 result 同时到达时，按“第一个被 kernel 提交的终态事件生效，后续同 token 事件只记审计不改状态”处理。

如果想更稳一点，可以给 tool 增补两个 metadata：

- `RetrySafety = SafeToRetry | Unknown | Unsafe`
- `CancellationSemantics = Cooperative | None | ExternalAckRequired`

## 7. 给 fairness 一个最低可执行版本，不要只留口号

第一版其实不用复杂，下面这组规则就够用了：

- queue 内 FIFO。
- 非空 completion queues 之间 round-robin。
- tool queues 之间也 round-robin；如果第一版只有 `tool:global`，至少保证 requeue 到队尾。
- 同一 session 在一次仲裁提交后，如果又变 runnable，重新排到队尾，不允许在同一轮无限自旋。
- 每个 tick 对单个 session 处理 wake event 的数量设上限，避免 wake storm 吞掉整个 kernel。
- 若某 queue 或 session 等待时间超过阈值，打观测告警，必要时做 aging promotion。

这已经足够支持 MVP，而且可测、可解释。

## 8. 明确“迟到结果”的处理策略

建议新增一个术语，例如：

- `StaleWakeEvent`
- `LateCompletion`
- `LateToolResult`

处理规则建议写死：

- token 不匹配 active lease 的结果，一律不得直接修改 durable session state。
- 这类事件只允许做日志、诊断、补偿流程触发，或者写入单独的 orphan record。
- 对 tool，如果迟到结果证明 side effect 已经发生，应走 recovery / reconcile，而不是悄悄并回主线状态机。

这条规则会极大降低 timeout/retry 相关的误伤面。

# 建议补充的不变量 / 验证点

- `AtMostOneReadyMembership`：一个 session 同时最多出现在一个 ready queue 中。
- `AtMostOneActiveLease`：MVP 中一个 session 同时最多拥有一个 active lease。
- `ReadyQueueKey != null` 当且仅当 session 处于 `RunnableForCompletion` 或 `RunnableForTool`。
- `InFlightLeaseId != null` 当且仅当 session 处于 `RunningCompletion` 或 `RunningTool`。
- `WaitingCompletionResult` / `WaitingToolResult` 必须伴随一个可匹配的 active lease。
- 所有 lease-bound wake event 都必须携带 `DispatchToken`；只带 `SessionId` 不足以做正确仲裁。
- 同一个 `DispatchToken` 的终态事件只能被 kernel 提交一次；后续同 token 事件只能作为审计或 stale event 处理。
- 旧 lease 的 wake event 不得覆盖新 attempt 的状态。
- `RunningTool` 在 timeout / crash recovery 后，默认不得自动重试，除非 tool metadata 明确声明 `SafeToRetry`。
- restore 之后不应“复活旧 lease”；恢复得到的是新的 runnable/faulted 状态，而不是继续等待旧 in-flight handle。
- `PersistFinished` 这类事件若保留，也应带 checkpoint sequence，旧 checkpoint 完成通知不得覆盖新 checkpoint metadata。

建议至少补这些验证场景：

- 同一个 `CompletionFinished` 被重复投递多次，只能生效一次。
- `TimeoutElapsed` 与 `CompletionFinished` 并发到达，最终只能有一个终态提交。
- `CancelRequested` 在 dispatch 前到达、dispatch 后到达、结果到达后到达，三种路径结果都要稳定。
- session 正在 `RunningCompletion` 时收到 `InputArrived`，不能导致重复 dispatch，但下一轮必须看见新输入。
- old attempt 的 completion/tool 结果迟到到达，不能污染 new attempt。
- tool timeout 后迟到返回 success，系统必须进入显式 reconcile 路径，而不是自动当成功合并。
- 热 session 连续产生 wake event 时，冷 session 仍能在有界时间内获得一次调度机会。
- crash 发生在 dispatch 后、结果回包前；恢复后不会把同一个外部操作无条件重放。

# 可延后问题

- completion queue key 是否只看 `model-id`，还是从一开始就纳入 provider / profile。这影响吞吐和限流，但不先于正确性。
- checkpoint 是单独 worker 还是混在 kernel tick 中。这个影响吞吐和尾延迟，但不妨碍先把事件仲裁语义定死。
- fairness 是否要引入权重、成本感知、tenant 配额。MVP 只要有可解释的最低规则即可。
- tool queue 是否从一开始就拆出 `filesystem` / `journal-write` 等 mutex group。只要 `single active step per session` 先定死，这部分可以后推。
- 流式 completion 的 partial chunk 是否需要单独的 wake type。若引入，也应该服从同一 `DispatchToken` 和同一 arbitration point。
- 将来如果要支持“一个 session 多个 in-flight tool step”，建议把它当成下一层模型升级，而不是在当前单 lease 状态机上硬补字段。
