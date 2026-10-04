# Galatea / SessionJournal 切换 RBF1 公开维护包

日期：2026-10-04。消费源码基线：`e030902472e5587bf31a105e1e1206759d566b00`。

## 交付与消费方式

五个存储包已发布到 nuget.org，版本统一为 **`0.2.0-rbf1-preview.1`**：`Atelia.Primitives`、`Atelia.Data`、`Atelia.Rbf`、`Atelia.RbfSegmentStore`、`Atelia.EventJournal`。
通常直接引用 [Atelia.EventJournal](https://www.nuget.org/packages/Atelia.EventJournal/0.2.0-rbf1-preview.1) 即可，其余四包作为传递依赖自动还原；直接使用更底层库的项目继续引用对应包。

- 存储源码：`3e9554e2ea70f769607e80b3fc11a85506050533`，维护分支 `RBF1`，不可变 release tag `v0.2.0-rbf1-preview.1`。
- [发布源码 CI](https://github.com/Atelia-org/atelia-storage/actions/runs/37170973994)：Windows / Ubuntu 均成功。
- [公开发布及回读](https://github.com/Atelia-org/atelia-storage/actions/runs/37171412733)：五包上传、签名/身份/资产校验和隔离 public PackageReference smoke 均成功。
- [Storage 交付记录](https://github.com/Atelia-org/atelia-storage/blob/RBF1/docs/rbf1-preview-delivery-20261004.md) 保存正式候选和公开签名包的 SHA-256。

保留原包名、namespace 和 assembly identity，以独立 `rbf1-preview` 版本系列维护。消费者固定版本，Storage main 的 RBF3 演进不会自动替换此依赖。

## 本仓修改

[StorageDependency.props](../../eng/StorageDependency.props) 统一固定上述包版本和源码 commit；保持 `UseStorageSources=false`，将默认 `StorageStrictTailOpen` 设为 `true`，匹配实际消费的 v2 API。
[存储依赖指南](../storage-dependency.md) 和 [Galatea README](../../prototypes/Galatea/README.md) 同步说明版本与源码联调边界。没有修改消费方 C# 源码，也没有改动 Completion 的 `0.1.0-preview.6` pin。

本机原来的 ignored `eng/StorageDependency.Local.props` 指向 `0.2.0-dev.20260929020652` / `5288bd55b4a5181942dadd76516f799002508144`，会覆盖公共配置。
已先原样备份到 `gitignore/rbf1-public-upgrade-20261004/StorageDependency.Local.props.backup`，核对后移除活动 override。备份 SHA-256：`f348b3950fd81684e9f0455015bd11040ed5e0a5b8450b326126ffe9d80add47`。
旧 `eng/NuGet.Storage.Local.config` 保留，但普通构建不再使用它。

Galatea / SessionJournal 的 MSBuild 实际属性均为：

| 属性 | 值 |
| --- | --- |
| `StoragePackageVersion` | `0.2.0-rbf1-preview.1` |
| `StorageSourceRevision` | `3e9554e2ea70f769607e80b3fc11a85506050533` |
| `UseStorageSources` | `false` |
| `StorageStrictTailOpen` | `true` |
| `RestoreConfigFile` | 空；使用根 `nuget.config` 的 nuget.org |

发布准备仅修改存储仓的文档、CI 和公开回读脚本，五库运行时 C# 与原 dev 基线相同。仍使用 RBF1 帧格式和 EventJournal/SegmentStore v2 目录协议；已经采用 v2 的数据不需要再次迁移。旧 v1 journal 目录仍需其原有显式离线升级流程。

## 包、资产与运行目录核对

- 普通 `dotnet restore Atelia.sln --disable-parallel -m:1 -nr:false` 成功，使用公共包消费模式。
- 核对当前 `src` / `prototypes` / `tests` 内 44 个实际引用存储包的项目资产：42 个包含同版五包；TextEditScript 及其测试仅包含同版 `Atelia.Primitives`。全部是 PackageReference 资产。归档旧项目和已删除项目残留的 obj 不属于此清单。
- 全局 cache 中五个 nupkg 的 SHA-256 逐一匹配正式发布任务回读的公开签名包，nuspec 的 repository commit 均匹配发布源码。
- Galatea Release / Debug 的五个存储 DLL 均逐字节匹配公开包，两个 `.deps.json` 都使用新版本。
- Debug 重建后曾残留旧 Data / Primitives DLL。其大小和 NuGet 归一化时间戳与新文件相同，MSBuild 的 `SkipUnchangedFiles` 跳过复制。追加 `-p:SkipCopyUnchangedFiles=false` 构建后，十个 DLL 哈希全部通过。运行目录核对独立于 assets/deps 的版本核对。
- 同时检查当前消费项目中实际存在的 Release 存储 DLL。刷新 TextEditScript 类库目录的旧 Primitives 副本后，167 个 DLL 全部匹配公开包；类库不复制运行时依赖的输出目录不作为缺失故障。

## 构建与回归

.NET SDK `10.0.201`。命令串行执行：

```bash
dotnet build Atelia.sln -c Release -t:Rebuild --no-restore -m:1 -nr:false
dotnet test Atelia.sln -c Release --no-build --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName!~Performance&FullyQualifiedName!~Stress' \
  --logger trx \
  --results-directory gitignore/rbf1-public-upgrade-20261004/test-results \
  -- xUnit.MaxParallelThreads=4
```

Release 全解决方案重建成功，**0 warning / 0 error**。测试进程显式关闭容量与 Galatea live opt-in，并清空子进程的 `OPENROUTER_API_KEY`；这是普通筛选回归，不是完整性能、容量或真实 provider 验收。

32 个测试程序集合计 **3,507 passed / 2 failed / 6 skipped**；测试命令退出码为 1。其中：

| 关键项目 | passed | failed | skipped |
| --- | ---: | ---: | ---: |
| Galatea.Server.Tests | 1,381 | 0 | 4 |
| SessionJournal.Tests | 555 | 0 | 0 |
| MemoPod.Tests | 282 | 2 | 0 |

六个 skip 是 Galatea 的一个 live 用例、三个 Release 下不启用的 DEBUG 诊断用例，以及 HistoryTimeline / RecapGrid.Manager 的两个 opt-in 容量用例。

### 已有的 MemoPod 失败

失败均在 `MemoPodRecallOutputValidationTests`：

- `MalformedArgumentsAreInvalidModelOutput(rawArgumentsJson: null)`；
- `InvalidToolCallIdsAreInvalidModelOutput(shape: Null)`。

测试 fake 在构造 Completion `RawToolCall` 时分别因 `rawInput` / `toolCallId` 为 null 抛出 `ArgumentNullException`，早于测试预期的 `MemoRecallException`。
MemoPod 及其测试 assets 均没有上述五个存储包依赖。

为区分升级影响，创建 detached 临时 worktree `/tmp/atelia-rbf1-consumer-baseline-20261004`，使用升级前消费 commit `e0309024`，恢复原 dev override，保持相同 Completion `0.1.0-preview.6`。Release build 成功；只筛选上述两个测试方法的 26 个 theory case，结果 **24 passed / 同样 2 failed**。
因此它们是消费基线已有问题，不属于本次存储包升级引入的回归；本次没有修补这些测试或修改 Completion 契约。

Galatea Debug 重建及强制复制构建亦成功，均为 0 warning / 0 error。没有启动或重启实际服务，没有迁移、写入或全面审计用户已有数据。

## 本机证据

- `/tmp/rbf1-consumer-restore.log`、`/tmp/rbf1-consumer-build.log`、`/tmp/rbf1-consumer-tests.log`。
- `/tmp/rbf1-consumer-debug-build.log`、`/tmp/rbf1-consumer-debug-refresh.log`。
- `/tmp/rbf1-consumer-textedit-refresh.log`：对 TextEditScript 的临时构建属性 `SkipCopyUnchangedFiles=false` / `CopyLocalLockFileAssemblies=true`，没有改变项目文件。
- `/tmp/rbf1-consumer-baseline-restore.log`、`/tmp/rbf1-consumer-baseline-build.log`、`/tmp/rbf1-consumer-baseline-tests.log`。
- `gitignore/rbf1-public-upgrade-20261004/test-results/`、`baseline-test-results/` 的 TRX。
- 同目录 `consumer-test-summary.json`、`storage-assets-inventory.json`、`galatea-storage-output-check.json`，以及旧 override 原样备份。

上述本机产物被 gitignore；共享的发布 CI、公开回读和固定源码/版本记录用于其他机器重现。
