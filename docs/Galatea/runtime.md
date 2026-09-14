# Galatea 运行时机制

本文说明 Galatea Server 的运行时职责、持久化边界与关键协议；常规启动、浏览器操作和简要日志命令由 [Galatea 文档索引](README.md) 承接。HTTP 语法见 [server-api.md](server-api.md)，连接与本地配置见 [configuration.md](configuration.md)。本文描述的是代码当前的设计合同；真实 provider/Codex 验证的证据范围见 [Codex delegation verification](codex-delegation-verification.md)。

## 模型切换与 reasoning 回放排障

2026-09-09 的 `gpt-5.6-sol -> gpt-6-astra` 故障在构造 Responses request 时发生：旧模型的 native reasoning 被直接
交给 exact-Origin replay validator，HTTP 尚未发送便抛异常；由于 journal 已记录 `CompletionAttemptStarted`，旧版
最终表现为需要显式恢复的 uncertain 状态。它不是 RecapGrid 损坏的证据，也不能用清空历史来修。

当前 Responses adapter 对相同 ProviderId/ApiSpecId 的 native reasoning 跨模型原样回放，仅省略其他 provider/profile
的 reasoning；可见正文、工具记录、原始 Origin 与历史不变。native payload 仍严格校验；已知本地拒绝使用
`CompletionRequestRejectedException`，由 SessionJournal 持久化为 `CompletionAttemptFailed`，不再错误停在 uncertain。
Codex 有跨模型 live 实验依据；公共 Responses 的相同行为为 operator 授权假设。适配器合同见
[Completion 的 replay 边界](../Completion/openai-codex-subscription-client-design.md#64-独立-protocol-identity)。

**2026-09-14 恢复语义更新：** 当前代码删除了手工 `RequestAdapterFingerprint` 门槛。
旧 Prepared v7 的 adapter 字段只在格式读取时校验并丢弃，v7/v8 使用同一当前 adapter 重构与调用。
已冻结的模型、prompt、history、tools 及逻辑请求 commitment 仍保留；connection、client/API、原生载荷和工具权限检查继续有效。
`StartedOutcomeUncertain` 的重新调用仍须在页面明确授权。授权允许当前修复后的 adapter 执行，不能据此更换连接或模型。

新 Prepared 写 v8；v5 保持历史审计可读、completion 不可执行。部署前正常停服并保留完整数据快照，
回退旧程序时需要匹配的数据快照，不能只换回二进制；恢复旧快照不保留升级后新增轮次。
详见 [Prepared 合同](../SessionJournal/current/contracts/completion-request-prepared-v7.md)。
若其余绑定检查失败，应先核查实际连接/载荷差异，不要连续点击恢复或修改冻结身份。
当前 abandon 仅适用于已确定失败的轮次，不能用来放弃 Prepared/Started；Undo 也不是 pending 迁移接口。
不要修改原始 manifest、reasoning Origin 或把同一个 connectionId 临时改绑另一个模型。
若 operator 明确选择舍弃未完成 turn 的 selected suffix，可以停服、备份后使用
[CLI `rewind-branch`](../../prototypes/SessionJournal.Cli/README.md#离线-branch-回退)：先在副本验证目标为 Idle，
再用 exact Ref/head/target 确认一次 ref 移动。这不是恢复或迁移 frozen request，不删除 raw events，
也不能撤销已发生的 provider/tool、delegation 或 CharacterMemory 副作用；不能据此自动重发旧输入。
没有 pending completion 的旧历史不需要迁移，合法 v2 native reasoning 可直接被新投影读取。
网络中断等真正不确定的调用仍不可自动重发；adapter 升级不增加自动重试权限。

日志排查先看 `Galatea.TurnRunner` 的 exception stack 与 `callLogDir` 中同次调用的 `exception` / `elapsedMs`，
不要把完整 prompt、reasoning payload 或凭据复制到 issue。`Provider` Debug 日志只记录跨 provider/profile 的省略计数，不记录内容。

## Completion、提取器与后处理

`GalateaCompletionOwner` 是 host-wide `CompletionConnectionRegistry` 的唯一 owner。主 Agent、input normalizer、每用户的 outbound-mail extractor 和 RecapGrid 路由都借用同一套惰性 client；extractor 的按用户构造不等于另建 provider client。关闭时先 drain session 和 delegation，再由 owner 清理借用的 RecapGrid runtime 与 distinct Completion client。启用 `callLogDir` 时，统一的 Completion decorator 也会记录 normalizer 的原始输入、prompt 与输出，因此它是本地敏感数据。

`TextExtractor` 是 internal、ephemeral 的结构化提取器：构造时冻结 system prompt、不可变 `TextExtractorToolSet`、connection 与借用 client accessor；调用只提供 `targetText` 和 `userPrompt`。它不拥有 HTTP、SessionJournal、持久化或 client dispose。一次 logical extraction 会建立独立 `ToolSession`/collector，故同一实例可并发使用而不串线，也没有 durable dedupe/recovery 语义。

每次 attempt 只发一个 Completion，请求使用 `Auto` 与 parallel tool calls；artifact tool call 是终态输出，不进入 tool-result 或 repair loop。只有 `OpenAICodexResponsesException` 的 `TransportOutcomeUnknown`、且尚未取得 response 时会重试，最多 **5** 次，退避为 1/2/4/8 秒；调用者取消和其他 transport exception 直接传播。0 个 tool call 表示没有产物，普通文本只是受限 diagnostics。未知、重复或畸形 call、schema/DataAnnotations/custom validation、invocation/termination/error 不匹配都会使整个 extraction 失败，不返回 partial result。工具名、arguments、call 数量和 UTF-8 文本均有代码边界；`openai-codex-responses` 工具名还受其 ASCII 命名限制。connection 未向调用者暴露业务 output cap；adapter 只在 provider 已报告的模型上限适用时传递该字段。

artifact 包装与 Observation 的协作模式、Mailbox 和 Character Note 的接点见 [TextExtractor / Observation Bridge](text-extractor-observation-bridge.md)。Character Note 的现行保存合同见 [Default MemoPod V1](character-note-default-memopod-v1.md)；[自动记忆工作单](automatic-memory-work-order.md)记录回执、DerivedInfo background pump 与 Memo recall 闭环的已批准实施范围，当前行为以对应源码和测试复核。

主 Completion 成功后，runtime 从 SessionJournal 的 frozen terminal Action 取得原始证据，再用 `GalateaVisibleActionTextRenderer` 顺序连接 Text block、排除 reasoning/tool block、整体剥离 inline think。Mail 与 Character Memory 对同一 target 并行 `ReconcileTargetAsync`，并总会 drain。仅 Character Note 的明确 pre-capture timeout、`TextExtractionException` 和 Pod unavailable 属于 best-effort 后处理失败；capture 后的结果保持 pending 或 quarantine，不能伪造回执。Quarantined/invariant 和 caller cancellation 仍按失败仲裁处理，已 durable 的主 Action 不回滚。

Outbound Mail extraction 不设置 code-owned elapsed deadline，只服从 caller cancellation。provider 不结束时，recent refresh、SSE terminal 和 `TurnLock` 会继续等待；Mail failure/cancellation 不写 empty tombstone，当前 SSE 以错误结束，下一次 admission 在新 turn 前重试该 exact gap。一次 logical extraction 的重试复用 request/client，artifact tool 只在最终成功响应后执行；远程重试仍可能重复消耗 provider 算力。capture commit 只 signal supervisor，主 Galatea turn 不等待 Codex accepted/final。

## Observation 与回合入口

新 player-composite turn 使用 runtime-owned `PlayerTurnObservation`，外层为 `PlayerTurnObservationEnvelope`，其 closed trigger set 是：

- `PlayerAction`：唯一携带非空玩家文本、可恢复草稿和 browser rewind token 的变体；可有 recall 和 notice。
- `DelegateReply`：没有玩家文本，至少一条 `Reply`/`DeliveryFailure` notice；不会伪造 player-action。
- `HeartbeatActivation`：没有玩家文本，写入包含经验证 `characterName` 的 code-owned 十分钟自主活动文本；它不携带外界回信。

materialization 从 host `TimeProvider` **只采样一次**本地时间，向下取整到秒，按 `yyyy-MM-dd'T'HH:mm:sszzz` 写入 trigger 前的 metadata；UTC 也写 `+00:00`。它只表示 Observation 的外界形成时间，不等同故事世界时间，不参与排序、identity、cadence 或 settlement。复原与重放使用已冻结的字节，绝不重新采样。

当前写入要求 canonical heading/info string、块顺序和动态 tilde fence；每块由 `AdaptiveMarkdownFenceRenderer` 渲染，正文不 trim、normalize 或 escape。parser 接受受限的历史 dialect，但新写入不接受 `Z`、小数秒或新旧 heading 混用。recall 以 `RecallType + SourceId` 去重，回执至多一条且必须最后；reply、failure、recall 与整个 composite 都有 UTF-8 上限，越界拒绝而不截断。完整字段、兼容读取和预算说明见 [TextExtractor / Observation Bridge](text-extractor-observation-bridge.md)。

### Canonical grammar 与兼容读取

`PlayerTurnObservationEnvelope` 只写入 code-owned prefix、timestamp、closed trigger、heading、info string、顺序以及按正文重新计算的 fence；parser 也只接受能 canonical 重渲染的相同形状。它不把普通 Markdown 当成松散协议：automatic trigger 携带玩家文本、recall 出现在 notice 后、Heartbeat 出现 Reply/DeliveryFailure，或 DelegateReply 没有外界 notice，都会被拒绝。

| 项目 | 当前写入规则 |
| --- | --- |
| `PlayerAction` | `## 玩家角色试图采取的行动` / `player-action`；非空玩家文本，随后 `0..32` recall、总计 `0..16` notice。 |
| `DelegateReply` | 无玩家文本；timestamp 后为 `0..32` recall、至少一条 Reply/DeliveryFailure、可选末尾 `NoteSaveReceipt`，notice 总数仍不超过 16。 |
| `HeartbeatActivation` | `## 角色自主活动时机` / `heartbeat-activation`；精确渲染 code-owned 十分钟活动正文，最多带 recall 和一条末尾 `NoteSaveReceipt`，不带 Reply/DeliveryFailure。 |
| Codex notice | 成功 heading 为 `来自外界代行者 Codex 的回信`，失败为 `发往外界代行者 Codex 的信未能送达`。 |
| Note receipt | `Note 保存回执` / `character-note-save-receipt`；每个 trigger 最多一条且必须最后。旧 V0 `Note 请求回执` / `character-note-request-receipt` 在 current grammar 中明确拒绝。 |
| Recall | `SourceId: ...` 是单行 anchor；精确 key 是 `RecallType + SourceId`。info string 分别为 `memo-gist-recall`、`memo-summary-recall`、`memo-exact-text-recall`。 |

每个 block 的 tilde fence 至少四个字符，且必须长于正文中最长连续 tilde；因此 nested backtick fence、Markdown、HTML/XML 与 Unicode 原样保留。`AdaptiveMarkdownFenceRenderer.RenderBlock` 的 info string 是 1..64 个 ASCII token。`SourceId` 上限 512 UTF-8 bytes，recall body 上限 262,677 bytes，reply body 256 KiB，failure body 4 KiB，整个 composite 1 MiB；所有超限都拒绝，不截断。

读取兼容四类历史形态：带 timestamp 的 current dialect、无 timestamp 的 current dialect、旧 Codex heading 的 legacy dialect、以及历史 Galatea heading/backtick player envelope。带 timestamp 的形状只接受 current heading，且同一 envelope 不得混用 current/legacy heading；recall 仅属于 current dialect。旧 reply 中固定文本“玩家本轮未提交新的动作；本轮仅由外界回信到达触发。”只有同时含至少一个且仅含 Reply/DeliveryFailure、无 recall 的 envelope 才解释为历史 `DelegateReply`，作为普通玩家文本时仍是 `PlayerAction`。V1 `reply_lease.player_text` 保留该值作为内部 identity discriminator，不向 current composer 重新渲染。

recent view 为三种 trigger 分别生成可读文本，但只有 `PlayerAction` 生成 `RestorablePlayerText` 和 rewind token；`DelegateReply` 显示“本轮由 Codex 回信触发。”，`HeartbeatActivation` 显示其 code-owned trigger 正文，两者都不把 synthetic text 放回 composer。inbound envelope 同样不属于普通 player Undo。

普通玩家入口在 per-session `TurnLock` 内先结算 durable reply lease 与 extraction gap，完成 normalization 后才接受新 turn。normalizer 只接收玩家文本，绝不接收 ready notice；一般异常 fail-open 使用原文，取消、fatal、显式 `GalateaTurnException` 和输入超限则阻止 abandon 旧失败 turn、创建 cutoff 或接受新 turn。取得 exact effective text 后才重新核验/abandon 可放弃的旧失败 turn。

SQLite `CutoffFrozen` lease 原子冻结 cutoff 前已 Ready 的 bounded FIFO notice 前缀；之后才 Ready 的结果留给下一合格 `PlayerAction` 或 `DelegateReply`，未选项保持原 FIFO 顺序。选择时为任意合法 64 KiB normalized player text、固定 timestamp metadata 的最坏 fence，以及 pending save receipt 的一个 notice 槽与实际渲染预算预留空间。`CutoffFrozen` 尚没有 Journal base/body；完成 desired setup 后、紧邻 `SendAsync` 前才用 exact selected head 和同一份 canonical Observation 调用 `BindObservationBase`。inbound/recovery 不开新 cutoff，也不为 `GalateaMailboxObservationEnvelope` 增加 timestamp。

`POST /api/v1/mailbox/inbound` 的来信是故事数据，不获得指令权限；它共享 maintenance、`TurnLock`、recovery admission 和 main-connection allowlist，但不经过 input normalizer，也不属于普通玩家 Undo。`MailboxMessage` 冻结校验后的收件人，HTTP caller 不能自报 `to`。

### Memo recall

per-session `IGalateaPlayerTurnRecallProvider` 在 CharacterMemory lazy attach 后构造。`galatea.memo-recall=null` 或 maintenance mode 使用 disabled singleton，在 context/barrier 建立前直接绕过，因此 disabled 路径没有 selector I/O；仅将 binding 设 null 不影响独立的 save-receipt 投递。enabled recall 服务三种 fresh trigger（包括带 reply lease 的 PlayerAction），inbound/recovery 不做 fresh recall。

它把已采样 timestamp、尚无 recall 的 typed Observation、本轮 Reply/DeliveryFailure 与同一 pre-append raw window 最近一条非空 visible Action 确定性渲染为 `atelia.galatea.memo-recall-context.v2` JSON，不再调用 query-builder 模型。`player-action` 带完整 playerText，`heartbeat-activation` 带 code-owned activationText，`delegate-reply` 只带 kind 并以 externalNotices 提供正文；optional notice 按 whole-item prefix、Action 按 whole item 纳入 512 KiB query budget，`NoteSaveReceipt` 不进入 query。Default MemoPod 在短 `_podMutationGate` 内按 settled identity 打开 Frozen handle，provider await 在 gate 外。

selector 最多返回 8 个 ordered ID；runtime 以 Title eligibility、`CharacterNoteOriginBarrier`、`RecallBarrier` 和 1 MiB final Observation budget 选择第一条 `MemoExactText`，最终注入 `0..1` 条。SourceId 是 `memo-pod:v1/<32-lowerhex PodId>/<canonical MemoId>`，正文是完整 `Title + ExactText`，不截断。空 selector 或全部候选过滤是正常 underfill；configured provider/authority failure 则 fail closed，阻止 main Completion。receipt 先占预算，recall 只使用剩余空间；最终 Observation bytes 在 selector 后、`SendAsync` 前绑定到 reply lease 与 receipt outbox。恢复复用已冻结 bytes，不重新 recall、领取或采样时间。

## Mailbox、Codex delegation 与恢复

outbound extractor 从可见 Action 产出有序 `SendMailIntent`（Recipient、可选 Subject、Body、可选 `InReplyToMessageId` 与 `EvidenceQuote`）。runtime 只校验结构、UTF-8 边界和格式；谁实际发送、actor ownership、正文和引用的语义由 extractor LLM 的保守 prompt 判断。当前唯一可路由 recipient 是大小写精确的 `Codex`，其他邮件 durable terminal 为 `Unrouted`，不会启动 sidecar。`EvidenceQuote` 是 provenance，不是 runtime authority；sidecar task 精确等于已验证的 Body，route capability 只来自 code-owned route。

delegation 采用 SQLite-backed durable owner。transport 是 strict bounded JSONL V5：`ensure-binding`、`start-turn`、`inspect-dispatch` 分别对应 binding、接受 turn 与 `not-found|unavailable|running|completed|failed|ambiguous` inspection；inspection 标示 `source=live|persistent`。`ensure-binding` 只建立并核验固定 owned thread，不能携带邮件正文或开始 turn。`Accepted` inspection 必须带 durable exact `turnId`；`OutcomeUnknown` 只能传 null，按 dispatch 发现。wire、thread ownership、环境清洗、工具能力和 durable law 的完整约束见 [delegation durability design](codex-delegation-durability-design.md) 与 [当前重构状态](codex-delegation-refactor-status.md)。

`Queued -> Started`、冻结 operation/thread 和占用 active dispatch 在一个 SQLite transaction 中完成，commit 后才允许 `start-turn`。启动失败带受控 `dispatchState`：只有本次持有 claim 且确证未调用 `turn/start` 时，才可重排队；失效线程随之解除绑定。发出后丢回应、进程崩溃或冷启动遗留 Started 均按 `OutcomeUnknown` 检查，不自动重发原任务。重复请求拒绝不能作为未发送证明。

绑定、发送前失败和结果检查共用邮件上的 `recovery_failure_count`，最多连续失败 8 次，指数退避最多 60 秒。成功绑定或 Accepted 不清零；仅同 generation 的精确 live Running 且当前 metadata active 才清零。历史 `inProgress` 只表示旧状态，不能无限延长恢复。inspection 总截止 45 秒；单请求超时不关闭其他用户共享的 sidecar。retry 时间在每个持久 revision 首次观察时映射到单调时钟，重启最多重新等待 60 秒，不重置失败次数。

耗尽后，同一 SQLite 事务终结本地等待、写入唯一 DeliveryFailure notice 并释放 active slot。`RESULT_UNCONFIRMED` 明确告诉角色结果未确认、旧工作可能仍在运行；`NOT_DISPATCHED_RETRIES_EXHAUSTED` 表示确认未发送。下一封邮件可以创建新线程继续；本地 terminal 不被迟到结果覆盖，该旧任务也不能隔离新任务的 route；普通远端矛盾证据仍按原规则处理。inbox 满时保留状态并等待容量，不继续外部调用。数据库/本地状态损坏仍需人工检查。完整决策与边界见[有限恢复方案](codex-delegation-recovery-refactor-plan.md)。

每个 user 的 `homeDir` 是代行者执行新任务的工作目录。共享 sidecar/app-server 的进程目录为 `/`；`ensure-binding` 和 `start-turn` 逐请求传 `cwd`，已使用的 thread 通过 resume 与显式 turn override 使用当前 home；同 generation 新建空线程的首轮直接 start，避免缺 rollout 的 resume 错误。`inspect-dispatch` 不带 CWD，也不要求历史目录仍存在或属于当前 allowedRoots。delegation SQLite V3 统一任务恢复计数，移除 route 的 ensure 预算；V2 已删除 route policy fingerprint。改 home 或 Codex 配置不会因此拒绝旧库。健康时保留同用户线程；失效或结果不明终结后解除绑定。Ready/Leased 回信和 frozen 主线请求继续保留。现有库必须停服后显式离线升级；操作见[恢复与升级说明](codex-delegation-operator-recovery.md)。

关闭时 nonterminal dispatch 保持持久状态；重启后继续有限恢复。C# client 按 dispatch 持有 start claim；只有匹配当前 Pending/requestId 的受控未发送回执能一次释放，迟到或重复帧无权释放后续调用的 claim。格式合法但没有 Pending 的迟到响应只记录诊断；真正的 framing、correlation、ownership 或 child reap 故障仍走进程故障处理。SQLite 和 reply lease 仍是持久任务与回信的唯一所有者。

本地不明终结不等于远端中止。后续任务可能与旧工作同时访问同一 homeDir；FIFO 在此指本地提交顺序。角色应根据失败回信核查当前文件状态。本阶段没有自动 interrupt、重跑原任务或重建 Codex 全部上下文。

### Capture、reply lease 与 Undo

每个 outbound-mail extraction batch 由 `GalateaDelegationSqliteStore` 在一个 transaction 中 all-or-nothing capture；成功的零 intent extraction 也写 `action_capture` tombstone，失败绝不能伪装为 zero。capture 前重验 terminal Action 是 current selected head；commit 后 Journal Undo 不删除、不撤回、不重新武装该 batch。stable dispatch ID 为 length-prefixed `(userId, "Codex", canonical Action head, artifact ordinal)` 的 SHA-256，即 `gd1-<64-lowerhex>`。candidate/outbox/inbox 都有 code-owned count/byte 上限；容量满时拒绝整批，绝不 evict 旧项而引入重复。正常串行 admission 下崩溃 gap 至多一个；下一次 player/inbound admission 或 session attach 在允许新 turn 前只结算 latest post-baseline terminal Action。baseline frontier 之前的历史和 rewind orphan 不补做；first durable capture 是重复结算 authority，只验证原 Action bytes/digest，不因 extractor 升级重写历史产物。

合法 Codex final 必须是 strict Unicode、nonblank，并同时满足 route reply bound 与 composite 的 256 KiB 单 reply 上限；非法或超限 final 变为 code-owned failure，不能截断冒充回信。`reply_notice` 以单调 `completionSequence` 保存 `Ready | Leased | Consumed`。一个 user 至多一个 active lease；recovery 只继承已持久 lease，不 claim 后来 Ready 的 notice，inbound turn 也不 claim。

`ObservationBound|ObservationCommitted` 冻结 exact Observation bytes、byte count 与 SHA-256。若上层还未返回但 Observation/Action 已 durable，恢复只从 selected raw lineage 的 exact base、Observation bytes/digest 和 terminal Action 分类：无 effect evidence 才 rollback 到 Ready；exact terminal Action 才 consume，并将同一 receiving Action address 写进每条 notice；任何证据分叉都 durable quarantine，不能由函数返回值或异常文本猜测。store reopen 用 closed parser 验证 timestamp current shape 和无 timestamp 历史 bytes，再逐项核对 player text、notice kind/order/body；不以 current writer 重渲染历史。

lease settlement 在 outbound extraction 之前：已接收回信的 terminal Action 即使后处理失败也不会再投递同一 notice。普通 player Undo 只移动 SessionJournal selected lineage：已 capture outbox 继续推进、active Codex turn 不 interrupt、Ready 保持 Ready、Consumed 永不重新武装，fixed Codex thread context 不倒退；不存在旧 `RetractedBeforeDispatch`/`SourceRetracted` 内存状态。

### Character Note capture、保存回执与 DerivedInfo

Character Note采用[忠实代写](character-note-transcription.md)：角色在同一Action中用自然语言表达当前保存意图及完整内容，extractor可以归整多段文字，保留事实、否定、条件和不确定性。artifact只含`text`，不要求`evidenceQuote`或与Action的逐字子串匹配；runtime仍检查非空、Unicode、条数和字节上限。采纳后以持久层`ExactText`原样保存，恢复不重新生成正文。保存回执称“已保存的 Note 内容”，不承诺与叙事原文逐字符相同。

Mail 与 Character Memory 是同一 frozen terminal Action 上相互独立的 durable effect，post-completion 并行启动并总会 drain。Character Note 的非空 `ExactText` batch 从 capture 到 Default MemoPod apply；pre-capture provider/unavailable 可以作为 best-effort 留下 latest Action gap，但一旦 capture durable，apply/recovery invariant failure 必须 fail closed。`Rejected` terminal 且无回执，`DeferredAfterCapture` 保留 active batch 并在下一次 admission 阻断新 mutation，`Quarantined` 始终阻断，`SelectedHeadChanged` 阻止新的 capture 但不撤销已 capture 的保存义务。

admission/restart 固定先恢复 store 中全局 `0..1` active Captured/Planned batch，再检查 global quarantine，随后才处理 latest exact terminal Action；不扫描完整 history。baseline physical frontier 覆盖启用前历史。absent capture 可以重跑 extractor，captured/planned batch 只能恢复 apply；所有正常 HTTP durable mutation 都经过这道 gate，所以 pending 会先于 Undo/rewind settlement，已 Applied 的 Memo 不因 rewind 自动删除。细节与状态表见 [Default MemoPod V1](character-note-default-memopod-v1.md)。

新的 `Planned -> Applied` transaction 原子建立 SQLite V3 save-receipt outbox；zero、Rejected、AlreadyApplied 和升级前历史 Applied 都不另建义务。outbox 依次为 `Pending -> ObservationBound -> Delivered`：下一次 `PlayerAction`、`HeartbeatActivation` 或 `DelegateReply` 领取最早 Pending；inbound/recovery 不领。绑定 exact base head 和完整 Observation 后，raw proof 为 NotAppended 回到 Pending，InProgress/Terminal 则 Delivered；冲突或无法证明 fail closed。Delivered 仅表示 Observation 已 durable append，不表示 provider 收到、角色读懂或主 Completion 成功；abandon/rewind 前必须先结算 bound receipt，Delivered 后不重发。极端 fence 无法与合法 player text 和最大 reply 同框时，写明“Note内容超出回执展示预算”的 identity-only 确认，列 Source Action/Memo IDs，不静默截断保存正文。

保存回执的 `notice_body` 在 Applied 事务中一次生成并冻结。后续展示文案变化不重渲染或改写旧回执，也不以新版文字与旧 body 不同为由拒绝打开数据库。重开仍校验 body 的非空、UTF-8 和预算，以及 Applied 来源与修订关系；已绑定 Observation 必须包含原来冻结的回执。旧 Delivered 回执保留原文，不新增投递义务。

每个新 Applied 同时创建 DerivedInfo `Pending` work。session-owned pump 对每个 external signal 最多推进一批：短暂持 `TurnLock` 从 Journal 恢复 exact source 并验证 fingerprint，随后锁外用独立 30 秒 deadline 调 `CharacterNoteDerivedInfoEnricher`。模型 timeout/invalid/failure 保持 Pending，不撤销 ExactText/receipt，也不使主 turn 失败；round-robin cursor 防止长期失败 batch 遮挡后项。结果先 durable `Prepared`，后续 provider-free 依 base/target identity 走 `UpdateDerivedInfo -> Planned -> Freeze/confirm -> Applied`；只有 Planned 占用 mutation slot。该泵没有 durable retry schedule/attempt counter；若 provider 忽略 cancellation，shutdown 等它返回才释放 session/durable 资源。

若 exact `Accepted` turn 已由操作员独立证实完成，但官方 projection 持续 `ACCEPTED_TURN_NOT_VISIBLE`，可在停服、锁检查和备份后使用离线 `operator recover-codex-completed`。默认 dry-run；`--apply` 只接受严格 evidence file，并复用生产 `RecordCompletedMail` transaction。它不启动 Web host、provider 或 sidecar。前置条件、证据格式、幂等重跑与禁止操作见 [operator recovery runbook](codex-delegation-operator-recovery.md)。

## 自动 Agent loop 与可观察性

非 maintenance 的 `GalateaServerAgentHostedService` 只 attach `serverAgentUserIds`，随后每 10 秒调用 `GalateaAutomaticTurnCoordinator`。协调器在 `TurnLock` 内先结算 lease/gap，仅在 exact `Idle` 边界 admission：有 Ready reply 时冻结 FIFO 前缀并运行 `DelegateReply`；没有时到期才 claim `HeartbeatActivation`。busy 跳过，不补 tick；首次启动重新 arm，不从 SQLite/raw 恢复 deadline，也不补停机期间的 tick。成功主回合重新计时，Heartbeat failure 只暂停空激活，reply failure 进入阻断以避免反复领取。实现边界与验证见 [headless agent pilot 工作单](headless-agent-pilot-work-order.md)。

admission失败保留`AUTOMATIC_ADMISSION_FAILED`及nullable `{code,error}`细节。显式`POST /api/v1/agent/retry-admission`在同一`TurnLock`内复用`ReconcileDurableAdmissionAsync`，只处理旧lease、extraction gap与保存恢复；它不`StartTurn`，不跳过真实provider/结构错误。处理成功且runtime为Idle后才清除admission失败，独立reply失败与runtime recovery仍保留各自约束。忙碌或失败返回409，具体HTTP合同见[Server API](server-api.md)。

`GET /api/v1/agent/status` 和 mailbox status 都是纯读投影：不 attach、reconcile、领取 lease、调用 provider 或等待长回合。mailbox status 只从 supervisor 已有 store 的单个 SQLite read transaction 聚合 state/count/attempt/code/next retry，不返回正文、recipient、subject、dispatch/thread/turn/operation identity 或 hash；Maintenance 时固定为 `unavailable/MAINTENANCE_READ_ONLY`。browser 只轮询状态/recent 并消费 SSE；它不裁决 cadence，也不因打开页面而恢复自动运行。API 的精确 JSON grammar 由 [server-api.md](server-api.md) 维护。

开发日志使用 `DebugUtil` 分类。`Galatea.Mailbox` 记录 extractor 事件、输入字节数与 intent 数，不重复可见 Action/邮件正文；`Galatea.TextExtractor` 记录上述 pre-response retry；`Galatea.Delegation` 记录 durable binding/dispatch/reconciliation/terminal/backoff；supervisor 与 sidecar 分类记录 store、生命周期和真实 transport/reap failure。Character Memory 的 `AppliedNow` 可记录 `PodId`、`MemoId`、`ExactText`；Note不再有`EvidenceQuote`字段。console 的 category 选择主要控制 `Trace`/`Info`；达到 console minimum level 的 `Warning`/`Error` 默认直接可见，不依赖该 category 是否启用。其余 progress log 只放受限 identifier、数量、字节数、布尔值、stage/code、pid，不重复邮件正文、subject、evidence、Codex final 或 sidecar stderr。Debug 文件和 `CallLogDir` 可能含故事敏感内容，且从不是 replay、migration 或 Memo apply authority；保留期与访问控制由本机操作者负责。

## SessionJournal / RecapGrid 恢复顺序

恢复先服从已经 durable 的 Journal 状态，不能拿 current 配置、current route 或当前 provider 猜测旧回合。历史 Agent Control profile 必须保留，供 `Prepared`/`ToolContinuation` 按 frozen identity 绑定；fresh `NewRequest` 不再绑定 current profile，也不会向新模型请求注入 `recap_grid_control`。current profile 仅提供 missing-session structural bootstrap 的 admission authority。route manifest 在首次 RecapGrid work 才延迟读取；canonical V2 只保存 exact per-route `connectionId` 与并发/timeout 调度 policy。Completion 不提供 caller-selected output cap，也没有 wildcard/default fallback。

恢复按以下顺序处理：

1. **`Prepared`**：先 exact bind frozen completion 与 frozen tool identity；不打开 Online 或 derived store。
2. **`Started`**：先使用启动时 strict-frozen config/connections；默认 `Refuse` 发生在本次 current connection selection/client、route 和 derived owner 之前。
3. **`ToolContinuation`**：先 bind frozen tool profile/operation，再以无工具的 current completion 继续；tool settlement 后才打开 Online readiness。
4. **`ToolResult` 后的 `NewRequest`**：不绑定 current tool profile，保留 ToolResult raw tail；只有它和 fresh request 创建 per-turn Online context。

Control 回执以既有 `OperationKey` 引用，不再附带派生 `ResultIdentity`。旧 Control v2 文件
按源格式验证后读取，保持原 Head 与 bytes；正常持久 mutation 才写 v3。历史 pending
工具操作继续匹配 frozen runtime、command 和 sequence，已生效的 receipt 重放不重复
推进 Control。尚未写入 Journal 的工具结果用当前 schemaVersion 2 与 operationKey 输出；
已经写入的旧工具结果保持原文。该升级不要求先收敛旧 pending，也不改变工具输入或
runtime identity；写入 v3 后的程序回退需匹配数据快照，见[回执简化设计](control-receipt-simplification-plan.md)。

当前 root strict config language 为 V9，connections 是 Completion-owned V3 catalog，delegate route 是 owner-defined V4，profile 是 owner-defined V1。Linux loader 对这些文件和 `characterContextTemplateFile` 都执行 code-owned byte cap、existing-ancestor no-reparse、final-file no-follow regular-file 检查；bootstrap 在首次写前也验证 parent chain。

Fresh/NewRequest 生命周期在合法 raw boundary 执行 Timeline reconcile/seal，必要时 Manager build，随后 Getter 给出 coherent candidate。empty Timeline 或 no-active recipe 使用 `raw-only`：不打开 Store，也不调用 recap provider。恢复路径不能借“补齐当前上下文”为由绕过 frozen identity。

Manager 向 Runtime 传递前驱 view 与实际 cells；Runtime 校验成员关系后直接计算原 prior digest，
对照 frozen spec。完整 `PriorInputProjection` 对象已删除，缓存键和持久格式保持，
无需数据升级；详见[前置输入简化](recap-prior-input-simplification-plan.md)。
