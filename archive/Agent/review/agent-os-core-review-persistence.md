# 结论

当前 draft 的方向是对的：它已经明确 `StateJournal` 是 checkpoint store，不是 runtime lease 镜像；也已经承认 `RunningCompletion` / `RunningTool` 在崩溃恢复后不能伪装“半截调用还在继续”。这条总方向值得保留。

但从“恢复、持久化、StateJournal 边界”视角看，设计还没有收口到可实施。最大的缺口是：缺少一个 durable 的“操作边界”来表达某一步到底处于“已决定但未发出”“可能已发出但结果未知”“结果已收到但未 durable 吸收”“side effect 已确认完成”中的哪一种。没有这层，`Running* -> Attempt++`、`PersistPending`、tool side effect 重放都会落入不诚实的灰区。

建议后续修文以“可恢复性诚实原则”为中心：

- 未被 durable 吸收的进展，不算已完成。
- 已对外发出、但结果未知的操作，必须被表示为“未知”，不能被表示为“仍在继续”或“可安全重试”。
- 不能证明幂等或可补偿的 side effect，恢复后就不自动重放。

# 主要问题

1. **durable state 边界仍然过粗，无法支撑诚实恢复**

- draft 已列出 `session identity`、`history`、`pending tool calls`、`retry / fault / recovery metadata` 等，但还没有定义最小必存字段集。
- 目前缺少 durable 的 `PendingOperation` 或等价结构，无法回答“这一步是什么、是否可能已经发给外部、结果是否已经 durable 吸收”。
- 仅靠 `State + WaitReason + StepId + Attempt`，无法区分：
  - 还没发出请求
  - 请求可能已发出但结果未知
  - 结果已收到但尚未 checkpoint
  - side effect 可能已发生但无法确认

2. **`Running*` 恢复策略过于乐观，`Attempt++` 不能直接代表安全重放**

- completion 不是纯内部步骤：它可能已计费、可能非确定、也可能已经被 provider 执行。
- tool 更不能统一视为可重放：读工具、幂等写工具、可补偿写工具、不可补偿写工具的恢复策略完全不同。
- 如果 crash 后统一变回 `RunnableForCompletion` / `RunnableForTool` 并 `Attempt++`，就会把“未知是否已执行”误判成“安全可重试”。

3. **缺少 durable correlation id，恢复后无法做晚到结果对账与重复检测**

- `StepId` 适合做 session 内顺序号，但不足以承担外部相关性。
- completion 请求至少需要 durable `CompletionRequestId`。
- tool 执行至少需要 durable `ToolInvocationId`。
- 如果 provider 或 tool 支持幂等键，还需要 durable `IdempotencyKey` 或 `ExternalOperationId`。
- 没有这些字段，重启后既无法识别晚到回包，也无法诚实说明“这次 retry 是同一外部操作的续处置，还是一次新操作”。

4. **`PersistPending` 语义危险，容易把“已发生但未落盘”伪装成普通内部状态**

- 如果 session 逻辑已推进、tool side effect 已发生、甚至外部回包已到达，但 checkpoint 还没完成，进程崩溃后 durable 世界仍停在旧 head。
- 这时外部世界和 durable 世界已经分叉。
- 所以 `PersistPending` 不能承担“对外已经前进一步，只是稍后落盘”的语义；否则恢复后必然不诚实。

5. **checkpoint 频率与热路径的张力还没有变成可执行规则**

- draft 正确指出 `StateJournal` 不应退化成“每次状态切换都同步写盘的调度日志”，但还没有给出“哪些点必须 commit，哪些点绝不 commit”。
- `StateJournal` 当前 `Open` 是全量加载，`Commit(root)` 又以 root 可达图为边界；如果把 queue churn、wake event、stream chunk 都纳入高频 commit，调度热路径会很快被拖垮。
- 反过来，如果外部 side effect 前后没有明确的 checkpoint 栅栏，恢复就会失真。

6. **“诚实降级”还停留在口号，缺少恢复后的显式不确定性表达**

- 需要 durable 的 `RecoveryStatus` / `RecoveryDisposition` / `RecoveryReason` 一类字段。
- 否则系统只能在普通 `Runnable` 或 `Faulted` 中隐含“未知是否执行成功”“是否允许自动重试”“是否需要人工确认”，后续实现很容易各写各的。

# 可行改进建议

1. **为每个 in-flight 外部操作引入 durable `PendingOperation` 记录**

- 建议最少包含：
  - `OperationKind`：`completion` / `tool`
  - `StepId`
  - `Attempt`
  - `CorrelationId`
  - `DispatchState`：`planned` / `dispatched` / `result_received` / `resolved_unknown_after_restart`
  - `RequestDigest`：请求摘要，用于恢复后核对是否是同一步
  - `ModelId` 或 `ToolName`
  - `ToolCallId`
  - `SideEffectClass`
  - `IdempotencyKey` 或 `ExternalOperationId`（如果外部系统支持）
  - `CompensationPolicy`
  - `StartedAtUtc`
  - `RecoveryDisposition`
- `LaneId`、`Task` handle、`CancellationTokenSource`、socket、SSE buffer 仍然只放 runtime overlay。

2. **把外部调用改成“先 durable intent，再 dispatch，再 durable result”的栅栏模型**

- 在发起 completion 或 side-effecting tool 之前，先把 `PendingOperation` commit 到 `StateJournal`。
- dispatch 成功后，如果拿得到外部确认信息，再补记 correlation / external id。
- 收到结果后，在让该结果驱动下一步 session 决策之前，先 durable 吸收结果或至少 durable 记录“结果已收到但未应用”。
- 只有当结果已经 durable 吸收，才能把这一步视为真正完成。
- 这本质上更接近一个轻量 outbox/inflight 记录，而不是把 `StateJournal` 当成逐事件 WAL。

3. **建立基于 side effect 分类的恢复决策表，不再统一 `Attempt++`**

| 操作类别 | 崩溃后默认动作 | 自动重放条件 |
|---|---|---|
| completion | 标记为结果未知，再按策略决定 retry 或暂停 | 仅在明确接受重复计费/非确定输出时 |
| 只读 tool | 可自动重放 | 默认允许 |
| 幂等写 tool | 可自动重放 | 需要稳定 `IdempotencyKey` 或等价 contract |
| 可补偿写 tool | 先进入待补偿/待确认 | 需要 durable `CompensationPolicy` |
| 不可补偿且非幂等 tool | 不自动重放，转人工恢复 | 默认禁止 |

4. **把必须 commit 的边界写死，把绝不进入热路径的状态写死**

- 必须 commit：
  - 外部输入被正式接受之后、向调用方确认之前
  - completion/tool 的 intent 形成之后、真正 dispatch 之前
  - 外部结果被 session 吸收之后、其影响外显之前
  - session 进入 `Completed`、人工恢复等待、不可自动补偿故障等最终语义节点时
- 绝不应要求逐次 commit：
  - ready queue 迁移
  - lane 分配
  - lease 创建/释放
  - wake event 入队/出队
  - timer 注册
  - 普通等待原因切换
  - 流式 token chunk 到达但尚未形成 durable 语义片段

5. **把“诚实降级”做成明确状态，而不是隐含在注释里**

- 恢复后至少应能表达：
  - `ReadyToReplay`
  - `ResultUnknown`
  - `NeedsCompensation`
  - `NeedsManualResume`
  - `RecoveryBlocked`
- 如果不想扩大顶层状态枚举，也至少要在 durable metadata 中保留这些恢复处置语义。
- 恢复时应把这类信息写入 session 自身的 recovery note，而不是只留在日志里。

6. **completion 请求 ID 与 tool invocation ID 都应成为 durable correlation id**

- `StepId` 仍然保留，负责 session 内排序。
- `CompletionRequestId` 负责关联一次真实的外发 completion 请求。
- `ToolInvocationId` 负责关联一次真实的 tool 运行。
- 如果未来接回调、重试、跨进程恢复或人工审计，这三个 ID 的职责不要混用。

# 与 StateJournal 边界的建议

`StateJournal` 在这里应被严格看作“durable 工作态对象图”，不是运行中调用栈镜像，不是调度日志，也不是 exactly-once 中间件。

| 必须 durable | 绝不能 durable |
|---|---|
| `SessionId`、schema version、稳定 root 下的 session graph | `Task` handle、线程、callback 栈 |
| 已接受的外部输入 | `CancellationTokenSource` |
| 已被 session 吸收的 completion 结果 / tool 结果 | socket、stream observer、SSE 中间缓冲 |
| `PendingOperation`、`StepId`、`Attempt`、`CompletionRequestId`、`ToolInvocationId` | `Lease`、`LaneId`、当前 queue node 位置 |
| side effect 分类、补偿策略、恢复处置、recovery note | ready queue / wait queue 的运行时实例 |
| 下一次恢复必须知道的 fault / retry / recovery metadata | 短生命周期 timer 对象 |

- `WaitReason` 是否 durable，取决于它是否承载恢复语义；如果只是调度提示，可重建，不必强存。
- `ReadyQueueKey` 通常应视为可重建策略结果，而不是 durable 事实；真正 durable 的应是决定它的业务字段，如 `model-id`、tool policy、mutex class。
- `PersistPending` 更适合作为 runtime overlay 或“已有 durable intent，等待下一次 commit 落最终结果”的中间语义，而不应被当成“逻辑已经安全前进”的 durable 事实。
- `Commit(root)` 的 root 决定了对象图边界，所以建议维持稳定 root，把 session durable state、pending operations、recovery metadata 都挂在稳定子树下，不要依赖旧进程里的对象引用。
- `Repository` 有进程独占锁，`Revision` 也不是并发对象图编辑器；更稳的边界是由 kernel 作为唯一 writer 修改 `Revision` 并 commit，executor 只返回结果 DTO 或 `WakeEvent`。
- `CommitAddress` / branch head 是存储地址，不应拿来充当业务 request id、tool invocation id 或恢复相关 correlation id。
- 如果后续需要做“恢复后审计/取证/手动 replay”，可以考虑从最近 durable head 派生恢复 branch 做分析；但不要把 branch 当作热路径调度状态。

# 可延后问题

- durable session state 是否直接用 `StateJournal` 对象图表达，还是保留中间 DTO 层，这个可以在恢复语义先收口后再决定。
- completion queue key 最终是否采用 `model-id`，还是 `provider + model-id + profile`，这不是当前恢复闭环的阻塞项。
- 是否引入单独 persistence worker，也可以等先把“哪些点必须 commit”定清后再优化。
- 一个 session 是否允许多个 tool step 同时 in-flight，属于更高阶调度问题；在恢复语义未定前，单 step in-flight 更安全。
- 流式 partial output 是否需要可恢复，目前可以延后；第一版优先保证“整步结果”的诚实恢复。
