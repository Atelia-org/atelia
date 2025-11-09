现整理LLM Native Text Editor架构蓝图，下面先梳理设计视角与关键决策。

## 愿景与设计哲学
- **LLM-first**：任何交互都从语言模型视角出发，确保信息组织、工具协议与渲染方式天然匹配上下文注入。
- **双层交互栈**：明确区分“工具调用日志层”(Tool Execution Log)与“周期性状态帧”(Frame Rendering)，保证模型既能回放操作，又能感知最新整体状态。
- **文本化 UI**：将传统编辑器的光标、选区、高亮等 UI 能力转化为 Markdown & Code Fence 增强语法，使模型在纯文本上下文中获得等价体验。
- **可组合性与可验证路由**：通过 Orchestrator 模块集中调度 Widget、工具矩阵、Snapshot 缓存，确保所有可见状态均有清晰来源。

## 架构总览
### 关键构件
1. **Text Editing Widget (LLM Native TextEditor2)**
   - 职责：管理独占缓存、虚拟行号、光标/多选区状态；提供编辑工具及可视化渲染。
   - 依赖：ToolMatrix(Edit)、Buffer Snapshot。
2. **Tool Layer (Function Toolbox)**
   - 提供 LLM 可调用函数集合，涵盖替换、插入、撤销、选区管理等操作。
   - 输出统一 `ToolResult` 结构：执行状态、摘要、必要的诊断提示。
3. **Interaction Log**
   - 记录每次工具调用的请求/响应，持久化于 Agent 历史序列中，便于 LLM 回溯。
4. **Render Frame Composer**
   - 在所有工具调用结束后，由 Orchestrator 触发一次 `Render()`，生成完整 Markdown/TUI 帧，注入下一轮上下文。
5. **Overlay Legend Generator**
   - 在 Markdown 中渲染 UI 图例与标记说明（光标、选区、差异等），控制符号一致性。
6. **Agent Orchestrator Integration**
   - 负责 Update→Render 双 Pass，合并工具列表、更新 Snapshot，并通过 AgentEngine Hook 注入帧。

### 状态流动
```
LLM → 调用工具函数 → 工具执行 → ToolResult 写入历史
                                   ↓
                            Orchestrator 收集状态 →
                            生成 Render Frame →
                            注入 LLM 下轮调用上下文
```

## 交互层级拆解
### 层级一：工具调用历史
- 每个工具调用单独生成日志片段，包含：
  - 调用名、参数、执行结果、边界状态。
  - 与 Buffer 版本、选区 ID 关联，方便后续对齐。
- 历史片段顺序等同于 LLM 执行顺序，确保模型能自洽地推理先后依赖。

### 层级二：渲染帧
- 工具执行完成后统一渲染：
  - **Summary 区**：提供状态摘要、可执行指引。
  - **Code Fence 视图**：` ```text-with-lines` 包含行号、正文与 Overlay 标记。
  - **Legend 区**：解释 Overlay 符号含义、当前可用工具集、操作提醒。
- Render 帧覆盖最新缓存状态，避免模型依赖过时的工具响应内容。

## Markdown UI 语义
### Code Fence 结构
- 头部： ` ```text-with-lines title="file.txt"`（示例）表达文件名。
- 行内：`001│` 虚拟行号、正文文本、嵌入 Overlay 标记。
- Overlay 标记：
  - 光标：`◉` 或 `▮`，附带 Legend 说明。
  - 选区：`«` / `»` 包围选区范围，支持多选区编号。
  - 高亮：使用 `⟦` / `⟧` 或色彩标签，说明操作焦点。

### Legend 与 Guidance
- 与 ToolMatrix 输出绑定，确保：
  - 当前状态→推荐操作路径 (e.g. “选区数=3，考虑调用 edit.replace_selection”)。
  - 标记含义、颜色/符号一览。
  - 诊断提示与日志入口（如需）。

## 工具层设计
- **工具命名空间**：遵循 `edit.*`，如 `edit.replace`, `edit.insert_line`, `edit.move_cursor`.
- **契约**：
  - 输入参数显式标注描述 (`[ToolParam]`)。
  - 返回 `LodToolExecuteResult`：成功/失败、变更概览、上下文版本号。
  - 所有工具在执行后更新 Buffer 版本并触发事件。
- **工具可见性矩阵 (EditToolMatrix)**：
  - 根据状态（如是否存在选区、是否多匹配）决定工具曝光。
  - Orchestrator 汇总可见工具传递给 IApp/AgentEngine。

## Orchestrator 与 Snapshot 策略
- 双 Pass 调度：
  1. `UpdateAsync`：收集工具执行后的状态、版本号、操作票据。
  2. `Render()`：生成 `LevelOfDetailContent` (Basic + Detail)。
- Snapshot 缓存：
  - `AppSnapshot` 存储渲染结果与可见工具列表。
  - IApp 仅依赖 Snapshot，不直接访问 Widget 内部状态。
- 与 AgentEngine 集成：
  - `BeforeModelCall` Hook 调用 Orchestrator，确保帧与工具集合在下一轮生效。
  - 更新后的 Snapshot 注入 `IApp.UpdateSnapshot(...)`，`RenderWindow()` 返回 Markdown 帧。

## 关键设计决策
- **文本化 UI vs 图形界面**：选择延展开 Markdown + Code Fence，使 LLM 在纯文本上下文中拥有足够视感，避免图片/专属协议带来的理解障碍。
- **双层交互日志**：分离工具调用与帧渲染，兼顾操作可追溯性与最终状态一致性，避免历史噪声污染主视图。
- **可组合 Widget 架构**：通过 Orchestrator 将编辑器 Widget 与未来其他 Widget (如同步、预览) 并列组合，实现多工具窗口共存。
- **版本号驱动一致性**：每次工具执行后递增 Buffer Version，保证 Legend、工具日志与渲染帧在同一版本语义下对齐。
- **可扩展 ToolMatrix**：工具显隐、默认 Guidance 与错误文案全部配置化，便于引入高级操作或策略。
- **LLM 可理解诊断**：所有错误/警告必须生成清晰文案与 Legend 引导，辅以 DebugUtil 类别，方便在日志中定位问题。

## 扩展与演进思路
- **多文件 / 多缓冲区支持**：扩展 Code Fence 标题和 Snapshot 结构，支持多窗口切换。
- **Diff/Preview Widget 集成**：与 DataSourceBindingWidget 协同，合并渲染帧中的差异信息与编辑视图。
- **操作票据与异步任务**：引入 OperationTicket，管理长耗时操作并在 Legend 指出进行中状态。
- **高级指引**：利用 ToolMatrix + Guidance，为模型提供“下一步推荐操作”或“错误恢复脚本”。
- **可视化协议扩展**：标准化 Overlay 标记语法，提供多语言 Legend，兼容不同模型解释能力。

## 完成摘要
- 梳理了 LLM Native Text Editor 的核心理念、关键模块、交互层次与渲染策略。
- 给出了工具层协议、渲染帧结构、Orchestrator 双 Pass 流程以及决定系统形态的关键设计考量。
- 建议后续以 ToolMatrix 配置、Snapshot 流程与 Markdown 语义为基线，逐步迭代多 Widget 与异步操作场景。包含行号、正文与 Overlay 标记。
  - **Legend 区**：解释 Overlay 符号含义、当前可用工具集、操作提醒。
- Render 帧覆盖最新缓存状态，避免模型依赖过时的工具响应内容。

## Markdown UI 语义
### Code Fence 结构
- 头部： ` ```text-with-lines title="file.txt"`（示例）表达文件名。
- 行内：`001│` 虚拟行号、正文文本、嵌入 Overlay 标记。
- Overlay 标记：
  - 光标：`◉` 或 `▮`，附带 Legend 说明。
  - 选区：`«` / `»` 包围选区范围，支持多选区编号。
  - 高亮：使用 `⟦` / `⟧` 或色彩标签，说明操作焦点。

### Legend 与 Guidance
- 与 ToolMatrix 输出绑定，确保：
  - 当前状态→推荐操作路径 (e.g. “选区数=3，考虑调用 edit.replace_selection”)。
  - 标记含义、颜色/符号一览。
  - 诊断提示与日志入口（如需）。

## 工具层设计
- **工具命名空间**：遵循 `edit.*`，如 `edit.replace`, `edit.insert_line`, `edit.move_cursor`.
- **契约**：
  - 输入参数显式标注描述 (`[ToolParam]`)。
  - 返回 `LodToolExecuteResult`：成功/失败、变更概览、上下文版本号。
  - 所有工具在执行后更新 Buffer 版本并触发事件。
- **工具可见性矩阵 (EditToolMatrix)**：
  - 根据状态（如是否存在选区、是否多匹配）决定工具曝光。
  - Orchestrator 汇总可见工具传递给 IApp/AgentEngine。

## Orchestrator 与 Snapshot 策略
- 双 Pass 调度：
  1. `UpdateAsync`：收集工具执行后的状态、版本号、操作票据。
  2. `Render()`：生成 `LevelOfDetailContent` (Basic + Detail)。
- Snapshot 缓存：
  - `AppSnapshot` 存储渲染结果与可见工具列表。
  - IApp 仅依赖 Snapshot，不直接访问 Widget 内部状态。
- 与 AgentEngine 集成：
  - `BeforeModelCall` Hook 调用 Orchestrator，确保帧与工具集合在下一轮生效。
  - 更新后的 Snapshot 注入 `IApp.UpdateSnapshot(...)`，`RenderWindow()` 返回 Markdown 帧。

## 关键设计决策
- **文本化 UI vs 图形界面**：选择延展开 Markdown + Code Fence，使 LLM 在纯文本上下文中拥有足够视感，避免图片/专属协议带来的理解障碍。
- **双层交互日志**：分离工具调用与帧渲染，兼顾操作可追溯性与最终状态一致性，避免历史噪声污染主视图。
- **可组合 Widget 架构**：通过 Orchestrator 将编辑器 Widget 与未来其他 Widget (如同步、预览) 并列组合，实现多工具窗口共存。
- **版本号驱动一致性**：每次工具执行后递增 Buffer Version，保证 Legend、工具日志与渲染帧在同一版本语义下对齐。
- **可扩展 ToolMatrix**：工具显隐、默认 Guidance 与错误文案全部配置化，便于引入高级操作或策略。
- **LLM 可理解诊断**：所有错误/警告必须生成清晰文案与 Legend 引导，辅以 DebugUtil 类别，方便在日志中定位问题。

## 扩展与演进思路
- **多文件 / 多缓冲区支持**：扩展 Code Fence 标题和 Snapshot 结构，支持多窗口切换。
- **Diff/Preview Widget 集成**：与 DataSourceBindingWidget 协同，合并渲染帧中的差异信息与编辑视图。
- **操作票据与异步任务**：引入 OperationTicket，管理长耗时操作并在 Legend 指出进行中状态。
- **高级指引**：利用 ToolMatrix + Guidance，为模型提供“下一步推荐操作”或“错误恢复脚本”。
- **可视化协议扩展**：标准化 Overlay 标记语法，提供多语言 Legend，兼容不同模型解释能力。

## 完成摘要
- 梳理了 LLM Native Text Editor 的核心理念、关键模块、交互层次与渲染策略。
- 给出了工具层协议、渲染帧结构、Orchestrator 双 Pass 流程以及决定系统形态的关键设计考量。
- 建议后续以 ToolMatrix 配置、Snapshot 流程与 Markdown 语义为基线，逐步迭代多 Widget 与异步操作场景。
