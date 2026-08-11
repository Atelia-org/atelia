# DerivedRecap Grid WP-05：Getter 与 ContextComposer

状态：Complete；两路independent closure review与final serial validation均GO；current production尚未切换

只需加载：目标设计、总计划、WP-04 handoff、本文和 WP-06 摘要。

## Intent

建立主线推理的唯一pure-read readiness边界：由owner-bound `SessionJournalReadView`打开Getter，按current whole
TimelineHead与active GridBuildRecipe解析exact fulfilled RowView，并向neutral candidate contract返回contributions与sealed-row anchor。
raw tail的选择、fold与Prepared冻结仍完全属于SessionJournal core；Getter不得逐列拼latest Cell、读取raw segment正文或接管tail composition。

## In scope

- 只消费WP-03 public `OpenReader`/owned ReaderHandle与closed `Found|Missing|Busy|Disposed|Invalid`结果；不得引用SQLite、schema、
  maintenance/reset或注入backend selector；whole-store locator/canonical/member/FK mismatch必须原样fail closed；
- select fulfilled ref与materialize RowView/Cells必须使用同一个owned Store ReaderHandle，并提交该handle的exact `StoreIdentity`到selection；
  两phase之间不得reopen。若caller在typed settlement后显式重开，新identity必须使旧selection stale；

- `RecapGridContextFactory.Open(SessionJournalReadView)`内部打开并持有owner-bound Timeline/Control Readers；调用方只传
  `Resolve(completionBoundary, nthPrevious)`，不得注入heads、recipe、Reader或backend；其中`completionBoundary`就是neutral request
  冻结的exact raw-head fence，不另立同义authority；
- membership complete、PriorInputAligned、可选FullRebuildChain provenance；
- exact Cell内容materialization与ordered contributions；
- candidate/partial/absent/invalid/stale/off-lineage typed results；
- head fence before/after materialization；
- neutral SessionJournal context-candidate adapter；
- bounded read-only diagnostics。
- strict `DerivedContext.NthPrevious`：沿exact Timeline predecessor chain选择第n个sealed row，再解析同一active recipe fulfillment；
- selection handle提交whole `TimelineHeadRef`、recipe/target/view/completion boundary及typed
  `HistorySegmentDescriptorDigest`并在materialize时全量复验；不得拆成只比较generation/head-row的弱快照。

## Ownership

- GridReader只读取Grid artifacts/fulfilled refs；
- TimelineReader只通过WP-01C public Reader读取closed whole-head snapshot、exact selected row、bounded path page与selection witness；
  witness必须回到同一Reader `ValidateWitness`，不得构造、拼接或只比公开字段；
- select/materialize冻结并在semantic terminal、materialization末尾复验whole ControlPlane snapshot；materialize要求handle-bound active
  recipe digest仍exact相等；
- pure-read composition只用`RecapGridControlFactory.OpenReader(repositoryPath, RefId)`取得owned handle；snapshot返回完整已接受closure且不按
  当前admission过滤。`TimelineUnsupportedSchema`、Control `UnsupportedSchema`、Busy、Disposed、Invalid均typed fail closed，selection期间
  不重新open或fallback另一Timeline/old config；
- Grid candidate adapter只返回exact row contributions、anchor setups与completion boundary；SessionJournal core继续独立fold raw tail，
  ContextComposer不得接管Prepared/raw request reconstruction；
- SessionJournal core只依赖neutral context candidate contract，不依赖concrete Grid projects。

## WP-04 complete handoff lock

- head-through build可提供non-durable `RecapGridPromotableProof`；ancestor-through只有不含ControlHead expected tuple的
  `RecapGridFulfillmentReceipt`。二者都不是WP-05 read authority。
- WP-05从current active Control snapshot + current exact Timeline head独立构造Fulfilled key并resolve；不得调用Manager mutation、
  Timeline Coordinator/ledger，也不得消费proof/receipt推断active或current head。
- Fulfilled selection与RowView/Cell materialization必须使用同一个owned Store ReaderHandle和同一个StoreIdentity；两阶段间不reopen。
- Manager已完成两路independent review；本节是WP-05的可施工baseline，但不表示production cutover。
- old-head Fulfilled cache可以因post-put authority drift而存在；current active/head exact key不匹配时它天然inert，Getter不得扫描或fallback。

## Out of scope

- 创建row/cell、执行Maintainer、repair/reset；
- provider/Completion client；
- Galatea active lifecycle与CLI cutover；
- Prepared/Started流程改造；但本包必须用throwing collaborators锁定这两相零Timeline/Grid/Control及DerivedRecap
  active/current-route config访问；frozen Host connection registry仍可exact bind；
- “若exact pack/view损坏则退到更早healthy”的fallback。

## Write scope

- new RecapGrid reader/composer owner与tests；
- 必要的neutral SessionJournal context-candidate adapter；
- no-write diagnostic facade；
- 不切 current production composition。

## Validation matrix

1. exact active recipe/head/view成功组合；
2. partial candidate永不出现在main context；
3. same ordinal different branch拒绝；
4. selected head在select/materialize间变化拒绝；
5. exact Cell/View删除、hash损坏、wrong recipe/target/FK拒绝且不fallback；
6. active recipe暂时unfulfilled typed unavailable；
7. raw tail只从fulfilled row end之后开始，无gap/overlap；
8. zero-row/zero-column与FirstRow边界；
9. inspect路径不创建DB、不清理、不加载provider secret；
10. contribution order、target、content limits与canonical hash复核。
11. `NthPrevious=0/1/N`严格取exact predecessor slot；slot missing/damaged、same ordinal sibling branch均拒绝且不跳邻居；
    算法先解析current head/active recipe的exact fulfilled view，再沿`PreviousRowViewDigest`链走n步，逐步复验same recipe与
    Timeline predecessor，不扫描任意RowView；
12. select/materialize间Timeline advance或active recipe promotion返回typed stale；
    Timeline Reader handle dispose、Busy/Invalid、selected-path root/snapshot corruption、whole-head Stale或locator切换也必须fail closed，
    不得重新open后把另一Timeline当同一selection；
13. overlay bootstrap的membership-complete mixed view可读；`PriorInputAligned`/`FullRebuildChain`只是provenance，不是read gate；
14. 无active nonempty recipe时显式`RawHistoryAuthorized/EmptyLineage`，不以Timeline是否已有sealed row为条件；active recipe
    partial/unfulfilled/Invalid绝不raw fallback。第二条raw-only规则是Timeline仍empty时即使recipe已active也返回raw-only且零Store open，
    否则首个row必须先依赖一个永远无法由Getter授权产生的fulfillment而形成死锁；一旦Timeline nonempty，active recipe missing就只能
    `Unfulfilled`；
15. Prepared删除Grid、改变control后仍byte-identical resume；恢复只允许按frozen completion identity从Host connection registry
    exact bind，不读DerivedRecap active/control/current route config；Started Refuse在client creation前零write，explicit restart只读frozen bytes。

## No-Go

- `ReadLatestCompleteView`无selected path API；
- 每列latest Cell临时拼row；
- pure read触发rebuild/cleanup/reset；
- ContextComposer缓存mutable active config；
- exact missing/damaged静默退到旧view/raw全量。
- adapter自己拼raw tail或改写Prepared codec/request recipe/snapshot hash。

## Done when

- pure-read dependency graph与head fences闭合；
- focused context/branch/corruption tests green；
- builds/docs/diff green；
- reviewer确认WP-06只需实现row-batch executor，不改变Getter/Store wire。

## Handoff to WP-06

交付exact frozen input/materialization contracts及mystery fixture的主线Context断言。

## Implementation and review record

- 新增独立`SessionJournal.RecapGrid.Getter`：`RecapGridContextFactory.Open(SessionJournalReadView)`内部只打开并持有
  owner-bound Timeline/Control Reader；Store Reader仅在“nonempty Timeline + active recipe”首次resolve时thread-safe lazy-open，随后由同一
  Getter lifetime持有到Dispose。empty Timeline或no-active的raw-only路径不打开、也不要求Store存在/healthy。
- select从current whole `TimelineHeadRef`与current whole `ControlHeadRef`独立构造exact `FulfilledViewKey`，只读current fulfillment；
  `NthPrevious`严格同时沿selected Timeline `PreviousRowId`和RowView `PreviousViewDigest`双链前进，不扫描latest、ordinal或旧fulfillment。
  compact handle使用strict versioned re-encode（上限512）并提交process-local opaque owner nonce；selection token提交canonical
  repo/Ref、whole T/C heads、StoreIdentity、recipe、current fulfilled/view与selected row/view。direct selection与neutral descriptor都只能由
  产生它的同一Getter lifetime/materializer消费；materialize先用同一Timeline Reader `ValidateWitness`复验original witness，再复验
  frozen T/C/raw fence，并以同一Store ReaderHandle读取exact view/cells。
- neutral adapter由同一Getter handle实现`ICoherentContextCandidateSource`与`ISessionContextLifecycleCoordinator`。phase2 contract已收口为
  `Materialized|Stale|Busy|Disposed|Invalid`；SessionJournal继续拥有selected anchor之后的raw-tail fold与Prepared request recipe。
  mature no-active与empty Timeline + active均返回`RawHistoryAuthorized/EmptyLineage`；nonempty active但current fulfillment missing严格
  `Unfulfilled`，不降级raw或旧cache。Timeline/Control/Store `UnsupportedSchema`在direct与neutral phase2都显式归为typed
  `Invalid(component,version)`，不得落入default `Stale`。
- `MembershipComplete`、`PriorInputAligned`、`FullRebuildChain`为独立bounded diagnostics。predecessor proof同时复验selected Timeline row、
  recipe/descriptor/view与prior projection；三者共享明确rows/cells/canonical UTF-8 bytes read-work budget并复用已materialize的首row证据，
  row/cell budget在对应predecessor authority/artifact lookup前扣账，因此cap耗尽后零额外未计数lookup；所有实际读取（含越过byte cap的
  单个bounded probe）都进入计数。预算不足、missing/Busy/Invalid只报告`Incomplete`，不把diagnostic提升为read gate。
- Control activation新增本地V1 composability policy：target `(Carrier,BlockKey)`必须唯一，definition max content不得超过256KiB。
  Control不引用SessionJournal neutral symbol；cross-project contract test锁本地policy常量与neutral carrier cap等值。Getter仍对实际definition、
  view/member/cell/content/hash做独立fail-closed复验，包含绕过activation的canonical forged-state fixture。
- Prepared与Started restart继续完全使用frozen request bytes；throwing lifecycle/select/materialize collaborators的调用数均为零。旧v8 source、
  Planner、Galatea、CLI production composition均未切换；本候选只做neutral接口的机械适配。
- 两路initial independent review均为P0=0；统一tail补齐owner nonce/witness、terminal fence、shared provenance budget、
  Nth>=2、rewind/sibling、Dispose drain与neutral UnsupportedSchema mapping后，两路closure review均为GO（P0=0，P1=0）。
- final serial evidence：Getter 21/21、Getter external public 2/2、Control composability 3/3、SessionJournal phase2 mapping与
  Prepared/Started recovery合计3/3、Walking architecture 16/16；`Atelia.sln` build 0 warning / 0 error、scoped docs checker 15/0，
  `git diff --check`除既有`Atelia.sln` line-ending提示外clean。containing commit提供commit evidence；本包未切换current production。
