# Recap 运维入口与诊断收口

> 状态：实现、独立审阅与验证完成；2026-09-14。基线 `3da6a6f4`，代码提交 `61283924`。
> 来源：唯一 Dev 数据升级与真实重建后的操作复盘；用户已批准设计与实施。

## 1. 目标与事实

普通 Recap 构建应直接消费 Galatea 已有的 route 和 connections 文件，通过正式 CLI 执行。
操作人员应能判断当前步骤、已完成工作和具体失败原因，无须编写临时 runner、查看 SQLite 或轮询日志文件大小。

前一轮真实重建共四次成功模型调用，生成两行四个 cell；遇到的入口问题都在调用前发生：

| 问题 | 已确认源码原因 | 本轮处置 |
|---|---|---|
| route 尾换行被 CLI 拒绝 | BuildAsync 使用 DecodeCanonical，Galatea 已使用 ParseJson | 人工配置入口复用 ParseJson，保留严格字段和边界验证 |
| 现有 connections V3 无法使用 | build 读取要求 defaultConnectionId 的 V2 file | build 直接使用 V3 catalog，由 exact route 决定连接 |
| Codex 连接无法构造 | CLI 只装配 DefaultCompletionClientFactory | 共享订阅环境装配，CLI 按需创建 |
| executor-rejected 只有计数 | 匿名对象内 result 的声明类型为基类，丢失派生字段 | 按实际结果类型输出，覆盖最终 JSON 验证 |
| 客户端构造原因不明确 | Hosting 将异常缩减为类型名 | 保留具体异常消息与底层原因 |
| 长请求期间缺少信息 | telemetry 和 call log 主要在完成时产生 | 提供 stderr 即时进度，stdout 保留最终 JSON |

Timeline、Store、Control 的持久格式和构建语义不变。真实数据已经完成切换，本轮不重复清空或重建。
不增加迁移框架、持久任务系统或自动重试。

## 2. 配置与客户端装配

`recap-grid build --connections` 明确读取 Completion V3 catalog，不再要求虚构的默认连接。
使用已有 catalog registry 和 `RecapGridCompletionHost.CreateBorrowingRegistry`，不增加格式猜测或 V2 fallback。
`run-online-turn`、`llm-smoke` 等其他命令仍保留各自 V2 文件合同，不因本切片整体改写。

人工 route 文件使用已有 `ParseJson`：允许空白、换行与属性顺序；重复、缺失、未知字段及无效 route 继续拒绝。
canonical 持久数据的 `DecodeCanonical` 不放宽。

Completion 提供共用 Codex subscription 环境装配，复用既有 credential provider/factory。
Galatea 保留当前启动时的配置验证行为；CLI 生产入口使用延迟 factory，首次实际创建 Codex client 时才读取环境。
非 Codex 连接沿普通 factory，`MainCore` 的显式注入不被覆盖。
无缺失工作、provider-free 命令与零调用预算重开不额外要求订阅环境或读取认证文件。
这不等于跳过配置验证：build 仍校验整个 catalog 的普通 `baseAddressEnv/apiKeyEnv`；
纯状态检查使用不读取 routes/connections 的 `progress`。本轮不增加第二套延迟配置解析器。

## 3. 错误与进度

最终 build JSON 保留派生结果的全部业务字段：Code/Detail、Failures、预算种类、Proof/Receipt、
实际 stale head 与 settlement 信息。修复报告边界，不为此修改持久结果或加入通用多态协议。
客户端构造和 route 加载错误保留具体原因；沿已有消息长度边界处理，不只返回异常类型。
telemetry 经 JSON 转义后若撑大报告，优先省略 evidence 并报告省略数量，保留业务结果和原 exit code。
不让附属诊断信息把真正的错误或 settlement 状态替换成“报告过大”。

必须区分客户端构造、Runtime invoker 执行、模型完成和 Store 提交。
`NewCalls == 0` 或空 telemetry 不能单独证明整个构建没有发生外部调用；尤其 executor 抛异常时计数可能尚未汇总。
请求开始仅表示进入本地调用边界，不声称服务器已接收；明确未调度与结果不确定仍沿现有业务结果解释。

进度写 stderr，最终结构化结果只写 stdout。至少提供当前行/列、连接别名与模型、调用开始、等待耗时、
调用结束和行结果提交。并发时按工作身份关联；不把模型完成当作数据库已提交，也不靠重复扫描或调用模型获取进度。
进度属于 best-effort 操作信息，输出异常不得改变 Store 写入、模型结果或原有取消/不确定语义。

实现接口限定为：Runtime 在既有 telemetry seam 增加 `completion-started`，保留 `completion-settled`；
Hosting 的 borrowing-registry 入口可接收 live sink，最终有界 evidence 仍只收 settled；
Manager 的单次 BuildAsync 可接收行结果确认 callback，不持久化观察者状态。
CLI 使用单个 scoped writer/timer，默认每 30 秒报告已开始但未结束的调用，结束时释放计时器。
stderr 前缀为 `[recap-build]`，事件为 `request-start`、`waiting`、`request-end`、
`row-committed`、`row-existing` 和 `summary`。不预扫描来猜总调用数。

## 4. 工作包与验收

| 工作包 | 范围 | 验收 |
|---|---|---|
| A 入口 | Completion 装配、Galatea 复用、CLI catalog/route | 原配置形状可读；factory 按需装配；注入与冷开边界不变 |
| B 诊断 | CLI 结果输出、Hosting 错误 | 最终 JSON 包含派生字段；构造失败与调用后失败可区分 |
| C 进度 | Runtime、Manager、Hosting 与 CLI stderr | 执行未结束时可见；等待与完成准确；观察者异常不影响结果 |
| D 集成 | 正式 CLI + gated fake provider | stdout 唯一 JSON；慢调用即时可见；冷重开零 client/零调用/零写入 |

主线程冻结跨包边界、审阅接缝与维护文档；工作包先独立分析，再实施，最后交叉审阅与尾修。
同文件有不同方法的包由主线程协调提交，避免混入其他包的未完成工作。

验证串行执行相关 Completion、CLI、Hosting、Runtime、Manager、Galatea 测试及 public surface，
使用 `--no-restore -m:1 -nr:false` 和 `xUnit.MaxParallelThreads=4`。
并行工作包通过 `flock --close /tmp/atelia-recap-ops-dotnet.lock` 串行化 .NET，防止编译器继承锁。
本轮采用 fake provider 验证，不需要再次调用真实 LLM；不以完整既有测试数代替实际执行记录。

## 5. 实施记录

代码与测试提交为 `61283924`。A/B/C 三包均经过独立只读审阅，D 用正式 CLI 入口独立验收；
主线程复核共享文件、最终输出及持久结果边界。实现如下：

- 共用 `CodexSubscriptionCompletionClientFactory.CreateFromEnvironment`；Galatea 去掉重复环境解析，
  CLI 使用 `CliCompletionClientFactory` 延迟装配。build 直接读取 V3/人工 route，借用既有 registry host。
- `PrintBuildResult` 显式按派生结果类型输出；实际编码后的报告过大时，省略 evidence 并附
  `evidenceOmitted/evidenceOmittedEventCount/evidenceDroppedEventCount`，保留业务 status、result 与 exit code。
- `RecapGridHostingDiagnostics` 在 4 KiB UTF-8 边界内保留异常类型、消息与 inner cause。
- `LiveRecapCompletionTelemetry` 分送即时观察和原 settled evidence；
  `RecapGridRowCommitProgress` 只在 Store winner 及 assignment 校验成功后通知。
  `RecapGridBuildProgressWriter` 输出即时进度及连接别名，终止等待循环后输出 summary。

审阅期间补齐了大 evidence 不得覆盖业务错误的测试，明确了普通 catalog 环境验证与订阅 lazy 装配的不同边界。
最后修正 Hosting public-surface 的旧签名断言，并增加 liveTelemetry 末参数可选、默认 null 的验证；
没有为通过测试恢复旧重载或放宽 Store/Runtime 语义。

### 5.1 验证结果

最终 **14 个项目、2,246 项通过、0 失败、1 跳过**；所有最终 test logs 均无 warning。
唯一跳过为 Completion 的 Windows sharing-violation 专用测试，当前环境为 Linux。

| 测试项目 | 通过 |
|---|---:|
| Completion | 818 |
| SessionJournal.Cli | 157 |
| RecapGrid.Manager | 89 |
| RecapGrid.Runtime | 73 |
| RecapGrid.Hosting | 37 |
| RecapGrid.Online | 33 |
| RecapGrid.WalkingSkeleton | 27 |
| Galatea.RecapGrid | 9 |
| Galatea.Server | 985 |
| 五个相关 public-surface 项目 | 18 |

public-surface 分项为 Manager 3、Runtime 4、Hosting 7、Online 3、Galatea.RecapGrid 1。
CLI 在连接别名尾修后再次全量通过；不将尾修前的结果冒充最终验证。

测试统一使用 §4 命令选项并清除 `ATELIA_RUN_*` opt-in；Completion 额外过滤 `Category!=LiveE2E` 及
`OpenAICodexResponsesLiveTests`、`OpenAICodexReasoningReplayLiveTests`、`OpenAIResponsesLiveTests` 三类。
Server 精确排除 `CharacterNoteTranscriptionLiveTests`、`GalateaCodexDelegationLiveTests`、
`GalateaScenarioLabLiveTests`，没有笼统排除名称包含 Live 的正常测试。

两个独立 build 均为 0 warning、0 error：`prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj` 与
`prototypes/Galatea/Galatea.Server.csproj`，使用 `dotnet build --no-restore -m:1 -nr:false`。
`python3 scripts/check_session_journal_docs.py` 为 41 files、0 diagnostics；`git diff --check` 通过。

关键用例包括：gated fake provider 运行期间 stderr 已可读、stdout 仍空；完成后整段 stdout 是单份 JSON；
冷开零调用且 Store/Control/Timeline/Journal 不变；构造失败与调用后失败可区分；16 个并发进度关联；
取消、closed stderr、观察者抛错、零列行、已有 winner 与提交不确定均不产生虚假成功。
最终临时汇总为 `/tmp/recap-ops-validation-final.json`，日志为 `/tmp/recap-ops-final-*.log`；
可复现的测试源已随代码提交。

本轮未调用真实 LLM、未清空或修改真实 `.atelia`、未启停 Dev 服务、未 push。
原来的 Dev 数据升级和真实重建记录仍在 [Timeline 实施记录 §8.4](timeline-row-identity-simplification-plan.md#84-唯一-dev-数据升级与真实重建)。
