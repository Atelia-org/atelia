# RecapGrid forward-policy 实施验收记录

状态：**进行中；不得据此安排真实实例升级。**

本记录只陈述当前工作树的已验证局部，不替代
[设计](recap-grid-forward-policy-refactor-plan.md)或
[工作单](recap-grid-forward-policy-work-order.md)的完整验收合同。

## 已实现并验证

- G5c1（仅 Store apply durability）：V4→V5 的 apply 先构造并以 Store
  full verifier 严格验证同目录 temporary V5；copy 出的 exact-name regular
  V4 backup 随后 `Flush(true)` 加 parent-directory fsync，并重新执行 V4
  identity/integrity/foreign-key/counter/partial-proof 校验及 SHA-256+length
  witness，才允许 replace active。replace 后再 fsync directory 并 strict
  verify V5。成功结果带 backup 与 active 的 identity、counters、physical
  witness，不再只报路径。
- G5c1：replace 成功后的 directory fsync、strict verify 或测试中断均返回
  typed `CommitIndeterminate`（保留已验证 backup evidence、best-effort
  observed active schema/identity/witness 与唯一 next action：先
  inspect/verify），不降格为 `Invalid`，也不自动 retry/restore。replace 前
  失败仍保持 V4、清理 temporary，backup 即使保留也不冒称升级成功。
  public `UpgradeV4` 没有 hook；内部 `UpgradeV4ForTest` 仅有 temporary
  verified、backup durable、replace/directory fsync、verify 五个 failpoint。
  Store crash harness 已预留 `upgrade-v4` 同名 failpoint 入口，仍只使用
  合成 stopped repository fixture，未触碰真实实例。

- G5a：HistoryTimeline 与 RecapGrid Control 增加 maintenance-only 的
  canonical exact-scope inventory / `(RefId, TimelineId)` read-only reader；
  不经 locator 的 active timeline 选择。枚举拒绝 reparse、foreign entry、
  non-canonical name 与内部 identity mismatch，并有 4096 scope bound；不写
  Journal/Timeline/Cadence/Control。合成 two-ref、同 Ref active+historical
  Timeline 验证 inventory 稳定排序、exact head/control 与 locator/control
  bytes 不变；public-surface 与 focused Release TRX 见本轮提交证据。
  no-reparse 检查是 cooperative durable-layout guard，不宣称抵抗恶意
  rename TOCTOU；未来 G5 apply 仍须 stopped repository、exclusive lock 与
  mutation 前 witness recheck。

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
- G3c：P0 partial RowWork 在 current default 切至已注册但 inactive 的不同
  P1 时，cell validation 从 `RowBuildSpec` 的 frozen producer definition
  精确解析；缺失/列不符返回 typed failure。Host 回归只补 P0 缺 cell、沿 P0
  route，WorkId/ProducerTarget 不变。completed P0 read 在 P1 与 throwing
  route loader 下零 maintenance，Control/Store 不变。Prepared、LegacyStarted
  与 ToolContinuation frozen 阶段同样零 maintenance，并保留旧 tool identity；
  其明确分界后的 fresh P1 只加载一次 P1 route、生成 P1 work，P0 active root
  及既有 P0 slots 不变。
- G3c：`GalateaConfigLoader.Load -> GalateaCompletionOwner -> BindPrepared`
  证明 profile bytes 延迟到 frozen exact bind：malformed bytes 不阻断
  config/owner construction，exact bind 才失败；canonical old profile 成功且
  zero dispatch。
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
- G4a：live 路径的新 RowWork 必须有调用方显式传入的 producer policy；没有
  policy 时 build/progress 返回 `ProducerPolicyRequired(root,row)`，并且在
  executor/raw capture/RowWork 写入之前停止。先精确读取到的既有 RowWork 永远
  优先，忽略本次 live policy；explicit candidate 的新 work 固定使用
  `recipe.Target`。冻结 closure 不再从 active root target 推断 live policy，
  definition/family closure 仅在真正选择新 work 前验证。CLI `build --live`
  接受 canonical `--producer-target` 文件，并在缺 policy 时通过 Manager
  progress 预检后、不读取 routes/connections 或构造 client 前返回
  `producer-policy-required`。

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
- G3c 聚焦 Host/Owner：6 passed（A/B/C/D）。
- G4a Manager 聚焦：`LiveNewWorkRequiresExplicitProducerPolicyBeforeDispatch`
  通过（build/progress typed required、零 executor batch/零 RowWork，提供
  policy 后完成）；Manager PublicSurface：3 passed；RecapGrid 与 CLI Release
  build 成功。尾修证明 P1 已选 work 在缺 policy 时先恢复，下一无
  work 行才要求 policy；后续 P2 work 的 exact prior 指向 P1 row result。
  CLI 缺 policy 在读 routes/connections 与创建 client 前返回，canonical
  target 成功，recipe/progress/promote 均拒绝 `--producer-target`；
  AgentControl 显式映射 typed result。非规模 ManagerVertical TRX：87 passed。
- G4b1：Overlay bootstrap 的新 work 在写入前以 exact base same-row view
  与 base RowWork 的实际 assignment 验证非重算列；已有 RowWork 则只接受其
  `ReusedCellId` 指向的 exact base member，绝不按当前 logical column 重选。
  缺失、替换或 definition/row 不符
  返回 public `OverlaySourceIncompatible`（CLI status
  `overlay-source-incompatible`），在 executor/provider 前停止，且不替换
  RowWork、不扩大预算。progress、Online/readiness 与 AgentControl 都显式映射
  此结果。Full V2 的 CLI composition 聚焦仅证明 active origin 参与 candidate
  digest 且 compose 不改 active；mixed-active completed/unpromoted 隔离留待
  G4b2 的独立 E2E。
- G4b1 聚焦 Release `--no-restore -m:1 -nr:false`：Manager frozen C0→base
  C1 replacement/cold reopen：1 passed；CLI build report + Full V2 composition：
  6 passed；AgentControl stable result code：1 passed；Galatea readiness：3 passed；
  Manager full：94 passed（含规模回归）；Manager PublicSurface：3 passed。
- G4b1 tail：new Overlay RowWork 选择前还验证 base view 的 target/prior、
  producer order 与每个 base RowWork assignment；C1 同列替换在 overlay
  RowWork 尚不存在时也 typed 拒绝并保持 missing。V5 work-addressed cell 与
  strict legacy base-slot 两种 Evaluate 形状都可作为已导入 complete row 的
  provenance；tail Manager 聚焦：2 passed。
- G4b2：H10 complete 的 A 与 Full V2 candidate B（`OriginRoot=A`）的
  H5 prefix 合成仓回归已验证：B/H5 proof 的 promotion 后 active=B，B/H6
  仍无 view；live 无 policy 在 H6 返回 `ProducerPolicyRequired`，提供 B
  policy 后 H6 exact prior=B/H5、不是 A/H10。CLI 回归实际执行
  `control promote --through-row H5`，并核对 active=B。`progress` 的 `NextWork` 现在同时报告 adopted/root
  `RecipeDigest`、该行的 `ProducerTargetDigest`，以及仅在 RowWork 已持久化时
  非空的 `WorkId`。因此 mixed root 不会把 root 冒充为 producer，也不会把
  proposed work 冒充成持久化工作；完整/fulfilled 的逐行 producer export 仍是
  G5 的范围。`control promote` 成功 JSON 明确标为
  `adoptionScope: "proof-through-row-only"` 且
  `candidateTailDebtAtProof`；它只在 proof 的 frozen Timeline head 高于
  proof through row 时为 true。采用只改变 Control.ActiveRecipeDigest，
  不表示 candidate 已覆盖 Timeline current head，也不改 raw/timeline/grid。
  `CommitIndeterminate` 的 CLI 合同继续输出稳定 `nextAction: "inspect"`
  且不输出成功字段；direct Control settlement 与 AgentControl durable
  receipt/reopen 是既有证据。CLI publish-after-fault→reopen→AlreadyActive
  的完整跨层 E2E 没有安全的局部 fault hook，仍未在本包执行；reopen 必须以当前
  Control 判断 AlreadyCurrent 或重新 proof→CAS，不能盲重放。
- Galatea.Server 完整套件：1288 passed、1 skipped，exit 0。V12 synthetic
  fixture 显式区分 character default connection 与 maintenance connection；
  old producer/default policy 不同不再造成 fresh admission 门禁。
- V4→V5 complete row / Overlay shared-cell / orphan partial migration 聚焦：3
  passed；Store exact external RowWork proof partial：1 passed；Store 完整：77
  passed；Store PublicSurface：5 passed。

## 尚未完成

G3 尚缺“历史未完成 family 按实际 work 路由”的完整纵向回归与全部冻结恢复
覆盖；G4a/G4b2 已收口显式 live producer policy、prefix promotion 与报告合同；
G5 尚缺 V4 partial、Overlay
共享 cell、多 ref、crash/reopen、export/restore 的完整无损矩阵；A1--A15
完整/规模验收尚未完成。本记录不能作为服务部署、真实 `.atelia/galatea` 迁移
或 NuGet 发布的授权。
