# Character Note Default MemoPod V1

## 状态

- 方案日期：2026-08-30
- 当前状态：Design Candidate；D0文档已建立，代码工作包尚未开始
- 前置版本：[`Character Note Request Receipt V0`](character-note-request-receipt-v0.md)
- 本轮目标：把已识别的Character Note保存请求幂等地写入每个角色唯一的Default MemoPod，并只在durable apply已经证明成功后生成诚实回执
- 本轮不包含：静态分类、PodCatalog、动态聚类、多Pod routing、Memo内容整理、主线程recall注入

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

角色继续通过叙事Action明确提交保存请求。主线角色模型不直接调用Memo工具，也不提供`PodId`、分类、标签或`MemoId`。

```text
terminal Action
  -> CharacterNoteExtractor
  -> durable capture / zero-result tombstone
  -> Default MemoPod apply
  -> durable apply settlement
  -> best-effort future Observation receipt
```

成功回执只证明本次请求对应的ExactText已经进入Default MemoPod的committed Frozen文档。它不承诺：

- 已分类、生成Title/Gist/Summary或建立embedding；
- 已接入主线程recall；
- pending可见回执在进程重启后仍会投递；
- Undo、rewind或SessionJournal回退会自动删除Memo。

只有收到`Note 保存回执`才表示保存成功。没有回执不应被解释为保存成功或失败；operator仍可通过durable store诊断。

## 2. Default Pod语义

每个启用Character Note的user有且仅有一个Default Pod：

- `PodId`在character-memory store首次创建时随机分配、durable保存、永不重新生成或复用；
- Topic是code-owned固定文本：`该角色主动提交、尚未分类的长期笔记。`；
- 首次provision即创建并Freeze空Pod，因此“始终存在”表示stable logical identity与committed document，不表示长期Editable；
- 一个Action中的`1..N`条Note在同一个Editable epoch中按叙事顺序Append，并只Freeze一次；
- V1没有Remove、Update、reclass、split或merge；Default Pod只追加；
- 不能由filesystem enumeration猜测Default Pod，SQLite meta中的exact `PodId`是binding authority。

V1阶段所有Note都进入Default Pod。未来出现分类Pod后，Default可以演化为unclassified fallback；是否移动历史Memo必须另立durable topology工作包，V1不预埋物理复制或跨Podtransaction。

## 3. Authority model

| Owner / carrier | Authority semantics | 明确不是 |
|:--|:--|:--|
| SessionJournal terminal Action | 角色本轮叙事Action与`SourceAction`的durable authority | Memo corpus或apply receipt |
| Character Note extractor output | 对exact terminal Action的一次versioned语义提取结果 | `PodId`、`MemoId`或保存证明 |
| Character-memory SQLite store | extraction capture、zero-result tombstone、operation identity、apply plan与settlement authority | 当前active Memo正文集合的查询authority |
| Default MemoPod committed document | 当前active Memo exact text与Pod-local ID的authority | extraction provenance、SessionJournal history或分类authority |
| in-process receipt queue | 已render的future Observation notice | durable apply authority、restart recovery queue |
| Debug log / SessionJournal receipt text | development evidence / narrative history副本 | replay、migration或Memo apply输入 |

SQLite为了crash recovery可以永久保存captured requested ExactText与EvidenceQuote；这份副本的authority问题是“角色提交了什么请求”，不是“当前Pod有哪些active Memo”。V1不提供删除或修订，因此两者不会产生active lifecycle分叉；未来一旦加入Remove/correction，必须重新审视retention与current-state projection，不能自动把capture表升级成第二份Memo corpus。

## 4. Stable identity

### 4.1 Action capture identity

一次提取capture由以下内容冻结：

```text
SourceAction
VisibleActionSha256
VisibleActionUtf8Bytes
ExtractorContractId
ordered intents[]
```

`SourceAction`是capture primary key。同一地址再次reconcile时：

- hash、byte count、contract与ordered intents commitment相同：读取既有capture，不再次插入；
- 任一identity字段不一致：typed conflict / quarantine，禁止覆盖；
- `0` intent也写durable tombstone，防止admission重复调用provider。

### 4.2 CharacterMemoId

每条captured intent由store分配一个Pod-independent stable `CharacterMemoId`：

```text
cm1:<32 lowercase hex>
```

它由runtime生成，不进入extractor artifact。V1中它映射到：

```text
CharacterMemoId -> (DefaultPodId, Pod-local MemoId)
```

未来若发生reclass，local locator可以改变而`CharacterMemoId`保持稳定。Galatea未来的`RecallEntry.SourceId`应编码`CharacterMemoId`，而不是把可变placement当成长期source identity。

### 4.3 Batch identity

一个Action中的ordered intents是一个apply batch：

- `0` intent：capture settled，无Memo effect；
- `1..N` intent：全批进入同一个Default Pod Freeze candidate；
- V1只有一个目标Pod，因此batch可以做到外部只观察完整base或完整target；
- 任何intent不能单独报告Applied，直到整个target document已证明committed。

## 5. Durable store logical schema

V1使用独立的per-user SQLite store，不复用delegation SQLite schema：

```text
character_memory_meta
  schema_version = 1
  user_id
  session_repository_id
  capture_frontier
  baseline_selected_head?
  default_pod_id
  revision

note_action_capture
  source_action_address PK
  visible_action_sha256
  visible_action_utf8_bytes
  extractor_contract_id
  artifact_count
  state: Captured | Planned | Applied | Quarantined
  base_pod_state_sha256?
  target_pod_state_sha256?
  quarantine_code?
  revision

character_note
  source_action_address FK
  artifact_ordinal
  character_memo_id UNIQUE
  exact_text
  evidence_quote
  memo_id?
  revision
  PK(source_action_address, artifact_ordinal)
```

初始上限沿用extractor合同：每个Action最多16条、每条ExactText 64 KiB、每条EvidenceQuote 8 KiB、总ExactText 256 KiB。Store reopen会重新验证这些bounds、strict UTF-8、canonical IDs、row count与meta identity。

每个store目录有process-lifetime exclusive lock。生产只允许一个writable owner；`UserSessionHost.TurnLock`继续序列化同一session的reconcile，但不能替代跨进程lock。

## 6. Apply protocol

### 6.1 New capture

```text
read exact target
  -> run extractor when capture absent
  -> final SessionJournal head fence
  -> SQLite transaction captures ordered batch / tombstone
```

capture一旦committed，后续apply不再调用extractor。Provider failure、invalid output或caller cancellation发生在capture前时无SQLite mutation；admission可以以后重试。

### 6.2 Plan before effect

对非空Captured batch：

1. strict Open Default Pod，要求Frozen；首次provision走Create+empty Freeze；
2. 读取base committed Pod state identity；
3. `ResumeEditing`；
4. 按ordinal Append全部ExactText，取得planned local Memo IDs；
5. 计算target candidate state identity；
6. SQLite transaction冻结base identity、target identity与每条planned MemoId，将batch转为Planned；
7. 只有plan已durable后才允许`FreezeAsync`；
8. Freeze proven Published或reopen证明target后，SQLite transaction转为Applied。

如果plan transaction失败，不能Freeze当前Editable handle；丢弃handle并重新Open authoritative base。

### 6.3 MemoPod state identity seam

现有MemoPod public API不暴露canonical document identity，而V1恢复需要区分exact base/target。增加一个刻意小的read-only seam：

```text
MemoPod.ComputeStateIdentity()
  -> schema-qualified SHA-256 of the canonical complete document candidate
```

它可以在Editable或Frozen阶段调用，只返回opaque identity，不返回document、prompt、snapshot、revision或detached resolver。identity必须覆盖`PodId`、Topic、`nextMemoId`、ordered active Memo IDs、metadata与ExactText，因此allocator high-water变化也会改变identity。

这不是MVCC或public snapshot；它只用于single-owner external-effect reconciliation。

### 6.4 Recovery matrix

| Durable state | Observed Pod identity | Action |
|:--|:--|:--|
| Captured | any valid current base | 重新构造plan；尚无Pod effect |
| Planned | exact base | 重放Append；每个返回MemoId必须等于plan；candidate identity必须等于target；Freeze |
| Planned | exact target | effect已提交；补写Applied settlement |
| Planned | neither | Quarantined；禁止猜测、覆盖或再次Append |
| Applied | exact target | AlreadyApplied success；不再次Append |
| Applied | other | Quarantined；store与Pod authority分叉 |
| Quarantined | any | fail closed；只允许显式operator诊断/未来maintenance |

`FreezeAsync`返回commit-indeterminate时立即discard handle并strict reopen；只按上表接受base或target。V1不做physical rollback。

## 7. Runtime sequencing

### 7.1 Post-completion

Mail与Note继续消费同一份`GalateaTerminalActionExtractionTarget`。Note task升级为：

```text
capture if absent
  -> reconcile Default Pod apply
  -> return Zero | AppliedNow | AlreadyApplied | BaselineCovered
```

两个任务仍可并行启动并都必须drain。它们是同一terminal Action上的独立durable effects；Mail failure不回滚已经settled的Note，Note failure也不撤销Mail capture。

V1保留现有product policy：Note provider/unavailable属于best-effort后处理，不把已经完成的main Action改报失败；但只要capture已经durable，apply/recovery invariant failure必须fail closed并留下可诊断状态，不能伪装成zero/no-match。

只有`AppliedNow`且final head fence仍成立时，当前进程才创建并enqueue一条`Note 保存回执`。`AlreadyApplied`不自动重新queue，避免restart/admission重复可见回执；因此crash-after-apply-before-enqueue仍可能丢失可见回执，这是明确的at-most-once delivery限制，不影响Memo保存事实。

### 7.2 Admission/restart

现有`ReconcileDurableAdmissionAsync`在Mail gap reconciliation旁增加Character Note reconciliation：

- 只处理latest exact terminal Action，不扫描完整history；
- store baseline physical frontier覆盖启用前的历史；
- absent capture时可以重跑extractor；
- captured/planned batch不重跑extractor，只恢复apply；
- admission返回`AppliedNow`也不自动enqueue receipt；可见反馈仍只属于原post-completion happy path。

### 7.3 Cancellation/failure

- caller/shutdown cancellation原样传播；
- provider failure/invalid output发生在capture前：无capture，未来可重试；
- SQLite capture成功后，不因caller取消回滚capture；apply可在后续admission继续；
- Pod NotPublished：保持Planned/base，未来重试；
- Pod commit-indeterminate：reopen并按base/target settle；
- mismatch：Quarantined并fail closed；
- fatal exception不包装成availability failure。

## 8. Config and provisioning

Galatea root config hard-cut到V6，每个user新增required：

```json
"characterMemoryStateDir": "character-memory/alice"
```

理由：Character Memory有独立privacy、backup、lock与lifecycle owner，不能藏进`sessionDir`、复用`delegationStateDir`或依赖CWD推导。

V6 exact path规则：

- nonblank；relative以config directory为base；runtime为canonical absolute path；
- 所有user之间exact unique、双向non-nested；
- 与所有`sessionDir`、`delegationStateDir`和optional `callLogDir`双向non-nested；
- existing path components拒绝symlink/reparse point；
- Note binding为`null`时不open/create该目录；
- Note binding非`null`且session writable attach时：missing则以current physical frontier创建baseline store与empty Default Pod，existing则strict open；
- maintenance mode不create、不apply、不取得writer lock。

Bootstrap写V6与该字段，但不创建character-memory state。Ignored live config migration必须停服、备份并单独执行；tracked tests不能代替本机迁移。

## 9. Prompt and receipt hard cut

启用Character Note binding时，main prompt从development request appendix hard-cut为真实保存Quick Start：

- 教角色明确提交完整Note原文；
- 明确只有后续`Note 保存回执`证明保存成功；
- 不承诺分类、metadata enrichment或recall。

Extractor semantic contract从development request语义升级；tool name与字段保持不变，`ContractId`自然变化。

`PlayerTurnNotice.NoteRequestReceipt` hard-cut为`NoteSaveReceipt`：

- heading：`Note 保存回执`；
- info string：`character-note-save-receipt`；
- body明确`已成功保存到默认MemoPod`，逐字列出ExactText；
- 仍然每轮最多一条、必须是最后notice、legacy dialect拒绝；
- queue仍是per-session bounded in-process FIFO与at-most-once delivery attempt。

不保留旧heading、旧info string或旧strong type compatibility reader；项目尚未发布，及时重构优于双协议。

## 10. Explicit non-goals / complexity tripwires

V1明确不实现：

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

Status：In progress。

### A0 — MemoPod state identity seam

Intent：增加覆盖canonical complete document candidate的opaque SHA-256，只解决external-effect recovery。

Write scope：`prototypes/MemoPod`、`tests/MemoPod.Tests`、MemoPod README/target doc。

Done when：Editable/Frozen、`nextMemoId`变化、metadata/content变化与reopen identity都有golden/negative tests；无snapshot/version API扩张。

### A1 — Character-memory durable store

Intent：实现独立SQLite schema、lifetime lock、baseline、capture/tombstone、plan/settle/quarantine与strict snapshot。

Write scope：`prototypes/Galatea/CharacterMemory`、focused Galatea tests。

Done when：duplicate commitment、bounds、old/new recovery、indeterminate、mismatch quarantine与reopen完整覆盖。

### A2 — Default Pod reconciler

Intent：把captured batch通过plan-before-effect协议apply到Default MemoPod。

Write scope：CharacterMemory domain/reconciler、Galatea↔MemoPod project reference、focused tests。

Done when：zero、first create、many-note batch、AlreadyApplied、crash windows、planned-ID mismatch与single Freeze语义通过。

### A3 — V6 config and lifecycle composition

Intent：增加explicit character-memory path、provision/open/dispose与admission reconciliation。

Write scope：config reader/DTO/bootstrap/current contract、session composition与config tests。

Done when：path disjointness、null binding no-touch、enabled create/open、maintenance no-create、invalid existing fail closed通过。

### A4 — runtime, prompt and save receipt hard cut

Intent：接入post-completion并行协调，只有AppliedNow创建诚实save receipt；删除development-only用户可见语义。

Write scope：GalateaServices、prompt resource/composer、receipt/Observation、docs与runtime tests。

Done when：Mail/Note并发drain、best-effort pre-capture failure、durable post-capture recovery、save receipt canonical grammar与四种prompt组合一致。

### R0 — integrated review and validation

Intent：独立review authority、crash/cancellation、prompt/runtime/doc一致性，收尾后串行跑focused与full Galatea suite。

## 12. Acceptance summary

V1完成必须同时满足：

1. 同一terminal Action最多产生一个durable capture；zero也settled。
2. 同一captured batch最多对应一组stable CharacterMemoId与local MemoId。
3. crash/restart后只会得到完整base或完整target，不会重复Append。
4. durable store与Pod mismatch会Quarantine，不会猜测修复。
5. Default Pod不存在分类、复制或跨Podmutation。
6. 只有proven AppliedNow才生成“已保存”可见回执。
7. current prompt、extractor、receipt、runtime和docs不再声称“存储尚未实现”。
8. SessionJournal、RecapGrid、Prepared wire与main-request recall仍保持不变。

完成V1后，下一轮最自然的入口是single-Default-Pod recall planner与`CharacterMemoId` SourceId codec；不是动态聚类。
