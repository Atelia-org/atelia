# Diagnostics Debug 级文件默认落盘（单门控）改造方案

状态：**设计方向已与用户对齐（2026-09-21），待实施**。本文档是跨仓实施入口，自包含全部背景与决策，实施时无须原会话上下文。带队方式沿用 two-layer：主线程统筹，subagent 分包执行。

## 0. 快速上下文（为压缩后的会话准备）

- 两个本地仓：`/repos/focus/atelia`（消费方主仓）、`/repos/focus/atelia-completion`（上游）。
- 上游 main：`0d6e832c73f8f03d5a94ab4d7a72246ad2299b3f`（含 OpenAI chat trailing usage 修复）。
- atelia 的 Git pin：`0.1.0-preview.2`（`eng/CompletionDependency.props`，对应上游 `8828cea`）。
- **当前工作区处于本地 feed 包模式**：`eng/CompletionDependency.Local.props`（gitignored）指向 `0.1.0-dev.20260921093903`（= `0d6e832`）。三态切换与接入新 dev 包的流程见 [Completion 依赖](../completion-dependency.md) 的"长期本地模式与切换备忘"。
- 基线（用 `0.1.0-dev.20260921093903` 验证过）：MemoPod.Tests **284/284**；Galatea.Server.Tests **1296 passed / 1 skipped / 0 failed**；Hosting.PublicSurface **7/7**；Galatea.Server Release 构建 0 警告 0 错误。若切回公开 pin（preview.2），MemoPod 的 `ProviderFreeSliceUsesDeepSeekWireUsageAndHydration` 为已知红（trailing usage 修复未随 preview.2 发布）。

## 1. 背景与已定决策

### 1.1 起因：content-free 审计

preview.2 迁移（atelia commit `3e02df48`）后的 review 发现若干日志内容问题：

- Debug 级（opt-in）：`GalateaServices.cs:2117` RunTurnAsync 的 `input={Preview(...)}`；`GalateaUserMessageNormalizer.cs:135-141` 的 before/after Preview；`GalateaServices.cs:808` 经 `DescribeTurn`（3934-3937）同时输出用户与 assistant 文本 Preview；`GalateaServices.cs:2700` durable memo 诊断 JSON 的 `exactText` 全文；`GalateaServices.cs:3642` 的 `sessionDir` 文件路径。
- Warning 级（默认可见）：`GalateaUserMessageNormalizer.cs:117-120 / 126-129 / 151-154` 三处的 `input={Preview(userMessage)}`。

### 1.2 用户决策（2026-09-21）

1. **Debug 级与常规运行日志是不同类别**：Debug 是开发期临时诊断；`[Conditional("DEBUG")]` 已是消费方编译期门控，**落盘时不应再过滤**。现状（preview.2 文件默认 `WARNING`）是双重门控，造成真实摩擦（Galatea README 开发命令必须带两个 env var）。
2. 连锁简化：Debug 级内容规则**放宽**（自己的数据、自己的机器）；`Preview(120)` 只是可读性选择而非规则；**exactText 拆双通道方案取消**（`DebugUtil` 自身已做 2048 截断 + 单行化；权威数据在 MemoPod/SessionJournal；测试断言走内存 sink，不受影响）。上节列出的 Debug 级内容项**全部保持现状，不改代码**。
3. **Warning/Error 维持 content-free**：真正要修的只有 normalizer 三处 Warning 的 `input Preview`（失败事实已由 `termination.Kind` / `providerReason` / `exceptionType` 携带）。

### 1.3 与上游既有设计的关系（论证要点）

`debug-util-redesign.md` 第 15 行否决的是**包自身 `#if DEBUG`** 作为默认机制（包以 Release 构建，下游 Debug 编译无法改变包内 `#if` 结果），由此确立"默认值必须固定"原则。文件默认 `WARNING` 是与控制台决策（第 22 行）统一沿用的，无独立论证。而 `[Conditional("DEBUG")]` 是**消费方**编译期门控：

- **Release 消费者**：Debug 调用根本不存在，文件默认改 `DEBUG` 是严格 no-op；
- **Debug 消费者**：开发期诊断自动留档，正是"开发期临时诊断"想要的语义。

因此"文件默认 `DEBUG`"不违反固定默认原则，只是把双重门控收敛为单一门控。

## 2. 目标语义合同（Rule-Tier）

| 级别 | 编译行为 | 文件 sink 默认 | 控制台 sink 默认 | 内容规则 |
|---|---|---|---|---|
| `Debug` | `[Conditional("DEBUG")]`，Release 调用点零开销 | **`DEBUG`（写入）** | `WARNING`（不写） | 放宽：开发期临时诊断，宿主可记录自己的数据；卫生由 `DebugUtil` 保证（2048 截断、单行化） |
| `Warning` | 始终编译 | `WARNING`（写入） | `WARNING`（stderr） | 必须 content-free（无凭据/正文/provider 原文/文件路径/堆栈；异常只写 `exceptionType`） |
| `Error` | 始终编译 | `WARNING`（写入） | `WARNING`（stderr） | 同上 |

env 覆盖语义不变：`ATELIA_DEBUG_FILE_LEVEL` / `ATELIA_DEBUG_CONSOLE_LEVEL` 接受 `DEBUG`/`WARNING`/`ERROR`/`OFF`，非法值回退默认，显式设置优先。

## 3. 工作包拆分（Plan-Tier）

### WP-U1（上游）：DebugUtil 文件默认 DEBUG + 测试宿主 + 文档

- **写入范围**：`src/Diagnostics/DebugUtil.cs`（`_fileLevel` 默认 `DebugLevel.Warning` → `DebugLevel.Debug`；`_consoleLevel` 不动）、`tests/Completion.Tests/DebugUtilPublicSurfaceTests.cs`、`tests/Completion.Tests/TestHostDiagnostics.cs`、`docs/Diagnostics/README.md`、`docs/Diagnostics/debug-util-redesign.md`（补决策附录，见 DP-3）。
- **TestHostDiagnostics**：现仅显式设 `ATELIA_DEBUG_CONSOLE_LEVEL=Error`。文件默认改 `DEBUG` 后，Debug 构建的测试运行会在测试 cwd 写 Debug 日志；按 DP-2 显式设置文件级别。
- **README**：配置表文件默认改 `DEBUG` 并补理由（`[Conditional]` 为消费方门控；Release 消费者 no-op；文件=留档记录、控制台=实时视图，两者默认分开）。
- **验证**：上游 `Completion.Tests` 全绿；新增/更新默认值断言（文件默认 DEBUG、控制台默认 WARNING、env 覆盖、非法值回退）。
- **完成定义**：上游窄提交，测试与文档同步收口。

### WP-A1（atelia，可与 WP-U1 并行）：normalizer 三处 Warning 删 input Preview

- **写入范围**：`prototypes/Galatea/GalateaUserMessageNormalizer.cs` 仅 117-120 / 126-129 / 151-154 三处文本（删 `input={Preview(...)}` 段，其余事实保留）；`Preview` 方法与 Debug 级调用（135-141）不动。
- **前置检查**：`rg "Input normalization" tests/` 确认无测试断言这些日志文本。
- **验证**：`dotnet build prototypes/Galatea/Galatea.Server.csproj -c Debug -m:1 -nr:false`（当前 local feed 模式下隐式生效）+ Galatea.Server.Tests 全量。

### WP-U2（上游）：打新 dev 包

- 沿用既有惯例：唯一版本 `0.1.0-dev.<timestamp>`，产物 `artifacts/feed-<version>/`（四包 nupkg/snupkg + manifest SHA256），源码 revision 写入 manifest。

### WP-A2（atelia）：消费验证 + 文档同步

- **接入**：按 [Completion 依赖](../completion-dependency.md) "接入新 dev 包"流程——复制四组包与 manifest 进 `gitignore/completion-local-feed/`，更新 `eng/CompletionDependency.Local.props` 的版本与 revision，`dotnet restore Atelia.sln` 统一切换。
- **验证**：MemoPod.Tests 284/284；Galatea.Server.Tests 1296/1skip/0；Hosting.PublicSurface 7/7；Galatea.Server Release 构建 0 error；**开发工作流验收**——Debug 构建 `dotnet run` 不带任何 env var，`.atelia/debug-logs/` 出现 Debug 日志且 stderr 正常路径安静。
- **文档**：AGENTS.md DebugUtil 节（分层规则 + 文件默认 DEBUG）；`prototypes/Galatea/README.md:123-131` 开发命令去掉两个 env var 并更新语义描述；`docs/completion-dependency.md` "调用与调试边界"节同步。
- **完成定义**：atelia 窄提交（WP-A1 可并入或独立提交）。

## 4. 验收矩阵

| 场景 | 预期 |
|---|---|
| Release 消费者，无 env | 与现状完全一致（无 Debug 调用；文件/控制台 Warning+） |
| Debug 消费者，无 env | Debug+ 自动写入 `.atelia/debug-logs/{category}.log`；控制台仍 Warning+ |
| `ATELIA_DEBUG_FILE_LEVEL=OFF/WARNING/ERROR` | 正常压低文件输出 |
| `ATELIA_DEBUG_CONSOLE_LEVEL=DEBUG` | 单次运行开控制台实时视图 |
| 非法 env 值 | 回退新默认（文件 DEBUG、控制台 WARNING） |
| 上游测试宿主 | 文件级别按 DP-2 显式设置，测试输出目录不产生 Debug 日志噪声 |
| atelia 回归 | 三套件达基线计数；Release 构建 0 error |

## 5. 决策点（实施前快速确认）

- **DP-1 控制台默认维持 `WARNING`**（推荐）：重构原始痛点是控制台噪声；文件=留档、控制台=实时视图，角色不同默认分开。用户已明确认可"落盘不过滤"，控制台为推荐项。
- **DP-2 TestHostDiagnostics 文件级别**（推荐 `WARNING`，仅在外部未显式设置时）：保持其注释承诺（Warning 事实仍落盘），避免测试运行写 Debug 日志噪声。
- **DP-3 上游文档形式**（推荐在 `debug-util-redesign.md` 末尾补"2026-09-21 决策附录"）：记录单门控论证与本次修改，避免未来被当作漂移改回。

## 6. 风险与边界

- **Debug 日志自动持久化内容**（用户输入 Preview、memo exactText 等）：已接受——单用户实验项目、自己机器自己的数据；"日志贴给 AI 助手排障"被视为特性。仅当未来出现非开发部署使用 Debug 构建时再评估。
- **无 rotation / 跨进程一致性**：上游既有 deferred 项，本次不处理；个人开发机体量可接受。
- **发布节奏**：下一个公开包（如 preview.3）将同时携带 trailing usage 修复与本改动；atelia Git pin 维持 preview.2，dev 测试经 Local.props（CI 与新 clone 不受影响）。
- **不改清单**（防止后续 agent 重新翻案）：§1.1 列出的全部 Debug 级内容项、exactText 内存 sink 链路（`CharacterNoteRuntimeTests.cs:291-322` 依赖）、`Preview` 辅助方法。

## 7. 证据锚点

- 上游默认值现状：`src/Diagnostics/DebugUtil.cs` 的 `_fileLevel`/`_consoleLevel` 初始化；设计理由：`docs/Diagnostics/debug-util-redesign.md:15,22,49-52`；测试宿主：`tests/Completion.Tests/TestHostDiagnostics.cs`。
- atelia 违规点：`prototypes/Galatea/GalateaUserMessageNormalizer.cs:117-154`；内容项见 §1.1。
- 本地模式机制：`eng/CompletionDependency.Local.props(.template)`、`eng/NuGet.Completion.LocalFeed.config`、`gitignore/completion-local-feed/`（累积 feed）。
- 既有复测基线证据：dev `20260921093903` feed 与 manifest 位于 `/repos/focus/atelia-completion/artifacts/feed-0.1.0-dev.20260921093903/`。

## 8. 执行约定

- 主线程先确认 DP-1/2/3，再分包；WP-U1 与 WP-A1 可并行（不同仓），WP-U2→WP-A2 串行。
- 重型 .NET 命令一律 `-m:1 -nr:false`；测试追加 `-- xUnit.MaxParallelThreads=4`；上游与 atelia 各自窄提交，atelia 提交排除用户未提交的 `docs/Galatea/recap-grid-forward-policy-*.md`。
- 禁区：不碰用户未提交文件、历史冻结 feed `gitignore/completion-packages/0.1.0-dev.20260916114103/`、`eng/NuGet.Completion.Local.config`（历史试运行入口）。
