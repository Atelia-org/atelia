# DerivedRecap Grid WP-02：Content-addressed Contracts 与 MaintainerControlPlane

状态：Ready；WP-01 stable Timeline handoff complete；尚未开工

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

默认候选是独立canonical control journal/state：definitions少、写入稀疏、适合Agent自主写与CAS，且不扩大raw event语义。
preflight仍必须用证据比较以下候选并选择且只选一个：

- raw SessionJournal control actions；
- 独立 control journal；
- versioned operator config/state。

比较维度：Agent自主写权限、branch语义、CAS、crash、secret隔离、Grid reset后存活、inspect/export与实现复杂度。若选择raw
actions，必须证明它们是Agent control facts而非把derived Cell写回raw；若选择独立journal/config，必须定义exact Ref/Agent
binding与backup/restore/explicit-reinitialize authority。禁止同时从两处merge“最新定义”。Control是Agent意图authority，
不提供像Grid那样的普通reset。

runtime对exact scope tuple只打开一个确定性的canonical carrier/path，绝不扫描backup/quarantine/export或按mtime找latest。
restore只在Host关闭、expected scope/version验证通过后原子替换canonical carrier；显式reinitialize也只替换该carrier并推进
generation，旧副本永久inert，crash后old-or-new valid。allowlist/scope/budget/capability只裁决新的Put/activate mutation，
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

## Handoff to WP-03

交付 canonical codecs/goldens、Cell/RowView locator字段定义、content-equivalence fixtures与ControlPlane test double。
