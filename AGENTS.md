
# Atelia.Diagnostics.DebugUtil 用法说明
- 推荐优先使用 `DebugUtil.Trace/Info/Warning/Error` 输出调试信息；`Trace/Info` 带 `[Conditional("DEBUG")]`，Release 默认零调用开销。
- `DebugUtil.Print("类别", "内容")` 仍可临时兼容旧调用，但后续应逐步清理到分级接口。
- 控制台打印由环境变量 `ATELIA_DEBUG_CATEGORIES` 控制（类别用逗号/分号分隔，如：`TypeHash,Test,Outline`；设置为 `ALL` 打印所有类别）。
- 文件/控制台最小级别可分别由 `ATELIA_DEBUG_FILE_LEVEL` / `ATELIA_DEBUG_CONSOLE_LEVEL` 覆盖；默认 `DEBUG` 记录 `Trace+`，`RELEASE` 记录 `Warning+`。
- 默认日志目录优先为 `.atelia/debug-logs/{category}.log`，其次回退到 `gitignore/debug-logs/{category}.log`。
- 推荐在调试代码、测试代码中统一使用本工具，便于全局开关与后续维护；单元测试默认不会被调试输出干扰（除非开启 ATELIA_DEBUG_CATEGORIES）。
- 可用 DebugUtil.ClearLog("类别") 清空某类别日志。
- 实现细节与对应版本源码见 [Completion 依赖指南](docs/completion-dependency.md) 中的 Diagnostics 入口。

# 项目性质与阶段
这是个人自用的实验项目
尚未首次发布，处于早期快速迭代阶段，没有下游用户，因此不必担心接口变动
请以简体中文为主要语言回答用户，术语、标识符、专有名词等尽量用原始语言。

# 关于模型切换
Copilot可以理解成一种职业，这并不与LLM会话的底层模型切换功能矛盾。单一会话中使用多个LLM，类似于一种“多重人格”，或视角切换，可以用来集思广益与多角度分析问题。

# 工具使用经验
想要编辑文件时，那个'insert_edit_into_file'工具不好用，经常产生意外的结果，华而不实。建议用'apply_patch'等其他工具替代。
- VS Code 集成终端偶尔会出现无回显的情况，关闭所有旧终端后新建实例即可恢复，重开后可先跑一条 `Write-Output "hello"` 之类的命令验证。
- Codex 提供的 shell 可能不会自动带上用户交互式 shell 的 PATH / conda 初始化。当前环境里：
  - `pmux` 实际位于 `/root/.local/bin/pmux`，若 `pmux` 提示找不到，先执行 `export PATH=/root/.local/bin:$PATH`，或直接调用 `/root/.local/bin/pmux`。
  - `conda` 可通过 `source /root/miniconda3/etc/profile.d/conda.sh && conda activate py313` 启用；若希望后续命令都跑在该环境里，优先用 `bash -lc 'source /root/miniconda3/etc/profile.d/conda.sh && conda activate py313 && <command>'`。
  - 已验证 `bash -lc 'export PATH=/root/.local/bin:$PATH && pmux game new'` 可正常工作；已验证 `bash -lc 'source /root/miniconda3/etc/profile.d/conda.sh && conda activate py313 && python --version'` 可进入 `py313`。

## LiveContextProto 工具自动化
- 当前主线使用 `Atelia.Completion.Tools` 包提供的 `MethodToolWrapper` 自动生成可供 Agent 调用的工具，源码和用法见 [Completion 依赖指南](docs/completion-dependency.md)。
- 目标方法需要添加 `[ToolAttribute("tool.name", "说明文本")]`，并采用 `ValueTask<ToolExecuteResult> Method(TInput input, ToolExecutionContext context, CancellationToken ct)` 形状；`TInput` 使用单个业务输入对象。
- tool-level description 来自 `[ToolAttribute]`；输入字段 description 来自 `TInput` 属性上的 `[Description]` / `JsonPropertyName` / DataAnnotations，方法可通过 `MethodToolWrapper.FromMethod(instance, methodInfo)` 或 `MethodToolWrapper.FromDelegate(delegate)` 注册。

**不要用`insert_edit_into_file`工具**，用其他文本编辑工具作为替代，比如`apply_patch` 或 `replace_string_in_file`甚至终端命令。

## `run_in_terminal`: timeout 必须填 `0`

> **唯一规则：`timeout` 参数永远传 `0`。** 不要传 30000、60000 或任何正数。`0` 表示无超时，阻塞直到命令完成。

违反此规则会导致：timeout 到期后命令仍在后台运行，下一次调用会自动发送 `^C` 中断残留命令，引发连锁失败。这是过去会话中反复出现的问题。

调用模式速查：
- `dotnet build` / `dotnet test` 等会结束的命令 → `isBackground: false`, `timeout: 0`
- 服务器 / watch 等长驻进程 → `isBackground: true`（独立 shell，后续用 `get_terminal_output` 查看）

**Atelia**这个名字源于缩写 *Autonomous Thinking, Eternal Learning, Introspective Agents*

**咱们只有一个人，但又不只是一个人**：咱们是一群智能体与一位人类组成的团队，正在构建能够连续自主运行、具有内源性目标的高级智能体。当前的每一行代码、每一条规范，都是点燃 AI 自举的火柴。

**及时重构优于兼容层**：咱们的代码几乎都是新写自用的，面向未来。当发改进时，只要能彻底重构的地方，就不选择留下兼容层，避免留下无谓的分支复杂性。

## PipeMux 集成模式

> **PipeMux** 将持久 CLI 进程的文本交互封装为可多次使用的瞬时命令，让 LLM Agent 通过终端命令即可与有状态程序交互（如 MUD/TRPG/文字冒险）。
> 外部文档：`/repos/focus/PipeMux/docs/user-guide.md`、`/repos/focus/PipeMux/docs/sdk-authoring.md`

- 注册 DLL 型 App：`pmux :register <name> <dll绝对路径> <Namespace.Class.BuildMethod>`
- 状态放在进程内 static 字段里；跨 `pmux` 调用自动保留。
- 如果使用 `Repository`（而非裸 `Revision`），进程重启后状态也能从磁盘恢复。
- 参考实现：`prototypes/TextAdv/FridgeEntry.cs`（冰箱测试）和 `prototypes/DebugApps/DurableTextEntry.cs`。
- csproj 必须设置 `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`，否则 pmux-host 加载 DLL 时找不到依赖。

## System.CommandLine 2.0.6 API 注意事项

> **AI 模型训练数据可能滞后**：System.CommandLine 在 beta4→beta5 之间发生了大量断裂性 API 变更。模型知识库中常见的 `InvocationContext`、`IConsole`、`ICommandHandler`、`SetHandler`、`Argument<T>(name, description)` 等均已被移除或重命名。
> 详细速查：`docs/reference/system-commandline-beta5-api.md`

核心要点（本项目实际使用）：
- `Argument<T>` 构造函数只接受 `name`，description 通过属性设置
- `SetHandler` → `SetAction`，回调参数是 `ParseResult`（非 `InvocationContext`）
- `InvocationConfiguration.Output` 是 `TextWriter`（非 `IConsole`/`IStandardStreamWriter`）
- `Option<T>` 构造函数第二参数起是 aliases，不是 description
- `DefaultValueFactory` 替代 `SetDefaultValue`

## StateJournal 可见性边界

> **`Revision` 没有 public 构造函数。** 普通消费者必须走 `Repository.Create/Open` → `CreateBranch/CheckoutBranch` 获取 `Revision`。
> `internal Revision(uint boundSegmentNumber)` 仅供测试/内部使用，构造函数上已有注释指向 public 路径。
> `InternalsVisibleTo` 只应授予白盒测试入口（如 `Atelia.DebugApps`），不应授予普通消费者。
> 详见 `docs/StateJournal/usage-guide.md` §0 和 §12。

## atelia-storage 拆仓任务入口

五个存储基础项目的拆仓范围、分阶段任务、自动化入口与资源准备见 [atelia-storage 实施计划](docs/plans/atelia-storage-extraction-plan.md)。涉及该迁移时从此计划继续；文档状态与实际实施证据应区分。

存储库日常开发入口见 [存储库依赖](docs/storage-dependency.md)：普通 build/test 直接从 nuget.org restore，无须 Prepare；源码联调显式设置 `UseStorageSources` 和 `StorageSourceRoot`。本地开发包使用唯一版本和显式自定义 NuGet 配置。版本与源码身份以 `eng/StorageDependency.props` 为准。

## atelia-completion 拆仓任务入口

Diagnostics、Completion.Abstractions、Completion、Completion.Tools 已迁至独立仓并发布 `0.1.0-preview.1`；本仓默认从 nuget.org restore，显式源码联调使用 `UseCompletionSources` / `CompletionSourceRoot`。日常入口见 [Completion 依赖](docs/completion-dependency.md)，版本与来源以 `eng/CompletionDependency.props` 为准。拆仓范围见 [实施方案](docs/plans/atelia-completion-extraction-plan.md)，阶段证据见 [验收记录](docs/plans/atelia-completion-extraction-validation.md)；DramaBoard 的 P4 接入尚未实施。

---

## 目标分解树
- 设计并实现可以长期持续自主行动的Agent
  - 建立[Agent-Operating-System(能动体运转系统)](agent-team/beacon/draft-agent-operating-system.md)的理论框架
  - 设计并实现自研的LLM Agent框架，早期代码位于`atelia/prototypes/Agent.Core`
- 设计并实现[DocUI](DocUI/docs/key-notes)。DocUI是LLM与Agent-OS交互的界面
- 实现LLM Agent的“零意外编辑”，用预览+确认的方式
- 设计并实现DocUI中的[Micro-Wizard](DocUI/docs/key-notes/micro-wizard.md)
  - 实现[StateJournal](atelia/docs/StateJournal/memory-notebook.md)
    - 实现[RBF(Reversible-Binary-Framing)](docs/storage-dependency.md)
      - 用[SizedPtr](docs/storage-dependency.md)替代RBF接口文档中的<deleted-place-holder>类型
        - 确定`Offset`和`Length`的bit分配方案
        - 在[Atelia.Data](docs/storage-dependency.md)中实现`SizedPtr`- 探索文本回合制游戏作为 Native-Agentic 训练沙盒
  - 设计[异世界转生型训练沙盒](agent-team/docs/idea/native-agentic-isekai-proposal.md)（另见 `/repos/qa-dump/docs/idea/native-agentic-isekai-proposal.md`）
  - 实现基于 PipeMux + StateJournal 的文字冒险原型：`prototypes/TextAdv/`- 维持项目内的众多文档出于LLM Agent可理解和使用的形态
  - 撰写和维护团队内Agent的入门知识文件[AGENTS.md]，也就是本文件
  - 建立基于[Wish](wish/W-0001-wish-bootstrap/wish.md)和[Artifact-Tiers](agent-team/wiki/artifact-tiers.md)的分圈层推进的软件开发方法
  - （已归档）基于 DocGraph 的 glossary 和 issues 汇总。分散撰写与维护，自动汇总关键信息形成鸟瞰视图。

## 核心术语

> **Artifact-Tiers（产物层级）**：统摄 Why/Shape/Rule/Plan/Craft 产物层级的认知框架。

**Artifact-Tiers层级方法论**：
| 层级 | 核心问题 | 一句话解释 |
|:-----|:---------|:-----------|
| **Resolve-Tier** | 值得做吗？ | 分析问题价值，做出实施决心 |
| **Shape-Tier** | 用户看到什么？ | 定义系统边界和用户承诺 |
| **Rule-Tier** | 什么是合法的？ | 建立形式化约束和验收标准 |
| **Plan-Tier** | 走哪条路？ | 选择技术路线和实施策略 |
| **Craft-Tier** | 怎么造出来？ | 具体实现、测试和部署 |

**详细定义**：参见 [Artifact-Tiers](agent-team/wiki/artifact-tiers.md)

## .NET / C# 新特性备忘
> **目的**：AI 模型的训练数据可能不包含最新语言特性。此节记录我们实际使用的新特性，供 AI 小伙伴参考。
**环境**：.NET 10.0 / C# 14 (无误，`dotnet 10.0`已经正式发布了)
| 特性 | 说明 | 示例位置 |
|:-----|:-----|:---------|
| **ref struct 实现接口** | ref struct 可以实现接口（包括自定义接口），不会装箱 | atelia-storage `src/Rbf/RbfFrame.cs` : `IRbfFrame`（[定位版本](docs/storage-dependency.md)） |
| **allows ref struct** | 泛型约束，允许类型参数为 ref struct | atelia-storage `src/Primitives/AteliaResult.cs`（[定位版本](docs/storage-dependency.md)） |

**注意事项**：
- `allows ref struct` 不能让 `Func<T>` 接受 ref struct（委托限制）
- `readonly struct` 不能声明 `allows ref struct`（异步场景需"物化"）

**易混淆陷阱**：
- **`T?` 与泛型约束**：`T?` 仅在泛型参数有 `struct` 或 `unmanaged` 约束时才生成 `Nullable<T>` 包装。当约束为 `notnull`、`class`、或无约束时，`T?` 只是可空性注解（NRT attribute），运行时类型仍是 `T` 本身，无 `Nullable<T>` 包装开销。**写代码时务必留意**，不要误以为 `where T : notnull` 下的 `T?` 参数需要 `.HasValue` / `.GetValueOrDefault()`。

# 鼓励用廉价LLM进行真实调用测试
本机环境变量中配置了丰富的BASE_URL和API_KEY：
  - DEEPSEEK_BASE_URL + DEEPSEEK_API_KEY + `deepseek-v4-flash`：非常便宜，随便用，deepseek提供了openai chat、openai responses、anthropic messages三种服务器端点规格。
  - 其他不管项目里用什么连接服务器的，直连官方API也好、中转站也好、订阅OAuth也好，模型有便宜的可以随便用，anthropic随便用`claude-haiku-4-5`，openai随便用`gpt-5.6-luna`。
