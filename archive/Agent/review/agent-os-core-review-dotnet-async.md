# 结论

这个 draft 从 ".NET async / ThreadPool / 单进程 I/O 密集型运行时" 视角看，**总体是现实可行的**，而且方向基本正确：MVP 应建立在 `.NET async/await + ThreadPool` 之上，把 Agent-OS Core 的"线程模型"定义成**逻辑串行状态推进模型**，而不是 OS 线程模型。

我支持 draft 中"completion/tool 回调不直接改 session，而是统一汇入 kernel"这一主线；这正是 .NET 下最稳的实现路线。对当前阶段来说，**一个串行的 kernel 状态变更点 + async I/O executor + 显式 wake event**，已经足够支撑 MVP。

但要把它做成一个真正可运行、可恢复、可取消、可 debug 的内核，还需要补齐几个关键约束：**事件入口必须具体化、过期结果必须可判废、取消链必须有所有权、持久化不能阻塞主循环、MVP 必须坚持每个 session 同时最多一个 in-flight step**。如果这几件事不写清楚，最后很容易在 .NET 的 callback/ThreadPool 现实里退化成"表面 actor-like，实际到处并发改状态"。

# 主要问题

- **"单 kernel 事件循环"的语义还不够精确。** 在 .NET 里，MVP 真正需要的不是"一个固定线程"，而是"一个 single-reader 的串行状态变更循环"。`await` 之后 continuation 跳到别的 ThreadPool 线程是完全正常的；安全性不能建立在线程亲和性上，只能建立在"同一时刻只有一个消费者在改 kernel/session 状态"上。

- **`TickAsync` 这个接口形状容易把实现带向 re-entrant polling。** 如果生产实现真的是多个地方并发调用 `TickAsync`，那 draft 的串行语义就会被破坏。生产态更像是一个长期运行的 `RunAsync`/`ProcessLoopAsync`，`TickAsync` 只适合测试或 deterministic stepping。

- **`IWakeEvent` 只有 `SessionId` 远远不够。** 在 timeout、cancel、retry、进程恢复后重放的场景下，旧的 completion/tool 结果很可能晚到。若事件里没有 `StepId`、`Attempt`、`LeaseId` 或其他 correlation token，kernel 就无法安全地区分"当前有效结果"和"过期结果"，这会直接导致错写 session state。

- **回调汇入统一状态点这件事，draft 还停留在原则层，没有落到 .NET 机械层。** `CompletionStreamObserver` 这类回调会在任意 ThreadPool 线程上触发；tool 完成回调、timer 回调、取消回调也一样。若没有一个明确的线程安全入口（例如 `Channel<KernelEvent>`），实现时很容易偷偷在 callback 里改共享状态。

- **持久化边界和主循环之间的关系还不够清楚。** 如果单 kernel loop 直接 `await SaveCheckpointAsync`，那么一次磁盘慢写或 journal flush 就会卡住所有 session 的状态推进。对 I/O 密集型负载来说，这会比模型调用本身更容易制造全局串行瓶颈。

- **取消传播还缺一个明确的 ownership model。** 至少要区分：host shutdown、kernel stop、session cancel、step timeout、provider request abort。`CancellationTokenSource` 本身当然不应进 durable state，但 runtime 必须有地方持有"当前 lease 对应的 linked CTS"。否则"谁有权取消谁"会越来越乱。

- **draft 还没有把"每个 session 仅一个 in-flight step"明确写成 MVP 纪律。** 如果一开始就允许一个 session 同时挂多个 completion/tool step，那么事件排序、结果判废、恢复、取消、checkpoint 一下子都会复杂一个量级。以当前 draft 的抽象成熟度，MVP 不适合这么做。

- **流式回调与 kernel mailbox 之间的边界需要收窄。** token/text delta 这种 UI 友好型流式事件不应全部进入 kernel 主邮箱；否则 mailbox 很快会被高频小事件淹没。真正需要进 kernel 的，应是会改变 durable working state 的事件，例如"completion 完成"、"tool 完成"、"timeout"、"cancel"。

# MVP 实现建议

- **把 kernel 明确定义成一个 actor-like 的 single-reader mailbox loop，而不是单线程专属 loop。** 最简实现是一个长期运行的 `RunAsync(CancellationToken)`，内部串行读取 `Channel<KernelEvent>`；状态安全来自 single reader，不来自固定线程。

- **跨线程入口优先用 `Channel<KernelEvent>`，不要先上 `AsyncAutoResetEvent`。** 对这个场景，`Channel` 同时解决了"有数据"和"唤醒消费者"两个问题，还天然避免 lost wakeup。MVP 可以直接用 `Channel.CreateUnbounded<KernelEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false })`。

- **session 本体尽量保持同步状态机。** `ILlmSession.DecideNextStep()` 和 `ApplyWakeEvent(...)` 这种接口保持同步是对的；不要让 session 自己去 `await` 网络、文件或 timer。I/O 应该以"effect/request"的形式由 kernel/executor 处理，再以 wake event 回灌。

- **completion/tool 调度的最小落地模型：**
  - kernel 拥有 session table、wait reason、runtime lease 表、按 `model-id` 的 completion ready queue、一个全局 tool ready queue。
  - 当 session 变成 runnable 时，kernel 只做同步 bookkeeping，并尝试把它 dispatch 到可用 lane。
  - 真正的 `ExecuteAsync(...)` 在独立 async runner 中等待完成；完成后只往 mailbox 写入 `CompletionFinished`/`ToolFinished`。

- **把"唯一状态变更点"落实成下面这个模式：**

```csharp
private readonly Channel<KernelEvent> _mailbox = Channel.CreateUnbounded<KernelEvent>(
    new UnboundedChannelOptions {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

public async Task RunAsync(CancellationToken cancellationToken) {
    await foreach (var evt in _mailbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) {
        ApplyEvent(evt);          // 同步改内存状态
        DispatchReadyWork();      // 同步挑选可发出的 step
    }
}

private void StartCompletion(CompletionStepRequest request, CompletionLease lease) {
    _ = DispatchCompletionAsync(request, lease);
}

private async Task DispatchCompletionAsync(CompletionStepRequest request, CompletionLease lease) {
    try {
        using var cts = CreateLeaseCts(lease, request.Timeout);
        var result = await _completionLane.ExecuteAsync(request, cts.Token).ConfigureAwait(false);
        _mailbox.Writer.TryWrite(new CompletionFinished(lease, request.StepId, request.Attempt, result));
    }
    catch (OperationCanceledException) {
        _mailbox.Writer.TryWrite(new CompletionCancelled(lease, request.StepId, request.Attempt));
    }
    catch (Exception ex) {
        _mailbox.Writer.TryWrite(new CompletionFaulted(lease, request.StepId, request.Attempt, ex));
    }
}
```

- **Wake event 至少要带上 `StepId + Attempt + LeaseId` 级别的关联信息。** kernel 应只接受与当前 runtime overlay 匹配的结果；其余都当 stale event 丢弃并记日志。这个判废机制是 async/cancel/retry 体系里最关键的保险丝之一。

- **MVP 明确只允许每个 session 同时一个 in-flight step。** 这样可以把 session 看成单入口单出口的逻辑协程，很多复杂问题都能降一个维度。

- **取消传播建议这样分层：**
  - host lifetime token：整个 kernel 进程退出。
  - session cancel intent：session durable state 中可见，但不是 `CancellationTokenSource`。
  - lease runtime CTS：由 kernel 在 dispatch 时创建，来源于 `CreateLinkedTokenSource(hostToken, sessionCancelToken)`，并叠加 step timeout。
  - completion/tool executor 只收 `CancellationToken`，不拥有取消语义主导权。

- **timeout 的 MVP 做法可以先偏简单。** 不必一开始就做 timer wheel；可以给每个 in-flight lease 一个 `CancelAfter`，再用一个粗粒度 `PeriodicTimer` 负责做超时清理、checkpoint flush、metrics/health tick。`PeriodicTimer` 适合 maintenance，不适合替代 mailbox。

- **持久化建议解耦出单独 worker，而不是在主循环里直接 await。** 最小方案是 kernel 发出 persist request 到另一个 channel，writer 完成后再发 `PersistFinished` 回主邮箱。只有当某个边界确实要求"先 durable 再前进"时，session 才进入 `WaitingPersist`；不要把所有状态切换都绑成同步写盘。

- **`Task` / `ValueTask` 的取舍建议保守一些：**
  - `Task`：lane 执行、completion 请求、checkpoint、dispatch runner。它们几乎总是异步，且会被组合、缓存、重试、跨层传递。
  - `ValueTask`：只保留给真正高频、经常同步完成的 leaf API。仓库里现有 tool 接口已经是 `ValueTask<ToolExecuteResult>`，这一层可以保留；但 kernel/scheduler 外围接口没必要为了"看起来高级"而全面切到 `ValueTask`。

- **对 I/O 密集型场景，优先推荐"少对象、少 scheduler、少线程概念"的结构。** 一个 kernel mailbox loop、少量 executor lanes、少量后台 runner，就已经足够。不要把每个 session、每个 queue、每个 timer 都做成独立 actor/task。

- **对阻塞型或 CPU 型 tool，要显式隔离。** MVP 可以规定"默认 tool 应 async/cancel-aware"；若必须包一层同步阻塞 API，则只在对应 tool lane 内部有限度地 `Task.Run`，并施加小并发上限。不要把整个 kernel 或所有 tool 调用都习惯性包进 `Task.Run`。

- **是否需要 shard/partition loop：MVP 先不要。** 等到单 mailbox loop 真成瓶颈，再按 `SessionId` hash 做 session-affine 分片，而不是按 model 或 tool 类型分片。原因是 session 状态所有权天然跟 `SessionId` 走；completion/tool 结果也更容易回路由到拥有该 session 的 shard。

- **继续保持库代码 `ConfigureAwait(false)` 的习惯，但不要把正确性建立在它上面。** 在 .NET 现代服务/控制台进程里通常没有自定义 `SynchronizationContext`，所以真正保证不会并发改状态的，仍然是 mailbox discipline，而不是 `ConfigureAwait(false)` 本身。

# 不建议过早引入的复杂度

- 不要为 Agent-OS Core 自定义 `TaskScheduler`、`SynchronizationContext` 或专属线程泵。

- 不要给每个 session 一个常驻 `Task.Run` loop，更不要给每个 session 一个专属线程。

- 不要一开始就做 shard kernel、多级优先级、抢占式时间片、公平性优化器；这些都应该等真实瓶颈出现再说。

- 不要把 completion、tool、persist、stream delta 全压进一个"统一大队列"并试图用一套算法吃掉全部问题。

- 不要把 token/text delta 级别的流式事件塞进 kernel 主邮箱；它们会制造高频噪音，却不一定带来状态价值。

- 不要在单 kernel loop 里大量 `await` 外部 I/O，尤其不要一边持有共享状态引用一边 `await`。

- 不要在 callback 线程里"只改一点点状态"；这类特例一旦放开，后面几乎一定会蔓延。

- 不要在 MVP 里承诺 side-effecting tool 的 exactly-once 语义。以当前恢复模型，更现实的表述是"at-least-once 或需要宿主/工具自带幂等性"。

# 可延后问题

- **kernel 分片何时需要。** 先靠指标判断：mailbox backlog、平均 event latency、ready queue 累积、persist backlog、in-flight lease 数量。没有数据前，不值得提前拆 shard。

- **completion 流式接口是否要从 callback observer 升级为 `IAsyncEnumerable<StreamEvent>`。** 这在 typed event、backpressure、组合式处理上会更舒服，但不是 MVP 必需。

- **更细粒度的定时器系统。** 只有当 timeout 数量大、精度要求高、`PeriodicTimer` 扫描成本明显上升时，再考虑 deadline queue 或 timer wheel。

- **更丰富的 lane capacity / provider rate limit / adaptive fairness。** MVP 先做"不饿死、可观测、可解释"就够了。

- **更强的持久化策略。** 包括批量 checkpoint、group commit、事件日志与快照结合、crash consistency 优化，这些都应在基础语义跑稳后再做。

- **多 step 并发的 session。** 除非后面明确需要 speculative execution、并行 tool fan-out，或者 session 内部天然是 DAG，否则不建议早做。

- **CPU 密集型工作负载的专门执行池。** 如果未来真的出现 embedding 批处理、本地解析、大型 diff/merge 等 CPU 热点，再考虑专门的 bounded worker pool；对当前 I/O 主导场景没必要先设计。
