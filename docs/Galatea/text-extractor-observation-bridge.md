# TextExtractor / Observation Bridge

本文记录 Galatea 当前已经落地的一种 runtime 与角色之间的异步双向通讯模式：

- 角色到 runtime：角色在叙事 `Action` 中表达意图，runtime 用 `TextExtractor` 把可见文本提取成 typed artifact。这个方向对位 LLM tool-call。
- runtime 到角色：runtime 把外部事件、回信、失败、时间等信息拼装进 composite `Observation`，在下一次主线 Completion 中交给角色。这个方向对位 tool-result。

这不是单次 provider invocation 内部的 tool call / tool result loop。它是 runtime 在 turn 边界外侧实现的通讯桥：
主线角色模型只继续书写故事；runtime在durable turn边界读取和提取。Mail与Character Note分别进入自己的durable
owner；Note在durable apply到默认MemoPod的同一SQLite事务中建立save receipt outbox，再以Observation数据注入。
三trigger共享记忆与durable投递的当前契约见[Automatic memory工作单](automatic-memory-work-order.md)。

## 源码地图

通用构件：

- [`TextExtractor`](../../prototypes/Galatea/TextExtractor.cs)：一次性 structured extraction wrapper。它调用 Completion，收集 artifact tool calls，返回 `TextExtractionResult`。
- [`TextExtractorArtifactTool`](../../prototypes/Galatea/TextExtractor.cs)：把 typed POCO 挂成 artifact tool；普通 assistant text 只作为 bounded diagnostic，不会被解析为 artifact。
- [`GalateaVisibleActionTextRenderer`](../../prototypes/Galatea/GalateaVisibleActionTextRenderer.cs)：从 terminal `ActionMessage` 中提取角色可见文本，排除 reasoning/tool blocks 并剥离 inline think。
- [`GalateaTerminalActionExtractionTarget`](../../prototypes/Galatea/GalateaTerminalActionExtractionTarget.cs)：冻结一条exact terminal Action的address、visible text、SHA-256与UTF-8 byte count，供多个extractor共享。
- [`GalateaFreshInput`](../../prototypes/Galatea/GalateaFreshInput.cs)：fresh turn 的 typed input 总入口。Mailbox 只是其中一个来源，后续 note/recall 不应把这个类型下沉到 Mailbox namespace。
- [`PlayerTurnObservation`](../../prototypes/Galatea/PlayerTurnObservation.cs)：PlayerAction、HeartbeatActivation与DelegateReply共享的typed composite Observation模型。
- [`PlayerTurnObservationEnvelope`](../../prototypes/Galatea/PlayerTurnObservation.cs)：把玩家行动、runtime metadata、reply/failure notices 渲染成 canonical Observation。

Mailbox specialization：

- [`MailboxMessage`](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs)：inbound mail 的 frozen value object。
- [`GalateaMailboxObservationEnvelope`](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs)：把 inbound mail 写成独立的 escaped Observation envelope。
- [`SendMailIntent`](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs)：outbound mail extraction 的 artifact contract。
- [`OutboundMailExtractor`](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs)：角色叙事 Action -> `SendMailIntent` 的 per-user extractor。
- [`GalateaOutboundMailExtractionReconciler`](../../prototypes/Galatea/Mailbox/GalateaOutboundMailExtractionReconciler.cs)：在 durable turn boundary 上读取 latest terminal Action、调用 extractor、写入 capture/tombstone。
- [`GalateaDelegationSqliteStore`](../../prototypes/Galatea/GalateaDelegationSqliteStore.cs)：当前 outbound mail artifact 的 durable owner。
- [`GalateaDurableDelegationDriver`](../../prototypes/Galatea/GalateaDurableDelegationDriver.cs)：把 routed outbound mail dispatch 到 Codex sidecar，并把 reply/failure 转成 ready notice。

Character Memory specialization：

- [`CharacterNoteIntent` / `CharacterNoteExtractor`](../../prototypes/Galatea/CharacterMemory/CharacterNoteExtractor.cs)：识别角色本人当前明确的长期Note保存请求，忠实整理为单字段`Text`；不要求专门提交句式或原文子串匹配，仅声称已经保存不构成新请求。
- [`CharacterNoteDefaultPodReconciler`](../../prototypes/Galatea/CharacterMemory/CharacterNoteDefaultPodReconciler.cs)：durable capture/zero tombstone、Default MemoPod plan/apply与restart/admission恢复owner。
- [`CharacterNoteDerivedInfoEnricher`](../../prototypes/Galatea/CharacterMemory/CharacterNoteDerivedInfoEnricher.cs)：在ExactText已保存后，基于source turn的raw Observation、visible Action与ordered Note targets生成完整Title/Gist/Summary batch。
- [`CharacterNoteDerivedInfoPump`](../../prototypes/Galatea/CharacterMemory/CharacterNoteDerivedInfoPump.cs)：session-owned非阻塞调度器；只在context materialization时短暂持有`TurnLock`，provider调用与Pod apply在锁外执行。
- [`CharacterNoteSaveReceipt`](../../prototypes/Galatea/CharacterMemory/CharacterNoteSaveReceipt.cs)：从durable Applied Memo identities与ExactText冻结诚实保存回执；极端正文使用明确标识的compact确认。
- [`GalateaNoteReceiptDelivery`](../../prototypes/Galatea/GalateaNoteReceiptDelivery.cs)：将SQLite V3 receipt outbox绑定到exact raw Observation，并以journal proof结算投递。
- [`CharacterNoteOriginBarrier`](../../prototypes/Galatea/CharacterMemory/CharacterNoteOriginBarrier.cs)：把当前provider-visible raw Action与CharacterMemory durable provenance做bounded exact join，阻止来源正文仍直接可见的Memo被动态召回重复注入；production recall disabled时整条barrier路径在context selection前绕过。
- [`GalateaMemoRecallQueryRenderer`](../../prototypes/Galatea/CharacterMemory/GalateaMemoRecallQueryRenderer.cs)：把preliminary typed Observation与同窗recent Action确定性渲染成MemoPod query，不增加前置LLM。
- [`GalateaDefaultMemoPodRecallProvider`](../../prototypes/Galatea/CharacterMemory/GalateaDefaultMemoPodRecallProvider.cs)：在settled Default Pod Frozen epoch上调用selector，并以Title、两道barrier与Observation budget规划0..1条`MemoExactText`。
- `PlayerTurnNotice.NoteSaveReceipt`：三种trigger Observation中的独立strong type；canonical顺序中至多一条且必须为最后notice。

入口与注入点：

- [`GalateaHostService`](../../prototypes/Galatea/GalateaServices.cs)：在 admission / send / recovery 边界串联lease settlement、shared-target Mail/Note post-completion extraction，以及fresh Observation materialization。
- [`Program.cs`](../../prototypes/Galatea/Program.cs)：HTTP `POST /api/v1/mailbox/inbound` 与 `POST /api/v1/mailbox/ready-turn`。
- [`GalateaRecentTurnDisplayAdapter`](../../prototypes/Galatea/GalateaRecentTurnDisplayAdapter.cs)：把 stored Observation 投影回 browser 可读显示。

## 双向通讯形状

### 1. 角色到 runtime：叙事意图提取

角色不会真的调用 Galatea runtime API。它只在主线 `Action` 中写出叙事化内容，例如“角色发送一封给 Codex 的邮件”。主线 `Action` durable 后，runtime 才读取 selected raw lineage 上的 latest terminal Action。

现行 Mailbox 路径是：

1. `GalateaHostService` 在允许下一次 admission 或完成 fresh turn 后，调用 `ReconcileOutboundMailExtractionAsync`。
2. `GalateaOutboundMailExtractionReconciler` 只读取 selected head 上的最新 completed turn，不扫描完整历史。
3. `GalateaVisibleActionTextRenderer.Render` 只把 Action 的 visible text blocks 拼起来，reasoning/tool blocks 不进入 extraction target。
4. `OutboundMailExtractor` 用 per-user `characterName` 渲染 system/user prompt，并通过 `TextExtractor` 要求 provider 用 `emit_send_mail_intent` tool calls 输出 artifact。
5. runtime 验证每个 `SendMailIntent` 的结构、UTF-8 bound、single-line recipient/subject、canonical reply id 等。
6. durable store 在一个 transaction 中 capture 整批 artifact；0 artifact 也是成功 tombstone，extractor failure 不能冒充空结果。

successful fresh/recovery主Completion只读取/render一次terminal Action target，并并行启动Mail与Character Note
`ReconcileTargetAsync`。两条task都必须drain。Note token只作用于capture前；capture后由Character Memory reconciler
完成或留下durable pending。明确的pre-capture timeout/TextExtraction/Pod availability在post-completion是best-effort，
但Quarantined/invariant fail closed。`DeferredAfterCapture`不回执，并由下一次admission先恢复；admission自己的
pre-capture失败也会阻止新mutation。

每次新的`Planned -> Applied`在同一SQLite V3事务中建立冻结回执，资格不依赖post-completion返回值或final head：
admission recovery即使settle了off-lineage source，也仍有真实保存事实可通知。AlreadyApplied不新建义务，zero/Rejected
无回执，Deferred等待真正Applied；旧版历史Applied migration不补发。Mail failure、caller cancellation或后续head change
不撤销已提交的Memo与outbox。病态adaptive-fence正文改用明确标注的Source Action/Memo IDs确认，不静默丢弃。

这一路的语义 authority 是分层的：

- “角色是否真的发送了邮件”“邮件正文是什么”“recipient 是否来自叙事 Action”由 extractor LLM 按 code-owned prompt 保守判断。
- Character Note同样由extractor判断当前保存意图及归属，并忠实整理正文；可以归整多段表达，无需固定提交句式或逐字子串。事实、否定、条件与不确定性必须保留，不能补写或摘要。其artifact只有`text`；详情见[Note 忠实代写](character-note-transcription.md)。
- runtime 只证明 artifact 的结构、bounds、route policy、durable identity 与幂等边界。
- Mail的`evidenceQuote`是extractor provenance，不是runtime对raw Action的机械source-grounding证明；Note不再携带该字段。

### 2. runtime 到角色：复合 Observation 注入

runtime 也不把外部事件塞进角色的 hidden state。它把外部信息写成主线模型可见的 Observation 数据，让角色在下一轮叙事中读取。

现行有两种注入形状：

- Typed composite Observation：`PlayerTurnObservationEnvelope`写入真实trigger与单次采样的外界本地时间，再按recalls、external notices、末尾receipt顺序组合；Heartbeat不带external notices，DelegateReply必须至少带一条，所有trigger的notice总上限仍为16。
- Inbound mail Observation：`GalateaMailboxObservationEnvelope` 把外部来信写成 escaped XML envelope，再作为 fresh input 启动一轮主线 Completion。

PlayerAction与DelegateReply中的external notices对位tool-result：它们是上一次或更早outbound artifact的异步结果，
但不在原provider invocation内返回。runtime在`BeginCutoff`时冻结bounded FIFO前缀，之后才Ready的结果留给下一轮；
存在pending保存回执时，cutoff预留一个notice槽位和该冻结回执的实际预算。

Note save receipt使用SQLite V3 `Pending -> ObservationBound -> Delivered` outbox。三个fresh trigger都可领取，
inbound与recovery不新领取。runtime先附receipt、再调用共享Memo selector；query V2保留真实trigger，receipt不进入query。
final canonical Observation bytes在SendAsync前绑定到exact base head。raw proof为NotAppended时回到Pending；
InProgress或Terminal即标记Delivered，含义仅为Observation已durable append，不是provider已收到或角色已理解。
冲突证据fail closed。abandon/rewind前先结算bound receipt；Delivered之后即使rewind也不重发。
receipt只证明采纳的Note内容已保存到默认MemoPod，不承诺与叙事Action逐字符一致，也不承诺分类、metadata补全或召回；pending在pre-dispatch failure/restart后保留。

这一路的安全/耐久边界是：

- Observation 文本由 runtime canonical renderer 生成，不由外部 caller 自报。
- 每个信息块使用 code-owned heading、info string 与 adaptive fence；正文不 trim、normalize 或 escape。
- runtime metadata 只作为故事可见数据，例如 host-local timestamp 不参与排序、identity 或 settlement。
- parser 只接受 canonical round-trip；历史兼容 dialect 是只读读取能力，不是新 writer 分支。

## 为什么这套模式适合 note / recall

自主笔记和动态信息召回很像 Mailbox，但不应该复用 Mailbox 名字或 storage contract。

已经落地的映射：

- note save intent：仅当对应binding非`null`时，code-owned主prompt appendix才告诉角色如何表达长期Note保存请求与完整内容；runtime用单字段`CharacterNoteIntent.Text`忠实提取，经durable capture/apply精确保留采纳后的正文，并在新的Applied事务中原子建立honest保存回执outbox。
- note derived-info enrichment：ExactText Applied后由CharacterMemory V3建立DerivedInfo Pending work；background pump从SessionJournal exact source重建上下文，在锁外调用独立enricher，并用Prepared/Planned/base-target recovery把Title/Gist/Summary写回同一Memo。失败保留Pending，不改变保存回执事实。
- note origin suppression：`CharacterNoteIntent`不携带Action identity；runtime从canonical visible Action派生address/hash/byte count并持久化。三个fresh trigger从同一provider-visible raw context重建`CharacterNoteOriginBarrier`，在来源Action仍可见时阻止对应typed Memo candidate重复注入。

已落地的recall映射（更细的触发策略仍留待真实使用数据）：

- recall trigger：PlayerAction、HeartbeatActivation与DelegateReply均在fresh materialization中查询一次Default MemoPod；有reply lease也不跳过。inbound/recovery不做新查询。
- recall result planning：production provider已用current Observation加同窗recent Action构造canonical query，调用MemoPod selector后同时应用canonical recall anchor barrier与Character Note origin barrier，再把第一条Title-qualified `MemoExactText`注入现有composite Observation。

应该复用的东西：

- `TextExtractor` 的 artifact-only extraction 模型。
- `GalateaVisibleActionTextRenderer` 的 visible Action target。
- per-user prompt render 与 `ContractId` fingerprint 思路。
- durable capture 的 first-commit authority、0-result tombstone、selected-head identity、retry/recovery fencing。
- `PlayerTurnObservationEnvelope` 的 composite Observation renderer 和 parser 严格 round-trip 风格。

不应该复用的东西：

- `Atelia.Galatea.Server.Mailbox` namespace。Note/recall 应该有自己的 domain namespace，例如 `Atelia.Galatea.Server.Memory` 或更窄的 `Atelia.Galatea.Server.CharacterMemory`。
- `SendMailIntent` / `IOutboundMailExtractor` / mailbox bounds。它们是邮箱协议，不是通用 extraction contract。
- `GalateaDelegationSqliteStore` 的 outbound_mail schema，除非新功能明确属于 Codex delegation owner。
- Codex recipient allowlist、reply lease、server automatic coordinator与ready-turn dev one-shot pulse。这些是当前服务端Agent/Mailbox的产品策略，不是模式本身；浏览器不再发送周期heartbeat。

## 新功能复用检查表

新增一个类似机制时，先回答这些问题：

1. 角色到 runtime 的 artifact contract 是什么？字段是否 immutable，是否有 DataAnnotations / custom validation？
2. artifact tool name 是否稳定，是否符合 provider surface 的 tool-name 限制？
3. extraction prompt 是否 per-user 渲染？角色名、voice marker、semantic version 是否进入 `ContractId`？
4. extractor failure、0 artifact、partial artifact、duplicate tool call 分别如何处理？
5. durable identity 绑定到哪个 raw Action / selected head / external event？是否需要 tombstone 防止重提取？
6. capture commit 后，Undo、rewind、provider retry、host crash 会不会重新武装同一 side effect？
7. runtime 到角色的注入形状是什么：普通 `PlayerTurnObservation` notice，还是独立 Observation envelope？
8. 注入文本的 authority 是谁？哪些字段来自 external caller，哪些必须由 runtime 生成？
9. parser 是否要求 canonical round-trip？是否需要只读历史 dialect？
10. recent view / Undo / recovery / maintenance mode 是否都知道这个新 Observation shape？
11. 新产物是否会在其来源Action仍可见时被零增量重复注入？如果会，runtime-owned provenance由谁持久化，provider-visible barrier如何重建？

## 命名建议

命名要暴露通讯方向和 domain：

- 从角色叙事提取并保存Note：`CharacterNoteIntent`, `ICharacterNoteExtractor`, `CharacterNoteDefaultPodReconciler`。
- 把 runtime 信息注入下一轮：`PlayerTurnRecall`, `RecallBarrier`, `CharacterNoteOriginBarrier`。
- 通用 helper 保持 domain-neutral：`TextExtractor`, `GalateaVisibleActionTextRenderer`, `GalateaFreshInput`。

避免使用裸 `ExtractionReconciler`、`IntentExtractor` 这类过宽名字。Mailbox 重构后已经把 outbound mail 相关类型收进
`Atelia.Galatea.Server.Mailbox`，就是为了给后续 note/recall 留出并列空间，而不是把所有异步通讯都塞进一个邮箱概念里。

## 当前 Mailbox 实例的最小心智模型

```text
main Action durable
    |
    v
visible Action text
    |
    v
TextExtractor + emit_send_mail_intent tool calls
    |
    v
SendMailIntent artifacts
    |
    v
durable capture / route / dispatch
    |
    v
Codex reply or delivery failure becomes Ready notice
    |
    v
next PlayerAction / DelegateReply Observation includes notice block
```

也可以把它理解成一条跨 turn 的“外置 tool loop”：

```text
role narrative intent  ~= tool-call
runtime durable effect ~= tool executor
future Observation     ~= tool-result
```

关键点是中间每一步都由 runtime 显式持久化和验证；角色文本只是输入证据，不是执行权限本身。
