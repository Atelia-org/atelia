# RecapGrid Manager 65,537 性能改进后续记录

日期：2026-09-22。起点：`5aa85299`。本轮由用户授权按实测收益与复杂性推进实现。
实现提交：`bfb3e99e`（Store session / Busy 修复）、`78260fd3`（Timeline 分页）。

## 1. 修正热点归因

原候选设计 §7 用一次整测试 A/B 推断读连接优化已无收益，该推断不成立：

- `ManagerVerticalTests.NewPath` 在本机选择 `/dev/shm`，Journal、Timeline、Grid 均在 tmpfs。
  对照基线 `c916128b` 也如此，不能把磁盘 fsync 延迟视为本 gate 的主要成本。
- 重新构建 `5aa85299` 后，4,097 行诊断运行：fixture 34.468 s、build 33.248 s、
  cold build 0.0126 s。全进程 69,805 次真实 `fsync`/`fdatasync` 调用合计 0.030942 s。
- EventPipe 的 build 内 sampled inclusive 时间：Store 读连接验证约 9.398 s / 33.257 s；
  其中 `ReadRowWork` 4.934 s、`FindMissing` 4.464 s。该数值是机会量级，不是保证收益。
  Store 所有 open 合计 13.961 s；包含前述读，不可相加。事务 Commit 栈约 0.647 s。
- Timeline discovery 逐行连接及 proof 验证没有计入 Store open 计数；final fences 每行
  还有两次 Timeline snapshot 与一次 Control snapshot。没有把这些嵌套 sampled 时间当作互斥分段。
- 原临时 Debug 二进制曾落后于源码；诊断记录只采用重新构建后的数据。

因此重新启用 P1 启用点 2，随后试验已有 Timeline 分页能力。写事务、journal mode、
`synchronous=EXTRA`、`busy_timeout=0`、first-winner、pre-dispatch durability 均不变。

## 2. 实现边界与取舍

### Store build 读连接复用

扩展既有 `RecapGridStoreReadSession`，支持 `ReadRowWork` 与 `FindMissingAssignments`。
共享现有 SQL/read core；open 全验证，每读 identity 哨兵。
session 限于单次 build；已有 discovery session 保留独立生命周期。
`CommitIndeterminate` 的 fresh observation 与所有写事务不使用该 session。
已完成行的 cold build 不打开多余 build session。

**Busy 回归触发的必要设计修正：** 新增“先打开 session，再由另一连接持有 EXCLUSIVE”
测试时，发现原有 session 的 managed `ReadIdentity` 会无限等待。EventPipe 确认调用链为
`SessionRowWorkAndMissingReadsReturnBusy → ReadRowWork → ReadIdentity →
SqliteDataReader.NextResult → Thread.Sleep`。Microsoft.Data.Sqlite 10.0.10 在 native
Busy 后还有托管重试，`DefaultTimeout=0` 表示无限等待；`busy_timeout=0` 不能禁用这层重试。
fresh open 的 native PRAGMA 通常先报 Busy，过去掩盖了缓存连接上的问题。

经强模型评审，将原“每读 autocommit”细节调整为**每次同步逻辑读取独立短 snapshot**：
native `BEGIN DEFERRED`，native `SELECT ... FROM store_metadata` 获取 SHARED 锁，
然后执行 identity 哨兵和既有 managed read core，`finally` 中 native `ROLLBACK`。
首次取锁由 native API 立即报告 Busy；DELETE journal / private cache 下，持有 SHARED 后
其他连接不能取得 EXCLUSIVE，避免后续 managed query 进入无限等待。
事务绝不跨逻辑读取、行构建或 `await`；provider 调用和 Store 写入时没有该读锁。
清理识别 SQLite 自动 rollback；清理失败关闭缓存连接并 latch invalid。
不采用负 `CommandTimeout` 的未文档化技巧，也不以放宽测试或增加等待超时掩盖问题。

参考：[provider NextResult 源码](https://raw.githubusercontent.com/dotnet/efcore/v10.0.10/src/Microsoft.Data.Sqlite.Core/SqliteDataReader.cs)、
[SQLite transaction](https://www.sqlite.org/lang_transaction.html)、
[SQLite locking](https://www.sqlite.org/lockingv3.html)。

### Timeline discovery 有界分页

Reader 新增显式 `ReadSelectedPathPageStartingAt`，沿用既有分页验证与逐行 Merkle proof。
Manager 保留 Through 与第一前驱的单行锚点探测，更长 catch-up 使用 32 行页。
每消费一行仍验证 predecessor、scope、duplicate，并读取 Store anchor；metrics 只计消费行。
中间 Through 从指定 predecessor 开始，不错误地从 whole head 开始。

强模型评审接受以下有限观察行为变化：

- 预取可能越过已完成 anchor，较早暴露旧行损坏；直接 typed fail-closed，不失败回退。
- 一页持有既有短读事务，最长 32 行；保持 `busy_timeout=0`。
- 缓冲行消费期间的 Timeline drift 可延至下一页或既有 dispatch/publish/final fence 发现。
  已验证 descriptor 是 frozen exact head 下的 immutable authority；全部现有 fence 保留。

不合并 `CheckFinalFences` 的两次 Timeline snapshot：当前顺序是
`Timeline(expected) → Control → Timeline(availability) → Raw`，直接删除会改变观察时机或失败优先级。

### 测量

两个规模测试新增 xUnit 输出，分别记录 fixture、build 与 cold build 的 wall time 和 metrics。
保留 65,537 行规模、10 分钟 build budget、zero provider 与 cold-reopen 断言。
不以 fixture 时间替代 build 时间，不因减少 open 数就宣称性能提升。

## 3. 验证记录

### 功能回归

| 验证 | 结果 | 本机 TRX 文件名 |
|---|---|---|
| Store 全套（含 crash/recovery） | 150 passed，26 s | `store-build-read-session.trx` |
| Store session focused | 14 passed，1 s | `store-read-session-focused.trx` |
| Manager 除 65,537 | 100 passed，1m10s | `manager-read-reuse-small.trx` |
| Manager 65,537 focused，原 10 分钟 build budget | 1 passed，15m29s | `manager-65537-read-reuse.trx` |
| Manager public surface | 3 passed | `manager-read-reuse-public.trx` |
| Timeline public surface | 9 passed | `timeline-pagination-public.trx` |
| Timeline 分页/selected path/Busy/Stale（不含其独立 65,537 测试） | 11 passed | `timeline-pagination-contracts.trx` |

TRX 位于各测试项目的 `TestResults/`（ignored 本机产物）。首次 Store 全套因新增 Busy 测试
暴露无限重试而终止，不计为通过；修复后重新构建并完整通过。Timeline 最初筛选误选其独立
65,537 测试，十余秒后主动终止，随后缩小筛选通过；该中止不构成规模验证。

Manager 的 4,097 测试分阶段输出：fixture 30.738 s；bootstrap 4,096 行 18.299 s，
`StoreConnectionOpens=8195`；head 一行 0.0147 s，opens=5；cold 0.0078 s，opens=2。
这与独立完整 4,097 行 harness 的工作量不同，不混作相同 A/B。

### 65,537 规模验收

`78260fd3` 的 Debug 二进制，真实公共 fixture 链路，原始 10 分钟 build 预算：

| 阶段 | 耗时 | 结果 |
|---|---|---|
| fixture | 9m52.946s | 65,537 个 Timeline selected rows |
| build | **5m36.020s** | Fulfilled；65,537 steps / committed RowViews；0 new calls |
| cold build | **0.0126s** | Fulfilled；0 steps / new calls / committed RowViews |

build 的 `StoreConnectionOpens=131077`（`2 × 65537 + 3`），discovery=1；
cold opens=2，discovery=1。所有原始规模、预算、provider 与冷重开断言保留。
本轮 Manager 全部 101 项分两批通过；加 Store、两个 public-surface 与 Timeline focused，
共 274 个测试通过（不重复计算 session focused）。

相较本机此前 baseline/HEAD 均 `BudgetExceeded` 的记录，本轮恢复原 gate 通过，build
距预算有约 4m24s 余量。历史 TRX 没有可靠独立 build 计时，不据此计算精确大规模 speedup。
当前总时长主要受约 10 分钟 fixture 限制；本轮停止在这两处读优化与必要 Busy 修复，
没有继续扩展至 fixture 缓存、写事务合并、WAL 或跨行持久化改造。

### 独立 build 计时

同一临时 harness 通过反射调用真实测试 fixture/OpenManager helper 与公开 BuildAsync；
Debug、同机 tmpfs、无 profiler、串行。优化前 `5aa85299` 两次 build 为 31.437 / 31.662 s，
fixture 为 34.489 / 32.991 s；opens=24584。优化后两次 build 为 22.692 / 29.845 s，
fixture 为 33.427 / 51.002 s；opens=8197，cold 均为零 steps / calls、opens=2。
首组 fixture 速度相近的观察中，build 降约 28%；第二次环境明显变慢。
没有把两个不同环境速度的均值包装成稳定 speedup 保证，也没有将两处优化分别归因。

临时原始记录位于 `/tmp/atelia-65537-investigation/`，不是永久仓库工件。
长期复测直接使用规模测试的 xUnit 分阶段输出；这里记录数值与限制，避免依赖临时文件存活。

### 复现命令

```bash
dotnet test tests/SessionJournal.RecapGrid.Store.Tests/SessionJournal.RecapGrid.Store.Tests.csproj --no-restore -m:1 -nr:false --blame-hang-timeout 60s -- xUnit.MaxParallelThreads=4
dotnet test tests/SessionJournal.RecapGrid.Manager.Tests/SessionJournal.RecapGrid.Manager.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName!~Public65537' --logger 'console;verbosity=detailed' -- xUnit.MaxParallelThreads=4
dotnet test tests/SessionJournal.RecapGrid.Manager.Tests/SessionJournal.RecapGrid.Manager.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~Public65537TimelineBuildsThroughHeadAndColdReopensWithoutProviderCalls' --logger 'console;verbosity=detailed' -- xUnit.MaxParallelThreads=4
```

只在确认测试二进制由当前源码构建后使用 `--no-build`。所有 .NET 重活串行执行。
