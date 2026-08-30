# TextExtractor / Observation Bridge

本文记录 Galatea 当前已经落地的一种 runtime 与角色之间的异步双向通讯模式：

- 角色到 runtime：角色在叙事 `Action` 中表达意图，runtime 用 `TextExtractor` 把可见文本提取成 typed artifact。这个方向对位 LLM tool-call。
- runtime 到角色：runtime 把外部事件、回信、失败、时间等信息拼装进 composite `Observation`，在下一次主线 Completion 中交给角色。这个方向对位 tool-result。

这不是单次 provider invocation 内部的 tool call / tool result loop。它是 runtime 在 turn 边界外侧实现的通讯桥：
主线角色模型只继续书写故事；runtime在durable turn边界读取和提取，只有Mail等真实durable effect进入自身持久化/
调度owner，development Character Note回执则只经过in-process queue，再把结果以Observation数据重新注入。

## 源码地图

通用构件：

- [`TextExtractor`](../../prototypes/Galatea/TextExtractor.cs)：一次性 structured extraction wrapper。它调用 Completion，收集 artifact tool calls，返回 `TextExtractionResult`。
- [`TextExtractorArtifactTool`](../../prototypes/Galatea/TextExtractor.cs)：把 typed POCO 挂成 artifact tool；普通 assistant text 只作为 bounded diagnostic，不会被解析为 artifact。
- [`GalateaVisibleActionTextRenderer`](../../prototypes/Galatea/GalateaVisibleActionTextRenderer.cs)：从 terminal `ActionMessage` 中提取角色可见文本，排除 reasoning/tool blocks 并剥离 inline think。
- [`GalateaTerminalActionExtractionTarget`](../../prototypes/Galatea/GalateaTerminalActionExtractionTarget.cs)：冻结一条exact terminal Action的address、visible text、SHA-256与UTF-8 byte count，供多个extractor共享。
- [`GalateaFreshInput`](../../prototypes/Galatea/GalateaFreshInput.cs)：fresh turn 的 typed input 总入口。Mailbox 只是其中一个来源，后续 note/recall 不应把这个类型下沉到 Mailbox namespace。
- [`PlayerTurnObservation`](../../prototypes/Galatea/PlayerTurnObservation.cs)：普通 player turn 的 composite Observation 模型。
- [`PlayerTurnObservationEnvelope`](../../prototypes/Galatea/PlayerTurnObservation.cs)：把玩家行动、runtime metadata、reply/failure notices 渲染成 canonical Observation。

Mailbox specialization：

- [`MailboxMessage`](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs)：inbound mail 的 frozen value object。
- [`GalateaMailboxObservationEnvelope`](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs)：把 inbound mail 写成独立的 escaped Observation envelope。
- [`SendMailIntent`](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs)：outbound mail extraction 的 artifact contract。
- [`OutboundMailExtractor`](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs)：角色叙事 Action -> `SendMailIntent` 的 per-user extractor。
- [`GalateaOutboundMailExtractionReconciler`](../../prototypes/Galatea/Mailbox/GalateaOutboundMailExtractionReconciler.cs)：在 durable turn boundary 上读取 latest terminal Action、调用 extractor、写入 capture/tombstone。
- [`GalateaDelegationSqliteStore`](../../prototypes/Galatea/GalateaDelegationSqliteStore.cs)：当前 outbound mail artifact 的 durable owner。
- [`GalateaDurableDelegationDriver`](../../prototypes/Galatea/GalateaDurableDelegationDriver.cs)：把 routed outbound mail dispatch 到 Codex sidecar，并把 reply/failure 转成 ready notice。

Character Memory V0 specialization：

- [`CharacterNoteIntent` / `CharacterNoteExtractor`](../../prototypes/Galatea/CharacterMemory/CharacterNoteExtractor.cs)：保守提取角色本人已明确完成提交、且正文exact source-grounded的development Note保存请求；仅声称已经保存不构成提交。
- [`CharacterNoteRequestReceipt`](../../prototypes/Galatea/CharacterMemory/CharacterNoteRequestReceipt.cs)：把一批validated intent渲染成honest development-only回执，并提供per-session bounded in-process FIFO。
- `PlayerTurnNotice.NoteRequestReceipt`：普通player Observation中的独立strong type；canonical顺序中至多一条且必须为最后notice。

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

successful fresh/recovery主Completion的现行post-completion路径只读取/render一次terminal Action target。Host直接
启动Mail `ReconcileTargetAsync`与enabled Character Note extraction，再先await Mail、后await Note。Mail继续拥有
durable/fail-closed语义；Note是best-effort：caller/shutdown cancellation传播，provider/validation failure与30秒
cooperative deadline只丢弃回执。Mail失败会取消并drain Note；两条task都在`TurnLock`和borrowed client lifetime内
结束，不使用`Task.Run`、`Task.WhenAll`或fire-and-forget。同一connection可收到两个overlapping调用，但client仍可
自行限流。

Note成功时，host在final `current head == SourceAction` fence后把1..N条request intent冻结为一条请求回执；0 intent不排队。
queue full或receipt因outer Observation bound无法render都只产生development diagnostic，不使主回合失败。V0没有
Note durable tombstone，因此admission/restart gap reconciliation仍只运行Mail，不补跑Note。

这一路的语义 authority 是分层的：

- “角色是否真的发送了邮件”“邮件正文是什么”“recipient 是否来自叙事 Action”由 extractor LLM 按 code-owned prompt 保守判断。
- runtime 只证明 artifact 的结构、bounds、route policy、durable identity 与幂等边界。
- `evidenceQuote` 是 extractor provenance，不是 runtime 对 raw Action 的机械 source-grounding 证明。

### 2. runtime 到角色：复合 Observation 注入

runtime 也不把外部事件塞进角色的 hidden state。它把外部信息写成主线模型可见的 Observation 数据，让角色在下一轮叙事中读取。

现行有两种注入形状：

- 普通 player turn composite Observation：`PlayerTurnObservationEnvelope` 写入玩家行动、Observation 形成时的外界本地时间、0..N 条 reply/failure notice，以及可选且必须位于最后的单条Note请求回执。
- Inbound mail Observation：`GalateaMailboxObservationEnvelope` 把外部来信写成 escaped XML envelope，再作为 fresh input 启动一轮主线 Completion。

普通 player turn 中的 notices 对位 tool-result：它们是上一次或更早 outbound artifact 的异步结果，但不在原 provider invocation 内返回。runtime 在 `BeginCutoff` 时冻结 bounded FIFO 前缀，把已经 Ready 的 notice 拼进本轮 Observation；之后才 Ready 的结果留给下一轮。

Note receipt使用另一条in-process at-most-once attach规则：只有普通player `StartTurn`在
`BeginCutoff == Empty`时`TryDequeue`一条，作为sole/final notice冻结进本次`PlayerAction`。Created reply cutoff、
ready-turn、inbound与recovery都不领取；领取后的pre-dispatch stop、失败、Undo、rewind或restart不重新排队。它只
证明runtime识别到了请求，不证明Memo已经保存、索引或可召回。

这一路的安全/耐久边界是：

- Observation 文本由 runtime canonical renderer 生成，不由外部 caller 自报。
- 每个信息块使用 code-owned heading、info string 与 adaptive fence；正文不 trim、normalize 或 escape。
- runtime metadata 只作为故事可见数据，例如 host-local timestamp 不参与排序、identity 或 settlement。
- parser 只接受 canonical round-trip；历史兼容 dialect 是只读读取能力，不是新 writer 分支。

## 为什么这套模式适合 note / recall

自主笔记和动态信息召回很像 Mailbox，但不应该复用 Mailbox 名字或 storage contract。

已经落地的V0映射：

- note request intent：仅当对应binding非`null`时，code-owned主prompt appendix才告诉角色如何在Action中明确完成提交development Note保存请求；runtime用`CharacterNoteIntent` artifact tool保守提取，并在未来eligible普通player turn返回honest请求回执。请求尚不保存、索引或召回。

仍属后续候选的映射：

- recall trigger：runtime 在 admission、turn completion 或显式事件边界判断是否需要召回。
- recall result injection：runtime 把召回内容作为新的 `PlayerTurnNotice` kind，或作为独立 fresh input / Observation envelope 注入。

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
- Codex recipient allowlist、reply lease、ready-turn browser heartbeat。这些是当前 Mailbox/Codex delegation 的产品策略，不是模式本身。

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

## 命名建议

命名要暴露通讯方向和 domain：

- 从角色叙事提取意图：`CharacterNoteIntent`, `ICharacterNoteExtractor`, `CharacterNoteExtractionReconciler`。
- 把 runtime 信息注入下一轮：`CharacterRecallNotice`, `CharacterRecallObservationEnvelope`。
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
next PlayerTurnObservation includes notice block
```

也可以把它理解成一条跨 turn 的“外置 tool loop”：

```text
role narrative intent  ~= tool-call
runtime durable effect ~= tool executor
future Observation     ~= tool-result
```

关键点是中间每一步都由 runtime 显式持久化和验证；角色文本只是输入证据，不是执行权限本身。
