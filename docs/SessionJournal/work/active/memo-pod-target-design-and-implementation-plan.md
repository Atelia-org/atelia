# MemoPod 动态外置记忆目标设计与分步骤施工计划

状态：Reviewed MemoPod core target；WP-00–WP-06、Track C1与Track C2 provider-free candidate不受rollback影响；authenticated
canary仍NotRun；**WP-07 Design Reopened**，旧WP-07A/B已撤回，B1已由`1d8c33bb`回滚，旧Gate A不可续用，
Gate B/B2 canceled且从未授权

本文定义一种与 RecapGrid 互补、面向具体事务细节的动态外置记忆单元，并把后续实现拆成可独立审阅、
验证和收口的工作包。本文是 target/plan，不是当前实现事实；当前事实仍以 owning code、tests 与
[SessionJournal 当前架构地图](../../current/architecture-and-code-map.md)为准。

current MemoPod是standalone library/operator能力，没有product upper consumer；其production显式`ProjectReference`仅为
`Completion.Abstractions`，不依赖SessionJournal、Galatea或RecapGrid。

状态证据只作导航：WP-00 target lock为`f0121f2b`；WP-01至WP-05 product/test链已收口到`7cd69639`；WP-06
fake-first operator及tail evidence为`6f2000d6`、`eaa57715`；Track C1为`18f168b8`；Track C2的provider-free live
runner/evidence为`2fa1ee3b`，不构成真实DeepSeek cache/质量/价格证据；WP-07A plan review收口于`d5a403c4`，Tier-A
Candidate及review tail为`edfe5230`、`19776980`，historical B1为`83477c06`。用户于2026-08-20撤回旧方向；
[historical evidence](../../evidence/completion-request-prepared-v6-candidate.md)保留原implementation/review事实，code/test由
`1d8c33bb`回到`a5098a77` exact SessionJournal tree。Gate B never granted，promotion never started，旧B2 never authorized。

## 1. Intent

为 SessionJournal/Galatea 产品族增加一个刻意简单的 `MemoPod`：一个 Pod 由caller以显式 `Topic` 声明同一主题，
并容纳以committed stable ID
索引的多条 `Memo`，只通过`Append`、`Remove`与read API改变条目集合，并可在 Frozen 阶段把全部条目
确定性投影成专职记忆召回 Agent 的
稳定 prompt prefix。召回 Agent 只返回 Memo ID；宿主验证 ID 后，从仍处于 Frozen 状态的同一个 Pod
取回 exact Memo 正文。

首版只机械验证Topic和正文的shape/bounds，不判断条目在语义上是否真的属于该Topic；归类正确性属于上层
caller责任，未来自动聚类器也只能通过显式mutation改变Pod内容。

首版不做自动提取、主题聚类、多 Pod 聚合或向量检索。先证明最小单元的状态机、持久化、确定性渲染、
LLM ID 召回与 prompt-cache 经济性成立，再决定更高层结构。

## 2. MemoPod 与 RecapGrid 的互补边界

MemoPod 不是轻量版 RecapGrid，RecapGrid 也不是粗粒度 MemoPod。二者针对不同的记忆失败模式：

| 维度 | RecapGrid | MemoPod |
|---|---|---|
| 首要问题 | 有限上下文窗口下长期对话如何保持连续 | 已知具体事务细节如何在需要时被按 ID 保存并尝试找回 |
| 主要承诺 | 身份连续性、信念连续性、长期叙事/世界理解连续性 | 对已经明确写入的细粒度事务信息提供 exact ID-addressed 保存；LLM召回质量另行验证 |
| 典型内容 | “老王是多次合作的回头客”、关系变化、长期判断与未决疑点 | 某次报价、交付日期、承诺条款、联系人偏好、具体往来细节 |
| 遗忘取向 | rolling rewrite 允许省略大量事务细节，只保留足以延续认知的摘要 | 偏好保留琐碎但将来可能有用的具体事实；召回时允许不选中，但 Store 不主动概括掉正文 |
| 组织方式 | Timeline row × Maintainer column 的 immutable derived cells | caller归类的同主题 Pod 内committed stable Memo ID → immutable Memo value的可编辑集合 |
| 进入主上下文 | 作为连续、低分辨率的长期 context base | 根据当前 query 返回少量 ID，再注入所选 Memo 的 exact text |
| 权威边界 | raw selected Parent lineage 是历史正文事实源；Timeline/Cadence/Control/Store各有owner-bound companion authority，Cells/Views是canonical derived artifacts | Pod durable document只权威表示“提交了哪些exact text”，不证明文本中的客户事实真实、最新或来源可信；未来提取器只可提出或应用显式 mutation |
| 更新方式 | 随历史分段 rolling maintenance/rebuild | 上层进入 Editable 阶段后显式Append/Remove，再Freeze成新的可查询状态 |

因此二者可以同时参与一次主 Agent 请求：RecapGrid 提供“我是谁、我相信什么、关系大体如何”的连续
背景，MemoPod 提供“这次问题涉及的具体日期、承诺、编号和往来细节”。RecapGrid Maintainer当然可以
独立概括包含这些事实的经历；禁止的是把Memo条目1:1镜像成Cell、把Grid当作Memo ID Store，或要求
RecapGrid承诺事务明细完整性。这里描述的是Galatea对两种产品的职责分工，不是限制generic RecapGrid
未来可以分析哪些题材。

## 3. Goals and non-goals

### 3.1 Goals

1. 一个 `MemoPod` 有caller负责且Pod-lifetime immutable的显式主题，并以committed stable、Pod-local
   `MemoId` append/remove/read多条Memo。
2. 用单对象 `Editable ↔ Frozen` 状态机取代并发 snapshot/version 系统。
3. Frozen 时所有写操作直接拒绝；Editable 时production `RecallAsync`直接拒绝；renderer/raw resolver不作为
   production lifecycle API暴露。
4. Frozen 状态缓存一份 exact canonical provider-neutral context projection，重复查询复用完全相同的前缀。
5. 召回 Agent 只能提交有序 Memo ID；production API在一次Frozen调用内完成query、验证与hydrate。
6. storage document、prompt render 与 provider request projection 分层，任一格式升级都不会悄悄改写另一层。
7. correctness 不依赖 prompt cache 是否命中；cache 只影响费用和延迟，并由真实 provider telemetry 验证。
8. 首版保持 single owner、顺序编排、单进程内无并发读写的简单约束。

### 3.2 Non-goals

- 不实现主题发现、聚类、拆 Pod、合 Pod或跨 Pod routing。
- 不实现从 raw SessionJournal 自动提取 Memo，也不实现 ExperienceRefiner/tool-assisted mutation。
- 不实现 embedding、ANN、全文索引、BM25 或混合召回。
- 不实现 `MemoPodSnapshot`、MVCC、revision CAS、并发 reader/writer 或多进程共享 writer。
- 不提供Memo原位Update/Replace或caller-supplied upsert；纠错由同一Editable阶段内Remove旧ID并Append新ID完成。
- Topic在`Create(root, podId, topic)`后Pod-lifetime immutable；改主题或重分类属于未来跨Pod操作。
- 不承诺一次mutation立即durable；首版以“写阶段在 Freeze 时整体提交”为transaction boundary。
- 首版不实现tombstone、delta overlay或compaction；V2 Remove仍物理移出下一份committed active集合。
- 不让 MemoPod 依赖 RecapGrid、HistoryTimeline 或具体 provider client。
- 不在 SessionJournal core 中新增 MemoPod storage/query owner。
- 不在首版定义跨 SessionJournal repository、跨用户或跨 Agent 的共享/授权模型。
- 不承诺删除等同于secure erasure；provider cache/log、filesystem remnants与既往Prepared request有各自retention边界。
- 不把 cache hit、LLM 召回结果或 provider response 当作 Memo authority。

## 4. Core model

### 4.1 Minimal values

首版只需要以下概念：

```text
MemoPod
  PodId
  Topic                   // Pod-lifetime immutable
  HandleValidity = Active | Invalidated
  Phase = Editable | Frozen
  OrderedMap<MemoId, Memo>
  NextMemoId
  Dirty
  FrozenPrompt?          // internal cache；不从production API detached返回
  FrozenPromptSha256?    // telemetry/debug identity，不是第二份 authority

Memo
  MemoId
  Title?
  Gist?
  Summary?
  ExactText

MemoRecallResult
  Ordered immutable Memo values
  FrozenPromptSha256
  provider usage / bounded diagnostics
```

`Phase`只在`HandleValidity=Active`时有意义；它仍然只有Editable/Frozen两个正常业务阶段。
`CommitIndeterminate`把handle正交地置为Invalidated，之后所有API均拒绝；首版没有long-lived backend handle或
Close语义，调用方只能丢弃该对象并重新`Open`。这不是可查询、可编辑或可恢复的第三业务phase。

`Memo`是immutable value。一个committed active `MemoId`在其整个可见生命周期内只对应一份`ExactText`
与一组可空metadata；不能以相同ID替换正文或metadata。`Title`、`Gist`、`Summary`是为后续渐进式召回、
索引与叙事化目录预留的非唯一字段；`null`表示尚未生成或未知，非`null`时必须是单行、已trim、非空、
strict UTF-8 bounded字符串。身份与引用稳定性仍只由`MemoId`承担。

Memo entry surface收窄为`Append(exactText, title?, gist?, summary?) -> MemoId`、`Remove(existingId)`与`Get/TryGet/List`。
不提供原位Update/Replace、caller-supplied insert/upsert或removed-ID revival。纠正文案时，caller在同一个Editable
阶段执行`Remove(oldId)`再`Append(correctedText)`；下一次successful Freeze把两步整体提交，因此reader只会观察
旧committed状态或“旧ID消失、新ID出现”的新committed状态。

`MemoId` 必须满足：

- Pod-local、短、ASCII、可严格解析；
- Editable中新分配的ID在successful Freeze前只是provisional，只能用于本次working phase内的Append/Remove/read；
- successful Freeze后成为committed stable ID；其active exact text不可改变，只能整体Remove；
- committed ID以及已写入durable allocator high-water的allocation hole均不得复用或revive；
- owner 单调分配，使新增 Memo 在 prompt 中自然追加到旧条目之后；
- 不从正文 hash 推导；任何纠错都获得新的MemoId。

provisional ID不得在Freeze成功前发布成foreign key、业务receipt或其他Store中的稳定引用。若未来业务要求
`Append`返回时ID就具备跨崩溃稳定性，必须另立durable reservation方案；首版不为此引入allocator journal。

具体 token 形状和上限在 WP-01 锁定，并以 parser/golden tests 固化。

### 4.2 Authority and working-state table

| 状态/载体 | Authority semantics |
|---|---|
| last durable Pod document | reopen时已提交Memo与allocator high-water的authority |
| Editable working set | 未提交proposal；可丢失，provisional IDs不得外发 |
| Frozen in-memory Pod | 与last durable document一致的当前query owner；可重复recall |
| internal frozen prompt/hash | 从Frozen Pod确定性派生的query cache，不是独立Memo authority |
| future Prepared inline Memo text | 只对那一个main request的execution/recovery负责；Pod后续编辑不改写它，也不让它成为当前Memo authority |

### 4.3 Phase state machine

```text
Create(root, podId, topic) ---------> Editable
Open durable document --------------> Frozen

Editable
  Append / Remove
  FreezeAsync
    validate complete working state
    build storage bytes + frozen prompt
    atomically commit durable document when dirty
    publish cached prompt
    transition to Frozen

Frozen
  RecallAsync                // public query -> validate -> hydrate closed operation
  repeated queries over the exact same frozen prompt
  ResumeEditing
    invalidate frozen prompt/cache metadata
    transition to Editable
```

状态转换是顺序编排合同，不是并发算法：

- `MemoPod` 明确声明为非线程安全；调用方不得并发调用任何成员。
- Frozen 时，`Append`、`Remove`、ID allocation 等所有 mutation 统一抛出
  `InvalidOperationException`。
- Editable 时，production `RecallAsync`抛出`InvalidOperationException`。
- `FreezeAsync` 与 `ResumeEditing` 对非法重复转换 fail fast，不做隐式 no-op。
- 不暴露可变 collection、可变 Memo 或可绕过 phase gate 的 backend handle。
- `Get/TryGet/List`在Editable和Frozen均可用，只返回当次物化的immutable values；不返回lazy/live view。
- frozen prompt、raw renderer output与ID resolver保持internal/test-only，production调用方不能把旧prompt带到新epoch。

一次完整 read phase 必须覆盖：

```text
Freeze
  -> RecallAsync内部使用当前cached prompt发起provider recall
  -> RecallAsync内部parse/validate IDs
  -> RecallAsync内部从同一个still-frozen Pod resolve IDs
  -> copy immutable Memo values into self-contained MemoRecallResult
  -> only then ResumeEditing
```

`MemoRecallResult` 返回后不得继续引用 Pod 内部 collection。这样即使 Pod 随后恢复编辑，已经召回的结果仍
保持本次请求需要的 exact text。

provider failure、invalid model output与caller cancellation都不自动改变phase或清除cached prompt；Pod仍
Frozen，可再次Recall或由调用方显式`ResumeEditing`。`ResumeEditing`是同步、无I/O、不可取消的转换。

如果durable publish settlement无法确定，当前handle进入exceptional invalidated/poisoned状态并只能discard；
这不是第三种正常phase，也不能继续任何业务API。调用方必须重新`Open`观察strict durable bytes。

### 4.4 Why no snapshot

本设计显式选择 temporal separation，而不是 concurrent snapshot isolation。Freeze preparation允许产生一份只在
本次调用中使用的immutable commit candidate，供codec与renderer读取；它不会从public API逃逸、不会跨epoch保留，
也不参与旧ID的后续resolve。本设计删除的是public/retained/versioned `MemoPodSnapshot`、revision、CAS、
snapshot-scoped lookup与多版本回收；代价是上层必须保证：

1. 同一个 Pod 同时只有一个 owner；
2. 召回期间不会恢复编辑；
3. 所有 Memo hydrate 都在 Frozen 阶段内完成；
4. 未来一旦出现真实并发、多 handle 或跨进程共享需求，必须重新立项，不得用零散 `lock` 把当前合同伪装
   成 snapshot-safe。

2026-08-30新增的`ComputeStateIdentity()`不改变该边界。它只返回
`atelia.memo-pod.document.v2.sha256:<lowercase-hex>`：digest输入是当前working aggregate经
`MemoPodDocumentCodec.Encode`得到的exact canonical complete document bytes。在Editable阶段它只是未提交candidate
identity，在Frozen阶段才表示该valid handle所代表的committed state；它不返回正文、revision、epoch handle或
snapshot-scoped resolver，也不提供CAS语义。indeterminate handle仍必须拒绝并要求fresh `Open`。

## 5. Persistence target

### 5.1 One aggregate document per Pod

首版采用`Create(root, podId, topic)`/`Open(root, podId)`形状，在caller-supplied root下以strict、path-safe的
PodId mapping保存“一 Pod 一个 versioned document”，不接受任意自由文件路径，也不引入SQLite。Pod 本来受
LLM context 与产品配置上限约束，可以在打开时全量读入内存；K-V 是领域 API 语义，不要求每个 Key 都有
独立物理文件。

建议 logical document：

```text
schema = atelia.memo-pod.document.v2
podId
topic
nextMemoId
ordered memos[]:
  id
  title
  gist
  summary
  exactText
```

V2 document是当前active Memo集合的materialization，不是operation history：Remove后该ID从下一份`memos[]`
省略，document不写tombstone或旧正文；`nextMemoId`仍保留allocator high-water，因此reader必须接受由Remove或
“provisional Append后同epoch Remove”形成的ID gap，并拒绝任何existing ID大于等于`nextMemoId`的文档。

storage document 与 provider prompt 是两种独立 canonical representation：

- storage codec 为 durable round-trip、strict parse、bounds 与 crash recovery 服务；
- prompt renderer 为模型可读性、exact byte identity 与 prefix stability 服务；
- 不能直接把 prompt 文本当作 storage wire，也不能让通用 JSON serializer 输出偶然成为 prompt contract。

### 5.2 Write phase as one transaction

Editable entry mutations只修改内存working state并设置`Dirty`。`FreezeAsync`是提交边界：

1. 在可取消的preparation阶段验证complete state、UTF-16/UTF-8、ID ordering、大小和重复项；
2. 预先生成canonical storage bytes与internal frozen prompt；
3. 若Dirty，在可取消阶段写same-directory temporary file并flush/close；
4. 在进入publish前执行最后一次cancellation check，随后进入不可取消的settlement fence；
5. `Create(...)`首次提交使用atomic no-clobber/create-if-absent，已从exact document `Open`的Pod才允许replace；
6. publish被证明成功后，只执行不应失败的内存赋值：`Dirty=false`、保存预计算prompt、切换Frozen；
7. publish前可证明未修改authority的失败/取消保持Editable与原Dirty；
8. publish可能已经发生但settlement不能证明时，invalidate当前handle并要求discard+reopen，禁止继续业务API。

成功publish后的temporary cleanup是best-effort diagnostics，不得把已经settled的Freeze改报失败。崩溃验收的
最低承诺是reopen只能接受完整旧文档或完整新文档，绝不能接受torn/mixed JSON；这与断电后的durability承诺
分开。目录fsync、Windows replacement与indeterminate settlement的精确平台合同由foundation store工作包
根据现有Atelia filesystem primitives锁定；不能在目标设计中虚构已经获得跨平台证明。

Linux current implementation另提供Frozen-only `ConfirmCurrentDocumentDurability()`：仅在fresh strict `Open`已经
观察并核对exact target identity后，重新验证current document path并fsync该Pod exact `memo-pods/v1/pods`
directory。它用于关闭rename已可见但先前directory fsync settlement未知的恢复窗口；正常
`FreezeAsync` proven Published已经完成相同directory sync，不需要重复调用。该方法不把indeterminate旧handle
恢复成valid，也不扩大跨平台durability承诺。

`Create(...)` 从 Editable 开始，首次 Freeze 才创建 durable document；`Open` 从已提交文档恢复为 Frozen。
`ResumeEditing` 本身不改变 durable bytes。进程在下一次 Freeze 前退出时，本轮尚未提交的编辑允许丢失。
`Create(...)`目标已存在时绝不覆盖；`Open`必须验证requested PodId、path mapping与document `podId`一致。
遗留temporary file不会自动晋升成authority。

`Dirty`只是保守的“需要publish”标志，不保存baseline snapshot来判断改后又改回：

| Operation | Resulting phase / Dirty |
|---|---|
| `Create(...)` | Editable / true，即使empty Pod也需要首次create |
| `Open` | Frozen / false |
| `ResumeEditing` | Editable / false |
| successful mutation | Editable / true |
| rejected mutation | state unchanged |
| pre-publish failure/cancel | Editable / prior Dirty preserved |
| successful Freeze | Frozen / false |
| indeterminate publish | current handle invalidated; reopen required |

`Append`在完成全部输入/overflow校验后，以单个内存步骤插入Memo、推进`NextMemoId`并设置Dirty。Open严格验证
IDs按allocation ordinal递增且唯一、允许gap、所有现存ID小于`NextMemoId`；字符串词法顺序不得把9→10等
边界排错。Remove missing/already-removed ID必须原子拒绝，不改变working set、Dirty或allocator high-water。

### 5.3 Bounds

首版必须显式限制：

- `PodId`、`MemoId` 和 Topic 长度；
- 单条 Memo UTF-8 bytes；
- 单条 Memo metadata UTF-8 bytes 与 active metadata 总 bytes；
- Memo count；
- 整个 Pod storage bytes 与 rendered prompt bytes；
- 单次返回 ID 数和 hydrated Memo 总 bytes。

超限必须在本地、provider call 之前 typed/fail-fast；不得 silently truncate，因为截断会改变“这个 Pod 可召回
哪些内容”的语义。durable hard cap必须先由本地资源预算锁定；route/model-specific默认cap与token estimator
可以在provider canary后单独收紧，不能让live provider结果反向决定storage wire能否解析。

`Open`只执行schema/local structural hard bounds并生成provider-neutral frozen prompt；当前route/model cap变小不得
使一个既有committed Pod无法Open、ResumeEditing或修复。route/model cap只在`RecallAsync` provider call之前
preflight；超限抛出typed local limit failure并保持Pod Frozen，不得误报为provider failure。

同一storage schema version的logical/storage/render hard bounds必须相容：任何可由`FreezeAsync`成功提交的V2
document都必须能被V2 `Open`重新生成prompt。renderer升级不得用更小hard bound把既有合法document变成孤儿；
确需收紧时必须升schema/renderer contract并另立migration设计。

Remove只承诺从下一次successfully committed active Pod state移除该ID。V2 storage document只保存active memos，
因此首版Freeze会把removed Memo物理移出下一份document；这是V2 representation，不是public API对未来backend的
承诺。Remove不承诺擦除历史Prepared request、provider
cache/log、backup、temporary/remnant blocks或底层介质。Galatea激活前必须另行关闭数据分类、日志正文、backup、
retention与secure-erasure non-promise。

## 6. Deterministic and cache-aware rendering

### 6.1 Provider-neutral projection

`IHistoryMessage` 是 provider-neutral projection unit，不是 storage wire。首版 renderer 在assembly内部产生：

```text
MemoPodFrozenPrompt
  ExactText
  Utf8Length
  Sha256
  ToHistoryMessage() -> ObservationMessage
```

`MemoPodFrozenPrompt`不从production API detached返回；它只由MemoPod/Recall service在当前Frozen epoch内部
消费。测试可以通过internal surface验证bytes/hash。

Recall request 使用现有 Completion abstractions：

```text
CompletionPromptPrefix
  SystemPrompt              stable retrieval instructions
  OutputContract            one required named recall tool
  SharedContextMessages     exactly one frozen MemoPod ObservationMessage

TailMessages
  exactly one ObservationMessage containing current query and maxResults
```

provider/model、reasoning effort、connection 与 cache behavior属于 composition/runtime policy，不进入 MemoPod
durable identity或 storage document。

### 6.2 Determinism versus prefix stability

renderer 同时满足两个不同合同：

1. **Deterministic**：相同 logical Pod state产生完全相同 UTF-8 bytes/hash。
2. **Prefix-stable append**：只执行`Append`产生更大MemoId时，旧Memo corpus结束前的bytes不改变，新条目只
   出现在旧corpus之后；exact longest-common-prefix边界由golden锁定。

为此：

- 用 MemoId ordinal排序，不依赖 dictionary iteration、locale、mtime或更新时间；
- 固定 renderer schema、UTF-8 no BOM、LF 与 escaping；
- Topic和 retrieval instructions可位于稳定前缀；
- revision、count、timestamp、整体 hash、trace id等易变信息不得放在 Memo corpus之前；
- query、`maxResults`、请求时间和调用追踪一律放在 tail；
- V2 Remove物理移出active entry，会从被删条目处破坏后续cache prefix；首版明确接受；
- Topic在Create后immutable；Memo metadata update仍通过Remove+Append获得新ID，不原位改写稳定entry；
- 纠错采用同一Freeze内`Remove(oldId) + Append(newText)`，因此V2的cache破坏范围仍从old entry开始；删除
  Replace的首版收益是ID/text不变量与API简化，不虚构额外cache收益；
- `ResumeEditing` 使 cached prompt失效；下一次 Freeze重新生成。

Frozen 阶段因此构成一个自然 cache epoch：可在完全相同的 prefix上执行多次 query。若产品实际每次 Freeze
之后只查询一次，cache收益可能很弱；实现必须观测 `queriesPerFrozenEpoch`，不能以静态单价代替真实证据。
Recall调用传入`PromptCacheReuseHint.ReuseExpectedSoon`表达经济意图；对DeepSeek当前只代表implicit/best-effort
行为，不是cache breakpoint或命中保证。2026-08-19复核的
[DeepSeek Context Caching](https://api-docs.deepseek.com/guides/kv_cache)也把cache描述为automatic、prefix-based、
best-effort，并通过usage的hit/miss token字段观测；这是可变provider事实，Track C实施和candidate激活时都要
重新核对，不能升级成MemoPod library invariant。

### 6.3 Deferred append-only tombstone and compaction evolution

若真实workload证明高频Remove导致重复prefill成本显著，后续V2可以在不改变public `Append/Remove/read`语义的
前提下，把Remove的物理表示演化成append-only tombstone。该方向是成本优化，不属于V1 correctness或activation
前置条件。

为了在“Remove之后又Append”时仍维持prefix，V2 renderer必须按时间顺序输出单一operation stream，例如：

```text
Append M1 <exact text>
Append M2 <exact text>
Remove M1
Append M3 <exact text>
```

不能把active Memo与tombstone分别渲染成两个固定分区；否则已有tombstone后再Append会把新Memo插入旧分区
中间，重新破坏prefix。V2 Open/renderer在宿主侧deterministically fold operation stream得到active view；
`Get/List/RecallAsync`只暴露active IDs，模型返回tombstoned ID必须由host拒绝。

tombstone会让已删除正文继续占用prompt tokens，并可能降低召回质量或延长上下文。积累到阈值后，compaction
原子开启new generation，生成只含active Memo的新canonical base并明确接受一次cache reset；只能承诺
generation内append-only，不能宣称Pod全生命周期prefix-stable。compaction必须保留全部active
`MemoId -> ExactText`映射与`nextMemoId` high-water，不能重排、复用ID或把旧ID绑定到新正文。触发策略不得只看
tombstone count，至少观测
dead prompt tokens、dead/total token ratio、预期后续query数、cache warm-up行为、latency与召回质量；阈值属于
route/operator policy，不进入MemoPod durable identity。需要隐私purge时不得等待成本阈值，必须走单独的即时
compaction/retention流程。V2 storage/renderer schema、migration、crash contract与compaction settlement必须另立
工作包，不能在V1 store中预埋半套operation log；V1→V2 migration只能以当时active set与high-water建立base，
不能虚构V1已经物理删除的历史操作。

## 7. Recall protocol

### 7.1 Model-visible task

召回 Agent 读取整个同主题 Memo corpus和本次 query，只判断哪些 Memo值得交给上层，不解释、不改写正文。
首版使用 DeepSeek V4 Flash的 non-thinking mode作为目标 canary route，但 library只依赖`ICompletionClient`和
provider-neutral contracts。

本地correctness只保证：committed Memo exact bytes、返回shape、ID存在性以及same-Frozen hydration。LLM是否
漏掉相关Memo、是否选中语义无关但合法的ID属于precision/recall质量指标，只能由fixture和canary评估，不能写成
“准确召回”的确定性承诺。

唯一 output surface 是 required named tool，例如：

```text
recall_memos
  memoIds: string[]   // relevance order; [] means no relevant Memo
```

首版不返回 score、reason、摘要或新 Memo proposal。`allowParallelToolCalls=false`；response必须恰好包含一次
目标 tool call，并且没有需要信任的自由文本正文。

`tool + Text block`和`tool + Reasoning block`都视为mixed output并拒绝；宿主不从附带文本中提取或猜测ID，
也不因已有一个合法tool call而忽略额外model output。

### 7.2 Host validation

production只公开完整的`RecallAsync(frozenPod, query, ...)`；provider output必须在该调用内部经过本地验证：

- exactly one expected tool call；
- arguments strict parse，拒绝 unknown/duplicate/reordered fields；
- ID count不超过 request cap；
- 每个 ID canonical、存在于仍 Frozen 的当前 Pod；
- 重复 ID拒绝而不是悄悄去重；
- preserved order就是召回相关性顺序；
- `[]` 是合法 no-match，不得与 provider failure混淆；
- validation完成后立即从 Frozen Pod复制 immutable Memo values，形成 self-contained result；
- 调用结束前再次确认Pod仍为同一Frozen epoch；在single-owner contract下这由禁止并发与不暴露detached
  prompt/resolve surface共同保证。

Memo正文是不可信模型输入。system prompt必须明确其只是待检索数据；召回客户端不得暴露除 terminal ID tool
之外的工具。Prompt injection仍可能影响选中质量，但不能获得写 Pod、写 raw journal、调用网络或执行任意工具
的能力。

### 7.3 Closed outcomes

production surface不混用result union与exception两套取消语义：

- 成功返回`MemoRecallResult`；其中合法ID list可为空，空表示no-match；
- tool/output shape或ID非法抛typed invalid-model-output failure；
- transport/terminal/provider failure抛typed provider failure；
- route/model preflight超限抛typed local limit failure；
- caller cancellation原样传播`OperationCanceledException`；
- Pod非Frozen或Invalidated属于本地lifecycle misuse，在provider call前抛出。

除成功返回外，上述failure/cancellation都不产生半成品result，也不改变Frozen phase或cached prompt。是否对
invalid model output做一次受限retry属于Host policy；首版默认不自动retry。上层可以明确选择“本轮不带 Memo
继续”或“拒绝主 completion”，但必须保留no-match与unavailable的区别。

wrapper只捕获并分类已知transport/protocol/provider失败；caller cancellation保持caller cancellation，
`OutOfMemoryException`等fatal异常和本地programming bug不得被宽泛`catch (Exception)`伪装成ProviderFailed。

## 8. Ownership and future SessionJournal integration

### 8.1 Initial assembly boundary

首版建议建立一个 sibling product assembly：

```text
prototypes/MemoPod/
namespace Atelia.MemoPod
```

它初期可以同时拥有 domain、file store、renderer和recall service，以减少空抽象；内部按 source module分区，
只有出现第二 backend或第二 recall protocol时再拆 assembly。它可以依赖 `Completion.Abstractions`，但：

- `SessionJournal` core不得反向依赖 MemoPod；
- MemoPod不得依赖 RecapGrid、HistoryTimeline、Galatea.Server或具体 provider assembly；
- provider construction、model route、root path和主 Agent failure policy由CLI/Host composition拥有。

### 8.2 Do not force current context-candidate seam

当前 `ICoherentContextCandidateSource` 面向 raw-lineage-addressed derived context，带有 admission anchor、setup
references和`AbsorbedThrough`。MemoPod 可能保存人工编辑、跨会话或外部业务事实，不能伪造 raw provenance。
首版不得为了接入主请求而给 Memo强加 EventAddress，也不得把 query-dependent recall伪装成静态 RecapGrid
candidate。

未来 Galatea vertical 应在独立工作包中回答：

1. recall query由当前 pending observation如何构造；
2. selected Memo exact text如何进入主 request context；
3. supplemental context使用新增generic seam、recipe evolution还是其他明确路径；不得偷塞进现有Recap
   contribution recipe；
4. `CompletionRequestPrepared`如何inline exact selected text、carrier/order/delimiter/recipe version和request
   commitment，使Prepared/Started reopen不重新查询或打开MemoPod；Prepared中的副本只对该request负责，
   不成为当前Memo authority；
5. recall发生在Prepared之前且进程崩溃时，是否明确允许重复provider调用和重复计费；
6. `NewRequestRequired`、`ToolContinuationRequired`以及tool result后的下一次completion是复用、重新recall，
   还是持久化turn-level selection；
7. Memo recall unavailable时 Host采取哪种显式 policy；
8. cross-session/user ownership、privacy、backup、retention与root binding如何定义。

在这些问题关闭前，MemoPod library可以独立完成，不修改 SessionJournal raw/wire/recovery合同。

### 8.3 Future mutation producers

未来自动提取器、主题聚类器或 ExperienceRefiner都不是 MemoPod Store本身。它们最多产生显式 mutation proposal
或在上层授权下进入Editable阶段调用Append/Remove。首版MemoPod不授予LLM直接写权限，也不预先设计exactly-once
operation ledger。真正需要可恢复外部 effect时，另立工作包定义 operation identity、apply/receipt/conflict语义。

## 9. Step-by-step implementation plan

每个工作包都执行 `re-review → plan lock → implement → focused review → tail-fix`。前一包的 target contract与
focused tests是后一包的输入；不允许为了跑通 vertical在后续包暗改早期 canonical contract。

```text
WP-00 -> WP-01 -> { WP-02, WP-03 } -> WP-04 -> WP-05 -> WP-06

WP-06 ----+
           +-> Track C2 provider-free candidate
Track C1 --+

WP-07 Design Reopened (no implementation edge until fresh design gates close)
```

Track C1/C2是route-specific证据旁路；它们不改变MemoPod library主链的correctness gate。

### WP-00：target lock and test fixtures

**Intent**

- 关闭本文 review findings，锁定首版的最小语义、状态机与明确禁区。

**In scope**

- 本文、文档 router、工作包验收矩阵；
- 两三个手写 MemoPod fixture：客户往来、项目约定、empty pod；
- 确认目标 assembly/test project命名。

**Out of scope**

- production code、provider调用、storage schema实现。

**Validation**

- SessionJournal docs checker、all-tracked report、relative link scan、`git diff --check`；
- independent architecture review确认 RecapGrid/MemoPod互补边界和Frozen简化没有自相矛盾。

**Done when**

- 所有 high-risk review findings已在本文收口；未决参数只保留为后续工作包明确输入。

### WP-01：project scaffold and Editable working aggregate

**Intent**

- 先建立可编译、可测试的product skeleton，并实现不冒充完整 `MemoPod` 的Editable working aggregate。

**In scope**

- product/test project、solution registration、dependency-direction test与最小README；
- `MemoPodId`、`MemoId`、immutable `Memo`、internal working state；
- `Append`/`Remove`/`Get`/`TryGet`/`List`、strict parsing与local bounds；
- provisional ID allocation、allocator high-water与atomic in-memory mutation；
- 不泄漏 mutable alias；
- fixtures和后续store/renderer均可消费的immutable document value。

**Out of scope**

- public `Create`/`Open`/`FreezeAsync`/`ResumeEditing`、persistence、prompt renderer、Completion request、concurrency。

WP-01不得交付“已经Frozen但尚无durable document或frozen prompt”的公开状态。完整phase machine只在WP-04
一次性成为production surface。

**Tentative write scope**

- `prototypes/MemoPod/MemoPod.csproj`
- `prototypes/MemoPod/Domain/`
- `prototypes/MemoPod/README.md`
- `tests/MemoPod.Tests/MemoPod.Tests.csproj`
- `tests/MemoPod.Tests/Domain/`
- `Atelia.sln`

**Validation**

- append/remove/read；provisional ID单调，Remove不回退high-water；
- Topic在working value中始终non-empty且Pod-lifetime immutable；public `Create`固定接收`topic`，不存在
  `SetTopic`或“待SetTopic”的半合法对象；
- 9→10等ordinal排序边界、allocator overflow、失败Append不消耗ID也不留下半条Memo；
- provisional Memo被Remove后再次Append获得新ID；同epoch successful Freeze持久化high-water gap；
- missing/already-removed Remove原子失败；caller-supplied insert/upsert与Replace/Update public surface均不存在；
- committed ID的ExactText不能改变；返回值和enumeration不能修改内部状态；
- dependency test锁定product project reference allowlist仅为`Completion.Abstractions`与`Atelia.Diagnostics`；
  禁止concrete `Completion`、SessionJournal core、RecapGrid、Galatea与具体provider。

**Done when**

- working aggregate与项目骨架可供后续两个internal foundation包并行消费，但对外没有语义残缺的Freeze。

### WP-02：canonical codec and durable publisher

**Intent**

- 独立完成aggregate document codec与可判定/不可判定settlement的durable publisher；暂不挂到公开MemoPod lifecycle。

**In scope**

- storage schema V2、strict reader/writer、canonical golden；
- caller-supplied root、strict path-safe PodId mapping与document identity验证；
- same-directory temporary、flush/close、no-clobber first create与atomic replacement；
- 明确的`Published`、`NotPublished`、`CommitIndeterminate` settlement结果；
- bounded corruption fixtures与subprocess crash harness。

**Out of scope**

- public `Create`/`Open`/`FreezeAsync`、prompt生成、SQLite、writer concurrency、global pod registry、
  backup/restore CLI。

**Tentative write scope**

- `prototypes/MemoPod/Store/`
- `tests/MemoPod.Tests/Store/`
- `tests/MemoPod.CrashHarness/MemoPod.CrashHarness.csproj`及其source，
  并登记到solution。

**Validation**

- same state产生exact canonical bytes；unknown/duplicate/trailing/oversize拒绝；reader是否接受property reorder在
  WP-00锁定，writer canonical order始终固定，不能把object order当成无意形成的合同；
- first create绝不覆盖既有authority；open验证requested PodId、mapped path与document PodId一致；
- settlement前fault返回`NotPublished`，reopen仍见旧文档；settlement不确定返回`CommitIndeterminate`；
- subprocess crash harness至少证明strict reopen只接受完整旧文档或完整新文档，不接受torn/mixed document；
- first-create crash/indeterminate覆盖“absent或完整new”，replace覆盖“完整old或完整new”；
- path、symlink/reparse、temporary file残留与cleanup合同按平台能力有明确tests。
- 最大合法durable V2 document可以成功decode；跨storage/render/Open的bound关系由WP-04集成测试负责。

**Done when**

- publisher能把“肯定没发布、肯定已发布、无法确定”交给lifecycle精确处理，且不靠日志猜测commit point。

### WP-03：pure frozen prompt renderer and prefix contract

**Intent**

- 从immutable document value纯函数式生成deterministic、cache-aware、provider-neutral context projection。

**In scope**

- renderer schema V2、exact UTF-8 bytes/hash；
- Memo ordering/escaping/delimiters；
- `ObservationMessage` projection；
- prompt byte bound与可替换token estimator seam。

**Out of scope**

- lifecycle/cache field、public detached prompt、provider调用、cache命中承诺、不同renderer格式的兼容读取。

**Tentative write scope**

- `prototypes/MemoPod/Prompt/`
- `tests/MemoPod.Tests/Prompt/`

**Validation**

- exact golden、Unicode/newline/delimiter adversarial corpus；
- equivalent state exact bytes/hash；
- append-only新增不改变旧 corpus之前的prefix；
- V2 physical Remove及`Remove + Append`纠错的预期longest-common-prefix破坏范围被golden明确记录；
- 易变metadata和query不出现在shared prefix。

**Done when**

- renderer determinism与prefix-stability是独立、可执行的contract，而不是“序列化看起来稳定”。

WP-02与WP-03在WP-01之后可以并行；两者均只接受immutable value，不能私自创建第二套Memo模型。

### WP-04：complete public MemoPod lifecycle

**Intent**

- 把WP-01至WP-03组装成第一个语义完整的`Editable ↔ Frozen` production surface。

**In scope**

- public `Create`/`Open`、全部read/Append/Remove、`FreezeAsync`与`ResumeEditing`；
- Dirty table、provisional→committed ID边界、Frozen全部mutation拒绝；
- Freeze的cancellable preparation、cancellation fence、durable settlement与无失败内存publish；
- Frozen内部prompt/hash缓存与ResumeEditing失效；
- `CommitIndeterminate`时invalidate handle并强制discard/reopen。

**Out of scope**

- provider recall、detached render/resolve API、concurrency、snapshot、多handle coordination。

**Tentative write scope**

- `prototypes/MemoPod/MemoPod.cs`
- `prototypes/MemoPod/Lifecycle/`
- `tests/MemoPod.Tests/Lifecycle/`

**Validation**

- `Create`首次Freeze no-clobber；`Open`从strict committed document进入Frozen；
- 最大合法durable V2 document可成功Open/render；renderer hard bound覆盖storage语言允许状态的最坏canonical
  projection，route token cap仍只在Recall preflight生效；
- Dirty false的Editable refreeze不重写authority，但会重建prompt并进入Frozen；
- mutation×Frozen和Freeze/Resume非法转换完整negative matrix；
- preparation cancel/fault保持Editable和原Dirty；successful settlement得到Frozen/Dirty false；
- publisher已报告`Published`后caller token立刻取消，Freeze仍成功且进入Frozen；post-publish cleanup失败只记
  diagnostics，不得反转成功settlement；
- committed M5(text A)在同一Editable阶段Remove M5并Append(text B)得到新ID后，successful Freeze/reopen只见
  新ID/text B，M5不可见且high-water继续前进；
- correction fault/cancel/crash只允许“old document含oldId、不含newId”或“new document不含oldId、含newId”，
  不能接受mixed aggregate；
- indeterminate settlement后的所有public Append/Remove/read/lifecycle入口均拒绝，discard+reopen观察完整old-or-new；
- 未Freeze进程退出后provisional ID允许消失；只对successful Freeze后的committed ID验证不复用。

**Done when**

- public Freeze从首次出现起就同时满足“valid document + durable settlement + cached prompt + Frozen”，不存在
  后续工作包才能补齐的弱化版本。

### WP-05：provider-neutral recall service

**Intent**

- 使用 `ICompletionClient`、required named tool和fake provider闭合 ID-only recall。

**In scope**

- stable system prompt、tool schema和`CompletionPromptPrefix`；
- query tail；仅接收`ICompletionClient`、`modelId`、`maxTokens`与带
  `PromptCacheReuseHint.ReuseExpectedSoon`的`CompletionInvocationOptions`；
- exact terminal parser、closed outcomes、ID validation与same-Frozen-Pod hydration；
- 只公开`RecallAsync`闭合调用，renderer output与独立ID resolver保持internal/test-only；
- prompt injection adversarial fixtures；
- bounded logging/telemetry，不记录完整敏感 Memo正文。

**Out of scope**

- reasoning/non-thinking connection配置、DeepSeek真实调用、Galatea主请求集成、automatic retry。

**Tentative write scope**

- `prototypes/MemoPod/Recall/`
- `tests/MemoPod.Tests/Recall/`

**Validation**

- one/many/none；ordered IDs；unknown/duplicate/oversize ID拒绝；
- wrong tool、multiple tool calls、free-text-only、`tool + Text`、`tool + Reasoning`、incomplete/error/cancel；
- recall期间Pod保持Frozen，返回结果自包含；
- provider failure、invalid output与caller cancellation后prompt/hash不变，下一次Recall复用相同prefix；
- fake provider证明storage bytes和provider request不是同一contract。

**Done when**

- 不联网即可证明完整请求/解析/hydrate主链，provider不能借召回协议写任何 authority。

### WP-06：minimal fake-first operator vertical

**Intent**

- 给开发者一个不接主Agent的最小完整入口，先用fake provider验证create/edit/freeze/recall/reopen工作流。

**In scope**

- 独立的narrow DebugApp/composition root选择disposable root、connection和limits；
- fake路径不需要reasoning配置；real connection的non-thinking/model route选择只由该composition root拥有，
  不扩张MemoPod或`Completion.Abstractions`；
- 显式命令执行完整write phase和read phase；
- inspect默认只显示content-free metadata，正文只在明确命令下读取；
- deterministic fake provider为默认路径，real connection仅作为可选配置点。

**Out of scope**

- 多Pod router、后台服务、Galatea online turn、自动提取、必须联网的验收。

**Tentative write scope**

- `prototypes/MemoPod.DebugApp/MemoPod.DebugApp.csproj`
- `prototypes/MemoPod.DebugApp/Program.cs`与`README.md`
- solution registration与`tests/MemoPod.Tests/Operator/`。

**Validation**

- clean temp root E2E；reopen；cancel/failure；无provider secret时inspect/edit/freeze/fake recall仍工作；
- fake-provider request capture证明同一Frozen epoch的prefix bytes不变；
- secret/endpoint不写入Pod document、tracked fixture或默认日志。

**Done when**

- 一个冷启动Coding Agent可以只按README在disposable root跑通单Pod lifecycle，且不被live provider gate阻塞。

### Track C1：DeepSeek usage telemetry correctness

**Intent**

- 在MemoPod live canary之前，先用provider-free tests关闭DeepSeek stream usage/cache字段的映射缺口。

2026-08-19的current-code reconnaissance显示：`OpenAIChatDialects.DeepSeekV4`尚未请求stream usage，usage
parser只覆盖OpenAI形状的cached-token details。因此这不是“若有必要”的canary顺手修补，而是独立Completion
correctness工作包；实施时仍须重新核对当时owning code与
[DeepSeek Chat Completion协议](https://api-docs.deepseek.com/api/create-chat-completion)中的`stream_options.include_usage`
及top-level cache hit/miss字段。

**In scope**

- DeepSeek dialect的stream usage请求；
- provider权威cache hit/miss字段解析与以下固定`CompletionUsage`映射：
  `prompt_cache_hit_tokens → CacheReadInputTokens`、
  `prompt_cache_miss_tokens → UncachedInputTokens`、
  `CacheCreationInputTokens = null`；miss不得伪装成cache write；
- 三个prompt字段同时存在时严格验证`prompt_tokens == hit + miss`；不完整字段按协议规则拒绝或标为unavailable，
  不做差值猜测；
- DeepSeek未报告cache write，因此当前归一化`PromptCacheObservationStatus=Partial`；若要改变`Partial/Complete`
  的provider-neutral含义，必须另立Completion contract review，不能在adapter内偷偷升级；
- absent/partial/malformed/final-only/chunked usage fixtures；
- 不把推导值冒充provider-reported值。

**Out of scope**

- MemoPod语义、真实网络调用、价格声明、Galatea集成。

**Write scope**

- `prototypes/Completion/OpenAI/`
- `tests/Completion.Tests/OpenAI/`。

**Validation**

- 全部provider-free parser/request goldens；OpenAI与其他dialect不回归；
- usage unavailable时明确保持unknown/unavailable，而不是填零伪装权威观测。

**Done when**

- live runner能区分provider-reported hit/miss与不可观测状态，并有focused tests证明映射。

Track C1可在WP-01至WP-06旁路推进；它只阻塞Track C2和真实provider经济性claim，不阻塞fake operator或
Galatea integration design。

### Track C2：MemoPod live runner and authenticated canary

**Intent**

- 在disposable fixture上验证目标DeepSeek V4 Flash route的召回质量、prefix reuse、实际cache telemetry、费用
  与延迟假设。

**Prerequisites**

- WP-06 fake-first DebugApp关闭；Track C1 telemetry correctness关闭；持有显式authenticated canary权限。

**In scope**

- 在WP-06 DebugApp中加入显式`--live` acceptance mode；默认路径仍是fake，不持有凭据时不触网；
- cold/warm/repeated query、non-thinking、required tool、empty result、恶意正文与large corpus fixture；
- route compatibility gate：确认当前DeepSeek endpoint接受`thinking.type=disabled`、required named tool以及
  converter在`allowParallelToolCalls=false`时发出的`parallel_tool_calls:false`；官方schema未列出的字段不能
  仅因OpenAI-compatible而假定受支持；
- content-free candidate evidence。

**Out of scope**

- 生产激活、主Agent自动注入、以一次canary冻结未来价格/模型保证。

**Write scope**

- `prototypes/MemoPod.DebugApp/`
- `tests/MemoPod.Tests/Live/`中的provider-free live-mode/config tests；
- `docs/SessionJournal/evidence/memo-pod-deepseek-v4-flash-candidate.md`。

**Validation**

- 记录exact model/route、renderer identity/bytes/tokens、provider-reported hit/miss tokens或明确unavailable、
  output tokens、latency、selected IDs与人工precision/recall判断；
- 同一Frozen prefix重复query与新Freeze后的cache变化均有数据；
- canary failure不修改durable Pod、不写secret/正文到tracked evidence、不冒充implementation readiness。

**Done when**

- 当前route有可复现质量/经济性证据；telemetry不权威或质量不达标时明确No-Go或缩小claim。

### WP-07：Design Reopened — no active implementation package

**Terminal disposition of the old attempt**

- 旧WP-07A plan和Prepared v6 Candidate只作historical input；
- historical B1 `83477c06`及其reviews曾PASS，但已由single atomic rollback `1d8c33bb`撤回；
- 旧Gate A随用户撤回终止且不可续用；Gate B canceled/never granted，promotion never started；
- 旧B2 canceled/never authorized，Galatea MemoPod adapter/config/vertical从未实现；
- WP-00–WP-06、Track C1、Track C2与MemoPod core不受影响。

**Current design gates**

active owner是[Design Reopened integration plan](memo-pod-galatea-integration-plan.md) §0。必须先分别关闭：query timing、
Pod动态create/classify/split/merge、Pod Indexer、empty-query prompt-cache renewal、main-thread injection、duplicate injection与
cross-turn dangling/reference continuity。

**Hard stop**

六项设计闸全部完成独立review并获得fresh user design authorization前，不得继续设计或实现SessionJournal integration
interface、Prepared recipe/body version、recovery wire、Galatea adapter/config或main-thread injection。未来若继续，必须新建
fresh Candidate、fresh code/test scope与fresh user gates；旧WP-07A/B text、Gate A和historical PASS都不能直接复用。

**Done when**

- 只表示六项设计问题已有可独立审阅的决策与authority/cost/privacy/recovery边界；不表示任何upper-consumer
  implementation获准开始。

## 10. Acceptance matrix

| Gate | Required evidence |
|---|---|
| Boundary | 文档和依赖测试证明MemoPod不进入RecapGrid publish path，SessionJournal core不反向依赖它 |
| Phase safety | mutation×Frozen、`RecallAsync`×Editable、非法转换及invalidated handle的完整negative matrix |
| Entry surface | production只有Append/Remove/read；无Replace/Update/upsert/SetTopic；纠错必须获得新ID |
| ID lifecycle | pre-Freeze ID明确provisional；successful Freeze后committed；同ID ExactText/metadata immutable；Remove/reopen后不复用或revive；overflow原子失败 |
| Alias safety | caller无法通过返回collection或Memo实例绕过phase gate修改Pod |
| Persistence | canonical V2 goldens、strict rejects、first-create no-clobber、old-or-new crash result、reopen Frozen |
| Settlement | pre-publish failure保持Editable/Dirty；Published后取消或cleanup失败仍进入Frozen；indeterminate使handle fail-closed并要求reopen |
| Determinism | same logical state → exact prompt bytes/hash |
| Prefix stability | Append保持旧corpus prefix；V2 physical Remove与Remove+Append纠错的破坏范围有golden proof |
| Recall protocol | exactly-one required tool、strict IDs、empty≠failure、same-Frozen-Pod hydrate；production无detached prompt/resolve |
| Safety | untrusted Memo无写权限/其他工具；logs/reports默认不泄漏正文 |
| Cache evidence | Track C2：real DeepSeek cold/warm telemetry；无权威字段则记录unavailable且不宣称命中 |
| Upper-consumer integration | 当前无product upper consumer；WP-07 Design Reopened六项闸关闭并获得fresh授权前，无SessionJournal/Galatea interface、wire、recovery或injection acceptance |
| Documentation | current/target/evidence分层、README routing、docs checker与diff check通过 |

## 11. Review checklist

每个实现包关闭前，reviewer至少回答：

1. 是否不小心重新引入 `MemoPodSnapshot`、revision/CAS或隐式并发承诺？
2. 是否存在绕过 Frozen gate 的 mutation alias、backend handle或serializer callback？
3. storage document、prompt render和provider wire是否仍是三个独立合同？
4. Append/Remove是否具有明确的prefix-cache影响；是否意外重新引入原位Update或把易变字段放进前缀开头？
5. no-match、invalid output、provider failure和cancellation是否仍可区分？
6. recall结果是否在ResumeEditing前完成hydrate并自包含？
7. 是否把MemoPod误写成raw-derived sidecar，或伪造EventAddress/`AbsorbedThrough`？
8. 是否把RecapGrid扩张成事务明细库，或让MemoPod承担身份/信念连续性？
9. 是否在没有真实telemetry时宣称prompt cache费用/命中收益？
10. 本包触达的新public/wire/persistence surface是否有focused negative tests和goldens？

## 12. Deferred decisions

以下决策有意延后到对应工作包plan lock，不应在WP-00顺手扩张：

- exact `PodId`/`MemoId` token、数值上限和prompt grammar；
- caller-supplied root在Galatea中的最终scope和privacy/backup policy；
- filesystem replacement的跨平台durability等级；
- large Pod的token estimator与产品默认上限；
- invalid model output是否允许一次retry；
- V2 chronological tombstone stream、generation/compaction wire、migration与cost/quality触发阈值；
- 多Pod routing、自动提取、聚类、refinement和跨session共享；
- Memo来源provenance是否需要独立字段或外部index。

这些延后项不会阻塞单Pod Frozen state、aggregate document、deterministic renderer和fake-provider recall主链。
