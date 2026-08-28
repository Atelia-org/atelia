# Galatea Codex delegation durable state machine

> 状态：Implemented；production hard cut active
>
> 启动日期：2026-08-28
>
> 完成日期：2026-08-28
>
> 现行产品用法与runbook见 [`prototypes/Galatea/README.md`](../../prototypes/Galatea/README.md)；
> 本文保留durable authority、state machine与failure-model设计真源。

本文是 Galatea Codex delegation durable state machine的设计与实施真源。现行binary已经将旧
process-local闭环hard-cut为一个 Galatea-owned、per-user、SQLite-backed current-state
machine，使进程崩溃、sidecar 断线和普通重启不再直接丢失 outbox、fixed
thread binding、reply inbox 或 one-shot lease。

这不是把邮件流程塞进 SessionJournal tool-call 状态机，也不是把所有中间变化
改造成 effect event sourcing。我们持久化当前业务状态、稳定 identity 与必要的 exact
evidence；调度器每次从盘上状态决定下一个有界步骤，不把一条长异步函数的调用栈
当成恢复权威。

## 1. 目标与非目标

### 1.1 本阶段目标

1. durable 记录每个 post-baseline terminal Action 是否已完成 extraction settlement，
   包括明确的 `0 intents` tombstone。
2. 以单事务全有或全无地写入一个 Action 的整批 `SendMailIntent`，然后由
   durable FIFO outbox 独立调度。
3. 跨重启保留 exact-one fixed Codex thread binding、accepted turn identity、terminal
   reply/failure 和路由 quarantine。
4. 将 reply ready/lease/consume 收口为 durable one-shot 协议，并使恢复只依赖
   SQLite current state 与 SessionJournal exact raw evidence。
5. 使outbox/external调度不依赖内存signal可靠到达：任何signal只是降低延迟的提示，1秒
   fallback pulse必须最终发现所有可由driver推进的SQLite状态。
6. 在不让主线角色 LLM 看到 delegation tools、不改变 SessionJournal provider
   tool-result continuation 语义的前提下完成上述目标。
7. 以 per-user database lifetime exclusive OS writer lock 保证任一时刻至多一个
   process writer；这是 P0 正确性前提，不用 epoch/fencing 将多 writer 合并成可接受状态。

### 1.2 明确非目标

- **不承诺 provider-call exactly-once。** 我们承诺稳定 operation identity、先写
  Started state、同一dispatch at-most-one `start-turn` attempt、绝不重发outcome-unknown
  effect，以及能够证明时的exact reconciliation。Codex app-server可持久读取owned
  thread/turn history，但它或下游provider没有提供的transactional exactly-once语义，
  Galatea不伪称已经拥有。
- 不做 multi-thread、thread rollover、thread selection 或长 thread 性能治理。
- 不把 inbound mailbox delivery queue纳入delegation SQLite；inbound turn继续由SessionJournal
  narrative persistence拥有。
- 不新增 browser UI 通知、operator UI 或人工解除 quarantine 流程。开发时可观测性
  继续使用 bounded `DebugUtil.Info/Warning/Error`。
- 不让 delegation state 随 SessionJournal fork/rewind/rollback 分叉，不尝试“撤销”
  已发生的 Codex side effect。
- 不引入 effect-event journal、event-sourced projection 或一份与 current-state tables
  并行的持久事件真源。
- 不保留旧process-local owner、live dual-write、compatibility fallback或operator切回旧owner路径。

## 2. 三个 authority

系统只承认三个正确性 authority。signal、pulse task、sidecar process、in-memory cache、log
文件和 UI projection 均不是 authority。

### 2.1 Narrative authority：SessionJournal selected raw lineage

SessionJournal raw selected `RefId` Parent lineage 唯一决定 Observation/Action 是否真实持久、
terminal Action 的 exact `EventAddress` 是什么，以及普通 player Undo 实际把主 branch
移到哪里。SQLite 不复制或改写 narrative Action 内容，不成为第二条故事历史。

SessionJournal 不拥有 outbox state、Codex thread binding、reply readiness 或 reply lease。
`EventAddress` 在 delegation store 中只是 provenance/deduplication key，不表示外部
side effect 能被 raw ref move 撤销。

### 2.2 Delegation operation authority：Galatea-owned SQLite

每个 Galatea user/session 的 delegation SQLite database 唯一决定：

- baseline 以后的 Action 是否已经 settlement；
- 哪些 intent 已被 capture，哪一封信可以或不可以 dispatch；
- 当前 fixed thread 是 unbound、binding、bound 还是 quarantined；
- 哪一个 dispatch 已 Started/Accepted/terminal/outcome-unknown；
- 哪些 reply/failure 是 Ready、Leased 或 Consumed；
- 崩溃后下一个允许执行的有界转移。

一个 SQLite 事务的 reopen 结果只能是旧 current state 或新 current state。调用方不用
内存 bool 、Task completion 或 log 文本推测事务是否已经发布。

### 2.3 External effect authority：Codex app-server persistent state

Codex app-server提供可持久读取的owned thread/turn history；该外部状态唯一决定thread是否已建立、
turn是否已接受或terminal，以及exact final是什么。Node sidecar和C# transport只是协议适配器，
不是durable ownership authority；history可读也不构成provider exactly-once承诺。

reconciliation 必须用 code-owned thread ownership marker、canonical cwd、stable `dispatchId` /
`clientUserMessageId` 以及已知 `threadId`/`turnId` 的 exact 组合证据证明同一个外部
operation。“现在没查到”不是“当时绝对没有执行”的证据。

## 3. 跨 authority 边界

SQLite 与 SessionJournal、SQLite 与 Codex app-server 之间都没有分布事务。正确性来自
稳定 identity、先持久后 effect、exact evidence 以及保守的 uncertain state，不来自
假设两个 store 可以原子 commit。

### 3.1 Capture 是对外 side-effect boundary

terminal Action durable 后，extractor 可以在 SQLite 之外运行；它没有外部 Codex
side effect。只有整批 capture transaction commit 后，该批 intent 才成为 durable outbox
authority，并允许 dispatcher 执行。崩溃可能导致 extractor 重算和重复计费，但不能在
capture transaction 之前启动 Codex dispatch。

整批 capture 必须在单个 SQLite transaction 中：

1. 插入 exact `sourceActionAddress` 的 settlement/tombstone；
2. 插入 0..N 个有序 mail rows；
3. 验证 row count、ordinal 和 stable dispatch identity；
4. commit 后才发 scheduler signal。

`0 intents` 也必须 commit action settlement。不能用“没有 mail rows”同时表示“已检查
且没有”与“还没检查”。重复 capture 看到已存在的 action settlement 后必须零修改
返回；不再比较新一次非确定 extractor 输出并覆盖 first committed batch。

### 3.2 Capture 之后 Undo 不撤销 delegation

capture transaction 一旦 commit，普通 SessionJournal Undo、rewind、fork 或 rollback 不得删除、
撤回、重新武装或改写该 batch。`Queued` 信件继续发送，`Started/Accepted`
继续 reconcile/poll，reply 仍可在后续时间线中 one-shot 呈现。

这是有意选择的分层：side-effect boundary 在 `OutboundMailExtractor -> durable capture`
之间。可能收到来自另一条故事时间线的回信，是换取外部 effect 简单、
可恢复、不伪装可逆的明确产品语义。新 durable 状态机不实现现行内存
`RetractedBeforeDispatch` 语义。

capture transaction 之前的 Action 仍必须在 current selected lineage 上通过 exact revalidation。
如果在 commit 前已经不再 selected，则不建立 settlement，也不产生 effect。

## 4. Explicit baseline

durable hard cut不能把现有会话中所有历史Action当成新邮件重放。Production supervisor对每个user
建立slot：existing `delegationStateDir`只有在matching `sessionDir`也存在时才在host composition strict-open；
state存在而session缺失先分类为`SESSION_MISSING`，不会打开SQLite/lock。Missing store保持
`Uninitialized`，直到该user第一次成功打开或provision writable SessionJournal并执行
`AttachWritableSession`，才在同一per-user serialization边界自动建立一次
`captureFromPhysicalFrontier`：

- 记录 repository 当时已知的 **physical append frontier**，以及当时 selected
  raw head 作为诊断/evidence；
- normal clean repository 中 current head 通常就是 physical frontier；
- `ReadPhysicalAppendFrontier`是bounded、O(1)、read-only seam，覆盖capture前selected与orphan
  physical frames；它不扫描payload/history，也没有扩张成full-lineage projector；
- frontier 之前的 Action 永不进入 durable extraction/capture；
- frontier 之后 append 的新 branch Action 依然应被 capture，即使 branch 的 Parent
  穿过 baseline head；不得使用 EventAddress 字符串顺序或 baseline ancestry 猜测
  physical frontier。

恢复不做全历史扫描。Galatea 的 per-session turn 是串行的，所以从上一次成功
settlement 到崩溃的 gap 最多只有一个未 settlement terminal Action。在下一次 admission
之前，host 只需：

1. 读取 baseline/frontier 和当前 selected latest terminal Action；
2. 判定该 Action 的 physical coordinate 是否在 frontier 之后；
3. 查询该 exact Action 是否已有 settlement；
4. 如果缺失，在允许新 player/inbound admission 前完成 extraction 和整批 capture。

baseline 只在初次 hard cut 建立，不随 process restart、SessionJournal Undo 或 branch
变化自动前移。缺失、重复或与当前 repository identity 不匹配时 fail closed，不静默
重建 baseline。

baseline创建失败或existing store的schema/owner/route policy/limits/integrity/lock不匹配时，该user
fail closed/unavailable；runtime不adopt、reset、迁移、删除数据库或换用内存路径。当前binary没有旧owner，
因此baseline写入前后都不存在“删掉candidate后继续process-local运行”的产品或operator分支。修复
filesystem/config后必须重启并继续使用同一durable composition。

## 5. SQLite current-state schema

### 5.1 物理边界

- database 归 Galatea user/session 所有，路径与 SessionJournal repository 共用同一
  user lifecycle，但不放进 raw EventJournal 或 RecapGrid derived roots。
- 每个 user database 必须在 SQLite open、baseline 创建或任何恢复之前，取得一把
  跨进程的 **lifetime exclusive OS writer lock**。lock 从 user durable owner 构造前一直持有到
  pulse/sidecar 已停止、database 已 dispose 之后；process crash 由 OS 释放锁。取锁失败时
  该 user fail closed/unavailable，不打开第二 writer。
- 本阶段不设 writer epoch、lease epoch、fencing token 或多 writer last-write-wins。SQLite
  row revision 只防止同一 writer 内的 stale transition，不替代 OS lock。lock 文件/路径的
  no-follow、canonical containment 与 filesystem lock semantics 必须由 focused test 验证。
- 当前binary只允许一个exact schema version并strict-open；没有compatibility reader、automatic migration
  或reset-on-mismatch路径。
- open 必须启用 `foreign_keys=ON`、bounded busy policy 和经验证的 durable SQLite
  journal/synchronous 组合。事务提交失败后 dispose/reopen，由盘上 current state 分类；
  不继续使用可能过期的连接缓存。
- 所有 body/final/evidence 继续使用现行 code-owned UTF-8 byte bounds。数据库不保存
  provider credential、connection secret 或无界 log payload。
- 每个状态表都有单调 row revision；调度器以 exact expected state/revision 更新，
  受影响行数不为 1 即重新读取，不带着 stale snapshot 继续 effect。

### 5.2 逻辑表

以下表是正确性所需的 current-state tables。列名可在实施时做机械调整，但不得
合并或删除所列 identity、unique constraint 和恢复证据。

#### `delegation_meta`

singleton，保存 schema/version、exact user/session/repository identity、
`captureFromPhysicalFrontier`、baseline selected head，以及下一个 `completionSequence`。任何
identity mismatch 都 fail closed。`completionSequence` 只在产生新 ready notice 的同一事务中分配。

#### `action_capture`

每个 post-baseline Action 一行，`sourceActionAddress` 为主键。至少保存 exact address、
visible Action SHA-256/byte count、extractor contract identity、`artifactCount` 与 row revision。
`artifactCount=0` 是一等 tombstone。此表不保存“正在 extraction”的长任务；在
capture commit 之前崩溃可以重算无 side-effect extractor。

当前 extractor 是per-user immutable实例：以该user的validated `characterName`在host composition阶段
展开code-owned system/user templates，但复用host-wide borrowed Completion client。实例ContractId采用
`atelia.galatea.outbound-mail-extractor.v2.<SHA-256>`，覆盖code-owned semantic/visible-renderer/tool
contract版本及exact rendered prompts，不包含provider/model/connection。既有capture即使保存historical
ContractId也仍是first-committed authority，不按current实例重做。

#### `outbound_mail`

每个 captured artifact 一行。主键是现行 stable `gd1-* dispatchId`，并对
`(sourceActionAddress, artifactOrdinal)` 建 unique constraint。保存冻结的 Recipient、Subject、Body、
InReplyToMessageId、EvidenceQuote，exact route classification，以及当前 dispatch state。

Codex-routed row 的状态集合为：

`Queued -> Started -> Accepted -> TerminalCompleted|TerminalFailed`

并允许：

- `Started -> OutcomeUnknown -> Accepted|TerminalCompleted|TerminalFailed|Quarantined`，但只能由
  reconciliation 推进；
- 任何 exact identity/protocol conflict `-> Quarantined`；
- 非 Codex recipient 在 capture 时直接成为 terminal `Unrouted`，永不进入 dispatcher。

row 同时保存 stable operation ID（当前即 `dispatchId`）、known durable requested
thread ID、冻结的 exact route policy fingerprint、accepted `threadId/turnId`、bounded terminal
stage/code 或 final digest，以及 revision。`Started` 表示 effect boundary 已可能跨过，不得
透明再发。`OutcomeUnknown` 还必须持久 reconciliation attempt count、last code 与
code-owned `nextReconcileAt` backoff frontier，使 unavailable/not-found 跨重启不忙轮询。

#### `route_binding`

per-user singleton，状态集合为 `Unbound|Binding|Bound|Quarantined`。它保存
stable binding operation identity、已证明的 fixed `threadId`、路由/runtime policy fingerprint、
active mail `dispatchId`、bounded quarantine code 和 revision。`Binding` 只表示正在建立一个
尚未承载任何 mail turn 的 empty owned thread；不得在此状态预先填入 mail
`dispatchId`。`Bound(threadId, activeDispatchId?)` 在 Started、Accepted 和 OutcomeUnknown 期间始终
保留 known thread 与 active mail，不用 route state 表示 reconciliation。同一时刻只允许一个
active mail dispatch。只有该 mail 已确定 terminal 并完成 notice settlement，才在同一事务
清除 `activeDispatchId`，route 仍为 `Bound(threadId)`。

#### `reply_notice`

每个 terminal routed mail 最多一行，以 `dispatchId` 为 unique source，保存
`Reply|DeliveryFailure`、exact bounded body/stage/code、唯一单调 `completionSequence`、
`Ready|Leased|Consumed`、可选 exact `consumedActionAddress` 与 revision。只有已证明的
terminal outcome 可以产生 notice；
`OutcomeUnknown`/`Quarantined` 不得伪造成“已发送失败”。
只有 `Consumed` notice 必须带 exact `consumedActionAddress`，它是实际接收并携带
该 reply 的 SessionJournal terminal Action address；Ready/Leased 必须为 null。

#### `reply_lease` 与 `reply_lease_item`

每个 user 最多一个 active lease。lease 保存唯一 `leaseId`、state
`CutoffFrozen|ObservationBound|ObservationCommitted|Quarantined`、冻结的
player text、ordered notice IDs/completion frontier，以及 row revision。`CutoffFrozen` 明确不保存
SessionJournal head 或 exact composite Observation。

`BindObservationBase` 后才写入 exact fresh base head、exact rendered composite Observation
canonical bytes/byte count/SHA-256，并将 state 改为 `ObservationBound`。之后可选写入
durable Observation/terminal Action addresses。

SQLite schema保持V1且不增加renderer/version列。当前writer使用角色中立reply/failure headings；strict
reader同时接受current neutral dialect与旧Galatea-heading dialect，但同一envelope禁止混用。对于
`ObservationBound|ObservationCommitted|Quarantined`中已有rendered Observation，open/reopen按stored
dialect做exact canonical parse，再逐项核对player text和notice kind/order/body；不得用current writer重渲染
历史bytes。`CutoffFrozen`没有rendered Observation，跨重启仍按§8.2直接rollback，因此不需要冻结renderer列。

`reply_lease_item` 以 `(leaseId, ordinal)` 保留 exact ordered membership，并对 notice ID 建
unique constraint。建立 lease 的同一事务将所有选中 notice 从 Ready 变为 Leased；
consume/rollback 也必须先在同一事务中结算所有 notices，再删除该
temporary `reply_lease_item` 与 `reply_lease` rows。不保留 RolledBack/Consumed lease
历史、membership 或 rendered Observation 永久副本；必要的 one-shot 证据归并到
`reply_notice.ConsumedActionAddress`。只有 `Quarantined` lease 仍保留 active rows/items/evidence
并 fail closed，等待未来显式治理。

### 5.3 不是 effect event sourcing

不建 `events`/`transitions` 真源表，不在恢复时重放 effect event 生成上述状态。
状态转移在一个或多个 current rows 上事务更新，完成后直接是下次 pulse 的
authority。可以写 bounded debug log 或非权威 telemetry，但它们不参与状态恢复，也不得
用来填补丢失的 current row。

## 6. Capture 与 admission 协议

### 6.1 正常 terminal Action

1. SessionJournal 先 durable terminal Action，并回到 `Idle`。
2. host 在同一 per-session admission/turn serialization 边界内运行该user的exact extractor。
3. capture 前重新验证 exact Action 仍位于 current selected lineage。
4. 单事务写 `action_capture + 0..N outbound_mail`。
5. commit 后发 signal；返回 SSE `done` 不等待 Codex accepted/final。

不允许在 Action 尚未 durable 时预先 capture，不允许先向 sidecar 写入再补 SQLite。

### 6.2 崩溃 gap

下一次普通 player 或 inbound admission 前先执行第 4 节的 latest-terminal-Action
settlement 检查。未 settlement 时，admission 等待 extraction/capture 成功或返回稳定可重试错误；
不允许新 turn 跳过 gap，否则串行“最多一个未 settlement Action”不变量会被破坏。

extractor failure 不写 empty tombstone；empty 只能表示一次成功、合法、产物为零的
extraction。因为 extractor 无外部业务 side effect，后续 admission 可用同一 Action 重试。

## 7. Durable outbound state machine

### 7.1 FIFO 与 durable Started

调度器只选择最早的 Codex-routed nonterminal mail，并且仅在 route 已经
`Bound(threadId, activeDispatchId=null)` 时推进。route 仍是 `Unbound|Binding`
时只能执行第 7.2 节的 `ensure-binding`，mail 保持 `Queued`。一个新 dispatch
只有一个 effect 前转移：单事务将 mail `Queued -> Started`，同时冻结 stable
operation ID、known durable requested `threadId`、exact route policy，并将
`route_binding.activeDispatchId` 从 null 改为该 `dispatchId`。commit 后才调用
sidecar/app-server `start-turn`。

不存在 mail `Prepared` state，也不存在 `Started -> Prepared` 的瞬时优化。一旦
`Started` commit，即使同一活进程的 transport 认为尚未 write，恢复也一律按 effect
可能已发生处理，只能进入 read-only reconciliation。

### 7.2 Fixed thread staged binding

fixed thread 的建立与第一封 mail turn 是两个严格分离的 operation。第一封 routed
mail 在整个 binding 过程中保持 `Queued`：

1. 单事务 `Unbound -> Binding(bindingOperationId)`，不修改 mail state；
2. commit 后调用 sidecar `ensure-binding`，它只允许执行 `thread/start +
   thread/name/set + ownership/cwd verify`，**绝不调用 `turn/start`**；
3. 返回的 thread 必须通过 code-owned name marker、canonical cwd 和协议 identity
   验证；
4. 验证后单事务 `Binding(bindingOperationId) -> Bound(threadId)`；
5. 只有 `Bound(threadId)` durable 后，第一封 mail 才和后续 mail 一样进入
   `Queued -> Started`，并调用独立 `start-turn(threadId, dispatchId, body)`。

如果进程在 `ensure-binding` 创建/验证 thread 后、`Bound` commit 前崩溃，恢复可以
重新执行 `ensure-binding`。每次这类不确定尝试最坏只会留下一个从未执行过 mail
`turn/start` 的 empty orphan thread，不会重复邮件 side effect；反复崩溃可以留下多个
这类 empty orphan，本阶段不治理它们。明确的 ensure 失败可以保留 `Binding` 后重试；
ownership/cwd/identity 冲突则 quarantine。一旦
`Bound(threadId)` durable，binding 永不被后来的 ensure 或 mail result 覆盖。

已经 Bound 的 route 不因单封信的断线丢失已证明 thread ID，但在该信 terminal
settlement 完成并清除 active dispatch 前停止 FIFO。任何 `Started|OutcomeUnknown|Accepted`
mail row 必须持有与 route 已证明 fixed thread 一致的 known durable `threadId`。

### 7.3 OutcomeUnknown 只能 reconcile 或 quarantine

任何在 `Started` 之后且未得到 exact accepted/terminal evidence 的 timeout、EOF、process
death、protocol loss 或 host crash，都使 mail row 进入 `OutcomeUnknown`；route 继续是
`Bound(threadId, activeDispatchId)`。因为 mail 只能在 fixed binding durable 后 Started，所有 OutcomeUnknown
都已知 exact durable `threadId`。此后唯一允许的外部动作是 read-only reconciliation：

- exact 找到同一 `clientUserMessageId/dispatchId` 的 owned thread/turn：持久 accepted
  identity，继续 poll terminal；
- exact 找到同一 turn 的 terminal final/failure：直接持久 terminal 与 notice；
- app-server/sidecar 暂时 unavailable，或当次 read-only lookup 返回 not-found：mail
  保持 `OutcomeUnknown`，持久 code-owned backoff 后再做 read-only retry；
- 发现 multiple candidates、ownership/cwd/body/client-message/thread/turn identity 的确定性冲突：
  持久 `Quarantined`。

不得把未查到候选当作重新 `turn/start` 的授权，不得将 `OutcomeUnknown`
直接改成 DeliveryFailure 后继续 FIFO。unavailable/not-found 也不是 quarantine 证据；它们可以
长期保留 OutcomeUnknown，但必须使用 durable bounded-exponential backoff 而不是每秒出站请求。
本阶段没有 operator UI，所以 deterministic conflict quarantine 之后保守停止该 user
route，并通过 bounded dev log 暴露 exact code。

reconciliation 只有在同一事务已持久确定 terminal/notice 时才清除
`activeDispatchId`，route 仍为 `Bound(threadId)`。如果只发现 exact Accepted/running
turn，mail 改为 Accepted 但 active dispatch 保留到 terminal settlement，不允许同 thread
并发第二个 turn。

### 7.4 Accepted 与 terminal polling

accepted `threadId/turnId`持久后，staged V2没有V1式terminal Task；host-local signal只提示
supervisor尽快pulse，pulse以exact IDs调用read-only `inspect-dispatch`读取app-server persistent state。
任一signal或单次inspection attempt丢失都不影响后续1秒fallback。对同一accepted turn的重复terminal
observation必须幂等：同一terminal零修改，不同final/status冲突进quarantine。

写入 `TerminalCompleted|TerminalFailed`、分配 `completionSequence`、创建 exact-one
`reply_notice` 和清除 route `activeDispatchId`（仍保持 `Bound(threadId)`）必须在同一
SQLite transaction。完成后发
signal；signal丢失不影响1秒pulse推进下一封queued mail。Ready notice本身不由pulse注入，只有后续普通
player cutoff会读取、claim并呈现它。

## 8. Durable reply lease

### 8.1 冻结 cutoff

普通 player admission 首先以单个 SQLite transaction 建立一个与 SessionJournal 尚未绑定的
cutoff：

1. 按 `completionSequence` 选择现行数量/字节上限下可渲染的最早 FIFO prefix；
2. 只冻结 exact player text、ordered notice membership 和 completion frontier；
3. 插入 `reply_lease(CutoffFrozen)` 与 items，并将选中 notice 改为 `Leased`。

`CutoffFrozen` 不记录 SessionJournal expected head，也不预先生成/冻结 exact composite
Observation。host 先完成 desired setup reconciliation 及为该 fresh turn 生成 exact
Observation 所需的纯准备。只有在紧邻 `SendAsync` 之前，才以单个 SQLite
transaction 执行：

`BindObservationBase(freshBaseHead, exactObservation)`

该转移必须重新验证 current SessionJournal head exact 等于 reconciliation 产生的
`freshBaseHead`，然后一次性持久 base head、exact Observation canonical bytes/byte count/
SHA-256，并将 lease 改为 `ObservationBound`。事务 commit 后必须紧接着以该
exact base/body 调用 `SendAsync`，不在中间运行其他可持久 mutation。

后来到达的 reply 不加入已冻结 lease。inbound admission 和 recovery admission 不开始
新 cutoff；recovery 只继承已持久 lease。

### 8.2 Exact raw evidence

SQLite 不得仅因为 `SendAsync`/上层函数返回或抛错而 consume/rollback。它必须使用
SessionJournal selected raw lineage 上的 exact evidence：

- **Bind 之前崩溃**：lease 仍为 `CutoffFrozen`，它没有 SessionJournal base/effect
  authority；同一事务将所有 notice 恢复 Ready，再删除 temporary lease/items。
  desired setup reconciliation 已经发生不改变这一结论。
- **Bind 后 Observation 未 commit**：current head 仍等于 lease `freshBaseHead`，且没有匹配
  Observation，则以同一事务将所有 notice 恢复 Ready，再删除 temporary
  lease/items 及其 rendered Observation。
- **Observation 已 commit**：存在直接从 `freshBaseHead` 下降的 Observation，其 exact
  canonical content/byte count/SHA-256 与 lease 冻结值一致，则记录 durable Observation
  address 并改为 `ObservationCommitted`。即使 raw head 已是 SessionJournal
  Prepared/Started/Action 后代，
  也必须沿 selected lineage 识别该 exact Observation。
- **terminal Action 已 commit**：匹配 Observation 已有 exact completed terminal Action，且该 turn
  的 execution boundary 已按 SessionJournal contract 结算，则以同一事务将所有 notices
  改为 `Consumed`、把该 exact terminal Action address 写入每一个
  `ConsumedActionAddress`，再删除 temporary lease/items。
- **明确放弃**：known failed turn 被 exact abandon 回 `freshBaseHead`，或 pre-observation
  stop 有零 raw mutation 证据，可在 notices 恢复 Ready 后删除 temporary lease/items。
- **证据分叉**：`freshBaseHead`、Observation bytes、selected lineage 或 terminal Action
  identity 不能同时成立，则 lease `Quarantined`，不自动恢复 Ready 也不假装
  Consumed。

这一证据设计专门覆盖“SessionJournal Observation/Action 已 durable，但 SQLite 尚未
记录下一状态”的崩溃窗口。

### 8.3 One-shot 不变量

- `Consumed` notice 永不因普通 Undo 重新武装。
- 每个 `Consumed` notice 永久保留实际接收它的 exact terminal Action address，
  但不保留已结算 lease 或 rendered Observation 副本。
- rollback 只能发生在没有 selected durable terminal Action 的路径。
- 同一 notice 不能同时属于两个 nonterminal lease。
- 一个 lease 的 membership、顺序与 player text 一旦 `CutoffFrozen` 便不可变；
  base head 与 rendered Observation 一旦 `ObservationBound` 便不可变。
- 进程重启不开始新 cutoff，而是先结算旧 lease。
- rollback/consume 成功后 `reply_lease` 与 `reply_lease_item` 必须为零 active rows；
  `Quarantined` 例外地保留 active row/items 并阻止新 cutoff。

## 9. Pulse 模型

Supervisor driver是对SQLite盘上外部operation状态的反复有界求值，而不是一个需要保存continuation的
长工作流。每次pulse最多对每个user拥有一个in-flight driver，并执行有限数量的：

- ensure an unbound fixed thread without starting any mail turn；
- `Queued -> Started` 的一个 safe dispatch；
- reconcile 一个 OutcomeUnknown；
- poll 一个 accepted turn；
- publish 一个 terminal notice/release next FIFO item。

需要SessionJournal exact raw evidence的reply lease settlement与latest post-baseline Action extraction gap
不由periodic driver猜测；它们在第一次writable session attach、每次player/inbound admission以及terminal
turn收尾时持有per-session `TurnLock`结算。这样supervisor可以在session尚未attach时推进existing outbox，
但不能绕过SessionJournal serialization修改lease/capture。

业务 transition 每次先读取 exact state/revision，在单事务中 claim，事务外执行必要的
provider I/O，然后以 exact claim/identity 结算。任何进程内 Task 只是当前尝试，不是
下次启动所需的恢复数据。

相关durable transition commit后发送host-local signal以尽快再pulse。无论signal是否丢失、合并或发生在
consumer 准备之前，一个 1 秒 periodic fallback 都必须再读 SQLite。两者共用同一个
non-overlap gate，不允许 signal 和 timer 同时对同一 user 执行 effect。

## 10. Crash/restart matrix

| 崩溃窗口 | 盘上可见权威 | 恢复动作 | 不得做 |
|---|---|---|---|
| 另一 process 已持有 user writer lock | OS lock holder 是唯一 writer | 该 user fail closed/unavailable | 以 epoch/fencing 打开第二 SQLite writer |
| Action durable，extractor 未完成 | SessionJournal 有 post-baseline terminal Action，无 `action_capture` | 下次 admission 前重跑 extractor 并 capture | 跳过 gap 开始新 turn |
| extractor 已返回，capture 未 commit | 仍无 settlement | 重跑 extractor；first committed batch wins | 把内存 artifacts 当成 durable |
| capture transaction 中途 | 旧 state 或整批新 state | reopen 并查 `action_capture` | 补写 partial rows |
| capture commit，signal 前 | 整批 outbox durable | 1 秒 pulse 发现 | 依赖 signal replay |
| `Binding` commit，`ensure-binding` 前 | route 未 Bound，mail 仍 Queued | 执行 `ensure-binding`，不启动 turn | 把 mail 改成 Started |
| `ensure-binding` 创建 thread，`Bound` commit 前 | route 仍 Binding，无 mail turn | 允许重新 ensure，容忍 empty orphan thread | reconcile/retry 任何 mail body |
| `Bound(threadId)` commit，首封 mail Started 前 | fixed thread 已 durable，mail 仍 Queued | 按普通 FIFO 推进第一封 mail | 重新 ensure 或覆盖 thread ID |
| `Queued -> Started` commit，`start-turn` write 前或中 | known bound thread + active dispatch durable；effect 是否发生不可由重启证明 | mail 改 OutcomeUnknown，在该 thread 上 read-only reconcile | 回 Queued 或重发 `turn/start` |
| OutcomeUnknown lookup unavailable/not-found | mail 及 route active dispatch 仍 durable，无确定性冲突 | 持久 backoff，到期后 read-only retry | quarantine、DeliveryFailure 或重发 |
| app-server accepted，SQLite 未记录 | SQLite 已有 bound thread，app-server 可能有 exact client message/turn | 在已 bound thread 上 read-only reconcile，exact 证明后记录 Accepted | 创建新 thread 或重发 body |
| `Accepted` durable，signal/inspection attempt丢失 | SQLite 有exact thread/turn | fallback pulse以`inspect-dispatch`读app-server persistent turn | 重发task body或等待不存在的V1 terminal Task |
| app-server terminal，SQLite 未记录 | external terminal authority 存在 | 以 exact IDs 读取并幂等写 terminal+notice | 根据 log 猜 final |
| notice Ready，signal 前 | `reply_notice=Ready` | 后续普通player cutoff直接从SQLite claim/inject；无需signal replay | 丢弃notice或由pulse擅自建立lease |
| cutoff frozen，desired setup reconciliation 前/后但 bind 前 | lease 只有 membership/player text，无 SJ base | 同事务 notices -> Ready 并删除 lease/items | 保留 RolledBack lease 或从 current head 猜测 bind |
| `ObservationBound` commit，Observation 未 commit | lease 有 exact fresh base + Observation bytes | current head 仍为 base 时，同事务 notices -> Ready 并删除 lease/items | 保留 rendered Observation 或盲目 consume |
| Observation durable，lease 仍 `ObservationBound` | raw selected lineage 有 exact base/Observation bytes | 记录 Observation address，继承 recovery | 重新 cutoff 或重复注入 |
| terminal Action durable，lease 未 consume | raw completed turn + SQLite lease/items | 同事务 notices -> Consumed + exact receiving Action address，再删除 lease/items | 保留 Consumed lease 或因上层返回丢失而 rollback |
| lease/evidence 冲突已 quarantine | active `Quarantined` lease/items/evidence | 保留 active 并 fail closed | 删除 evidence、恢复 notices 或开新 cutoff |
| capture 后 SessionJournal Undo | SQLite batch 仍是 delegation authority | 不修改 outbox/reply state | retract queued mail 或重新武装 consumed reply |
| route/lease identity 冲突 | current row 与 external/raw evidence 不能同时成立 | 持久 quarantine，停止该 route/lease | last-write-wins 或自动修复 |

## 11. Production hard cut（已完成）

`GalateaHostService` production composition现在只构造durable owner：Completion/RecapGrid、normalizer、
每个user的rendered extractor等fallible preflight全部成功后，最后构造host-wide
`GalateaDelegationSupervisor`。所有extractor共享Completion owner的lazy borrowed client，但各自冻结角色prompt
与ContractId。这是因为
existing writable store可能立即被pulse；composition不能在此后再保留会使host半构造失败的preflight。

Supervisor拥有一个shared lazy V2 transport及每user store/driver。Existing state目录只在matching session
目录也存在时于host启动strict-open并取得lifetime writer lock；`SESSION_MISSING`在store open前fail closed。
Missing state目录只在第一次writable SessionJournal attach时按§4创建
baseline。Maintenance只read-only open existing store，不attach writable session、不启动scheduler，也不
执行transport call。

Production source已删除旧C# coordinator/ledger/ReplyInbox/V1 client以及Node V1 entry/adapter/protocol。
同一extraction batch没有dual-write，reply cutoff没有双authority，`npm run start:galatea`只指向durable V2。
这里没有hidden feature flag、fallback branch、`AbandonDurableCandidate`或任何恢复旧owner的operator路径；
store/baseline失败只会使该user fail closed，不能靠删除durable evidence继续运行。

## 12. 工作包

| WP | 状态与主要 commits | 已交付 |
|---|---|---|
| WP0 | Complete — `6aab3310` | authority、非目标、crash matrix与hard-cut方向锁定 |
| WP1 | Complete — `b95134e7`, `10310b2d` | exclusive writer lock、strict SQLite current state、bounds、reopen与transition seams |
| WP2 | Complete — `a09ef6f5`, `3f96280a` | physical frontier与latest post-baseline Action extraction settlement |
| WP3 | Complete — `8ec6c19a`, `0c82339e`, `7f26aa02`, `30b9f2a5`, `6ba7b4cc` | Node/C# staged V2 transport、fixed-thread driver、OutcomeUnknown recovery |
| WP4 | Complete — `e3c68d23`, `5ac58d59`, `fd9e36d9` | exact Observation proof与durable reply lease/restart settlement |
| WP5 | Complete — `37a53bb6` | host-wide supervisor、per-user non-overlap、signal + 1秒fallback、shutdown drain |
| WP6 | Complete — `eaf5692a`, `0dcab030`, `ff9467ef`, `c35cdd9c`, `5c957f48`, `03d20259`, `b99ab11a`, `76e0f566` | Root V3、production hard cut、legacy C# owner删除、normalizer/admission/cleanup尾修与vertical gates |
| WP7 | Complete — `47f0efbe`, `10198814`, `bfdfdbc7`, `9cd3596d`, `fcb2c2f6` + tracked docs closure | Node V1/env hard cut、单一durable start入口、shared lifecycle tests、real V2 canary与current docs升格 |

任何要引入provider retry、capture-after-Undo撤回、dual-write、branch-following inbox、multi-thread或新operator
能力的改变，都需要重新设计决策，不是当前实现的机械延伸。

## 13. Regression contract 与 operational evidence

以下条目是current implementation必须持续满足的回归契约。Deterministic tests已经覆盖store、driver、lease、
supervisor、hard-cut composition与host restart vertical；2026-08-28的real V2 transport canary另验证了
empty binding、pre-start NotFound、exact一次start、duplicate local tombstone与inspect Completed，但没有构造
Galatea host/SQLite vertical。同日ignored `cyber` production smoke独立验证第一次writable attach自动baseline、
停服释放lock、SQLite quick check与cold reopen，但没有启动sidecar或调用provider。

### 13.1 Deterministic/store gates

- schema 对 unknown version/property/state、duplicate identity、illegal transition、oversize/invalid Unicode
  全部 fail closed。
- 两个独立 process 同时打开同一 user store 时 exact 一个成功、一个在任何 SQLite
  writer/open 前 fail closed；持有者 crash 后 OS 释放锁并允许新单 writer 恢复。测试不引入
  epoch/fencing 备用路径。
- 每个多表 transition 都有 before-commit/after-commit reopen 测试，只观察到完整旧/新状态。
- empty extraction 产生 durable tombstone；N 个 artifacts 只产生 exact N 个有序 rows；
  中途故障不存在 partial batch。
- baseline 之前历史零 extraction；baseline 之后 current selected latest Action 的 crash gap
  在下次 admission 前结算；跨 baseline rewind 后新 append Action 仍被处理。
- stable dispatch ID、FIFO、completion sequence、lease membership/rendered Observation 具有重启前后
  byte-exact golden。
- 不同user角色的extractor prompt/ContractId分离而共享lazy Completion client；historical capture保持zero-call。
- inbound envelope冻结validated To；player composite current neutral与legacy Galatea dialect都能exact读取，
  mixed dialect拒绝。legacy `ObservationBound|ObservationCommitted` lease可cold reopen并按raw evidence结算；
  schema仍为V1且没有migration/renderer列。

### 13.2 Effect/recovery gates

- crash/restart tests必须同时断言transport start count与最终state，不能只看最终state而遗漏重复effect。
- `Started/OutcomeUnknown` 恢复路径对 `turn/start` 是零调用，只允许 read-only
  reconciliation；unavailable/not-found 持久 backoff 并继续 OutcomeUnknown，只有确定性
  ownership/cwd/multiple/identity 冲突 durable quarantine；此路径必须始终带 known
  bound thread ID。
- `ensure-binding` 的deterministic protocol/backend tests证明它只执行thread start/name/verify，对
  `turn/start` 零调用。在 thread 建立与 `Bound` commit 之间崩溃可重新 ensure，但
  所有遗留候选都是没有 mail turn 的 empty orphan。
- 任何 mail（包括首封）只能在 `Bound(threadId)` durable 后 Started；所有
  信件只使用该 exact bound thread，不存在 accepted mail 反向建立 binding 的路径。
- mail 没有 `Prepared` state 或 Started-to-Prepared 退回；`Queued -> Started` 与冻结
  thread/policy、设置 route active dispatch 是一个 transaction，provider I/O 只发生在其后。
- accepted后signal或inspection attempt丢失时，使用persisted thread/turn继续read-only inspect final，不重发body；
  staged V2没有可等待的terminal Task。
- capture 后 Undo 不减少 outbox，不 interrupt active turn，不清除 Ready notice。
- reply lease 覆盖 cutoff 后 bind 前、`BindObservationBase` 后 Observation 前、Observation 后、
  terminal Action 后四个崩溃窗口；bind 前无 SessionJournal head/body，在 desired setup
  reconciliation 后紧邻 `SendAsync` 前才冻结 exact fresh base/Observation；
  每个 notice 最多注入一次，Consumed 不随 Undo 重新武装。
- rollback 与 consume 都在一个 transaction 内先 exact 结算全部 notices，再删除
  temporary lease/items；settled 后数据库中无 RolledBack/Consumed lease 或 rendered Observation
  副本。consume 后每个 notice 为 Consumed 且带相同 exact receiving terminal Action
  address。Quarantined lease 仍 active、保留 evidence 并阻止新 cutoff。
- signal 全部丢失时，只依赖 1 秒 fallback 仍能把每个可推进状态推进；
  signal/timer 并发不产生重复 effect。

### 13.3 Hard-cut 与 operational evidence

- Production composition只构造durable supervisor/store/driver/lease；source scan保持旧process-local owner、
  V1 C#/Node business protocol与live dual-write/fallback为零。
- Existing store在matching session存在时eager strict-open，`SESSION_MISSING`preempt store open；missing store在第一次writable session attach自动创建baseline。Baseline
  failure、store mismatch与writer-lock conflict都fail closed，没有operator abandon或旧owner fallback。
- `prototypes/Galatea/README.md`、root config V4 current contract与本设计必须同步current schema/path/
  runbook/failure semantics。
- Focused store/state-machine/Galatea tests、full Galatea tests、solution build、Node suite、docs checker与
  `git diff --check`是后续修改必须重跑的常规gate；repository durability tests使用真实稳定存储，不用tmpfs
  伪装durability。
- 已通过的current real app-server V2 transport canary证明`ensure-binding`建立empty fixed thread、pre-start
  inspection为NotFound、exact一次`start-turn`、duplicate在本地tombstone拒绝且最终inspect Completed。它不证明
  production supervisor创建baseline、accepted与final之间C# host restart、第二封续用同thread或reply lease
  one-shot consume；其中baseline/lock/cold reopen已由独立无provider的ignored开发实例smoke验证，其余可由future
  full-host provider vertical另行验证。任何canary/smoke都只证明该exact
  build/environment，不升格为app-server/provider exactly-once承诺。
