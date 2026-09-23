# Completion 依赖

Diagnostics、Completion.Abstractions、Completion、Completion.Tools 的源码与自身测试在
[atelia-completion](https://github.com/Atelia-org/atelia-completion) 维护。
本仓的包版本、源码身份与仓库地址统一记录在 [CompletionDependency.props](../eng/CompletionDependency.props)。
各产品仍只引用其实际使用的包；Diagnostics 不会带入 Completion。

## 当前包交付

当前 pin 为 `0.1.0-preview.3`，源码身份
`bbce08b85aec114463313f6d9b539e8412b740eb`。四包
（Atelia.Diagnostics、Atelia.Completion.Abstractions、Atelia.Completion、Atelia.Completion.Tools）
已公开发布到 nuget.org；上游 tag `v0.1.0-preview.3` 指向同一提交。

没有本地 override 的 clone，普通 restore/build 直接使用根 [nuget.config](../nuget.config)，无须本地冻结 feed：

```sh
dotnet restore tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj -p:UseCompletionSources=false -m:1 -nr:false
dotnet build prototypes/Galatea/Galatea.Server.csproj --no-restore -c Release -p:UseCompletionSources=false -m:1 -nr:false
```

旧本地试运行产物保留为历史证据：[NuGet.Completion.Local.config](../eng/NuGet.Completion.Local.config)
和 `gitignore/completion-packages/0.1.0-dev.20260916114103/` 只对应当时的唯一 dev 包交付，
不再用于当前 preview.3 流程；该 feed/cache 是 ignored 本地产物，不随 Git clone 搬运。
不要用旧本地配置消费新 pin，也不要在相同版本下重新打包覆盖。

对应源码指南可从 tag `v0.1.0-preview.3` 取得：`docs/Completion/quick-start.md`、
`src/Completion/README.md`、`src/Completion.Tools/README.md`、`src/Diagnostics/README.md`。
本轮验收与试运行注意事项见[实施记录](Galatea/completion-auto-retry-implementation.md)。

## 长期本地模式与切换备忘

`eng/CompletionDependency.props` 末尾按存在性导入 `eng/CompletionDependency.Local.props`
（gitignored，不随 Git 搬运）。该文件存在即覆盖默认 pin 与 restore 配置（方式 A 经
`RestoreConfigFile` 选用 [NuGet.Completion.LocalFeed.config](../eng/NuGet.Completion.LocalFeed.config)）；普通
`dotnet build` / `dotnet test` / `dotnet restore`（含 `dotnet test` 的隐式 restore）
自动生效，无须命令行参数。删除或重命名该文件再重新 restore，即回落公开包 pin。
一次性实验仍可用 `-p:CompletionPackageVersion=<版本>` 临时覆盖，命令行属性优先级最高。
模板见 [CompletionDependency.Local.props.template](../eng/CompletionDependency.Local.props.template)。

三态切换：

| 目标状态 | 操作 |
|---|---|
| 公开包（默认，可移植） | 确认不存在 `eng/CompletionDependency.Local.props`，执行 `dotnet restore <项目或 Atelia.sln>` 重新落 assets |
| 本地 feed 包 | `cp eng/CompletionDependency.Local.props.template eng/CompletionDependency.Local.props`，保留方式 A，把 `CompletionPackageVersion` 改成目标 dev 版本 |
| 本地源码联调 | 同上，保留方式 B（`UseCompletionSources=true` + `CompletionSourceRoot`）；源码模式禁止 consumer pack |

接入新 dev 包（上游 pack 之后）：

1. 把上游 `artifacts/feed-<version>/` 中的四组 nupkg/snupkg 与 manifest 复制进
   `gitignore/completion-local-feed/`（累积 feed，多版本共存；ignored 本地产物）。
2. 修改 `eng/CompletionDependency.Local.props` 中的 `CompletionPackageVersion` 与
   `CompletionSourceRevision`（取自 manifest）。
3. `dotnet restore Atelia.sln` 统一切换整个工作区。

注意事项：

- 根 nuget.config 的 packageSourceMapping（`*` → nuget.org）会把本地 feed 从 Atelia 包解析中
  排除，因此方式 A 不直接加源，而是经 `RestoreConfigFile` 使用带显式 source mapping 的
  [NuGet.Completion.LocalFeed.config](../eng/NuGet.Completion.LocalFeed.config)：四个 Atelia
  包允许来自 completion-local 或 nuget.org，其余包仅来自 nuget.org。
- dev 版本必须唯一，不得在相同版本号下重新打包；包缓存为 `gitignore/completion-local-cache/`
  （由该 config 的 globalPackagesFolder 指定）。
- 模式文件是本机状态：每台机器各写各的；CI 与新 clone 无此文件时即默认公开 pin。
- 长期本地模式下建议在里程碑保留 feed 快照与 manifest SHA256 作为身份锚点
  （沿用 `gitignore/completion-packages/` 的既有惯例）。

## 显式源码联调

```powershell
dotnet build Atelia.sln -c Debug -p:UseCompletionSources=true -p:CompletionSourceRoot=E:/repos/Atelia-org/atelia-completion
dotnet test tests/SessionJournal.RecapGrid.Runtime.Tests/SessionJournal.RecapGrid.Runtime.Tests.csproj -c Debug -p:UseCompletionSources=true -p:CompletionSourceRoot=E:/repos/Atelia-org/atelia-completion
```

源码根必须是包含四个项目的绝对路径；包模式联调以 `v0.1.0-preview.3` /
`bbce08b85aec114463313f6d9b539e8412b740eb` 为基准，其他需求使用明确 revision，不自动探测兄弟目录。
四库随开关整体切换，避免包和项目中出现相同程序集的两份身份。切回包模式时重新 restore。
Storage 开关独立；通常只需 Completion 源码 + Storage 包。源码模式禁止 consumer pack。
如需打包消费者，先将上游变更打成唯一开发版本，再显式选用该包版本。

本地开发包的长期使用与三态切换见上文[长期本地模式与切换备忘]；dev 版本必须唯一，
不重新打已发布版本，不清理全局 NuGet 缓存来掩盖版本内容冲突。

## 调用与调试边界

宿主传递 CancellationToken 并控制期限；只把 Completed 正文交给成功业务处理。
Incomplete/Failed 与 terminal 前流中断分开处理。库不自动重试；宿主可依据业务语义，在本次调用
退出并完成清理后重新计算纯生成。Galatea 的自动重试不包含 Journal 提交、工具执行或其他外部副作用；
可能重复计算和计费，不宣称远端 exactly-once。
usage 的 null 是未知；保留消息中的 reasoning 原始信息供协议内回放。
Tools 权限、参数校验和执行序号不代替宿主副作用事务。

Diagnostics public API 只有 `DebugUtil.Debug/Warning/Error`；`Debug` 带 `[Conditional("DEBUG")]`，
Release 调用点零开销。Completion/Tools 以 Release 打包后，其内部 `Debug` 调用已被裁掉，
环境变量不能恢复。控制台一律写 stderr，文件写入当前工作目录
`.atelia/debug-logs/{safe-category}.log`；`ATELIA_DEBUG_FILE_LEVEL` /
`ATELIA_DEBUG_CONSOLE_LEVEL` 接受 `DEBUG`/`WARNING`/`ERROR`/`OFF`。
文件 sink 默认 `DEBUG`、控制台默认 `WARNING`（自 `0.1.0-dev.20260921145510` 起；
`0.1.0-preview.2` 及更早版本文件默认同为 `WARNING`）。
没有 `ATELIA_DEBUG_CATEGORIES` 或类别开关，也没有 `Trace`/`Info`/`Print`/`ClearLog`。
调用文本按级别分层：`Debug` 是开发期临时诊断，可记录宿主自己的数据；`Warning`/`Error`
必须 content-free，异常只允许写 `exceptionType={type.FullName}`。
