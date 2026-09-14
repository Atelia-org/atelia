# 固定旧 Timeline v2 / Control v3 样本

生成基线：`3c1fd372ad488b4fb848779f9af59eef063478c7`，2026-09-14。
产物来自该提交的旧生产 APIs；没有用新 writer 改字段伪造旧库。
原 `LegacyV2` 与 `ControlReceiptV2` 四个 ZIP 保持原字节。

## 内容和证据边界

- `repository.zip`：完整合成 repository。Timeline schema 2，12 个持久行、11 个 selected 行、1 个 retained 非 selected 行，1 policy、19 Merkle 节点，head generation 13。Control schema 3、generation 10，包含非空 bootstrap 与 3 个实际 registration receipts。Recap Store schema 3 为空。
- `control-backup.zip`：旧 `RecapGridControlMaintenance.Backup` 的原目录内容，包含原 `control.json` 和 manifest；没有重写 manifest。
- `expected.json`：Ref/Timeline、原 head 与 descriptor canonical Base64、旧 deselected 行、新 recipe 与 bootstrap。
- `commands.json`：旧 canonicalizer 直接产生的 registration bytes/digests、原规则 canonical bytes 和 operation 字段；promotion 使用旧 `PromotionDigest`。family-only、definition-only、recipe-nonempty-witness 已实际应用。recipe-null-witness 仅编码，未在非空 Timeline 上非法应用。空 bundle 被旧构造器拒绝，记录 `ArgumentException`，不存在伪造的空命令 bytes。
- `sqlite-snapshot.json`：对 ZIP 隔离解压副本以 SQLite `mode=ro` 读取的全部表。每表给出 columns 和 rows；BLOB 为 `{ "base64": "..." }`，rows 按 JSON 文本排序，比较时应按实际主键匹配。包括所有非 selected 行。
- `repository-files.json`：ZIP 内每个原始文件的长度与 SHA-256，可证明升级范围外的 Journal/Cadence/Control 原字节保持。
- `RowIdentityV2FixtureGenerator.cs.txt`：只在旧 baseline 的 Manager.Tests 中临时编译的生成器，复用该基线测试的 fixture helper；不是当前生产文件。
- `capture.py`：只读提取 SQL 与文件证据，不改 ZIP。

物理 scope：

```text
RefId: 000000000400001f
TimelineId: 31c9aaeabf0c7fdbc987538775addccb
derived/history-timeline/v2/refs/000000000400001f/timelines/31c9aaeabf0c7fdbc987538775addccb.sqlite
```

生成先追加 2 个 initial turns 和 reserve，再追加 2 个 later turns 和 reserve；通过真实 `RewindLatestCompletedTurn`、`ReconcileSelectedPath`、新 sibling observation 与 Cadence offline seal 留下旧分支尾行。当前只有一个 active Timeline scope；样本不声称覆盖多个 retained Timeline 库。

Family 使用旧 Manager 测试协议 `output-v1/input-v1/runtime-v1`，不直接支持 `RecapCompletionRuntime`。纵向集成可以在升级后注册正式 V3 Family/Definitions/recipe，再通过真实 Manager/Runtime 与 fake provider 构建，同时核验旧规则与 receipts 保留；不得把此样本当作真实 provider 调用证据。全部原始对话及输出都是合成文本，没有 credentials 或真实 `.atelia` 数据。

## 复现

在上述旧提交的隔离 worktree 中，把生成器复制为
`tests/SessionJournal.RecapGrid.Manager.Tests/RowIdentityV2FixtureGenerator.cs`，再执行：

```bash
flock /tmp/atelia-timeline-dotnet.lock dotnet restore tests/SessionJournal.RecapGrid.Manager.Tests/SessionJournal.RecapGrid.Manager.Tests.csproj --disable-parallel -m:1 -nr:false
flock /tmp/atelia-timeline-dotnet.lock env ATELIA_ROW_FIXTURE_OUTPUT=/tmp/row-fixture-new-output dotnet test tests/SessionJournal.RecapGrid.Manager.Tests/SessionJournal.RecapGrid.Manager.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~GenerateRowIdentityV2FixedFixture -- xUnit.MaxParallelThreads=4
python3 /absolute/path/to/capture.py /tmp/row-fixture-new-output /tmp/row-fixture-new-evidence
```

两个输出目录必须尚不存在。新生成实例的随机 ID 和 ZIP metadata 会改变，因此复现验证的是构造过程与不变量；提交中的固定 ZIP、expected 和 SQL 快照应作为同一份证据使用，不得混搭。

实际生成验证：旧基线 restore 成功；指定 generator test **1 passed / 0 failed / 0 skipped**。生成器同步 test 调用旧异步 helper 产生一条 `xUnit1031` warning；这是生成 harness 的静态建议，不是生产编译失败。旧 `HistoryTimelineMaintenance.Verify` 已在打包前成功，生成器还检查了旧尾行已退出 selected path。

ZIP 解压时保留零字节锁文件；Linux 测试复制到隔离目录后按现有 fixture 约定恢复私有目录/文件权限。不要删除源 ZIP 中的锁文件或改写旧 backup manifest。
