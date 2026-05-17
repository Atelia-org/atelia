# Mutable Context Agent 原型递归分解计划

**状态**：Draft v0.1
**目标原型名**：`prototypes/MutableContextAgentProto`
**核心假设**：上下文不再等价于原始 message history，而是由可变的工作上下文投影生成。

---

## 0. 一句话目标

构造一个新的实验性 Agent 原型，用尽量少的历史包袱验证三件事：

1. 单条 `user` message 驱动 tool-loop 是否可行。
2. micro-wizard 能否让 Agent 在读取/编辑文本时只保留选择性记忆，而不把原始大文本长期留在上下文。
3. 渐进展开的 Memory Notebook 是否能成为代码理解与重构的高级工作记忆。

当前原型不追求通用 provider 适配，不复用旧 `Agent.Core` 的核心实现，优先降低工程摩擦与设计惯性。

---

## 1. 设计边界

### 1.1 固定模型端点

第一版只支持 DeepSeek V4：

- `DEEPSEEK_BASE_URL`
- `DEEPSEEK_API_KEY`
- `model-id`: `deepseek-v4-pro`
- API 形态：OpenAI Chat 风格 endpoint

不做多 provider 抽象，不做 Anthropic/OpenAI/本地模型兼容层。

### 1.2 双层上下文

原型内部应区分两种历史：

- `EventLog`：append-only，记录真实发生过的输入、模型输出、工具调用、工具返回、文件读写、错误，用于 debug、审计、复现。
- `WorkingContext`：mutable，保存当前任务、行动日志、已知事实、选择性记忆、临时视图、疑问、假设和纠错。

真正投给模型的是 `WorkingContext` 的渲染结果，而不是 `EventLog` 的逐条重放。

### 1.3 单 user message 投影

第一阶段只验证一种投影：

```text
role = "user"
content = """
你最初的目标是...

你近期的行动日志如下：
...

当前已知信息如下：
...

可用工具如下：
...

任务：请为了最初的目标，继续你的思路和行动。
"""
```

如果模型返回 tool call，则运行工具、更新 `EventLog` 与 `WorkingContext`，再渲染下一条单 `user` message。

### 1.4 不复用旧核心

可以借鉴旧代码思路，但第一版不直接引用：

- `Agent.Core`
- `Completion.Abstractions`
- `Completion.Tools`

非创新点可以写朴素版本，例如最小 HTTP client、最小 JSON schema、最小 tool dispatcher。

---

## 2. 递归拆解规则

每个单回合任务应满足：

- 有明确文件范围，最好只新增/修改少量文件。
- 有可运行的验收命令。
- 不依赖外部 LLM 时，至少有 smoke/fake 测试。
- 若依赖 DeepSeek，应提供 dry-run 或 mock 路径，避免 API 不可用时无法验证工程正确性。
- 不把“设计一个系统”作为单任务；应拆成“定义接口/实现一个工具/跑通一个场景/补一个测试”。
- 每个任务结束时更新 README 或计划文档中的状态。

推荐任务卡模板：

```markdown
### T-nn 任务名

目标：
产出：
改动范围：
验收：
明确不做：
风险：
```

---

## 3. Phase 0：原型脚手架

目标：先得到一个能构建、能运行、能做本地 fake loop 的独立项目。

### T-00 新建原型项目

目标：建立 `prototypes/MutableContextAgentProto` 控制台项目。

产出：

- `MutableContextAgentProto.csproj`
- `Program.cs`
- `README.md`
- 最小 `dotnet run --project ... -- smoke`

改动范围：

- 只新增 `prototypes/MutableContextAgentProto/`
- 如需加入 sln，单独修改解决方案文件

验收：

```bash
dotnet run --project prototypes/MutableContextAgentProto -- smoke
```

明确不做：

- 不接真实 DeepSeek
- 不实现 tool-loop
- 不设计复杂目录结构

### T-01 定义核心内存模型

目标：定义最小 `EventLog` 与 `WorkingContext` 类型。

产出：

- `EventLogEntry`
- `WorkingContext`
- `ActionLogItem`
- `MemoryItem`
- `TransientView`

验收：

- smoke 命令打印一份示例上下文
- 单元测试或内置 smoke 验证 transient view 可加入和移除

明确不做：

- 不做持久化
- 不做 token 预算
- 不做复杂图结构

### T-02 实现单 user message renderer

目标：把 `WorkingContext` 渲染为唯一一条 Chat message。

产出：

- `SingleUserContextRenderer`
- 可读的 markdown/text 模板
- 渲染快照测试或 golden output

验收：

```bash
dotnet run --project prototypes/MutableContextAgentProto -- render-demo
```

明确不做：

- 不适配多 provider
- 不输出 assistant/tool 历史消息

---

## 4. Phase 1：单 user message tool-loop 可行性

目标：验证“每次模型调用都只给一条新 user message”是否足以稳定驱动多步任务。

### T-10 DeepSeek Chat client 最小实现

目标：实现绑定 DeepSeek V4 的最小 OpenAI Chat 风格 client。

产出：

- `DeepSeekChatClient`
- 请求/响应 DTO
- 环境变量读取
- 超时、错误信息、原始响应 debug 输出

验收：

```bash
DEEPSEEK_BASE_URL=... DEEPSEEK_API_KEY=... dotnet run --project prototypes/MutableContextAgentProto -- ping-llm
```

明确不做：

- 不做 streaming
- 不做多模型配置
- 不做 provider 抽象

风险：

- DeepSeek V4 的 tool call 返回格式可能与 OpenAI 兼容但有细节差异，应在 debug 输出中保留原始 JSON。

### T-11 最小工具协议

目标：定义模型可调用工具的 JSON 协议和 dispatcher。

产出：

- `ToolDefinition`
- `ToolCall`
- `ToolResult`
- `ITool`
- `ToolDispatcher`

建议第一版模型输出格式：

```json
{
  "thought": "简短说明",
  "tool_calls": [
    {
      "id": "call-1",
      "name": "maze.move",
      "arguments": { "direction": "north" }
    }
  ],
  "final": null
}
```

验收：

- fake 模型输出一个 tool call
- dispatcher 执行 fake tool
- `EventLog` 记录 call/result
- `WorkingContext` 记录简短行动日志

明确不做：

- 不使用 provider native tool call
- 不支持并发工具调用
- 不支持复杂 JSON schema 校验

### T-12 迷宫环境工具

目标：实现一个小型文本迷宫作为第一阶段任务环境。

产出：

- `MazeWorld`
- `maze.look`
- `maze.move`
- `maze.status`
- 一个固定小地图

验收：

```bash
dotnet run --project prototypes/MutableContextAgentProto -- maze-demo
```

明确不做：

- 不接 LLM
- 不做随机地图
- 不做复杂游戏规则

### T-13 Fake agent loop 跑通迷宫

目标：不用真实 LLM，用 scripted policy 跑完整 tool-loop。

产出：

- `FakeMazePolicy`
- `AgentLoop`
- `maxSteps` 防死循环
- 每步打印渲染后的单 user message 摘要

验收：

```bash
dotnet run --project prototypes/MutableContextAgentProto -- maze-fake-run
```

明确不做：

- 不调 DeepSeek
- 不优化提示词

### T-14 DeepSeek agent loop 跑迷宫

目标：让 DeepSeek V4 在单 user message 模式下实际操作迷宫。

产出：

- `maze-llm-run` 命令
- 基础 system/developer prompt 文本
- JSON 输出解析失败时的 repair prompt 或 retry 机制
- 运行日志落到 `.atelia/debug-logs/MutableContextAgentProto.log` 或原型目录下的 logs

验收：

```bash
DEEPSEEK_BASE_URL=... DEEPSEEK_API_KEY=... dotnet run --project prototypes/MutableContextAgentProto -- maze-llm-run
```

观察指标：

- 是否能稳定按 JSON 协议输出
- 是否会被单 user message 迷惑角色边界
- 是否能跨多步保持目标
- 死胡同路径是否能从 `WorkingContext` 中删除或压缩

明确不做：

- 不要求 100% 走出迷宫
- 不做复杂记忆策略

### T-15 Phase 1 复盘文档

目标：沉淀单 user message tool-loop 的经验。

产出：

- `prototypes/MutableContextAgentProto/notes/phase-1-findings.md`

必须回答：

- 单 user message 是否可行？
- 模型是否需要显式“你不是用户，你是 Agent”提醒？
- JSON 协议最常见失败是什么？
- tool result 不作为 `tool` role 回灌是否导致行为异常？
- `WorkingContext` 哪些字段最有用，哪些是噪音？

---

## 5. Phase 2：文本读取/替换与 micro-wizard

目标：验证“不留痕的选择性记忆流程”能否用于真实文本任务。

### T-20 文本沙盒与文件工具

目标：提供安全的文本读写沙盒。

产出：

- `TextWorkspace`
- `text.list_files`
- `text.read_range`
- `text.replace_exact`
- `text.preview_replace`

验收：

- 准备一个 fixture 文本目录
- fake run 能读取范围、预览替换、提交替换

明确不做：

- 不操作整个仓库
- 不做 AST
- 不做多文件复杂事务

### T-21 TransientView 生命周期

目标：让大段读取结果只进入 transient view，不自动进入长期上下文。

产出：

- `TransientView.Id`
- `TransientView.Source`
- `TransientView.Content`
- `TransientView.ExpiresAfterStep`
- `working_context.drop_view`

验收：

- 读取大文本后，下一轮可见
- commit memory 后，原始 view 被移除
- renderer 不再包含完整原文

明确不做：

- 不做自动摘要
- 不做 token 精算

### T-22 micro-wizard：明确意图

目标：读取文本前必须先写入当前查看意图。

产出：

- `wizard.start_inspection`
- `InspectionIntent`
- renderer 中的当前 micro-wizard 区块

验收：

- 没有 intent 时调用 `text.read_range` 返回协议错误
- 有 intent 后允许读取

明确不做：

- 不让工具自己判断意图质量

### T-23 micro-wizard：选择性记忆

目标：读取后必须显式选择保留哪些信息。

产出：

- `wizard.remember`
- `wizard.discard_view`
- `MemoryItem` 支持 `SourceViewId` / `SourceSpan`

验收：

- fake run：读取 100 行，只保留 2 条 MemoryItem
- 后续 renderer 只出现 2 条记忆，不出现完整 100 行

明确不做：

- 不自动把 tool result 变成 memory

### T-24 micro-wizard：文本替换不回灌旧文本

目标：替换工具结果只记录操作摘要，不把被替换的大段旧文本长期保留。

产出：

- `TextEditEffect`
- `ActionLogItem` 记录：文件、范围/匹配摘要、新文本摘要、状态
- `EventLog` 保留完整 diff 或 old/new
- `WorkingContext` 只保留必要摘要

验收：

- 替换大段文本后，下一轮单 user message 不包含完整 old text
- debug/event log 中能查到完整 old/new

明确不做：

- 不实现复杂 conflict resolution

### T-25 DeepSeek 执行文本微任务

目标：让真实模型完成一个小文本编辑任务，例如“把 fixture 文档中的术语 X 统一改为 Y，并说明保留了哪些上下文记忆”。

产出：

- `text-llm-run` 命令
- 一组 fixture
- 运行日志

验收：

- 文件实际被改对
- `WorkingContext` 没有残留完整旧文本
- `phase-2-findings.md` 记录失败案例

---

## 6. Phase 3：Memory Notebook 与代码理解

目标：把 `WorkingContext` 从线性日志升级为可渐进展开的高级记忆笔记。

### T-30 Memory Notebook 节点模型

目标：定义三档内容节点。

产出：

- `NotebookNode`
- `Title`
- `Summary`
- `Body`
- `Links`
- `Tags`
- `SourceRefs`

验收：

- renderer 可按 `TitleOnly` / `Summary` / `Body` 三档渲染

明确不做：

- 不持久化
- 不做图搜索

### T-31 Notebook 编辑工具

目标：让模型通过工具编辑 Notebook。

产出：

- `notebook.create_node`
- `notebook.update_summary`
- `notebook.update_body`
- `notebook.link_nodes`
- `notebook.collapse_node`
- `notebook.expand_node`

验收：

- fake run 创建、链接、折叠、展开节点
- renderer 根据展开状态变化

### T-32 Notebook 持久化草案

目标：先用简单 JSON 文件持久化 Notebook。

产出：

- `notebook.json`
- load/save
- schema version

验收：

- 运行一次创建节点
- 重启后节点仍存在

明确不做：

- 不接 StateJournal
- 不做并发写入

### T-33 Roslyn Node 代码查看 spike

目标：验证用 Roslyn 节点作为代码查看单元。

产出：

- `code.index_project`
- `code.list_symbols`
- `code.view_symbol`
- `CodeNodeRef`

验收：

- 对一个小 C# fixture 项目列出类型/方法
- 查看一个 symbol 时只返回签名、摘要、可选 body

明确不做：

- 不做重构
- 不处理大型 solution 性能

### T-34 Roslyn + Notebook 选择性记忆

目标：查看代码节点后，模型只把必要事实写入 Notebook。

产出：

- `code.view_symbol` 返回 transient view
- `notebook.remember_code_fact`
- SourceRef 指向 symbol/span

验收：

- 查看完整方法体后，下一轮上下文只保留所选事实与 source ref

### T-35 最小代码重构闭环

目标：让 Agent 完成一个受控 C# fixture 重构。

产出：

- `code.preview_replace_symbol_body` 或文本级替换桥接
- `code.apply_edit`
- 重构任务 fixture

验收：

- Agent 查看相关 symbol
- 写入 Notebook 记忆
- 修改代码
- 运行 fixture 测试
- 上下文不保留陈旧完整 diff

---

## 7. 横切任务

这些任务可在任一阶段穿插，但不应抢在核心闭环前过度建设。

### X-01 Debug 日志规范

统一使用 `DebugUtil.Trace/Info/Warning/Error`，类别建议：

- `MutableContext`
- `MutableContext.Tool`
- `MutableContext.Render`
- `MutableContext.DeepSeek`
- `MutableContext.Wizard`

### X-02 Golden Context 快照

为关键场景保存渲染后的单 user message 快照，用于观察上下文是否膨胀、是否泄漏 transient raw text。

### X-03 Prompt 版本记录

每次调整核心 prompt，记录：

- 版本号
- 变更原因
- 对 maze/text 任务的影响

### X-04 失败语料库

保存模型失败样本：

- JSON 格式错误
- 忘记调用工具
- 把 transient view 当长期记忆
- 编造未查看文件内容
- 修改后仍依据旧文本推理

---

## 8. 推荐推进顺序

最稳的路线：

1. T-00 至 T-02：建立原型骨架与单 user renderer。
2. T-10 至 T-13：不用真实 LLM 先跑通 tool-loop。
3. T-14 至 T-15：接 DeepSeek 跑迷宫并复盘。
4. T-20 至 T-24：实现 micro-wizard 的文本闭环。
5. T-25：真实模型做文本编辑任务。
6. T-30 之后：再进入 Notebook/Roslyn 大工程。

不建议一开始就做 Memory Notebook/Roslyn，因为那会把失败原因混在一起：不知道是单 user message 不行、tool 协议不行、micro-wizard 不行，还是代码理解层太复杂。

---

## 9. 当前最小下一步

下一回合最适合启动的任务是：

**T-00 新建原型项目**。

完成后继续 T-01/T-02，就能在不接真实模型的情况下看到“mutable working context 渲染成单 user message”的第一块实体样本。
