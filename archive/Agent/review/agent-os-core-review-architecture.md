# 结论

这份 draft 已经抓住了几个真正值得保留的核心判断：`LlmSession` 应是状态机而不是能力容器、completion 与 tool 应分成不同调度域、外部回调应先产出 `WakeEvent` 再由统一入口推进状态。这几条是文档里最稳的部分，也是后续实现最该守住的不变量。

当前最大的问题不是方向错，而是叙事重心还略偏向 `kernel` / OS analogy，导致读者容易先把它理解成“一个大一统内核”，再回头补分层细节。更稳的写法应是：**先讲 invariants 与边界，再讲分层，再讲术语，最后把 OS 类比降格为解释辅助**。如果沿着这个顺序重写，文档会明显更难误导实现。

# 主要问题

## 1. `kernel` 叙事仍然偏大，容易把实现拉向“大内核”

文档已经多次强调 completion/tool 是独立子系统，但标题、一句话定位、`AgentOsCoreKernel` 命名、以及若干段落里的口气，仍然容易让人脑补出一个“统一持有调度、资源、恢复、持久化语义的中心内核”。这会带来两个风险：

- 实现上容易把 queue policy、retry policy、checkpoint policy、resource bookkeeping 都堆进 `kernel`
- 读者会默认 completion/tool 只是 kernel 的内部执行分支，而不是平级的资源域/子系统

如果保留 `kernel` 一词，最好显式收窄它的定义：**它只是 session 状态推进的串行协调点，不是所有策略与资源语义的总拥有者。**

## 2. `policy`、`mechanism`、`runtime`、`durable model` 还没有被切成四层

这是当前最需要补强的一点。文档里已经隐约有这四层，但还没有被明确命名，所以很多内容混在一起：

- `model-id` 分队列、tool mutex group、fairness、retry、timeout，本质上更像 policy
- `WakeEvent` 入口、dispatch、统一 apply，本质上是 mechanism
- `Lease`、ready queue membership、timer、in-flight bookkeeping，本质上是 runtime
- session graph、checkpoint metadata、recovery metadata，本质上才是 durable model

这种混写会让第一版实现时很难判断“某个字段该不该进 `StateJournal`”、“某个决策是 host 注入还是 kernel 固化”。

尤其值得注意的是：

- `PersistPending` / `WaitingPersist` 看起来更像持久化机制泄漏进 session 主状态机，而不一定是 durable 业务相位
- `RunningCompletion` / `RunningTool` 一方面被写进状态机，另一方面又被说成 crash 后不能诚实恢复，这说明它们更像 runtime overlay，而不是稳固 durable state

## 3. completion/tool 虽已分开叙述，但 `executor` 一词又把层级重新揉回去了

文档里同时出现了这些概念：

- `CompletionScheduler`
- `ICompletionLane`
- `ICompletionClient`
- completion/tool executors

它们各自指向的是不同层次：调度器、资源槽位、底层能力客户端、执行子系统。但 `executor` 这个词太宽，容易把“谁负责排队”“谁负责分配 capacity”“谁真的发起调用”混成一层。

这会直接影响代码边界。否则很容易出现：

- `CompletionExecutor` 既负责 queue policy，又直接包 `ICompletionClient`
- `ToolExecutor` 既做 tool visibility，又做互斥调度，又做实际执行

也就是文档想避免的“大一统执行壳”又换个名字回来。

## 4. 若先讲 analogy 再讲 invariants，读者会更容易继承类比的副作用

这份 draft 里真正稳定的设计资产其实不是“像 OS 的什么”，而是这些不变量：

- session state 与 execution capability 分离
- callback 不直接深改 session
- completion / tool 是不同资源约束域
- runtime resource 不进入 durable checkpoint
- crash recovery 必须诚实降级

这些东西并不依赖 OS 类比成立。反过来说，OS analogy 只是帮助理解 ready/wait、wakeup、resource reservation 的辅助语言。若文档先把 analogy 放得太靠前，读者会更容易问“像不像 CPU / device / thread”，而不是先问“哪些状态是 durable、哪些只是 runtime”。

## 5. 若不额外收窄，`lane` / `lease` / `session` 这组命名都有潜在误导

- `lane` 在 completion 侧很好理解为 capacity slot，但在 tool 侧就容易和 queue、mutex domain、worker 混淆
- `lease` 在分布式系统语境里常带“可续约、TTL、失效回收”的含义；若这里只是进程内占用令牌，`reservation` 之类的词更不易误读
- `session` 当前同时像 durable aggregate、runtime instance、调度单元三者的总称；若不补充一层 `SessionSnapshot` / `SessionRuntimeRecord` 之类的命名，后续接口会越来越模糊

# 架构与命名改进建议

## 1. 建议把文档主线改成“先不变量，后类比”

推荐主结构：

1. 先写 system invariants
2. 再写四类边界：durable model / runtime model / mechanism / policy
3. 再写 completion 与 tool 是两个子系统
4. 最后把 OS analogy 放到“帮助理解”的附录式小节

这样读者先拿到“实现必须守住什么”，再看“借用了哪些比喻”。整体会稳很多。

## 2. 显式给出四分法，而不是让读者自己推断

建议文档直接放一个小表，把内容按四层归位：

- durable model：`SessionSnapshot`、durable phase、pending work、history、recovery metadata、checkpoint metadata
- runtime model：ready queue membership、`WaitReason`、in-flight reservation、timer、transient callback state
- mechanism：kernel event loop、`WakeEvent` ingestion、dispatch/apply discipline、checkpoint trigger pipeline
- policy：queue key selection、lane capacity、fairness、retry、timeout、tool mutex policy、recovery downgrade policy

一旦这个表摆出来，后文很多争论都会自动变简单。

## 3. completion/tool 建议明确写成“平级子系统”，不是“内核里的两条分支”

当前文档已经接近这个意思，但还可以再收口一些：

- kernel 只负责把 session 推进到“可提交 completion work”或“可提交 tool work”
- completion subsystem 自己负责 completion queue、lane、provider budget、dispatch policy
- tool subsystem 自己负责 queue、mutex domain、resource conflict、dispatch policy
- kernel 只消费它们返回的标准化 wake/result 事件

也就是说，**kernel 是协调面，不是资源域本身。**

## 4. 对命名做一次“降误导”收敛

比较推荐的方向如下。

### `kernel`

如果只是文档内部的核心协调器，可以考虑弱化为：

- `SessionRuntimeCore`
- `ExecutionCoordinator`
- `SessionKernel`

如果坚持保留 `kernel`，建议全文反复强调它是 **coordination kernel**，不是 OS kernel 的一揽子对应物。

### `executor`

建议少把它当总称使用，改按角色命名：

- 排队/调度：`Scheduler` / `Dispatcher`
- 底层调用适配：`Client` / `Adapter`
- 资源槽位：`Lane` / `Slot`
- 工具实际执行者：`Invoker` / `Runner`

`executor` 这个词最好只保留给非常窄的“接收 request、异步产出 result”的接口，不再拿来统称整个子系统。

### `lane`

建议只在“capacity slot”语义很明确的地方使用。completion 侧保留 `lane` 基本成立；tool 侧若主要表达的是互斥队列或资源域，可以改成：

- `ToolQueue`
- `ToolMutexDomain`
- `ResourceQueue`

除非你们真的希望两边都共享同一个“lane = dispatch slot”抽象，否则不必强求同名。

### `lease`

若它不包含续约和 TTL 语义，建议认真考虑改成：

- `Reservation`
- `DispatchReservation`
- `InFlightReservation`

如果仍保留 `lease`，最好在文档里补一句：**这里只表示进程内运行时占用期，不包含分布式 lease 的续约/过期语义。**

### `session`

建议显式拆开至少两个名字：

- `LlmSession`：领域对象/状态机实例
- `SessionSnapshot` 或 `DurableSessionState`：可持久化形态

若还需要承载 queue membership、reservation id、timer 等运行时字段，再单列：

- `SessionRuntimeRecord`
- 或保留 `SessionControlBlock`，但要明确它只是 bookkeeping record，不是 OS PCB 的强对应

## 5. 状态模型建议改成“durable phase + runtime overlay”双轴

这是我最推荐补进文档的一点。相比把所有东西塞进单一状态枚举，更稳的方式是：

- durable phase：`CollectingInput` / `RunnableForCompletion` / `WaitingCompletionResult` / `RunnableForTool` / `WaitingToolResult` / `Suspended` / `Faulted` / `Completed`
- runtime overlay：`ReadyQueueKey`、`InFlightReservationId`、`PersistDirty`、`PersistInProgress`、timer、attempt bookkeeping

这样有几个好处：

- `Running*` 不再假装自己是可恢复 durable state
- `PersistPending` 不再把 persistence mechanism 提升成 session 主相位
- crash recovery 规则会更自然，因为 runtime overlay 本来就允许丢失后重建/降级

如果第一版不想改太大，至少也建议文档明确写出：`RunningCompletion` / `RunningTool` / `PersistPending` 更偏 runtime execution view，不是“永远应进入 checkpoint 的真实业务相位”。

# 哪些 OS 类比值得保留，哪些应降温

## 值得保留

- `ready queue` / `wait queue` 分离：这是真正帮助思考调度边界的类比
- `WaitReason` / `WakeEvent`：这是最有价值的 OS 借鉴之一，而且几乎不带副作用
- “回调线程只记事件，统一调度点再 apply”：这条很像中断/事件投递纪律，值得保留
- `LlmSession` 像 lightweight process / coroutine：只在解释“不是一 session 一线程”时有帮助
- tool 像 device queue：只在解释 side effect、互斥、不可预测时长时有帮助

## 应降温

- `kernel = OS kernel`：这个类比最容易把实现带向大内核，应降到最低
- `completion lane = CPU`：可以作为口头类比，但不应让人误以为要做 CPU 式时间片或统一 run queue
- `tool lane = device`：可保留“队列化 side effect”这一层，别再往 driver stack / interrupt completion 上类推太多
- `SessionControlBlock = PCB`：可以借名字，但不要让人期待它承担地址空间、句柄表、权限边界那套含义
- `lease` 的类比：如果不打算做续约/过期语义，就不要让读者自动联想到分布式租约系统

## 一个推荐表述

建议在文档里直接加一句类似的话：

> OS analogy 只用于解释调度与等待的直觉，不作为接口切分与持久化边界的依据。真正的设计依据是 session/durable/runtime/policy 的不变量。

这句话会很有“收口”作用。

# 可延后问题

以下问题可以后放，不必阻塞主文档先把架构边界写稳：

- completion queue key 最终是 `model-id` 还是 `provider + model-id`
- tool mutex group 由静态 metadata 决定还是 host policy 注入
- 第一版是否允许单 session 多个 in-flight tool step
- checkpoint 是按事件、按时间，还是混合策略
- 是否需要单独 persistence worker
- durable state 是否直接采用 `StateJournal` 对象图，还是保留中间 DTO
- `SessionControlBlock` 这个名字最终是否保留

这些问题大多属于 policy 或实现演进问题；它们重要，但不应先于核心分层与术语边界定稿。
