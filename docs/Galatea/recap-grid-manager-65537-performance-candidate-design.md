# RecapGrid Manager 65,537 规模性能候选设计

状态：**P0、P1 两个启用点已实施；Timeline 有界分页已实施。** 当前验证与取舍见
[后续实施记录](recap-grid-manager-65537-performance-follow-up.md)。§7 的 G3 stop
与“写事务持久化主导”归因已被后续分阶段测量推翻，保留为历史记录，不再指导实施。
初稿 2026-09-22 01:08:45 CST；同日经三方独立
dialectical review（demand skeptic / minimal architect / semantic defender）交叉质询后收敛修订。

§1–6 保留原始候选设计；实际实施状态与证据以上述后续记录为准。
目标是把已观察到的耗时风险拆成可独立验证的小步，并明确哪些优化不允许以性能为名越过持久化合同。

## 1. 背景与现状

当前规模门禁是
[ManagerVerticalTests.Public65537TimelineBuildsThroughHeadAndColdReopensWithoutProviderCalls](../../tests/SessionJournal.RecapGrid.Manager.Tests/ManagerVerticalTests.cs:789)。
该测试使用 65,537 行、zero-column、`maximumNewCalls=0`，并在完成后冷重开一次以证明已完成的 row 不再产生新调用。

已有正式证据：

- 4,097 行 focused 回归：1 passed，约 56 秒。
- 65,537 行 focused 回归：1 passed，约 15 分 44 秒。
- Manager full：97 passed，20 分 38 秒，包含 4,097 与 65,537 两个规模测试。

这不是 provider 成本问题：测试中的 `executor.Batches` 为空，也没有 live provider 调用。
耗时集中在 Manager/Store 的本地持久化与 orchestration 常数上。

### 1.1 产品节奏边界

本设计优化的是 **gate 测试与 CLI 显式 budget 的批处理路径**，不是产品稳态延迟：

- Galatea Online/Host catch-up 是 Grid-first、每 pass 至多 seal 一行 Timeline row、publish
  一行 recipe-row；稳态下 `DiscoverProgression` 只需读到近期锚点，per-pass 成本有界。
- 65,537 全量 build 只出现在规模门禁测试与 CLI 显式 budget 的冷启动 catch-up。
- 因此收益方是 CI gate 时长与一次性 CLI catch-up；不应据此为产品 per-pass 延迟引入新机制。

## 2. 当前热点

以下结论来自静态代码路径，仍需 P0 计数与 profile 证实：

1. `DiscoverProgression` 会沿 selected path 逐行调用 `_store.Reader.ReadViewAt(...)`。
   入口见 [ManagerProgression.cs](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerProgression.cs:24)。
2. `SqliteRecapGridStore` 的每个读写入口都可能调用 `OpenVerifiedConnection()`。
   该方法每次都新建 `SqliteConnection`（`Pooling=false`），设置并回验一组 PRAGMA，
   再检查 schema identity 与 counters。
   入口见 [SqliteRecapGridStore.cs](../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs:1470)。
3. Manager 每个新 row 至少涉及：
   - `ReadRowWork` / `PutRowWork`：[ManagerRowBuild.cs](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerRowBuild.cs:353)。
   - `PutRowView`：[ManagerWavefront.cs](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerWavefront.cs:489)。
4. `PutRowWork` 内部又会先读取同 key 的 existing RowWork，再决定 insert、already present 或 conflict。
   入口见 [SqliteRecapGridStore.cs](../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs:381)。

因此，同一 row 的构建路径可能重复打开并验证多个 SQLite connection。
在 65,537 行规模下，固定开销会被放大成主要成本。

## 3. 候选设计

经评审收敛，候选从 5 个收敛为 2 个：**P0 最小观测**与 **P1 唯一的 read session 机制**。
原 P3（get-or-insert 合并）与原 P4（zero-column 快速返回）经源码证伪后删除，理由见 §4.1。

### P0：最小 in-code 观测计数

不做 7 阶段 phase-timing 框架。最小形态：

- 在现有 `RecapGridBuildMetrics`
  （[ManagerContracts.cs:259](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerContracts.cs:259)）
  上扩展 2-3 个 int 计数字段（如 `ConnectionOpens` 与 discovery/build 分组计数）。
- Store 侧在 `OpenVerifiedConnection` 这唯一收口点加 per-instance int 计数器
  （直接自增；不用 delegate hook、不用 Stopwatch，避免计时污染与并行测试串扰）。
- 沿用 per-instance 注入模型（`ManagerTestHooks` / Store hooks），禁止 static/共享计数器：
  避免 65,537 行规模下计时开销污染被测数据，以及并行测试互相污染计数。
- 外部 profiler（dotnet-trace 等）不是第一选择，仅当计数无法解释 4,097 与 65,537 差距时作深挖手段。

理由：验证策略要求跨 commit 的可重复 A/B；一次性外部 profile 无法低成本支撑多步 A/B，
而 2-3 个计数字段 + 单点 hook 是更小的机制。

### P1：operation-scoped verified read session（唯一行为改动候选）

**一个机制、两个启用点，不建两种 session：**

- 机制：Store 内新增一个 operation-scoped 只读 session。open 时执行一次现有
  `OpenVerifiedConnection` 全套验证（PRAGMA 回验 + schema shape + counters +
  缓存 `store_instance_id`）；session 内的读操作复用该连接；dispose 时释放。
- 启用点 1（无条件）：`DiscoverProgression` 的逐行 `ReadViewAt`。
- 启用点 2（P0-gated）：Manager build 其余读路径（`ReadRowWork` 预读、overlay base
  校验读等），仅当 P0 归因显示 build 期读连接开销仍占显著份额时启用。
  每个启用点独立 A/B、独立提交。

两层验证设计（保持 fail-closed）：

- **open 一次**：全套 PRAGMA 回验、schema shape 全对比、counters 非负检查、缓存 instance_id。
  - PRAGMA 是 session 自持连接的状态，读路径不会改动它，open 时回验一次即足够。
  - counters 的真实一致性由写路径在事务内原子维护；读 session 内仅省略重复的
    非负 sanity 检查，无实质风险。
- **每次读前**：执行单行 `ReadIdentity`
  （SELECT `schema_version` + `store_instance_id`，
  [SqliteRecapGridStore.cs:1723](../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs:1723)），
  并与 open 时缓存的 identity **比对**。哨兵的覆盖边界：同文件 in-protocol 的
  identity/schema 篡改与漂移（SQLite change-counter 使缓存页失效，比对必可见）；
  协议内 Reset 换库已被 exclusive lease 阻断；inode 级换库不在哨兵覆盖内
  （旧连接继续读自洽旧数据）。lease、哨兵、session pass 内一致性三者互补。
- **写路径完全不变**：仍每入口 fresh open + 全套验证。

设计边界（一份清单，不按启用点分写）：

- session 是**连接复用，不是事务复用**：每次读独立 autocommit，不得跨读持有 `BEGIN`。
  否则 DELETE journal 下长读事务会把并发 writer 的 COMMIT 变成 `Busy`，
  破坏 first-winner 的显式失败语义。
- `TryObserveCell` / `TryObserveRowViewAt` / `TryObserveFulfilled`
  （CommitIndeterminate 观察路径）**不得使用 session**，必须继续 fresh open。
  观察只依赖观察时点的全新验证，不与 build 读 session 共享生命周期；
  这是零重构成本约束（三者现为 Store private 方法），防止未来重构时被合并进 session。
- Manager 写路径（`PutRowWork` / `PutRowView` / `PutCell`）不进入 session。
- RowWork first-winner 与逐行恢复语义不变。
- Store lifetime、Busy、Disposed、Invalid 的 typed result 映射不变。
- session 结束后必须释放连接；不得泄漏到 operation 之外。

## 4. 不建议的方向

### 4.1 评审证伪后删除的候选

- **原 P3（`ReadRowWork` + `PutRowWork` 合并为 get-or-insert）：delete。**
  - `PutRowWork` 内部已是 `BEGIN IMMEDIATE` + read-existing + insert-or-conflict
    的闭合 get-or-insert（[SqliteRecapGridStore.cs:381](../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs:381)）。
  - Manager 的前置 `ReadRowWork` 不是冗余 presence 探测，而是 resume 判别器：
    其内容驱动 `DeriveAssignments` 的 derive-from-existing / derive-from-plan 分支
    与 `ProducerPolicyRequired` 判定；work 已存在时 Manager 根本不调 `PutRowWork`。
  - progression frontier 的锚是 RowView 而非 RowWork，无法证明 work missing；
    RowWork-without-RowView 崩溃窗口内的行若跳过预读，会把 resume 快路径的廉价读
    变成 `BEGIN IMMEDIATE` 写锁竞争。
  - 预读的连接开销由 P1 session 覆盖，本候选不留独立段落。
- **原 P4（zero-column / no-evaluate 快速返回）：delete。**
  - 文本自相矛盾："避免打开数据库"与"不能跳过 RowWork 校验"不可同时成立——
    zero-column 行也必须 `PutRowWork` + `PutRowView` 才能支撑冷重开零重建断言。
  - zero-column 只是测试 fixture 形态：真实 Galatea recipe target 恒非空
    （provisioner 强制非空 definition closure），CLI 构造点亦非空；
    no-evaluate assignment 无真实消费者。
  - 重启触发条件：未来出现真实 no-evaluate recipe 形态且其持久化需求被显式重新定义时再议。

### 4.2 持久化合同保护边界（不得以性能为名越过）

- 修改 WAL 或降低 `synchronous`。
- 启用通用 connection pooling。`Pooling=false` 保证"连接关闭"即"锁释放 + 下次全新验证"；
  池化会让 fresh-open 的 PRAGMA 回验与 identity 哨兵整体失效，Busy 后归还池中的连接状态也不再可信。
- 跨行批量插入 RowWork。
- 合并 RowWork 与 RowView 事务。
- 改变 first-winner 或 pre-dispatch durability 语义。
- 调高 `busy_timeout`。`busy_timeout=0` 让所有锁竞争立即以 typed `Busy` 返回、
  由 Manager 显式决策；任何等待都会把显式失败信号变成隐式时序依赖。

## 5. 验证策略

1. 先在 4,097 行上做 A/B，用 P0 计数确认每个启用点的收益。
2. 启用点 2 仅在 P0 归因支持时实施；若 discovery 已占绝对主导，则停止。
3. 对最终选中的方向跑一次 65,537 规模回归。
4. 保留现有测试断言，不放宽预算、不缩小规模、不删除失败边界。
5. 验证 cold reopen 后已完成的 row 不产生新调用。
6. 每个优化步骤单独提交，便于回滚和审计。

验收标准：

- 语义不变：RowWork、first-winner、provider 调用前持久化、cell 按 `WorkId` 定位。
- 65,537 测试仍通过，且耗时显著下降。
- 4,097 与 65,537 的差距可由 P0 计数解释。
- 不新增隐式连接池或全局状态。

## 6. 推荐实施顺序

1. P0：最小观测计数（Metrics 字段 + 单点 hook）。
2. P1 启用点 1：`DiscoverProgression` 接入 read session；4,097 A/B。
3. 用 P0 计数重新归因：若 build 期读连接开销仍显著，实施启用点 2；否则停止。
4. 65,537 规模回归收尾。

如果 P0 证明主要成本不在连接生命周期，则重新评估整个 P1 方向。

## 7. 实测结果与 G3 裁决（2026-09-22）

P0 与 P1 启用点 1 已实施并通过验证（commits `7457d7c8`、`8a0e2f1b`、`54545d82`、`c9be1dad`）。
4,097 垂直测试实测（同一测试、同机同会话 A/B）：

| 段 | StoreConnectionOpens | StoreDiscoveryConnectionOpens |
|---|---|---|
| bootstrap（4,096 行，G0 → G2） | 28,673 → 24,578 | 4,096 → 1 |
| head（1 行，G0 → G2） | 9 → 8 | 2 → 1 |
| cached reopen（G0 → G2） | 2 → 2 | 1 → 1 |

剩余 24,578 次 open ≈ 6 次/行 = 4 读（`ReadRowWork`×2 +
`FindMissingAssignments`×2，全部在启用点 2 白名单）+ 2 写
（`PutRowWork` + `PutRowView`）。

决定性 wall-time 证据：G0 后与 G2 后各跑一次 4,097，Duration 均为 1m11s——
消除 4,095 次连接 open 对耗时无可测影响，单次 open 成本上界 <约 0.5ms。

**G3 裁决：stop，不实施启用点 2。** 依据：

1. 连接生命周期不是 65,537 gate 的主导成本。启用点 2 全量接入白名单的
   收益上界 ≈ 26.2 万次 open × ≤0.5ms ≈ ≤131s（≤14% gate 耗时），
   按每 open 0.1-0.4ms 的现实估计约 3-8%。
2. 本设计验收标准"65,537 耗时显著下降"在当前持久化合同内不可达：
   主导成本在写事务持久化（`synchronous=EXTRA` + DELETE journal，
   每行 `PutRowWork` + `PutRowView` 两个独立写事务）与测试 fixture 构建，
   前者属 §4.2 保护边界，后者在 build 路径之外。
3. 工作单 §7"计数降≠耗时降"停止条件已触发（计数降 4,095，耗时不动）。

P0 计数与 P1 机制保留：计数器持续提供归因证据；read session 机制
（含 identity 哨兵与测试矩阵）已落地，未来若写路径策略变化可自然扩展启用面。

后续真实杠杆（均需单独设计决策，本轮不做）：

- 写路径持久化策略（WAL、`synchronous`、RowWork/RowView 事务合并、批量化）——
  §4.2 保护边界内的合同变更。
- Timeline `ReadSelectedRow` 逐行 fresh open（约 65k 次/gate，独立 Store 与验证机制）。
- 测试 fixture 构建耗时（测试路径，非 build 路径）。

### 7.1 65,537 gate 环境性对照（2026-09-22 04:45）

最终验证中 65,537 gate 在当前机器条件下返回 `BudgetExceeded`
（测试内固定 `maximumElapsed=10min` build 预算）。对照实验：checkout 到
改动前基线 `c916128b`，在相同条件下跑同一 focused 测试，同样
`BudgetExceeded`（基线 19m56s vs 当前 HEAD 20m5s，差异在噪声内）。
当前机器比 15m44s 基线记录时慢约 27%（4,097 focused：约 56s → 1m11s），
固定 10 分钟预算被环境超限，与本次重构无关；按合同不放宽预算。
两次运行均在 build 预算点中止，G2 的 65,537 实际耗时收益无法从这两次
运行直接测得，量级以 §7 上文分析为准。全量 Manager 套件其余 96/97 通过。
