# DerivedRecap Grid Rewrite 总施工计划

状态：Implementation program active；WP-00、WP-01A、WP-01B、WP-01C、WP-02 complete，WP-03 Ready；current production尚未切换

目标设计：[`derived-recap-grid-target-design.md`](derived-recap-grid-target-design.md)

## 1. Intent

以全新、可分阶段验收的实现替换 current complete-roster epoch DerivedRecap。施工采用“旁路建设、最终一次切换”：

- Git 保存旧实现，不在主干长期注释调用点；
- 第一层 production owner 是 `HistoryTimeline`；
- content-addressed definitions/recipes/cells 与 minimal active CAS 分离；
- 每个工作包保持 buildable、reviewable、可单独提交；
- 最终 cutover 同一提交切 composition root 并删除旧 implementation/tests/config/docs；
- 不建设 v8/v9 compatibility reader、migration layer 或第二状态机。

本文只维护全局依赖、包边界和 gate。具体实施会话只加载目标设计、本文、当前 WP、前一包 handoff 与后一包摘要，
避免一次装入全部施工文档。

## 2. Planning baseline and freshness

本计划起草基线为 `9274fec7`。WP-00 fresh inventory已经把实际cut-start锁为
`5e1ba46eb84f784a6fa481829a0cabc14b73781f`：该提交包含目标设计与施工计划，但没有Grid production实现。
production call sites、durable paths 与 test inventory由
[`DRGRID-CUTOVER migration ledger`](derived-recap-grid-migration-ledger.md)记录；本文的路径仍只作导航。

旧实现通过 Git branch/tag 冻结，不复制到新的 source archive：

```text
archive/derived-recap-pre-grid       exact cut-start baseline
feature/derived-recap-grid-rewrite   green incremental integration branch
```

WP-00采用上述exact名称，并把两个ref都建立在`5e1ba46e`。必须保持“一个冻结 baseline + 一个集成分支”。并行 writer 使用独立 worktree；同一
文件树只允许一个 writer，其他 agent 只读 review。

## 3. Global architecture rules

1. raw events + selected `RefId` Parent lineage 是 History 正文 authority。
2. Timeline ledger 是已经封口 row 边界、长度、predecessor 与 selected head 的 authority；不保存 History 正文。
3. MaintainerControlPlane 是 definitions、GridBuildRecipes 与 active recipe CAS 的唯一逻辑 authority；只选一个物理 carrier。
4. RecapGridStore 只保存 immutable cells/views 与可重建 fulfillment/index；whole-store corruption 只 reset/rebuild。
5. production project graph从`SessionJournal <- HistoryTimeline <- RecapGrid.Abstractions`开始：Timeline独占
   `TimelineId/HistoryRowId/HistorySegmentDescriptorDigest`，Grid不得复制第二套identity。`HistoryTimeline`不直接引用Maintainer、Grid、
   Completion runtime/provider或Galatea；允许消费SessionJournal暴露的provider-neutral history-message contract。
6. Cell semantic identity 只提交 Maintainer 实际可见输入；runtime connection/model/lane/cache 不进入 identity。
7. Completion call 不持有 durable transaction；commit 只发生在成功结果返回后。
8. Prepared/Started frozen completion recovery不打开active Timeline/Grid/ControlPlane，也不读取DerivedRecap active/current route
   config；Prepared仍按frozen completion identity从Host registry exact bind，Started Refuse先于client creation。
9. old/new production paths 不并存为长期 feature flag；WP-07A/07B candidate 只用于隔离验收，WP-08 一次 direct cut。
10. unknown schema、hash mismatch、wrong lineage/head、duplicate/unknown fields 全部 typed fail closed。
11. Timeline有四种分离transaction：immutable policy put不改head；partition-policy CAS不追加row；row append原子插row并保留
    whole expected head的active policy；selected-path reconcile不插row、不切policy，只在owner-bound raw fence与whole expected head
    同时匹配后回指共同ancestor/empty。empty policy CAS或reconcile都保持row/raw fence为null。

## 4. Work-package graph

```text
WP-00 Baseline + migration ledger + walking skeleton [complete]
  |
WP-01A Timeline contracts/partition [complete]
  |
WP-01B Timeline raw integration [complete]
  |
WP-01C Timeline durable ledger [complete]
  |
WP-02 Content-addressed contracts + MaintainerControlPlane [complete]
  |
WP-03 SQLite RecapGridStore
  |
WP-04 Grid build engine + MaintainerManager + fake runtime
  |
WP-05 Getter + ContextComposer
  |
WP-06 Completion runtime/family/prefix integration
  |
WP-07A CLI/operator vertical candidate
  |
WP-07B Galatea/online vertical candidate
  |
WP-08 Atomic production cutover + legacy deletion
```

WP-02已完成formal contracts、单一canonical Control carrier、public factory/Reader、maintenance、crash/settlement与
independent review/final serial validation；WP-03已Ready。该旁路owner不改变current production；production cutover仍只属于WP-08。

工作包文档：

1. [`WP-00`](derived-recap-grid-wp00-baseline-and-walking-skeleton.md)
2. [`WP-01 overview`](derived-recap-grid-wp01-history-timeline-ledger.md)；
   [`01A`](derived-recap-grid-wp01a-timeline-contracts-and-partition.md) ->
   [`01B`](derived-recap-grid-wp01b-timeline-raw-integration.md) ->
   [`01C`](derived-recap-grid-wp01c-timeline-durable-ledger.md)
3. [`WP-02`](derived-recap-grid-wp02-content-addressed-control-plane.md)
4. [`WP-03`](derived-recap-grid-wp03-sqlite-grid-store.md)
5. [`WP-04`](derived-recap-grid-wp04-build-engine-and-manager.md)
6. [`WP-05`](derived-recap-grid-wp05-getter-and-context-composer.md)
7. [`WP-06`](derived-recap-grid-wp06-completion-runtime.md)
8. [`WP-07A`](derived-recap-grid-wp07a-cli-operator-vertical.md) ->
   [`WP-07B`](derived-recap-grid-wp07b-galatea-online-vertical.md)
9. [`WP-08`](derived-recap-grid-wp08-atomic-cutover.md)

任何 WP 若证明后一包前提不成立，必须先更新目标设计、本文依赖图和受影响的相邻 WP；不得在实现里静默变更语义。

## 5. Per-package loop

每包固定执行：

1. **Fresh re-review**：重新盘点 exact HEAD、owners、callers、tests、dirty files 与已完成 handoff。
2. **Plan lock**：锁定单一目标、In/Out scope、write scope、最小验证、No-Go 条件与独立 commit。
3. **Implement**：一个 writer；必要时内部用 `A0 contract seam -> A1 durable/behavior cut`，但包末不得留半代 public surface。
4. **Independent review**：至少一条只读 contract/robustness review；高风险 finding 回到当前 writer 尾修。
5. **Validation**：focused tests、affected builds、docs checker、diff check；并行测试结果不能替代最终串行 gate。
6. **Handoff**：记录 commit、exact tests、残余风险、下一包成立的前提以及是否需要调整后续计划。

第一次 wait 超时不是失败；只要 agent 有合理进展且关键路径未阻塞，继续主线程非重叠工作并耐心等待。

## 6. Migration ledger instead of commented callers

WP-00 建立单一 migration ledger，至少包含：

```text
Legacy owner/symbol/path
Production callers
Behavior/invariant worth preserving
Replacement WP/owner
Disposition: Preserve | Move | Rewrite | Delete | Retarget | Keep-connected-until-WP08
Status and proof
```

只在 ledger 使用统一 `DRGRID-CUTOVER` 标识。最终 architecture boundary tests 扫描禁止 symbol/path；不把大量调用点
注释掉，也不使用长期 `[Obsolete]` warnings 模拟迁移状态。

旧 tests 分三类：

- raw/lineage/recovery authority tests：留在SessionJournal owner；HistoryLoad pure tests迁到Timeline owner，并新增Timeline
  consumer/branch tests；
- v8/v9 layout/repair-specific：冻结分支保留，WP-08 删除；
- user-visible/vertical scenarios：用新 Grid 语义重写。

## 7. Validation tiers

### Per-WP

- new/affected focused tests；
- affected project builds `0 warning / 0 error`；
- `git diff --check`；
- `python3 scripts/check_session_journal_docs.py`；
- P0/P1 review closure。

### Milestones

- WP-01：Timeline 独立 crash/branch/B-change gate；
- WP-03：SQLite child-process crash/contention/query-plan/CLI gate；
- WP-04：mystery-analysis、overlay/full/A-B、reuse/Keep gate；
- WP-07A：disposable CLI/operator candidate；
- WP-07B：disposable Galatea + CLI second-host online candidate；
- WP-08：solution build、affected full suites、fresh checkout direct-cut gate。

Live provider canary 只证明环境/provider行为；credential/network failure必须诚实标为 environment-blocked，不能阻塞本地
semantic correctness，也不能虚报 cache 命中。

## 8. Global No-Go conditions

以下任一出现必须暂停当前包并回看目标设计：

- Timeline 开始依赖 Maintainer/Grid；
- ControlPlane 与 Grid DB 同时成为 active definition/recipe authority；
- 同一个 semantic input 出现两套 EvaluationKey 算法；
- 为恢复进度引入 Pending/Running/Settlement durable campaign 状态机；
- SQLite 与 JSON 同时保存 live artifact authority；
- old/new runtime 通过长期 feature flag 并行；
- cutover 需要 tolerant legacy decoder、silent migration、auto-reset 或 full-raw fallback；
- 为通过 LOC gate 删除 hash、bounds、lineage proof、strict codec、fsync/crash safety。

## 9. Program done when

- Galatea 主线只使用 Timeline/Grid/ControlPlane/Manager/Composer 新链；
- Agent 可声明式创建专题 Maintainer，完成 column overlay 或 full-grid analysis；
- content-equivalent inputs exact reuse，changed inputs 产生新 cell 或 Maintainer `KeepUnchanged`；
- Prepared/Started recovery 仍自足且不触碰 active Grid；
- `DerivedContext.NthPrevious`沿exact Timeline chain保持strict ordinal且不跳损坏slot；
- current docs/CLI/config 只描述新架构；
- old DerivedRecap Store/Planner/Maintainers/Runtime owner、wire、paths、tests 与 composition production 命中为零；
- old derived roots对新runtime保持inert并由独立offline exact-confirm procedure归档/删除；new Grid有明确reset/rebuild，
  不存在compatibility layer；
- migration ledger 全部关闭，WP 文档转 archive，current architecture map 指向新 owners。
