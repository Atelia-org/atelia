# atelia-completion 方案审视记录

> 2026-09-14；本轮产物为方案，未执行拆仓、运行测试或发布包。
> 最终实施入口：[分阶段方案](atelia-completion-extraction-plan.md)。本文只保留裁决理由，不作为第二份任务清单。

## 方法与证据边界

按 `dialectical-simplification` 组织三名独立 reviewer：需求怀疑者、最小架构师、语义防守者。三方完整读取同一初稿，以方案 §1 的用户决定与需求来源为共同台账；只读检查当前 Atelia/DramaBoard 代码，不编辑方案。

第一轮各自形成主张，第二轮交换最强反方场景并质询；主线程核对源码后修改方案。争议在第二轮收敛，没有启动第三轮，也没有用投票决定结论。`extract-dotnet-repo` 提供来源、构建闭包、包消费与发布经验；上次 storage 文档属于经验材料，不能独立证明本次需要同等设施。

调查对象为 Atelia `60eb58a1` 和 DramaBoard `d3d6cef`。只读结果证明当前代码形状，不能证明未来提取后的构建或测试已经通过。

## 最小可实施模型

一个新仓、四个独立包、一套迁出的 Completion.Tests。P0 固定 Atelia 基线，P1 形成独立源码和候选包，P2 在隔离候选中切原消费者，P3 发布公开包后切共享默认引用，P4 再做 DramaBoard 的窄适配。

新增包 smoke 只选择一个现有 provider；现有 RecapGrid 四 provider 组合覆盖保留。DebugUtil 保持现有实现，包化后的诊断区别写清楚。基础库归属、存储包版本、游戏提交模型均不因本轮改变。

## 裁决

| 对象 | 裁决 | 依据与最小处理 |
| --- | --- | --- |
| P1 冻结包后、P2 再改上游 IVT | simplify | 在 P1 打包前移除跨仓 friend，P2 只改下游测试；否则两种模式可能验证不同程序集。后续上游修正仍须新开发版本 |
| C/S/G 平行版本术语 | delete | 使用实际 CompletionPackageVersion 属性即可；现有 storage pin 不变，不再引入一套字母身份 |
| 本轮下游实验包与不同版本 nuspec 分支 | defer | 当前交付没有下游 NuGet 包；未来真有产包任务再验证。源码模式拒绝 pack 保留，防止错误传播版本 |
| P0 提前运行 DramaBoard 测试基线 | defer | 移到 P4 修改前固定来源并运行；P0 只读调查。更接近实际接入，减少基线过期与重复执行 |
| 新增包 smoke 的完整 provider 矩阵 | simplify | 一种协议证明真实 nupkg/public API 可用；各 provider 的完整测试仍归 Completion.Tests，不复制矩阵 |
| RecapGrid 四 provider 组合投影 | keep | 真实 runtime 拼出的请求可能出错，而上游人工请求单测仍绿；原 tools omission、Anthropic cache_control 等断言需保留 |
| Release 日志两层语义 | keep | 调用点 Conditional 裁剪与已发布 Diagnostics 的 sink 默认级别是两个真实边界；文档说明即可，不换日志框架 |
| Completion 相关已有持久化合同 | keep | SessionJournal codec/canonicalizer 实际消费消息序列化；保留来源差分与既有 fixture/恢复测试，不复制 storage 冷 writer 平台 |
| P2 本地 dev 包直接发布为 Atelia 默认引用 | simplify | 候选留在隔离分支；P3 公开包实际可取且默认 restore 验证后再发布 main，无需 staging registry |
| DramaBoard 期限、重试与生命周期 | keep | 承接 RequestTimeout、区分用户取消与超时；保留成功正文格式纠正请求，禁止结果不确定的透明重放；共享 client 仍由宿主统一释放 |

保留五个阶段，没有新增协调阶段。减少了三种字母别名、一个无当前产包对象的验收分支和一次过早的 DramaBoard 测试基线；调整 IVT 时机避免一次可以预见的上游候选重做。这是步骤与概念的收敛，不是削减原有业务断言。

## 关键源码证据与修订

- [AssemblyInfo.cs](../../src/Completion/Properties/AssemblyInfo.cs) 的外仓 IVT 与 [RuntimeProviderProjectionTests.cs](../../tests/SessionJournal.RecapGrid.Runtime.Tests/RuntimeProviderProjectionTests.cs) 证明组合测试接缝。原 Atelia 在 P1 仍使用原源码，因此新仓先去掉 friend 不会要求提前删除旧测试。
- [WalkingSkeleton 结构测试](../../tests/SessionJournal.RecapGrid.WalkingSkeleton.Tests/AssemblyDependencyBoundaryTests.cs) 不只检查 csproj，还检查路径、源码与 IVT。迁移必须调整断言的归属，不能泛化成“所有外部引用都跳过”。
- [DebugUtil.cs](../../src/Diagnostics/DebugUtil.cs) 的 Conditional(DEBUG) 与 GetDefaultFileMinLevel/GetDefaultConsoleMinLevel 支持保留日志说明。
- [SessionEventCodec.cs](../../prototypes/SessionJournal/SessionEventCodec.cs) 和 [SessionRequestCanonicalizer.cs](../../prototypes/SessionJournal/SessionRequestCanonicalizer.cs) 表明 Completion 消息参与持久化与请求身份；“纯 HTTP 库无需合同回归”不成立。
- DramaBoard `src/FirstBoard.Demo/DemoBackend.cs` 将 RequestTimeout 传给 HttpClient；`src/Player.Llm/Backends/CodexAppServerBackend.cs` 已有 linked CTS 与 TimeoutException 约定。方案承接配置，但明确旧 HTTP 实现使用 ResponseHeadersRead，不能宣称旧 Timeout 覆盖全部正文读取。新适配器的完整调用预算在宿主边界实现。
- DramaBoard `src/Player.Llm/LlmPlayerDriver.cs` 会对成功返回但业务格式非法的正文做一次纠正请求；`MemoryMaintenance.cs` 也通过 ILlmChatBackend 读取正文。需求怀疑者据此修订“只让异常沿栈传播即可”的初始主张，补上每次请求期限及用户取消的区别。
- DramaBoard `src/FirstBoard.Demo/DemoLlmComposition.cs` 先收口 driver、后释放共享 backend。保持该顺序可避免仍在进行的记忆维护访问已释放传输，不需要新生命周期框架。
- 当前 [Completion README](../../src/Completion/README.md) 已说明 Windows/Linux 文件凭据支持；较早下游分析中的 Linux-only 结论已过时。新仓默认示例也不能沿用旧 quick-start 对本机服务的硬前提。

没有需要现在向用户追加确认的产品选择。新仓 Trusted Publishing 身份配置、可能的登录/2FA，以及可选 live smoke 凭据属于实施时资源配合，已集中列入方案 §5。
