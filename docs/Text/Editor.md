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

Robird: 请为我讲讲，你见过的核心框架最小巧精干，设计精良的文本编辑器是什么？不论是GUI/TUI/Headless的函数库或组件都行，不限编程语言。我正在为Agent中的LLM开发一款LLM原始的文本编辑器及相应的TUI框架。追求小而美，有扩展机制，结构清晰的。如果回忆不清晰，可以用你的工具翻翻github repo或者搜索网上信息，委托我帮你查找信息也行。

GitHub Copilot: 很高兴聊聊小而美的文本编辑器，我先介绍我认为设计最精巧的一个并补充一些对比参考。

## 核心推荐：Xi Editor

### 架构亮点
- **双层结构**：Rust 编写的 `xi-core` 负责文档、增量编辑和并发调度，前端（TUI/GUI/IDE 插件）通过 JSON-RPC 与核心通信，天然解耦，便于替换/扩展界面。
- **Rope 数据结构**：核心使用 `rope science`（rope 树 + 小块缓存）高效处理大文件和随机编辑，在保证低内存占用的同时支持快速撤销/重做。
- **增量渲染协议**：核心通过“差量更新”把最小变更块推给前端，避免整屏重绘；前端只需理解“视窗 + patch”模型。
- **插件机制**：插件与核心同样通过 RPC（可多语言），作用于文档流的增量事件；LLM agent 可以在此链路中插入过滤或建议。
- **多线程友好**：内部把昂贵操作丢给后台线程池，前端只需等待回调；对需要嵌入 LLM 处理的场景尤其舒服。

### 为什么适合做 LLM 原始编辑器
- RPC 边界清晰，可在 Agent 里直接以消息流加入“补全/评审/格式化”。
- Rope 与增量 patch 让你容易实现 token-level diff、局部上下文提取。
- 核心无 UI 负担，适合作为 headless 服务，前端/TUI 可以按需求定制。

### 潜在改进点
- 原项目维护趋缓，可参考社区 fork（如 `xi-term`, `xi-mac`, `xi-gtk`）或直接复刻架构。
- 插件协议可以升级为 Cap’n Proto / gRPC 以获得更好的类型校验和流式能力。

## 值得借鉴的其他“小而美”方案

### Helix
- Rust 实现，着重无模式编辑与语义结构；Tree-sitter 深度集成，LSP 原生。
- Buffer 采用 ropey，命令体系受 Kakoune 启发，操作语义清晰。
- 缺点：更偏终端编辑器；核心和界面耦合较紧，不如 Xi 那样完全分层。

### Kakoune
- 设计哲学 “selection-first”，命令语言简单可组合。
- Scriptable（shell + POSIX 工具），宏系统轻量。
- 对 LLM Agent 友好？命令都是文本化，可通过 pipe 交互，但内部没有独立核心。

### Lite XL
- Lua 可扩展，嵌入式 GUI 编辑器，代码体积小（< 40k 行）。
- 渲染基于 SDL2；插件生态以 Lua 为主，成本低。
- 使用 gap buffer + piece table；对极大文件稍逊，但构建 UI/TUI 框架灵活。

### Micro
- Go 编写的终端编辑器，插件同样用 Lua。
- 架构简单，事件驱动；适合作为入门模型，但扩展边界没有 Xi/Helix 清晰。

## 设计借鉴建议

### 关键契约
- 文档存储：采用 rope/piece-table 组合，确保 $O(\log n)$ 插入/删除。
- API 边界：前端 <-> 核心只交换“增量 patch + selection state”，便于复用。
- 插件协议：统一消息格式，LLM Agent 可注册为文档变更监听器。
- 渲染策略：分离“模型（buffer）”和“视图（panes/cursors）”，可同时驱动 GUI、TUI、headless。

### 适配 LLM 场景的扩展钩子
- Context Providers：从 buffer 快速抽取 prompt、周边上下文。
- Intent Hooks：捕捉命令/宏，将其映射到 Agent 工具调用。
- Streaming Output：允许核心接收流式补全 patch，实时合并/冲突解决。

## 下一步探索
- 读 `xi-editor` 的 `docs/rope_science.md` 和 `protocol.md`，快速掌握核心数据结构与 RPC。
- 对比 Helix 的事件循环与 Tree-sitter 集成，思考如何在 Agent 中共享语法树。
- 设计你自己的 `AgentBufferCore` 接口，先在 headless 模式跑通“LLM 补丁 → buffer diff → 前端 patch”。

如需具体源码走读或接口示例，随时告诉我，我可以帮你定位关键文件或提取片段。

Robird: 相比之下，从AvalonEdit中拆出来一个编辑器核心，剥离掉GUI部分，是否也是好选择？

GitHub Copilot: 继续探讨可能的核心选型，我先梳理AvalonEdit的结构再评估拆分可行性。

## 架构速览
- **核心类型**：`TextDocument`（文本缓冲）、`DocumentLine`（行索引）、`ISegment`/`TextAnchor`（位置跟踪）、`UndoStack` 等组成逻辑层，UI 控件 `TextEditor`/`TextArea` 只是监听文档事件并渲染。
- **事件驱动**：文档通过 `TextDocument.Changed`、`ChangeTracking`、`LineTrackers` 把增量操作通知视图，默认协议即“变更范围 + 替换内容 + 文档版本”。
- **可扩展点**：格式化器、折叠策略、语法高亮等都已抽象为服务接口（`IHighlightingDefinition`、`IDocumentColorizingTransformer` 等）。
- **依赖面**：逻辑层仍有不少 `System.Windows`（WPF）的引用，例如 `Dispatcher`, `Freezable`, `DependencyObject` 等，这些在完全 headless 环境下需要替换。

## 拆分可行性评估
### 优势
- **成熟稳定**：用于 SharpDevelop、ILSpy 等项目多年，算法和 Undo/Redo 流程成熟。
- **高层契约简单**：`TextDocument` 本身即可作为独立 DocCore 使用；对 Agent 来说，增量变更 API 友好。
- **现成特性**：折叠、书签、着色器等已有接口，若计划未来提供 UI，可直接复用。

### 挑战
- **WPF 耦合**：即便抛弃控件，逻辑层也散落 `Dispatcher`、`Thread`、`Freezable` 依赖；需要系统性替换或改写。
- **编码规范**：原项目以 .NET Framework 为主，内部使用派生类/事件模式；现代 .NET（Core/Standard）需迁移适配。
- **性能模型**：基于 `Rope` + `GapBuffer` 的混合结构在 Windows 桌面表现优良，但缺少面向多线程/后台任务的设计，想要在 Agent 背景线程处理大批量补丁，可能需要额外封装。
- **插件协议缺位**：AvalonEdit 没有内建 RPC/消息协议，若要面向 LLM 的“核心-前端”分层，需要自己定义桥梁。

## 与 Xi Editor 核心对比
| 维度 | AvalonEdit Core | Xi Core |
| --- | --- | --- |
| **语言/平台** | C# / .NET | Rust，面向多前端 |
| **UI 耦合** | 中等，需要手工剥离 WPF 类型 | 无 UI，RPC 中心 |
| **增量协议** | 事件回调（`DocumentChangeEventArgs`） | 显式 JSON-RPC Patch |
| **多线程/并发** | 主要面向 UI 线程 | 天生后台任务/线程池 |
| **LLM 友好度** | 需自行封装 IPC/补丁协议 | 原生流式 patch 模式 |
| **学习/复用成本** | .NET 生态熟悉度高 | 需掌握 Rust/rope 科学 |

如果你的 Agent 运行环境是 .NET，且希望快速拥有一个可靠的缓冲区实现，那么从 AvalonEdit 抽离核心是一个可行起点：
1. 建议从 `ICSharpCode.AvalonEdit.Document` 命名空间整体搬迁，排查对 `System.Windows` 的引用，用自定义 stub 替换；
2. 封装一个 `DocumentHost` 层，负责对接 LLM 补丁、快照序列化、事件广播；
3. 另写一个轻量协议（例如 JSON-RPC 版本的 `DocumentChange` 结构）供 TUI/GUI 订阅。

不过，若你优先考虑**跨语言/跨前端 + 并发友好 + RPC 原生**，Xi 的整体架构仍然更贴合“LLM 原始编辑器”这一目标。

## 行动建议
- 若倾向 .NET：试着 fork AvalonEdit，建立独立的 `AvalonEdit.Core` 包；先跑 `TextDocument` + `UndoStack` 单测，清理掉 WPF 依赖，再围绕它设计 Agent IPC 层。
- 若可接受 Rust：以 Xi Core 为蓝本，自行实现精简版（甚至用 C# 重写 rope + RPC），借鉴其协议与线程模型。
- 结合两者：用 AvalonEdit 的 API 概念（Segment、Anchor）+ Xi 的 RPC 设计，打造自定义 `AgentBufferCore`。

需要我帮你梳理 AvalonEdit 具体哪些文件/类型最值得保留，或示例化一个 headless 包装层的话，告诉我；我可以再深入挖。

Robird: 你了解[Zed](https://github.com/zed-industries/zed)这个code editor吗？前人的文本编辑器项目众多，我既然是新写，那肯定是要尽量学习前人的优秀设计。

GitHub Copilot: 关于 Zed 的设计，我们可以细看它的核心架构再提炼对新编辑器的启示—我先概览其关键模块。

## 架构速览
- **语言与工程**：整体用 Rust 编写，围绕一个名为 **Redwood** 的任务调度/状态管理框架，把 UI、文档、协作等子系统组织成响应式任务图。
- **专有 UI 引擎**：采用自家的 GPU immedi­ate-mode 框架 **GPUI**，把界面与渲染写成声明式状态树（类似 React + ECS），借助 `wgpu` 做跨平台 GPU 加速。
- **后端服务**：默认自带 Zed “cloud” 后端用于协作、AI 功能，但编辑器本身可以离线运行；协作能力基于自有 CRDT + presence 服务。

## 文本缓冲与协作
- **Rope 数据结构**：文本核心使用 rope（历史上沿袭自 Xi Editor 的 `ropey` 思路），支持 $O(\log n)$ 插入/删除，并有“行缓存”辅助视图。
- **增量管线**：编辑操作转换为 `Transaction`，里头包含文本 diff、selection、undo/redo 元数据，提交到文档后会触发订阅者更新。
- **协同编辑**：Zed 选择定制的 CRDT（源自 `collab` 项目，后来改名为 `zedcode`），通过 operation log 与 cloud 同步；冲突解决能维持字符顺序且尽量保持语义邻近。
- **AI 集成**：文档模型支持“suggestion patch”概念，AI 生成的补丁和多段位建议以 diff 形式注入，和协作补丁共用流水线。

## UI 与渲染
- **Layout / Style**：GPUI 是即时模式，但在 Redwood 的 state watcher 里做 diff，所以只有变更节点会重绘；`ParagraphCache` 类似文本布局缓存，按 viewport 分块避免全局 re-layout。
- **输入模型**：键盘/鼠标事件先进入 Redwood 任务，再转换成命令（command palette、vim 模式等），最后落到 `EditorState`，利于插入 Agent hook。
- **插件体系**：目前偏“内建”，通过 `ExtensionHost` 加载 TypeScript/Tree-sitter bundle，语法解析和 LSP 由 Redwood 任务管理；自定义扩展能力仍在演进中，但结构已经围绕 IPC 通道设计。

## 对新建 LLM 编辑器的启示
- **状态管理**：Redwood 的任务依赖图把 UI、buffer、协作解耦，你可仿照：
  - 把文档、光标、语法树等封装成可观察状态；
  - 把 Agent 的推理/补丁当成异步任务节点，自动触发视图刷新。
- **GPU/TUI 抽象**：若要同源支持 GUI/TUI，可借鉴 GPUI 的“声明式渲染 + diff”思想，在 TUI 里也维护虚拟树，再映射到字符网格或 WebGPU。
- **协作/AI 同轨**：把编辑、协作、AI 补丁统一成 transaction 流，让冲突解决和回滚逻辑只写一遍；LLM 工具可生成“待应用 patch”，由核心调度合并。
- **性能策略**：Zed 在后台线程做语义解析、代码索引等重活，再把结果通过 Redwood emit；同理，你可以让 Agent 的昂贵调用与 UI 完全分离，靠增量 diff 注入结果。

## 深入资料建议
- 阅读仓库中的 `docs/architecture.md`、`crates/redwood_core`，了解任务系统实现。
- `crates/editor` 展示 transaction/rope 的结构，可作为设计文档的出处。
- `crates/gpui`、`crates/text` 提供 UI 和文本布局细节；对 TUI 实现，可思考把 GPUI 的节点 diff 映射成终端绘制命令。
- 官方博客与 issue（如 #1753）讨论协作、性能和扩展计划，能看到团队如何权衡设计。

如果想要我帮你对比 Zed 文档核心与 AvalonEdit 或 Xi，在代码层面列出具体契约，也可以继续聊；或者想看 Redwood/GPUI 的关键文件路径，我也可以协助定位。

Robird: 如果与Roslyn深度集成的话，Microsoft.CodeAnalysis.Text.SourceText依然是最优选择吧？比如token级的信息注入，LLM流式输出`MyClass.`时，我们的程序就调用Roslyn注入成员信息，就是IDE中的体验。这种情况下，直接用SourceText作为buffer，似乎就能0拷贝了。你怎么想？

GitHub Copilot: 这确实是个关键决策点，我先就 Roslyn 深度集成的角度梳理 `SourceText` 能带来的优势与隐患，再给出一个混合方案建议。

## Roslyn 集成视角
- Roslyn 的工作区（`DocumentState`、`SourceTextContainer`）本身就是围绕 `SourceText` 做增量版本管理的，所有语法树、语义模型、增量分析器都以 `SourceText` 的快照为入口。
- `SourceText` 设计为**不可变、结构共享**：调用 `WithChanges` 会返回新实例，但内部的 `SourceText.Storage` 会尽量复用旧块，对 Roslyn 而言这提供了天然的版本链（配合 `VersionStamp`）。
- 你可以继承 `SourceText` 或实现 `SourceTextContainer`，从而把自定义 buffer（例如 rope）暴露给 Roslyn，实现“按需”零拷贝。

## 作为编辑缓冲的优点
1. **0 拷贝读取**：`GetSubText` 会共享底层存储；自定义子类还能用 `ReadOnlyMemory<char>` 暴露数据，避免中间字符串。
2. **增量语义**：`WithChanges(IEnumerable<TextChange>)` + Roslyn 的 `IncrementalAnalyzer`，可以非常方便地在 LLM 输出新 token 时触发局部语法/语义更新，实现“`MyClass.` → 自动补成员提示”的体验。
3. **生态直连**：无需写额外桥接，就能复用 Roslyn 的 Formatter、CompletionService、SemanticModel。

## 潜在瓶颈与对策
| 问题 | 说明 | 对策 |
| --- | --- | --- |
| **频繁小改写开销** | LLM 流式输出若逐字符 `WithChanges`，虽然共享底层块，但仍会频繁实例化新 `SourceText` 和 `TextChangeRange`。 | 先在自有 buffer（rope/gap）累计流式输入，按段提交到 `SourceText`；或实现自定义 `SourceText` 直接包裹 rope。 |
| **缺少“原位”编辑 API** | `SourceText` 是不可变的，不提供随机写操作。 | 用自定义 `EditingSession` 聚合多个 `TextChange`，一次性 `WithChanges`。 |
| **线程模型** | Roslyn 假定 `SourceText` 快照多线程只读；你的编辑循环需要额外的 mutable 层。 | 参考 VS 的做法：`SourceTextContainer` 负责线程安全写入，`SourceText` 作为快照派发。 |
| **LLM 语境需求** | 需要快速抽取 prompt 周边上下文、token 索引。 | 在 `SourceText` 外层维护光标/selection 索引缓存，或在 rope 层存储 token 映射。 |

## 推荐做法
1. **双层架构**：
   - 下层保持灵活的 rope/gap buffer，负责高频流式写入、文本分块缓存；
   - 上层实现一个 `SourceTextContainer`，把 rope 快照映射成 Roslyn `SourceText`（可自定义子类，让 `CopyTo`/`GetSpan` 直接访问 rope 内存）。
   这样你既能保持编辑性能，又能在需要 Roslyn 服务时零拷贝出快照。
2. **批量化流式更新**：为 LLM 输出设计 “chunk aggregator”，例如每收到换行或符号边界再推送一次 `TextChange`，避免单字符更新。
3. **Roslyn Hook**：利用 `Workspace.TryApplyChanges` 或自建的 `CustomWorkspace`，把新的 `SourceText` 推入 Document，这样 Roslyn 完成树会自动增量更新；同时注册 `SemanticModel`/Completion 的监听，实时把成员候选注入到 TUI。
4. **Token 信息注入**：在增量更新后，使用 `SemanticModel.LookupSymbols` 或 `CompletionService` 获取成员列表；通过自定义协议回写到编辑器界面/Agent 逻辑中，实现“IDE 级体验”。

## 后续探索
- 研究 Visual Studio Editor 如何桥接 `ITextSnapshot`↔`SourceText`（参考 `Microsoft.VisualStudio.Text` + `Roslyn` 的适配层），这是成熟的双层方案。
- 若想完全摆脱中间 Buffer，可尝试直接继承 `SourceText` 以 rope 为底层实现，但需要小心处理版本戳和 `GetChangeRanges`。
- 对接生成式补丁时，可以扩展 `SourceText.WithChanges` 的 `TextChange` 元信息，记录“补丁来源 = LLM/用户/协作”，方便冲突调度。

总的来说，`SourceText` 在 Roslyn 集成方面仍是最优的“快照格式”。我建议把它视作 Roslyn 交互层的标准输出，而在 LLM 实时编辑的内部循环里保留一个专门的 mutable buffer，这样既不牺牲流式性能，又能享受 Roslyn 提供的全部语义能力。需要实操样板（如自定义 `SourceTextContainer` 的骨架）可以继续告诉我。
