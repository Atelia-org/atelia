# Galatea Codex 委派自动恢复重构方案

> **当前格式边界（2026-09-16）**：下文是 2026-09-14 有限恢复方案及其阶段证据。有限检查、诚实“结果不明”回信、失效绑定处理及不自动重发可能已执行任务的规则继续适用；任务全文／wire V5／SQLite V3 的格式描述已由[结构化输入合同](structured-input-rendering-design.md)和[当前运行时](runtime.md)接续，当前为 delegation SQLite V5、wire V6。真实部署及调用结果见[迁移验收](player-character-migration-validation.md)，下文“尚未执行 live”仅指当时阶段。

> 2026-09-14 阶段状态：WP0–WP4 已实施、审阅并通过本地 Debug/Release 验证；当时真实实例部署与 live canary 尚未执行。
>
> 日期：2026-09-14。实施入口：本文；不重新执行旧 `GOAL-codex-delegation-local-resilience.md`。
>
> 设计轮完成后，用户已明确授权带领 subagents 实施并按需提交；不重新执行旧 GOAL。历史分析与验收矩阵仍保留。

## 0. 压缩后的接续摘要

目标：Codex 临时崩溃、线程丢失、历史查询失效时，Galatea 能有限恢复；恢复不了就给角色一封真实的失败回信并释放委派队列，不再让普通故障永久卡住用户。

最小模型：**一封持久任务、一个持久恢复失败计数、一个可替换的线程绑定**。健康时复用原线程；只有失效才换线程。SQLite 继续是唯一的 Galatea 委派状态所有者，sidecar 不增加持久账本。

用户已明确选择：已证明没有发送 `turn/start` 的任务可以自动重试；已可能执行的任务经过有限检查仍不明时，终结为“结果不明”，通知角色、释放队列，不自动重发相同任务。

本阶段不开展之前讨论的 RecapGrid/hash 全面简化，不升级 Codex，不改模型输出上限，不重新设计 SessionJournal，不操作真实账号额度。

## 1. 本次故障和工作区证据

### 1.1 故障链

1. 主模型生成完成，邮件提取产生一封 Codex 任务。
2. `ensure-binding` 成功创建线程并持久绑定。
3. `startBoundTurn` 在第一次 `turn/start` 前无条件 `thread/resume`。
4. 新线程还没有 rollout，resume 返回 `no rollout found`；本次还没有发送 `turn/start`。
5. Node 把错误概括为 `START_OUTCOME_UNKNOWN`，C# 对 StartTurnAsync 的各种异常也统一记 OutcomeUnknown。
6. 后台查询历史又返回 `missing source rollout`，变成 `INSPECTION_UNAVAILABLE`。
7. `RecordPollMiss` 只有退避、没有终结出口；route 的 active dispatch 一直占位，角色无法收到完成或失败回信。

隔离、无模型调用的复现结果：WSL 全局 `codex-cli 0.153.4` 和项目内固定 `0.154.0-alpha.3` 都能创建空线程，但首轮前 resume 都报缺少 rollout。Galatea 使用 delegates 配置中的固定可执行文件，全局升级不会自动改变它。历史目录缺失只证明无法读取，不能独立证明某个旧任务从未执行。

### 1.2 已完成的其他修复，勿混入本任务

- `e8b8a43b`：Anthropic 能力接口 404/405/501 时回退 max_tokens；保留原 adapter identity。Completion 825 pass / 1 skip，Server build 成功；用户确认可运行。
- `7ab556d6`：Galatea route manifest 接受普通 JSON 空白和字段顺序；Hosting 29 pass、Galatea config 51 pass。用户确认可运行。
- 本次委派故障没有要求回退这些修复，也不应重新改它们。

### 1.3 实施前保留的首轮修复

设计时 HEAD 为 `7ab556d6`。以下首轮启动修复现已归并提交 `e913e4c6`：

- `local-codex-mcp/src/codex/backend.ts`：`freshGalateaBindings` 记录本 generation 新建空线程；首次 start 直接发 turn/start；资格使用一次，进程退出/stop 清除。
- `local-codex-mcp/tests/fixtures/fake-app-server.ts`：模拟空线程 resume 缺少 rollout。
- `local-codex-mcp/tests/galatea-staged-backend.test.ts`：首轮不 resume、cold generation 不借用首轮资格、后续原生配置和 cwd 继续生效。
- `local-codex-mcp/README.md`：上述行为说明。

上一轮 `npm test`：113 pass / 2 explicit skip；已生成 dist。该补丁仅解决暖进程首轮；在 ensure-binding 成功后、首轮前重启，仍需要本方案的“已知未执行 → 重建绑定”路径。实施前检查实际 Git diff，不能丢弃此修复，也不能把测试结果冒充新方案的验证。

### 1.4 已完成的局部人工恢复

用户在前端成功回退产生任务的一轮，并停服后，已授权完成：备份该用户 delegation SQLite；删除唯一的 OutcomeUnknown 邮件；对应 action_capture 保留并将 artifact_count 改为 0，避免重新提取旧邮件；route 重置 Unbound；保持 baseline/frontier、其他 captures、SessionJournal 不变。无 reply_notice/lease 被删除。

SQLite integrity / FK、限定行差异及生产 `OpenExistingReadOnly` 严格重开通过。备份位于机器本地 `.atelia/galatea/operator-backups/`；不要把私有任务正文、真实账号、完整状态或私有 ID 写入方案/测试，也不要把这次临时 SQL 当成正式恢复机制。下一轮不必再次清理这个旧任务；重新检查现场再谈 live 操作。

## 2. 需求台账与授权来源

| 编号 | 要求 | 来源与强度 |
|---|---|---|
| U1 | 个人自用、未发布、无下游，优先简单且能持续运行 | 用户及根 AGENTS.md，明确要求 |
| U2 | 普通 OutcomeUnknown/线程找不到不能永久阻塞委派，允许回退、跳过、创建新线程 | 本轮用户明确方向 |
| U3 | 写详细方案，经独立辩证审查，本轮不实施；下轮需能脱离长对话接续 | 本轮用户明确范围 |
| B1 | 健康时同一用户任务继续复用线程，保留 Codex 上下文 | 当前真实行为；本轮未要求每封信换线程 |
| B2 | 已排队任务及最终回信跨重启保存；回信至多一次结算/消费 | 当前 SQLite、reply_notice/lease 消费者，保留 |
| B3 | 不把消息交给另一个用户/错误线程，不放宽 cwd 约束和真正的工具权限 | 当前隔离与真实副作用边界，保留 |
| B4 | 恢复不了时失败回信也能进入正常角色循环；后面的独立任务继续 | U2 的具体产品承诺 |
| U4 | 结果长期不明后告知角色、结束旧任务并继续队列，不自动重跑 | 用户在本轮选项中明确确认 |
| P2 | 任务确实一直运行，但很久没有最终结果，是否强制停止 | 本阶段不新增运行时长硬上限；失败恢复预算不等于执行超时 |
| P3 | 换线程后丢失此前 Codex 对话上下文 | 接受线程失效带来的连续性降级；新任务本体和 cwd 继续传递，不做全历史重建 |

实施遵循 U4。不得臆造远端任务已失败、已停止或没有副作用；这里结束的是本地等待。

### 2.1 覆盖旧文档的具体规则

旧 local resilience work order 已完成，保留为阶段记录。其“同一 dispatch 永远仅一次 start”“不允许 rollover”“历史不可见一直重试”“无恢复期限”不再是本阶段不可变要求。

新的边界：**同一封任务最多一次可能到达 Codex 的启动；明确未发送的失败可以持久重排队；结果不明有终结出口。** 健康线程复用、正式 API 检查、SQLite 唯一结算权、无第二持久账本仍保留。

## 3. 现有生产链路与具体缺口

下表 C# 路径相对 `prototypes/Galatea/`，Node 路径相对 `local-codex-mcp/`；测试集中在 `tests/Galatea.Server.Tests/` 和 `local-codex-mcp/tests/`。

| 层 | 入口 | 本次缺口/改动责任 |
|---|---|---|
| 捕获 | `Mailbox/GalateaOutboundMailExtractionReconciler.cs` | 保留原信及 capture 去重，不通过删除再提取来恢复 |
| 驱动 | `GalateaDurableDelegationDriver.cs` 的 StartQueuedMailAsync / PollActiveMailAsync / RecordPollMiss | 统一 Unknown，所有普通查询失败无限退避，active mail 阻塞后续 |
| 存储 | `GalateaDelegationSqliteStore.Transitions.cs` / `.Snapshot.cs` / `.Schema.cs` | 目前 Started 后没有安全重排队出口；普通 terminal failure 多要求 turnId；缺失未决终结形状 |
| C# transport | `GalateaCodexDurableSidecarClient.cs` / `GalateaSidecarProcess.cs` | ClaimStart 按 dispatchId 永久 tombstone；inspection responseDeadline=null；须能受控释放未发送 claim，超时不能误杀其他用户 |
| Node protocol | `src/galatea/durable-protocol.ts` / `durable-adapter.ts` | failed 帧不说明 turn/start 是否发送；错误归类吞掉执行阶段 |
| Node backend | `src/codex/backend.ts` / `client.ts` | 必须在实际 turn/start request 边界保守标记；不要在外层 catch 用缺文件猜测 |
| 状态显示 | `GalateaDelegationState.cs` / `GalateaDelegationSqliteStore.Snapshot.cs` / `wwwroot/assets/galatea.js` | backoff 不能成为唯一观察结果；终结后显示普通失败回信和空闲/下一任务 |
| 生命周期 | `GalateaDelegationSupervisor.cs` / `GalateaSidecarProcess.cs` | 现有 process generation 负责故障重建；避免为了一个用户杀掉其他用户的健康任务 |

具体事实：`StartQueuedMail` 在调用 sidecar 前提交 Started，operation_id 当前等于 dispatch_id；不能把它解释为远端已发出。现有 `_startTombstones` 按 dispatchId 判重，不修改这一点就无法实现有限安全重试。

## 4. 最小目标模型

### 4.1 保留现有两个核心对象

- `outbound_mail`：逻辑任务与用户可见完成结果。dispatchId 保持稳定，最终 notice 仍按 dispatchId 唯一。
- `route_binding`：该用户当前可复用线程。绑定可以变更，不等于任务本身。

继续使用现有 `dispatchId` 标识逻辑邮件与 Codex `clientUserMessageId`；`requestId` 关联单次 sidecar RPC，mail/route revision 关联数据库 CAS。现有 `operation_id = dispatch_id` 暂保留，不新增 UUID 或 wire identity，也不再引入 fingerprint。

**不增加 attempt 表、start_attempt 或多执行历史。** U4 已禁止重跑可能执行过的旧任务；允许再次启动的前次调用均被证明未发送，因此不存在两个合法远端执行结果需要竞争结算。崩溃后的 Started 仍保守按 Unknown 处理。

### 4.2 两类失败知识

1. `NotDispatched`：当前调用成功持有该任务的启动 claim，并能证明该受控调用链没有调用向 Codex 写入 turn/start 的函数；或 transport 能证明尚未取得/使用本次 claim、start-turn 帧未交给 sidecar，且没有任何已知同任务执行在先。
2. `MayHaveDispatched`：越过上述边界，或者当前进程丢失了足够证据。包括写入后超时、响应丢失、进程崩溃、异常类型不明。

`DUPLICATE_DISPATCH_ID`、`DISPATCH_ALREADY_ACTIVE` 等拒绝可能说明同一任务的另一次调用已在执行：即使这次调用没写，也**不能**宣称逻辑任务未执行、不能释放原调用的 claim。

不新增一个请求/回执握手来追求绝对准确边界。Node 在调用 request("turn/start") **之前**切换为 MayHaveDispatched；它可能把一小部分实际未发送判为不明，但绝不能反过来。resume 的确定性错误发生在边界之前。错误消息只用于细分原因，不能决定可否重发。

### 4.3 决策表

| 当前事实 | 本次动作 | 是否可再次 turn/start |
|---|---|---|
| 暖 generation 新建空线程 | 保留现有首轮修复，直接 start | 是，第一次 |
| 线程缺失/不可恢复，NotDispatched | 持久释放绑定、进入新绑定过程 | 是，有限重试 |
| 临时 sidecar 不可用，NotDispatched | 现有退避后再尝试 | 是，有限重试 |
| INVALID_CWD/配置无效，NotDispatched | 一次明确失败回信，不无限建线程 | 否，等待后续修正/新任务 |
| 返回匹配的 Accepted/Running | 固定 turnId，使用正式 API/live evidence 继续检查 | 否 |
| 返回匹配的 Completed/Failed | 现有 terminal CAS 与 notice 结算 | 否 |
| Unknown + not-found / 历史缺失 / 检查不可用 | 有限检查，到期终结为结果不明 | 否（U4） |
| 明确身份错配/不属于本用户 | 停止使用该 binding，普通任务明确失败；不把错误结果当成功 | 否 |
| SQLite 损坏/本地状态无法验证 | 保持人工恢复入口，不能假装正常清空数据库 | 否 |

## 5. 恢复预算：使等待有终点

本阶段只需要**一个持久连续恢复失败计数**。将现有 `reconcile_attempt_count` 明确定义/重命名为任务级 `recovery_failure_count`，贯穿 Queued/Binding/Started/OutcomeUnknown/Accepted 的失败过程。不要同时新增 start_attempt、recovery_started_at_ms 和另一个累计次数。

初始工程默认：每个恢复过程最多 8 次失败；沿用指数退避，最大 60 秒。第 8 次失败在该次事务中进入本地失败结算（若等待合法 inbox 背压，不再发 provider/inspection）。这不是 8 次模型调用：已可能发送的任务不再 start；预算包括无法启动进程、绑定失败、发送前失败和检查失败。所有重试使用同一预算，不能换线程就清零。

- 成功建立空线程不是有效执行进展，不清零。
- 首次拿到 Accepted turnId 只更新知识，不自动清零检查失败次数。
- 只有同一 app-server generation 中、精确 thread/turn 的 live Running 证据，且当前 metadata `status.type == active`，才清除本轮连续失败 streak。Completed/Failed 直接结算。
- **Persistent `inProgress` 单独不是运行活性证据。** 旧进程退出后历史可以永远停在 inProgress；应返回“运行状态待确认”并消耗有限恢复预算，不调用现有 ConfirmAcceptedMailRunning 清零。
- 健康长任务只要仍有上述活性证据可以持续运行；这里没有模型执行时长上限。一个任务永远 active 却不产出结果的治理不属于本阶段。

每次实际操作必须有上界：ensure/start 保留并核实现有操作总截止；inspection 新增覆盖 metadata、所有 turns/items 页的总截止，初始 45 秒。Node 每一步等待至多为 `min(单 RPC timeout, 剩余操作时间)`，到期结束该请求链，不再继续分页。仅 Promise.race 返回却让旧循环后台继续运行不合格。禁止超时后在后台继续发 turn/start；如果请求已经发出，归类 MayHaveDispatched。

### 5.1 重试调度和时钟回拨

沿用一个任务 `next_retry_at_ms` 作为重开后的调度提示，不再把 route.ensure_* 当第二预算所有者。Binding 当前服务的任务由 FIFO 首信决定；事务校验该 dispatchId 和 mail/route revision，不增加 binding-owner 表。

可执行算法：

1. 每次新的失败事务写入计数、last_code 和 `utcNow + delay(count)`。
2. Driver 第一次观察一个新的持久 retry revision 时，计算 `remaining = clamp(next_retry_at - utcNow, 0, MaxBackoff)`，用进程单调时钟创建一次本地 due。
3. 同一 revision 的后续 pulse 只读该单调 due，不能每个 pulse 重算为“从现在起再等 60 秒”。重启最多重新等待一个 MaxBackoff；持久失败计数保持。
4. Store 的状态转换按 revision/CAS 和预算校验；相应去掉只因墙钟 `now < previous/next_at` 而拒绝 Driver 已到期重试的约束。不要用旧墙钟检查把新调度重新锁死。

有界性条件：进程有机会持续调度、每次调用遵守截止、回信容量能被正常消费时，一个持续故障的恢复过程在有限失败数后结束。停机、数据库损坏、inbox 永久无人消费不在这个承诺内。不承诺严格 5 分钟 SLA，也不增加持久总时长字段。

## 6. 事务与协议调整

### 6.1 自定义 sidecar wire

实施采用 wire V5（原 V4），同步 C#/TS strict parser；不升级固定 Codex 二进制或原生 schema。只给 start failure 增加 `dispatchState: not-dispatched | may-have-dispatched`；保留现有 requestId/dispatchId/threadId 关联。普通协议错误或响应缺失默认不明，不能仅看 error code 猜未执行。

C# 的 dispatch tombstone 继续按 dispatchId。将 claim 的获取/归还做成受控的一次所有权操作：

- 本调用首次 claim 成功，且匹配 requestId 的受控失败为 NotDispatched，才可释放自己的 tombstone。
- 在调用链尚未 claim 时发生的确定性前写失败，没有 tombstone 要释放。
- 重复 claim 拒绝不拥有原 claim；不得执行无条件 `Remove(dispatchId)`。
- 任何写入可能已发生、Accepted、响应丢失、超时、未知异常都保留 tombstone，不许可第二次启动。
- NotDispatched 回执的重复/迟到帧不得释放后来新调用的 claim。

Node activeDispatches 继续只防同任务并发启动；收到 duplicate/active 拒绝不能把它包装成安全重试。新一轮重排队仍使用同一 dispatchId；每次 transport 调用都有新 requestId。

### 6.1.1 截止仅结束当前等待，不等于进程坏了

当前 AwaitAttachedResponseAsync/AwaitDetachedResponseAsync 超时会 FailGeneration；Take<TPending> 对没有 Pending 的响应也会抛错。直接给 inspection 填 responseDeadline 会杀掉共享 sidecar 中其他用户任务。实施必须同时改这两处：

- 普通 ensure/start/inspect 响应截止只移除当前 Pending，返回本次失败/不明；不能据此调用 FailGeneration。
- Node 端按 §5 结束本操作请求链；C# 端端到端截止作为最终等待上界。
- 响应先做结构解析：没有对应 Pending 的合法迟到帧直接忽略并记固定诊断；不新增 retired-requestId 集合或持久迟到账本。
- 有 Pending 的响应仍校验类型、dispatch/thread/known turn/selector。进程真的退出、stdio 部分写入造成帧流不可信、无效 JSON/协议帧等才走现有 generation failure。
- Host 正常 shutdown 保留未决任务，不能当成一次远端失败给角色发信；重启从持久状态继续有限恢复。

### 6.2 安全重试事务

新增一个具名存储操作，例如 `RequeueNotDispatchedMail`，输入 expectedMailRevision、expectedRouteRevision、dispatchId、原因、当前时间。业务调用方必须持有对应受控 NotDispatched 结果，不允许仅凭日志消息调用。

1. 验证当前 Started、dispatch/thread 与预期 revision。
2. 该失败消耗任务级预算；若已耗尽转 §6.3。否则回 Queued，清除这次 requested/accepted handles 和 active_dispatch_id，保留预算/next_retry_at。
3. 仅线程缺失、ownership 丢失或无法继续复用时将 route 改 Unbound；临时 transport 故障不无条件毁弃健康线程。
4. 下次正常 StartQueuedMail 重新提交 Started、绑定当前 route；operation_id 保持现有值，不再增加 attempt 序号。

Binding 也必须有任务级出口：BeginBinding/RecordBindingMiss 接收并校验 FIFO 首信及 mail/route revision；失败更新这封信的同一计数。成功建线程不清空此前失败预算。

崩溃窗：NotDispatched 回执已到、tombstone 已释放但 Store 重排队尚未提交，重启仍看到 Started → MayHaveDispatched。允许保守结束任务，不能从“理论上那时应该未发送”重发。重排队事务已提交后则有合法 Queued，可再次尝试。

### 6.3 无 turnId 的本地终结

新增 `FinishMailLocally`（具体名称可调整）表达本地放弃恢复；不是伪造 Codex 的 Failed。

单个 SQLite 事务完成：

- 校验任务/route 当前身份与 revision。
- 任务 TerminalFailed；terminal code 区分 `NOT_DISPATCHED_RETRIES_EXHAUSTED`、`RESULT_UNCONFIRMED`、明确配置失败等。
- 不要求 AcceptedTurnId；已有真实 turnId 仍保留，不捏造。
- 生成唯一 DeliveryFailure notice、推进 next_completion_sequence。
- 解除 active_dispatch_id 及回信容量 reservation；失效/结果不明的线程退出后续复用，route 改 Unbound。健康线程上的明确业务失败可保留 Bound，不必每次失败都丢失上下文。
- 保留 capture，保持 artifact_count 与邮件行匹配；生产恢复不删除任务或修改 SessionJournal。

现有失败终结会清空 body/evidence。新终结建议仍复用这一做法：原任务正文可从原始 Action/既有捕获证据回查；本阶段不提供原任务按钮式重发。若需要原文重发，应另设计保留字段与数据生命周期，不能顺手把全部 body 永久复制。

幂等：重复执行该事务只能返回同一 notice；不能生成第二封失败信。采用“首个持久终结生效”：如果真实成功已先提交，本地超时终结返回既有成功，不覆盖为失败；如果本地不明终结先提交，迟到成功遵循下段规则。状态转移失败只重新读取，不递归发起新远端调用。

**必须修正已存在的反向隔离路径**：`RecordTerminalMail` 在已终结、再次收到不同 terminal 证据时调用 `QuarantineRouteForTerminalConflict(current.Route)`。对本地 `RESULT_UNCONFIRMED` 等恢复终结，任何后到远端结果都只返回既有结算/记不含正文诊断，不覆盖失败、不发第二封信、不隔离已开始 B 的当前 route。不能只在新增 API 做幂等而漏掉旧入口。现有正常成功后矛盾重复证据的处理另行保留，本次只收窄本地终结分支。

### 6.4 后台线程与仍在运行的旧工作

本地结束等待不等于外部工作已经停止；failure notice 必须诚实表达“结果无法确认，旧工作可能仍在运行”。

本阶段**不增加 durable turn/interrupt 操作**：当前协议没有它；MCP 的 generic interrupt 也不等价于按精确 dispatch/turn 停止。即使尽力调用成功也不能撤销已发生的副作用或保证所有外部子进程停止。不能用另一个可能失败的 RPC 作为本地终结的前提。

采用 U4 的具体语义：FIFO 约束 Galatea 本地任务提交顺序；不明任务本地终结后，B 可以继续并创建新线程。A 的残留外部工作可能与 B 并行访问同一 cwd。**不再承诺不明故障后远端严格串行、绝无并发或已停止。** 这项残余风险明确记录，而不引入容器、worktree 隔离或自动 kill 证明来掩盖它。

不自动向下一线程注入新的动态恢复说明，也不保存“下一次待消费提示”标志。现有失败回信先告诉角色核查现状，角色可在后续委派中说明。预先排队的 B 未必能读到 A 的失败说明；这也是继续队列的实际语义，不能写成已有预检查保证。

定向中止、失效任务专用 cwd 隔离、自动恢复上下文等列为后续增强：只有真实使用证明这类残留执行造成问题时再展开。

## 7. 回信、排队与用户界面

失败信进入现有 `reply_notice → reply_lease → ready-turn`，复用一次消费语义，不新增旁路通知系统。

示例文案：

> Codex 未能确认本次任务结果。后台恢复已结束；此前可能产生部分工作，请先核查现状。后续任务可以继续。

执行前失败则明确“任务尚未开始”；两者不能共用“结果未写入/任务未执行”的文案。

健康状态仍显示排队/执行/回信；backoff 显示有限恢复的当前阶段与剩余次数（可由固定上限与已有 attemptCount 推导，优先不扩展 HTTP DTO），不输出任务正文、凭证、原始 RPC error。迟到消息有固定诊断码。主聊天不因后台普通错误被锁住。

队列保持该用户 FIFO；一个失败任务只能产生一次失败回信。下一封独立任务可推进；不在程序中自动复制失败信为新任务。角色自行再次委派会形成新的逻辑任务，本阶段不做跨任务语义相似度去重或无限递归重试框架。

inbox 满时必须保留失败结果/待结算状态，不丢信；优先利用 active mail 已占的 reservation，原子兑换成 notice。Queued/Binding 的失败若没 reservation，需测试正常背压，不用静默删除绕过容量。

## 8. 迁移与旧状态

### 8.1 格式

实施采用 delegation SQLite V3，并提供 V1/V2 离线升级；复用/重命名 reconcile_attempt_count 为通用恢复计数、统一 next_retry_at，删除或迁移 route.ensure_* 的第二套预算字段，扩展 TerminalFailed 的合法形状。延续已有 `UpgradeExisting` 备份、事务、严格重开流程；不新增运行时双版本状态机。wire V5 与数据库迁移须作为同一部署包验证。

建议旧值映射：

| 旧状态 | 初始化 |
|---|---|
| Queued/Unrouted | 无失败则计数 0、next_retry_at 空；若 route 为 Binding，则把它的 ensure streak/调度归并到 FIFO 首信 |
| Started/OutcomeUnknown/Accepted | 保留真实 operation/thread/turn IDs；保留可识别的现有连续失败 streak；绝不能推断 NotDispatched |
| Terminal/已有 notices/leases | 保留所有终结/回信/消费事实；恢复计数 0、调度为空 |
| Quarantined | 保留原因；按白名单将远端生命周期故障纳入本地终结，身份或数据库损坏不盲目解封 |

V2 的 operation_id 等于 dispatch_id，这个旧值本身仍是合法尝试标识，不必重写历史 IDs。现有 ConfirmAcceptedMailRunning 已会清除失败 streak；按具体 V2 状态映射现值，不笼统视为累计总轮询数。旧超过上限的 streak 可钳到上限，升级后第一次调度正常结算，迁移事务本身不伪造远端结果；不能重启就清零。Binding 无法唯一确定首信时保持明确诊断，不随意归属另一个任务。

元数据中的 capture baseline、physical frontier、原 Action 地址、所有 capture 与 notice 序列必须逐项保持。回退聊天仍不会自动撤回外部任务；不改变这条行为。

### 8.2 部署边界

本阶段实现和测试不授权清空/迁移真实 `.atelia`，不运行真实模型或重发实际邮件。下一轮可完成代码、模拟迁移和部署说明；在真实停服、备份及部署窗口明确后执行 live 操作。此前一次性的旧任务清理已经完成，不能据此默认清理以后所有不明任务。

## 9. 实施工作包与验收矩阵

按竖向链路推进，不先建设通用重试框架。

### WP0：基线与首轮补丁归并

- 重读本文、根 AGENTS.md、当前差异；确认固定 executable/version 和 schema。
- 保留 §1.3 四个 dirty 文件；把首轮暖线程与重启后的区别写入测试。
- 不必因为本次改变自定义 wire 就重新生成/升级所有 Codex 原生 schema。

### WP1：发送阶段贯穿 Node → wire → C# → Store

- Typed NotDispatched 只来自实际 start 边界前的受控失败。
- 复用 requestId/dispatchId/revision；ClaimStart 受控释放，仅释放本请求拥有且证明未发送的 claim；不新增 attempt identity。
- 暖空线程直接 start；cold 空线程 resume 失败时明确未发送，并能重建绑定再启动。
- 任意发出后的异常、丢回应、重启后 Started 都维持 MayHaveDispatched；duplicate/active 拒绝不能释放原调用占位。

### WP2：有限恢复与终结释放队列

- 实现安全重排队和本地终结事务、预算持久化、有限端到端 inspection。
- 普通远端故障退出 route-wide quarantine；本地 corruption 保持可见人工处理。
- 真正 Running 不被固定执行时长打断；失效历史不能无限续命。
- 处理迟到 RPC、终结 CAS 竞争、inbox reservation 兑换。

### WP3：迁移与闭环验证

- V2 fixture → V3 → production strict reopen，旧 terminal/notice/lease、baseline 全部不变。
- 两封信场景：A 故障耗尽 → 恰一封失败回信 → B 新线程完成 → 两封回信各消费一次。
- cold restart、错误线程与无 turnId 终结均走生产 Store/Driver/transport spine。

### WP4：文档与部署交接

- 更新 runtime、server-api、configuration（仅有真实配置变更时）、verification、sidecar README、旧设计/工作单的后继链接。
- 写一份短运行说明：如何识别恢复中/失败已结算/数据库需人工处理；禁止让“删除数据库”成为标准流程。
- 报告自动测试与 live 证据的界限，不把 fake app-server 成功当成真实任务成功。

| 场景 | 必须断言 |
|---|---|
| 新建空线程 | 首轮零 resume、一次 turn/start |
| 绑定后、Started 提交前重启，尚无 rollout | 此时仍为 Queued；resume 的受控未发送失败后重建，最终仅一次可能到达的远端执行 |
| resume 返回不存在 | 稳定 NotDispatched；非 Unknown；有限创建线程 |
| turn/start 已写但响应丢失 | 不二次 start；先查原任务 |
| Completed 早于 accepted response | 成功结算优先，不被迟到 Running 降级 |
| Unknown 一直 not-found / inspection error | 有限结束、唯一诚实失败信、active slot 释放 |
| Accepted 的历史投影落后 | live 证据可结算；无证据则有限不明终结，不当作从未执行 |
| 连续多页每页接近 RPC timeout | 端到端检查仍有上界，不无限等待 |
| 时钟回拨、错误码变换、重启 | 不无限刷新计数；同 retry revision 的单调 due 不每次 pulse 重新后移 |
| 本地终结前/事务中/commit 后崩溃 | 回信数 0 或 1，无半释放、重复 notice 或第二 start |
| 真实成功与本地耗尽结算竞争 | 首个持久 terminal 保持唯一；失败不能覆盖先提交的成功，晚成功不覆盖本地终结 |
| 旧 requestId 的重复 NotDispatched / 迟到成功 | 不释放新调用的 claim、不污染当前任务、不产生第二封信或隔离 B 的 route |
| 用户 A 出错、用户 B 正在执行 | A 的恢复不无条件杀掉共享 sidecar 中 B 的任务 |
| 旧工作仍可能运行 | 失败说明不谎称已停止；不自动重跑原任务 |
| inbox 已满 | 保留终结事实并背压，不丢任务/回信 |
| 错误 body/turn/user 身份 | 不能采纳外来成功结果；错误不能变成无限普通重试 |
| V2 旧不明任务迁移 | 不凭空添加未发送证明；保留并限制失败 streak，重启不清零 |

建议验证命令（串行）：

```bash
cd /repos/focus/atelia/local-codex-mcp
unset CODEX_BRIDGE_RUN_LIVE CODEX_BRIDGE_RUN_GALATEA_HOME_LIVE ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE ATELIA_RUN_GALATEA_NOTE_LIVE ATELIA_RUN_GALATEA_LAB_LIVE
npm run check
npm test
cd /repos/focus/atelia
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~GalateaDurable|FullyQualifiedName~GalateaDelegation'
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'Category!=Live&Category!=GalateaNoteLive&Category!=GalateaLabLive'
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj -c Release --no-restore -m:1 -nr:false --filter 'Category!=Live&Category!=GalateaNoteLive&Category!=GalateaLabLive'
python3 scripts/check_session_journal_docs.py
git diff --check
```

实施前核对实际 live trait/环境门控；上述 filter 不构成运行真实 provider 的授权。无需每个工作包反复全套；聚焦验证通过后，在最终集成时跑一次完整所需集合。

## 10. dialectical-simplification 审查与最终裁决

审查过程：先落盘完整草案和 §2 需求台账；三名独立 reviewer 分别从 Demand skeptic、Minimal architect、Semantic defender 阅读全文并核对生产代码/测试；再交换最强对立 trace 交叉质询。最后只对“是否必须新增 attempt identity”进行一次定向裁决。没有按票数决定，也没有把旧测试当成新用户要求。

### 10.1 不可删的核心

1. SQLite 保存任务、失败次数和最终回信；不能只把计数放内存导致重启永远回到第零次。
2. NotDispatched 需要实际调用阶段和 claim 所有权证明；不能由文件不存在或 duplicate 拒绝推断。
3. task terminal、唯一 notice、active slot 释放必须在同一事务；否则断电可能造成丢信或假释放。
4. 当前 Pending 的身份关联、Store revision CAS 继续保护正确的任务/线程，不增加新的内容 hash。
5. 用户明确选择“告知结果不明并继续”，程序不得静默丢任务、自动重发可能执行过的任务，或伪称外部工作已停止。

### 10.2 结论表

| 位置/原提案 | 需求来源与当前消费者 | 成本、最小替代与裁决 | 移除后的具体故障/保留边界 | 置信度 |
|---|---|---|---|---|
| §4.1/§6.1 新 attempt 表、start_attempt、UUID operationId | 草案工程提案；当前消费者只需 dispatch tombstone、Pending requestId、Store revision | **delete/defer**；保留现有三个职责明确的标识，不新增身份 | 只有未来允许 Unknown 自动重跑才出现多远端结果竞争；U4 明确不做 | 高，经第三次定向质询收敛 |
| 永久 dispatch tombstone | 原 at-most-one-start 策略；C# ClaimStart | **simplify**；NotDispatched 时仅由原 Pending 一次性释放 | 第二个重复请求若能 Remove 原 claim，会导致双执行；因此不能只按 dispatchId 裸删除 | 高 |
| §5 三套预算/总时间 SLA | U2 要求有限结束；5min/3次是草案自行提出 | **merge** 为单 mail streak + 操作截止 + 有界退避 | 只给 poll 加预算仍会在 Binding 无限循环；时钟回拨必须按 §5.1 处理 | 高 |
| 任意 Running 都续命 | 当前 persistent classifier、Driver RecordRunning | **simplify** 为精确 live+active 联合证据；旧 inProgress 进入待确认 | 崩溃前最后状态 inProgress 可永远留在历史，每次清零会造成无限占位 | 高 |
| 响应超时=FailGeneration | C# AwaitAttached/DetachedResponseAsync；共享 sidecar 的真实多用户消费者 | **simplify** 为单 Pending 截止；格式合法但无 Pending 的响应忽略 | A 查询超时杀 B 的任务会制造新的 Unknown；迟到 A 帧不能再次杀全 generation | 高 |
| 本地 terminal 后保留旧 conflict 分支 | Transitions.RecordTerminalMail / QuarantineRouteForTerminalConflict | **simplify** 本地终结分支；不全局关闭活动任务冲突检查 | A 不明终结→B 启动→A late success，会隔离 B；需显式测试 route 不变 | 高 |
| §6.4 新 turn/interrupt 协议 | 草案新增建议，当前 durable wire 无消费者 | **defer**；U4 只结束本地等待，失败信说明残留风险 | 旧工作可能仍执行；best-effort interrupt 也不能保证外部子进程停止，不能变成新终结门槛 | 高；残余风险已写入 §6.4 |
| 下一线程动态恢复说明/消费标志 | 草案建议；现有失败回信已向角色传递事实 | **delete/defer**；不新增跨任务提示状态，不改 B 的业务正文 | 预先排队 B 未必知晓 A 的失败；这是明确的继续队列语义，不伪造隔离保证 | 中高 |
| 新 reservation 对象/第二终结日志 | 无新增需求；现有 active_dispatch_id 与 reply_notice 已承担职责 | **keep** 事务语义，**delete** 新对象 | 单独先释放再写回信，会被其他 notice 抢占容量或断电留下半状态 | 高 |

### 10.3 对立反例和撤回记录

- 初稿认为需要新的 operationId 贯穿 wire。反对方提出：U4 下同任务最多一次可能执行，现有 requestId+revision 足够。语义方给出最强迟到 trace：request1 未发送失败→释放→重排队→request2 claim→request1 重复失败晚到。裁定：只能由当前匹配 Pending 拥有者释放一次 claim，旧帧没有 Pending 不产生状态修改，即可解决，不需新 ID。初稿撤回新 ID/attempt 字段。
- 初稿认为“尽力 interrupt”能帮助释放队列。反对方核实当前 Galatea transport 没有精确停止接口，且成功回包也不能撤销既有副作用。裁定：不承诺外部停止，本地终结与 failure notice 已满足 U4；新增中止机制延期。
- 初稿提出 3 次启动、8 次检查、5 分钟总时长。各方核对后只找到一个当前需求：持续故障必须结束。裁定：统一 8 次连续失败，逐操作有界；删除重复时钟/次数；明确调度机会和 inbox 消费前提。
- 各方独立发现 `RecordTerminalMail` 的 late terminal quarantine 和响应 timeout 的 shared-generation kill。它们是当前可执行 trace，不是未来想象；已提升为强制实施/验收项。

### 10.4 剩余产品边界（不阻塞本阶段）

- U4 已有明确用户答复，不再向下一轮用户重复索要这个选择。
- 本地失败后继续下一任务，接受未决旧外部工作可能仍运行；本阶段不提供远端严格串行保证。
- 健康任务无限 active 的治理、人工按键中止、自动重建完整线程上下文、失败任务一键重发均延期。
- 固定失败上限/退避值是实现默认，可根据实际体验调整，不是需用户逐个批准的架构决策。

概念减少（相对本文初稿，可直接数）：不新增 attempt 表；去掉 start_attempt 与 recovery_started_at 两个候选字段；去掉新增 UUID operationId 及对应 wire 字段；取消恢复提示消费状态、停止协议与 retired-requestId 缓存提案。保留一个任务恢复计数和两个具名存储操作，不演变为通用工作流框架。

## 11. 原实施提示（历史入口）

以本文最终版和本轮用户答复为目标，先验证现有状态再改代码。不要运行旧 GOAL 文本，也不要恢复其中的“无限只读检查”约束。不要将 RecapGrid/hash 清理、主模型 retry、全面 Codex 升级或通用任务编排系统混入。

首先保证“Queued 任务冷启动空线程无法 resume → 确定未发送 → 自动重建 → 同一任务完成”的最小生产竖切片；随后保证“无法确认 → 有限终结 → 失败回信 → 下一任务继续”。任务只有当两条链及迁移/重启/重复结果测试都闭合时才算完成。

可直接作为下一轮开场任务：

> 请按 `docs/Galatea/codex-delegation-recovery-refactor-plan.md` 最终方案实施。先核查并保留首轮启动的四个未提交文件，沿 WP0–WP4 完成代码、迁移测试、文档和本地验证。用户已明确选择：可能执行但有限恢复仍不明时，告知角色、结束旧任务、继续队列，不自动重发。复用 dispatchId/requestId/revision；不新增 attempt 表或 UUID，不引入总时长持久字段。重点验证 claim 所有权、迟到结果不隔离新 route、普通超时不杀共享进程、时钟回拨不无限退避、唯一失败回信和后续任务推进。不要执行旧 GOAL；不要混入 hash/RecapGrid 重构，不调用真实 provider，不改写私有 live 状态。以实际工具结果报告完成范围，说明尚需单独执行的部署验证。


## 12. 实施记录（2026-09-14）

本轮采用 `two-layer-refactor-driving`：协议、Store、Driver 与跨层测试分包；独立 reviewer 检查跨包边界，发现直接压回本包修复。真实 provider 与用户状态不在本次本地验证内。

具体设计收口：

- 不扩张 inspection wire：历史仍可返回 `running/source=persistent`；Driver 消耗恢复预算。只有 live Running 才清零。
- 本地终结保留已发生的恢复失败次数与最后诊断码，清除 retry 时间；普通远端 terminal 保持原有清理规则。notice 的 terminal code 是最终结果说明。
- SQLite V1/V2 仅由离线 upgrader 处理：先验证旧格式，在内存副本执行同一迁移并严格验证预期 V3，再备份源库、事务迁移与严格重开。运行时只有 V3 一条路径。
- 回信、active slot 和路由释放仍同事务；没有新增 attempt 表、operation UUID、总时长字段或额外恢复账本。

主要提交：`e913e4c6` 首轮空线程；`5d822063` 批准方案；`c5f5ad42` Node V5/阶段证明/总截止；`29563546` C# transport；`d2037b36` / `c124ba1e` Driver；`2ce1cbbc` SQLite V3/恢复事务；`ba9ae8de` 迁移测试；`6af424b0` / `e79551a7` 跨层与事务测试；`fc9b1b93` 消除回信测试的后台写入竞态。前端和取消检查另有小幅提交。最终集成验证见[验证记录](codex-delegation-verification.md)。


完成结论：WP0 首轮修复已保留；WP1 V5 发送证明与 claim 已贯穿；WP2 统一预算、有限恢复和本地终结已实施；WP3 V1/V2 迁移及两封邮件闭环已验证；WP4 runtime/API/sidecar/升级说明已同步。Debug 与 Release 各 971 pass / 1 live skip，Node 116 pass / 2 live skip，浏览器 17 pass，Store/migration 定向 82 pass，文档 31 files / 0 diagnostics。

最终 code review 无未决 finding。部署剩余事项仅为明确停服窗口后的真实库离线升级与按需 live 验证；本次未操作真实用户库、未重发真实任务、未启动真实 provider。已确认的产品边界继续见 §6.4 与 §10.4。
