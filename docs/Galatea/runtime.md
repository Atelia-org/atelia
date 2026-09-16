# Galatea 运行时机制

本文说明 Galatea Server 的运行时职责、持久化边界与关键协议；常规启动、浏览器操作和简要日志命令由 [Galatea 文档索引](README.md) 承接。HTTP 语法见 [server-api.md](server-api.md)，连接与本地配置见 [configuration.md](configuration.md)。本文描述当前代码合同；自动重试的实施与尚未完成的验收见[实施记录](completion-auto-retry-implementation.md)，不能据此认定真实角色已经升级。既有身份迁移证据见[迁移验收](player-character-migration-validation.md)，Codex 专项证据见 [Codex delegation verification](codex-delegation-verification.md)。

## 模型切换与 reasoning 回放排障

2026-09-09 的 `gpt-5.6-sol -> gpt-6-astra` 故障在构造 Responses request 时发生：旧模型的 native reasoning 被直接
交给 exact-Origin replay validator，HTTP 尚未发送便抛异常；由于 journal 已记录 `CompletionAttemptStarted`，旧版
最终表现为需要显式恢复的 uncertain 状态。它不是 RecapGrid 损坏的证据，也不能用清空历史来修。

当前 Responses adapter 对相同 ProviderId/ApiSpecId 的 native reasoning 跨模型原样回放，仅省略其他 provider/profile
的 reasoning；可见正文、工具记录、原始 Origin 与历史不变。native payload 仍严格校验；已知本地拒绝使用
`CompletionRequestRejectedException`。本地配置、投影或协议拒绝保留原 Prepared，由 host 标记 process-local blocked，不能伪装为成功 Action 或不断创建新输入。
Codex 有跨模型 live 实验依据；公共 Responses 的相同行为为 operator 授权假设。适配器合同见
[独立 Completion 仓库与当前依赖入口](../completion-dependency.md)。

**2026-09-14 恢复语义更新：** 当前代码删除了手工 `RequestAdapterFingerprint` 门槛。
旧 Prepared v7 的 adapter 字段只在格式读取时校验并丢弃，v7/v8 使用同一当前 adapter 重构与调用。
已冻结的模型、prompt、history、tools 及逻辑请求 commitment 仍保留；connection、client/API、原生载荷和工具权限检查继续有效。
旧 Started 仍按原 schema 验证，但纯生成恢复不再要求页面授权重发；恢复同一冻结任务不能据此更换连接或模型。

新 Prepared 写 v9 语义计划，Observation/Setup 写 v2；新调用不再写 `CompletionAttemptStarted` 或逐次 `CompletionAttemptFailed`。完整、校验通过的结果才提交 Action，旧 Started/Failed 的读取验证保留。旧 v7/v8 保持 exact 恢复，v5 保持历史审计可读、completion 不可执行。部署前正常停服并保留完整数据快照，
回退旧程序时需要匹配的数据快照，不能只换回二进制；恢复旧快照不保留升级后新增轮次。
详见 [Prepared v9 与旧版本矩阵](../SessionJournal/current/contracts/completion-request-prepared-v9.md)。
若其余绑定检查失败，应先核查实际连接/载荷差异，不要连续点击恢复或修改冻结身份。
结束 pending 任务使用安全边界上的 `TurnEnded`，不回退整个输入；旧 Failed 尾保持 blocked，只允许显式结束，不自动生成。Undo 是独立的 selected-lineage 操作，不是 pending 迁移接口。
不要修改原始 manifest、reasoning Origin 或把同一个 connectionId 临时改绑另一个模型。
若 operator 明确选择舍弃未完成 turn 的 selected suffix，可以停服、备份后使用
[CLI `rewind-branch`](../../prototypes/SessionJournal.Cli/README.md#离线-branch-回退)：先在副本验证目标为 Idle，
再用 exact Ref/head/target 确认一次 ref 移动。这不是恢复或迁移 frozen request，不删除 raw events，
也不能撤销已发生的 provider/tool、delegation 或 CharacterMemory 副作用；不能据此自动重发旧输入。
没有 pending completion 的旧历史不需要迁移，合法 v2 native reasoning 可直接被新投影读取。
纯生成即使远端可能计算过也可重算，可能重复计费；这不授予工具或 Codex delegation 的未知副作用重放权限。

日志排查先看 `Galatea.TurnRunner` 的 exception stack 与 `callLogDir` 中同次调用的 `exceptionType` / `elapsedMs`，
不要把完整 prompt、reasoning payload 或凭据复制到 issue。`Provider` Debug 日志只记录跨 provider/profile 的省略计数，不记录内容。

### 纯生成重试与业务结束

Galatea 的 `GalateaCompletionRetryClient` 只包围 Completion 调用，不包围 Journal 提交、整个 RunTurn 或工具执行。它复用同一逻辑 request/client；重试不重新接受 Observation、normalize、选择 recall 或切换模型。结构化 Transport、HTTP 408/429/500/502/503/504 及已知临时 provider code 可重试；未知 code、认证、永久配额、配置和协议错误阻断，不能从异常文字猜测可重试性。退避从 5 秒指数增长，基础值上限 300 秒，附加 0–20% 正 jitter；Retry-After 是可超过该上限的下界，不以次数耗尽结束任务。

每次调用默认有 30 分钟总期限，可按连接配置；这不是 token 间隔超时，也不证明远端停止计算。期限或 Stop 取消底层调用后，必须等待调用退出和资源清理，才能下一次尝试；忽略取消的 client 继续占有任务，不并发补发。attempt、等待与失败预览是瞬态状态，重试重置本次预览，不删除之前已提交的 Action/工具显示。环境 blocked 留下 Prepared，普通 pulse 不反复探测；显式恢复或服务重启可以重新评估。

用户 Stop、明确 refusal/content filter 或完整 terminal 的输出上限 Incomplete，在安全边界分别提交 `TurnEnded(Stopped/Rejected/Incomplete)`。recent 区分成功 Action 与非成功结束，不伪造 Assistant。已接受输入和已提交工具事实保留；工具批次未完全闭合时不能追加 TurnEnded，用户 Stop 不取消已提交工具执行。Stop 请求接受不等于持久结束完成；shutdown 则保留 pending 供启动恢复。无活动 live turn 的 pending 结束入口取得 TurnLock 并验证 expectedHead，不调用 provider。

Action/TurnEnded 的提交发布结果不确定时必须停用当前 writer、重开核实 selected lineage，不能按网络故障重算或直接补结束。完整结果已提交后才执行工具；工具与 delegation 的未知结果仍遵守各自恢复合同。

## Completion、提取器与后处理

`GalateaCompletionOwner` 是 host-wide `CompletionConnectionRegistry` 的唯一 owner。主 Agent、input normalizer、每 Character 的 outbound-mail extractor 和 RecapGrid 路由都借用同一套惰性 client；extractor 的按角色构造不等于另建 provider client。关闭时先 drain session 和 delegation，再由 owner 清理借用的 RecapGrid runtime 与 distinct Completion client。启用 `runtime.callLogDir` 时，共用 decorator 只记录 Completion metadata：连接/模型、摘要与长度（可计算时）、数量、耗时、结果及异常类型；不保存 normalizer、主请求或辅助请求的输入/输出全文，也不记录异常消息。诊断摘要不是 durable 派发证据。

`TextExtractor` 是 internal、ephemeral 的结构化提取器：构造时冻结 system prompt、不可变 `TextExtractorToolSet`、connection 与借用 client accessor；调用只提供 `targetText` 和 `userPrompt`。它不拥有 HTTP、SessionJournal、持久化或 client dispose。一次 logical extraction 会建立独立 `ToolSession`/collector，故同一实例可并发使用而不串线，也没有 durable dedupe/recovery 语义。

TextExtractor 本身不再拥有 Codex 专属五次重试循环。input normalizer、outbound-mail extractor、Character Note extractor 与 Memo recall 的纯生成 client 统一借用上述 retry decorator；各 feature 仍负责自己的输入、验证和领域结算，重试不包围 capture/apply。每次 attempt 只发一个 Completion，请求使用 `Auto` 与 parallel tool calls；artifact tool call 是终态输出，不进入 tool-result 或 repair loop。0 个 tool call 表示没有产物，普通文本只是受限 diagnostics。未知、重复或畸形 call、schema/DataAnnotations/custom validation、invocation/termination/error 不匹配都会使整个 extraction 失败，不返回 partial result。工具名、arguments、call 数量和 UTF-8 文本均有代码边界；`openai-codex-responses` 工具名还受其 ASCII 命名限制。connection 未向调用者暴露业务 output cap；adapter 只在 provider 已报告的模型上限适用时传递该字段。

artifact 包装与 Observation 的协作模式、Mailbox 和 Character Note 的接点见 [TextExtractor / Observation Bridge](text-extractor-observation-bridge.md)。Character Note 的现行保存合同见 [Default MemoPod V1](character-note-default-memopod-v1.md)；[自动记忆工作单](automatic-memory-work-order.md)记录回执、DerivedInfo background pump 与 Memo recall 闭环的已批准实施范围，当前行为以对应源码和测试复核。

主 Completion 成功后，runtime 从 SessionJournal 的 frozen terminal Action 取得原始证据，再用 `GalateaVisibleActionTextRenderer` 顺序连接 Text block、排除 reasoning/tool block、整体剥离 inline think。Mail 与 Character Memory 对同一 target 并行 `ReconcileTargetAsync`，并总会 drain。仅 Character Note 的明确 pre-capture timeout、`TextExtractionException` 和 Pod unavailable 属于 best-effort 后处理失败；capture 后的结果保持 pending 或 quarantine，不能伪造回执。Quarantined/invariant 和 caller cancellation 仍按失败仲裁处理，已 durable 的主 Action 不回滚。

Mail/Note extraction 不另设整个 logical operation 的短 deadline；单次 provider deadline 与退避由 decorator 负责，调用者取消仍终止等待。provider 忽略取消时，recent refresh、SSE terminal 和 `TurnLock` 继续等待清理，不并行补发。Mail failure/cancellation 不写 empty tombstone，下一次 admission 在新 turn 前重试该 exact gap；已 durable 的主 Action 保留。一次 logical extraction 的重试复用 request/client，artifact tool 只在最终成功响应后执行；远程重试仍可能重复消耗 provider 算力。capture commit 只 signal supervisor，主 Galatea turn 不等待 Codex accepted/final。

session attach 是 provider-free：只做本地打开、durable delivery proof 与已 Prepared 的领域结算，不在尚未发布的 attach task 内无限等待提取器。需要 provider 的旧 gap 由后续 pulse/显式 admission 在 TurnLock 下处理；独立的 admission scope 可取消，只保留原持久目标，不创建新 Observation，也不把整理取消写成旧业务轮次的 TurnEnded。主轮 Stop 与 admission Stop 是不同作用域；关闭时先取消并 drain 已登记 scope。DerivedInfo 的独立 signal/pump 合同不因此变成无限自主重试，见下节。

## Observation 与回合入口

新主线通过 `GalateaObservationContent` 保存 `galatea.observation.v1` 或 `galatea.observation.v2` 的机读 JSON，外层由 SessionJournal
`SessionInputContent.Structured` 明确标记。`GalateaInputProjector` 在请求时用 md-json 投影；正文的空白、换行、
Unicode 和 Markdown 不被当成 runtime 元数据解析。存储、查询、Undo、exact append proof 不读取生成的 fence。

| kind | 持久事实与入口语义 |
|:--|:--|
| `player-action` | 已认证 Player 的 id/name 快照、normalized action.text；表示尝试行动，结果仍由角色世界规则决定。 |
| `heartbeat-activation` | Runtime 来源、采样时间、Character 与周期事实；独立于 Player。 |
| `delegate-reply` | Runtime 触发；各条回信或失败通知保留自己的来源与 dispatch/thread/turn/notice 关联。 |
| `inbound-mail` | 内部信记录核实的 Character sender；HTTP 注入记录已认证 Player 与信内自称 From，二者不混同。 |

notices 与 recalls 各自保留来源；不能把整个 composite 当成 Player 的话。Note receipt 最多一条且在 notices
末尾；Heartbeat 不带外界 Reply/DeliveryFailure，DelegateReply 必须有外界 notice。业务时间在形成 Observation
时采样，不能因重渲染变成当前时间。具体字段、数量、UTF-8 边界和闭合 schema 由
[`GalateaObservationContent`](../../prototypes/Galatea/GalateaObservationContent.cs) 验证。

### Canonical grammar 与兼容读取

新写入的 canonical 内容是机读 JSON，不再是旧 `PlayerTurnObservationEnvelope` 的 heading/info string/fence。
旧 text Observation 继续经有限的历史 envelope/classifier 读取；无法确定的来源不补造。已 Bound 的旧格式
按原 exact bytes 证明，不把新 projector 用作旧 canonical verifier。旧 Pending notice 可以以显式 legacy 内容
进入新的 structured Observation，来源格式与新绑定格式相互独立。

recent 从 typed 字段生成可读视图；只有 PlayerAction 的原动作可恢复到 composer，自动触发和 inbound 不
冒充玩家动作。普通入口在 per-character `TurnLock` 内先结算 reply lease/extraction gap，再完成 normalization。
normalizer 只接收玩家动作原文，不接收 Ready notice；一般异常仍沿现有 fail-open 政策，取消、fatal、显式
`GalateaTurnException` 与输入超限阻止新轮次。待恢复 Prepared 或旧 Failed 不能被新输入覆盖；先恢复原任务或显式结束，再接收新轮次，不自动 abandon 旧输入。

SQLite CutoffFrozen lease 原子选择已 Ready 的 bounded FIFO 前缀；后来 Ready 的结果留给下一轮。完成 desired
setup、确定 recall/receipt 内容后，用 exact selected base 和同一份 structured Observation 绑定，再 append。
容量按机读内容检查；实际请求在渲染后检查发送限额。换布局不能删字段、重选 notice 或更改已绑定来源。
inbound/recovery 不领取新 cutoff，也不重新 normalize 或 fresh recall。

### Memo recall

per-session `IGalateaPlayerTurnRecallProvider` 在 CharacterMemory lazy attach 后构造。`galatea.memo-recall=null` 或 maintenance mode 使用 disabled singleton，在 context/barrier 建立前直接绕过，因此 disabled 路径没有 selector I/O；仅将 binding 设 null 不影响独立的 save-receipt 投递。enabled recall 服务三种 fresh trigger（包括带 reply lease 的 PlayerAction），inbound/recovery 不做 fresh recall。

它把已采样 timestamp、尚无 recall 的 typed Observation、本轮 Reply/DeliveryFailure 与同一 pre-append raw window 最近一条非空 visible Action 组装为 `atelia.galatea.memo-recall-context.v4` JSON query，作为本次辅助调用的瞬态输入，不落盘，也不再调用 query-builder 模型。`player-action` 带完整 playerText；heartbeat 带当轮持久的 characterName 与 `externalIntervalMinutes` snapshot；`delegate-reply` 以 externalNotices 提供结果。v1 heartbeat 只接受并保持 `10`，新 v2 heartbeat 接受并保持 `1..525_600`；读取历史绝不从当前 config 重算。query 的稳定 inputMeaning 解释时间、分块来源及旧记录边界。最近 Action 保留原 sourceStartInclusive/sourceEndInclusive，明确是 GM 可见叙述，不冒充角色发言。optional notice 按 whole-item prefix、Action 按 whole item 纳入 512 KiB query budget，`NoteSaveReceipt` 不进入 query。Default MemoPod 在短 `_podMutationGate` 内按 settled identity 打开 Frozen handle，provider await 在 gate 外。

selector 最多返回 8 个 ordered ID；runtime 以 Title eligibility、`CharacterNoteOriginBarrier`、`RecallBarrier` 和 1 MiB final Observation budget 选择第一条 `MemoExactText`，最终注入 `0..1` 条。SourceId 是 `memo-pod:v1/<32-lowerhex PodId>/<canonical MemoId>`，保存当时 title、exactText 和 Pod 状态版本，投影时再呈现，正文不截断。空 selector 或全部候选过滤是正常 underfill；configured provider/authority failure 则 fail closed，阻止 main Completion。receipt 先占预算，recall 只使用剩余空间；最终 structured Observation 在 selector 后、`SendAsync` 前绑定到 reply lease 与 receipt outbox。恢复复用已选机读内容，不重新 recall、领取或采样时间。MemoPod Open/Freeze 冻结并验证机读 document；Recall 前才惰性渲染缓存，await 前后仍核对同一冻结内容周期，缓存对象不承担周期身份。

## Mailbox、Codex delegation 与恢复

outbound extractor 从可见 Action 产出有序 `SendMailIntent`（Recipient、可选 Subject、Body、可选 `InReplyToMessageId` 与 `EvidenceQuote`）。extractor LLM 依可见 Action 判断角色是否确实尝试发信，以及正文和引用的语义；runtime 校验结构、UTF-8、收件目标，并用当前 Character 的受信 id/name 快照捕获 sender，不接受模型覆盖发送者身份。大小写精确的 `Codex` 仍走专属 sidecar route；命中 config-owned character directory 的非 self 名字则在同一 capture transaction 建立 sender-side `internal_mail_outbox`，由 host-local relay 投递到目标现有 `InboundMail` turn。未知/self recipient 保持 `Unrouted`，不产生失败回信。角色信以目标 Journal exact inbound Observation 的 durable append 为 Delivered，不承诺 Completion、回执或阅读；Bound evidence 必须在所有目标 writer/admission/结束/rewind 前先 proof，`NotAppended` 才回 Pending，`InProgress|Terminal|Terminated` 的精确 Observation 证明均可 Delivered，未知/冲突均 fail closed。完整故障与身份漂移边界见[角色间站内信设计](character-mail-design.md)。`EvidenceQuote` 是 provenance，不是 runtime authority；capture 保存正文和当时 sender 快照；sidecar task 在 Start 前瞬态生成，route capability 只来自 code-owned route。

真正 Start 前先投影 task 并检查完整 UTF-8 请求限额，然后在同一 Started/route claim transaction 保存摘要与长度，
确认提交后才调用 transport。Inspect 使用已记录 task 承诺及 dispatch/thread/turn 身份，不重渲染旧任务求值；
提交不确定或结果未知不能因换 renderer 自动重发。投影失败保留未发送的 Queued 内容。

delegation 采用 SQLite-backed durable owner。transport 是 strict bounded JSONL V6：`ensure-binding`、`start-turn`、`inspect-dispatch` 分别对应 binding、接受 turn 与 `not-found|unavailable|running|completed|failed|ambiguous` inspection；inspection 标示 `source=live|persistent`。`ensure-binding` 只建立并核验固定 owned thread，不能携带邮件正文或开始 turn。`Accepted` inspection 必须带 durable exact `turnId`；`OutcomeUnknown` 只能传 null，按 dispatch 发现。wire、thread ownership、环境清洗、工具能力和 durable law 的完整约束见 [delegation durability design](codex-delegation-durability-design.md) 与 [当前重构状态](codex-delegation-refactor-status.md)。

`Queued -> Started`、冻结 operation/thread 和占用 active dispatch 在一个 SQLite transaction 中完成，commit 后才允许 `start-turn`。启动失败带受控 `dispatchState`：只有本次持有 claim 且确证未调用 `turn/start` 时，才可重排队；失效线程随之解除绑定。发出后丢回应、进程崩溃或冷启动遗留 Started 均按 `OutcomeUnknown` 检查，不自动重发原任务。重复请求拒绝不能作为未发送证明。

绑定、发送前失败和结果检查共用邮件上的 `recovery_failure_count`，最多连续失败 8 次，指数退避最多 60 秒。成功绑定或 Accepted 不清零；仅同 generation 的精确 live Running 且当前 metadata active 才清零。历史 `inProgress` 只表示旧状态，不能无限延长恢复。inspection 总截止 45 秒；单请求超时不关闭其他角色共享的 sidecar。retry 时间在每个持久 revision 首次观察时映射到单调时钟，重启最多重新等待 60 秒，不重置失败次数。

live 身份由同 generation 的 `turn/start` 请求与关联响应建立，不要求启动投影包含 userMessage；后续 user item 验证一致性，terminal/final 通知推进结果。进程结束后不会从历史重建 live 身份。空投影导致的误恢复诊断与简化依据见[空启动投影与 live 观察](codex-delegation-live-observation.md)。

耗尽后，同一 SQLite 事务终结本地等待、写入唯一 DeliveryFailure notice 并释放 active slot。`RESULT_UNCONFIRMED` 明确告诉角色结果未确认、旧工作可能仍在运行；`NOT_DISPATCHED_RETRIES_EXHAUSTED` 表示确认未发送。下一封邮件可以创建新线程继续；本地 terminal 不被迟到结果覆盖，该旧任务也不能隔离新任务的 route；普通远端矛盾证据仍按原规则处理。inbox 满时保留状态并等待容量，不继续外部调用。数据库/本地状态损坏仍需人工检查。完整决策与边界见[有限恢复方案](codex-delegation-recovery-refactor-plan.md)。

每个 Character 的 `homeDir` 是代行者执行新任务的工作目录。共享 sidecar/app-server 的进程目录为 `/`；`ensure-binding` 和 `start-turn` 逐请求传 `cwd`，已使用的 thread 通过 resume 与显式 turn override 使用当前 home；同 generation 新建空线程的首轮直接 start，避免缺 rollout 的 resume 错误。`inspect-dispatch` 不带 CWD，也不要求历史目录仍存在或属于当前 allowedRoots。delegation SQLite V5 保留统一恢复计数与独立角色邮件 outbox，并记录结构化 sender/notice/binding 与实际任务发送证据；旧格式只经显式离线升级进入新库。改 home 或 Codex 配置不会因此拒绝旧库。健康时保留同角色线程；失效或结果不明终结后解除绑定。Ready/Leased 回信和 frozen 主线请求继续保留。现有库必须停服后显式离线升级；操作见[恢复与升级说明](codex-delegation-operator-recovery.md)。

关闭时 nonterminal dispatch 保持持久状态；重启后继续有限恢复。C# client 按 dispatch 持有 start claim；只有匹配当前 Pending/requestId 的受控未发送回执能一次释放，迟到或重复帧无权释放后续调用的 claim。格式合法但没有 Pending 的迟到响应只记录诊断；真正的 framing、correlation、ownership 或 child reap 故障仍走进程故障处理。SQLite 和 reply lease 仍是持久任务与回信的唯一所有者。

本地不明终结不等于远端中止。后续任务可能与旧工作同时访问同一 homeDir；FIFO 在此指本地提交顺序。角色应根据失败回信核查当前文件状态。本阶段没有自动 interrupt、重跑原任务或重建 Codex 全部上下文。

### Capture、reply lease 与 Undo

每个 outbound-mail extraction batch 由 `GalateaDelegationSqliteStore` 在一个 transaction 中 all-or-nothing capture；成功的零 intent extraction 也写 `action_capture` tombstone，失败绝不能伪装为 zero。capture 前重验 terminal Action 是 current selected head；commit 后 Journal Undo 不删除、不撤回、不重新武装该 batch。stable dispatch ID 为 length-prefixed `(CharacterId, "Codex", canonical Action head, artifact ordinal)`（沿用原 ID 字符串值） 的 SHA-256，即 `gd1-<64-lowerhex>`。candidate/outbox/inbox 都有 code-owned count/byte 上限；容量满时拒绝整批，绝不 evict 旧项而引入重复。正常串行 admission 下崩溃 gap 至多一个；下一次 admission/pulse 在允许新 turn 前只结算 latest post-baseline terminal Action，attach 本身不执行 provider 提取。TurnEnded 没有 terminal Action，不生成提取目标。baseline frontier 之前的历史和 rewind orphan 不补做；first durable capture 是重复结算 authority，只验证原 Action bytes/digest，不因 extractor 升级重写历史产物。

合法 Codex final 必须是 strict Unicode、nonblank，并同时满足 route reply bound 与 composite 的 256 KiB 单 reply 上限；非法或超限 final 变为 code-owned failure，不能截断冒充回信。`reply_notice` 以单调 `completionSequence` 保存 `Ready | Leased | Consumed`。一个 Character 至多一个 active lease；recovery 只继承已持久 lease，不 claim 后来 Ready 的 notice，inbound turn 也不 claim。

`ObservationBound|ObservationCommitted` 保存选中 notice 和 exact 机读 Observation 的绑定证据；旧绑定保留原 text bytes。若上层还未返回但 Observation/闭合事件已 durable，恢复只从 selected raw lineage 的 exact base、Observation bytes/digest 和闭合结果分类：无 effect evidence 才 rollback 到 Ready；exact terminal Action 或 TurnEnded 均 consume，不能因非成功结束再次投递同一输入。每条 notice 的 `ConsumedTurnEndAddress` 指向闭合事件，历史物理列名 `consumed_action_address` 保留；结束原因从 Journal authority 读取，不复制第二份。任何证据分叉都 durable quarantine，不能由函数返回值或异常文本猜测。store reopen 用 closed parser 验证 timestamp current shape 和无 timestamp 历史 bytes，再逐项核对 player text、notice kind/order/body；不以 current writer 重渲染历史。

lease settlement 在 outbound extraction 之前：已接收回信的 terminal Action 即使后处理失败也不会再投递同一 notice，TurnEnded 同样结算输入消费。普通 player Undo 只移动 SessionJournal selected lineage：已 capture outbox 继续推进、active Codex turn 不 interrupt、Ready 保持 Ready、Consumed 永不重新武装，fixed Codex thread context 不倒退；不存在旧 `RetractedBeforeDispatch`/`SourceRetracted` 内存状态。

### Character Note capture、保存回执与 DerivedInfo

Character Note采用[忠实代写](character-note-transcription.md)：角色在同一Action中用自然语言表达当前保存意图及完整内容，extractor可以归整多段文字，保留事实、否定、条件和不确定性。artifact只含`text`，不要求`evidenceQuote`或与Action的逐字子串匹配；runtime仍检查非空、Unicode、条数和字节上限。采纳后以持久层`ExactText`原样保存，恢复不重新生成正文。保存回执称“已保存的 Note 内容”，不承诺与叙事原文逐字符相同。

Mail 与 Character Memory 是同一 frozen terminal Action 上相互独立的 durable effect，post-completion 并行启动并总会 drain。Character Note 的非空 `ExactText` batch 从 capture 到 Default MemoPod apply；pre-capture provider/unavailable 可以作为 best-effort 留下 latest Action gap，但一旦 capture durable，apply/recovery invariant failure 必须 fail closed。`Rejected` terminal 且无回执，`DeferredAfterCapture` 保留 active batch 并在下一次 admission 阻断新 mutation，`Quarantined` 始终阻断，`SelectedHeadChanged` 阻止新的 capture 但不撤销已 capture 的保存义务。

admission 固定先恢复 store 中全局 `0..1` active Captured/Planned batch，再检查 global quarantine，随后才处理 latest exact terminal Action；不扫描完整 history。重启 attach 不执行 provider 提取，后续 admission/pulse 承接该 gate。baseline physical frontier 覆盖启用前历史。absent capture 可以重跑 extractor，captured/planned batch 只能恢复 apply；普通新轮次与 Undo/rewind 仍先服从该结算门禁；pending/stop 仅在安全 Journal 边界结束原输入，不为结束任务启动新提取。已 Applied 的 Memo 不因 rewind 自动删除。细节与状态表见 [Default MemoPod V1](character-note-default-memopod-v1.md)。

新的 `Planned -> Applied` transaction 原子建立 SQLite V4 save-receipt outbox，保存成功事实、源 Action、Pod、
ordered Memo IDs 与当时正文/不可变来源，不预先生成展示通知。zero、Rejected、AlreadyApplied 和旧历史
Applied 不新增义务。`Pending -> ObservationBound -> Delivered` 的 exact base 与 raw proof 门禁保留；
Delivered 只证明 Observation append，不证明 provider 收到或理解；Terminated 也保留送达证明，结束/rewind 前先结算。

首次请求规划在预算内选择全部确认 IDs 加完整正文，放不下则全部 IDs、零正文，不截断 Note。
选择进入持久绑定后，换风格只能重新呈现，不能再次删字段。旧 `notice_body`/`rendered_observation`
按旧合同读取和证明，旧 Delivered 不重发；新 structured Bound 可以携带显式 legacy receipt 原文。

每个新 Applied 同时创建 DerivedInfo `Pending` work。session-owned pump 对每个 external signal 最多推进一批：短暂持 `TurnLock` 从 Journal 恢复 exact source 并验证 fingerprint，随后锁外用独立 30 秒 deadline 调 `CharacterNoteDerivedInfoEnricher`。模型 timeout/invalid/failure 保持 Pending，不撤销 ExactText/receipt，也不使主 turn 失败；round-robin cursor 防止长期失败 batch 遮挡后项。结果先 durable `Prepared`，后续 provider-free 依 base/target identity 走 `UpdateDerivedInfo -> Planned -> Freeze/confirm -> Applied`；只有 Planned 占用 mutation slot。该泵没有 durable retry schedule/attempt counter；若 provider 忽略 cancellation，shutdown 等它返回才释放 session/durable 资源。

若 exact `Accepted` turn 已由操作员独立证实完成，但官方 projection 持续 `ACCEPTED_TURN_NOT_VISIBLE`，可在停服、锁检查和备份后使用离线 `operator recover-codex-completed`。默认 dry-run；`--apply` 只接受严格 evidence file，并复用生产 `RecordCompletedMail` transaction。它不启动 Web host、provider 或 sidecar。前置条件、证据格式、幂等重跑与禁止操作见 [operator recovery runbook](codex-delegation-operator-recovery.md)。

## 自动 Agent loop 与可观察性

非 maintenance 的 `GalateaServerAgentHostedService` 为每个 configured Character 保留 10 秒 fallback，不依赖 Player 登录或页面打开。所有 configured 的已有 session 都先检查 pending Journal，包含 `autonomyIntervalMinutes=0` 与 `players=[]`；已有 NewRequest/Prepared/可恢复工具尾优先于 reply 和新 heartbeat。interval=0 不为检查恢复而创建不存在的 session，空 reply-only 路径不创建 heartbeat。attach 本地完成后，恢复 runner 持有同一个 TurnLock；等待重试时 pulse 不产生第二个 writer。

没有 pending 时，Ready reply 优先，Empty 才按正 interval 的 process-local monotonic cadence 决定 `HeartbeatActivation`。busy 跳过、不补 tick；首次启动重新 arm，不恢复停机期间的 deadline。完成或合法业务结束后重新计时；临时纯生成故障在同一个 runner 内退避，不变成失败 heartbeat 的永久暂停。环境/协议 blocked 和旧 Failed 保留原任务并阻止普通 pulse 重试，不能借新 heartbeat 覆盖；损坏、quarantine 或未知工具副作用仍 fail closed。原 cadence 设计背景见[自主 interval 设计](per-character-autonomy-interval-design.md)，自动恢复增量以[实施记录](completion-auto-retry-implementation.md)为准。

admission失败保留`AUTOMATIC_ADMISSION_FAILED`及nullable `{code,error}`细节。显式`POST /api/v1/characters/{characterId}/agent/retry-admission`在同一`TurnLock`内复用`ReconcileDurableAdmissionAsync`，只处理旧lease、extraction gap与保存恢复；它不`StartTurn`，不跳过真实provider/结构错误。处理成功且runtime为Idle后才清除admission失败，独立reply失败与runtime recovery仍保留各自约束。忙碌或失败返回409，具体HTTP合同见[Server API](server-api.md)。

`GET /api/v1/characters/{characterId}/agent/status` 和 mailbox status 都是纯读投影：不 attach、reconcile、领取 lease、调用 provider 或等待长回合。mailbox status 只从 supervisor 已有 store 的单个 SQLite read transaction 聚合 state/count/attempt/code/next retry，不返回正文、recipient、subject、dispatch/thread/turn/operation identity 或 hash；Maintenance 时固定为 `unavailable/MAINTENANCE_READ_ONLY`。browser 只轮询状态/recent 并消费 SSE；它不裁决 cadence，也不因打开页面而恢复自动运行。API 的精确 JSON grammar 由 [server-api.md](server-api.md) 维护。

开发日志使用 `DebugUtil` 分类。`Galatea.Mailbox` 记录 extractor 事件、输入字节数与 intent 数，不重复可见 Action/邮件正文；`Galatea.Completion` 记录 feature 纯生成重试与期限诊断，主轮通过 SSE 显示重试等待/预览重置；`Galatea.Delegation` 记录 durable binding/dispatch/reconciliation/terminal/backoff；supervisor 与 sidecar 分类记录 store、生命周期和真实 transport/reap failure。Character Memory 的 `AppliedNow` 可记录 `PodId`、`MemoId`、`ExactText`；Note不再有`EvidenceQuote`字段。console 的 category 选择主要控制 `Trace`/`Info`；达到 console minimum level 的 `Warning`/`Error` 默认直接可见，不依赖该 category 是否启用。其余 progress log 只放受限 identifier、数量、字节数、布尔值、stage/code、pid，不重复邮件正文、subject、evidence、Codex final 或 sidecar stderr。领域 Debug 文件与旧全文日志可能含故事内容；新 `CallLogDir` 只记录 metadata。两者都不是 replay、migration 或 Memo apply authority。

## SessionJournal / RecapGrid 恢复顺序

恢复先服从已经 durable 的 Journal 状态，不能拿 current 配置、current route 或当前 provider 猜测旧回合。历史 Agent Control profile 必须保留，供 `Prepared`/`ToolContinuation` 按 frozen identity 绑定；fresh `NewRequest` 不再绑定 current profile，也不会向新模型请求注入 `recap_grid_control`。current profile 仅提供 missing-session structural bootstrap 的 admission authority。route manifest 在首次 RecapGrid work 才延迟读取；canonical V2 只保存 exact per-route `connectionId` 与并发/timeout 调度 policy。Completion 不提供 caller-selected output cap，也没有 wildcard/default fallback。

恢复按以下顺序处理：

1. **`Prepared`**：先验证 raw/setup/工具与持久计划，再 exact bind completion/tool identity；v9 使用当前 projector 表达同一计划，v7/v8 仍重建原 exact 请求。不打开 Online 或 derived store。
2. **旧 `Started`**：验证旧 schema、父链及请求承诺后找到来源 Prepared，按同一纯生成恢复路径执行，不再有 Refuse/确认开关，也不追加新 Started。旧 Failed 不走此路径，保持 blocked，允许安全边界上的显式 TurnEnded。
3. **`ToolContinuation`**：先 bind frozen tool profile/operation，再以无工具的 current completion 继续；tool settlement 后才打开 Online readiness。
4. **`ToolResult` 后的 `NewRequest`**：不绑定 current tool profile，保留 ToolResult raw tail；只有它和 fresh request 创建 per-turn Online context。

Control 回执以既有 `OperationKey` 引用，不再附带派生 `ResultIdentity`。writer v4 删除 bootstrap 的第二行身份；
旧 Control v2/v3 按源格式验证后投影到同一 graph，保持原 Head/bytes，下一真实 mutation 才写 v4。
receipt 仍匹配 frozen runtime、command 和 sequence，成功重放不重复推进 Control。尚未写入 Journal 的工具结果
用既有 schemaVersion 2 与 operationKey 输出；已有旧工具结果保持原文，tool input/catalog/runtime identity 不变。

Recipes 非空 registration 的 command preimage 删除旧字段，旧 recipe receipt 按新命令会 Conflict；
family/definition-only registration 与 promotion 命令不变。最终真实切换前须在旧状态可读时正常收敛受影响的
recipe registration，以及依赖旧 Store proof 的 promotion，不能以 receipt 存在绕过 command/proof 检查。
当前实施与最终处置见 [Timeline 单一行身份](timeline-row-identity-simplification-plan.md)。

当前 root strict config language 为 V11，connections 是 Completion-owned V3 catalog，delegate route 是 owner-defined V4，profile 是 owner-defined V1。Linux loader 对这些文件和 `characterContextTemplateFile` 都执行 code-owned byte cap、existing-ancestor no-reparse、final-file no-follow regular-file 检查；bootstrap 在首次写前也验证 parent chain。

Fresh/NewRequest 生命周期在合法 raw boundary 执行 Timeline reconcile/seal，必要时 Manager build，随后 Getter 给出 coherent candidate。empty Timeline 或 no-active recipe 使用 `raw-only`：不打开 Store，也不调用 recap provider。恢复路径不能借“补齐当前上下文”为由绕过 frozen identity。

Manager 向 Runtime 传递前驱 view 与实际 cells；Runtime 对照 frozen spec 独立校验历史、规则、
前驱 ID 与有序成员，不再计算 prior digest。

RecapGrid 构建改用 `CellSlot(recipe, history row, column)` 与 Store 分配的 `CellId/RowResultId`；
同 Slot 重开复用首个结果，Overlay 显式复用 base cell。Getter `PriorSourceAligned` 比较来源前驱，
不再以摘要内容 hash 判断等价；合法 Overlay 来源不同仍可读。Store schema v4 遇旧库明确 unsupported，
不会随刷新或恢复自动清库。Timeline schema 3 的行 ID 保持原值，当前 reader 不读旧 schema；最终离线升级与 Reset/重建前提见 [Timeline 计划](timeline-row-identity-simplification-plan.md)。
