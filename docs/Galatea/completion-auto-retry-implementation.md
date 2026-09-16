# Completion 自动重试实施记录

状态：实现、独立审阅、包模式回归与本地交付验收完成；本文件随实现一并提交。尚未部署真实角色，公开发布另行安排。开始日期：2026-09-16。

## 当前验收结论（优先于下文历史流水）

- P1–P5 实现、独立 review 与高风险尾修已完成；历史 Started/Failed 严格读取保留，新的调用开始/重试不再写入业务事件链。
- 新增冷恢复一次性 0–250ms jitter、单次生成 boundary 恢复、可停止 admission、Recap maintenance 单次期限移交，均有对应故障测试。
- 上游最终提交 `612b9bc7fec52c4bd3a98b06b64f61e3ead6ef93`，离线测试 **843 passed / 1 Windows-only skip / 0 failed**。
- 最终开发包为 `0.1.0-dev.20260916114103`；冻结 feed 为 `/tmp/completion-retry-bounded-package.HaeDM6/feed`，manifest 记录四包 SHA256。三个独立消费者通过；Atelia 独立缓存四包 hash 与 manifest 一致，assets 均为 package，输出 Completion DLL 与包内 DLL hash 一致。
- 最终包模式 Release：Core **557/557**、PublicSurface **4/4**、Offline **30/30**。源码模式 Debug：HistoryTimeline **207/207**、Recap.Runtime **96/96**、Hosting **39/39**、Hosting.PublicSurface **7/7**、Online **33/33**、CLI **162/162**；Node **23/23**。
- Host 源码 Debug **1291/1291**；随后新增四个冷恢复 jitter 用例。Release 包模式两次完整运行均为 **1294/1295**，分别暴露测试夹具在线 seed CAS 竞态、跨状态变化二次读取快照竞态；均只修夹具，不降低生产约束。相关三个测试连续三轮通过；最终完整 Release 包模式 **1295/1295，0 failed**（2 分 30 秒），TRX：`/tmp/galatea-retry-test-results/retry-host-package-release-verified.trx`。
- scoped 文档检查 **57 files / 0 diagnostics**；`git diff --check` 通过。构建测试项目仍有 nullable/xUnit analyzer warnings，不宣称测试代码零警告。
- 独立最终审计确认旧 v7/v8 Prepared/Started 与新事件混合历史、冷 strict open/audit、工具仅执行一次、提交不确定后冷恢复、生产 SSE 预览隔离、Note/mail 冷结算和 Terminated lease 消费已有实际断言。
- 仅合成故障与隔离临时 home 验证；未调用真实 provider，未修改真实角色数据，未部署、公开推送或发布 NuGet。
- **交付方式已确认**：用户选择先本地试运行，NuGet 另行安排。`eng/CompletionDependency.props` 已 pin 最终开发包与源码提交，不推送、不公开发布。必须使用显式本地 NuGet 配置或源码联调；普通 nuget.org restore 不可用。

当前本地交付入口（仓库根执行，SDK 10.0.201）：

```sh
dotnet restore tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --configfile eng/NuGet.Completion.Local.config -p:UseCompletionSources=false -m:1 -nr:false
dotnet build prototypes/Galatea/Galatea.Server.csproj --no-restore -c Release -p:UseCompletionSources=false -m:1 -nr:false
```

冻结包与 manifest 已从临时目录原样复制至 `gitignore/completion-packages/0.1.0-dev.20260916114103/`，
四个 nupkg 与四个 snupkg 的 SHA256 全部通过 manifest 校验。显式配置精确映射四包，独立缓存为
`gitignore/completion-local-cache/`；此目录不随 Git clone 搬运，另一机器须复制包与 manifest 并校验。
历史 TRX、最终上游离线 TRX 与独立消费者结果已复制至 `gitignore/completion-auto-retry-validation/`。
上游三项本地提交未推送。固定 SDK 已保存至 `gitignore/dotnet-10.0.201/`，无需依赖临时目录或修改全局安装；
本机新 shell 的 PATH 设置见[依赖指南](../completion-dependency.md)，其他机器正常安装仓库指定 SDK 即可。

本地交付最终复验：无包版本覆盖参数、`UseCompletionSources=false`，使用仓内显式配置 restore 成功；
assets 四包均为默认 pin 的 package、cache/source 均为上述持久本地目录；四包缓存 hash、nuspec 源码提交及
Release 输出 Completion DLL 均与冻结包一致。完整 Release Host **1295/1295、0 failed**（2 分 16 秒），
TRX：`gitignore/completion-auto-retry-validation/retry-host-local-delivery.trx`。
独立交付 review 的 README restore/Release 示例 finding 已修复；scoped 文档检查 **57/0**、暂存 diff check 通过。
使用持久本地 SDK 的 Galatea.Server Release build 再验通过：**0 warnings / 0 errors**。

### 试运行边界

本轮没有启动真实实例。切换前先正常停服，将完整状态目录备份到目录外，在隔离副本 strict open/audit
并确认旧尾恢复；新 binary 会写新 Action/TurnEnded，不能仅换回旧 binary 回滚，须使用配套备份。
按[启动说明](../../prototypes/Galatea/README.md)显式传入已核对的 config；不要与旧服务并发打开同一状态目录。
观察暂时 transport/限流故障后的退避恢复、停止与重连预览，以及非暂时错误的 blocked 提示。
生成重试可能重复远端计算与计费；工具结果未知仍不会自动重放。真实试运行结果不计入本轮合成验收。

下文为按时间保留的实施流水，其中“待执行/正在修复”和旧候选包不代表当前状态。

## 用户请求的临时暂停检查点

用户已通知网络恢复，实施继续。以下保留暂停时的检查点作为进度依据，不代表当前仍暂停。

2026-09-16：用户预告外网暂时断开，要求方便时暂停。没有运行中的 .NET 测试；不再启动
构建、网络请求或新工作包。目标未完成，也不是 blocked，待用户通知继续。

恢复后的直接入口：

1. 核对工作树及 subagent `completion_contract` 最后补丁；复测 Hosting.PublicSurface 唯一
   失败 `BorrowingFactoryExposesOptionalAgentControlAndLiveTelemetryComposition`（新增可选 factory
   参数导致旧反射合同失配）。Runtime 96/96、Hosting 39/39 已通过。
2. 完整 Host 首轮 28 失败的尾修已经分包完成；新的 frozen boundary API 与 Host 接通，Core
   已复测 557/557、PublicSurface 4/4。重跑完整 Host 非 live 套件和新 Recap maintenance 纵向例，
   不沿用先前 1254/1282 的结果当作最终证据。
3. 明确版本 Atelia 包消费、隔离历史验收、最终文档/需求审计与 Atelia 提交仍未完成。
   新增 `recap-grid-route-manifest-v2.md` 的 current 文档需要加入 scoped checker 清单。

固定 SDK：`/tmp/galatea-dotnet-10.0.201.95kOZL/dotnet`；源码联调显式
`-p:UseCompletionSources=true -p:CompletionSourceRoot=/repos/focus/atelia-completion`。
测试证据目录 `/tmp/galatea-retry-test-results`；上游包目录及提交见下文。
实际 Atelia Git index 未暂存本任务；上游两提交已完成、未推送/发布；真实角色状态未改动。

依据：[重构方案](completion-auto-retry-refactor-plan.md)。用户已授权实施、必要的 Git 提交与文档维护。
本记录区分源码、合成故障验收、包消费和真实部署；不把其中一项替代另一项。

## 工作包与完成闸门

| 包 | 范围 | 完成证据 | 当前状态 |
|:--|:--|:--|:--|
| P1 上游单次调用合同 | 窄失败事实、I/O 异常边界、取消/释放、terminal 立即返回 | HTTP/SSE fixtures、并发取消、Codex 认证单次协商、独立 review | 已提交；843 通过 / 1 平台跳过 |
| P2 Journal 业务边界 | 新 Prepared→Action、历史严格读取、TurnEnded、提交不确定 poison/reopen | 新旧 schema、冷恢复、工具/结束、ref 故障测试、独立 review | Core 557/557；PublicSurface 4/4；Offline 30/30 |
| P3 主生成重试 | 唯一宿主 decorator、退避/期限、纯生成重用请求、瞬态预览 | 表驱动分类、时间/取消、无重叠、透传身份/选项测试 | 实现、review 与最终包回归完成 |
| P4 宿主与消费者 | 启动恢复、blocked、Stop、SSE、lease/mail/Timeline/Undo | interval=0、players=[]、多角色、工具批次、停止与重连验收 | 最终 Release 包模式 Host 1295/1295 |
| P5 持续运行覆盖 | admission、Note/邮件纯生成阶段重试，保留领域结算边界 | 故障后原目标结算、不重发、不重生成 Action | 实现、纵向验收与 review 完成 |
| P6 交付 | 明确版本包、独立消费者、Atelia 集成、隔离历史升级验证 | 版本/来源/hash、受影响套件、文档检查、完成审计 | 本地 pin/feed/config 已交付；本地 Release Host 1295/1295；真实部署/公开发布另行安排 |

真实部署须遵守方案的停服、目录外备份、隔离验证、再切换顺序；不在开发中直接修改角色状态。
公开推送/包发布不从“允许 Git 提交”推定授权；优先用唯一开发版本与显式本地源完成包验收。

## 执行约束

- 主线程管理跨包合同与共享文件；每个包先复核，再定稿实施，最后独立审阅和尾修。
- 上游仅提供事实，不引入通用重试；宿主重试不包住 Journal commit、工具或领域提交。
- 同一可写 .NET 构建图串行验证；联调显式 `UseCompletionSources=true`、`CompletionSourceRoot=/repos/focus/atelia-completion`。
- 不清理全局包缓存、不覆盖旧包、不改写旧 raw 历史、不读取或迁移永久角色实例作为快捷验证。
- 已存在的方案与索引修改属于本任务设计产物，保留并随实施维护。

## 验收账本

最终必须逐项对照方案 §10 的全部验收组，不以局部测试成功宣称 S1/S2/S3 完成。

### 首轮实施进度（未最终验收）

- P1–P4 与 Offline/CLI/Recap/H0 消费者已落初版；新旧测试正在一起迁移。P5 已将生产 feature binding 的纯生成包在同一 retry decorator，正在移除 TextExtractor 原有第二重试循环。
- 独立 review 已驱动修复：巨大 Retry-After 展示时间溢出、deadline 未接生产状态、connection 期限覆盖；Prepared 的工具来源完整批次验证；MoveRef 异常 poison；已知 CAS 冲突不 poison。
- 上游 review 另发现 shared model-capability preflight 最后 waiter 取消后未 drain，可能重叠请求；正在修复，首轮测试绿色不能代替此项验收。
- 环境默认 SDK 为 10.0.112，仓库固定 10.0.201。已从官方固定版本 tarball 安装至临时目录 `/tmp/galatea-dotnet-10.0.201.95kOZL`，未修改 global.json；后续本轮命令使用该目录内 dotnet。
- 上游首轮离线套件：831 passed / 1 Windows-only skip / 0 failed；随后仍有 review 尾修，须重跑。
- SessionJournal Debug 源码联调 build 成功（0 errors，上游原 XML 注释 warnings）。
- 首轮 Server build 暴露 TextExtractor 旧 Codex failure enum 依赖；作为删除双 retry owner 的自然尾修处理，尚不算 build 通过。
- UI 子包报告 Node 22/22；主线程最终仍需重跑受影响集合。
- 上游实现已提交 `e67129916380ce214e3f59c2c0b42b31f286ac1c`，独立包消费者测试补充提交 `0847bf37969cd01682affbc7d065901e353ba61b`。没有公开推送/发布或真实实例切换。所有 .NET 验证串行排队。
- 上游第二轮 841 passed / 1 Windows-only skip / 0 failed；Offline Debug 30/30；主线程 Node SSE 文件通过。
- Journal 第一轮完成执行：549 passed / 7 failed。失败已分派：三个新测试并发打开同一 RBF；一个删除 Started 后的精确 header visit 变化；两项旧测试需在故障后重开；一个真实 CAS 包装错误识别遗漏。修复后必须重跑，不能将此轮称绿。
- P5 review 发现 attach 中不可取消的无限 feature retry：已移出 attach，并新增可见、可停止且关联 shutdown 的瞬态 admission operation。此新接缝仍需编译和故障验收。
- Journal 修复后完整 Debug 套件 **556/556**，TRX：`/tmp/galatea-retry-test-results/retry-core-full.trx`。
- 上游唯一开发包 `0.1.0-dev.20260916104416` 已冻结，来源 `0847bf37969cd01682affbc7d065901e353ba61b`；feed：`/tmp/completion-retry-package.i3O3W9/feed`，该目录 manifest 记录四包 SHA256。独立 `PackageSmoke` / `DiagnosticsOnly` / `AbstractionsOnly` 消费者验证通过，证据：`/tmp/completion-retry-package.i3O3W9/consumer/package-smoke-results.json`。Atelia 自身包模式仍待验收；未公开发布。
- HistoryTimeline Debug **207/207**、RecapGrid.Runtime Debug **96/96**、全部 Node **23/23**。CLI 修复旧 Started 断言后复测 **162/162**，TRX：`/tmp/galatea-retry-test-results/retry-cli-full.trx`。
- 使用临时 Git index 纳入新文件后的 scoped 文档检查：56 files / 0 diagnostics；实际 Git index 未因此改动。

### 独立验收审计后仍需补齐

- Host 双工具之间 Stop，以及已执行工具之后的生成断流重试，必须实际证明工具仅执行一次。
- Note/邮件暂时故障后成功的 durable settlement 与冷重开，不以直接调用 wrapper 的测试替代。
- 生产 observer 的半个 think 标签/partial tool 失败后重试，验证真实 SSE/replay 与 filter 隔离。
- players=[] 的 pending 冷启动恢复、其他角色不被退避阻塞、maintenance 新端点拒绝。
- Host 提交不确定的联合恢复、完整宿主回归、明确版本 Atelia 包消费与隔离旧历史验收。

以上为未完成清单，不是已通过声明；已经交给有界测试包补充。

新增纵向测试已写入（尚待实际执行）：`GalateaRetryPreviewTests`、`GalateaRetryIsolationTests`、
`GalateaFeatureRetrySettlementTests`、双工具三轨迹与 Host Action/TurnEnded 发布故障。
Galatea 首次 targeted 73 passed / 11 failed，全部为可选 timeout map 默认序列化成 null 与 strict
reader 不一致；已为 DTO 加省略 null 的序列化合同，并补 bootstrap roundtrip，正在扩大范围复测。

扩大 targeted：92 passed / 4 failed；两项 feature 测试发现 production components 未将宿主
TimeProvider 传给 feature owner（已修），另两项是 synthetic home allowedRoots 与 fresh 无工具
的 fixture 设置错误（已修、待复测）。完整 Host 非 live 套件正在执行，尚不能宣布通过。

新增必补 S2 接缝：Recap maintenance route 仍直接借 raw registry client，503 会经 Runtime/Manager
返回欠债并使 Host 暂停；route 的整体 dispatch timeout 也会截断内部重试。正在按窄 maintenance
invoker 接缝实现单次期限移交，不改主角色 registry binding，不重试 Manager/PutCell settlement，
不把逻辑 work dispatch 的预算计数说成物理 provider attempts/费用上限。

完整 Host 首轮 **1254 passed / 28 failed / 1282 total**（排除三类 opt-in live），TRX：
`/tmp/galatea-retry-test-results/retry-host-full.trx`。按失败证据拆包尾修，未降低生产校验：
legacy synthetic fixture projector、只读 audit 使用时机、旧 interval/人工恢复断言、启动 busy 的
明确未接纳重试、固定 API 字段/端点闭集等。

该轮还发现真实 frozen 工具恢复缺口：原 Prepared 成功后 Engine 立即续接工具与下一生成，
但 frozen runtime 没有后继 context source。修复采用共享同一生成内核的窄
`ResumePreparedCompletionToBoundaryAsync`，仅提交原 Prepared 的 Action；Host 随后在同一写权下
重新检查边界，进入既有工具续接与后继 maintenance/runtime 绑定。不把 null candidate 换成未经
维护的当前上下文，也不把整个工具循环放进 retry。

窄 boundary API 加入后的 Core 复测 **557/557**、PublicSurface **4/4**。Recap maintenance 窄
deadline-owner 接缝已实现并通过只读 review，新 Runtime/Hosting/Host 验收正在排队。

## 恢复执行后的验证

- Recap Runtime **96/96**、Hosting **39/39**、Hosting.PublicSurface 修复反射合同后 **7/7**。
- Host 复测 **1288/1291**；三项为旧错误码、从旧 Failed 启动 normalization 的错误 fixture、
  cold attach 短暂占锁的单次 GET 断言。已保留原语义约束修正，正在再次完整运行。
- 独立安全复核确认新 SSE/API/retry 日志没有传播 provider 全文；但发现上游通用 HTTP 异常
  diagnostic 从旧 512 字符扩大为全文。已要求恢复有界 excerpt，完整 body 仍只用于结构化 code
  解析。此尾修将独立提交并产新唯一版本包，旧候选不会覆写；最终包消费必须使用新候选。
- 为 Atelia 包消费准备独立缓存及配置目录 `/tmp/atelia-retry-consumer.3weqAj`；尚未据此宣称包模式通过。
