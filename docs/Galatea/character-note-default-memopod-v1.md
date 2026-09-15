# Character Note Default MemoPod V1

> **当前格式边界（2026-09-16）**：本文保留 Default Pod V1、后续 SQLite V3 回执方案及其阶段记录。当前 CharacterMemory SQLite 为 V4；新版回执保存机读事实，旧 `notice_body`／冻结 Observation 按原格式读取，不能作为新写入模板。capture、Planned→Applied、old-or-new reconciliation 和回执投递证明仍适用。后继边界见[结构化输入合同](structured-input-rendering-design.md)、[当前运行时](runtime.md)；真实升级与通信结果见[迁移验收](player-character-migration-validation.md)。

## 状态

- 方案日期：2026-08-30
- V1 阶段状态：Implemented and reviewed；D0-R0完成
- 前置版本：[`Character Note Request Receipt V0`](character-note-request-receipt-v0.md)
- 本轮目标：把已识别的Character Note保存请求幂等地写入每个角色唯一的Default MemoPod，并只在durable apply已经证明成功后生成诚实回执
- 本轮不包含：静态分类、PodCatalog、动态聚类、多Pod routing、Memo内容整理、主线程recall注入

本文保留Default Pod V1的原始apply设计与已完成工作包记录。后续DerivedInfo V2、Memo recall与
[Automatic memory闭环](automatic-memory-work-order.md)阶段将CharacterMemory SQLite推进到V3，三种typed trigger共享
召回和durable receipt投递。[Note 忠实代写](character-note-transcription.md)进一步放宽capture前的文字提取：不要求叙事原文逐字符匹配。下面保留这些阶段更新后的提取与receipt条款；§10–12的V1阶段non-goals/验收历史不再表示当前缺口。

## 一句话决策

首版不等待分类系统：每个启用Character Note能力的角色拥有一个稳定、始终存在的Default MemoPod；所有Note先进入该Pod。

真正的首要难题不是聚类算法，而是：

```text
durable extraction capture
  -> durable apply plan
  -> MemoPod Freeze external effect
  -> exact old-or-new reconciliation
  -> durable apply settlement
```

只有这条链路收口后，runtime才可以声称Note已经保存。

## 1. 用户可见产品形状

角色继续通过叙事Action表达当前保存请求与完整内容，不需要专门的提交句式。Extractor忠实整理文字，可以归整多段叙述，但不能增删事实、改变否定、条件或不确定性。主线角色模型不直接调用Memo工具，也不提供`PodId`、分类、标签或`MemoId`。

```text
terminal Action
  -> CharacterNoteExtractor
  -> durable capture / zero-result tombstone
  -> Default MemoPod apply
  -> durable apply settlement
  -> atomic SQLite V3 Pending receipt
  -> future typed Observation + exact raw append proof
  -> Delivered
```

成功回执只证明本次采纳的Note内容已经进入Default MemoPod的committed Frozen文档。持久层`ExactText`表示采纳后精确保留的正文，不表示它是叙事Action的逐字子串。它不承诺：

- 已分类、生成Title/Gist/Summary或建立embedding；
- 本条Memo一定会被主线程召回或已经被角色理解；
- Undo、rewind或SessionJournal回退会自动删除Memo。

角色收到`Note 保存回执`才有runtime提供的成功通知。没有回执不应被解释为保存成功或失败；operator仍可通过durable store诊断。
Pending回执会跨restart保留；Delivered只证明Observation已durable append，不承诺provider接收或角色认知。

## 2. Default Pod语义

每个启用Character Note的user有且仅有一个Default Pod：

- `PodId`是V1 code-owned固定canonical ID `00000000000000000000000000000001`；每个user拥有独立root，因此无需随机分配、持久化或枚举发现；
- Topic是code-owned固定文本：`该角色主动提交、尚未分类的长期笔记。`；
- 首次provision即创建并Freeze空Pod，因此“始终存在”表示stable logical identity与committed document，不表示长期Editable；
- 一个Action中的`1..N`条Note在同一个Editable epoch中按叙事顺序Append，并只Freeze一次；
- V1没有Remove、Update、reclass、split或merge；Default Pod只追加；
- 不能由filesystem enumeration猜测Default Pod；binary中的V1 exact ID与code-owned Topic共同定义唯一合法Default Pod。

V1阶段所有Note都进入Default Pod。未来出现分类Pod后，Default可以演化为unclassified fallback；是否移动历史Memo必须另立durable topology工作包，V1不预埋物理复制或跨Podtransaction。

## 3. Authority model

| Owner / carrier | Authority semantics | 明确不是 |
|:--|:--|:--|
| SessionJournal terminal Action | 角色本轮叙事Action与`SourceAction`的durable authority | Memo corpus或apply receipt |
| Character Note extractor output | 对exact terminal Action的一次versioned语义提取结果 | `PodId`、`MemoId`或保存证明 |
| Character-memory SQLite store | extraction capture、zero-result tombstone、operation identity、apply plan与settlement authority | 当前active Memo正文集合的查询authority |
| Default MemoPod committed document | 当前active Memo exact text与Pod-local ID的authority | extraction provenance、SessionJournal history或分类authority |
| SQLite V3 receipt outbox | 冻结notice、exact Observation绑定与持久化投递状态 | provider接收证明、角色认知或Memo corpus |
| Debug log / SessionJournal receipt text | development evidence / narrative history副本 | replay、migration或Memo apply输入 |

SQLite为了crash recovery可以永久保存captured requested ExactText；这份副本的authority问题是“首次采纳了哪些保存正文”，不是“当前Pod有哪些active Memo”。当前`CharacterNoteIntent`只含`Text`（JSON `text`）；不要求`EvidenceQuote`，也不执行正文的ordinal substring检查。语义忠实由extractor判断，runtime验证非空、Unicode与数量/字节上限。持久格式保持不变。V1不提供删除或修订，因此capture与active corpus不会产生lifecycle分叉；未来一旦加入Remove/correction，必须重新审视retention与current-state projection，不能自动把capture表升级成第二份Memo corpus。

## 4. Stable identity

### 4.1 Action capture identity

一次提取capture由以下内容冻结：

```text
SourceAction
VisibleActionSha256
VisibleActionUtf8Bytes
ExtractorContractId
ordered ExactText[]
```

`SourceAction`是capture primary key：

- capture absent时，current extractor的hash、byte count、contract与ordered ExactText生成新commitment；
- capture already exists时，直接读取stored contract/commitment/rows，不重跑current extractor，也不要求current ContractId相同；
- 只有同一个`CaptureNew`竞争请求提交不同commitment时才返回typed conflict且零mutation；
- `0` intent也写durable tombstone，防止admission重复调用provider。

### 4.2 Applied Memo identity

V1不提前引入placement-independent `CharacterMemoId`。一条captured item的稳定业务identity是：

```text
(SourceAction, ArtifactOrdinal)
```

成功apply result是：

```text
(DefaultPodId, Pod-local MemoId)
```

这两个identity已经分别回答“哪一次请求中的哪一条”和“当前保存在哪里”。跨Pod move后仍保持旧引用、redirect或global identity只在真实reclass/split/merge需求出现时设计；V1不为future topology建立随机ID allocator或mutable placement mapping。

### 4.3 Extraction commitment

每个capture保存一个versioned、length-prefixed `ExtractionCommitment`，覆盖：

```text
SourceAction
VisibleActionSha256
VisibleActionUtf8Bytes
ExtractorContractId
ordered ExactText[]
```

既有capture使用首次durable保存的ContractId、commitment与ExactText作为authority；未来current extractor升级不会重跑或推翻历史capture。只有同一次`CaptureNew`竞争提交了不同commitment时才是typed conflict，而且不得把健康store写成Quarantined。

### 4.4 Batch identity

一个Action中的ordered intents是一个apply batch：

- `0` intent：capture settled，无Memo effect；
- `1..N` intent：全批进入同一个Default Pod Freeze candidate；
- V1只有一个目标Pod，因此batch可以做到外部只观察完整base或完整target；
- 任何intent不能单独报告Applied，直到整个target document已证明committed。

## 5. Durable store logical schema

最初V1使用独立的per-user SQLite store，不复用delegation SQLite schema。以下为原始核心表；当前V3保留其authority，
另有DerivedInfo work与下面的receipt表：

```text
character_memory_meta
  schema_version = 1
  user_id
  session_repository_id
  capture_frontier
  baseline_selected_head?
  store_state: Provisioning | Ready | Quarantined
  provision_target_pod_state_identity
  settled_default_pod_state_identity?
  active_source_action?
  quarantine_code?
  quarantine_observed_pod_state_identity?
  store_revision

note_action_capture
  source_action_address PK
  visible_action_sha256
  visible_action_utf8_bytes
  extractor_contract_id
  extraction_commitment
  artifact_count
  state: ZeroCaptured | Captured | Planned | Applied | Rejected
  base_pod_state_identity?
  target_pod_state_identity?
  rejection_code?
  state_revision

character_note
  source_action_address FK
  artifact_ordinal
  exact_text
  memo_id?
  PK(source_action_address, artifact_ordinal)
```

初始上限沿用extractor apply合同：每个Action最多16条、每条ExactText 64 KiB、总ExactText 256 KiB。Store reopen严格验证schema/index、meta、`0..1` active batch与全局SQL count invariant；历史capture的bounds、strict UTF-8、canonical IDs与commitment只在按`SourceAction`执行bounded exact read时验证，不在Open或每次status read中全量materialize。

`settled_default_pod_state_identity`是store对当前committed Default Pod tip的commitment fence。它不承载正文查询，但每个新plan都必须证明observed base与该值exact相等，禁止静默收养旁路MemoPod修改。Applied settlement与meta tip推进必须在同一个SQLite transaction中完成；历史Applied row不要求自己的旧target永远等于当前Pod。

store同一时刻最多有一个`Captured`或`Planned` batch；`active_source_action`和数据库约束共同锁定该事实。`ZeroCaptured`、`Applied`与`Rejected`是terminal，不占active slot。Quarantine是store-global health，不是某条capture的普通lifecycle state。

每个store目录有process-lifetime exclusive lock。生产只允许一个writable owner；`UserSessionHost.TurnLock`继续序列化同一session的reconcile，但不能替代跨进程lock。

### 5.1 当前V3 receipt outbox

`note_receipt_delivery`以`source_action_address`为主键/FK，冻结`notice_body`、`created_revision`、`state_revision`，
并保存`expected_session_head`、`rendered_observation`与`observation_address`。状态为
`Pending | ObservationBound | Delivered`；全store最多一条ObservationBound，Pending按created revision/Source Action排序。
只有ObservationBound保留完整`rendered_observation`供raw proof；Delivered后清除它，继续保留notice payload、
source/base与Observation address，不永久复制外部回信或recall正文。
每次新的非空`Planned -> Applied`在同一事务建立一条Pending receipt，不依赖caller之后是否收到`AppliedNow`。

旧store先strict验证，再事务化迁移到V3；V1经过V2 DerivedInfo迁移。旧版已Applied历史不会补建receipt，
旧Captured/Planned在V3下首次真正Applied时创建。AlreadyApplied不创建第二份义务。升级不从旧receipt正文/debug log猜测投递状态。

## 6. Apply protocol

### 6.1 Default Pod provisioning

首次provision本身服从plan-before-effect。code-owned empty candidate identity在SQLite create transaction前计算一次并作为`provision_target_pod_state_identity`持久冻结；restart不得用可能已经升级的current code重新推导并替换它：

```text
SQLite create commits Provisioning + exact empty target identity
  -> code-owned DefaultPodId + empty candidate identity已冻结
  -> Create exact empty Pod + Freeze
  -> proven Published，或fresh Open target后再次确认directory durability
  -> SQLite transaction写Ready + settled identity
```

恢复规则：

- `Provisioning + document absent`：用同一code-owned PodId重新Create empty candidate；
- `Provisioning + exact empty target`：确认当前document directory durability后补写Ready；
- `Provisioning + other document`：store-global Quarantined；
- `Ready`：每次apply前observed identity必须exact等于meta settled tip；
- `Quarantined`：禁止capture/apply，不自动adopt、delete或overwrite。

Ready前不允许创建Action capture。Default Pod ID固定消除了“随机ID先写哪一边”的额外状态，但不能消除SQLite与empty document之间的publish window，因此Provisioning仍是必要状态。

### 6.2 New capture

```text
read exact target
  -> run extractor when capture absent
  -> final SessionJournal head fence
  -> SQLite transaction captures ordered batch / tombstone
```

capture一旦committed，后续apply不再调用extractor。Provider failure、invalid output或caller cancellation发生在capture前时无SQLite mutation；post-completion可以保持best-effort，但下一次admission必须在接受任何新turn、rewind或其他durable mutation前完成该exact latest Action的capture/apply，不能把provider failure再次吞掉后越过它。

### 6.3 Plan before effect

对非空Captured batch：

1. strict Open Default Pod，要求Frozen；
2. 读取base committed Pod state identity，并要求exact等于meta settled tip；
3. `ResumeEditing`；
4. 按ordinal Append全部ExactText，取得planned local Memo IDs；
5. 计算target candidate state identity；
6. SQLite transaction冻结base identity、target identity与每条planned MemoId，将batch转为Planned；
7. 只有plan已durable后才允许`FreezeAsync`；
8. Freeze proven Published后，SQLite transaction原子把batch转为Applied并把meta settled tip从base推进到target。

如果plan transaction失败，不能Freeze当前Editable handle；丢弃handle并重新Open authoritative base。

capacity/count failure发生在plan前时把capture转为terminal `Rejected`，不Freeze且不无限重试。`Rejected`不等于Quarantined：前者是该请求无法进入已满Default Pod的确定性结果，后者是整个store/Pod authority已经分叉。

### 6.4 MemoPod reconciliation seams

现有MemoPod public API不暴露canonical document identity，而V1恢复需要区分exact base/target。增加一个刻意小的read-only seam：

```text
MemoPod.ComputeStateIdentity()
  -> schema-qualified SHA-256 of the canonical complete document candidate

MemoPod.ConfirmCurrentDocumentDurability()
  -> Frozen handle only; fsync the exact current Pods directory
```

identity可以在Editable或Frozen阶段计算，只返回opaque token，不返回document、prompt、snapshot、revision或detached resolver。它直接hash canonical complete document bytes，必须覆盖`PodId`、Topic、`nextMemoId`、ordered active Memo IDs、metadata与ExactText，因此allocator high-water变化也会改变identity。

durability confirmation只用于`CommitIndeterminate`或crash后fresh Open已经观察到exact target的情况。Rename后立即可见target并不证明此前directory fsync成功；只有再次fsync成功，才可以补写Applied/Ready。正常`FreezeAsync` proven Published不需要重复确认。

这两个seam都不是MVCC、revision或public snapshot；它们只用于single-owner external-effect reconciliation。

### 6.5 Recovery matrix

| Durable state | Observed Pod identity | Action |
|:--|:--|:--|
| Captured | exact meta settled tip | 重新构造plan；尚无Pod effect |
| Captured | other | store-global Quarantined；禁止收养旁路变化 |
| Planned | exact base | 重放Append；每个返回MemoId必须等于plan；candidate identity必须等于target；Freeze |
| Planned | exact target | fresh Open确认directory durability；补写Applied并推进meta tip |
| Planned | neither | Quarantined；禁止猜测、覆盖或再次Append |
| Historical Applied / Zero / Rejected | exact meta settled tip | terminal result；不再次Append |
| Any healthy state | other than meta settled tip且无active plan | store-global Quarantined |
| Quarantined | any | fail closed；只允许显式operator诊断/未来maintenance |

`FreezeAsync`返回commit-indeterminate时立即discard handle并strict reopen；只按上表接受base或target。看到target后必须再次确认directory durability，不能仅凭可见性声称Published。V1不做physical rollback。

Reconciler使用closed typed outcome，不把no-effect、availability与authority mismatch混在一起：

```text
BaselineCovered
ZeroCaptured
AppliedNow(ordered (SourceAction, ordinal, PodId, MemoId, ExactText))
AlreadyApplied
Rejected(code)
DeferredAfterCapture(code)
Quarantined(code)
SelectedHeadChanged
```

只有`AppliedNow`携带从durable capture/apply重新读取的frozen batch；当前V3的receipt已在Applied事务中建立，
不依赖这个返回值创建。`DeferredAfterCapture`表示capture/plan仍可由admission继续；`Rejected`是terminal single-request outcome；`Quarantined`是store-global fail-closed health。

## 7. Runtime sequencing

### 7.1 Post-completion

Mail与Note继续消费同一份`GalateaTerminalActionExtractionTarget`。Note task升级为：

```text
capture if absent
  -> reconcile Default Pod apply
  -> return BaselineCovered | ZeroCaptured | AppliedNow | AlreadyApplied
          | Rejected | DeferredAfterCapture | Quarantined
          | SelectedHeadChanged
```

两个任务仍可并行启动并都必须drain。它们是同一terminal Action上的独立durable effects；Mail failure不回滚已经settled的Note，Note failure也不撤销Mail capture。

V1保留有限的best-effort policy：post-completion的pre-capture provider/unavailable不把已经完成的main Action改报失败；但它会留下一个latest Action gap。下一次admission不得在同样失败后继续接受新turn。只要capture已经durable，apply/recovery invariant failure必须fail closed并留下可诊断状态，不能伪装成zero/no-match。

`Rejected`是terminal且无save receipt；`DeferredAfterCapture`保留active batch，post-completion可以结束但下一次admission必须阻断并继续settle；`Quarantined`在post-completion与admission都fail closed。`SelectedHeadChanged`阻止新的capture并要求caller重新admit exact head，但不能撤销已经captured batch后续settle的保存事实或receipt义务。

每次新的Applied settlement原子创建`Note 保存回执`，因此crash-after-apply-before-runtime-return不会丢通知。
source Action已不在selected lineage也仍有资格；这是保存事实的通知，不是给源Action重新授予capture权限。

Mail与Note是独立durable effects。Mail失败、caller cancellation或后来head change都不撤销已提交的Memo/outbox，
也不能伪造尚未Applied的receipt；原有异常优先级与两个任务均drain的边界保持不变。

### 7.2 Admission/restart

现有`ReconcileDurableAdmissionAsync`在Mail gap reconciliation旁增加Character Note reconciliation，固定顺序为：

- 先从store读取全局`0..1` active Captured/Planned batch并恢复；这一步不依赖current SessionJournal head；
- 任一global Quarantined状态阻止所有后续capture与admission；
- active batch清空后才处理latest exact terminal Action，不扫描完整history；
- store baseline physical frontier覆盖启用前的历史；
- absent capture时可以重跑extractor；
- captured/planned batch不重跑extractor，只恢复apply；
- admission恢复pending batch若首次settle Applied，同一事务照样创建receipt；AlreadyApplied不重复创建；
- admission的pre-capture provider/timeout/invalid failure阻止新turn，确保旧Action不会被后继latest head越过。

所有normal HTTP durable mutation入口继续先走同一admission gate；因此pending batch会在Undo/rewind之前settle，而已经Applied的Memo不会因SessionJournal rewind自动删除。

Receipt投递在fresh PlayerAction、HeartbeatActivation与DelegateReply共享；inbound/recovery不领取新receipt。
runtime先选Pending receipt，再做optional Memo recall，冻结完整Observation后绑定exact base head。
`GalateaNoteReceiptDelivery`读取raw journal exact proof：NotAppended回到Pending，InProgress/Terminal标记Delivered；
其他证据fail closed。pre-dispatch stop/failure与restart不会丢Pending。abandon/rewind前必须先结算Bound，
Delivered后不因rewind重发；此承诺止于durable append，不是provider已读或Completion成功。

### 7.3 Cancellation/failure

- pre-capture token可以组合caller、30秒deadline与Mail abort；只在capture transaction前使用；
- provider failure/invalid output发生在capture前：无capture，未来可重试；
- capture transaction前执行最后一次caller cancellation check；
- SQLite capture成功后，deadline/Mail abort失效，不得把post-capture状态归类成普通timeout/provider failure；
- plan transaction前仍可观察caller cancellation；Planned commit后进入non-cancelable local settlement，完成Published/indeterminate old-new分类后再决定是否传播caller cancellation；
- Pod NotPublished：discard handle并strict reopen；只有exact base才保持Planned并允许未来重试，missing/unsafe/other identity一律fail closed；
- Pod commit-indeterminate：使用fresh handle reopen；target必须再次确认directory durability，base保持Planned，neither Quarantine；
- mismatch：Quarantined并fail closed；
- fatal exception不包装成availability failure。

## 8. Config and provisioning

V1实施时Galatea root config hard-cut到V6，每个user新增required（当前root config为V8，见项目README）：

```json
"characterMemoryStateDir": "character-memory/alice"
```

理由：Character Memory有独立privacy、backup、lock与lifecycle owner，不能藏进`sessionDir`、复用`delegationStateDir`或依赖CWD推导。

V6 exact path规则：

- nonblank；relative以config directory为base；runtime为canonical absolute path；
- 所有user之间exact unique、双向non-nested；
- 与所有`sessionDir`、`delegationStateDir`和optional `callLogDir`双向non-nested；
- existing path components拒绝symlink/reparse point；
- Note binding为`null`时仍完成path resolve、lexical topology与existing-ancestor reparse preflight，但不create/open/lock/store-validate该目录；
- Note binding非`null`且session writable attach时：missing则以current physical frontier创建baseline store与empty Default Pod，existing则strict open；
- maintenance mode完全不open/create/lock/apply Character Memory；V1没有需要read-only handle的status/recall consumer。

V6把session、delegation、character-memory与optional call-log路径关系收进一个total topology validator；production loader与直接构造`GalateaConfig`的测试/consumer都必须经过同一验证，不能继续让call-log disjointness只存在于loader私有分支。

当时Bootstrap写V6与该字段；当前写V8，仍不创建character-memory state。Ignored live config migration必须停服、备份并单独执行；tracked tests不能代替本机迁移。

## 9. Prompt and receipt hard cut

启用Character Note binding时，main prompt从development request appendix hard-cut为真实保存Quick Start：

- 教角色用自然语言表达当前保存请求与完整Note内容，不要求固定格式或提交仪式；
- 明确只有后续`Note 保存回执`证明保存成功；
- 不承诺分类、metadata enrichment或recall。

V1当时从development request升级时保留了tool字段；当前[忠实代写合同](character-note-transcription.md)保留tool name，字段已收敛为`text`，`ContractId`随语义与tool合同变化。历史capture仍用首次采纳内容，不因新合同重提取。

`PlayerTurnNotice.NoteRequestReceipt` hard-cut为`NoteSaveReceipt`：

- heading：`Note 保存回执`；
- info string：`character-note-save-receipt`；
- body明确`已成功保存到默认MemoPod`，以“已保存的 Note 内容”正常逐字列出采纳后的ExactText；病态fence-heavy正文则明确标注展示预算限制，只列Source Action与Memo IDs；
- 仍然每轮最多一条、必须是最后notice、legacy dialect拒绝；
- 投递由SQLite V3 outbox拥有；三个trigger都至多一条末尾receipt，notice总上限16。reply cutoff为pending receipt预留一个槽位与实际预算。

完整回执与compact fallback都必须能容纳任意合法player text和一条最大reply，保证保存通知不会因为后续Ready reply
持续到达而永久饥饿；optional recall只使用receipt之后的剩余Observation预算。fallback不截断保存正文冒充完整展示，
不承诺metadata已补全或记忆已召回。

不保留旧heading、旧info string或旧strong type compatibility reader；项目尚未发布，及时重构优于双协议。2026-08-30实施前只读审计两个configured本机SessionJournal，对旧`## Note 请求回执`heading的binary/text命中均为0，因此当前没有需要迁移或保留legacy reader的durable V0 Observation证据。

## 10. Explicit non-goals / complexity tripwires

以下是原始V1阶段non-goals，作为历史设计边界保留。DerivedInfo、Memo recall和durable receipt后续已分别实现，
不再是当前系统的non-goals；automatic-memory工作单是本次扩展的authority。

V1当时明确不实现：

- 静态分类、LLM Pod router、embedding、ANN或vector store；
- PodCatalog、aliases、secondary membership；
- 多Pod physical copy、跨Podtransaction、reclass/split/merge；
- Remove、Update、correction、expiry、privacy purge或secure erase；
- Title/Gist/Summary自动生成；
- Memo recall provider、main-request injection、Prepared recipe/wire变化；
- durable receipt delivery、automatic receipt turn、frontend Note API；
- background retry scheduler、多pending apply batch、MVCC、CAS或多process reader/writer；
- 从旧V0 receipt/debug log迁移Memo；
- 自动收养已有standalone MemoPod document。

出现下列信号时先停下来检查真实需求：

- 为保存一条Note开始设计跨Pod原子提交；
- 为未来分类把active corpus再完整复制进SQLite；
- 为一次post-turn apply引入后台worker/scheduler；
- 为可能的concurrent recall引入snapshot/MVCC；
- 为未来SessionJournal recall恢复旧Prepared v6 candidate；
- 为visible receipt不丢失而扩大成durable delivery protocol。

这些都不是Default Pod保存闭环的必要条件。

## 11. Work packages

### D0 — design lock

Intent：保存本文，完成独立设计review并收掉must-fix finding。

Status：Complete；首轮findings与tail复审均已收口，无P0/P1残留。

### A0 — MemoPod reconciliation seams

Intent：增加覆盖canonical complete document candidate的opaque identity与Frozen directory durability confirmation，只解决external-effect recovery。

Write scope：`prototypes/MemoPod`、`tests/MemoPod.Tests`、MemoPod README/target doc。

Done when：Editable/Frozen、`nextMemoId`变化、metadata/content变化、reopen identity与commit-indeterminate后durability confirmation都有golden/negative tests；无snapshot/version API扩张。

Status：Complete；opaque complete-document identity、Frozen directory durability confirmation与old-or-new recovery seam均已由MemoPod focused tests覆盖。

### A1 — Character-memory durable store

Intent：实现独立SQLite schema、lifetime lock、baseline、Provisioning/Ready/Quarantined health、capture/tombstone、current Pod tip、plan/settle/reject与bounded exact reads。

Write scope：`prototypes/Galatea/CharacterMemory`、focused Galatea tests。

Done when：duplicate commitment、zero、single-active slot、bounds、provision/current-tip invariants、store transaction crash hooks与strict reopen完整覆盖。

Status：Complete；per-user SQLite/lock/baseline、capture/tombstone、plan/settle/reject与strict reopen均有focused store tests覆盖。

### A2 — Default Pod reconciler

Intent：把captured batch通过plan-before-effect协议apply到Default MemoPod。

Write scope：CharacterMemory domain/reconciler、Galatea↔MemoPod project reference、focused tests。

Done when：zero、first provision、many-note batch、AlreadyApplied、capacity Rejected、crash windows、planned-ID mismatch、durability confirmation与single Freeze语义通过。

Status：Complete；code-owned empty identity由真实MemoPod candidate锁定，focused A2 tests 23/23通过。

### A3 — V6 config and lifecycle composition

Intent：增加explicit character-memory path、provision/open/dispose与admission reconciliation。

Write scope：config reader/DTO/bootstrap/current contract、session composition与config tests。

Done when：total path disjointness、null binding no store touch、enabled create/open、maintenance no-open/no-create、invalid existing fail closed通过。

Status：Complete；V6 path/config、per-session attach/dispose与disabled/maintenance零touch均有focused lifecycle覆盖。

### A4 — runtime, prompt and save receipt hard cut

Intent：接入post-completion并行协调，只有AppliedNow创建诚实save receipt；删除development-only用户可见语义。

Write scope：GalateaServices、prompt resource/composer、receipt/Observation、docs与runtime tests。

Done when：Mail/Note并发drain、best-effort pre-capture failure、durable post-capture recovery、save receipt canonical grammar与四种prompt组合一致。

Status：Complete；runtime/save receipt/Observation/admission focused tests覆盖真实MemoPod apply、并发2、failure matrix、rewind pending恢复、Mail独立性与旧V0 grammar拒绝。

### R0 — integrated review and validation

Intent：独立review authority、crash/cancellation、prompt/runtime/doc一致性，收尾后串行跑focused与full Galatea suite。

Status：Complete；最终独立复审无must-fix。Character Note runtime/classifier focused tests 24/24、Release Galatea.Server build 0 warning / 0 error、docs checker 21 files / 0 diagnostics、`git diff --check`均通过。最终Debug suite排除下述独立test-harness residual后为537 passed / 1 live skipped；未过滤运行仅剩`NormalizerAndMainAgent_ShareOneRegistryClientAndOwnerDisposesOnce`失败，该测试在`WebApplicationFactory`已启动异步shutdown但尚未完成Completion client disposal时立即断言`DisposeCount`，可单独稳定复现，未修改Character Note scope来掩盖此旁支时序问题。

## 12. Acceptance summary

V1完成必须同时满足：

1. 同一terminal Action最多产生一个durable capture；zero也settled。
2. 同一captured batch最多对应一组stable `(SourceAction, ordinal) -> local MemoId`映射。
3. crash/restart后只会得到完整base或完整target，不会重复Append。
4. durable store与Pod mismatch会Quarantine，不会猜测修复。
5. Default Pod不存在分类、复制或跨Podmutation。
6. 只有proven AppliedNow才生成“已保存”可见回执。
7. current prompt、extractor、receipt、runtime和docs不再声称“存储尚未实现”。
8. SessionJournal、RecapGrid、Prepared wire与main-request recall仍保持不变。

完成V1后，下一轮最自然的入口是single-Default-Pod recall planner与`(PodId, MemoId)` SourceId codec；不是动态聚类。是否需要placement-independent identity留到真实reclass设计再决定。
