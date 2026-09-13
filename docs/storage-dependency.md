# 存储库依赖

Primitives、Data、Rbf、RbfSegmentStore 和 EventJournal 在 [atelia-storage](https://github.com/Atelia-org/atelia-storage) 维护。
[eng/StorageDependency.props](../eng/StorageDependency.props) 是本仓包版本与对应源码 commit 的唯一配置入口。
日常构建通过 PackageReference 从 nuget.org 自动 restore，不需要先运行 Prepare，也不需要旁边存在存储源码仓。

在仓根执行：

```powershell
dotnet build Atelia.sln -c Release
dotnet test Atelia.sln -c Release --no-build
```

发布与迁移验收状态见[实施计划](plans/atelia-storage-extraction-plan.md)；用法说明不代替公开下载验证。

## 显式源码联调

需要同时修改存储库时显式选择完整新仓的绝对路径，每次切换模式重新 restore：

```powershell
$storage = 'E:/repos/Atelia-org/atelia-storage'
dotnet restore Atelia.sln -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet build Atelia.sln -c Release --no-restore -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet test Atelia.sln -c Release --no-build -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
```

`StorageSourceRoot` 缺失或无效时明确失败，不会回退到包。源码模式仅用于 build/test，消费仓 pack 会拒绝此模式。
切回默认包模式时执行 `dotnet restore Atelia.sln -p:UseStorageSources=false`，再按上面的普通命令构建。

## 使用本地开发包

需要验证包交付边界时，先在存储仓按其 `eng/Pack.ps1` 规则，从明确的干净 commit 打出一个新的唯一版本：

```powershell
$storage = 'E:/repos/Atelia-org/atelia-storage'
$storageDevVersion = "0.1.1-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).g$([Guid]::NewGuid().ToString('N').Substring(0,8))"
& "$storage/eng/Pack.ps1" -Version $storageDevVersion -OutputDirectory "$storage/artifacts/dev-feed"
```

在本消费仓忽略的 `.artifacts/storage-dev/NuGet.Config` 保存以下配置，将 feed 路径换成刚才的绝对路径。
五个精确 PackageId 只从开发 feed 取得，其余依赖从 nuget.org 取得；不要修改日常根配置。

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="storage-dev" value="E:/repos/Atelia-org/atelia-storage/artifacts/dev-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="storage-dev">
      <package pattern="Atelia.Primitives" />
      <package pattern="Atelia.Data" />
      <package pattern="Atelia.Rbf" />
      <package pattern="Atelia.RbfSegmentStore" />
      <package pattern="Atelia.EventJournal" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

在同一 PowerShell 会话、消费仓根目录中运行：

```powershell
dotnet restore Atelia.sln --configfile .artifacts/storage-dev/NuGet.Config -p:UseStorageSources=false "-p:StoragePackageVersion=$storageDevVersion"
dotnet build Atelia.sln -c Release --no-restore -p:UseStorageSources=false "-p:StoragePackageVersion=$storageDevVersion"
dotnet test Atelia.sln -c Release --no-build --no-restore -p:UseStorageSources=false "-p:StoragePackageVersion=$storageDevVersion"
```

排查开发包时，以那次上游 Pack 的 manifest.sourceRevision 定位源码。
每次改变包内容都生成新版本，不覆盖已发布版本或已有开发版本，也不通过清空用户全局缓存来掩盖版本错误。
结束实验后，用根配置重新 `dotnet restore Atelia.sln`，再普通 build/test，恢复 props 中的公开版本。

## 按版本阅读用法

使用 `StorageSourceRevision` 定位 `https://github.com/Atelia-org/atelia-storage/blob/<commit>/`，或在本地 checkout 此 commit。
优先入口为 `README.md`、`src/EventJournal/README.md`、`src/RbfSegmentStore/README.md` 和 `docs/Rbf/rbf-guide.md`。
包中的 README/XML 文档与 Source Link 用于发现和定位对应源码；不要用兄弟仓当前 HEAD 的行为解释固定版本的包。
消费方仍需主动读取这些文档。归档设计中的旧源码路径保留历史语境。
