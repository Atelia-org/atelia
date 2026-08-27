# Galatea Codex delegation durable state machine

> 状态：In Progress（implementation contract）
>
> 启动日期：2026-08-28
>
> 现行产品契约仍为 [`prototypes/Galatea/README.md`](../../prototypes/Galatea/README.md)；
> 本文只在 durable hard cut 完成后才升格为产品现状。

本文是 Galatea Codex delegation durable 子阶段的设计与实施真源。它将已经跑通的
process-local 闭环收口为一个 Galatea-owned、per-user、SQLite-backed current-state
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
5. 使调度不依赖内存 signal 可靠到达：任何 signal 只是降低延迟的提示，1 秒
   fallback pulse 必须最终发现所有可推进的盘上状态。
6. 在不让主线角色 LLM 看到 delegation tools、不改变 SessionJournal provider
   tool-result continuation 语义的前提下完成上述目标。
7. 以 per-user database lifetime exclusive OS writer lock 保证任一时刻至多一个
   process writer；这是 P0 正确性前提，不用 epoch/fencing 将多 writer 合并成可接受状态。

### 1.2 明确非目标

- **不承诺 provider-call exactly-once。** 我们承诺稳定 operation identity、先写
  Started state、不透明重发 outcome-unknown effect，以及能够证明时的 exact
  reconciliation。Codex app-server 或下游 provider 没有提供的 exactly-once 语义，
  Galatea 不伪称已经拥有。
- 不做 multi-thread、thread rollover、thread selection 或长 thread 性能治理。
- 不将 inbound mailbox 入口或 inbound mail 持久化纳入本工作包。
- 不新增 browser UI 通知、operator UI 或人工解除 quarantine 流程。开发时可观测性
  继续使用 bounded `DebugUtil.Info/Warning/Error`。
- 不让 delegation state 随 SessionJournal fork/rewind/rollback 分叉，不尝试“撤销”
  已发生的 Codex side effect。
- 不引入 effect-event journal、event-sourced projection 或一份与 current-state tables
  并行的持久事件真源。
- 不在 hard cut 前迁移、删除或同时驱动现行 process-local owner。

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

Codex app-server 持久的 owned thread/turn 唯一决定外部 thread 是否已建立、turn 是否
已接受或 terminal，以及 exact final 是什么。Node sidecar 和 C# transport 只是
协议适配器，不是 durable ownership authority。

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

durable hard cut 不能把现有会话中所有历史 Action 当成新邮件重放。每个新 database
必须在停服 cutover 中显式建立一次 `captureFromPhysicalFrontier`：

- 记录 repository 当时已知的 **physical append frontier**，以及当时 selected
  raw head 作为诊断/evidence；
- normal clean repository 中 current head 通常就是 physical frontier；
- 如果可能存在 orphan tail，现有 public API 又不能证明 exact physical frontier，
  实施阶段只补一个 bounded、read-only frontier seam，不扩张成新的 full-lineage
  projector；
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

baseline 创建是 hard cut 的 no-return publication point，不是可试运行的 preflight。所有可回退
precondition（exclusive lock、path/schema 创建能力、旧 ledger drain、无 live turn/exchange、
composition candidate 可构造、必要测试）必须在任何 production baseline 写入之前完成。
baseline 一旦建立，不得保留 candidate database 而直接恢复 process-local owner。

## 5. SQLite current-state schema

### 5.1 物理边界

- database 归 Galatea user/session 所有，路径与 SessionJournal repository 共用同一
  user lifecycle，但不放进 raw EventJournal 或 RecapGrid derived roots。
- 每个 user database 必须在 SQLite open、baseline/candidate 创建或任何恢复之前，取得一把
  跨进程的 **lifetime exclusive OS writer lock**。lock 从 user durable owner 构造前一直持有到
  pulse/sidecar 已停止、database 已 dispose 之后；process crash 由 OS 释放锁。取锁失败时
  该 user fail closed/unavailable，不打开第二 writer。
- 本阶段不设 writer epoch、lease epoch、fencing token 或多 writer last-write-wins。SQLite
  row revision 只防止同一 writer 内的 stale transition，不替代 OS lock。lock 文件/路径的
  no-follow、canonical containment 与 filesystem lock semantics 必须由 focused test 验证。
- 只允许一个 schema version、strict open 和 code-owned migrations。本阶段没有可兼容的已发布
  durable schema；若 schema 尚未 hard cut，优先直接重写而非保留兼容层。
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
2. host 在同一 per-session admission/turn serialization 边界内运行 extractor。
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

accepted `threadId/turnId` 持久后，内存 terminal Task 只是低延迟通知。它丢失时，
pulse 以 exact IDs 读取 app-server persistent state。对同一 accepted turn 的重复 terminal
observation 必须幂等：同一 terminal 零修改，不同 final/status 冲突进 quarantine。

写入 `TerminalCompleted|TerminalFailed`、分配 `completionSequence`、创建 exact-one
`reply_notice` 和清除 route `activeDispatchId`（仍保持 `Bound(threadId)`）必须在同一
SQLite transaction。完成后发
signal，但 signal 丢失不影响 1 秒 pulse 最终发现 ready notice 或下一封 queued mail。

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

调度器是对盘上状态的反复有界求值，而不是一个需要保存 continuation 的长工作流。
每次 pulse 最多对每个 user 拥有一个 in-flight driver，并执行有限数量的：

- settle pending reply lease from exact raw evidence；
- settle latest post-baseline Action capture gap；
- ensure an unbound fixed thread without starting any mail turn；
- `Queued -> Started` 的一个 safe dispatch；
- reconcile 一个 OutcomeUnknown；
- poll 一个 accepted turn；
- publish 一个 terminal notice/release next FIFO item。

业务 transition 每次先读取 exact state/revision，在单事务中 claim，事务外执行必要的
provider I/O，然后以 exact claim/identity 结算。任何进程内 Task 只是当前尝试，不是
下次启动所需的恢复数据。

每次 durable commit 后发送 host-local signal 以尽快再 pulse。无论 signal 是否丢失、合并或发生在
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
| `Accepted` durable，terminal Task 丢失 | SQLite 有 exact thread/turn | pulse 读 app-server persistent turn | 重发 task body |
| app-server terminal，SQLite 未记录 | external terminal authority 存在 | 以 exact IDs 读取并幂等写 terminal+notice | 根据 log 猜 final |
| notice Ready，signal 前 | `reply_notice=Ready` | 后续 player cutoff 或 1 秒 pulse 可见 | 丢弃 notice |
| cutoff frozen，desired setup reconciliation 前/后但 bind 前 | lease 只有 membership/player text，无 SJ base | 同事务 notices -> Ready 并删除 lease/items | 保留 RolledBack lease 或从 current head 猜测 bind |
| `ObservationBound` commit，Observation 未 commit | lease 有 exact fresh base + Observation bytes | current head 仍为 base 时，同事务 notices -> Ready 并删除 lease/items | 保留 rendered Observation 或盲目 consume |
| Observation durable，lease 仍 `ObservationBound` | raw selected lineage 有 exact base/Observation bytes | 记录 Observation address，继承 recovery | 重新 cutoff 或重复注入 |
| terminal Action durable，lease 未 consume | raw completed turn + SQLite lease/items | 同事务 notices -> Consumed + exact receiving Action address，再删除 lease/items | 保留 Consumed lease 或因上层返回丢失而 rollback |
| lease/evidence 冲突已 quarantine | active `Quarantined` lease/items/evidence | 保留 active 并 fail closed | 删除 evidence、恢复 notices 或开新 cutoff |
| capture 后 SessionJournal Undo | SQLite batch 仍是 delegation authority | 不修改 outbox/reply state | retract queued mail 或重新武装 consumed reply |
| route/lease identity 冲突 | current row 与 external/raw evidence 不能同时成立 | 持久 quarantine，停止该 route/lease | last-write-wins 或自动修复 |

## 11. Hard-cut 策略

### 11.1 Dormant-before-cut

hard cut 之前，新 store、state machine、pulse 和 reconciliation 实现可以被单元/集成测试
直接构造，但不得被 production `GalateaHostService`/`UserSessionHost` 活路径构造或调用。
现行 process-local coordinator 在此期间仍是唯一 live owner。

禁止 live dual-write：

- 不允许同一 extraction batch 同时写内存 ledger 和 SQLite；
- 不允许两个 pump 竞争同一 dispatch；
- 不允许内存 ReplyInbox 和 durable notices 同时为 cutoff authority；
- 不用隐藏 feature flag 在同一 live 进程中切换 owner。

### 11.2 Atomic hard cut

hard cut 是一个可审查的单次产品切换：

1. **baseline 前的可回退 preflight：**停服，确认无 live Galatea turn、无 active
   sidecar exchange，现行 process-local queue/inbox/lease 已 drain；验证 exclusive OS
   locks、database path/schema candidate、physical-frontier read seam、new composition 构造与全部
   cutover tests。任一失败都在无 baseline 时取消，可继续旧 owner。
2. **no-return publication：**为每个 user 创建/strict-open candidate database，记录
   explicit `captureFromPhysicalFrontier` 和 current selected head。从第一个 production baseline
   commit 开始，不得直接恢复旧 owner。
3. 在 composition root 中一次性将 capture、pump、thread binding、reply cutoff 全部切换到
   durable owner。
4. 删除或断开现行 process-local ledger 的 production call sites；不保留 fallback branch。
5. 运行完整 restart/crash/E2E/canary gates，然后更新
   `prototypes/Galatea/README.md` 与阶段状态。

如果 baseline publication 之后、live durable owner 启动之前发生失败，系统保持停服。只有一个
显式、停服下的 `AbandonDurableCandidate` 流程完整删除/废弃所有 candidate databases，
并验证不再存在任何 published baseline 后，才可恢复旧 owner。下次 cutover 必须重新
执行全部 preflight 并创建全新 baseline；不复用 abandoned database/frontier。

## 12. 工作包

| WP | 状态 | 边界 | 必须交付 |
|---|---|---|---|
| WP0 | Complete | 设计收口 | 本文、status 分阶段、authority/非目标/crash matrix 锁定 |
| WP1 | Complete (`b95134e7`) | dormant store/kernel | lifetime exclusive OS writer lock、strict SQLite current-state tables、bounds、transactions/reopen、baseline、capture/outbox/route/reply lease kernel；未接 production composition |
| WP2 | Pending | dormant capture | latest-terminal-Action gap settlement、all-batch/empty tombstone、first-commit-wins、capture-after-Undo 契约测试 |
| WP3 | In Progress | staged sidecar/outbox | Node V2 staged backend/protocol/adapter seam已于 `8ec6c19a` 完成；C# V2 transport、pulse/driver 与 live entry 仍 Pending |
| WP4 | Pending | dormant inbox/lease | `CutoffFrozen -> BindObservationBase -> ObservationCommitted`、durable Ready/Leased/Consumed、exact Observation/Action evidence、restart one-shot matrix |
| WP5 | Pending | dormant host orchestration | signal + 1s fallback、per-user non-overlap gate、startup/admission recovery，仍不接 live owner |
| WP6 | Pending | hard cut | baseline-before/after no-return gates、candidate abandon 流程、atomic composition switch、旧 production call sites清理、README 升格 |
| WP7 | Pending | closure | full/focused tests、fake-sidecar crash matrix、real app-server restart canary、independent review、status Complete |

后续 WP 可在不改变本文契约的前提下继续细分，但不得跳过 dormant store/
state-machine 验证直接 live cutover。任何要引入 provider retry、capture-after-Undo 撤回、
dual-write、branch-following inbox 或新 operator 能力的改变，都需要重新设计决策，不是实施细节。

## 13. 验收门禁

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

### 13.2 Effect/recovery gates

- crash matrix 每一行都有 deterministic fake-sidecar/failpoint test；测试必须断言 provider
  start count，不只断言最终 state。
- `Started/OutcomeUnknown` 恢复路径对 `turn/start` 是零调用，只允许 read-only
  reconciliation；unavailable/not-found 持久 backoff 并继续 OutcomeUnknown，只有确定性
  ownership/cwd/multiple/identity 冲突 durable quarantine；此路径必须始终带 known
  bound thread ID。
- `ensure-binding` 的 fake/live protocol 证明它只执行 thread start/name/verify，对
  `turn/start` 零调用。在 thread 建立与 `Bound` commit 之间崩溃可重新 ensure，但
  所有遗留候选都是没有 mail turn 的 empty orphan。
- 任何 mail（包括首封）只能在 `Bound(threadId)` durable 后 Started；所有
  信件只使用该 exact bound thread，不存在 accepted mail 反向建立 binding 的路径。
- mail 没有 `Prepared` state 或 Started-to-Prepared 退回；`Queued -> Started` 与冻结
  thread/policy、设置 route active dispatch 是一个 transaction，provider I/O 只发生在其后。
- accepted terminal Task 丢失后使用 persisted thread/turn 读取 final，不重发 body。
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

### 13.3 Cutover/closure gates

- hard cut 前 production composition 对新 store/state machine 零构造、零读写、零调度；
  测试只经显式 harness 构造 dormant components。
- 所有可回退 hard-cut preconditions 在第一个 production baseline 前验证。failpoint 证明
  baseline 后失败会保持停服，不直接运行旧 owner；只有显式 abandon/delete
  所有 candidate DB 并证明 baseline 消失后才可回旧 owner，下次 cut 产生全新 baseline。
- hard cut commit 不存在 live dual-write/fallback；source scan 能证明旧 process-local owner
  production call sites 为零。
- `prototypes/Galatea/README.md` 只在 hard cut 后修改，并同步记录 schema/path/
  operator runbook 与 failure semantics。
- focused store/state-machine/Galatea tests、full Galatea tests、solution build、`git diff --check`
  全部通过；与 repository I/O 相关的测试使用真实稳定存储，不用 tmpfs
  伪装 durability。
- real app-server canary 至少覆盖：`ensure-binding` 先建立不含 mail turn 的
  fixed thread、首封只在 `Bound` durable 后启动、C# 在 accepted 与 final 之间重启、
  原 thread/turn 恢复 final、第二封续用同 thread、两封 reply 均
  one-shot consume。canary 通过只证明该 exact build/environment，不升格为 provider
  exactly-once 承诺。
- independent reviewer 确认 authority、crash matrix、no-dual-write 和非目标没有被实现
  侵蚀，才能将状态改为 Complete。
