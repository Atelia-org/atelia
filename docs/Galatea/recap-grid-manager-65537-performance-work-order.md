# RecapGrid Manager 65,537 性能重构工作单（P0 计数 + P1 read session）

状态：**Ready for implementation**（由用户另行启动实施 Goal）。2026-09-22 02:27 CST。
目标设计：[recap-grid-manager-65537-performance-candidate-design.md](recap-grid-manager-65537-performance-candidate-design.md)
（已吸收施工勘误：P0 计数器形态、哨兵覆盖边界、TryObserve 排除理由）。
实施提示词：[GOAL-recap-grid-manager-65537-performance.md](GOAL-recap-grid-manager-65537-performance.md)。

## 1. Authority map

- 用户当前授权：形成本工单与 GOAL 提示词；**未授权开始实施**。实施由用户启动 Goal 后进行。
- 设计权威：目标设计文档经用户指示的多轮辩证评审收敛（5 候选→2 候选），其语义边界为实施权威；
  本工单只做施工细化与 gate 划分。
- 仓库指令：根 AGENTS.md（.NET 重活串行 `--no-restore -m:1 -nr:false`、apply_patch 编辑、
  不用 insert_edit_into_file、保持无关 surface 不动）。
- 实施事实来源：当前源码、测试、Git state（§2）；本文与设计文档均为证据，不是运行时指令。

## 2. Outcome 与基线

**Outcome**：65,537 gate
（`ManagerVerticalTests.Public65537TimelineBuildsThroughHeadAndColdReopensWithoutProviderCalls`，
tests/SessionJournal.RecapGrid.Manager.Tests/ManagerVerticalTests.cs:789）
在全部语义断言不变的前提下耗时显著下降；每个阶段有计数或测试证据支撑。

基线事实（已核实）：

- 4,097 focused ~56s、65,537 focused ~15m44s、Manager full 97 项 ~20m38s（设计文档记录；
  G0 起以当次实测为准）。
- RecapGrid Store 每读写入口 fresh open：`OpenVerifiedConnection`
  （prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs:1470）。
- discovery 回溯每行 = 1 次 Timeline `ReadSelectedRow` fresh open
  （prototypes/SessionJournal.HistoryTimeline/SqliteHistoryTimelineLedger.cs:702）
  + 1 次 Store `ReadViewAt` fresh open
  （prototypes/SessionJournal.RecapGrid/Manager/ManagerProgression.cs:105）。
- `InspectBuildProgress` 也调用 `DiscoverProgression`
  （prototypes/SessionJournal.RecapGrid/Manager/ManagerProgress.cs:46），启用点 1 同时惠及两条路径。
- Timeline 是独立 Store 与验证机制，**不在本工单范围**。

## 3. 实施不变量（详见目标设计 §3/§4）

- session = 连接复用，非事务复用（全部读 autocommit）。
- 写路径零改动：`PutRowWork` / `PutRowView` / `PutCell` / `TryObserve*` 不进 session、不改语义。
- session open 时全套验证并缓存 identity；每读前 `ReadIdentity` 比对；
  mismatch → `LatchInvalid("GridStoreInstanceIdMismatch")` latch 全 Store
  （与现有 `IsStoreFailure` latch 行为同构）。
- Manager 不存 session 字段；生命周期严格限定在 `DiscoverProgression` 方法体内。
- P0 计数 per-instance（每 handle 独立 Store 实例，无跨线程共享）、无 delegate hook、无 Stopwatch。

## 4. Scope boundary

In scope：§5 gate 文件清单内的代码、测试、断言与文档增补。

Non-goals（不得进入）：

- Timeline read session（独立设计增补；G3 只记录证据）。
- 设计 §4.1 已证伪删除的原 P3/P4。
- pooling、`busy_timeout`、WAL/`synchronous`、跨行批量写、RowWork/RowView 事务合并。
- Getter / Runtime / Hosting 等其他消费者改造。

允许：每 gate 独立本地 commit（不 push）；新增/更新测试断言；更新 public surface 期望
（`RecapGridBuildMetrics` 加字段属预期 public 变化，项目未发布）。
需另行授权：push、真实实例迁移、ignored live state、任何 destructive 操作。

## 5. Dependency-ordered gates

### G0：P0 连接计数

- 关闭问题：连接成本能否按 Store discovery / Store build / Timeline / 写路径归因？
- 实施：
  - `RecapGridBuildMetrics`（prototypes/SessionJournal.RecapGrid/Manager/ManagerContracts.cs:259）
    追加带默认值 0 的 `StoreConnectionOpens`、`StoreDiscoveryConnectionOpens`。
  - `SqliteRecapGridStore` 加 `internal int ConnectionOpens`，在 `OpenVerifiedConnection`
    唯一收口自增；`RecapGridStoreHandle` internal 透传
    （prototypes/SessionJournal.RecapGrid/Store/StoreContracts.cs:700、StoreRuntime.cs:125）。
  - `BuildState` 在 `RunWavefrontAsync` 边界与 `DiscoverProgression` 前后做 delta 快照
    （prototypes/SessionJournal.RecapGrid/Manager/ManagerWavefront.cs:24-31、ManagerAuthority.cs:39 组装）。
  - Timeline 打开数用现有 `SelectedRows` 近似（每行恰一次 `ReadSelectedRow` + 冻结期常量），
    不改 HistoryTimeline 接口。
- 验证：4,097 filter 通过；计数出现在 `result.Metrics`；确定性基线值记录进断言与提交信息。
  public surface 期望若因新字段变化，同步更新。
- Commit：`recapgrid: add store connection-open counters (P0)`

### G1：read session 机制

- 关闭问题：operation-scoped verified read session 机制是否正确且边界完整？
- 实施：
  - 新文件 prototypes/SessionJournal.RecapGrid/Store/RecapGridStoreReadSession.cs：
    internal sealed session（typed open result：Opened/Busy/Invalid/Disposed）；
    open 复用全套验证并缓存 identity（把 `OpenVerifiedConnection` 拆 Core 返回 identity；
    `ValidateSchemaIdentity` 改为返回其末尾已读出的 identity，现有 fresh-open 调用点零改动）。
  - `RecapGridStoreReader.OpenSession()`（prototypes/SessionJournal.RecapGrid/Store/StoreRuntime.cs:290）。
  - 每读前 `ReadIdentity`（SqliteRecapGridStore.cs:1723）比对，mismatch latch 全 Store；
    错误映射复用 Reader 现有模板（StoreRuntime.cs:408-424 形态）。
  - 读面首版仅 `ReadViewAt`；`ReadRowWork` 等其他读待 G4 gate 再加入。
  - 每读必过 `_lifetime.TryEnter`；`Dispose` 幂等、只关连接不碰 lifetime/lease。
- 测试：tests/SessionJournal.RecapGrid.Store.Tests/StoreReadSessionTests.cs（新）：
  T1 dispose 幂等+后续 `Disposed`；T2 第二连接 in-place 篡改 `store_metadata` →
  `Invalid("GridStoreInstanceIdMismatch")` 且同 handle fresh open 亦 `Invalid`、新 handle 正常；
  T3 idle session 不阻塞并发 writer；T4 `handle.Dispose` 排空 in-flight session 读；
  T5 latch 传播；T6 session 读与 fresh-open 读结果一致。
- 验证：Store.Tests 全绿 + Store.PublicSurface.Tests（internal 零 surface 变化）+ Manager 4,097。
- Commit：`recapgrid: add operation-scoped verified read session`

### G2：启用点 1（DiscoverProgression）

- 实施：`DiscoverProgression`（prototypes/SessionJournal.RecapGrid/Manager/ManagerProgression.cs:24）
  方法体顶部 `using` open session；:105 `ReadViewAt` 改走 session；open 失败映射现有
  `Unavailable(Store, ...)` 形态（参考 :117-130 Busy 分支）。`HydrateAssignedView` 不改签名
  （anchor 的 `ReadCell` 留 fresh open，O(#anchors) 可忽略）。session 不越出方法体。
- 验证：4,097 A/B：`StoreDiscoveryConnectionOpens` 降至 ~1；全部既有断言绿；
  Inspect 路径由现有 progress 测试覆盖。
- Commit：`recapgrid: use read session in DiscoverProgression (P1 enablement 1)`

### G3：证据裁决（gate-owned）

- 关闭问题：启用点 2 是否实施？
- 判据（建议值，以真实计数定案）：剔除 Timeline（≈`SelectedRows`）后，剩余 build 期 Store
  打开数 ≥ G2 后总打开数的 20%，或 4,097 wall-time 改善 ≥ 10% → 实施 G4；否则按设计 §6 停止。
- 允许结论：proceed / stop / Timeline 主导需独立设计增补（只记录，不做）。
- 结果记录：设计文档增补段或提交信息。
- Commit：`recapgrid: record P1 A/B results and gate decision`

### G4：启用点 2（P0-gated，仅在 G3 proceed 时）

- 读点测绘（包 C 已核实，含 file:line）：可进 session 白名单 = overlay base 读
  （ManagerProgression.cs:304）、overlay base 校验 `ReadRowWork`（ManagerRowBuild.cs:275）、
  resume 判别器 `ReadRowWork`（ManagerRowBuild.cs:378）、`ResolveSelectedCells` `TryReadCell`
  （ManagerRowBuild.cs:669）、`FindMissingAssignments`（ManagerWavefront.cs:187、ManagerProgress.cs:104，
  注意其 Store 内部单连接遍历形态需逐点小设计）、Inspect `ReadFulfilled`（ManagerProgress.cs:206）。
  永不进：CommitIndeterminate 观察读（ManagerRowBuild.cs:581、ManagerSettlement.cs:40/:163）。
- 逐点接入、逐点 commit、每点后跑 4,097；session 读面按白名单逐步扩展。
- 收尾：65,537 回归（耗时下降、cold reopen 零新调用不变）；
  Commit：`recapgrid: 65537 regression after read session`

## 6. Global completion contract

- 全量 Manager 套件跑通一次（约 97 项）；Store.Tests、Store.PublicSurface.Tests、
  Manager.PublicSurface.Tests 通过。
- cold reopen 零新调用断言不变；预算不放宽、规模不缩、失败边界不删。
- 写路径 diff 零触碰（§3 不变量）。
- 每次提交前 `git diff --check` + staged diff 审查；只提交本 Goal 引入的改动，
  起始已存在的未提交文档改动（设计文档、本工单、GOAL 文件）不得混入代码 commit。
- 停止边界：G3 判 stop 且结论已记录，或 G4 收尾验证完成。

## 7. Pause / escalation conditions

- 计数显示连接生命周期非主导（设计 §6 停止条件）→ 停止并报告，不静默改设计。
- Timeline 打开占比反超 → 只记录证据并停止；Timeline session 化需独立设计增补。
- 发现需改写路径、public 合同超出 Metrics 新字段、或任何写语义 diff → 停止上报。
- 起始 dirty 路径：docs/Galatea/recap-grid-manager-65537-performance-candidate-design.md
  （未提交，含上一轮评审修订与本轮勘误）、本工单、GOAL 文件。
