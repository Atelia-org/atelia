# RecapGrid forward-policy 实施验收记录

状态：**G1--G6 已实现，A1--A15 本地验收完成。不得据此安排真实实例升级。**

本记录只陈述当前工作树的已验证局部，不替代
[设计](recap-grid-forward-policy-refactor-plan.md)或
[工作单](recap-grid-forward-policy-work-order.md)的完整验收合同。

## 已实现并验证

- G5 完成显式、provider-free V4→V5 工具链。CLI 默认 dry-run；apply 先创建并
  fsync exact-name V4 backup，再从该 durable snapshot 构造 strict-verified V5
  temporary。replace 前同时重验 backup/active 的 identity、counters 与
  SHA-256+length witness；replace 后 directory fsync 并 full verify。健康 V5
  重跑返回 `AlreadyCurrent`。对应提交：`2fb46dc9`、`eb0ec13f`、`7931a8fc`、
  `29336e20`。
- G5 complete/partial proof：V4 complete row 重建实际 RowWork；partial 通过
  exact Control/Timeline scope 求得唯一 actual producer、exact prior 与
  assignments，再由 Store 逐项验证。Full、Overlay shared cell、active/inactive
  candidate、historical timeline 与 multi-ref 均保留旧 IDs/内容/producer；orphan
  或无法证明的 partial typed 拒绝且零 mutation。CLI fixtures 同时断言 raw
  Journal、Timeline/Cadence/Control bytes 不变。对应提交：`6f3cc091`、
  `a23d832d`、`3cfd8ee5`、`71daa988`、`0ef9a70e`、`6f577b0d`。
- G5 backup/restore：新增 `prepare-restore-store-v4` 与
  `restore-store-v4`。prepare strict-verify active V5 与 exact V4 backup；restore
  要求 active/backup 双 physical witness，replace 前再次重验两侧。成功返回
  `Restored`，相同 V4 重放返回 `AlreadyRestored`；恢复后保持 V4，不隐式向前
  迁移，operator 必须显式再 upgrade。对应提交：`b7de173e`、`7ad721e6`。
- G5 durability：upgrade full/partial 的 10 个、restore full/partial 的 8 个
  真实子进程 `Environment.FailFast` phase case 已覆盖。pre-replace 只允许原 V4，
  post-replace 只允许严格 V4/V5；恢复 crash 后还能显式再 upgrade。post-replace
  无法确认 settlement 时返回 `CommitIndeterminate`，携带 evidence 与唯一 inspect
  next action，不自动 retry/restore。对应提交：`1881e67b`、`2e7d95bc`。
- G5 verify/export：full verify 与 export 都闭合检查 RowWork canonical/physical
  projection、actual producer、exact prior、Overlay reuse、cell/row WorkId；export
  新增 `row-work` item 与 typed V2 cursor，保留旧三种 V2 cursor。257 个 zero-cell
  work 分页和 4,097 个 actual work/cell/row drain 已覆盖。对应提交：`3f8efe1a`、
  `8d680417`。正式模型见
  [Store V5 合同](../SessionJournal/current/contracts/recap-grid-store-sqlite-v5.md)。

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
- Galatea.Server 当时的阶段完整套件：1288 passed、1 skipped，exit 0。V12 synthetic
  fixture 显式区分 character default connection 与 maintenance connection；
  old producer/default policy 不同不再造成 fresh admission 门禁。
- G5 CLI exact-scope 迁移组：6 passed（active/inactive partial、historical
  Timeline、Overlay bootstrap/post-bootstrap、orphan、multi-ref），multi-ref 独立
  复验：1 passed；结果见 `recap-grid-v4-upgrade-group.trx` 与
  `recap-grid-v4-multiref.trx`。
- G5 Store 完整 Release：136 passed，包含 upgrade/restore 18 个 cold-process
  fail-fast case、双 witness、`AlreadyCurrent`、RowWork verify/export/cursor 以及
  4,097 actual work rows；`review-g5c4-store-full.trx`。4,097 聚焦复验另为
  1 passed；`review-g5c4-4097.trx`。最终 Store PublicSurface 计数见下方 G6 总验收。

G5 已知残余（不改变已实现合同）：

- dry-run 正常返回时 active authority bytes 不变，但会在 Store 同目录构造、验证并
  清理 temporary；进程 crash 可能留下 residue，需要人工 inspect。
- export 的 cell phase 在极端多列下可能重复解码同一 RowWork，是诊断性能风险。
- restore pre-replace temporary 删除失败目前只有有限 P2 可诊断性，operator 仍须检查
  同目录 sidecar/residue。

## G6 最终验收

本节记录 2026-09-17 forward-policy 收尾时的本地验收；其尾修证据对应 `f4abd4a1`
与 `82f0c820`。当时 Completion restore 使用 `eng/NuGet.Completion.Local.config`
和 `UseCompletionSources=false`，不表示今天的依赖切换规则已改变。
最终 restore 均显式使用 `eng/NuGet.Completion.Local.config`、
`UseCompletionSources=false` 与 `-m:1 -nr:false`；后续测试均使用 Release、
`--no-restore -m:1 -nr:false`。26 个项目的 `TestResults/g6-final.trx` 合计
2397 passed、1 skipped、0 failed。唯一 skip 是显式 opt-in 的
`GalateaCodexDelegationLiveTests.DurableV5_EnsureStartInspectCompletesInCleanRepo`，
本轮不要求 live provider。

最终完整计数：

- Abstractions 34；AgentControl 33 + PublicSurface 1；Cadence 29 + 2；
  Control 95 + 5；Getter 38 + 3；Hosting 39 + 7。
- Manager 97 + PublicSurface 3；Online 34 + 3；Runtime 97 + 4；
  Store 136 + 10；WalkingSkeleton 27。
- HistoryTimeline 207 + PublicSurface 9；CLI 178；Galatea.RecapGrid 9 + 1；
  Galatea.Server 1296 passed + 1 skipped。
- Manager full 用时 20 分 38 秒，包含
  `Public4097TimelineBuildsThroughHeadOneRowAtATimeAndReopensZeroStep` 与
  `Public65537TimelineBuildsThroughHeadAndColdReopensWithoutProviderCalls`；
  HistoryTimeline full 用时 3 分 37 秒，包含
  `V2MutableSelectedPathCommitsAndVerifies65537Rows`。

2026-09-21 在后续 Completion/Diagnostics 依赖与 Galatea 调用面更新后的当前
HEAD `2123d3c8` 上，本机处于 gitignored local feed 模式
（`eng/CompletionDependency.Local.props` 指向唯一 dev 包）。针对这些后续提交的
影响面补跑：`Galatea.RecapGrid.Tests` 9/9 passed；`Galatea.Server.Tests`
1296 passed、1 个显式 live-provider skip、0 failed。结果分别为两个项目的
`TestResults/g6-current-head.trx`。这不是重复 2026-09-17 的 26 项目全量门禁，
而是当前 HEAD 的受影响面复验；规模与迁移合同仍以上方原始 G6 证据为准。

### A1--A15 审计

| 项 | 最终证据与闭环 |
|:--|:--|
| A1 | `CompletedP0ReadinessAndMaterializationIgnoreCurrentP1`：P0 ready、read 时 0 route/call、Store bytes 与 Control head 不变；P1 bundle 未预注册，避免 fixture 掩盖升级路径。 |
| A2 | 同一 Host 垂直片只为 H1 生成 P1 两列，exact prior 指向 R0；P0 WorkId、row/cell IDs、内容和 producer 不变。fresh maintenance 先用窄权限幂等注册 code-owned family/definitions，`ActiveRecipeDigest` 仍为 P0 root。 |
| A3 | `ExistingRowWorkKeepsItsProducerBeforeNextRowSelectsNewPolicy`：0-cell P1 work 冷开后仍完成 P1，下一未选行才用 P2。 |
| A4 | `ModelSwitchAfterPartialFailureBuildsOnlyMissingCell`：A 成功/B 失败后只补 B，不重发 A；WorkId 与 producer 不变。 |
| A5 | `ConflictingRowWorkCannotPublishOrFillNewCell`：不同 prior 得到不同 WorkId/slot，同 key first-winner 不可重绑。 |
| A6 | `CurrentReserveAndNthPreviousUseEachRowsFrozenProducer` 及 Ref/Evaluate/Reuse 负例；新增 `V5SelectedRowWithoutPersistedWorkFailsClosed`、`V5SelectedRowWithoutPhysicalWorkLinkFailsClosed`，Getter 不再回退 root recipe target。 |
| A7 | `LiveActiveReselectsOriginalWorkAfterBootstrapDetachAndSiblingBuild`：Undo 早于 bootstrap、sibling 分叉、回选原后缀均命中原 work/view，0 新调用。 |
| A8 | candidate failure/resume 与 promotion 前旧 root 保持；`PromotionLostResponseReopensAsAlreadyActiveWithoutRepublish` 和 CLI reopen 证明不明 CAS 后先读再收敛。 |
| A9 | `PrefixPromotionLeavesNewRootTailAsItsOwnPolicyDebt` 与 CLI prefix promotion：只采用 proof 前缀，新 root 尾债不拼接旧尾部。 |
| A10 | Full V2 `OriginRootRecipeDigest`、Overlay actual reuse 拒绝反例（0 calls）及 RowWork export 的逐行 actual producer 均已覆盖。 |
| A11 | character/prompt digest、旧 family 补列、跨 exact route global lane 均有回归；旧 P0 仓未预注册 P1 时，Host 在首次 fresh work 前注册当前 code-owned bundle，read/frozen 阶段不写 Control。 |
| A12 | Prepared/LegacyStarted 保持 recap=0、routeLoads=0、Control 不注册 P1；tool continuation 只在 frozen tools durable settle 后注册 default bundle，再进入 maintenance，旧 runtime/profile identity 不变。 |
| A13 | V4 full/Overlay/active+candidate partial/multi-ref/orphan、18 个 upgrade/restore fail-fast phase、AlreadyCurrent/AlreadyRestored、verify/export 全部 provider-free；旧 IDs/内容/producer 与 raw Journal/Timeline/Cadence/Control bytes 按合同保留。 |
| A14 | `SameAdoptedRootKeepsPolicyChangesRowLocalAndDiscoveryIncremental` 证明 P0→P1→P2 同 root、Control recipe count=1、selected-row 扫描量递减；4,097/65,537 规模门禁均通过。 |
| A15 | V11→V12 保留连接/预算，歧义 dry-run 零写；`MissingCreateIfMissing_EmptyHistoricalProfilesBootstrapCodeOwnedBundleWithoutProvider` 证明空历史 profile 的新仓 bootstrap。 |

总审计尾修还关闭了两处此前被 fixture 掩盖的边界：Getter 对缺失 RowWork/物理
work link fail closed；Host default policy 携带与 target 严格一致且不含 recipe 的
code-owned bundle，只用 `RegisterFamily|RegisterDefinition` 权限，注册前尊重取消，
`CommitIndeterminate` 后 exact reopen，不能把注册误当 adoption。对应提交
`f4abd4a1`，tool/default bundle 共存断言为 `82f0c820`。

### 残余与停止边界

- G5 已知的 dry-run/crash temporary residue、极端多列 export 重复解码、restore
  pre-replace temporary 删除失败诊断限制仍保留；不影响本轮合同，但属于后续维护项。
- Host default-bundle direct Put 的 `CommitIndeterminate` 已实现 exact reopen；本轮没有
  为该内部 fault window 新增专用 fault-hook。A8 的 CLI lost-response、A10 的
  mixed producer/Full origin、A14 的策略计数/大规模分别由组合证据闭合，未再造一个
  巨型单测。
- Galatea.Server 构建仍报告既有 nullable 与一条 xUnit2031 analyzer warning；测试
  结果为 0 failed。规模测试耗时仍是明确的性能观察项，未通过放宽生产预算规避。
- 本轮只操作源码、测试、合成仓/测试临时目录、正式迁移工具与文档；未访问或修改
  `prototypes/Galatea/.atelia/galatea`，未执行真实实例迁移、服务部署、push 或 NuGet
  发布。真实实例升级仍须单独授权，并从已文档化的 inspect/backup/upgrade/restore
  operator 入口开始。

## 真实实例 Store V5 迁移记录（2026-09-23，Asia/Singapore）

在 Galatea root V12 与独立 Codex Home 切换后的首次真实生成中，`gpt` 回合在进入
模型调用前以 `recap-grid-maintenance-unavailable` 失败。停服后的正式只读
`recap-grid progress` 将原因收敛为 Store dependency schema 4；Timeline 与 Cadence
均可正常读取。`cyber` 的 Store 也仍是 schema V4。此前仅验证 host `/login` 的部署
烟测没有覆盖这个派生库版本门禁。

迁移前已将两个完整 session 备份到
`prototypes/Galatea/.atelia/galatea-recap-store-v5-backup-20260922T231148Z/`。
两次 `upgrade-store-v5` dry-run 分别确认 `gpt` 的 8 row views / 16 cells 与
`cyber` 的 6 row views / 12 cells 可迁移；随后对两个停服 repository 执行 `--apply`。
工具各自保留 exact V4 backup，并将 active Store 原位升级为 V5。迁移没有构造
provider client，也没有调用模型。

迁移后两个 Store 的 `verify` 均为 `healthy`、schemaVersion 5，`progress` 均为
`complete`，重复 upgrade 均为 `already-current`。真实 host 冷启动、登录和两个
`recent-turns` 请求均成功，`cyber` readiness 为 `exact/ready`，`gpt` 为
`exact/fulfillment-missing`；后者表示失败回合推进 Timeline 后仍有可在线结算的
fulfillment，不再是 schema unavailable。host 随后正常退出，未执行新的生成。

后续部署检查不应以 `/login` 成功代替 session 可用性证明。涉及 RecapGrid schema
硬切时，应在停服副本或真实停服 repository 上先执行 `verify` 与 provider-free
`progress`，再启动 host 并读取每个角色的 exact readiness。
