# RecapGrid Manager 65,537 规模性能候选设计

状态：**Candidate design / 未实施。** 2026-09-22 01:08:45 CST。

本文只描述候选改进方向，不表示已经优化，也不改变当前 65,537 规模测试的语义或预算。
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

## 2. 当前热点

以下结论来自静态代码路径，仍需 profile 证实：

1. `DiscoverProgression` 会沿 selected path 逐行调用 `_store.Reader.ReadViewAt(...)`。
   入口见 [ManagerProgression.cs](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerProgression.cs:24)。
2. `SqliteRecapGridStore` 的每个读写入口都可能调用 `OpenVerifiedConnection()`。
   该方法每次都新建 `SqliteConnection`，设置并回验一组 PRAGMA，再检查 schema identity 与 counters。
   入口见 [SqliteRecapGridStore.cs](../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs:1470)。
3. Manager 每个新 row 至少涉及：
   - `ReadRowWork` / `PutRowWork`：[ManagerRowBuild.cs](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerRowBuild.cs:353)。
   - `PutRowView`：[ManagerWavefront.cs](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerWavefront.cs:489)。
4. `PutRowWork` 内部又会先读取同 key 的 existing RowWork，再决定 insert、already present 或 conflict。
   入口见 [SqliteRecapGridStore.cs](../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs:381)。

因此，同一 row 的构建路径可能重复打开并验证多个 SQLite connection。
在 65,537 行规模下，固定开销会被放大成主要成本。

## 3. 候选设计

### P0：先加分阶段观测，再优化

在实施任何行为改动前，先加入可测试的 phase timing 或诊断计数，至少覆盖：

- fixture 构建
- `DiscoverProgression`
- connection open / PRAGMA
- `ReadRowWork`
- `PutRowWork`
- `PutRowView`
- final fulfilled / cold reopen

目标不是引入通用 profiler，而是让 4,097 与 65,537 的耗时可以按阶段拆开。
如果观测证明主要成本在别处，后续优化顺序应重新评估。

### P1：`DiscoverProgression` 使用 bounded read session

为逐行 `ReadViewAt` 复用一个已经过同一套验证的只读 connection。

设计边界：

- 不改变写事务、fsync、first-winner 或崩溃恢复语义。
- 只限制在 `DiscoverProgression` 的读取阶段。
- session 结束后必须释放 connection。
- 不得把该只读 session 泄漏到 Manager 的写路径。

这是风险最低的候选切口，适合作为第一步实施。

### P2：Manager build 使用 Store read session

把 Manager build 过程中的读操作改为复用同一个已验证连接。
写操作仍保持 `PutRowWork` / `PutRowView` 各自独立事务。

设计边界：

- 保留 RowWork first-winner。
- 保留逐行恢复语义。
- Store lifetime、Busy、Disposed、Invalid 的映射必须继续返回原 typed result。
- 不允许为了复用连接而吞掉 schema identity、counters 或 PRAGMA 校验错误。

### P3：`ReadRowWork` + `PutRowWork` 合并为 get-or-insert

当前 `PutRowWork` 会先读 existing，再决定插入。
可以把 Manager 的“先读，缺失再写”合并成一次事务内的 get-or-insert。

设计边界：

- 仍必须在 provider 调用之前持久化 RowWork。
- `SelectionConflict`、`AlreadyPresent`、`CommitIndeterminate` 的语义不能被折叠。
- 不能把竞争窗口变成静默覆盖。

### P4：zero-column / no-evaluate 快速返回

对 zero-column 或 no-evaluate assignment，可以在完成必要校验后直接返回 Complete，避免打开数据库。

设计边界：

- 只影响明确不需要评估的路径。
- 不能跳过 RowWork、timeline scope 或 Store identity 校验。
- 对有 provider 调用的路径没有收益。

## 4. 不建议的方向

以下方向在当前合同下不建议先做：

- 修改 WAL 或降低 `synchronous`。
- 启用通用 connection pooling。
- 跨行批量插入 RowWork。
- 合并 RowWork 与 RowView 事务。
- 改变 first-winner 或 pre-dispatch durability 语义。

这些选项可能改变持久化或崩溃恢复语义。除非后续设计明确允许，否则不应作为本轮优化方案。

## 5. 验证策略

1. 先在 4,097 行上做 A/B，确认每个候选方向的收益。
2. 再对最终选中的方向跑一次 65,537 规模回归。
3. 保留现有测试断言，不放宽预算、不缩小规模、不删除失败边界。
4. 验证 cold reopen 后已完成的 row 不产生新调用。
5. 每个优化步骤都单独提交，便于回滚和审计。

验收标准：

- 语义不变：RowWork、first-winner、provider 调用前持久化、cell 按 `WorkId` 定位。
- 65,537 测试仍通过，且耗时显著下降。
- 4,097 与 65,537 的差距可由分阶段计时解释。
- 不新增隐式连接池或全局状态。

## 6. 推荐实施顺序

1. P0：分阶段计时。
2. P1：`DiscoverProgression` bounded read session。
3. P2：Manager build Store read session。
4. P3：`ReadRowWork` + `PutRowWork` get-or-insert。
5. P4：zero-column / no-evaluate 快速返回。

如果 P0 证明主要成本不在连接生命周期，则在 P1 之前重新评估方案。
