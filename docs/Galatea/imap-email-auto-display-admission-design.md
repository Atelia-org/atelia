# IMAP 自动展示准入：设计与施工方案

状态：产品已实施，验收记录见第 8 节。日期：2026-10-07。设计代码基线：`g01/smtp-outbound` / `b47848f1`；施工从 `5ff37bcb` 开始。此前文档审查的只读限制仅属于设计阶段，本次施工及受控测试由用户授权。

最小模型是：**启动配置中的自动展示名单，与同角色已持久受理的外发 email 收件人取并集。只有获准邮件进入角色经历并自动触发回合；陌生邮件只推进收取游标。** 用户已进一步明确：派生许可跟随角色保留，修正 SMTP endpoint 或更换角色邮箱不会撤销原联系人的许可。

本文件是[IMAP MVP 主方案](imap-email-mvp-design-and-implementation.md)的准入规则与施工入口；主方案拥有传输、首次新信基线、MIME 限额、入箱与 Journal proof。直接合入拟实施的 V16 / Delegation V8，不先上线无条件自动展示，也不增加 V17 / V9。

## 1. 需求账本与证据边界

| 编号 | 要求与来源 | 当前消费者 |
|:--|:--|:--|
| U1 | 用户本轮明确：runtime 管理的自动展示名单是 MVP 核心。 | 每角色 IMAP 来信入口。 |
| U2 | 用户本轮明确：角色主动向某地址写信，意味着愿意接收对方回信。 | 已实施 SMTP 外发与拟实施 IMAP 入箱。 |
| U3 | 用户本轮明确：角色自主查信、取信延后。 | 本轮不增加角色操作或查询协议。 |
| U4 | 用户此前明确：首次只收启用后的新信；持久化后自动触发来信回合。 | 此次将自动回合限定为获准邮件；首次基线不变。 |
| U5 | 用户本轮澄清：派生联系人许可跟随同一角色的持久 store 保留。 | SMTP endpoint / 邮箱变更不撤销许可，不跨角色或 store 查询。 |
| C1 | CaptureActionBatch 同事务创建 action_capture、outbound_mail 和真正外发 email 的 smtp_mail_outbox。 | 派生联系人直接读取已有事实，无第二联系人状态。 |
| C2 | SMTP 状态管理发送过程；现有 Unknown 测试覆盖远端接受后、本地结果提交前中断。 | 不能等 ProviderAccepted 才承认联系人，也不能因收件规则重发 Unknown。 |
| C3 | 当前 Quarantined 阻断 writer；已有 relay 使用 TurnLock 与精确 Journal proof。 | 正常筛选不进入故障 gate，获准入箱后继续已有投递证明。 |
| C4 | 当前 root V15、Delegation V7；IMAP V16 / V8 / Observation v5 均未实施。 | 同次格式升级，无已发布 IMAP 兼容义务。 |
| P1 | 本方案默认：许可长期跟随保留的正常外发受理事实；名单/联系人变化只影响尚未裁决的 UID。 | 无 TTL、撤销、历史重判或取信消费者。 |
| P2 | 本方案默认：未知信正文不持久、不投射；原件留服务商，只提交单调 cursor。 | 无逐封筛选审计 API，不为诊断新建持久账本。 |

U 是用户决定，C 来自源码/测试，P 是明确默认。主 IMAP 文档是同源提案，不是独立需求证据。运行模型为单 host、每角色独占 SQLite owner、单个不重叠 poll、启动配置冻结；网络/正文投影不持有事务。没有游标倒退、陌生信历史重判或跨 store 联系人搜索。scope 争议已由 U5 结束，没有待用户批准的实施前置产品问题。

证据入口：[capture 同事务](../../prototypes/Galatea/GalateaDelegationSqliteStore.Transitions.cs)、[SMTP outbox 与引用验证](../../prototypes/Galatea/GalateaDelegationSqliteStore.Smtp.cs)、[Unknown 中断测试](../../tests/Galatea.Server.Tests/GalateaSmtpOutboundTests.cs)、[Quarantined writer gate](../../prototypes/Galatea/GalateaCharacterMailDelivery.cs)。

## 2. 配置与唯一地址比较规则

拟 V16 的 `characters[].email.imap` 增加 `autoDisplaySenders`，缺省 `[]`；两个许可来源固定启用，不增加选配策略组合。

```json
{
  "imap": {
    "host": "imap.example.test",
    "port": 993,
    "tlsMode": "implicit",
    "autoDisplaySenders": ["friend@example.test"]
  }
}
```

数组最多 128 个，只接受既有 GalateaExternalMailAddress 支持的单个 ASCII 地址；拒绝重复项、域名通配符、正则和任意 expression。名单重复与入信匹配使用同一规则：**domain ASCII 不区分大小写，local-part Ordinal 精确**。不去掉 plus-tag、不合并别名、不修改发送地址或已有 artifact。[RFC 5321 §2.4](https://www.rfc-editor.org/rfc/rfc5321.html#section-2.4) 支持此边界。

From 只取 MimeKit 解析出的单个 mailbox address。多 From header、多地址、非法/超限地址拒收，不选第一个，不从 display name、Reply-To 或正文签名回退。命中不升级成 Player / Character identity，Observation 仍是 runtime 报告的外部声明。

名单随内部启动快照冻结，修改后重启；没有热更新 API、角色管理工具或列表展示。名单不进入 SMTP / IMAP accountReference 指纹，增删名单不重建 UID 基线、不改变发送身份；其内容和解析错误原值不进入角色投影或日志。

## 3. 派生联系人只有一个事实源

```text
Allowed(from) = ConfiguredAutoDisplaySenders.Contains(from)
             OR ExistsAcceptedOutboundEmailInThisRoleStore(from)
```

事实源只有同角色 store 的 `smtp_mail_outbox`。仅认可该 owner.CharacterId 对应的正常 `smtp:<characterId>:` 引用；排除 blocked / offline，不比较当前账号指纹，不读取发送状态或正文。

**受理边界是正常 SMTP outbox 行同 capture 已提交。** 抽取候选、叙事中提到地址、Codex/站内邮件、旧无 SMTP outbox 的 Unrouted、SMTP_DISABLED / NO_SENDER_BINDING 等 blocked 引用均不产生许可。正常引用的 Pending / Attempting / ProviderAccepted / DefiniteFailure / OutcomeUnknown 均可产生许可：规则表达角色选择通信对象，不表达发送成功。网络失败和捕获时无可用绑定不能按同一 DefiniteFailure 状态排除。

回信可能在 ProviderAccepted 落库前到达，也可能对应 Unknown；不等待发送终态、不监听成功事件、不建联系人表/持久缓存/回放。旧 V7 正常 outbox 可直接作为依据，无历史提取或旧发送重放。角色 Undo 不撤销已有外发受理事实。

许可长期跟随同一角色的持久 store，换邮箱、SMTP/IMAP endpoint 或授权码不撤销；换角色/store 不跨仓继承。既有 SMTP 捕获指纹、换 endpoint 拒绝旧队列、Unknown 禁止自动重发的合同完整保留：接收意愿与发送工作身份各有自己的责任。

### 窄查询与标准索引

禁止每封入信通过 ReadSnapshot 装入全历史 subject/body。查询只返回 bool；一个普通 recipient 索引，没有新列、地址回填、自定义 collation 或准入来源字段：

```sql
CREATE INDEX ix_smtp_auto_display_sender
ON smtp_mail_outbox(recipient COLLATE NOCASE);

SELECT 1
FROM smtp_mail_outbox
WHERE from_character_id = $character
  AND substr(sender_account_reference, 1, length($smtpPrefix))
      = $smtpPrefix COLLATE BINARY
  AND recipient = $from COLLATE NOCASE
  AND substr(recipient, 1, instr(recipient, '@') - 1) = $local COLLATE BINARY
LIMIT 1;
```

`$smtpPrefix = "smtp:" + owner.CharacterId + ":"`，参数化精确前缀，不用通配 LIKE。已有打开验证确保 owner、引用语法及 outbox/capture 一致性；新增查询仍使用这些边界，读失败或校验错误不能视为“陌生人”。NOCASE 缩小 ASCII 候选，BINARY local-part 保留精确语义。V8 strict schema 新增索引预期，并针对该索引用 index_xinfo 核对 NOCASE，不建设通用索引框架。

设计阶段的内存 SQLite 原型已验证 7 个地址/作用域样本、cursor-only 提交顺序及索引 SEARCH；施工阶段进一步以真实 store 和故障注入验证，见第 8 节。

checkpoint COMMIT 不明的确认采用窄 checkpoint 后态读取；现有 ExecuteWrite 在不明提交时调用 ReadSnapshotCore，不能直接作为该操作的确认路径而装入无关正文。沿用 owner、异常分类和精确后态原则，只补这个 domain 操作所需的读回，不泛化写入平台。

## 4. 一次有界拉取，事务内裁决

```mermaid
flowchart LR
    A[既有有界 UID raw MIME 拉取与解析] --> B{名单或正常外发受理事实}
    B -->|未获准| C[原子推进 cursor；丢弃瞬时内容]
    B -->|获准| D[正文投影 / 校验]
    D --> E[Pending 或 MIME Rejected 与 cursor 同事务]
    E --> F[既有 relay / Journal proof / 自动回合]
```

复用主方案唯一 raw 路径：UID size summary + 最多 raw-limit+1 的 partial BODY.PEEK，raw 2 MiB、MaxMimeDepth=32、每 poll 16 封等限额不变；size 不能替代实际字节上限。解析 From 后、访问/解码正文和生成 MailboxMessage 前准入。未知 raw 可以短暂存在内存，但不得进入持久正文、日志、receipt、status、Journal、recap 或 Note。删除独立 HEADER→BODY 两次请求；减少未知下载量的优化延后。

读完合法 From 后只调用一次最终 store 操作，在 owner gate 的短事务内核对预期 accountReference / UIDVALIDITY / cursor / revision，读取启动名单与 outbox bool：

- 未获准：只 CAS 前移 checkpoint 的 cursor/revision，提交后丢弃 MIME/raw；不写 external_mail_inbox、不创建 messageId、不占 Pending 名额、不唤醒角色。最近 poll 的 filteredCount 可在内存计数。
- 获准：返回 true，不前移 cursor；事务外解码/投影正文，再将 Pending 或明确 MIME Rejected 与 cursor 同事务提交。Allowed 只在内存，无持久阶段或来源证明副本。
- 容量满：可已读取这封有界 raw 用于分类；首封获准信无法入箱时停止，不前移、不误判拒收、不越过它处理后续 UID。

不增加预查/复查两条路径。capture 与最终准入使用同一 owner gate，提交顺序就是因果边界，不比较外部 Date 与本地发件时间。发件先提交则此次查询看到许可；未获准 cursor 先提交则该信保持过滤，后续新信才受新许可影响。运行中名单冻结且正常受理事实不撤销，因此 true 到正文入箱无需第二份授权状态。

### 游标就是陌生信的提交证明

已越过的 UID 不自动回头。新增名单或后来主动写信不补投此前陌生信；删除名单不撤销此前已入箱的 Pending/Bound/Observed。未提交收件事务的 UID 在重启后重新裁决。立即暂停新自动处理使用 IMAP 开关；Bound proof gate 继续有效。

| 故障窗口 | 最小处理 |
|:--|:--|
| 发件候选或 capture 回滚 | 无正常 outbox 提交事实，不产生许可。 |
| 远端收信但本地 Attempting/Unknown | 许可已随 capture 存在，不重发 SMTP。 |
| 未获准 cursor 已提交后崩溃 | 从 cursor+1 扫描，不再处理该信，不需要逐 UID 行。 |
| cursor COMMIT 结果不明 | 保持 owner gate，关闭原 connection，窄读相同命名空间 checkpoint 的预期后态；确认提交则继续，确认原态可重新裁决，不明/冲突停止，不猜失败。 |
| 获准、正文入箱前崩溃 | cursor 未前移，下次重新有界读取/裁决。 |
| 获准 row+cursor 已提交、relay 未启动 | Pending 保留，继续既有 proof，不建立第二个来信 Observation。 |
| UIDVALIDITY 改变 | 沿主方案阻断；显式 rebaseline 用新 validity / scanUpperUid 跳过当时旧信，不回退旧命名空间或复活过滤信。上界优先真实 UIDNEXT−1，缺失时只读最高现存 UID。 |

只跨越确认不存在的 UID 空隙，不越过未处置获准 UID。获准 inbox 的 UID 唯一键、内容冻结及 Journal proof 保留。MIME malformed/超限仍按主方案写正常 Rejected；策略过滤不增加 Filtered、Held 或 Quarantined 状态。Quarantined 只属于投递证明异常，会阻断 writer；陌生信不进该 gate。

原件留服务商，不置 Seen、不移动删除。当前没有逐封筛选历史、待审 UI 或重新放行 API；将来确需这些能力时再定义消费者和历史取回语义。

## 5. 延后范围与真实性边界

延后自主查信/取信、角色管理名单、网页待审/放行、筛选 LLM、主题列表/摘要、联系人撤销/黑名单、TTL、线程许可、持久唤醒配额及独立头部下载优化。触发条件是用户明确需要自主操作/历史重判，或实际获准流量/未知下载成本成为问题。

地址名单降低陌生流量进入角色注意力的机会，不构成强身份认证或总唤醒上限。From 可伪造，获准联系人也能发垃圾或高频邮件。未来使用 Authentication-Results 需验证收件服务商信任边界，不能信任任意自带字段。[RFC 8601 §1.2](https://www.rfc-editor.org/rfc/rfc8601.html#section-1.2)

复用主方案只读 status，至多增加最近 poll 的进程内 filteredCount，不加全历史聚合或正文预览。诊断仅固定阶段/原因码、UID/计数和 exceptionType，不含名单、主题、正文、协议原文、秘密或路径。实施时角色协议只补充“主动外发建立后续新信接收意愿，陌生信不自动入场”的行为，不注入版本、索引或 operator 文档。

## 6. 施工切片与验收

| 切片 | 最小纵向产物与验收 |
|:--|:--|
| A1 配置与真实持久准入 | 接入 IMAP I1 的 V16 数组和 V8 recipient 索引；真实 loader/store 验证静态、正常 outbox、blocked/offline/其它角色。窄查询不读历史正文。 |
| A2 有界 MIME 到角色 | 接入 IMAP I3 单次有界 raw，在正文投影前原子准入；未知仅 checkpoint 变化，获准 row+cursor 同事务，再走 relay/proof。未知 sentinel 不进持久正文或角色上下文。 |
| A3 故障回归与两邮箱 canary | capture/准入提交顺序、COMMIT 不明、重启、名单/邮箱变化、Unknown 不重发及 SMTP/站内/人工入信回归；两受控邮箱按下面步骤真实验证。 |

主要落点：strict reader / email settings；GalateaExternalMailAddress 附近的唯一比较函数；GalateaDelegationSqliteStore.Smtp.cs 窄查询与 schema/upgrade 索引集合；拟 IMAP inbox partial 的 checkpoint 裁决、poller / MIME decoder 的投影边界。SMTP sender、TextExtractor、收据和 SessionJournal 核心不为准入重构。

真实验收使用新的隔离 store/session，两账号足够，旧 canary 邮件由首次基线跳过。第 1–3 步关闭宿主 SMTP，用普通邮件客户端发送受控测试信；第 4 步再启用宿主 SMTP，不能以 blocked 捕获冒充正常受理：

1. 两账号 baseline Ready；接收角色空静态名单、没有向对方的正常 outbox。寄唯一新信，确认 cursor 前进、无 inbox/Journal 正文、Seen 不变。
2. 加入对方静态地址，重启保留 checkpoint；寄另一新信确认入 Journal/完成回合，之前过滤信不补投。
3. 清空静态名单，核查第 2 步没有自动回信产生联系人；寄新信仍应过滤。若已提前回复，事实不得删除或伪称不存在，改用新隔离实例重做来源隔离验证。
4. 启用 SMTP 并重启保留 checkpoint，再让接收角色真实受理向对方的外发 email；空静态名单下接收一封唯一新信，应由派生许可入场；冷重开不新增 Observation。

静态阶段要求角色只确认内容、不回复，并核查 outbox，否则两个许可来源的证据会混合。多角色不串用、建立联系人后其它未知 From 仍拒绝、正常绑定网络失败/Unknown、domain/local-part/plus-tag、背压、缺省名单与配置严格性由合成测试覆盖。

.NET 检查按主方案串行 `--no-restore -m:1 -nr:false`。第 8 节区分代码、离线验证、真实邮箱测试与长期实例接入；本节保留施工验收要求。

## 7. 两轮辩证审查与裁决

三位 reviewer 分别担任 Demand skeptic、Minimal architect、Semantic defender。Round 1 独立读同一草案/账本，Round 2 steelman 对方的 crash、scope、下载和历史反例；主线程核查代码与内存 SQL，不按票数裁决。scope 由用户 U5 决定，其余已收敛，无第三轮未决争议。

| Verdict | 结论与保留理由 |
|:--|:--|
| keep | 正常 capture 提交即许可；等 ProviderAccepted 会拒绝“远端接受、本地 Unknown”的回信。保留 owner、地址语义、原子 cursor、获准 inbox 与 Journal proof。 |
| simplify | 绑定范围改角色 store；三键索引缩 recipient 单键；一次最终事务、一次既有有界 raw，保留因果和资源上限。 |
| delete | Filtered 枚举/陌生逐 UID 行、admissionSource/dispatchId 两持久说明字段、双命中优先级、独立预查询、强制 HEADER→BODY 及第三测试地址前置条件。 |
| merge | 准入直接合入未实施 V16/V8 和 IMAP poll/inbox；SMTP outbox 是唯一联系人事实源。 |
| defer | 自主查取信、待审/历史重判、TTL/撤销/线程、筛选模型、认证平台、唤醒配额、下载优化，有消费者再建。 |

消除的矛盾：主方案所有合法新信自动入场；发送 descriptor 被草案扩成社交许可身份；把陌生字节进内存误当角色已经注意；把诊断行误当游标恢复必要条件。初稿的两个准入说明字段和一个新状态均删除。最终增量是一个配置数组、一个标准索引/窄查询、一个正文投影前的原子准入操作；新增角色交互协议、联系人表和持久筛选状态均为零。

## 8. 2026-10-07 施工与验收记录

本轮按 bounded-delegation 分为配置、持久模型、角色投递和传输/MIME 四个包，主线程集成 Host/HTTP/operator 并串行验收；独立 reviewer 根据实际代码指出接缝并复核修正。

产品入口已完成：

- [V16 配置合同](../SessionJournal/current/contracts/galatea-root-config-v16.md)与 strict reader/loader/bootstrap；静态名单冻结，SMTP / IMAP 引用同一不可变账号对象，两个窄 policy 视图不复制凭据、不重写现有 SMTP 协议。
- [V8 持久实现](../../prototypes/Galatea/GalateaDelegationSqliteStore.Imap.cs)：静态 ∪ 同角色正常 outbox；未知仅 cursor，入箱/拒收与 cursor 原子提交，COMMIT 不明窄读后态。两个表、一个普通 recipient 索引，没有联系人表/逐信 Filtered 账本。
- [传输](../../prototypes/Galatea/Mailbox/GalateaImapTransport.cs)、[唯一 MIME 路径](../../prototypes/Galatea/Mailbox/GalateaImapMimeDecoder.cs)、[poller](../../prototypes/Galatea/Mailbox/GalateaImapPoller.cs)：MailKit 4.18.1，未选中 INBOX 的 STATUS→EXAMINE/UID/BODY.PEEK partial；EXAMINE 必须自行给出相同 UIDVALIDITY，省略 UIDNEXT 时只能沿用确切 STATUS 值，不推算。额外在库分配 literal 缓冲前拦截忽略 partial 的超限响应。先 HeaderList 取 From/准入，获准后才建立 MIME 正文树。
- [共用 proof](../../prototypes/Galatea/GalateaCharacterMailDelivery.cs)和[单 relay](../../prototypes/Galatea/GalateaCharacterMailRelay.cs)：闭合 origin、Observation v5，混合 Bound/Q 进入所有既有 writer gate，公平来源 FIFO，一次 append；邮件不额外领取 recall/notice/receipt。失败或用户停止后暂停新自动 admission。
- [纯读取 status](../../prototypes/Galatea/GalateaHostService.Imap.cs)及[离线 rebaseline](../../prototypes/Galatea/GalateaImapRebaseline.cs)。维护写打开不顺便恢复 SMTP Attempting；必须核对五个 preview 值才 CAS 应用。全局开关默认关闭，停服先取消/排空 poll 再释放 owner。

真实 owner store 与合成协议覆盖：联系人地址/作用域/发送终态、V7 显式升级与历史合同、capture/准入提交顺序、事务不明和 rollback、索引归属/NOCASE/非 partial、UID 空隙/退步/上限/vanished、未知 sentinel 不进持久正文或模型、背压、MIME/charset/附件/40层 nesting、名单变更/邮箱变更、混合 Bound/Q、Observed 后 Undo、生成失败/用户停止、无副作用状态、维护和关闭。真实 TLS fake server → production poller → owner store → relay → accepted runner → Journal 的连续测试通过。

Debug 非 Live 全集：**1907 passed、2 skipped、0 failed**；两个 skip 是原有的显式真实 Codex gate。运行入口：

```bash
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false \
  --filter 'Category!=GalateaLabLive&Category!=GalateaNoteLive&Category!=GalateaEmailLive&Category!=GalateaConnectionStateLive&Category!=GalateaImapLive'
```

Release 最终针对性回归：**379 passed、0 skipped、0 failed**，包含真实 TLS 协议、持久准入、共用来信、SMTP 和迁移。完整 Debug 回归后增加的 STATUS/EXAMINE 与 Open 阶段异常案例均包含在此次 Release 复验。

独立复核发现并已修正：Subject wire 展开后再解码避免吞掉隐藏换行；runtime 普通 snapshot 不追加读取收件历史正文；内部绑定/Host projector/RecallBarrier 的 V5 接缝；停止暂停不被 settlement 清理；poll 中 UIDNEXT 退步即阻断；recipient 索引严格归属正确表；STATUS 缓存不能掩盖 EXAMINE 缺少 UIDVALIDITY；已有 checkpoint 在 Open 阶段遇到明确 namespace 变化/UIDNEXT 退步也持久 Block。文档检查目前只有 4 条施工前已存在的 AgentControl 历史断链，本次无新增诊断。

真实两账号 canary 在基线阶段停止，尚未发送测试邮件：QQ 建立 Ready（cursor=9），126 返回 `IMAP_INVALID_UID_METADATA`。独立只读 probe 再确认：QQ STATUS / EXAMINE 的 UIDVALIDITY=1667961098、UIDNEXT=10，INBOX=1；126 两者 UIDVALIDITY=1、UIDNEXT 均缺失，INBOX=4。probe 不作 SEARCH/BODY/SMTP，不改变 Seen 或原失败 ledger。数量及最高现存 UID 不证明 UIDNEXT；126 不建立猜测基线。用户随后明确本轮完成 QQ 验收、126 兼容另开一轮，因此 I5 两账号完整验收延期。

QQ 独立真实 canary **1 passed / 0 failed，耗时 188 秒**；原失败 ledger 保留，另用 `ledger-qq.json`，完成后 guard 禁止重跑。5 次 SMTP 均为 `SMTP_DATA_ACCEPTED`：4 封受控对象寄给 QQ，1 封 QQ 角色的正常原 Action→capture→outbox→consumer 外发。QQ 四封新来信中，空名单与移除名单后的两封仅推进 cursor，静态名单与派生许可各产生一条 Observed；两条 exact Journal proof、4 次 Seen 不变、未知 sentinel 隔离、冷重开无新模型调用/Observation/SMTP 消费全部通过。126 在此路径仅作 SMTP 控制对象，没有调用其 IMAP。

验收使用 production network transport、owner store、poller、relay、runner、Journal；completion 为确定性“不回复”，contact extraction 为确定性 exact 原 Action。它证明运行时链路及准入，不证明真实 LLM 判断或自主回复。重现入口要求私有配置与全新隔离 guard；既有 Completed / Failed / Running 不得覆盖后盲重试：

```bash
ATELIA_GALATEA_IMAP_CANARY_CONFIG="$PWD/.atelia/imap-mvp-canary/config.json" \
  dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj -c Release --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~QqOnlyReceiver_AdmissionAndRuntimeObservation_AreDurableAndReadOnly'
```

长期 gpt/cyber 实例配置、旧 store 升级及真实 LLM 自动理解/回复不由隔离 canary 代替；自主查信、HTML 转换、强认证等延期维持第 5 节边界。

## 9. 126 缺失 UIDNEXT 的适配

第 8 节记录的是首轮验收的实际限制。后续用户授权继续适配 126，目标是让既有准入、持久化与角色投递链路接收新信，不引入邮箱厂商配置、联系人表、角色查取信协议或存储迁移。

适配只改变扫描边界的来源。`IGalateaImapConnection` 保留真实可空 `UidNext`，增加 `ReadScanUpperUidAsync`：优先返回真实 `UidNext−1`；两处服务端元数据均缺失时，只读 `UID FETCH 4294967295:* UID`，取得最高现存 UID。标准 MailKit `UniqueIdRange(Max,Max)` 生成这个范围；[RFC 3501 §6.4.8](https://www.rfc-editor.org/rfc/rfc3501.html#section-6.4.8) 的反向范围规则使它仅匹配最高现存 UID。响应须有有效 UID 且对应当前最后一个序号；非空但缺结果、重复或序号不符都延后，不把缺结果当空箱。明确空箱的上界为 0。

最高现存 UID 不是 UIDNEXT 的推算值：历史最高 UID 的邮件可能已删除。它仍能作为一次观测的扫描上界，因为同 UIDVALIDITY 内新分配 UID 严格递增且不可复用；[RFC 4549 §4.3.1](https://www.rfc-editor.org/rfc/rfc4549.html#section-4.3.1) 同样以已知最高 UID 发现后续邮件。首次保存这个上界跳过旧信；之后仍按有界 UID 区间扫描。删除导致上界低于持久 cursor 时保持 cursor，绝不回退或误报 UIDNEXT 退步。取得真实 UIDNEXT 时，既有单调检查仍有效。

所有协议等待之后重验 UIDVALIDITY，已有 checkpoint 在等待之前也先检查已知身份变化。namespace 改变仍持久 Block，不自动重建基线。仅支持持久 UID；不增加 MailKit 没有公开支持的 UIDNOTSTICKY 协议观察器，也不宣称主动侦测。符合 [RFC 4315 §3](https://www.rfc-editor.org/rfc/rfc4315.html#section-3) 的非持久 UID 邮箱会在下次选中时改变 UIDVALIDITY，被既有检查阻断。

两个协议边界保留在同一实现中：fallback 采样后若真实 UIDNEXT 出现，它必须高于本轮已采样上界；否则不能用矛盾元数据前移 cursor。普通 SEARCH 的请求用最多 256 个显式 UID 构成固定数字范围，避免 `uint.MaxValue` 被 `UniqueIdRange` 序列化为 `*` 后，在尾信删除时反向覆盖历史邮件。`Max:*` 只用于单尾 UID 探测，不用于扫描新信。

离线 `operator rebaseline-imap` 与 receiver 复用同一个上界方法。五个预览值中的 `newUidNext` 改为 `newCursor`，apply 参数直接使用 `--new-cursor`，允许 0..uint.MaxValue；不留旧参数别名，不执行 `upper+1`。仍只解除 `IMAP_UIDVALIDITY_CHANGED`，要求精确核对旧 checkpoint、当前远端 namespace 和新 cursor，并保留旧 inbox 与无关 SMTP Attempting。配置 V16、store V8、Observation v5、accountReference 及准入合同均不变。

### 9.1 实际验证

独立 source review 检查并促成修正上述两处因果边界，没有残留阻断。真实 TLS fake server 的 **35 个协议 case 全部通过**，包含原生元数据优先、STATUS 缺 next 而 EXAMINE 提供、双缺 next 的尾 UID、空箱/消失/并发新增/重复/缺 UID/非末序号/拒绝、来源切换、namespace 变化和最大 numeric SEARCH 端点。receiver 与真实配置/owner/store 的 operator cases 覆盖冷重开、新信准入、删除后保持游标、五值 CAS、错误源数据不写、旧 inbox 和 SMTP Attempting 保留。

Release 非 Live 全集 **1963 passed / 4 skipped / 0 failed，耗时约 2 分 5 秒**。四项 skip 是一项显式真实 Codex gate 和三项要求 DEBUG build 的既有 extraction diagnostics 测试；本轮没有改动这些诊断路径。重现入口：

```bash
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj -c Release --no-restore -m:1 -nr:false \
  --filter 'Category!=GalateaLabLive&Category!=GalateaNoteLive&Category!=GalateaEmailLive&Category!=GalateaConnectionStateLive&Category!=GalateaImapLive'
```

只读双账号 probe **1 passed / 0 failed**，每账号两次独立连接，不读取正文或 flags、不进行 SMTP 或修改 ledger。QQ 的 UIDVALIDITY=1667961098、UIDNEXT=14，末尾 UID=13 / 12；126 的 UIDVALIDITY=1、UIDNEXT 仍缺失，末尾 UID=1791351821 / 1791351820。两账号的末尾观测均跨连接一致，间隙均为 1；这是具体观测，不代替持久 UID 的协议前提。probe 对有 native next 的 QQ 只取最后至多两个临时序号的 UID；只有缺 native next 的 126 强求单尾查询。首版探针对 QQ 同样强求备用尾查询时失败，不能据此声称 QQ 的 fallback 已通过。

双 receiver 真实 canary **1 passed / 0 failed，183 秒**，使用新独立 `ledger-dual-horizon.json`；原 Failed / QQ Completed ledger 保留。两个 baseline Ready 后才发件，六次 SMTP 均为 `SMTP_DATA_ACCEPTED`；六个新 UID 中三个仅推进 cursor，三个获准入箱并形成 Observed（QQ 两条、126 一条），三个 exact Journal proof、六次 Seen 不变、冷重开无额外模型调用/Observation/SMTP 消费全部通过。正常角色外发仍从原 Action→production reconciler→outbox→consumer 提交并建立派生许可；IMAP 准入与投递复用 production transport / poller / owner store / relay / runner / Journal。

六封新信的 UID gap 均为 1，包括 126 的默认过滤与静态获准来信。保留 256 数字跨度 / 16 封的现有限额，不增加未经需要的稀疏 UID 扫描策略；极稀疏 UID 邮箱仍可能需要多轮推进，这是当前有界轮询的吞吐限制。未知 marker 与真实授权码没有写入可持久角色工件，私有配置/ledger 未加入 Git。文档检查仍只有四条已有 AgentControl 历史断链，没有新增诊断。

精确运行入口（需私有配置与全新独立 Prepared guard；Completed / Failed / Running 不得覆盖后重跑，也不要批量运行整个 Live category）：

```bash
ATELIA_GALATEA_IMAP_CANARY_CONFIG="/absolute/private-canary-config.json" \
  dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj -c Release --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~DualReceiversAfterUidHorizonAdaptation_AdmissionAndRuntimeObservation_AreDurableAndReadOnly'
```

本轮完成两个真实账号的隔离运行时收件验收，解除此前 126 兼容造成的 I5 延期。completion/extraction 仍为确定性测试，没有验证生产 LLM 的邮件判断或回复；长期 gpt/cyber 实例没有切换、升级或改写配置。自主查取信、HTML 转换等产品边界继续按第 5 节延期。
