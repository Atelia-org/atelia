# DerivedRecap Grid WP-01C：Single Durable Timeline Ledger

状态：Complete；两路independent review均GO；依赖 WP-01B complete

只需加载：Grid target、Master、WP-01 overview、WP-01B handoff与本文。

## Intent

选择并实现唯一Timeline durable backend，分别提供immutable policy put、policy whole-head CAS、atomic row insert + append-head
CAS、selected-path reconciliation whole-head CAS，以及backup/restore与bounded operator surface。

## Backend decision gate

Directory+canonical files与独立SQLite ledger先按同一组语义维度做结构性A0比较：row insert/head CAS、two-writer、crash
before/after commit、canonical corruption、branch/path query、backup/restore、file/state/API budget。A0不声称Directory候选已有一套
可执行的同fixture实现；选择winner后删除loser code/dependency/tests，禁止双写、fallback或configurable dual backend。Timeline与Grid
即使用同种技术也必须是独立durability domains，禁止跨库transaction。

本包裁决：唯一production backend为同assembly内SQLite，direct pin `Microsoft.Data.Sqlite 10.0.10`；因其传递native bundle
`SQLitePCLRaw.lib.e_sqlite3 2.1.11`命中GHSA-2m69-gcr7-jv3q（影响`<=2.1.11`），同一owner另加security override direct pin
`SQLitePCLRaw.bundle_e_sqlite3 2.1.12`。实测native SQLite为3.53.3且NuGet audit零warning；Control不重复pin。`Pooling=false`、
rollback journal `DELETE`、`synchronous=EXTRA`、短`BEGIN IMMEDIATE` writer transaction。Directory+canonical files经结构性A0分析后
淘汰：它需要另造multi-file atomic CAS、index-root publication与restore protocol，却没有减少authority或failure mode；可执行的
四transaction、two-writer、crash、branch snapshot与backup/restore fixture只用于winner SQLite。最终Directory loser的code/dependency/
test/config均为零；
WP-01B `InMemoryHistoryTimelineLedger`已迁到test assembly，只保留语义测试载体，不存在production backend selector。

canonical inventory固定为：

```text
<repository>/derived/history-timeline/v1/
  locks/<ref-id>.lock
  refs/<ref-id>/locator.json
  refs/<ref-id>/timelines/<timeline-id>.sqlite
```

normal create/open只按exact Ref locator与exact Timeline slot工作；不扫描orphan、backup、mtime或“latest”。
V1 durability/lease只在Linux上验证并启用；其他platform必须返回typed `TimelineStorePlatformUnsupported`，不得降级成无fsync或
无`flock`模式。上表是normal canonical slots，不是所有crash残留清单：process death可留下unreferenced Timeline SQLite、locator/restore
dot-temp或SQLite exact-slot旁的rollback journal；它们只能由SQLite exact-slot recovery或后续explicit inventory/retention action处理，
normal create/open绝不把它们当候选、backup或“latest”。

## In scope

- V1 create/open strict schema；以下四种transaction不得合并成隐式“最新状态”写入：
  - `PutPolicy(canonicalPolicy)`：strict decode/canonical equality后content-addressed idempotent insert，绝不改head；
  - `CompareExchangePolicy(expectedWholeHead, nextPolicyDigest)`：CAS比较完整`TimelineHeadRef`，只替换active policy并推进
    generation；next policy必须已存在且属于同Timeline；即使empty head仍保持row/raw fence为null；
  - `CommitRow(proposal)`：raw authority内部重读proposal绑定Ref的current selected head并匹配outer captured head，在同一
    transaction完成immutable row insert与完整head CAS；new head把row设为proposal row、raw fence设为outer captured head、
    generation加一并保留expected active policy，不顺带切policy；
  - `ReconcileSelectedPath(expectedWholeHead, candidate)`：CAS比较完整`TimelineHeadRef`，在transaction内部复验owner-bound
    captured raw head，只把head回指expected selected predecessor chain上的共同ancestor或empty；不插row、不切policy，non-empty
    target记录本次captured raw fence，empty target保持raw fence为null，并推进generation；
- whole-head CAS比较TimelineId/RefId/head row/active policy/selected raw fence/generation全部字段；caller传入的
  `BoundHistorySegmentRange`只是typed evidence，不代替raw authority；
- Timeline durable owner独占`TimelineHeadRef`与`ActiveTimelineLocator` strict canonical codec：strict UTF-8、duplicate/unknown/
  null/case/order/whitespace拒绝并要求canonical byte equality；其他层不得自写JSON locator；
- create/open/restore在读取或分配前执行code-owned hard caps：policy/descriptor沿用4 KiB/16 KiB，HeadRef/locator canonical bytes
  各不超过4 KiB；backend/backup总bytes、entry/row count、path page与restore copy上限必须在backend plan lock给出exact常量和
  exact-cap/cap+1 tests，不得由输入或config放宽；
- `inspect/export/verify` read-only/no-create/bounded；
- verified backup + restore演练；
- canonical `ActiveTimelineLocator` per Ref；`abandon --confirm <RefId,TimelineId,locator-generation>`在Host关闭时CAS到
  explicit initial policy创建的new TimelineId；旧ledger/backup永久inert，不提供普通in-place reset；
- corruption/unknown schema fail closed，不自动重新分段。
- durable backend维护per-selected-head exact membership snapshot/witness，至少同时支持`RowId -> descriptor`与
  `EndInclusive -> RowId/descriptor` lookup；ancestor reconcile必须切换到已提交snapshot root，不能从Timeline head/root回扫或复制
  全path。online reconcile只遍历bounded raw prefix并做index probe；若Beyond则由WP-01B的owner-bound offline streaming seam对caller
  提供的fresh audited forward cursor执行单次bootstrap-to-captured-head pass、逐address probe index并只保留latest match。durable port不得
  要求候选集合、global/latest successor扫描、65,536 candidate cap或O(n²) reopen；最终仍构造同一opaque candidate并执行第四whole-head CAS。

## Crash/concurrency matrix

1. `PutPolicy` crash只见absent或完整canonical policy；不改变head；
2. `CompareExchangePolicy` crash只见old或完整new whole head，row set不变；empty generation 0可变为empty generation 1+；
3. `CommitRow` crash只见old head，或完整row + new head；new head保留expected active policy；
4. `ReconcileSelectedPath` crash只见old或完整new whole head，row set与active policy不变；boundary/interior/empty target均比较
   whole expected head，raw drift或target不在exact selected predecessor chain时零mutation；
5. raw authority重读current selected head与proposal/reconcile candidate的outer captured head不等时，row insert与head mutation都必须为零；
6. 从same expected whole head竞争的policy CAS、row append与reconcile只能一个胜出，loser零mutation；同类two-writer亦然，且只匹配
   generation但其他head field不同必须失败；
7. crash/reopen重新计算strict canonical whole-head digest，只见old或完整new head；
8. backup manifest提交TimelineId/RefId/schema/generation、canonical whole-head digest与whole-backup digest；restore先把外部backup复制到
   canonical root内private temp，再只以该私有副本复算size/digest、strict schema与全部hard caps，仅在Host关闭、expected active
   scope/version exact且backup包含active head时atomic old-or-new替换；
   更旧backup只能走abandon；
9. corrupt descriptor/head/locator/selected-boundary index或digest mismatch使ledger Invalid，normal path零mutation；
10. abandon错误确认零mutation；正确确认的locator crash只见old或new，旧bytes不改且旧Grid/control scope自然失配；
11. inspect absent/existing均不create、不加载Maintainer/provider/secret；
12. bounded path pagination，不把全History/全DAG物化进内存；open/restore每个hard cap做exact-cap接受、cap+1拒绝。
13. selected-path长链证明RowId membership、EndInclusive probe与ancestor snapshot切换均为indexed/bounded；offline streaming scan的
    内存不随row候选数增长，scan期间raw drift及policy/append/reconcile CAS竞争均零额外mutation并返回typed loser outcome。

## No-Go

- StateJournal/EventJournal复用迫使Timeline引入不需要的process-exclusive/second-protocol complexity；
- 损坏时静默reset/repartition；
- losing backend或migration runner留在production；
- Agent/operator需要手改live store。

## Done when

single backend、operator action、crash/backup/branch tests、affected build/docs/diff与independent review全部green。

## Production surface and lifecycle

- `HistoryTimelineFactory.Create(SessionJournalReadView, InitialPolicySpec, estimators)`只创建fresh identity；已有locator返回
  `AlreadyExists`且不按caller spec切policy。caller随后用`Open`取得`HistoryTimelineHandle`；
- `HistoryTimelineFactory.Open(SessionJournalReadView, estimators)`exact验证locator、SQLite scope/schema/head与active policy/estimator，
  返回同时含`Coordinator`与`Reader`的disposable capability；两者共享operation-refcount lifetime guard，dispose先进入closing、拒绝新
  operation、等待已进入operation退出后才释放shared lease；dispose后所有operation closed fail；
- `HistoryTimelineMaintenance.OpenReader(repositoryPath, RefId)`只给WP-02/04/05所需的read-only handle；`SelectedRow`不可由assembly
  外构造，ancestor witness绑定canonical repository/Ref/Timeline/whole head/row/descriptor digest并由同一Reader复验；
- 所有backend types、port与test hooks均为internal；无IVT的external-composition fixture证明public caller无需也无法选择backend；
- per-Ref normal handle持shared lifetime lease；Create/Restore/Abandon需要exclusive lease。locator第一次为generation 0；abandon先
  durable-create新DB再atomic locator generation+1，旧DB bytes不改且normal path不再发现；
- Restore只要求current schema与canonical head仍可strict读取并exact等于manifest，而非要求whole current store healthy；因此可用
  same-head verified backup修复row/trie corruption。current head/schema不可读时Restore拒绝，只能exact-confirm Abandon。

## Durable indexes and hard caps

selected path由两棵content-addressed immutable fixed-depth byte-radix trie组成，key分别为32-byte `RowId`与16-byte
`EndInclusive`；每个committed row保存snapshot roots。append结构共享且最多新增50个nodes；reconcile只能切到expected selected
predecessor chain已提交root或empty，不存在global End-to-candidate authority，也不回扫head/root。相同End、不同policy的合法candidate
可共存；只有expected selected chain决定authority。

production caps为：policy/head/locator 4 KiB、descriptor/backup manifest 16 KiB、policies/rows各65,536、trie nodes 3,276,800、
path page 128 rows / 4 MiB、DB与restore copy各8 GiB。常量不可由config放宽；internal small-limit fixture覆盖exact cap与cap+1。

## Implementation record

- product：strict HeadRef/locator/manifest codecs、SQLite ledger/trie、factory/lifetime/Reader、inspect/verify/backup/restore/abandon；
- tail hardening：snapshot canonical commitment按exact predecessor递推验证；hot open不做physical recount；online/offline reconcile每次只开
  一个fixed-root boundary probe；Restore只验证private copy；strict sqlite_schema/PRAGMA/FK/historical reference与paged trie verify；
  lifetime drain、fresh lock fsync、exact locator existence和offline Busy/Invalid/Absent/store-limit typed mapping均已有focused fixture；
- tests：真实SQLite reopen/branch snapshot/same-End/mixed CAS、caps/root/index/schema/PRAGMA corruption、long divergence writer interleave、
  read-only、无IVT public surface、16窗口child crash harness；
- architecture：只有HistoryTimeline product direct pinSQLite packages（`Microsoft.Data.Sqlite 10.0.10`与security override
  `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`），product assembly不存在in-memory backend或公开backend selector；
- scope：未修改old DerivedRecap、Galatea、CLI composition或current production behavior；WP-07A只负责把既有typed library actions映射成CLI；
- final serial validation：Timeline full 156/156、SessionJournal raw audit 19/19、walking architecture/package gates 13/13、
  assembly-external public surface 2/2、`Atelia.sln` build 0 warning / 0 error、docs checker 15/0、diff check clean；
- review/commit evidence：冻结candidate经两路独立只读review均为GO；包含本次变更的containing commit作为commit evidence，
  不在提交前虚构commit hash；
- cutover/platform boundary：current production仍由old DerivedRecap composition承载，尚未cutover；Timeline V1 durable lease/fsync
  仍为Linux-only，其他platform只返回typed unsupported，不提供弱durability fallback。

## Handoff to WP-02

交付stable reader/witness、backend diagnostics、Timeline scope identity和abandon后Grid/control必须重建的规则。
production factory必须在本包绑定canonical repository path、durable ledger与`ActiveTimelineLocator` create/open；new Ref创建new
TimelineId也在此gate完成，不能把WP-01B internal in-memory carrier暴露为可选backend。
