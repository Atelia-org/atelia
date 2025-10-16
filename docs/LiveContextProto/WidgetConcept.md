# LiveContextProto Widget 设计概念草案

*版本：0.2-draft · 更新日期：2025-10-15*

> 关联文档：
> - 《ToolFrameworkDesign.md》：供 Widget 暴露工具时复用的 `ITool` 契约与参数抽象。
> - 《ToolCallArgumentHandling.md》：说明工具参数解析的“宽进严出”策略。
> - 《ConversationHistory 抽象重构蓝图 · V2》：提供 AgentState、LiveScreen 与上下文渲染的总体设计目标。

---

## 文档目的

- 给出 LiveContextProto 场景下 **Widget** 的概念、职责边界与最小接口草案。
- 取代早期松散的 LiveInfo 约定，统一“状态建模 → 操作接口 → [LiveScreen] 呈现”三类功能。
- 为 Memory Notebook 的 Widget 化改造提供设计基准，并指导后续的 Planner / Provider / Orchestrator 集成。

## 背景动机

| 痛点 | 现状 | 影响 |
| --- | --- | --- |
| LiveInfo 松散 | 记忆、系统指令、工具清单等信息由调用方各自维护 | 状态更新缺乏一致性，无法审计或复用已有逻辑 |
| 工具与状态分离 | 工具操作直接操纵 AgentState 内部字段 | 缺少封装，难以形成可复用的“功能组件” |
| [LiveScreen] 绑定零散 | 上下文渲染时需要额外判断何时插入 [LiveScreen] | 扩展新信息源时易漏打点或重复输出 |

Widget 旨在将某类信息的 **数据模型、操作工具、呈现逻辑** 内聚封装成一个内存对象，便于灵活组合和生命周期管理。

## 设计原则

1. **单一事实源**：Widget 内部维护自身状态/视图，并通过受控入口与 AgentState 协作，避免各处散落的 LiveInfo。
2. **工具化操作**：Widget 暴露一组使用 `ITool` 契约描述的工具，供 LLM 调用时执行具体操作。
3. **上下文可视化**：Widget 负责渲染自身 [LiveScreen] 内容（Markdown/纯文本等），并决定注入策略。
4. **供应商无关**：Widget 不直接依赖特定 Provider；渲染的结果由 AgentState / Orchestrator 统一注入上下文。
5. **简单优先**：保持实现路径尽可能线性、同步，无需提前为并发或持久化设计复杂抽象。
6. **渐进式扩展**：接口尽量保持最小可用，允许未来引入遥测、事件化、Planner 提示等能力。

## 最新设计决策（2025-10-15 更新）

- Memory Notebook 仍维持“内存态”存储；后续若需要持久化，由 `MemoryNotebookWidget` 自行对接外部流式读写。
- `AgentState` 将直接持有 `MemoryNotebookWidget`，Widget 视为 AgentState 的子系统，无需额外 Catalog 层。
- `MemoryNotebookWidget` 暂为唯一实现，后续扩展时再考虑集合化与通用注册机制。
- 所有 Widget 交互先以同步 API 表达，需要异步 IO 时再逐层升级为异步接口。
- 渲染顺序“先简单后精准”：`AgentState` 遍历所持 Widget，按照内存迭代顺序展平渲染 Markdown 片段。
- 原型阶段优先端到端验证，可暂缓新增独立单元测试。
- Widget 生命周期不提供显式 `InitializeAsync` / `DisposeAsync` 钩子；如需资源管理将另行设计。
- 即便未来扩展到多 Widget，也默认按照集合的自然顺序渲染，不额外引入排序或优先级规则。
- Widget 之间视为完全独立实体，不共享状态、也不相互调用工具。
- 遥测保持局部化，不尝试将 Widget Metadata 汇聚到全局遥测体系。
- Planner 使用场景仅消费 Markdown 文案，不提供结构化提示或 JSON Schema。
- Widget 工具在返回 `LevelOfDetailSections` 时，`Full` 档需与该 Widget 的 `[LiveScreen]` 输出协同，避免在同一条上下文消息中重复呈现同一份内容；`Summary` / `Gist` 的组织不受此限制。


## 核心接口草案

```csharp
namespace Atelia.LiveContextProto.Widgets;

internal interface IWidget {
    string Name { get; }
    string Description { get; }

    /// <summary>
    /// Widget 暴露给 LLM 的工具集合（只读视图）。
    /// </summary>
    IReadOnlyList<ITool> Tools { get; }

    /// <summary>
    /// 渲染 [LiveScreen] 片段；返回 null 表示无内容。
    /// </summary>
    string? RenderLiveScreen(WidgetRenderContext context);

    /// <summary>
    /// 派发工具调用。toolName 必须来自 <see cref="Tools"/> 集合。
    /// </summary>
    ToolHandlerResult ExecuteTool(
        string toolName,
        ToolExecutionContext executionContext
    );
}
```

- `Tools` **必须返回只读的 `ITool` 集合**（如 `ImmutableArray<ITool>` 或 `IReadOnlyList<ITool>`），确保调用方不会破坏 Widget 内部状态。
- `RenderLiveScreen` 允许 Widget 使用 `WidgetRenderContext` 获取 AgentState 快照或调试信息，默认以 Markdown 片段输出。
- `ExecuteTool` 统一负责工具分派，可选择内部 switch 或字典映射；异常由调用者按需捕获。

### 辅助类型草案

```csharp
internal sealed record WidgetRenderContext(
    AgentState AgentState,
    ImmutableDictionary<string, object?> Environment
);
```

- `AgentState`：让 Widget 读取全局历史或 LiveInfo 快照（只读）。
- `Environment`：运行时上下文（如会话 ID、调试开关），由 Orchestrator 注入。

未来可扩展 `WidgetMetadata`、`WidgetTelemetry` 等结构，用于托管统计信息或 Planner 提示。

## Minimal Prototype Scope

### 必需组件

- **IWidget 接口**：沿用本文档定义的同步方法，约束名称、描述、工具列表与两大职责。
- **WidgetRenderContext**：提供对 `AgentState` 与运行时环境的只读访问，暂不扩展额外元数据。
- **MemoryNotebookWidget**：
    - 内部字段维护 Notebook 最新全文，默认以 `string` 持有。
    - `Tools` 仅暴露 `memory_notebook_replace`（同步版本），借鉴旧 `MemoReplaceLiteral` 的参数语义。
    - `ExecuteTool` 成功时更新私有字段并调用 `AgentState` 的写入 API，失败时返回 `ToolHandlerResult` 的错误状态。
    - `RenderLiveScreen` 输出固定 Markdown（示例标题：`## Memory Notebook`），内容为空时仍输出占位提示。
    - 通过 `DebugUtil.Print("MemoryNotebookWidget", ...)` 记录工具调用的开始、结束与异常信息。
    - 在生成 `LevelOfDetailSections` 时，`Full` 档应避免重复 `RenderLiveScreen` 已提供的 Notebook 全文，可选择输出 diff、执行摘要或其他补充说明。

### 外部依赖最小约定

- **AgentState**（参考 `prototypes/LiveContextProto/State/AgentState.cs`）：
    - 提供同步的 Notebook 快照访问器（例如 `GetNotebookSnapshot()`）。
    - 提供同步的 Notebook 替换写入方法（例如 `ReplaceNotebook(string content)`）。
    - 在构造阶段直接创建并持有 `MemoryNotebookWidget` 实例，暴露给 Orchestrator 与 Tool 执行栈。
- **ToolExecutionContext / ToolHandlerResult**（参考 `prototypes/LiveContextProto/Tools/`）
    - 继续沿用既有结构；原型阶段允许同步调用。
- **调试分类**：统一使用 `MemoryNotebookWidget` 作为 DebugUtil 日志类别，便于过滤与排查。

### 初版明确不包含

- 多层级渲染（Summary/Gist）与 `MultiLevelSections` 数据结构。
- 多工具扩展（如 append、summarize）与复杂 diff 逻辑。
- Widget 生命周期钩子（`InitializeAsync`/`DisposeAsync`）与热插拔。
- 遥测、事件流、Planner 提示词生成。
- 跨 Widget 协作或共享状态。

## Prototype Workflow

| Step | Actor | 行为 | 输出/说明 |
| --- | --- | --- | --- |
| 1 | Orchestrator | 构造 `AgentState`，内部创建 `MemoryNotebookWidget` 并保持引用 | `AgentState` 暴露 `MemoryNotebookWidget Widget { get; }` 或等价访问器 |
| 2 | Planner / LLM | 读取 `MemoryNotebookWidget.Tools`，选择 `memory_notebook_replace` 并发起调用 | 通过 `ToolExecutor` 或直接回调 Widget 执行同步方法 |
| 3 | ToolExecutor | （可选）使用轻量 `WidgetToolHandler` 将工具调用委派给 `MemoryNotebookWidget.ExecuteTool` | `ToolExecutionContext` 携带解析后的参数与调用上下文 |
| 4 | MemoryNotebookWidget | 在 `ExecuteTool` 中验证参数、更新 `_notebookContent`、写入 AgentState | 成功返回 `ToolHandlerResult.Success`，并记录调试日志 |
| 5 | AgentState | 在写入流程中刷新 Notebook 快照（沿用既有 Entry 机制） | 为渲染阶段准备最新内容 |
| 6 | AgentState | 在上下文渲染阶段调用 `MemoryNotebookWidget.RenderLiveScreen` | 返回 Markdown 片段供 [LiveScreen] 注入 |

> 若工具执行失败，Widget 应返回错误状态并附带原因，AgentState 不更新 Notebook，但仍可记录失败日志。

## AgentState 与 Widget 的协作关系

- **宿主职责**：`AgentState` 负责创建并持有 `MemoryNotebookWidget`，对外暴露同步访问器（例如 `GetMemoryNotebookWidget()`）。
- **状态一致性**：Widget 内部维护 Notebook 最新全文；`AgentState` 仍可保留 `_memoryNotebook` 字段以兼容旧逻辑，但写入统一通过 Widget 封装方法完成，确保事实源唯一。
- **渲染流程**：`AgentState.RenderLiveContext()` 在处理 LiveScreen 注入时直接调用 `MemoryNotebookWidget.RenderLiveScreen(context)`，若返回文本则追加到现有 Markdown。
- **工具桥接**：为了复用 `ToolExecutor`，可以在 `Program` 或 `AgentState` 内部构造一个 `WidgetToolHandler`（实现 `IToolHandler`），其 `ExecuteAsync` 直接调用 Widget 的同步 `ExecuteTool` 并包装为 `ToolHandlerResult`。
- **未来扩展**：当引入其他 Widget 时，可将 `AgentState` 中的单实例扩展为 `List<IWidget>`，再考虑抽象注册点；当前实现保持“单 Widget + 最小接口”。

## 历史注入与多层级呈现

首版沿用 AgentState 现有的 Markdown 拼接逻辑，不额外维护多层级视图。Widget 仅需返回单一 LiveScreen 片段，历史裁剪、摘要化等能力留待未来需要时再评估。

## Risk & Deferral

- **多层级渲染策略**：彻底移除本版本对 `MultiLevelSections` 的依赖，待 Phase B 再行评估；首版仅输出单一 Markdown 片段。
- **高级工具集**：`memory_notebook_replace` 先跑通；append / summarize / diff 等能力待 Notebook 基础路径稳定后再扩展。
- **Widget 生命周期管理**：明确不提供 `InitializeAsync` / `DisposeAsync` 等钩子；若出现资源管理需求，将另起设计讨论。
- **遥测与 Planner 集成**：保持最小化方案——仅记录 Debug 日志、向 Planner 提供 Markdown；如需结构化提示或遥测汇聚，届时再补充能力。
- **跨 Widget 交互**：当前默认 Widget 之间互不依赖，未来若需协作将重新评估封装边界与安全策略。

## MemoryNotebookWidget 轮廓

- **状态**：持有 Memory Notebook 内容的只读快照或委托，负责读写 `_memoryNotebook`。
- **工具**：
  - `memory_notebook_replace`（迁移自旧 `MemoReplaceLiteral`）
  - 后续可追加 `memory_notebook_append`, `memory_notebook_summarize` 等
- **渲染**：输出带标题的 Markdown 区块，统一处理换行、空内容情况。
- **遥测**：通过 `ToolHandlerResult.Metadata` 记录操作类型、长度变化、锚点命中情况。

## 与 Conversation History 的关系

| 角色 | LiveInfo 时代 | Widget 方案 |
| --- | --- | --- |
| 状态存储 | AgentState 字段 + 调用方自定义逻辑 | Widget 内部封装，必要时触发 AgentState API |
| 工具入口 | 离散的 `ITool` 实现 | Widget 暴露的工具集合（`Tools`） |
| [LiveScreen] | AgentState 手工注入 | Widget 渲染并返回 [LiveScreen] 片段 |
| 遥测/调试 | 调用方自行写入 Metadata | Widget 统一封装，便于记录与回溯 |

## 验收与测试策略

- **功能验收**
    - `AgentState` 初始化后，可通过公开访问器获取 `MemoryNotebookWidget`，其 `Tools` 列表包含 `memory_notebook_replace`。
    - 成功调用 `memory_notebook_replace` 后，Widget 与 `AgentState` 的 Notebook 内容保持一致，LiveScreen 渲染展示最新内容。
    - 工具执行失败（如缺少必需参数）时，返回错误状态且 Notebook 不变，同时写入调试日志。
    - 渲染阶段调用 `RenderLiveScreen`，总是输出包含标题的 Markdown；内容为空时仍返回占位提示。
- **日志与调试**
    - 工具调用开始、结束、异常路径均记录 `DebugUtil.Print("MemoryNotebookWidget", ...)`。
- **验证方式**
    - 以端到端实验（控制台读写 Notebook → LiveScreen 输出）作为首要验证手段；阶段性跳过新增独立单元测试。

## 迭代路线建议

1. **Phase A**：实现同步版 `IWidget`、`MemoryNotebookWidget`，并让 `AgentState` 直接持有它；完成基础工具迁移。
2. **Phase B**：在确保 Notebook 工具跑通的前提下，再评估 Planner、遥测与多 Widget 支持所需的抽象。
3. **Phase C**：针对潜在的持久化、异步 IO 需求，逐步向上游扩展异步接口，并补充测试与遥测能力。

## 下一步

- 在 `AgentState` 构造流程中插入 `MemoryNotebookWidget` 初始化与访问器实现。
- 衔接 `ToolExecutor` → `MemoryNotebookWidget.ExecuteTool` 的同步调用链，复用旧 `MemoReplaceLiteral` 的参数契约。
- 梳理 Widget ↔ AgentState ↔ Provider 的调用序列图，验证工具调用与 [LiveScreen] 注入的时序；必要时补充端到端操作手册。
- 根据实际需要，再行补充单元测试或迁移持久化策略。

---

> 本文为初稿，欢迎在后续迭代中补充接口签名、示意图与实现注意事项。
