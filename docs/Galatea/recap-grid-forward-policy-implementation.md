# RecapGrid forward-policy 实施验收记录

状态：**进行中；不得据此安排真实实例升级。**

本记录只陈述当前工作树的已验证局部，不替代
[设计](recap-grid-forward-policy-refactor-plan.md)或
[工作单](recap-grid-forward-policy-work-order.md)的完整验收合同。

## 已实现并验证

- Full recipe canonical V2 可记录 `OriginRootRecipeDigest`，V1 canonical
  bytes/digest 仍按原语义解码。
- Store V5 fresh schema 含 `row_work`/`row_work_member`；RowWork 在
  `(Ref, Timeline, root, HistoryRow)` 下 first-winner 持久化，cell 可按
  `WorkId + LogicalColumnId` 定位。work 的写入早于 provider dispatch。
- Manager 的零调用 progress 路径不创建 RowWork；可调用的 build 在
  dispatch 前重新进入并持久化选择。
- Getter 对有 RowWork 的 row 使用其 `ProducerTarget` 和 exact prior
  校验；V4 旧 row 仍按其存储 recipe target 读取。Runtime prior rendering
  使用实际 definition 的 header，而不是当前 work 的 header。
- Galatea read/admission 不再把 active root target 与当前 code-owned
  default target 不同不再构成读取或恢复门禁；新工作才使用当前
  maintenance policy。
- Galatea config V12 使用单一 maintenance connection/concurrency/attempt
  timeout 和可为空的 `historicalAgentControlProfileFiles`。fresh bootstrap
  只使用 code-owned bundle；历史 profile 仅供 frozen tool exact recovery。
  Host 对实际 work route key 延迟构造 exact route，并在一个 runtime 内用
  global lane 限制全部 maintenance route 的并发，避免按 work 放大预算。
- `Galatea.Server operator upgrade-recap-grid-config-v12` 已具备 V11 的
  provider-free dry-run/apply：机械候选唯一时保留连接/预算；候选歧义时
  不写入并要求 `--maintenance-route-index`；apply 先保留 V11 backup 并
  reopen 验证 V12。此工具未对真实实例运行。
- `control compose-full-recipe` 输出 Full V2；当 Control 已有 active root
  时，recipe 显式记录它为 `OriginRootRecipeDigest`。
- `recap-grid upgrade-store-v5` 已接入 CLI，默认 dry-run。当前已验证的
  V4 complete-row 路径重建 RowWork、保留 instance/cell/row/content ID，apply
  先备份再替换并 strict reopen；无 row proof 的 partial cell 明确拒绝。
  该工具仅在合成仓执行过。
- 对 V4 partial，CLI 只读收集 Control/selected Timeline 的唯一
  `RowWork` 证明；Store 逐项验证 root/target/exact prior/reuse source 后才
  回填 `WorkId`。无法唯一证明 scope 或 prior 的 partial 仍明确拒绝。
- WP1 补强：live maintenance 的 bootstrap 只验证 Control 内部证据；explicit
  candidate 仍要求 bootstrap 位于当前 selected path。新 cell/row publication
  必须有已持久化的 RowWork，zero-column row 也会在发布前选择 work；纯 progress
  不创建 work。缺 cell 的 definition 校验以 RowWork 的实际 producer spec 为准。

已运行的聚焦证据：

- `SessionJournal.RecapGrid.Store.Tests`：77 passed。
- `SessionJournal.RecapGrid.Manager.Tests`：基线完整 89 passed；后续
  producer-policy 改动后重新验证 `FullMultirowBuildAndRepeatedRequestAreExact`。
- `SessionJournal.RecapGrid.Getter.Tests`：32 passed，另有
  `PersistedRowWorkProducerOverridesCurrentRootTargetForReadAndMaterialization`
  通过。
- `SessionJournal.RecapGrid.Runtime.Tests`：97 passed。
- Galatea RecapGrid composition/readiness 聚焦：28 passed。
- Galatea V12 strict config/field-language 聚焦：98 passed；V11→V12
  conversion 聚焦：2 passed；composition：24 passed；readiness：3 passed。
- CLI Full V2 / route-missing RowWork 聚焦：各 1 passed。
- Runtime global maintenance lane 跨两个 distinct exact routes 的并发回归：1
  passed（route 各自声明 8，但 host-wide lane=1 时 provider 最大并发仍为 1）。
- Manager 4,097 行 `Public4097TimelineBuildsThroughHeadOneRowAtATimeAndReopensZeroStep`
  重跑通过（1 passed，56 秒，exit 0）；新增 65,537 行 zero-column candidate→cold reopen
  回归通过（1 passed，15 分 44 秒，`maximumNewCalls=0`）。耗时是当前规模
  风险，应在后续优化中保留相同语义复测。
- Galatea partial model-switch / Prepared、LegacyStarted frozen recovery 聚焦：
  3 passed；补列事件现断言携带原 RowWork 的 WorkId，已成功 cell 不重发。
- Galatea.Server 完整套件：1288 passed、1 skipped，exit 0。V12 synthetic
  fixture 显式区分 character default connection 与 maintenance connection；
  old producer/default policy 不同不再造成 fresh admission 门禁。
- V4→V5 complete row / Overlay shared-cell / orphan partial migration 聚焦：3
  passed；Store exact external RowWork proof partial：1 passed；Store 完整：77
  passed；Store PublicSurface：5 passed。

## 尚未完成

G3 尚缺“历史未完成 family 按实际 work 路由”的完整纵向回归与全部冻结恢复
覆盖；G4 的 candidate/Overlay/promotion 仍未收口；G5 尚缺 V4 partial、Overlay
共享 cell、多 ref、crash/reopen、export/restore 的完整无损矩阵；A1--A15
完整/规模验收尚未完成。本记录不能作为服务部署、真实 `.atelia/galatea` 迁移
或 NuGet 发布的授权。
