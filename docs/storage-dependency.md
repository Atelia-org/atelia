# 存储库依赖

Primitives、Data、Rbf、RbfSegmentStore 和 EventJournal 在 [atelia-storage](https://github.com/Atelia-org/atelia-storage) 维护。Atelia 默认通过 NuGet 使用这五个库，版本及对应源码 commit 集中在 [eng/StorageDependency.props](../eng/StorageDependency.props)。不要用兄弟仓当前 HEAD 的行为解释固定版本的包。

本次固定版的[用法入口](https://github.com/Atelia-org/atelia-storage/blob/09d979941d2c671a1e7a8ffabfa6e2b340e00f69/README.md)在首次 push 后可访问；当前可从 Prepare 生成的 `.artifacts/storage-source/<StorageSourceRevision>/README.md` 读取同一文件。升级 pin 时一并更新此文档链接。

首次构建先准备固定来源的本地包，再按正常流程构建：

```powershell
./eng/Prepare-Storage.ps1
dotnet build Atelia.sln -c Release
dotnet test Atelia.sln -c Release --no-build
```

远端首次建立前，Prepare 的 `-SourceRepository` 参数可指向本地 atelia-storage Git 仓库；脚本仍按 StorageSourceRevision 取固定 commit，不使用其未提交修改。包默认放入忽略的 `.artifacts/storage-feed`，可通过 `-OutputDirectory` 指定另一个 feed（同时需为 restore 指定匹配的 NuGet 配置）。`nuget.config` 将五个准确包名映射到默认 feed，其余包从 nuget.org 还原。清理目录或换机器后重新 Prepare。

需要同时修改存储库时显式启用源码联调，每次切换模式重新 restore：

```powershell
dotnet restore Atelia.sln -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
dotnet build Atelia.sln -c Release --no-restore -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
dotnet test Atelia.sln -c Release --no-build -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
```

StorageSourceRoot 必须是包含五个项目的绝对路径。源码模式仅用于 build/test，消费仓 pack 会拒绝此模式。需要实验包时，先用新仓 `eng/Pack.ps1` 生成新版本，再以该 StoragePackageVersion 进行包模式构建；不能覆盖已有版本的包。

阅读依赖代码时，将 StorageSourceRevision 填入 `https://github.com/Atelia-org/atelia-storage/blob/<commit>/`，或在本地 checkout 此 commit。优先入口为 `README.md`、`src/EventJournal/README.md`、`src/RbfSegmentStore/README.md` 和 `docs/Rbf/rbf-guide.md`。包中的 README/XML 文档与 Source Link 用于发现和定位对应源码；消费者仍需主动读取这些文档。

归档设计中的旧源码路径保留历史语境；迁移执行记录见 [实施计划](plans/atelia-storage-extraction-plan.md)。
