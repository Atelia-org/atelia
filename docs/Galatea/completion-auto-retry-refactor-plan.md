# Completion 自动重试与 SessionJournal 持久化边界重构方案

状态：实现与故障验收完成，按用户选择收口本地交付；真实试运行与公开 NuGet 发布另行安排。完成度与实际验证见[实施记录](completion-auto-retry-implementation.md)。日期：2026-09-16。

源码基线：Atelia `cc5e569f1373f416a99f3ffa3e78f9cb5c3702ac`；
atelia-completion `3ae1ebeccdd94a7bd507444154a283201a68c992`，与研究时
`eng/CompletionDependency.props` 的 preview.1 来源一致。研究阶段只读源码、测试和配置，
没有调用 provider、启动服务或修改角色状态；本文不重新确认任何角色的当前运行状态。

## 1. 建议采用的最小模型

**一次已接受的生成任务可以重新计算；只接纳一次完整结果，再执行该结果中的工具。**

保留 `ObservationAccepted` 与语义 `CompletionRequestPrepared`。新调用不再向主事件链写
`CompletionAttemptStarted`，也不为每次暂时失败写 `CompletionAttemptFailed`。
调用、断流、次数、退避和预览均属进程内执行状态。完整且验证通过的结果以一次
`AgentActionProduced` 提交；真正的工具执行继续使用独立的 durable 边界。

```text
Observation → Prepared ── 完整且合法的结果 ──→ Action → 工具执行/结果 → 下一 Prepared
                  │                            └─ 无工具：轮次完成
                  ├─ 暂时失败 → 清理 → 退避 → 重试同一 Prepared
                  ├─ 环境/合同错误 → 保留 Prepared，blocked
                  └─ 用户停止/本次输入明确不成功 → TurnEnded
```

“连接已断开”应落实为**本次调用已经退出并完成资源清理**，而非共享 client 的某个连接布尔值。
断开不能证明远端没有计算或计费；本方案允许这一重复，保证的是本地结果接纳和工具因果顺序，
不宣称远端 exactly-once。当前 Completion wire 只声明本地 function tools，没有把 Codex
代行任务或其他远端执行操作塞进本次纯生成调用。

## 2. 需求账本与证据边界

| 编号 | 要求及来源 | 对设计的约束 |
|:--|:--|:--|
| U1 | 本轮用户：朝无人值守连续运行演进，断开且无完整结果应择机重试 | 暂时失败不能在一次或固定 N 次后永久等待人工 |
| U2 | 本轮用户：质疑开始/重试进入主链，允许联合重构 Completion | 重审旧 Started/Refuse 合同；旧文档不是不可变产品要求 |
| U3 | 本轮用户：研究、形成方案、使用 subagents 与 dialectical-simplification | 交付设计；不实施或操作 live 状态 |
| C1 | Engine 的现有执行顺序：验证完整结果、提交 Action、再执行工具 | 允许重算，不允许从旧输入重跑已经执行的工具 |
| C2 | 当前单服务、每角色 TurnLock、EventJournal expected-head 提交 | 复用单写者；不引入分布式 lease、调度数据库 |
| C3 | 当前已有 raw 历史及 RecapGrid/邮件/记忆地址引用 | 保留历史解码，不改写旧事件地址与 selected lineage |
| C4 | Completion 已发布 preview.1；Atelia 显式引用版本 | 联调后发布新版本；不能覆写旧包或暗用兄弟仓 |
| C5 | README：Stop 只停止当前轮；网页关闭不停止服务端 Agent | 浏览器断开不取消；Stop 完成后允许未来正常自主激活 |
| P1 | 本方案产品建议，非已有承诺：接受断流/超时后的重复计算及可能重复计费 | 不要求保存计费账本；退避约束频率，不承诺严格费用上限 |
| P2 | 本方案产品建议：30 分钟单次调用期限；5 秒起、300 秒封顶退避 | 是可调默认值，不是“模型已经失败/停止”的证据 |

旧 README、此前设计与由其生成的测试只作为旧行为证据，不当作多个独立需求来源。
上游 AGENTS/传输文档中的“terminal 前中断不得透明重试”需随实施收窄为：库不擅自重试，
宿主依据调用的业务语义决定是否再次生成。仍禁止把 partial 当作 Completed。

## 3. 当前实现为何不能自行恢复

| 源码入口 | 已确认的行为 | 重构含义 |
|:--|:--|:--|
| [Engine](../../prototypes/SessionJournal/SessionJournalEngine.cs)，`StartAndExecuteCompletionAttemptAsync` / `ResumeCompletionAsync`，约 3068、3226 行 | 请求发出前写 Started；普通 transport 异常留下 AwaitingCompletion；默认 Refuse | 合并待发送与调用结果未知两个生成阶段 |
| 同文件 `ExecuteCommittedCompletionAttemptAsync`，约 3100 行 | Completed、Invocation、tool policy 校验后才 append Action | 重试范围可以精确截在 Journal 写入之前 |
| [AutomaticTurnCoordinator](../../prototypes/Galatea/GalateaAutomaticTurnCoordinator.cs)，约 149、183 行 | interval=0 无 reply wake 早退；非 Idle 阻塞 | 启动恢复已有工作独立于是否允许新 heartbeat |
| [AutonomyCadence](../../prototypes/Galatea/GalateaAutonomyCadence.cs)，`SettleMainTurn` | heartbeat 任意 non-completed 后暂停 | retry-wait 不得结算成轮次失败或暂停 |
| [Services](../../prototypes/Galatea/GalateaServices.cs)，约 2141 行；[CompletedTurns](../../prototypes/SessionJournal/SessionJournalEngine.CompletedTurns.cs)，约 952 行 | 已知失败调用 AbandonFailedTurn，selected head 退到 observation 前 | 会连本轮已提交工具因果一起移出 lineage；改为追加业务结束事实 |
| [StopController](../../prototypes/Galatea/GalateaTurnStopController.cs)，约 37 行 | dispatch 后只置 Observer.ShouldStop | 无后续 frame 时不能及时停止；改用单次调用取消 |
| Services，约 2090 行；[前端](../../prototypes/Galatea/wwwroot/assets/galatea.js) | 同一逻辑轮次内各次 Completion 共享 filter、tool 状态，delta 持续拼接 | 每次 attempt 隔离预览及文本过滤状态 |

上游额外发现（路径相对于 `/repos/focus/atelia-completion`）：

- `src/Completion/CompletionHttpRequestUtility.cs:48` 丢弃 Retry-After/provider code；错误正文读取异常时存在 response 未释放路径。
- `src/Completion/OpenAI/OpenAICodexResponsesException.cs` 已声明 RetryAfter，但当前错误分类未真正填入响应头值。
- `OpenAIChatClient.cs:119` 在请求 stream usage 时继续读取 terminal 后的数据；`GeminiClient.cs:90` 也可能为 trailing usage 等待。与传输 README 的“terminal 到达立即返回”不一致。
- `ICompletionClient.cs:13` 允许共享实例上的重叠调用；因此不能添加全局 `IsConnected` 来决定重试。

这些是源码结论；本次未运行故障注入测试，不能称为已复现的在线根因。

## 4. 三层职责与上游最小改动

### 4.1 Completion：完成一次调用，报告事实

在 `Completion.Abstractions` 增加一个窄的失败事实结构，示意名称如下，实施时统一命名：

```csharp
CompletionFailureInfo(
    Kind,                 // Transport / Http / Provider
    int? HttpStatusCode,
    string? ProviderCode,
    TimeSpan? RetryAfter);
```

共享于类型化调用异常与 `CompletionResult` 的 provider-failure 信息；保留现有
Completed/Incomplete/Failed 终止分类。结构不带 IsRetryable、DispatchState、连接状态、
attemptId 或用户必须确认的标志。重试资格由 Galatea 决定。

具体改动：

1. HTTP 错误保留状态、结构化 provider code、Retry-After（delta/date 均解析）；SSE provider error 也保留稳定 code。缺字段就是未知，不解析自然语言错误正文作策略。
2. 只在实际 SendAsync、打开/读取 response stream 等 I/O 边界归一化 transport 错误。parser、投影、observer 抛出的异常保持可区分；不能包住整个流循环后把所有 IOException 都当网络故障。
3. terminal 前 EOF 明确报告 stream interruption；caller cancellation 保留其 token。每次调用拥有独立 parser/聚合器/response，正常返回或异常退出均释放资源。
4. 权威 terminal 一经解析即收口，不为可选 usage 再等网络数据。已收到的 usage 保留，未收到的字段保持 unknown；不补零。测试锁定 terminal 后 half-open/读取异常不会丢掉完整结果。
5. 不加入库级通用退避循环。Codex 现有“401 后发现 credential generation 改变，仅立即重新发送一次”的认证协商保留并单独测试/计数；它不重试 429/5xx/断流，也不取得 OAuth refresh 所有权。

若 client 不响应取消，不能靠 `Task.WaitAsync(timeout)` 放弃等待后另发请求。
必须保留旧调用的所有权、禁止重叠补偿，并显示 transport-unresponsive；这属于 client 合同缺陷。
内建 HTTP client 的阻塞 read 可取消性须由测试证明。

### 4.2 Galatea：唯一重试策略所有者

增加一个 Galatea 内部 `ICompletionClient` decorator，仅用于主角色生成的实际调用，
放在现有 runtime 注入点。一次 Engine 调用该 decorator，内部可执行多次纯生成 attempt；
它不包含 Action commit、工具执行、邮件提取或整个 RunTurnAsync。
decorator 原样转发 Name、ApiSpecId、两个调用 overload 和 InvocationOptions，保留返回结果的
Invocation 与 reasoning Origin；不能改变 provider 身份或丢弃非默认 cache hint。

现有 AcceptedTurnRunner 在整个逻辑轮次保留 TurnLock，退避期间不释放角色写权。
其他角色继续运行；同角色新输入沿现有 busy 规则处理。复用共享 HttpClient，
每次只释放本次 response/stream，不销毁其他角色仍在使用的 client。

```text
固定本次 Prepared 对应的 request
循环：
    检查 shutdown / user stop
    创建本 attempt 的 observer/filter、linked deadline token
    await 一次 underlying client 调用及清理
    Completed → 返回给 Engine 验证并提交
    已知暂时失败 → 撤销当前预览，发布 retry-wait，取消感知的退避
    其他结果/异常 → 交给明确的 blocked 或业务结束分支
```

同一进程内重试复用同一个已重建请求，不重选 recall、Recap、输入、连接或工具集。
重启后 v9 按原语义 Prepared 与当前 renderer 重建；v7/v8 继续遵守其 exact 重建合同。
不自动换模型/连接，不把重试变成一次 FreshSend。

### 4.3 SessionJournal：计划、结果与业务因果

- 保留语义 Prepared、expected-head 提交、Action、ToolExecutionStarted、ToolResultObserved。
- 新写入删去 Started 与逐 attempt Failed；移除生成路径上的 Refuse/RestartWithNewAttempt 和其 UI 确认位。
- 生成恢复只返回一个 pending requirement，带现有 source Prepared、current head 及绑定 runtime 所需字段；不另造任务 ID。
- 新 Action 使用明确的新 schema 版本，允许父节点为 Prepared 或合法的历史 Started 尾。旧 Action 仍按旧 schema 验证 Started parent，不能全局放宽旧字节验证。
- 新 Action 不承接 Started 的请求 hash/length。当前这些字段仅有旧 codec 验证消费者，没有业务恢复消费者；诊断需要时记摘要日志即可。
- 所有可能已经发布 ref 的写入异常使 writer 不可继续使用；冷重开后以 selected lineage 判定下一步。已知提交前的 CAS 冲突只重新读取状态，不谎称网络失败。

## 5. 故障分类、退避和期限

以下是建议默认政策，实施应使用表驱动测试，而非异常 message 匹配。

| 情况 | 行为 | Journal 与下一步 |
|:--|:--|:--|
| 建连/读流故障、terminal 前 EOF；宿主单次期限到期 | await 清理后重试 | 保持同一 Prepared，不写失败 attempt |
| HTTP 408/429/500/502/503/504；明确 provider overload/rate-limit/temporary internal error | 按 Retry-After 与退避重试 | 保持 Prepared |
| 429 携明确永久配额/账户 code | blocked | 不凭状态码覆盖更具体的稳定错误事实 |
| 401/403、失效配置、context 太长、投影/协议不兼容、未知 provider code | blocked | 保留 Prepared；显式恢复、修配置后的重启重新判定 |
| 明确 refusal/content filter、完整 terminal 的输出上限 Incomplete | 结束本次输入，标明非成功原因 | 写 TurnEnded(Rejected/Incomplete)，不提交 partial 为 Action |
| 用户 Stop | 取消本次生成/退避；到安全边界结束 | 写 TurnEnded(Stopped) |
| 服务 shutdown、进程退出 | 取消并 drain；不记用户停止 | Prepared 留待启动恢复 |
| observer/程序错误、Journal/derived corruption、工具结果未知 | 对应层 blocked/recovery | 不进入 Completion retry loop |
| Action/TurnEnded 写入发布不确定 | poison + reopen | 先确认提交事实，不能重新生成或直接收尾 |

认证失败本轮不增加自动登录/刷新器；外部凭据轮换的自动唤醒可在有真实需求时单独接入。
这仍会使凭据失效成为可解释的阻塞，不把“无人值守”宣称为任何配置故障都能自愈。
进程内 blocked 必须阻止每 10 秒 pulse 再次 dispatch；显式恢复或服务重建才重新判定。
blocked 时新输入不能覆盖原 Prepared：先恢复原任务，或显式 Stop 闭合后再接受新输入。

建议首个等待 5 秒，此后指数增长至 300 秒；增加 0..20% 正 jitter。
有效 Retry-After 是下界，不被 300 秒上限截短；非法/负值忽略，过大值需按可表示时钟范围安全处理。
成功完成一次主生成后重置计数；暂时故障不设固定次数后永久 paused。
计数、nextRetryAt 为瞬态，重启先短暂 jitter 再续接，不承诺重启保持精确退避或累计费用预算。

单次调用建议默认 30 分钟，允许 connection 覆盖，期限由 Galatea linked CTS 执行；
从 underlying 调用开始计时，包含其本地 admission/preflight。没有 token 输出不是失败证据，
不增加 stream-idle 判死策略。期限到达代表宿主决定停止等待，远端可能仍计算。
配置建议只暴露必要的 attempt timeout；退避常量先集中定义，不增加通用策略 DSL。

## 6. Stop、业务结束与已执行工具

新增一个业务事件 `TurnEnded`，只表达“该轮不再生成”的决策，原因限于
Stopped / Rejected / Incomplete 等明确业务结束。它不表示某次连接失败，环境错误不写此事件。

事件从当前合法 completion frontier 追加，保留已接受输入与已提交 Action/ToolResult，
令业务轮次进入 Idle。其来源轮次由 lineage 确定，不再生成一套关联 identity。
Stop 与调用成功竞争时由同一写者串行决定：Action 已提交就尊重已提交事实；
尚未提交则可选择结束并丢弃未提交结果。不能让迟到结果越过 TurnEnded。

必须区分两种 token：

- user stop 立即取消正在等待的 Completion/退避，阻止下一次模型 dispatch；不直接取消已承诺工具的 execution token。
- host shutdown 控制服务资源生命周期，按既有工具恢复合同处理中断；不被当作用户结束轮次。

实现上 user-stop token 留在 decorator 与轮次停止控制器中，不直接代替 Engine/ToolSession 的
生命周期 token。停止造成调用退出后，Engine 释放本次 mutation scope，宿主再以 exact head
调用安全结束操作；该操作重新检查完整工具批次已闭合。结果提交和结束提交都在同一角色写权下，
不从 Stop HTTP handler 并发写 Journal。

`AwaitingToolExecution` 时不允许直接追加 TurnEnded。即使 head 是 ToolResultObserved，
也可能还有同一 Action 的其他待执行工具。已承诺工具按已有机制结算到
`AwaitingAgentAction` 后再结束；结果未知的实际副作用仍按工具自己的规则阻断。
本轮不发明伪 ToolResult 或通用工具撤销机制。

Stop API 可先确认“请求已接受”，UI 显示 stopping；只有 TurnEnded 落盘才显示 stopped。
请求接受后、落盘前 crash 不承诺停止已经完成。因此不新增 durable StopRequested。
停止当前轮不关闭角色；正 interval 在轮次结束后重新计时。

还需支持**没有 liveTurn 的待处理任务**：现有 `/chat/turns/{turnId}/stop` 仅设置运行中控制器，
无法结束已经退出 runner 的 blocked Prepared。新增角色级 `/chat/turns/pending/stop`，请求携带
expectedHead；取得 TurnLock、确认无活动写者并重读安全边界后，直接追加 TurnEnded(Stopped)。
head 改变或工具未闭合则返回冲突，不调用 provider；maintenance 禁用。前端 blocked 页面同时
提供“恢复原任务”和“结束待处理轮次”。未接受 Observation 前的停止无需伪造 TurnEnded。

### 业务结束的消费者必须同步改造

现有 `CompletedTurns` 约 319 行假定 Idle 的该轮一定有 terminal Action。不能只给 reducer 加一个 case。

| 消费者 | 新合同 |
|:--|:--|
| ExpectedObservationTurn / recent-turns / UI | 区分 Completed(Action) 与 Terminated(reason)，二者都 Closed；不伪造成功 Action |
| durable reply lease | Observation 已被该结束轮消费，结算 lease，记录非成功结束；不得 rollback 为 Ready 再自动投递同一输入 |
| character-mail delivery | 保留 Observation 已送达的证明；送达与角色是否产生回答分离，不因 Terminated 重发 |
| Note/发信 extraction | 只消费真实已提交的适用 Action；无 terminal Action 的结束不得生成假提取目标 |
| HistoryTimeline / Recap / tail projector | 认可结束边界；保留输入与工具因果，用类型化非成功结束信息渲染，不能破坏 tool-use/result 配对 |
| Undo / abandon | 不把自动错误处理绑定到整轮 rewind；已有显式 Undo 独立，不宣称撤销外部副作用 |

Terminated 不计为成功生成，也不触发失败 heartbeat 的永久暂停。环境 blocked 仍有原 Prepared，
不会通过新 heartbeat 不断造新 Observation；服务重启可再检查/尝试一次，遇同错再次 blocked。
本方案不要求把环境阻塞原因持久化，也不承诺该类故障重启后零 provider 调用。

## 7. 启动恢复、SSE 与后处理

启动为所有已配置且已有 session 的角色做有界检查，包括 interval=0、players=[]。
缺失且 interval=0 的 session 不因检查被 provision；读取边界后再决定是否 attach 执行。
沿用每角色 pulse，优先级为：已有轮次恢复/旧结算 → Ready reply → 到期的新 heartbeat。
maintenance 仍禁写。在线 attempt 由 runner/decorator 持有，不再交给 pulse 安排第二份重试。

| 冷启动边界 | 行为 |
|:--|:--|
| Observation 已接受、尚无 Prepared | 通过现有 readiness/计划路径准备一次，不重新接收输入 |
| Prepared 或历史合法 Started 尾 | 绑定原连接与计划，自动调用同一生成路径 |
| Action 已提交、工具待执行/结果未知 | 按既有工具合同续接或阻塞，绝不重新生成该 Action |
| 工具全部结算 | 从最新边界准备后继生成，不重复工具 |
| terminal Action / TurnEnded 已提交 | 不恢复已结束轮次；仅结算旧 receipt/lease 与允许的后处理 |
| 旧 Failed 尾 | 首版保持 blocked，不自动生成；允许在严格验证其来源 Prepared、完整工具批次已闭合后显式追加 TurnEnded(Stopped)。新 Action 不接受 Failed parent |

SSE 增加瞬态 attempt 开始/重置及 retry-wait 信息；状态携带次数、归一化错误码与 nextRetryAt。
每次 attempt 新 observer、InlineThinkTextFilter、thinking/tool 提示状态。重置范围是当前未提交
生成预览，不删除此前已提交 Action/工具展示。重连 replay buffer 也必须遵守同样范围，
不能仅清 DOM 却把旧 delta 再播放回来。不持久化 attempt 序号，不输出凭据或完整错误正文。

主模型暂时失败在 decorator 内处理，不触发 FinishTurn 的失败暂停。
已有 Action 之后发生 Note/邮件提取故障时，也绝不重新生成 Action；由已有 reconciler 处理
原目标。本轮最小切片不把这些 feature calls 纳入主模型装饰器；后续可以分别采用同一失败
分类与退避原语，但提取后的存储/发信仍由各领域的 durable settlement 负责。
因此第一切片完成不等于所有 admission/后处理故障都已无人值守自愈。

## 8. 故障窗口与不可删的约束

| 轨迹 | 必须得到的结果 |
|:--|:--|
| Prepared 落盘 → crash，尚未发送 | 重启自动生成，不创建第二个 Observation/Prepared |
| 远端计算完成 → 本地断流，无完整结果 | 清理后可以重算；承认可能重复计费 |
| 收到若干 partial tool call → EOF | 丢弃预览；工具执行次数为 0 |
| 收到完整结果 → 尚未写 Action → crash | 可以重算；没有本地已接纳结果被重复执行 |
| Action ref 发布/flush/返回抛错 | writer 停用，冷重开；若 Action 在 selected lineage，额外 provider 调用为 0 |
| 冷重开仍为 Prepared | 按 pending 生成恢复；不凭日志或全局扫描把孤立 Action 升为 authority |
| Tool A 已执行并结算 → 后继生成断流 | 只重试后继 Prepared；Tool A 执行仍为 1 次 |
| Action 含 A/B，A 已结算，Stop 到来 | 不能直接 Idle；按工具规则闭合 B 后才记结束，unknown 则保持工具恢复边界 |
| terminal 已到 → trailing usage 永不结束 | 立即返回业务结果，不因 usage 再次生成 |
| observer 自己抛 IOException / parser 发现非法 shape | 不标为可恢复网络故障 |
| client 不响应取消 | 不另发重叠请求；暴露合同故障并保留调用所有权 |

提交不确定与网络结果未知不同：前者可能已有本地业务事实，需要先恢复其 authority；
后者尚无可接纳结果，重复计算符合本轮产品目标。

## 9. 历史格式与跨仓交付

不重写旧 raw 文件，不回退 selected head 来“清理 Started”，不重编号 event kind。
保留旧 Started/Failed 的解码、严格 parent 链与既有证据验证；这由真实磁盘历史要求，
不是为未发布 API 留长期兼容层。新 runtime 只保留一套生成策略。

旧 v7/v8 Prepared 继续 exact 重构；v9 使用语义重构。已存在的 verification-only 格式
不借本轮偷偷开放执行。新 schema 的 Action 可直接接 Prepared；旧 schema Action 仍要求
旧 Started。audit、forward fold、tail resolver、dependency fold、history projection 必须一致。
TurnEnded 的新 schema 单独允许上述已验证的 legacy Failed 安全尾作为 parent；这只是显式结束，
不扩大旧失败事件的自动重试权限，也不改写其原始事实。

跨仓顺序：上游源码联调使用显式 `UseCompletionSources` / `CompletionSourceRoot`，验证后
按[依赖指南](../completion-dependency.md)产唯一版本包，再更新 Atelia 的包版本/来源身份。
交付决定（2026-09-16）：用户选择先本地交付，试运行后另行安排 NuGet 发布。因此本轮 pin
唯一开发包、保留冻结本地 feed 与显式配置，不推送、不公开发布，也不宣称普通 nuget.org restore 可用。
同步修改上游传输/quick-start/AGENTS 的旧重试政策说明；本次不改其当前行为文档冒充已实施。

真实实例切换另作部署步骤：停服、状态目录外备份、在隔离副本验证 strict open/audit、
用 fake client 验证旧尾恢复与工具不重放，再切换 binary。回滚需要恢复配套备份，
旧 binary 不保证理解新 Action/TurnEnded。源码和合成 fixture 验收不等于真实实例已升级。

## 10. 最小纵向实施切片与验收

按可工作的纵向切片推进，不先造通用重试基础设施。

### S1：纯生成重试的完整闭环

联合修改上游失败事实/资源清理/terminal 收口、Engine Prepared→Action 路径、Galatea decorator、
冷启动恢复、Stop/TurnEnded 及必要消费者、SSE reset。保留原工具执行合同，不扩展 feature-call
策略。上述项共同组成最小可部署切片：缺 Stop 或冷启动恢复不能宣称已满足持续运行。

建议先以 synthetic session 打通“断流 → 同 Prepared 重试 → 唯一 Action → 冷重开”，
再补齐多工具与消息消费；在全部通过前不部署到角色状态。

### S2：扩大持续运行覆盖

基于 S1 相同失败事实，逐个接入旧 admission、Note/发信提取等纯生成阶段的自动退避，
保留其领域提交/幂等边界。不是给 ReconcileDurableAdmissionAsync 整体套 catch-and-retry。
为各阻塞原因明确自动恢复或人工修配置的分类，消除完成主模型后仍因暂时提取故障永久暂停的路径。

实施审阅补充：session attach 必须保持 provider-free；非 liveTurn 的 admission 生成同样需要
可见、可停止、可由 shutdown drain 的调用所有权。因此在已有 TurnLock 内登记瞬态 admission
operation，精确 operationId 仅用于 Stop fencing，不写 Journal、不成为新的调度或结果 authority。
Stop 取消本次整理，保留原 durable target；下一 pulse 可以续接。acceptance 失败清理只做
provider-free 结算，不能在 CancellationToken.None 下启动无限生成。详见实施记录与 runtime/API。

### S3：包消费与实例切换

验证明确版本包的独立消费者、Atelia 集成，以及隔离旧历史副本升级，再进行另行安排的真实部署。
不以普通 build 成功代替包来源或真实状态验证。

| 验收组 | 必须覆盖 |
|:--|:--|
| 上游 HTTP/SSE fixtures | 408/429 Retry-After/date、5xx、结构化 code、preterminal EOF、取消阻塞 read、错误正文读取失败仍 dispose、observer IOException 不重试 |
| 上游 terminal | Chat/Gemini terminal 后不再等待 usage；Responses/Anthropic 现有成功/拒绝/协议检查不退化；usage unknown 保真 |
| 上游 Codex | 401 同 generation 不内层循环、换 generation 至多一次协商；429/断流无内层退避重试；并发调用取消隔离 |
| Engine/recovery | Prepared 之后零个新 Started/attempt Failed；多次调用一个 Action；历史 v7/v8/v9 与 Started 尾；新旧 Action parent 分别严格验证 |
| 存储故障 | Action 与 TurnEnded ref 发布前/后失败；cold reopen 判定；已提交结果不再调用 provider |
| 工具与结束 | 两工具间 Stop、后继生成断流、unknown tool 仍阻断；Terminated 保留输入和已提交工具，不回退 lineage |
| Host/scheduler | interval=0 pending、players=[]、shutdown/restart、maintenance、其他角色不被退避阻塞、原配置绑定不被默认连接替换；无 liveTurn 的 blocked/legacy Failed 能以 exact head 显式结束且零 provider 调用 |
| UI/SSE | failed partial 不拼入成功回答、attempt filter 重建、重连不复播旧 partial、Stop accepted 与 stopped 区分 |
| 邮件/Note/Recap | Terminated 正确结算输入消费，不重发 reply；无 Action 不作成功提取；Timeline/readiness/Undo 理解新结束边界 |

测试优先使用 FakeTimeProvider、脚本化 ICompletionClient、HTTP/SSE fixture 与 journal failpoint；
不靠真实 provider 制造断线。受影响 .NET 构建/测试串行执行，沿用 `--no-restore -m:1 -nr:false`。
S1 可选廉价模型 smoke 只证明正常连接，不替代上述故障验收。本次研究未执行这些验收。

## 11. 辩证审查结论与暂缓项

三位独立 reviewer 分别担任需求怀疑者、最小架构师、语义守卫；首轮独立阅读，第二轮交换
最强反例。剩余“环境错误是否结束轮次”单独裁决为保留 Prepared。以下按证据判定，非投票。

| 判定 | 对象、来源与当前消费者 | 成本/最小替代 | 删除或保留错误时的具体后果 | 可信度 |
|:--|:--|:--|:--|:--|
| delete | 新 Started、其新请求摘要、逐 attempt Failed；旧设计/codec/audit | 去掉两类新写入，诊断走日志 | 没找到阻止工具重复的需求；工具以已提交 Action 为界 | 高 |
| merge | AwaitingCompletionDispatch / AwaitingCompletion 生成语义；Engine recovery | 单 pending requirement，不保留两套执行策略 | 只删事件不改 coordinator，重启仍卡住 | 高 |
| simplify | retry 调度；当前 AcceptedTurnRunner + TurnLock | decorator 持有在线轮次；pulse 仅恢复/准入 | 多层 retry 会放大调用；每次释放锁会引入额外竞争状态 | 高 |
| keep | Prepared；真实 Recap/输入/runtime 绑定 | 复用原计划和 existing head | 重跑 FreshSend 会重复输入、换上下文或重放工具 | 高 |
| keep | 工具日志与提交不确定恢复；真实外部效果/磁盘窗口 | 原工具机制；统一 writer 停用与重开 | tool 已执行却失去因果；已提交 Action 被重复生成 | 高 |
| simplify | Stop/输入明确失败收口；真实 UI/工具/lease 消费者 | 一个业务 TurnEnded；环境错误留 Prepared | 瞬态 Stop 会在重启复活；整轮 rewind 隐去已执行工具 | 高 |
| keep | 旧格式严格读取；现存历史/地址引用 | 只读旧格式分支，新执行路径唯一 | 直接删 codec 使旧会话不可读；重写地址破坏引用 | 高 |
| defer | 全局连接状态、durable retry queue、费用账本、通用策略 DSL、provider 结果找回平台 | 当前没有必要消费者 | 增加第二套 authority；未来若接远端 hosted tools 则重新评估 | 中高 |

审查中的修正：从 pulse 驱动每次 retry 收敛为 decorator；撤回把 Started 请求摘要迁到 Action
的默认建议；不新增 durable StopRequested；不把环境错误统一写为 PermanentFailure 结束轮次。

可观察的简化量：新路径减少 Started/attempt Failed 两种事件写入，生成恢复阶段由两个合为一个，
删去人工 uncertain-restart 开关与双执行 policy；新增一个有实际消费者的业务结束事件和一个窄失败事实结构。
历史 decoder 分支保留，工具状态数量不减。没有声称减少多少代码行或迁移成本。

仍属产品参数的选择是单次期限、最大退避间隔以及可接受的重复计算费用。本方案已给建议默认值，
可直接据此实施，不把这些参数包装成必须先回答的架构问题。严格跨重启费用上限、Stop 请求接受即
durable、自动认证刷新、远端 hosted-tool 重放属于不同承诺，有实际需求后另行设计。
