# DerivedRecap Grid WP-05：Getter 与 ContextComposer

状态：Planned；依赖 WP-04 fulfilled-view semantics

只需加载：目标设计、总计划、WP-04 handoff、本文和 WP-06 摘要。

## Intent

建立主线推理的唯一pure-read边界：按selected raw Ref、TimelineHead和active GridBuildRecipe解析exact fulfilled RowView，
再与该row之后的raw tail组合。不得逐列拼latest Cell或让GridReader读取Timeline。

## In scope

- `ResolveFulfilledView(selectedRawRef, completionBoundary, timelineHead, activeRecipe, nthPrevious)`；其中
  `completionBoundary`就是neutral request冻结的exact raw-head fence，不另立同义authority；
- membership complete、PriorInputAligned、可选FullRebuildChain provenance；
- exact Cell内容materialization与ordered contributions；
- Timeline `OpenSegment`/raw tail的独立composition；
- candidate/partial/absent/invalid/stale/off-lineage typed results；
- head fence before/after materialization；
- neutral SessionJournal context-candidate adapter；
- bounded read-only diagnostics。
- strict `DerivedContext.NthPrevious`：沿exact Timeline predecessor chain选择第n个sealed row，再解析同一active recipe fulfillment；
- selection handle提交Ref/Timeline/head generation+row/recipe/target/view/completion boundary并在materialize时全量复验。

## Ownership

- GridReader只读取Grid artifacts/fulfilled refs；
- TimelineReader只读取row descriptors与selection witness；
- select/materialize两个phase各读一次ControlPlane snapshot；materialize要求handle-bound active recipe digest仍exact相等；
- Grid candidate adapter只返回exact row contributions、anchor setups与completion boundary；SessionJournal core继续独立fold raw tail，
  ContextComposer不得接管Prepared/raw request reconstruction；
- SessionJournal core只依赖neutral context candidate contract，不依赖concrete Grid projects。

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
13. overlay bootstrap的membership-complete mixed view可读；`PriorInputAligned`/`FullRebuildChain`只是provenance，不是read gate；
14. 无active nonempty recipe时显式`RawHistoryAuthorized/EmptyLineage`，不以Timeline是否已有sealed row为条件；active recipe
    partial/unfulfilled/Invalid绝不raw fallback；
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
