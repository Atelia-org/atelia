# DerivedRecap Grid WP-02：Content-addressed Contracts 与 MaintainerControlPlane

状态：Complete；independent review与final serial gates green；WP-03 Ready；current production尚未切换

只需加载：目标设计、总计划、WP-01 handoff、本文和 WP-03 摘要。

## Intent

锁定“想要什么”与“是否已经算过”的分界：definitions/recipes是content-addressed canonical values，active recipe是唯一
mutable CAS；Cell 输入identity由exact visible contents计算。本包不存Cell、不调用模型。

## In scope

- complete canonical `FamilyDefinition`、`MaintainerDefinitionRevision`及其digests；
- `BuildTarget`、`GridBuildRecipe`；
- `PriorInputProjectionDigest`、`EvaluationKeyDigest`；
- canonical codecs、domain separation、strict UTF-8/duplicate/unknown-field rejection；
- `MaintainerControlPlane`：
  - `PutFamilyDefinition`
  - `PutMaintainerDefinition`
  - `PutBuildRecipe`
  - `CompareExchangeActiveRecipe`
  - `ReadSnapshot`
- allow-listed family、DeclarativeSpec、scope/budget/capability validation；
- exact active recipe与Timeline path/bootstrap binding；
- exact control scope `(repository/session identity, RefId, TimelineId)`；
- `PutBuildRecipe`使用WP-01只读Timeline witness验证bootstrap ancestor，不读取Grid且witness不进入digest。

WP-02只能消费WP-01C public `HistoryTimelineReader`/`HistoryTimelineReaderHandle`：从closed snapshot读取whole head，通过
`ReadSelectedRow`或bounded newest-to-oldest path page取得不可伪造的selected row/witness，并由同一Reader的`ValidateWitness`
复验canonical repository/Ref/Timeline/whole-head/row/descriptor commitment。不得引用SQLite、ledger port、locator codec或
Coordinator mutation capability；backend Busy/Invalid/Stale保持typed，不按message或global End lookup猜ancestor。

## Physical carrier decision

V1已选择且只保留独立bounded canonical whole-state carrier；raw SessionJournal action与operator config均不是第二authority。
canonical slot为
`<repo>/control/recap-grid/v1/refs/<ref>/timelines/<timeline>/{control.json,lifetime.lock,writer.lock}`，故Grid root删除不影响
Control，Timeline换新`TimelineId`也不会fallback旧Control。normal handle长期持shared lifetime lease，每次mutation短持writer exclusive；
maintenance固定按Timeline lifetime -> Control lifetime exclusive -> writer取得authority。V1 durable lease/fsync为Linux-only，其他平台
typed fail closed。

runtime对exact scope tuple只打开一个确定性的canonical carrier/path，绝不扫描backup/export/temp或按mtime找latest。
restore只在Host关闭、expected scope/version验证通过后原子替换canonical carrier；显式reinitialize也只替换该carrier并推进
generation；两者都生成new `ControlInstanceId`且generation严格为current + 1，防止ABA。旧副本永久inert，crash后old-or-new valid。
allowlist/scope/budget/capability裁决每次Put/activate mutation及其entire recipe base closure，projected calls按closure累计，
`ReadSnapshot`不得按当前config过滤、重解释或自动deactivate已接受state；budget是admission ceiling，不产生spent-call ledger。
exact runtime binding缺失时typed `BindingUnavailable`，不得fallback当前catalog或旧recipe。

若默认候选无法满足atomic CAS、crash、backup/inspect或scope binding才允许选择其他carrier；必须记录No-Go原因并删除loser。

## Canonical identity rules

```text
FamilyDefinitionDigest = H(SystemPrompt, OrderedTools, OutputProtocol, InputRenderingProtocol)
DefinitionDigest       = H(LogicalColumnId, FamilyDigest, Target, Capability, DeclarativeSpec, MaxBytes)
RecipeDigest           = H(TimelineId, BootstrapRow, BuildTarget, BaseRecipe, RecomputedColumns)
PriorProjectionDigest  = H(ordered LogicalColumnId + visible ContentDigest)
RowDescriptorDigest    : HistorySegmentDescriptorDigest
EvaluationKeyDigest    = H(RowDescriptorDigest, DefinitionDigest, PriorProjection | FirstRow)
```

`RowDescriptorDigest`必须直接使用HistoryTimeline的typed `HistorySegmentDescriptorDigest`，不得重新包装裸string。
Hasher只接受canonical typed values，不接受caller-provided digest/pre-rendered provider JSON作为authority。provider request
fingerprint是telemetry，不是Cell identity。model/connection仅在被明确声明为A/B语义时进入Definition。

V1 outer wire中的nested canonical values（例如Recipe中的BuildTarget、Cell中的EvaluationKey）显式以JSON
base64 byte string承载；这是版本化wire决策，不是serializer偶然细节。decode必须先受outer exact/cap约束，
再对child递归执行各自的`DecodeExact`、schema和cap验证；child的whitespace、reorder、unknown、trailing bytes或
digest不匹配不得因outer JSON仍合法而被接受。

## Out of scope

- SQLite Grid schema、Cell/RowView persistence；
- durable Campaign/Attempt/Settlement；
- Completion client、family lane/cache；
- Galatea composition；
- 自动依赖推断或per-column Timeline。

## Write scope

- WP-00锁定的 new RecapGrid abstractions/control owner及tests；
- 选定的单一control carrier adapter；
- bounded inspect/export for control snapshot；
- 不改 old DerivedRecap definitions/config semantics。

## Validation matrix

1. canonical roundtrip与digest golden；
2. typed construction先规范化caller data；strict canonical decoder拒绝property reorder、extra whitespace、duplicate、unknown与case mismatch；
3. invalid UTF-16、default identity、wrong Timeline/base cycle/off-chain bootstrap拒绝；
4. same visible prior contents across different RowViews得到相同projection/key；
5. column order/label/content、definition、row commitment任一变化改变key；
6. Put same value idempotent；same digest/different bytes typed Invalid；
7. active recipe two-writer CAS只一胜；atomic replace前后child crash、失败零mutation、reopen；
8. Grid DB完全删除后definitions/recipes/active仍可读；
9. unauthorized family/scope/over-budget request零mutation；
10. A-v1/A-v2与overlay/full same target产生不同definition/recipe identities；
11. Family/Maintainer/Recipe完整canonical values在Grid reset和进程重启后仍可重建runtime语义；
12. `DefinitionRevisionId`从identity model删除；若实现保留human label，只能是明确非authority metadata且不得进入artifact wire。
13. Timeline selected-path root/snapshot corruption、Reader `Busy`、whole-head `Stale`与handle `Disposed`均在control mutation前typed
    fail closed；不得重新open、扫描global End或把损坏witness降级成caller fields。
14. state publish前failure保持canonical head/bytes不变；rename publish后hook/fsync failure返回typed
    `CommitIndeterminate(Intended, Observed?)`，不得谎报普通Invalid。Backup同理返回`PublishIndeterminate`；reopen以observed exact head
    reconcile。Restore/Reinitialize安装new instance且generation=current+1；
15. `RecapRowView`与`FulfilledViewKey` canonical bytes可在没有non-durable RowBuildSpec/cells的WP-03 restart中standalone exact decode；
    contextual spec/member/scope validation是叠加层，不能迫使Store持久化RowBuildSpec。

## No-Go

- 只存digest不存canonical preimage；
- Grid DB成为control authority；
- control carrier双源合并；
- BuildTarget或runtime route无条件进入EvaluationKey；
- 为resume新增通用durable Campaign状态机。

## Done when

- carrier与权限模型锁定；
- canonical values/hashers/control CAS完整；
- tests/build/docs/diff green；
- reviewer确认 WP-03 可以只消费opaque canonical artifacts与active snapshot。

## Implementation record（2026-08-10）

- formal owner：`SessionJournal.RecapGrid.Abstractions`已承载Family/Definition/Target/Recipe/Projection/EvaluationKey/Cell/RowView/
  FulfilledViewKey canonical values；nested canonical child bytes在V1 outer wire以base64携带，并递归`DecodeExact`与cap；
- runtime owner：新`SessionJournal.RecapGrid.Control`只direct reference Abstractions + HistoryTimeline，无SQLite、Completion、Galatea、
  StateJournal或old DerivedRecap dependency；
- public capability：`Create/Open/OpenReader(repositoryPath, RefId, ...)`内部唯一通过Timeline locator绑定exact Timeline；Handle拥有Timeline
  Reader lifetime，dispose后Reader/Coordinator typed `Disposed`；外部不能注入Reader或backend；
- mutation：Put全head CAS、idempotent same canonical bytes、Activate/Promote分权；Put与activate都重新验证entire stored recipe closure的
  family/capability/carrier/column、selected bootstrap与累计admission ceiling，Control不持久化WP-04 promotion proof；
- maintenance：Inspect/Verify/Export no-create；Backup/Restore/Reinitialize要求exact canonical slot与whole expected head，restore/reinitialize
  安装new `ControlInstanceId`，corrupt current不能在线修复，只能offline exact archive/delete后重新Create；
- crash：CreateNew temp、file `Flush(true)`、atomic replace、parent fsync；test-only child failpoints证明publish前旧state、publish后新state；
  recoverable post-publish fault返回typed indeterminate并在writer lock内best-effort读取Observed head，pre-publish fault仍零state mutation；
- 两条独立只读review线已对canonical identity/recipe派生、admission/base-closure budget、scope/lifetime/
  crash settlement、standalone artifact decode与public dependency surface完成tail closure，最终P0=0/P1=0；
- final serial evidence：HistoryTimeline 156/156，HistoryTimeline public surface 2/2，Abstractions 15/15，Control 26/26，
  Control public surface 2/2，Walking/architecture 13/13；solution build 0 warning / 0 error；NuGet vulnerable package scan零命中；
  docs checker 15/0与`git diff --check` clean。包含本次变更的commit作为exact commit evidence；
- WP-02只完成旁路contracts/control owner；不改变old DerivedRecap/Galatea/CLI composition，production cutover仍只属于WP-08。

## Handoff to WP-03

WP-03可直接消费canonical codecs/goldens、正式Cell/RowView/FulfilledViewKey（含standalone restart decode）、content-equivalence fixtures。WP-03只direct reference
Abstractions并持久化opaque canonical artifacts；不得引用Control carrier。WP-04/05通过public Control Handle/Reader消费snapshot与CAS。
