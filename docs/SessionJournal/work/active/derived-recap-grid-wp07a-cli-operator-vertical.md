# DerivedRecap Grid WP-07A：CLI 与 Operator Vertical Candidate

状态：Complete；两路独立closure GO；依赖 WP-01..06 complete。

> 历史说明：本文锁定的是当时的Route manifest V1（含`max-output`）。current product已在post-R2
> hard-cut为[Route manifest V2](../../current/contracts/recap-grid-route-manifest-v2.md)：route删除该字段，
> Completion request与connection均有意不暴露caller output cap；具体adapter只使用不限量或模型最大值语义。

只需加载：目标设计、Master、WP-06 handoff、本文与WP-07B摘要。

WP-06已完成implementation、两路independent closure与final serial validation，因此本包现为Ready。后续composition必须消费
`RecapCompletionRouteKey(FamilyDigest, RuntimeProtocolId, SemanticModelId?)`与deferred exact resolver；`null`不可fallback，
不得预造client/lane/logger或新增第二scheduler。CLI diagnostics只展示bounded operational route/call状态，不把provider、cache、usage或
call-log identity写入Grid artifacts。

## Intent

用disposable repository把Timeline/Control/Grid/Manager/Composer串成可操作但不替换production default的CLI vertical；先锁
read-only/no-secret diagnostics与各durability domain的destructive authority。

## Locked implementation shape

- 新薄product `SessionJournal.RecapGrid.Hosting`只直接引用`RecapGrid.Runtime`与`Completion`，不拥有scheduler、
  backend或Grid算法。`RecapGridRouteManifest`用严格canonical V1 JSON提交exact
  `(FamilyDigest, RuntimeProtocolId, SemanticModelId?) -> connectionId/lane/timeout/max-output`；semantic model即使为
  `null`也必须显式出现，三元key不允许wildcard/default/fallback；
- Hosting-owned Completion connections V1 reader在任何Manager/Store/provider打开前完成whole-file、count、field strict UTF-8 bounds及
  duplicate/unknown/BOM/invalid UTF-8/UTF-16检查，保留全部正式connection字段并defensive freeze；candidate build不再走旧unbounded
  `LoadFile`入口；
- Hosting resolver只用`CompletionConnectionRegistry.TryGet`与`GetClient`，首次真实work之前不创建client/lane或materialize operational
  evidence queue。Runtime route借用registry-owned client；关闭顺序固定为先drain/dispose Runtime，再由幂等async registry best-effort
  逐个释放distinct client并在全部释放后rethrow/aggregate；cleanup只聚合non-fatal，`OutOfMemoryException`/`StackOverflowException`/
  `AccessViolationException`与Runtime同taxonomy立即透传。call evidence按field/event/retained-total UTF-8 bytes三层限界且只在CLI report中输出；
- Manager新增pure-read `InspectBuildProgress`：内部打开并固定同一Timeline/Control/Store authority，复用正式recipe closure与row plan
  derivation；不`OpenSelectedSegment`、不写Store、不调用executor/provider，只返回bounded first incomplete frontier、Complete或closed typed failure；
- `HistoryTimelinePathCursor`增加严格bounded public codec，仅用于CLI continuation round-trip，不暴露ledger或backend。

## Candidate command surface

已有stable store-only surface保持不变：

```text
recap-grid inspect|verify --input <repo>
recap-grid export --input <repo> [--after <cursor>] [--include-content]
recap-grid reset --prepare --input <repo>
recap-grid reset --input <repo> --confirm-length <bytes> --confirm-sha256 <sha256>
```

新增surface全部位于明确candidate子树：

```text
recap-grid candidate init
recap-grid candidate timeline create|sync|inspect|verify|export|backup|restore|abandon
recap-grid candidate control create|inspect|verify|export|put-family|put-definition|put-recipe|activate|promote|backup|restore|reinitialize
recap-grid candidate build|progress|materialize
```

所有branch命令都从`SessionJournalEngine.OpenReadOnly(repo, branch)`取得canonical repository/Ref/read view；per-Ref mutation要求
exact `--confirm-ref`。`init`按Timeline -> Control -> Store显式分步创建并输出typed阶段结果；其他命令不隐式auto-create。
Timeline初始policy字段全部显式。Control admission是独立strict bounded V1输入，权限/Family/Capability/carrier/logical prefix/budget
均须显式，不能从Family/Definition/Recipe payload自授权；三种payload只经正式`DecodeCanonical`进入Control。

## Executable vertical

- `timeline sync`先走owner-bound online reconcile/plan/rematerialize/commit；遇`OfflineBootstrapRequired`后才建立一次性、content-free、
  code-capped audited forward snapshot，完成offline reconcile/build并在operation结束释放，不扫描orphan或持久化campaign；
- `build`是唯一读取strict bounded route/connections并构造Hosting Runtime的命令，且在这些输入、`--confirm-ref`与budget/request全部
  preflight完成后才打开Manager；candidate build永不activate；`progress`与所有init/diagnostic/
  maintenance/materialize路径在Completion factory之前返回；
- `control promote`在同一进程pure-read调用`InspectBuildProgress(ExplicitCandidate(recipe))`；只有fresh
  `Complete + FulfillmentPresent + exact RecapGridPromotableProof`才立即CAS，过程不调用Build、不写Store、也不构造provider；
  CAS原样使用proof whole Timeline/Control heads。partial/stale/missing均不activate，proof不序列化；
- `materialize`只调用Getter strict NthPrevious；raw-only仅报告`raw-history-authorized`，不在CLI复制raw-tail reducer；
- 输出是bounded `atelia.session-journal.recap-grid-candidate-cli.v1` JSON envelope；syntax/confirmation为exit 1，closed operational failure
  为exit 2，success/idempotent为exit 0。Busy/Stale/Unsupported/Indeterminate不自动retry；indeterminate保留intended/observed与inspect提示。

## In scope

- `timeline create/sync/inspect/export/verify/backup/restore/abandon`；
- `recap-control inspect/export`、register Family/Definition/Recipe、explicit activate CAS；
- `recap-control verify/backup/restore/reinitialize`直接映射WP-02 typed library actions；normal Control无普通reset，corrupt current不能在线
  restore/reinitialize，必须走offline exact archive/delete+Create；
- `recap-grid inspect/export/verify/reset`；
- build recipe/candidate fulfill/full rebuild、pure-read progress与pure-read promotion commands；
- materialization inspect的strict `--nth-previous`；
- bounded progress：active recipe、Timeline head、fulfilled-through、missing assignments；HistoryLoad仅表示Timeline cadence；
- current CLI自写parser的strict command/confirmation/error mapping。

WP-01C已经提供typed library actions：`OpenReader/Inspect/Verify/Backup/Restore/Abandon`。本包只做System.CommandLine/文本
映射与operator confirmation，不重开backend选择、codec、path discovery或transaction；CLI normal discovery只用canonical locator，
不能扫描orphan/backup/latest。Restore要求current canonical schema/head仍可读且与manifest exact；head/schema不可读时只允许
explicit Abandon，不把restore降格成reset。

WP-03已经交付stable store-only顶层`recap-grid inspect|export|verify|reset`和typed library mapping，且没有接provider；本包只在
完整operator vertical中复用/验收该surface并补Manager/Getter commands，不重写Store path、witness、cursor或error taxonomy。

## Candidate boundary

CLI入口必须是明确candidate command group，不能成为long-livedfeature flag。diagnostic命令read-only/no-create且Completion
factory throwing；destructive action要求exact store/timeline scope确认。Grid reset只删Grid，raw/Timeline/Control bytes exact不变；
Timeline abandon是另一条高权操作，不能借Grid reset触发。
Control V1同样为Linux-only；CLI只映射typed platform/schema/busy/stale结果，不增加弱durability fallback或扫描backup/temp/latest。
Control `CommitIndeterminate`/backup `PublishIndeterminate`必须显示intended与observed scope/head并要求重新inspect，不得自动重试写操作。

## Acceptance matrix

1. absent/existing inspect前后filesystem snapshot一致，正文默认隐藏；V1使用WP-03 code-owned page/error bounds，不宣称未实现的
   `--limit/--max-errors`；
2. Agent/developer注册definition、overlay/full/A-B recipe，build与activate分离；
3. same recipe restart只调用missing cells；partial candidate不出现在materialize；
4. strict nth=0/1/N、branch sibling、missing/damaged slot不跳邻居；
5. wrong confirmation、wrongRef/scope、unauthorized family、over budget零mutation；
6. closed-store Grid reset crash old-or-empty-valid，raw/Timeline/Control exact不变；
7. Timeline restore/abandon独立演练；
8. provider failure保留successful Cells但不commitpartial RowView；retry只补missing；
9. Getter诊断必须显示两条raw-only规则且证明零Store open：(a) no-active时不论Timeline empty/nonempty；(b) Timeline empty时即使
   recipe active也raw-only，允许raw增长与首row seal而不形成fulfillment先决死锁。Timeline nonempty + active unfulfilled/Invalid必须
   fail closed，不得raw/旧cache fallback。
10. exact/null route没有fallback，Hosting lazy construction与Runtime-before-registry dispose有external public surface证据；
11. online与bounded offline sync、new Ref new TimelineId、candidate build不activate、pure-read promotion、build后provider-free progress及
    promotion后strict materialize通过同一真实CLI fixture；
12. stable store-only`recap-grid`与旧`recap`命令仍走原dispatch，candidate不得改变Galatea/current production composition。
13. offline audit在code-owned event cap的cap-1/exact边界分别typed limit/success；raw-head drift不retry；malformed connections在Manager、
    Store、provider之前失败且repository bytes exact不变；
14. Timeline backup/restore/abandon、Control backup/restore/reinitialize与Grid reset均以真实authority files逐域比较，证明每个命令只改变
    自己的durability domain；shared lifetime lease映射稳定`busy`而不自动retry。

## No-Go

- diagnostic创建Store或加载provider/secret；
- CLI把Control/Grid/raw事实merge成“latest”；
- reset跨durability domain；
- candidate command变成新旧runtime长期switch。

## Done when

disposable CLI E2E、crash/confirmation/read-only gates、build/docs/diff和independent review green。

## Implementation candidate record

- product：`SessionJournal.RecapGrid.Hosting`；CLI candidate分片位于`RecapGridCandidate*.cs`；Manager新增
  `InspectBuildProgress`；Timeline只增加opaque path cursor public codec；
- closure-tail serial evidence：Hosting 16/16、Hosting external public surface 1/1、Completion registry lifetime 6/6、Manager 60/60、
  Timeline cursor focused 1/1、Walking architecture/source 20/20；
- Hosting真实in-flight integration让一个正式Runtime batch进入blocking Completion client，再并发`DisposeAsync`；放行前Host未完成且client
  dispose count为0，batch settled后Host才完成并释放client exact-once，后续sync/async重复dispose仍幂等。registry sync/async fatal
  cleanup回归证明fatal立即透传且不包装为Aggregate/non-fatal尾结算；
- CLI真实fixture 8/8：explicit init+online sync+no-provider diagnostics/raw materialize、wrong Ref/duplicate/unknown option零derived mutation、
  bounded offline audit cap-1/exact与raw-head drift、formal Family/Definition/Recipe + WP-06 Runtime provider build + provider-free progress +
  pure-read promotion + strict materialize、fork Ref独立Timeline/no fallback、malformed connections全repo零mutation、六种Timeline/Control
  maintenance与Grid reset真实四域byte isolation/Busy；
- stable store-only + old recap/history-load targeted regression 7/7。此前pre-tail CLI full为72/73；唯一失败是本包未改的
  `CompletionTargetIdentityFactoryTests.Create_PreservesWireFingerprintsAndExcludesSecrets`旧golden（expected `209495...`、actual
  `f4172...`），对应product/test source对HEAD均无本包diff，因此不把它误报为candidate通过，也不在本包修改unrelated identity；
- `Atelia.sln`已登记Hosting product/tests/public surface；closure tail后的root final solution build为0 warning / 0 error。scoped docs checker
  为15/0、`git diff --check`仅报告共享worktree `Atelia.sln`既有LF->CRLF提示且无error；两路independent closure最终均为
  GO（P0=0，P1=0）。WP-07A现为Complete，但不表示production cutover。

## Handoff to WP-07B

交付strict candidate command surface、operator evidence、pure-read progress与Hosting exact-route composition owner。WP-07B的Galatea与CLI
`run-online-turn`composition必须复用Hosting，不得另建route fallback或scheduler；Host lifecycle仍须把
`Timeline reconcile/seal -> Manager fulfill -> Getter readiness`组成composite coordinator。Getter handle直接只注册为candidate source，
Host只注册composite为lifecycle。关闭顺序仍为Runtime drain -> registry clients。

WP-07B施工裁决把Agent-facing Control tool、code-owned built-in genesis与ToolResult/ToolContinuation明确拆到WP-07C；本包的canonical
operator provision fixture只证明显式provision可执行，不代表Agent tool或normal Host auto-create已经完成。
