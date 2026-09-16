# Completion 依赖

Diagnostics、Completion.Abstractions、Completion、Completion.Tools 的源码与自身测试在
[atelia-completion](https://github.com/Atelia-org/atelia-completion) 维护。
本仓的包版本、源码身份与仓库地址统一记录在 [CompletionDependency.props](../eng/CompletionDependency.props)。
各产品仍只引用其实际使用的包；Diagnostics 不会带入 Completion。

## 当前本地包交付

当前 pin 为 `0.1.0-dev.20260916114103`，源码身份
`612b9bc7fec52c4bd3a98b06b64f61e3ead6ef93`。用户选择先本地试运行，再另行安排公开发布；
此版本不在 nuget.org，普通默认 restore 尚不可用，也不能降回缺少新 API 的 preview.1。

本机冻结包及 manifest 位于 `gitignore/completion-packages/0.1.0-dev.20260916114103/`。
[本地配置](../eng/NuGet.Completion.Local.config)精确映射四包，其他依赖仍用 nuget.org，
独立缓存为 `gitignore/completion-local-cache/`。在仓库根执行：

```sh
dotnet restore tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --configfile eng/NuGet.Completion.Local.config -p:UseCompletionSources=false -m:1 -nr:false
dotnet build prototypes/Galatea/Galatea.Server.csproj --no-restore -c Release -p:UseCompletionSources=false -m:1 -nr:false
```

feed/cache 为 ignored 本地产物，不随 Git clone 搬运。另一台机器需复制该版本目录与 manifest，
核对全部 SHA256 后使用同一显式配置；或者使用下文显式源码模式。不能在相同版本下重新打包覆盖。
本轮验收与试运行注意事项见[实施记录](Galatea/completion-auto-retry-implementation.md)。
对应源码指南在本机 `/repos/focus/atelia-completion/docs/Completion/quick-start.md` 与
`src/Completion/README.md`、`src/Completion.Tools/README.md`、`src/Diagnostics/README.md`；
提交尚未推送，不以远端链接宣称已经可取得。公开发布后须一起更新 props、来源说明和包源配置。

## 显式源码联调

```powershell
dotnet build Atelia.sln -c Debug -p:UseCompletionSources=true -p:CompletionSourceRoot=E:/repos/Atelia-org/atelia-completion
dotnet test tests/SessionJournal.RecapGrid.Runtime.Tests/SessionJournal.RecapGrid.Runtime.Tests.csproj -c Debug -p:UseCompletionSources=true -p:CompletionSourceRoot=E:/repos/Atelia-org/atelia-completion
```

源码根必须是包含四个项目的绝对路径，使用与需求相符的明确 revision；不自动探测兄弟目录。
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

Release Completion/Tools 中的 Trace/Info 调用可能已裁掉，环境变量无法恢复。
下游 Debug 调用仍受发布版 Diagnostics 的 sink 阈值控制；需要时显式设置
ATELIA_DEBUG_FILE_LEVEL / ATELIA_DEBUG_CONSOLE_LEVEL，内部详细调试使用源码 Debug 模式。
