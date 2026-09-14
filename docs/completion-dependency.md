# Completion 依赖

Diagnostics、Completion.Abstractions、Completion、Completion.Tools 的源码与自身测试在
[atelia-completion](https://github.com/Atelia-org/atelia-completion) 维护。
本仓的包版本、源码身份与仓库地址统一记录在 [CompletionDependency.props](../eng/CompletionDependency.props)。
各产品仍只引用其实际使用的包；Diagnostics 不会带入 Completion。

## 默认包模式

公开交付完成后，普通 `dotnet build Atelia.sln -c Release` 直接从 nuget.org restore，
不需要新仓检出或 Prepare 脚本。当前迁移候选的开发包验收使用仓外冻结 feed；
合入共享 main 前必须切到已公开且实测可还原的版本。

对应版本指南：[快速上手](https://github.com/Atelia-org/atelia-completion/blob/v0.1.0-preview.1/docs/Completion/quick-start.md)、
[传输合同](https://github.com/Atelia-org/atelia-completion/blob/v0.1.0-preview.1/src/Completion/README.md)、
[Tools](https://github.com/Atelia-org/atelia-completion/blob/v0.1.0-preview.1/src/Completion.Tools/README.md)、
[Diagnostics](https://github.com/Atelia-org/atelia-completion/blob/v0.1.0-preview.1/src/Diagnostics/README.md)。
固定 tag 与 props 的版本一起更新；源码和 Source Link 不会自动成为 Agent 的上下文，修改消费代码前应主动阅读相关指南。

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
Incomplete/Failed 与 terminal 前流中断分开处理；结果不确定时不能透明重试。
usage 的 null 是未知；保留消息中的 reasoning 原始信息供协议内回放。
Tools 权限、参数校验和执行序号不代替宿主副作用事务。

Release Completion/Tools 中的 Trace/Info 调用可能已裁掉，环境变量无法恢复。
下游 Debug 调用仍受发布版 Diagnostics 的 sink 阈值控制；需要时显式设置
ATELIA_DEBUG_FILE_LEVEL / ATELIA_DEBUG_CONSOLE_LEVEL，内部详细调试使用源码 Debug 模式。
