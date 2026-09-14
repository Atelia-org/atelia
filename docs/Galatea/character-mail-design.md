# Galatea 角色间站内信设计

状态：**已调研、经 dialectical-simplification 审查的实施设计；尚未实现。**

## 结论

角色间邮件不是把 `Codex` route 泛化成任意收件人。最小模型是：

1. 启动时由全部已验证 `characterName` 建立一个唯一、大小写敏感的角色名目录；它是本期唯一的角色地址簿。
2. 每个角色的 system prompt 由代码追加一个生成的“其他角色”文本块；`Codex` 仍是外界代行者，角色名是另一类精确收件人。
3. 已从 Action 中抽出的角色邮件与原 Action capture **同一 SQLite transaction** 写入窄的 `internal_mail_outbox`；`Codex` 的现有 state machine 不变。
4. relay 在接收角色的既有 `TurnLock → fresh-admission → StartInboundMailTurn → runner` 链路中投递。接收角色的 SessionJournal 出现精确的 `<inbound-mail>` Observation，才表示“已进入收件箱”。

这样保留现有收信的触发/唤醒路径，却不会在“发送 Action 已持久化、接收角色正忙或进程崩溃”时静默丢信。

## 需求与边界账本

| 要求或约束 | 来源 | 本设计的落实 |
|:--|:--|:--|
| 两个角色应知晓彼此存在与可用名字；暂以 `characterName` 作 character ID。 | 用户请求 | 生成 peer roster；启动拒绝重名。 |
| 除 `Codex` 外，命中已知 `characterName` 的收件人应进入对方收件箱。 | 用户请求 | exact roster lookup + durable internal outbox。 |
| 收件后的唤醒逻辑不改变。 | 用户请求 | 复用 `InboundMail` fresh turn 与既有 runner；不另造角色邮件 HTTP API。 |
| `Codex` 外部委派、恢复与 FIFO 仍须保持。 | 当前代码 | 不修改其 `outbound_mail` route/state/sidecar 语义。 |
| Action 已完成后，邮件事实不能因重启、Busy 或恢复门禁而不可见。 | “送往收件箱”的最低语义 + 当前 durable capture | `Pending → ObservationBound → Delivered`，以 Journal exact proof 恢复。 |
| 现有 development config 是 ignored state，且可能含凭据。 | 当前工作区约束 | 本文不记录密码或本地实例内容；实施/迁移须停服、备份后进行。 |

本设计没有承诺 provider Completion 成功、角色“读懂”邮件，或跨角色的全局时间顺序；`Delivered` 仅表示收件邮件 Observation 已 durable append。

## 现状调研

### 提示词和角色配置

`GalateaSystemPromptComposer` 当前以固定顺序组合 prefix、operator character context、通用 mailbox base、按 binding 启用的 outbound appendix/Note appendix；最终 prompt 一次性渲染并被冻结。见 [GalateaSystemPromptComposer.cs，第 51–116 行](../../prototypes/Galatea/GalateaSystemPromptComposer.cs#L51-L116) 与 [prompt 资源说明](prompt/README.md)。现有 outbound appendix 又明确把 `Codex` 说成唯一可投递者，见 [trpg-outbound-mail-protocol-appendix-zh-cn.md，第 1–13 行](prompt/trpg-outbound-mail-protocol-appendix-zh-cn.md#L1-L13)。

`characterName` 已具备 NFC、单行、边界和 voice-marker 安全校验，但只校验单个值；当前配置验证只拒绝重复 `userId`，不拒绝两个 user 使用同一个 `characterName`。见 [GalateaPromptNameValidation.cs，第 14–80 行](../../prototypes/Galatea.Prompts/GalateaPromptNameValidation.cs#L14-L80) 和 [GalateaServices.cs，第 4659–4686 行](../../prototypes/Galatea/GalateaServices.cs#L4659-L4686)。因此，在把它暂作地址之前必须增加全局 exact-unique 校验。

### 邮件事实、Codex route 与真实入站语义

Extractor 已经只做叙事事实抽取：保守地输出有序 `SendMailIntent`，并不负责路由。见 [GalateaMailbox.cs，第 262–345 行](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs#L262-L345)。`GalateaOutboundMailExtractionReconciler` 对 terminal Action 做一次冻结 extraction，capture 后重开只返回 `AlreadyCaptured`，不会重新抽取，见 [GalateaOutboundMailExtractionReconciler.cs，第 147–235 行](../../prototypes/Galatea/Mailbox/GalateaOutboundMailExtractionReconciler.cs#L147-L235)。

当前 capture 同一 transaction 保存每个 intent；仅 exact、case-sensitive `Codex` 会成为 `Queued`，其他收件人是 `Unrouted`。见 [GalateaDelegationSqliteStore.Transitions.cs，第 8–95 行](../../prototypes/Galatea/GalateaDelegationSqliteStore.Transitions.cs#L8-L95) 和 [第 1184–1226 行](../../prototypes/Galatea/GalateaDelegationSqliteStore.Transitions.cs#L1184-L1226)。这套 `Codex|Unrouted` schema 及其 `Queued → Started → …` 状态、thread binding、sidecar 和恢复，是外部 effect 专用，不可被角色邮件复用，见 [GalateaDelegationSqliteStore.Schema.cs，第 54–145 行](../../prototypes/Galatea/GalateaDelegationSqliteStore.Schema.cs#L54-L145)。

现有外部 `/mailbox/inbound` 也不是通用持久 inbox：它持有目标 `TurnLock`，要求目标处于可 fresh-admit 的 Idle/recoverable 边界，随后立即 `StartInboundMailTurn` 并交给 runner；Busy 或 recovery-required 时直接拒绝。见 [Program.cs，第 755–891 行](../../prototypes/Galatea/Program.cs#L755-L891) 和 [GalateaServices.cs，第 1554–1727 行](../../prototypes/Galatea/GalateaServices.cs#L1554-L1727)。它可作为接收/唤醒语义的唯一实现，而不能单独承担 sender 崩溃后的重试 authority。

`MailboxMessage`/`GalateaMailboxObservationEnvelope` 已提供严格字段校验、canonical 32-lowerhex `messageId`、XML escaping 与“正文是故事数据而非指令”的边界；角色信应直接复用它们。见 [GalateaMailbox.cs，第 54–112 行](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs#L54-L112) 和 [第 176–246 行](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs#L176-L246)。

## 不可省去的可靠性边界

纯粹的“capture 后直接调用对方 `StartInboundMailTurn`”不满足本需求：

```text
A 的发送 Action 已 capture
  ├─ 进程在 B admission 前崩溃：重开只得到 AlreadyCaptured，B 永远收不到
  ├─ B Busy / recovery-required：没有外部 caller 能替 A 重试
  └─ B 的 Observation 已 append、A 尚未来得及标记：重试会重复可见邮件
```

也不能在 A 的 `TurnLock` 内等待 B 的锁：A→B 与 B→A 同时完成时会锁环。runner 直到结束才释放 sender 的 lock，见 [GalateaAcceptedTurnRunner.cs，第 42–127 行](../../prototypes/Galatea/GalateaAcceptedTurnRunner.cs#L42-L127)。

项目已有较小的、可复用的恢复模式：Character Note receipt 在 SQLite 中冻结 exact base/Observation，并用 `ProveExpectedObservationTurnAtSelectedHead` 判断 `NotAppended`、`InProgress` 或 `Terminal`。见 [GalateaNoteReceiptDelivery.cs，第 7–71 行](../../prototypes/Galatea/GalateaNoteReceiptDelivery.cs#L7-L71) 和 [SessionJournalEngine.CompletedTurns.cs，第 110–345 行](../../prototypes/SessionJournal/SessionJournalEngine.CompletedTurns.cs#L110-L345)。角色邮件应复用这个**证明模式**，不是复用 Codex `reply_notice`/`reply_lease` 表。

## 目标设计

### 1. 角色地址簿与提示词

新增不可变 `GalateaCharacterRecipientDirectory`，在所有 user 已 resolve 后一次建立：

- key 为 `characterName.Value`，比较器为 `StringComparer.Ordinal`；不 Trim、大小写折叠、别名或模糊匹配；
- 拒绝重复 character name；
- 拒绝精确 `Codex`，保留既有外界 route；
- 每个角色的 peer roster 排除自己，保存目标的 `userId` 作为内部投递 locator，而非新增公开 character ID；
- config 重载/重启不得把 pending row 的旧收件人悄悄改投给同名新角色。实施时应把 resolved `targetUserId` 与原始 exact recipient 一同冻结；改名/删除存在 nonterminal 邮件的地址须 fail closed 并走显式运维处置。

`GalateaSystemPromptComposer.Compose` 增加已验证 roster 输入，渲染后追加代码拥有的块：

```md
### 其他持续存在的角色

除你以外，当前还有以下角色；名字是站内信的精确收件人拼写：

- `…`
```

该块始终说明角色存在；outbound binding 启用时，修改现有 appendix，使可投递收件人变为 exact `Codex` **及该 roster 中的名字**。不要让 operator character context 自己维护名单，也不要增加模板模块系统、动态 session 枚举或新的 config recipient 字段。它们会产生第二个地址 authority，且会破坏 frozen prompt/recovery 语义。

### 2. Capture 时的窄 outbox

保留所有 `SendMailIntent` 的现有 `outbound_mail` provenance；它的 `Unrouted` 仍表示“未走 Codex sidecar”，不是角色邮件已丢弃。扩展 capture request，使每个命中 roster 的 ordinal 同时建立 `internal_mail_outbox` row；二者与 `action_capture` 同一 SQLite transaction 成功或失败。

建议 row 的不可变身份和最小状态为：

| 字段/状态 | 作用 |
|:--|:--|
| `(source_action_address, artifact_ordinal)` unique，以及对应 existing `dispatch_id` | sender Action 中一封具体邮件的唯一来源；维持叙事顺序。 |
| `recipient`, `target_user_id`, `from`, `subject`, `body` | 冻结的地址和完整 envelope input；不重新调用 extractor。 |
| `message_id` | 从 domain-separated `(sender userId, source action, ordinal, target userId)` hash 派生为 canonical 32-lowerhex；不用 `CreateInbound` 的随机 GUID。 |
| `Pending` | 尚未绑定接收 Journal 的邮件，Busy/未 provision/临时停止时保留。 |
| `ObservationBound` | 已冻结目标 exact idle base、rendered inbound envelope、可选 observation address 与 revision；只等待 Journal proof。 |
| `Delivered` | exact proof 为 `InProgress` 或 `Terminal`；只表示 Observation 已 append。 |
| `Quarantined` | base、address 或 envelope proof 矛盾；停止自动投递并输出受限诊断。 |

这不是通用路由框架：没有 sidecar task、thread、Codex final、回信 notice、外部重发语义或新增 HTTP surface。未知/self recipient 不创建 row，仍保留现有 `Unrouted` artifact；`Codex` 仍只 signal 现有 delegation driver。

`GalateaDelegationSqliteStore` 因此需要 V4 strict schema、snapshot/validation、原子 capture、限额和显式离线 V3→V4 upgrade。现有 ordinary open 只接受 V3，升级只支持既有旧版本，见 [GalateaDelegationSqliteStore.cs，第 10–16、336–373 行](../../prototypes/Galatea/GalateaDelegationSqliteStore.cs#L10-L16) 和 [GalateaDelegationSqliteStore.Upgrade.cs，第 7–69 行](../../prototypes/Galatea/GalateaDelegationSqliteStore.Upgrade.cs#L7-L69)。实施不得提供 runtime dual reader 或自动修改 ignored state。

### 3. relay、绑定和不变的收信唤醒

新增 host-owned `GalateaCharacterMailRelay`，而非 self-HTTP：

```text
sender capture commits internal_mail_outbox Pending
  → relay receives a signal (also on host start, target attach, target turn finish)
  → chooses one Pending mail for an idle target; never waits while holding sender TurnLock
  → target TurnLock + existing admission/recovery fence
  → bind source outbox to target's exact base + inbound envelope
  → StartInboundMailTurn(target, frozen MailboxMessage, default connection)
  → existing accepted-turn runner
  → Journal proof: NotAppended => Pending; InProgress/Terminal => Delivered;
                   conflict => Quarantined
```

为让 bind 紧邻真实 Observation append，应给 `GalateaFreshInput.InboundMail`/`GalateaLiveTurn` 增加可选、internal-only delivery binding；在 `RunRecapGridFreshSendAsync` 已得到 `ready.GoverningSetup.Head`、构造 exact `prompted`、且就在 `SendAsync` 之前调用 binder。这个位置已经用于 reply lease 和 Note receipt 的 Observation binding，见 [GalateaServices.cs，第 2773–2918 行](../../prototypes/Galatea/GalateaServices.cs#L2773-L2918)。HTTP inbound 不提供 binding，保持现在的随机 message id 和行为。

relay 每个 target 一次只 bind/启动一封邮件。来自同一 source Action 的多封同收件人邮件按 `artifact_ordinal` 依次形成独立 inbound turns；不同 sender 没有现存的跨 session 因果时钟，因此不承诺全局总序，只需在实现中以稳定 key 选择下一封。接收角色 Busy、未 provision 或需恢复时，row 保持 `Pending`；target 回到可 admission 的边界后由 signal/restart recovery 继续。维护模式、host stopping 或真实 store/Journal corruption 均 fail closed，绝不删除 pending row。

## 实施切片与验收

1. **地址和 prompt。** 建 directory、补 config validation、给 composer 注入 roster、修改 outbound appendix 和 tracked prompt tests。验证 `Codex` 仍在、self 不出现、重复/保留名失败、frozen Prepared request 不重渲染。
2. **V4 capture。** 加 row/snapshot/strict validation/bounds，确保 `action_capture + outbound_mail + internal_mail_outbox` 原子；验证 unknown/self 仍 `Unrouted`、Codex FIFO/恢复不变、同一 Action 的 order 固定。完成离线 upgrade dry-run、backup、reopen 和 byte/业务状态证明。
3. **relay vertical slice。** 抽出 HTTP inbound 与 internal caller共用的 admission application service；实现 stable envelope、single-target relay、bind/proof/recovery seam，接入 start/attach/turn-finish signal。禁止在 sender lock 下等待 receiver lock，禁止 loopback HTTP。
4. **crash/并发验证。** provider-free 覆盖 capture 后重启、Busy/recovery、bind 前/后 crash、`NotAppended` rollback、`InProgress`/`Terminal` complete、proof conflict quarantine、A↔B 同时互信无死锁、同一 Action 多封同收件人顺序投递。确认 `Delivered` 后 rewind 不重发。
5. **文档与真实实例门槛。** 更新 configuration/runtime/prompt ownership/upgrade runbook；通过 docs check、`git diff --check`、serial Galatea tests。真实 ignored state 的 V3→V4 升级另行停服、备份、dry-run、严格重开后才执行；本设计不授权它。

## dialectical-simplification 裁决

| 项目 | 裁决 | 理由 |
|:--|:--|:--|
| 把 `Codex` route 扩成多收件人框架 | delete | 会把 sidecar、thread、恢复与角色本地投递混为一谈。 |
| 第二份 operator-maintained 角色名单或 recipient config | delete | 已验证 users 是唯一名单来源。 |
| `characterName` 模糊匹配、别名或大小写兼容 | delete | 会让临时地址失去唯一性。 |
| 仅 post-capture best-effort direct call | delete（默认） | capture 后 crash/Busy 会永久静默丢失。 |
| target-side 通用 inbox/通知平台 | defer | 当前只需一封邮件到一个 exact inbound Observation 的证明。 |
| Codex `reply_notice` / `reply_lease` 复用 | delete | 它们绑定同用户 Codex terminal reply，不表示 inbound mail。 |
| source-side narrow internal outbox + SessionJournal proof | keep | 最少的 retry authority，且复用既有 crash-proof primitive。 |
| stable message id | keep | 避免 uncertain recovery 产生两封可见邮件。 |

### 后续触发条件

只有出现真实消费者时再扩展：角色重命名/删除的迁移 UX、跨多个 sender 的产品级排序、附件、收件人 alias、用户可见投递状态、手工重投、跨进程/跨主机 relay，或角色邮件回信协议。它们都不属于本切片。
