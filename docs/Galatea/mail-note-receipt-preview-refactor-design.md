# Mail / Note 统一操作回执与正文预览重构方案

> 状态：**Implementing，正在按本方案实施与验证**。日期：2026-10-06。源码基线：`40e5b246`。
>
> 本文已完成 `dialectical-simplification` 的独立审阅与交叉质询，施工契约已收敛。以最终产品代码的简单性为准，允许迁移过程更复杂。用户已授权运行时代码、合成 fixtures、相关文档和 git 提交；实例迁移与部署不属于本次执行。

最终模型：**两个业务 owner 在确认事务中冻结短回执，共用一个 fresh composer 和一个投递流程；每次普通角色轮次最多取 Mail、Note 各一批，用现有 `notices[]` 承载。** 正文展示为首 32、尾 16 个 Unicode scalar 的识别预览；邮件受理和 Note 保存分别表达自己的真实结果。

处理顺序为：角色原文 → TextExtractor 候选 → 同事务业务确认与冻结回执 → 统一选择回执与回信 → 冻结 Observation → 所有参与者绑定相同输入 → append → exact Journal proof。

## 1. 需求与现状证据

### derived [S-RECEIPT-REQUIREMENT-LEDGER] 需求来源决定设计边界

| 需求 / 边界 | 来源 |
|:--|:--|
| Mail / Note 等角色主动提交行为共用反馈路径，回执不再重复长正文。 | 用户当前明确要求。 |
| 以重构完成后的产品代码简单性为准，迁移过程可以复杂。 | 用户当前明确要求；不为省迁移工作量保留两套现行写路径。 |
| 为“已发送 / 已送达 / 发送失败”等最新进展留下扩展位置，本轮不提前实现进展通知。 | 用户认可前轮分析并要求编辑方案；下一轮准备实施当前回执重构。 |
| 提取接纳、邮件受理、Note 保存具有不同资格；业务正文保持完整。 | 当前提取 ACK、Mail capture/outbox、Note Applied 事务。 |
| 确认结果与回执义务原子提交；重开不丢、不重复通知。 | 当前 Note 事务和 crash tests；新增 Mail 采用相同可靠性。 |
| 旧 Journal / Prepared / Bound 有真实消费者；旧 Pending 尚未建立 Observation commitment。 | 当前恢复代码、迁移与投递测试。无已发布下游 API 兼容义务。 |
| 每角色 TurnLock；冻结 source Action；Mail/Note 并行提取并排空；两个独立 SQLite owner。 | 当前 GalateaServices 和领域 store。 |
| 每个 Action 最多产生 Mail、Note 各一批；各领域 FIFO / ordinal 已知，两库 revision 不可比较。 | 当前提取和持久模型。 |

首尾长度、wire 组织和迁移方式是本方案裁决，不冒充用户已指定的参数。邻近设计文档属于背景，其衍生文档不构成独立需求证据。此次接入仅覆盖 Mail/Note；角色状态识别、后台 metadata enrichment 等内部 TextExtractor 不自动生成回执。

### derived [S-RECEIPT-SOURCE-EVIDENCE] 关键代码与实际缺口

| 位置 | 当前行为 / 对设计的影响 |
|:--|:--|
| [TextExtractor.cs](../../prototypes/Galatea/TextExtractor.cs)，L200 | 工具 ACK 只确认候选在内存中接纳，接收方是提取模型，不证明业务生效。 |
| [CharacterNoteSaveReceipt.cs](../../prototypes/Galatea/CharacterMemory/CharacterNoteSaveReceipt.cs)，L24；[CharacterNoteReceiptContent.cs](../../prototypes/Galatea/CharacterMemory/CharacterNoteReceiptContent.cs)，L44–69 | 当前 Note 全文优先、预算不足才只回 IDs；全文进入 `exactTexts[]`。 |
| [GalateaServices.cs](../../prototypes/Galatea/GalateaServices.cs)，L1737、L3046、L3134–3159 | cutoff 预留回执与 fresh 内容选择分处两点；lease / Note 已分别绑定同一 Observation。 |
| [GalateaDelegationSqliteStore.Transitions.cs](../../prototypes/Galatea/GalateaDelegationSqliteStore.Transitions.cs)，L8–139 | Mail capture、outbound 行和内部信 outbox 同事务；尚无发件受理回执。 |
| 同文件 L1258–1323 | 内部信的 outbound 行也可能是 `Unrouted`，但有真实 internal outbox；不能只看 outbound 状态判定失败。 |
| 同文件 L550、L1009–1011；[Recovery](../../prototypes/Galatea/GalateaDelegationSqliteStore.Recovery.cs) | 邮件 terminal / preflight / recovery 会清除正文。下一轮再从当前邮件生成 preview 可能已没有材料。 |
| [CharacterMemorySqliteStore.Transitions.cs](../../prototypes/Galatea/CharacterMemory/CharacterMemorySqliteStore.Transitions.cs)，L389 | 新 Note 回执义务与 Applied settlement 原子提交，是应保留的成功边界。 |
| [GalateaNoteReceiptDelivery.cs](../../prototypes/Galatea/GalateaNoteReceiptDelivery.cs)，L18–55 | exact Observation proof 决定 Delivered；不是 provider 完成或角色理解证明。 |
| [GalateaObservationSchema.cs](../../prototypes/Galatea.Input/GalateaObservationSchema.cs)，L195–239 | notice 已按 kind 分派，V1..V4 共用该验证；没有独立的 schema→notice 集合身份。 |
| [GalateaDelegationState.cs](../../prototypes/Galatea/GalateaDelegationState.cs)，L8；[ObservationLimits](../../prototypes/Galatea.Input/GalateaObservationLimits.cs) | Mail 单批最多 64、Note 最多 16；Mail recipient 最多 1024 UTF-8 bytes，subject 最多 4096。前稿 Note 16 KiB 不能直接覆盖 Mail。 |
| [Transitions](../../prototypes/Galatea/GalateaDelegationSqliteStore.Transitions.cs)，L377、L870；[InternalMail](../../prototypes/Galatea/GalateaDelegationSqliteStore.InternalMail.cs)，L60；[Recovery](../../prototypes/Galatea/GalateaDelegationSqliteStore.Recovery.cs)，L190 | Started 不证明已发送；Codex Accepted 证明远端接纳；internal Delivered 证明收件 Journal append；RESULT_UNCONFIRMED 不等于肯定发送失败。 |
| [MailboxStatus](../../prototypes/Galatea/GalateaDelegationSqliteStore.MailboxStatus.cs)、[共享 projector](../../prototypes/Galatea.Input/GalateaObservationInputProjector.cs)，L9 | 现有状态查询只有聚合结果；projector 不打开 store，历史输入只按冻结内容投影。 |
| [Leases](../../prototypes/Galatea/GalateaDelegationSqliteStore.Leases.cs)，L63、L647；[Snapshot](../../prototypes/Galatea/GalateaDelegationSqliteStore.Snapshot.cs)，L1104 | store 创建 membership 时仍按所有合法 playerText 预留预算；冷重开只检查实际 membership / I，没有该最坏输入假设。 |
| [CharacterMailRelay](../../prototypes/Galatea/GalateaCharacterMailRelay.cs)，L172–210；GalateaServices L1856、L1933、L3945 | relay 跳过完整 durable admission，但经过 PrepareFreshTurnAdmissionAsync；当前该函数没有 receipt 对账。pre-rewind / cleanup 已有 Note 入口，必须覆盖新 Mail owner。 |
| [CharacterMemoryStoreUpgrade](../../prototypes/Galatea/GalateaCharacterMemoryStoreUpgrade.cs)，L22；[SessionJournalEngine](../../prototypes/SessionJournal/SessionJournalEngine.cs)，L185 | 当前升级命令只打开 SQLite，无法判定 Bound 是否 append；已有 provider-free OpenReadOnly 可补 exact proof。 |

提取工具仍是 `emit_send_mail_range` / `emit_character_note_range`。坐标、完整源文本切片、dedup 和提取失败合同不因回执改变。Codex 的实际回信和收件角色的实际邮件正文也保持完整。

## 2. 确认资格与唯一通知内容

### decision [S-RECEIPT-CONFIRMED-RESULT] 领域确认后建立回执义务

| 领域结果 | 回执含义 |
|:--|:--|
| capture 事务建立 Codex 路由或内部信 outbox | `accepted`：“本次邮件提交已受理”。不宣称当前仍等待、已经送达或对方已经处理。 |
| capture 没有有效路由 / internal outbox | `unrouted`：“本次邮件未进入投递队列”。固定结果，不引用异常原文。 |
| Note durable Applied | “本次 Note 内容已保存到 Default MemoPod”。不承诺分类、metadata 或召回。 |
| 候选仅在内存、提取失败、Note 尚未 Applied | 没有成功回执；沿用业务失败与恢复流程。 |
| 零候选 | 不产生角色回执。 |

Mail 结果 MUST 从提交时的路由与 outbox 事实确定，不能从之后会变化的 mail state 推断。受理回执与后续回信是不同事实，不能共用通知状态并让后者覆盖前者。

### spec [R-RECEIPT-ATOMIC-SNAPSHOT] 业务事务冻结有界通知内容

Mail MUST 在 `CaptureActionBatch` 事务中创建回执义务并冻结 `ReceiptBatch`；Note MUST 在 Applied settlement 事务中执行相同的通知创建路径。完整业务正文继续留在原业务生命周期，Mail 正文清理不延后。

回执内容在事务内生成，与业务确认一起提交；提交成功后才成为有效 Pending。随后普通轮次选中并 append 到角色 Journal 才可结算为 Delivered；这里 Delivered 只表示回执进入输入历史，不表示原邮件送达、provider 完成或角色已理解。

冻结 batch 是“这次需要通知什么”的内容 authority。它不接管路由、Memo corpus 或正文 authority，只保存源 Action、全部业务标识、确定结果和短预览。它没有 Markdown 包装、完整原文、原 subject、evidence、随机 receipt ID 或新正文 hash。

`AlreadyCaptured` / `AlreadyApplied` MUST NOT 重新创建义务、重新提取或再次执行业务。提交结果不明确时，existing `ExecuteWrite` 回读判定 MUST 同时核对准确的确认事实和 frozen batch；不能仅查 capture 存在就伪造此次提交成功。

## term `Receipt-Batch` 一次领域确认的冻结通知

只定义 Mail / Note 两个闭合 typed variant：

| Variant | 内容 |
|:--|:--|
| `MailReceiptBatch` | source Action；按原 artifact 顺序的 `{ dispatchId, outcome, recipientPreview, preview }`。 |
| `NoteReceiptBatch` | source Action、PodId；按原 artifact 顺序的 `{ memoId, preview }`。 |

数组位置即领域 artifact ordinal，MUST 是完整批次且保序，不另存重复 ordinal 字段。原 dispatchId / MemoId 及领域 owner 保留原含义，不能用预览区分或重新执行操作。短预览可能相同，这是合法的识别线索碰撞。

Pending / Bound 的持久 snapshot 中全部预览 MUST 是已冻结的 string；nullable 只用于送入 Observation 的预算投影。Delivered 可释放整个通知 payload，不把持久 snapshot 改写成 null 预览模式。

## 3. 预览与内容版本

### spec [F-RECEIPT-SINGLE-PREVIEW] 一个字符串表达首尾预览

统一 helper 从有效原文产生一个字符串：

- 长度 ≤48 个 Unicode scalar：保留完整短文本。
- 长度 >48：`前32 + " …（省略）… " + 后16`。
- 按 `Rune` 或等价方式截取；不切断 surrogate pair。最多 56 个 scalar。
- 不 trim、不归一化、不改写换行、不 HTML-decode、不调用 LLM 摘要；转义交给 JSON / MdJson。

Mail 正文与 recipient 识别标签使用相同 helper。字段明确叫 `recipientPreview`，不冒充完整地址或路由证明；完整业务 recipient 继续用于真正投递。subject 不进入回执。

预览允许空白片段，不能套用“原文必须非空白”的规则拒绝截取结果。本期不引入 grapheme cluster 分段；组合音标、多码点 emoji 可能在边界分开。没有机器消费者需要独立 head/tail 或精确省略数，因此不存这三个字段。

### spec [F-RECEIPT-NOTICE-VERSION] 独立版本化 notice 沿用现有容器

新增严格的 `kind = "action-receipt-v1"`。每份 notice 承载一个领域 batch，直接使用现有 `notices[]`，没有额外跨领域聚合外层。示例中的地址和 ID 是占位符：

```json
{
  "kind": "action-receipt-v1",
  "sender": { "kind": "runtime", "id": "galatea", "name": "Galatea runtime" },
  "receipt": {
    "kind": "mail",
    "sourceActionAddress": "<原 source Action>",
    "items": [
      {
        "dispatchId": "<原 dispatchId>",
        "outcome": "accepted",
        "recipientPreview": "Codex",
        "preview": "<首尾预览>"
      }
    ]
  }
}
```

Note receipt 使用 `kind = "note-save"`、`sourceActionAddress`、`podId`、`items[{memoId,preview}]`；其 kind 已说明“保存成功”，不重复存通用 status。

所有预览字段为必需、nullable string。当前 writer 只生成新操作回执；旧 `note-save-receipt` / legacy receipt 仅由历史 reader 读取，原字段、上限和解释严格保留。

不增加 Observation V5、nullable connectionState 或四类 trigger 的新容器版本。此次有意扩展一个独立 notice variant；旧内容的解码与 exact proof 不因接纳集合扩展而改变。将来调整截取合同应使用新的 notice kind，不能重新解释已冻结的 v1 内容。

### spec [A-RECEIPT-TWO-PROJECTIONS] 整体预算只选择两个投影

一次规划最多选择 Mail、Note 各一个 FIFO batch。两个候选依次为：

1. 选中 batch 的全部 frozen 预览；
2. 所有选中条目的正文、recipient 识别预览一起置为 `null`。

源 Action、全部业务 IDs、顺序、outcome、PodId 始终完整。`null` 只表示本次未展示识别预览，不表示正文为空、丢失或确认失败。选择 MUST NOT 修改持久 snapshot；绑定验证 MUST 精确匹配 frozen batch 的上述两个投影之一。

共享 schema MUST 对同一 I 中全部 `action-receipt-v1` 预览字段检查“全 string 或全 null”，包括 Mail recipientPreview；拒绝单 batch 部分 null 和 Mail full / Note null 的混合投影。各 owner 再验证自己的完整 frozen batch。该新规则不作用于历史 kind；不增加 mode 字段或跨库读取。

选中操作回执 notice 数组的完整 JSON UTF-8 预算定为 **128 KiB**，包含 sender 和包装；还须满足整条 Observation / request 预算。Mail 64 + Note 16 共最多 144 个预览字符串；64 个 dispatchId 固定 68 ASCII 字符。最终字段的最坏转义编码必须以真实 C# writer 验证，不能把这个上界推导当作已完成运行时测试。

128 KiB 是新 notice 集合的严格 codec 上限，由 shared schema / writer 校验；选择 authority 仍只有 fresh composer，不增加回执专用 planner。整体 notice 上限仍为 **16**：本轮选中 0 / 1 / 2 个领域 batch 时，Ready reply 至多分别取 16 / 15 / 14 条；null 预览不释放 notice 槽位。

此前按上述 wire 形状做了保守 ASCII 转义计算：所有源片段用 supplementary scalar，地址 / MemoId 按字符串长度上界构造，完整预览为 **99,837 bytes**，null 预览为 **11,709 bytes**。该样本只验证包装长度上界，不是语法有效的业务 fixture，也不代替最终 .NET serializer 和整体请求验收。

第二候选有真实进度用途：合法回信可含大量控制字符，JSON 编码后接近组合预算；省掉固定长度预览仍可能决定最早 Ready reply 是否放得下。局部预览有界不等于全输入一定放得下。两个投影都失败时，保持义务 Pending，沿用明确的内容规划失败；不遗漏 IDs、不提前 Delivered，也不额外创建仅为回执服务的角色轮次。

## 4. 一次规划与共享投递

### spec [A-RECEIPT-SINGLE-COMPOSER] 选定内容贯穿 cutoff 与 fresh 输入

同一个 fresh composer MUST 决定回执投影和当轮有资格领取的 Ready reply FIFO 前缀。它先取两个领域的最早 Pending，对每个预算候选连同实际 trigger、sender、timestamp、connectionState 和行动文本一起规划。PlayerAction / DelegateReply 若有 Ready，候选必须至少能容纳最早一条，再扩展可共存的连续前缀。完整预览不能与最早 reply 共存时尝试 null 预览，不能先以“回执单独放得下”锁定完整预览。

选择结果作为进程内不可变计划贯穿 admission 与 fresh send，至少包含固定输入事实、选中 receipt snapshots / notices、准确 ReplyLeaseMember[] 与无 recall 的 preliminary I。reply lease 只领取计划指定的 membership；fresh send 不重新读 Pending 或 Ready，不重新采样固定事实。recall 仅使用剩余预算，不反过来重选回执。最终 I 只补入能共存的 recall；所有参与者绑定同一个 I。没有另一个持久 coordinator plan、group ID 或选择状态机。

本轮 MUST 删除 `BeginCutoff` 的内容选择及 `BeginReplyLeaseMembership → RequireRenderableLease` 的“每种合法 playerText 都能 fit”检查。store 保留唯一 membership、Ready / row revision、FIFO prefix、计数、实际 Bound I 的 schema / 字节 / 内容与身份验证；冷重开和 proof 保留原合同。不能以删除重复预算为由削弱存取验证，也不增加可信 plan token。

同轮最多取每领域一批，避免“每轮生成两批、只消费一批”的持续积压；不等待另一个领域完成业务。领域内 FIFO 使用自己的 created revision，两个库的 revision MUST NOT 排序比较。

当前普通 PlayerAction、HeartbeatActivation、DelegateReply 输入接入该路径。InboundMail 保留现有不带 enrichment notices / recalls 的合同；回执等待后续普通轮次。Ready 为空仍不创建 reply turn，也不为回执新增 wake 策略。此方案没有 wall-clock 投递时延保证。

| 已确定的 fresh trigger | 领取 reply lease | 领取 Mail / Note 回执 | recall |
|:--|:--|:--|:--|
| PlayerAction | 有 Ready 时领取所选 prefix | 各域最多一批 | 使用剩余预算 |
| DelegateReply | MUST 非空 prefix，否则返回 Empty、不创建轮次 | 各域最多一批 | 使用剩余预算 |
| HeartbeatActivation | 不领取；已有 Reply 优先激活策略仍由现 automatic coordinator 决定 | 各域最多一批 | 使用剩余预算 |
| InboundMail / 既有轮次恢复 | 不新领取 | 不新领取 | 不新查询 |

Heartbeat 已确定后才到达的新 Ready 留给后续轮次，不把 Heartbeat 重新归类或塞入 reply lease。当前 Memo recall query 继续只选 Reply / DeliveryFailure 作为检索依据；新确认回执不进入 query。使用同一冻结输入作为来源，不表示每个消费者都投影全部 notices。

### spec [S-RECEIPT-CAUSAL-PRESENTATION] DEPRECATED：现行顺序见新条款

此前“回执先于相关回信”的跨轮读法无法由每域一个 FIFO batch 保证；由 @[S-RECEIPT-SELECTED-NOTICE-ORDER] 替代，不引入确认屏障。

### spec [S-RECEIPT-SELECTED-NOTICE-ORDER] 同一输入中选中的历史确认作为前缀

新操作回执作为 notice 前缀，固定 Mail→Note；后续回信保留原 CompletionSequence 顺序。每域至多一份新回执。固定 Mail→Note 是展示顺序，不声称原 Action 的跨领域全局叙事顺序。

这里只保证同一 I 中已选回执前置，不保证每封邮件的确认都在其回信之前。连续 InboundMail 可累积多批 Pending，下一普通轮只取最早一批，但 Ready prefix 可能包含更晚 source 的回信。MUST NOT 因其受理回执尚未展示而阻挡已有 Ready；不新增跨轮 gate、多批追赶或 receipt-only wake。

使用原 source Action / dispatchId 和历史确认措辞“本次提交已受理”，避免把迟到确认误解为新任务或当前等待状态。历史旧 Note notice 的末尾规则继续由旧 kind reader 保留。新 schema 分类 MUST 将操作回执识别为回执，不计作 heartbeat 禁止的 external notice，也不能使只有回执的 DelegateReply 冒充已有真实回信。

### spec [R-RECEIPT-SHARED-DELIVERY] 共用三态、同构行与 exact proof

两 owner 各自维护同构的回执义务行，共用窄存取协议与投递实现：

| 行字段 | 职责 |
|:--|:--|
| source Action，PK / 关联本领域 capture | batch 身份；领域 owner 隐含，不再持久化全局 owner ID。 |
| created revision、state revision | 本领域 FIFO 与 CAS fence。 |
| Pending / ObservationBound / Delivered | 通知投递状态，与业务状态分离。 |
| frozen receipt content | Pending / Bound 持有有界通知；Delivered 可清理。 |
| expected head、bound input、Observation address | 既有 exact 证据；Bound 保存 I，Delivered 保存实际 append 地址。 |

窄接口为 `ReadPending / ReadBound / Bind / Rollback / Complete`；只共享通知 codec、preview 和确实重复的行操作 / proof。领域在原业务事务内生成 batch 并插入义务，不能替换成提交后 enqueue，也不提取通用 SQLite 事务框架。新 Bind 对比 frozen 通知投影，不读取当前 Memo、不从 Mail 当前正文重算。保留轻量业务关系检查：Note 来源确为 Applied、结算 revision 相符；Mail 来源和结果对应不可变 route_class / internal outbox 提交事实，不能要求当前 mail 仍 Queued 或持有 body。

原子范围仍是各 SQLite owner。按固定参与者顺序绑定已有 reply lease、Mail、Note；所有选中绑定成功后才允许 Journal append。任一绑定失败，禁止 append。该机制延续已有 lease + Note 的组合绑定，不新增跨库事务或全局组状态。

| 故障窗口 | 最低处理 |
|:--|:--|
| 确认事务提交后退出 | Pending / snapshot 与业务一起存在；重开不再次执行业务。 |
| 部分 bind 成功，尚未 append | 各 Bound 以原 H/I 得到 exact NotAppended 才回滚 Pending。未知 proof 阻断，不能猜。 |
| append 后只有部分 owner settle | 未结算 owner 以同一个 I 补齐 Delivered，不重复通知。 |
| receipt 已 append，provider 尚未完成 | receipt 可 Delivered；reply lease 仍按原 terminal / abandon 合同保留。两者不能合并结算状态。 |
| 后续 Undo / rewind | 不重新制造已 Delivered 义务，不因恢复/渲染变化重发外部工作。 |

### spec [R-RECEIPT-RECONCILE-BEFORE-HEAD-MOVE] 既有 host 边界覆盖所有回执 owner

共同 Mail / Note receipt Reconcile MUST 接入 `ReconcileDurableDeliveries` 的现有 attach、admission、恢复、正常结算、异常 cleanup 与显式结束路径，并在 `PrepareAndCommitPopLatestTurn` 的 rewind 准备前完成。任一未结算 owner 对账失败或 proof 未知，禁止该 head 变化。否则 append 后部分 settle，再 Undo 到原 H，会使无 ObservationAddress 的剩余 Bound 得到 NotAppended 并重新通知。

所有 fresh 入口在 caller 持有 TurnLock 时，MUST 经现有 `PrepareFreshTurnAdmissionAsync` 执行同一 receipt Reconcile，之后才规划 / claim。此公共边界覆盖未调用完整 durable admission 的 character-mail relay；只替换现有 Note 调用点不足以覆盖它。Setup 在该边界之后、receipt Bind 之前，不另设 coordinator 或新 Setup 入口。

已有 reply lease 的 recovery / settle 保留原入口，位于本轮 claim 之前或原失败 / 完成 cleanup 中。当轮 CutoffFrozen 已领取后，正常 Setup / recall 路径 MUST NOT 再运行会回滚该 lease 的旧 ReconcileActiveLease。Receipt Delivered 与 lease terminal / abandon 的结算资格继续分离。

### decision [A-RECEIPT-PROGRESS-SEAM] 在新 Observation 的组装处保留进展通知扩展位置

本轮 MUST 保留“领域提供通知内容 → fresh composer 统一预算与冻结 → 纯 projector 展示”的职责边界。领域 store 仍是流程状态 authority；确认回执只表达历史确认，不随业务后续状态改写。以后进展通知使用独立 notice，通过原 dispatchId 关联；不把 `action-receipt-v1` 的 outcome 改成可变流程状态。

本轮只有 Mail / Note 确认回执的实际消费者。扩展位置由现有 composer 和 typed notices 的职责分界承担，MUST NOT 为此预建空进展接口、provider registry、通用流程数据库、全局进度时钟、额外 wake 或未使用 wire variant。未来接入所需的通知身份与游标不能假设现有 source Action batch 主键已足够。

### spec [S-RECEIPT-FROZEN-PROJECTION] 最新状态在 fresh 组装时采样，历史渲染只读冻结输入

新状态若以后接入，MUST 在构造新的 PlayerTurnObservation 时读取领域的持久事实，并在同一 fresh 计划里冻结有界快照；“最新”表示采样时已知的状态，不宣称整个 provider 执行期间的实时状态。recall、主模型请求与 Journal 使用相同快照。

projector、CLI / 人类历史展示、Prepared 恢复与旧 Observation 重放 MUST NOT 再读取当前业务 store 或重新采样。流程在采样后继续推进不允许改变已绑定 I。未来进展内容不需要完整正文，也不能要求延长 Mail 正文或已 Delivered receipt payload 的保留期。

### derived [S-RECEIPT-PROGRESS-FUTURE-TRIGGER] 未来接入进展通知时重新落实的产品选择

以下是未来功能的设计入口，不是本轮 schema、迁移或新增状态机：

| 问题 | 最小候选及必须验证的边界 |
|:--|:--|
| 状态含义 | Queued 表示排队；Started 表示尝试开始；Codex Accepted 表示远端接纳；内部 Delivered 表示已写入收件输入历史。失败按 stage / code 区分肯定失败与结果未确认，不能把所有 TerminalFailed 都渲染成“发送失败”。 |
| 最新进展还是逐阶段事件 | 优先每流程合并为最新已知状态，允许跳过中间阶段；若产品要求每阶段必达，才引入阶段事件队列。活动状态是否每轮重复或只在变化时显示，接入时明确。 |
| 终态可见性 | 若承诺终态通知最终可见，选择集合必须包含活动流程与尚未通知的终态。仅查询活动流程会漏掉两轮之间已经完成的邮件。 |
| 身份、游标与竞争 | 一封信可多次通知，须选择每流程的稳定身份与已通知语义版本。只在 exact append 后确认所选版本；采样后的更新不能被旧通知的 Complete 一并消费。历史快照允许落后于当前状态。 |
| 预算与因果 | 使用原 IDs 和短状态，不重载正文。确认回执、Ready reply 与进展快照由同一 composer 规划；进展不能挤掉已有必需输入，未展示的结果不提前消费。不能因显示“处理完成”而消费或删除尚未注入的真实回信。 |
| 已有通知 | Codex 终态已有 Reply / DeliveryFailure；应先判定是否已足够表达结果，再增加进展 notice，避免同一结果重复通知。现有 content-free 聚合 MailboxStatus 不能直接充当逐邮件结果。 |

触发本节实施的条件是接入具体进展通知，并明确最新状态 / 逐阶段、终态保证与展示节奏。当前回执重构不依赖这些未来选择，也不为本节单独增加数据库版本或实现验收。

## 5. 迁移换取唯一当前路径

### spec [R-RECEIPT-EXPLICIT-MIGRATION] 旧 Pending 转换，旧 commitment 保真

允许两个 owner 的表升级和显式迁移，以删除产品路径中的旧全文 writer、Note 专用 hydration、旧 noticeBody/renderedObservation 组合和两处独立 receipt selector。迁移工具与窄历史 codec 承担旧格式成本。

基线中 CharacterMemory schema 为 V4，delegation 为 V5；本次目标分别为 V5、V6，已有更早版本沿既有显式升级链衔接。版本只区分实际表布局 / 内容变化，不作为新 cutover ID。

迁移沿用既有 operator 命令及停服 / exclusive owner 边界，不调用 provider、不重做 Memo apply、不重发邮件：

- `upgrade-character-memory-store` 从配置确定该角色 SessionDir，用 `SessionJournalEngine.OpenReadOnly` 提供原 H/I 的 typed exact proof；升级器不能只凭 SQLite 状态判定是否 append。
- 只读 Journal 句柄、原 owner lifetime lock MUST 覆盖旧行读取、proof、转换事务和冷重开验收；只读打开不执行 raw-tail recovery。当前 storage pin 的共享只读句柄与普通 writer 的独占打开互斥；这属于既有维护边界，不新增全局锁服务。
- dry-run / apply 都先验证全部旧 Bound 的 proof。任一 unknown / Conflict / Corruption，整个该 owner 保持原 schema / Bound 字节并停止切换；不先部分转换再把未知旧 Bound 塞入新版当前路径。另一 owner 已完成的升级不回滚。

1. 用原 reader 验证 source、capture state、created revision 与 settlement 关系；旧 Pending 从关联 Applied ledger 的 Memo IDs、顺序和原文生成当前 snapshot，不解析 Markdown。
2. 旧 Bound 守住原 H/I，先以原 exact proof 对账。已 append 的项按原 I 结算 Delivered；NotAppended 的项在同一 owner 事务里回滚并转换为当前 Pending。preflight 后在事务内重验原行 authority，CAS 与已有提交不明回读判定同时覆盖转换结果。
3. 转换表布局和读取协议，保留 source、FIFO、CAS 及已 Delivered 事实；新产品路径只有当前 Pending 与当前绑定。当前 writer 没有 opaque legacy Pending 的构造入口。
4. 旧 Journal / Prepared 字节及内容承诺原样保留，历史读取、展示、CLI 和恢复继续支持旧 variants；不重写已绑定输入或重算未知外部调用。
5. Mail 只为升级后的新 capture 原子创建义务；不补发所有升级前已 capture 的信，包括仍 Queued / internal Pending 的旧信。AlreadyCaptured 不 retro-fit 回执，无需额外 cutover ID。

各库迁移事务原子并可重开判定；两库部分升级后，fresh admission 等所需 owner 验证完成。只继续未完成的升级，不回滚已成功的业务库，不引入跨库迁移事务。实例部署前保留匹配快照，回退程序须与数据一起处理。

迁移不变量应按状态验证：Pending 的表示允许改变，但确认事实 / IDs / 顺序保持；Bound 的原 I 保真；Delivered 不补发。旧测试的“所有回执表字节完全不变”不能继续当作此次迁移法律。

## 6. 最小垂直施工与验收

### derived [A-RECEIPT-VERTICAL-SLICE] 按完整生产链接入两个消费者

| 切片 | 交付与代码入口 |
|:--|:--|
| 一个 Note 的完整闭环 | 两个闭合 batch 定义、Rune helper 和新 kind codec 直接接入 Note Applied→snapshot→一次 admission plan→bind→append→proof→reopen；同时让主模型 / 人类投影读到短回执。形成首条实际消费者链，不先铺无消费者平台。入口：CharacterMemory Transitions / ReceiptDelivery、PlayerTurnObservation、ObservationContent / Schema、GalateaServices、共享投递。 |
| Mail 与规划收口 | Codex / internal / unrouted 同一 capture batch 逐项冻结结果；把同一 plan 接入所有 fresh 入口；lease 只 claim membership，删除 universal reserve；16槽与full/null共同验证。claim前 / pre-Undo对账覆盖两个owner和relay。入口：GalateaServices、DurableReplyLease、Delegation Transitions / Leases / 新ReceiptDelivery。 |
| 显式迁移与恢复 | Note V5 / delegation V6、operator Journal只读proof、原lifetime锁、独立旧fixtures、unknown原库不变与部分升级重开。仅把已确认旧Pending转为snapshot；旧Bound先proof，历史I / Prepared原样。旧施工入口最终删除。 |
| 全消费者与删除验收 | DerivedInfo、recall、Recap / CLI接受新kind；query仍只选Reply / Failure；新Setup解释历史确认与短预览。删除旧当前writer、selector/hydration、重复预算；历史writer仅留test fixtures。依据 @[A-RECEIPT-PROGRESS-SEAM] 保留职责边界，进展API / 表 / variant为零。 |

每个阶段完成垂直验证，不先建设无消费者的通用平台；最终发布一个当前生产模型，不长期保留平行旧新写路径。相关文件还包括 [GalateaObservationContent](../../prototypes/Galatea/GalateaObservationContent.cs)、[PlayerTurnObservation](../../prototypes/Galatea/PlayerTurnObservation.cs)、[共享 projector](../../prototypes/Galatea.Input/GalateaObservationInputProjector.cs)、[输入说明](../../prototypes/Galatea/GalateaSystemInstructionContent.cs)和 [Recent display](../../prototypes/Galatea/GalateaRecentTurnDisplayAdapter.cs)。

### derived [A-RECEIPT-IMPLEMENTATION-HANDOFF] 下一轮施工按同一时序与验证范围推进

实现入口时序：

1. caller 持有角色 TurnLock，完成已有 durable admission / recovery 检查；所有 fresh 入口经 `PrepareFreshTurnAdmissionAsync` 对账旧 receipt，relay也走同一边界。旧 reply lease recovery在claim前处理。
2. 固定本轮 trigger、sender、timestamp、character 与 connectionState；读取两receipt FIFO头和当轮有资格的Ready snapshots，composer产生唯一不可变plan。
3. 以 plan 的准确成员 / expected row revisions 原子领取reply lease；随后创建live turn并把plan交给fresh send。后台新Ready不进入本轮plan；冲突不静默重选。
4. desired Setup / Recap context / optional recall使用该plan；只加入能fit的recall。不重读回执、不重选membership，也不回滚本轮CutoffFrozen。最终绑定使用Setup完成后的准确H与最终I。
5. 依次bind lease、Mail、Note；全部成功后才append。计划/recall/claim/bind失败沿原失败cleanup处理；未append时仅在允许的无效果状态或exact NotAppended证据下回滚，不重做业务。
6. append后receipt可Delivered；reply lease按原terminal / abandon资格结算。冷重开、显式结束、Undo均先共同对账，unknown不猜。

下轮先核对实际HEAD、schema版本和工作区，记录现有定向测试baseline；随后串行执行受影响测试。当前实现的回归入口包括 `tests/Galatea.Server.Tests` 下的 CharacterMemoryReceiptDeliveryTests、CharacterMemorySemanticReceiptMigrationTests、CharacterMemoryStoreUpgradeTests、GalateaDurableReplyLeaseTests、GalateaNoteReceiptDeliveryTests、GalateaNoteReceiptProcessCrashTests、GalateaObservationSharedProjectionTests，以及 internal mail / relay / CLI测试。新增断言围绕下面的故障和可观察请求，不为计划记录类型本身增加镜像测试。全部非Live回归命令使用[E2E指南](e2e-testing.md#离线与非-live-命令)中的明确类筛选。

下一轮实施范围是运行时代码、合成fixtures与文档联动；实际实例迁移 / provider调用 / 部署须单独记录实际执行证据。方案准备不表示这些工作已经完成。

### spec [A-RECEIPT-ACCEPTANCE] 以生产请求和故障窗口验收

| 场景 | 必须证明的行为 |
|:--|:--|
| 确认资格 | 内存候选不成功回执；Note 尚未 Applied 不回执；Codex / internal 提交受理，真实 unrouted 如实反馈。 |
| Mail 正文清理竞争 | capture 后立即完成/失败并清 body，下一轮仍有正确 frozen preview；不保留额外全文、不重提取。 |
| 预览边界 | 1 / 48 / 49 scalar、supplementary 字符、空白片段、换行、控制字符、引号和长 fence；有效 Unicode，首尾正确；原业务正文不变。 |
| 规模和编码预算 | 64 Mail + 16 Note、最长合法业务 IDs、最多 144 个预览、真实 JSON 最坏转义；回执数组 ≤128 KiB；省略预览投影仍保留全部 IDs / outcome。 |
| 紧组合预算 | 合法控制字符 reply 使完整预览不能 fit、null 预览可以 fit；最早 Ready prefix 能推进。两投影均失败不消耗义务。 |
| 实际输入预算 authority | 短PlayerAction / DelegateReply的实际I可fit而最坏player reserve会拒绝的membership能够claim / bind / 冷重开；缺失、错序、非Ready或错误revision仍拒绝。 |
| 选择与吞吐 | 每个领域各一 FIFO 头；连续双领域提交不被“一轮只消费一批”人为限制；空领域不阻塞另一域；cutoff / fresh 使用同一选择。 |
| notice槽与trigger | 16个小Ready + 双receipt本轮只claim前14条，其余仍Ready；一receipt为15、零为16。Heartbeat在新Ready竞争中仍不领取lease；Empty不创建DelegateReply。 |
| 来源与展示顺序 | 同I选中回执为前缀，回信内部顺序不变；连续InboundMail造成两批回执积压时，较晚source的Ready仍可先展示且身份准确，迟到确认使用历史措辞。内部信不因outbound=Unrouted误报。 |
| snapshot 与 bind 篡改 | 缺失/重复/错序ID、错误结果、改preview、单batch部分null、两新notice混合full/null、来源/版本不匹配都拒绝；预算选择不改snapshot；新规则不否决旧variant。 |
| 纯投影与职责边界 | 同一冻结 I 在 Mail 状态推进和正文清理前后投影一致；Prepared 恢复不查询最新状态；当前代码没有空进展框架、进展表或未用 wire variant。 |
| 提交不明与 crash | before/after commit、部分 bind、全部 bind 后退出、append 后部分 settle、Prepared/Started 恢复；不丢、不重复确认或外部工作。 |
| 部分结算与Undo / relay | 双owner同I append后只Complete一域，正式Undo入口先完成另一域；再次冷开均无Pending。注入剩余owner结算失败则head不移动。relay经过公共fresh对账，不绕过未结算Bound。 |
| 迁移 | Pending、Bound已append / NotAppended / unknown、Delivered；operator只读Journal proof且provider零调用；unknown整owner旧schema / I原样；两库部分升级可继续，无legacy当前writer；proof到事务期间普通writer无法改变Journal。 |
| 端到端消费 | fake provider 捕获最终请求：操作回执没有长正文独特中段，含完整身份；人类显示同样简短；主线/辅助 LLM/CLI/Recap 接受新 variant；真实邮件与回信全文保真。 |

测试入口：CharacterMemoryReceiptDeliveryTests、CharacterNoteSaveReceiptTests 的历史 fixtures、GalateaNoteReceiptDeliveryTests、GalateaNoteReceiptProcessCrashTests、CharacterMemorySemanticReceiptMigrationTests、GalateaDurableReplyLeaseTests、GalateaObservationSharedProjectionTests、internal mail 和 CLI / Recap 回归。原“当前 Selection 回传 64 KiB 全文”断言转为新回执断言，另保留显式旧 variant 的全文恢复证明。

上下文收益以实际 fake provider request 为依据，隔离回执贡献；源 Action 自己仍含原文时，不能把它误算成回执泄漏。这里只承诺回执预览有界，不承诺跨模型固定 token 节省比例。已有历史全文在退出 raw context 窗口前仍可能占用上下文。

后续 .NET 验证串行，使用 `--no-restore -m:1 -nr:false`；全非-live 筛选遵循 [E2E 指南](e2e-testing.md#离线与非-live-命令)，不能用会误排除 Delivery 的 `!~Live`。实际迁移、provider 调用和部署尚未执行。

## 7. 辩证裁决与删除范围

### derived [S-RECEIPT-DIALECTICAL-VERDICTS] 按当前消费者与故障轨迹裁决

三角色分别为需求质疑、最小架构、语义守护。初次完善裁决版本与frozen内容；本次在进展扩展加入后进行独立审阅和交叉质询，争议已闭合，无需第三轮。主线程独立核验关键源码，没有以票数决定，也没有实施代码。

| 项目 | Verdict | 依据 / 最终处理 |
|:--|:--|:--|
| 全文优先、三字段 preview、平行 arrays | simplify / merge | 每 item 一个 nullable preview；没有独立省略数字消费者。 |
| 任意多源聚合、group 状态机、额外 wire 聚合外层 | delete | 现有 notices[] 承载每域一批；固定最多两份，同 I 逐 owner proof 已足够。 |
| 每轮仅一领域 / 固定领域优先 | simplify | 双领域持续提交会积压或饥饿；改为各取一个 FIFO 头。 |
| Observation V5、nullable connectionState 升级 | delete | 新 notice kind 足够区分合同；旧 variant 的解释与 exact input 比较不变，未找到要求冻结全部接纳集合的消费者。 |
| 当前正文 hydration / 每次 bind 重算 | merge | Mail terminal 清正文有真实竞争；两事务冻结通知内容，共同验证其投影。 |
| 全预览 / null 预览两候选 | keep | JSON 转义后的紧 reply 预算有真实反例；所有识别预览一起省去，不增加模式字段。 |
| 事务内义务、CAS、Bound input、exact proof | keep | 提交后退出会丢通知；append 后退出会重复；需守住真实持久窗口。 |
| 历史 reader、原 Bound / Prepared | keep | 真实数据与恢复消费者；原 I 不能改写。 |
| opaque legacy Pending 当前 writer | simplify | Applied ledger 可验证并转换；迁移和旧 proof 承担成本，最终当前路径不继续写全文。 |
| 跨轮确认先于相关回信 | simplify | 连续InboundMail可积压回执，单FIFO头不足以保证；改为同I所选回执前置，不给已有Ready增加确认gate。 |
| store内 universal player预算预留 | delete / merge | actual输入composer已选择，冷重开无此假设；删除创建时重复预留，保留store结构 / CAS / membership / 实际I校验。 |
| 独立回执预算planner | delete | 128KiB保留为严格codec上限，16槽与整体预算都由同一composer选择。 |
| 各owner独立校验足以保证全局投影 | simplify | 两域各自合法也可能Mail full / Note null；shared schema检查新notices整体投影，不增加mode或跨库状态。 |
| 所有owner在head变化前对账 | keep | partial settle→Undo到H会使剩余Bound误判NotAppended；覆盖公共fresh准备、relay和pre-Undo，复用原cleanup。 |
| SQLite-only旧Bound升级 | simplify | 是否append须Journal proof；现operator持有OpenReadOnly和原lifetime锁，unknown保留整owner旧schema。 |
| 空进展接口 / 状态表 / 未用variant | defer | 用户要求保留扩展位置，composer / typed notices / 纯projector已满足；具体进展消费者接入时再落实身份、游标与节奏。 |
| 全局时钟、随机 receipt ID、handler registry、回执新 wake | defer | 当前两个消费者不需要；只有新增具体领域或独立 wake 产品需求时再审。 |

可观察的简化是：三个预览字段合为一个，两组平行数组合为一个item数组，admission / fresh内容选择合为一个plan，删除store的第二次预算预留；两个领域共用snapshot / 投递合同。新增第三库、全局调度身份、新容器版本、进展API / 表 / variant均为零。实际类型数和代码行变化需实施后量测。

当前没有需要用户追加决定的实施阻塞项。确认反馈发生于后续普通轮次，允许历史确认晚于实际回信；recipient仅是识别预览，没有wall-clock时延承诺。未来最新状态 / 逐阶段、终态保证和重复展示节奏在 @[S-RECEIPT-PROGRESS-FUTURE-TRIGGER] 延后，不阻塞本轮。

实施时同步把[结构化输入合同](structured-input-rendering-design.md) §5.2 的“全文或IDs”现行规则及旧Pending进入新writer的描述指向本方案；[保存合同](character-note-default-memopod-v1.md)和[Observation Bridge](text-extractor-observation-bridge.md)的“唯一末尾receipt / 15槽”规则改为本方案的新kind分类与16槽分配。同步[运行时](runtime.md)、新Setup说明和[文档索引](README.md)，历史阶段证据及旧kind codec保留原含义。
