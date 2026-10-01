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
$storageDevVersion = "0.2.0-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).g$([Guid]::NewGuid().ToString('N').Substring(0,8))"
& "$storage/eng/Pack.ps1" -Version $storageDevVersion -OutputDirectory "$storage/artifacts/dev-feed"
```

长期使用本地包时，复制以下模板并填写本机值；两个实际文件均已 gitignore：

- `eng/StorageDependency.Local.props.template` → `eng/StorageDependency.Local.props`：填写唯一版本与 Pack manifest 的 sourceRevision。
- `eng/NuGet.Storage.Local.config.template` → `eng/NuGet.Storage.Local.config`：填写开发 feed 路径（模板使用相对配置文件位置的兄弟仓路径）。

`Directory.Build.props` 在 Completion 依赖之后导入 Storage local override，使组合配置的
`RestoreConfigFile` 最后生效。无需额外命令行属性即可普通 restore/build/test：

`StorageStrictTailOpen` 是显式 API 能力标记，必须与 pin/sourceRevision 配套：默认公开旧包为
`false`，CLI 显式关闭旧包的 open-time recovery；v2 本地包或 v2 源码设为 `true`，CLI 使用
新版严格打开入口（坏物理尾报错、不修复；新版已删除 recovery options）。不能仅升级版本而遗漏
此标记，也不能在旧包模式设为 `true`。源码联调 v2 同样在 Local.props 显式设置此标记。

```powershell
dotnet restore Atelia.sln
dotnet build Atelia.sln -c Release --no-restore
dotnet test Atelia.sln -c Release --no-build --no-restore
```

若同时启用了 `CompletionDependency.Local.props` 的本地包模式，保留其中的版本和来源，
并在 Storage 的 NuGet 配置中加入原 Completion feed 的 `packageSources` 项，以及对应
`packageSourceMapping` 的四个精确 PackageId：`Atelia.Diagnostics`、
`Atelia.Completion.Abstractions`、`Atelia.Completion`、`Atelia.Completion.Tools`。
四个 Completion exact mappings 同时保留在 Completion feed 与 nuget.org，允许切回公开
Completion pin；模板已包含公开源的四项精确映射。只有 nuget.org 的 `*` 时，它会被本地源的
精确映射遮蔽。五个 Storage exact mappings 只指向 Storage feed；其余包使用 nuget.org 的 `*`。
一个 restore 只使用一个完整 config；不会自动合并另一个
`RestoreConfigFile`，也不能仅靠 `RestoreSources` 覆盖根 source mappings。

源码联调也可在 Storage local props 中设置 `UseStorageSources=true` 与绝对
`StorageSourceRoot`，并删除本地版本和 `RestoreConfigFile` 项；源码模式禁止 consumer pack。
每次切换都重新 restore。移走 Storage local props 后恢复公开 Storage pin；若保留
Completion local props，其本机 Completion 模式继续生效。

排查开发包时，以那次上游 Pack 的 manifest.sourceRevision 定位源码。
每次改变包内容都生成新版本，不覆盖已发布版本或已有开发版本，也不通过清空用户全局缓存来掩盖版本错误。

本机 Galatea 的 v2 数据与 dev 包切换证据见[2026-09-29 升级记录](Galatea/storage-v2-local-upgrade-20260929.md)。该记录不改变上面的公开默认 pin。

## 按版本阅读用法

使用 `StorageSourceRevision` 定位 `https://github.com/Atelia-org/atelia-storage/blob/<commit>/`，或在本地 checkout 此 commit。
优先入口为 `README.md`、`src/EventJournal/README.md`、`src/RbfSegmentStore/README.md` 和 `docs/Rbf/rbf-guide.md`。
包中的 README/XML 文档与 Source Link 用于发现和定位对应源码；不要用兄弟仓当前 HEAD 的行为解释固定版本的包。
消费方仍需主动读取这些文档。归档设计中的旧源码路径保留历史语境。

## v2 消费回归边界

v2 的严格 format gate 会将已存在但缺 `journal.format` 的不完整目录判为
`StorageOpenException`：`FormatUnsupported/LegacyOrIncompleteLayout`；Galatea 不吞掉此错误。
缺失、空目录与文件路径仍保持 `session-unprovisioned` 合同，拒绝自动接管或覆盖已有内容。
旧包分支继续断言旧错误类型；v2 synthetic fixtures 是旧合成 ZIP 的显式离线升级，
其原始 facts 与应用 sidecars 保真证据见各 `*StorageV2/README.md`。
