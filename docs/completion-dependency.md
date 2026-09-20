# Completion 依赖

Diagnostics、Completion.Abstractions、Completion、Completion.Tools 的源码与自身测试在
[atelia-completion](https://github.com/Atelia-org/atelia-completion) 维护。
本仓的包版本、源码身份与仓库地址统一记录在 [CompletionDependency.props](../eng/CompletionDependency.props)。
各产品仍只引用其实际使用的包；Diagnostics 不会带入 Completion。

## 当前包交付

当前 pin 为 `0.1.0-preview.2`，源码身份
`8828ceaa03cb490cf29e3e9ba8ff4129763e9d51`。四包
（Atelia.Diagnostics、Atelia.Completion.Abstractions、Atelia.Completion、Atelia.Completion.Tools）
已公开发布到 nuget.org；上游 tag `v0.1.0-preview.2` 指向同一提交。

普通 restore/build 直接使用根 [nuget.config](../nuget.config)，无须本地冻结 feed：

```sh
dotnet restore tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj -p:UseCompletionSources=false -m:1 -nr:false
dotnet build prototypes/Galatea/Galatea.Server.csproj --no-restore -c Release -p:UseCompletionSources=false -m:1 -nr:false
```

旧本地试运行产物保留为历史证据：[NuGet.Completion.Local.config](../eng/NuGet.Completion.Local.config)
和 `gitignore/completion-packages/0.1.0-dev.20260916114103/` 只对应当时的唯一 dev 包交付，
不再用于当前 preview.2 流程；该 feed/cache 是 ignored 本地产物，不随 Git clone 搬运。
不要用旧本地配置消费新 pin，也不要在相同版本下重新打包覆盖。

对应源码指南可从 tag `v0.1.0-preview.2` 取得：`docs/Completion/quick-start.md`、
`src/Completion/README.md`、`src/Completion.Tools/README.md`、`src/Diagnostics/README.md`。
本轮验收与试运行注意事项见[实施记录](Galatea/completion-auto-retry-implementation.md)。

## 显式源码联调

```powershell
dotnet build Atelia.sln -c Debug -p:UseCompletionSources=true -p:CompletionSourceRoot=E:/repos/Atelia-org/atelia-completion
dotnet test tests/SessionJournal.RecapGrid.Runtime.Tests/SessionJournal.RecapGrid.Runtime.Tests.csproj -c Debug -p:UseCompletionSources=true -p:CompletionSourceRoot=E:/repos/Atelia-org/atelia-completion
```

源码根必须是包含四个项目的绝对路径；包模式联调以 `v0.1.0-preview.2` /
`8828ceaa03cb490cf29e3e9ba8ff4129763e9d51` 为基准，其他需求使用明确 revision，不自动探测兄弟目录。
四库随开关整体切换，避免包和项目中出现相同程序集的两份身份。切回包模式时重新 restore。
Storage 开关独立；通常只需 Completion 源码 + Storage 包。源码模式禁止 consumer pack。
如需打包消费者，先将上游变更打成唯一开发版本，再显式选用该包版本。

本地开发包使用显式 NuGet.Config、四个包 ID 的精确 source mapping 和独立缓存；
用 `-p:CompletionPackageVersion=<唯一开发版本>` 覆盖。多个验证者共享一次冻结 feed，
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
`ATELIA_DEBUG_CONSOLE_LEVEL` 接受 `DEBUG`/`WARNING`/`ERROR`/`OFF`，默认均为 `WARNING`。
没有 `ATELIA_DEBUG_CATEGORIES` 或类别开关，也没有 `Trace`/`Info`/`Print`/`ClearLog`。
调用文本必须 content-free；异常只允许写 `exceptionType={type.FullName}`。
