# Galatea 角色间站内信设计

> **格式边界（2026-09-16）**：本文保留站内信的原始设计和阶段验收；`users[]`、`rendered_observation` 与冻结 Observation 文本的描述属于旧格式。V10 已完成 Player/Character 分离和 delegation SQLite V5 的稳定机读绑定；当前 root config 已为 [V11](../SessionJournal/current/contracts/galatea-root-config-v11.md)，请求时仍瞬态渲染。见[结构化输入合同](structured-input-rendering-design.md)与[当前运行时](runtime.md)。原子 capture、目标门禁、exact append proof 和 Delivered 的语义继续适用，旧 Bound 按原证明读取。真实 V10 迁移验收记录仍见[迁移验收](player-character-migration-validation.md)，不替代 V11 operator 切换。

2026-09-15 阶段状态：**已实现，当时真实 ignored 实例尚未迁移。** 该阶段完成第二次 dialectical-simplification 审查、独立代码审阅及 provider-free 两角色 production spine 验证。第 6 节保留当时的部署前置步骤；后续实际结果以上方迁移验收为准。

## 最小模型

角色地址来自已验证的 `users[].characterName`。发送 Action 被 capture 时，在发送者现有 SQLite 内原子记录一条待投递记录；后台通过目标角色的既有 `InboundMail` turn 投递。**目标 Journal 已 durable append 精确的邮件 Observation，才算 Delivered；Completion 是否成功不影响这个事实。**

```text
已完成的发送 Action
  → 同一 SQLite transaction：action_capture + outbound_mail + internal_mail_outbox
  → relay 找到目标，取得目标 TurnLock，核对未决投递，仅在 exact Idle 启动
  → StartInboundMailTurn → accepted-turn runner 接手
  → runner 内、SendAsync 前冻结目标 exact base + 最终 Observation 文本
  → SendAsync append 邮件 Observation 并执行既有轮次
  → exact Journal proof → Delivered
```

跨库没有原子提交，所以必须保留 outbox 和 proof。更关键的是：**目标下一次开始输入、恢复或撤回历史之前，也必须先完成该 proof 对账。** 后台 relay 不是唯一对账入口。

本期只增加一个投递附表、一个 host-owned relay，以及接入现有 delivery reconciliation 的目标门禁；不泛化 Codex route，不新增 inbox 数据库、HTTP API 或全局邮件时钟。

## 1. 需求账本与运行边界

### 产品行为及来源

| 行为 | 来源与本期处理 |
|:--|:--|
| 角色知道彼此存在，暂以精确 `characterName` 为地址。 | 沿用原稿记载的产品方向；本轮未独立取得其历史用户对话，不把文档归因当成新的用户指令。 |
| 信件进入目标角色的 `<inbound-mail>` Observation，并触发既有 runner。 | 沿用原稿方向；复用输入形式和执行链，后台 admission 政策见第 5 节。 |
| `Codex` 外部委派、FIFO、恢复及 frozen Prepared request 保持既有语义。 | 当前代码与真实 durable state。 |
| capture 成功后，Busy、重启或恢复门禁不能使邮件静默丢失。 | 持久发送事实的最低可靠性要求；不是承诺 Completion 成功。 |
| 同一 sender→target 的邮件按发送 capture 顺序投递。 | 当前已有 `capture_sequence` 和 `artifact_ordinal`，保留已知先后；不声称是历史用户原话。 |
| 未知名字、self recipient 不投递；名称不模糊匹配。 | 沿用原稿的本期策略，保留 `Unrouted` artifact，不产生失败回信。 |
| 来信可在无浏览器、未加入 `serverAgent` 名单时唤醒角色。 | 明确沿用原稿自动 relay 的设计选择：来信与 heartbeat enrollment 独立。角色可连续互信；本期不增加循环检测或回合配额。 |

最后一项是产品政策，不能仅由“复用 runner”推导出来。若以后要求收信受 enrollment 或配额控制，应单独修改该政策；它不阻塞当前文档收敛。

### 接受的运行与故障模型

- 单个 host 管理少量角色；每个 SessionJournal 只有现有 `TurnLock` 保护的 writer。sender SQLite 由既有 delegation supervisor 持有，不允许第二 owner。
- sender SQLite 与 target Journal 分开持久化。进程可在 capture、bind、append、settle 之间崩溃；signal 可合并或丢失。
- 已有 V3 delegation stores 是真实兼容约束；新 V4 尚无数据，不为新格式增加兼容 reader。
- 正常在线 rewind/abandon 必须走服务门禁。离线替换 Journal、删除 store 或移除配置中的 sender 不属于可自动恢复的运行模型，处置前提见第 6 节。
- 不承诺跨 sender 全局总序、provider 成功、角色理解邮件或收件人阅读回执。Delivered 后撤回目标回合或 sender Action 都不撤销投递、不自动重发。

## 2. 实码依据与施工入口

下表是 2026-09-15 的实现依据；相邻设计只作背景，不作为独立需求证据。

| 入口 | 当前行为及对本设计的约束 |
|:--|:--|
| [GalateaSystemPromptComposer](../../prototypes/Galatea/GalateaSystemPromptComposer.cs)、[prompt 资源说明](prompt/README.md) | 固定协议组合、一次模板渲染、最终 UTF-8 限额；outbound appendix 由 extractor binding 启用。 |
| [GalateaPromptNameValidation](../../prototypes/Galatea.Prompts/GalateaPromptNameValidation.cs)、[GalateaServices](../../prototypes/Galatea/GalateaServices.cs) 的 config resolve/validation | 单名已校验 NFC、单行和边界；只做了 userId 全局去重。当前逐 user Compose，需改成先验证全体名字、再生成各自 prompt。 |
| [GalateaOutboundMailExtractionReconciler](../../prototypes/Galatea/Mailbox/GalateaOutboundMailExtractionReconciler.cs)、[capture transitions](../../prototypes/Galatea/GalateaDelegationSqliteStore.Transitions.cs) 的 `CaptureActionBatch` | 一次冻结 extraction；`AlreadyCaptured` 不重抽。仅 exact `Codex` 被路由，其他 artifact 保存为 `Unrouted`。 |
| [delegation schema](../../prototypes/Galatea/GalateaDelegationSqliteStore.Schema.cs)、[snapshot validation](../../prototypes/Galatea/GalateaDelegationSqliteStore.Snapshot.cs) | `outbound_mail` 已存来源、ordinal、recipient、subject、body；角色投递无需复制这些字段。Codex 的 thread、sidecar、reply lease 是另一套真实语义。 |
| [GalateaMailbox](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs) | `MailboxMessage.FromCanonicalEnvelope` 已可接受冻结字段，message ID 必须是 32-lowerhex；复用字段验证、XML escaping 和正文作为故事数据的边界。 |
| [GalateaDelegationDurableContract](../../prototypes/Galatea/GalateaDelegationDurableContract.cs) 的 `CreateDispatchId` | 现有 dispatch ID 是 `gd1-` 加 64hex，不能直接充当 message ID；无需修改其算法。 |
| [GalateaDelegationSupervisor](../../prototypes/Galatea/GalateaDelegationSupervisor.cs) | 启动打开所有已配置且存在的 sender stores；Session 则按需 attach。通过 supervisor 查询邮件，不依赖 sender Session 存在。 |
| [Program](../../prototypes/Galatea/Program.cs) 的 `/mailbox/inbound`、[AutomaticTurnCoordinator](../../prototypes/Galatea/GalateaAutomaticTurnCoordinator.cs) | HTTP inbound 可接纳 `FailedTurnMustBeAbandoned`，自动入口只接纳 exact Idle；不能整段照搬 HTTP policy。 |
| [GalateaNoteReceiptDelivery](../../prototypes/Galatea/GalateaNoteReceiptDelivery.cs)、[CompletedTurns proof](../../prototypes/SessionJournal/SessionJournalEngine.CompletedTurns.cs) | 复用 exact base/content proof，但该 proof 只定位当前或最近一轮，**不是历史 messageId 搜索或去重接口**。Note receipt 在本用户 store，角色信则在其他 sender store。 |
| [GalateaAcceptedTurnRunner](../../prototypes/Galatea/GalateaAcceptedTurnRunner.cs) | runner 接手目标 `TurnLock`，到 finally 才释放；sender capture 路径不能等待另一个角色的锁。 |

三个不可省去的失败轨迹：

```text
capture 已提交 → crash / target Busy → 没有 durable outbox 就永远丢信
target append 已完成 → sender 未标 Delivered → 盲目重试就重复投递
target append 已完成 → sender 未标 Delivered → target 先开始下一轮或 rewind
  → 现有 proof 失去原来的最近轮次证据，不能再安全决定重投
```

稳定 message ID 只保证 envelope 稳定；真正阻止重复投递的是原子 capture、exact proof 和目标门禁。

## 3. 地址簿与 prompt

新增不可变 `GalateaCharacterRecipientDirectory`，由全体已验证 user 构建；每项包含 `characterName`、`userId` 和既有 `sessionRepositoryId`。后者直接复用 supervisor 的 `CreateSessionRepositoryId(sessionDir)`，不新增 character ID 或 generation。

1. 使用 `StringComparer.Ordinal`。拒绝重复 character name 和精确 `Codex`；不 Trim、折叠大小写或建立 alias。
2. 每个角色的 peer roster 排除自己，按角色名 Ordinal 排序。名单始终说明其他角色存在；只有 outbound extractor binding 启用时才承诺可发给 exact `Codex` 及 peer 名字。
3. 更新 [outbound appendix](prompt/trpg-outbound-mail-protocol-appendix-zh-cn.md) 中“Codex 是唯一可投递者”的文字。保留完成发送、同一 Action 中完整收件人和正文、可选 subject、按叙事顺序抽取等既有规则。
4. 名字是数据：用 JSON 字符串数组展示 roster，追加在模板 Render 之后，再校验**完整 prompt** 的 UTF-8 限额。现有名字校验允许反引号等字符，不能用未转义的 Markdown inline-code 拼名单。
5. config resolve 分两步：先验证全体名字并建 directory，再为每个 user Compose。operator 不维护第二份名单。fresh setup 使用当前完整 prompt；frozen Prepared recovery 仍读取已冻结内容，不重新生成 roster。

## 4. 原子 capture 与持久表示

### 一个 artifact，一条投递附记录

保留 `action_capture` 和 `outbound_mail` 的当前语义。对每个非 self、命中 directory 的 intent，同一 `CaptureActionBatch` transaction 额外插入 `internal_mail_outbox`。这类 artifact 在 Codex 专用列中仍是 `Unrouted`；角色投递状态只由附表负责，不能从 `route_class/state` 推断角色邮件丢失。

| 附表字段 | 唯一职责 |
|:--|:--|
| `dispatch_id` PK/FK → `outbound_mail.dispatch_id`，删除 RESTRICT | 关联一封既有邮件。不再重复 source action、ordinal、recipient、subject、body。 |
| `target_user_id`, `target_session_repository_id` | capture 时解析并冻结目标 locator；不在重试时重新按名字路由。 |
| `from_character_name` | 冻结发送时的名字；不能从重启后的 config 重算。 |
| `message_id`，32-lowerhex，store 内 unique | 首次 capture 生成一次 `Guid.NewGuid().ToString("N")` 并同事务保存；不是新的业务身份算法。 |
| `state`, `revision` | 四种状态及既有 revision-CAS 转移方式。 |
| `expected_session_head`, `rendered_observation` | bind 时冻结的目标 exact idle base 与实际传给 `SendAsync` 的最终文本。 |
| `observation_address` | 仅 Delivered 保存 proof 返回的地址；Bound 不另存可选地址。 |
| `quarantine_code` | 仅 Quarantined 的代码拥有诊断类别，不存任意异常正文。 |

recipient、subject、body、source/ordinal 从关联 artifact 读取；排序再 join `action_capture.capture_sequence`。角色 artifact 始终保持 `Unrouted` 的不可变字段，不套用 Codex terminal 清理 body 的逻辑。`inReplyToMessageId` 和 evidence quote 继续只作既有 provenance；本期不新增信件线程或回信关联协议。

capture request 携带已解析的角色目标和发送者名字；transaction 不调用 provider、不拿 target TurnLock。`AlreadyCaptured` 必须读取并验证已存结果，**不得补投旧 Unrouted、覆盖目标或重新生成 ID**。commit 返回不确定时也先读既有 capture，不凭异常推断“未提交”。只有已提交 capture 才有“不重抽”的持久保证；提取完成但尚未提交就崩溃，允许按既有 admission 再提取。

### 状态约束与转移

| 状态 | 必须存在的事实 | 可自动离开到 |
|:--|:--|:--|
| `Pending` | 已提交 artifact、冻结地址及 message ID；proof 字段为空 | `ObservationBound` |
| `ObservationBound` | exact base + rendered Observation；地址和 quarantine code 为空 | `Pending`、`Delivered`、`Quarantined`，仅由下表 proof 决定 |
| `Delivered` | 保留 binding，并有 exact proof 的 Observation address | 无；终态，rewind 不重开 |
| `Quarantined` | 保留 binding 和矛盾诊断；不伪造 Delivered address | 无自动转移；目标写门禁保持阻塞 |

绑定期间，使用 `MailboxMessage.FromCanonicalEnvelope` 还原消息；通过既有 envelope renderer 生成文本。先持久 bind，再 append。正常 proof request 的 `ExpectedObservationAddress` **始终为 null**，因为 Bound 尚无已证实地址。

| proof / 故障 | 处理 |
|:--|:--|
| `NotAppended` | Bound → Pending，清空 binding；保留原 message ID 和地址。只有这个结果允许重新投递。 |
| `InProgress` / `Terminal` | Bound → Delivered，保存证据地址；不等待 Completion 成功。 |
| `Retryable` | 保持 Bound，本次不推进 Journal，后续重新读取；不忙循环。 |
| `LimitExceeded` / `UnsupportedSchema` / I/O 不可读 | 保持 Bound，阻塞目标写入并报告准确原因；不能当成邮件不存在。 |
| `Conflict` / Journal `Corruption` | store 可安全写时转 Quarantined；否则保留 Bound。均停止目标写入，不重投。 |
| `Abandoned` / 未知结果 | 本请求构造下 Abandoned 不可达；保持 Bound 并阻塞，按不变量违例诊断，不能当 NotAppended。 |

每个目标跨所有 sender 最多一条 Bound；检查与 bind 都持有该目标 TurnLock。发现多条是矛盾，不能依序猜测投递。

### Schema、限额与升级

将 delegation SQLite 从当前 V3 升到 V4，扩展 strict schema、snapshot reader/validator、durable-write confirmation 和离线 upgrader。转移的确认不能只检查 store revision，还须验证对应附表 row 的最终事实。

附表数量不超过既有 artifact 数量上限；邮件文本沿用 `GalateaMailboxBounds`。capture 容量检查须计入新增字段及将来冻结 envelope 的字节预留，避免已经接受的邮件到 bind 才发现无空间；不借用 Codex route 的 queued/task/reply 配额。达到限额时整个 capture 失败，禁止记录已 capture 却丢掉角色 row。本期不增加自动清理或留存策略。

离线 V3→V4 升级只增加空附表和新 schema 元数据，**不把历史 Unrouted 按当前 roster 重新解释为待发邮件**。普通 open 只接受 V4；已有 Codex capture、thread、FIFO、lease、恢复事实保持一致。升级工具延续 [现有 Upgrade](../../prototypes/Galatea/GalateaDelegationSqliteStore.Upgrade.cs) 的停服锁、backup、dry-run、显式 apply、strict reopen；工具实现与真实实例执行分开验收。

## 5. Relay、统一门禁与唤醒

### Store 发现与锁责任

relay 借用 supervisor-owned stores，新增按目标查询 Bound/Quarantined 的窄访问方法；gate 必须同时看见这两类记录。不得另开 store owner，不得通过 `GetSessionAsync(sender)` 发现邮件。

本期 gate 在目标 TurnLock 内逐个短查询所有配置中的 source stores；每次只持一个 store 的内部 gate，读完释放，不跨 store 持锁或 await。sender capture 只写自己的 store 并发 signal；**任何持有 sender TurnLock 或 store gate 的调用都不得等待 target TurnLock**。

选择扫描而非缓存索引有明确代价：任一应可读的 source store 不可读，就不能证明它没有给某目标留下 Bound，因此阻塞全部目标的**新 Journal 写入门禁**，保留只读服务和已运行 turn 的收尾。正常、可证明未初始化的 slot 不等于损坏 store。不要静默跳过 unavailable store；本期接受这个可用性范围，不另造持久反向索引。

### 对账必须先于破坏证据窗口的操作

新增一个目标投递对账方法，职责只有查 Bound、执行 proof、更新 source SQLite；不抽取 Action、不调用 provider、不启动下一封信。接入点集中在 [GalateaServices](../../prototypes/Galatea/GalateaServices.cs)：

| 接入点 | 施工要求 |
|:--|:--|
| `ReconcileDurableDeliveries` | 首先核对跨 sender 的角色投递，再处理既有 Note receipt / reply lease；由此覆盖 `ReconcileDurableAdmissionAsync`、attach、普通输入、自动输入和 recovery admission。 |
| `RunTurnAsync` 的成功/失败 settlement | 在释放 target TurnLock 前对账；best-effort cleanup 失败可延期，但下一次 writer 必须被严格 gate 挡住。 |
| `AbandonFailedTurnAndReconcile` | 必须先结算“曾 append”的角色信，再允许 abandon；沿既有前后 reconciliation 调用链接入。 |
| `PrepareAndCommitPopLatestTurn` | 将当前 Note-only 检查扩为同一 delivery gate，防止直接内部调用绕过。 |
| fresh / recovery 的实际执行入口 | 在开始改变 head 前经过同一 gate；核查所有 writer 调用者，不能只在 HTTP 层加检查。 |

gate 未解决 Bound/Quarantined 时，fresh、recovery、abandon、rewind 都不越过。已有活动 turn 内的正常 Journal 追加继续由其 TurnLock 保护；对账应赶在它结束后的下一轮或回退之前。Delivered 后无需再次搜索历史。

这条门禁也解释了为何可以删除 Bound 的可选 Observation address：在线回退总会先记录 Delivered，合法执行不会出现“曾 append 又回到 base，仍被误认作 NotAppended”。离线绕过门禁不能靠加一个可选地址补救。

### 调度与 admission

host 启动 relay 时只开始调度，不等待发送者会话。使用一个有界 signal channel、单个扫描 consumer 和固定周期兜底；默认一秒、测试通过 `TimeProvider` 控制。扫描包含 Pending 和 Bound，只有 Bound 的重启也必须被发现；Quarantined 保持可诊断且不自动推进。start/capture/target attach/turn finish 的 signal 只降低延迟，周期扫描保证漏信号后仍可继续。启动须晚于 store/config preflight 和 runner 注册；停机先停 relay admission，再 drain runner，最后释放 stores。

每次 sweep 对每个目标最多启动一封，使用 `TurnLock.Wait(0)`；忙则跳过，不占住整个 consumer 等待。选择规则：

- 先解决该目标既有 Bound；Quarantined 不绕过。
- 每个 sender→target 取最早非终态队首，按 `(capture_sequence, artifact_ordinal)` 排序，不用 Action address 字符串排序。
- 跨 sender 按队首轮转，游标只存内存、重启可重置；不新增 durable completion sequence 或跨 sender 总序。

```text
发现 Pending / Bound → 校验冻结目标仍匹配 directory
→ GetSessionAsync(target) 按需 attach（允许既有正常 provisioning）
→ target TurnLock.Wait(0)
→ 检查 stopping / maintenance
→ 先执行角色邮件 proof gate；只有 Bound 时也完成结算
→ 若无 Pending 或现有 automatic failure latch 已暂停，则结束
→ ReconcileDurableAdmissionAsync（包含角色邮件 gate）
→ 仅 NoRuntimeRequired { Phase: Idle } 可继续
→ 默认 connection + PrepareFreshTurnAdmissionAsync
→ StartInboundMailTurn(冻结的 MailboxMessage, internal delivery binding)
→ runner.Start 成功后转交锁；失败由 caller 清理 live turn 并释放锁
```

未能完成正常 provisioning、Busy、recovery-required 均保留 Pending。后台**不接纳 `FailedTurnMustBeAbandoned`**，也不自动恢复 uncertain Completion；HTTP inbound 保留原有显式调用政策。可以共享 start/runner 交接代码，不要为“复用”合并两个 caller 的 admission 选择。

internal delivery binding 只需关联 sender store、dispatch ID 和预期 revision，放在 `GalateaFreshInput.InboundMail` 的内部上下文并由 live turn 携带；HTTP 不提供它。在 `RunRecapGridFreshSendAsync` 已取得 `ready.GoverningSetup.Head` 和最终 `prompted`、现有 lease/Note binder 所在的 `SendAsync` 前位置，完成 Pending → Bound。不要在 admission 之前绑定旧 head，也不要同时在 FreshInput 和 LiveTurn 各维护一份可变投递状态。

### 失败与自动重试节奏

周期扫描自动重试 Busy、正常等待和 proof 的暂时不可用；它不自动重跑已经失败或被 Stop 的生成尝试。

当前 `FinishTurn` 只为失败的 DelegateReply 设置 `AutomaticReplyFailed`，普通 InboundMail 不设置。实施时把**带内部 delivery binding 的 InboundMail** 纳入同一个进程内失败暂停机制；relay 检查现有 `AutomaticAdmissionFailed` / `AutomaticReplyFailed`。admission 异常复用 `RecordAdmissionFailure`；runner 接手后的失败或 Stop 由 `FinishTurn` 处理。可按真实职责调整内部名称，不增加 durable 重试状态或计数器。

因此，append 前失败经 proof 回到 Pending 后，会保持暂停，不在每个 pulse 重新启动；append 后失败仍是 Delivered，但暂停后续自动尝试。成功的人工 turn/recovery 按既有逻辑清除暂停，进程重启按现状重置该内存状态。无 binding 的 HTTP inbound 行为保持现状。

## 6. 配置漂移与运维前提

重开时，对当前可见的非终态 row 检查冻结的 `target_user_id`、artifact recipient 与 `target_session_repository_id` 是否仍匹配 directory。不匹配则保留证据、停止该信投递；已有 Bound 还必须挡住原目标的写门禁。Pending 不得按新名字映射到别的用户。

现有 repository identity 由规范化 sessionDir 派生，能发现路径变化，**不能发现同路径换仓**。如果把整个 sender 从配置删除，host 也不会再打开其旧 outbox，无法仅凭新配置自动发现遗漏。

所以本期约束是：改变相关 userId、characterName、sessionDir、delegationStateDir，删除角色或替换仓库前，先停服，在**旧配置和全部旧 stores** 下完成只读盘点；仍有 Pending、Bound 或 Quarantined 就保留旧身份并先结清或显式处置。禁止删除文件来“解决”未决邮件。没有 pending 的身份迁移 UX、历史配置 manifest 和任意旧目录发现机制均不在本期。

真实 ignored state 迁移须另行执行停服、备份、dry-run、显式 apply 和 reopen。本文没有读取或记录本地实例凭据，也不把设计完成写成真实实例已升级。

## 7. 最小施工切片与验收

### 切片 A：一条可恢复的 A→B 通路

把 directory/prompt、V4 附表与 capture、relay、目标 gate、binder 和失败暂停作为一个可验收的纵向切片。内部可按“schema → capture → gate → runner → prompt”顺序施工，但在全链路可用前不发布“可投递”的 prompt 承诺。

使用两个隔离、已 provision 的测试角色和可控 provider/extractor，沿真实 production capture、admission、runner、Journal 路径验证：一次发送、一次 exact Observation、Delivered；重开后不重复投递。附表状态不要靠测试手改跳过生产转移。

### 切片 B：故障、入口竞争与离线升级闭环

| 场景 | 必须观察到的结果 |
|:--|:--|
| duplicate/reserved 名字，self/unknown，outbound binding 关闭 | 启动校验或既定 Unrouted 策略正确；关闭时不承诺发信，名单仍存在。 |
| roster 含合法反引号等字符、总 prompt 超限、frozen recovery | 数据展示完整，最终限额生效，frozen request 不重渲染。 |
| capture 提交前/后进程退出或确认失败 | 三表全有或全无；先确认读回，已提交结果的 ID/目标/正文不变且不重抽；确认未提交才允许重新提取。 |
| sender 未 attach、双方无浏览器、最后 signal 早于目标 unlock | 重启扫描和周期兜底能投递；不依赖新的浏览器请求。 |
| Busy、未 provision、FailedTurnMustBeAbandoned、uncertain recovery | 不自动丢弃旧轮次；邮件保留，正常就绪后可继续。 |
| bind 前 crash、bind 后 append 前 crash | 恢复保持或回到 Pending，使用原 ID；bind 后由 NotAppended 明确回退。 |
| **append 后、sender settle 前 crash；HTTP/automatic/recovery/abandon/rewind 抢先** | **每个 writer 都先完成跨 sender proof 或被阻塞，不能越过证据窗口。** |
| InProgress / Terminal，随后目标或发送者 rewind | Delivered 仅依赖 append；不重发。 |
| Retryable、读预算不足、unsupported、corruption、多个 Bound、store 不可读 | 按结果表保留或隔离证据；不把未知当空集，不触发错误重投。 |
| append 前失败或 Stop；append 后 provider 失败 | 前者 Pending 且暂停，后者 Delivered 且暂停；连续 pulse 不反复生成。 |
| Bound + 失败暂停，首次 proof 暂时不可用 | 后续 pulse 仍可结算 Bound，但不启动下一封；暂停只挡新生成。 |
| A↔B 同时发送；同 Action 多封；同 sender 跨 Action；多 sender 持续发信 | 无锁环，保持本地 FIFO，跨 sender 不因固定优先级饿死。 |
| visible row 的目标改名/换路径；sender 删除的离线盘点 | 目标漂移不误投；盘点明确发现非终态，不能假称新 config 可自动发现旧 sender。 |
| V3→V4 合成 store dry-run / apply / reopen | 新附表为空，原 Codex 状态与业务顺序一致，无历史 Unrouted 重投。 |

至少给 capture 后和 append 后两个关键窗口做真实子进程退出/重开测试；普通 provider-free 单测负责其余可控竞争。参考 [Note receipt crash tests](../../tests/Galatea.Server.Tests/GalateaNoteReceiptProcessCrashTests.cs) 和 [receipt delivery tests](../../tests/Galatea.Server.Tests/GalateaNoteReceiptDeliveryTests.cs)，但新增测试必须经过跨 sender gate。

完成后更新 configuration、runtime、prompt ownership 与 upgrade runbook，串行运行相关 Galatea tests 和文档检查。live provider canary 只能补充真实调用证据，不能代替上述 crash/去重证明；真实 ignored 实例升级是独立部署步骤。

## 8. 审查裁决与延期条件

本次经过三位独立审阅者的需求质疑、最小架构、语义防守及第二轮交叉质询。争议按代码和失败轨迹裁决，不按票数；收敛后未再扩展第三轮。

| 项目 | 裁决 | 最小理由 |
|:--|:--|:--|
| 第二份角色名单、通用 route/inbox 平台、Codex reply lease 复用 | delete | 没有独立消费者，且混淆已有 authority。 |
| source outbox + exact Observation proof + target gate | keep | 分别防 capture 后丢信、append 后重投、后续输入/回退破坏证明。 |
| 重复 source/ordinal/recipient/subject/body | merge | 由一条 dispatch FK 关联已有不可变 artifact。 |
| 新的四元组 message ID hash | simplify | 首次 capture 一次 GUID 并持久冻结即可；稳定性不依赖定制算法。 |
| Bound 可选 Observation address | delete | 所有在线回退先 settle；可选地址也覆盖不了 append 后尚未写地址的 crash。 |
| 独立 store owner、attach-only discovery、signal-only 调度 | simplify | 借用 supervisor stores，周期扫描保证活性。 |
| HTTP 与后台共用完整 admission policy | delete | HTTP 可显式放弃 failed turn，后台仅 exact Idle。 |
| 新 retry 状态机或持久全局排序 | simplify | 复用进程内失败暂停；本地 capture FIFO 加内存轮转。 |
| target 反向索引、身份迁移平台、手工重投/回执 UI | defer | 分别等扫描开销或故障隔离需求、真实身份变更需求、实际操作消费者出现。 |

相较原稿，删除了 5 个重复业务字段、1 个可选 Bound 字段和 1 套定制 ID 配方；保留 4 种投递状态，并补齐目标 repository locator、现有门禁接线和调度/暂停契约。缩减的是重复表示，不能删掉跨库交付的真实故障边界。

## 9. 实施记录

实现拆为可审阅的独立提交：`a453592e` / `2c6ab5a1` 建立 exact character directory、生成 roster 和 prompt 边界；`d9cba141` / `52ac9634` 建立 V4 sender outbox、显式升级和 fail-closed store read；`a7016278` / `bbccf2c6` 接入 resolver、relay、target gate、fresh binder、生命周期及完整两角色通路。

验证包括：角色名 JSON roster 特殊字符与限额、V3→V4 dry-run/提交边界/复杂快照、119 项 store/extraction 定向测试、以及从真实 HTTP fresh Action/extractor 到 target relay/runner/`SendAsync`/exact inbound Observation/Delivered 的 provider-free spine。最终相关定向集合 168/168 通过；HTTP inbound 仍不携带内部 binding。`Abandoned` evidence 保持 `ObservationBound` 并阻塞，不伪造 Quarantined 或 Delivered。
