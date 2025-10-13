# LiveContextProto Widget 设计概念草案

*版本：0.1-draft · 更新日期：2025-10-14*

> 关联文档：
> - 《ToolFrameworkDesign.md》：供 Widget 暴露工具时复用的 `ITool` 契约与参数抽象。
> - 《ToolCallArgumentHandling.md》：说明工具参数解析的“宽进严出”策略。
> - 《ConversationHistory 抽象重构蓝图 · V2》：提供 AgentState、LiveScreen 与上下文渲染的总体设计目标。

---

## 文档目的

- 给出 LiveContextProto 场景下 **Widget** 的概念、职责边界与最小接口草案。
- 取代早期松散的 LiveInfo 约定，统一“状态建模 → 操作接口 → Live Screen 呈现”三类功能。
- 为 Memory Notebook 的 Widget 化改造提供设计基准，并指导后续的 Planner / Provider / Orchestrator 集成。

## 背景动机

| 痛点 | 现状 | 影响 |
| --- | --- | --- |
| LiveInfo 松散 | 记忆、系统指令、工具清单等信息由调用方各自维护 | 状态更新缺乏一致性，无法审计或复用已有逻辑 |
| 工具与状态分离 | 工具操作直接操纵 AgentState 内部字段 | 缺少封装，难以形成可复用的“功能组件” |
| Live Screen 绑定零散 | 上下文渲染时需要额外判断何时插入 Live Screen | 扩展新信息源时易漏打点或重复输出 |

Widget 旨在将某类信息的 **数据模型、操作工具、呈现逻辑** 内聚封装成一个内存对象，便于灵活组合和生命周期管理。

## 设计原则

1. **单一事实源**：Widget 内部维护自身状态/视图，并通过受控入口与 AgentState 协作，避免各处散落的 LiveInfo。
2. **工具化操作**：Widget 暴露一组使用 `ITool` 契约描述的工具，供 LLM 调用时执行具体操作。
3. **上下文可视化**：Widget 负责渲染自身 Live Screen 内容（Markdown/纯文本等），并决定注入策略。
4. **供应商无关**：Widget 不直接依赖特定 Provider；渲染的结果由 AgentState / Orchestrator 统一注入上下文。
5. **渐进式扩展**：接口尽量保持最小可用，允许未来引入遥测、事件化、Planner 提示等能力。

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
    /// 渲染 Live Screen 片段；返回 null 表示无内容。
    /// </summary>
    ValueTask<string?> RenderLiveScreenAsync(WidgetRenderContext context, CancellationToken cancellationToken);

    /// <summary>
    /// 派发工具调用。toolName 必须来自 <see cref="Tools"/> 集合。
    /// </summary>
    ValueTask<ToolHandlerResult> ExecuteToolAsync(
        string toolName,
        ToolExecutionContext executionContext,
        CancellationToken cancellationToken
    );
}
```

- `Tools` **必须返回只读的 `ITool` 集合**（如 `ImmutableArray<ITool>` 或 `IReadOnlyList<ITool>`），确保 `ToolCatalog`/Planner 读取时不会破坏 Widget 内部状态。
- `RenderLiveScreenAsync` 允许 Widget 使用 `WidgetRenderContext` 获取 AgentState 快照或调试信息，默认以 Markdown 片段输出。
- `ExecuteToolAsync` 统一负责工具分派，可选择内部 switch 或字典映射。

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

## AgentState 与 Widget 的协作关系

- **Widget 注册位置**：Widget 集合由 `AgentState` 直接持有。AgentState 在构造或启动阶段创建 `WidgetCatalog`，并注册所有 `IWidget` 实例。这样可以保证所有历史写入仍由 AgentState 统一掌控，Widget 通过受控 API 更新状态或插入历史条目。
- **生命周期**：Widget 与 AgentState 共生，遵循 orchestrator 的单线程执行假设，避免额外同步原语；若未来需要热插拔，可在 AgentState 内部提供受控的注册/注销接口。
- **状态访问**：Widget 通过 `WidgetRenderContext.AgentState` 读取历史快照，并调用 AgentState 暴露的语义化写入方法（例如 `AppendNotebookEditEntry`）来记录差异或操作摘要。
- **工具桥接**：AgentState 暴露 `WidgetCatalog.CreateToolHandlers()`（类似现有 `ToolCatalog.CreateHandlers()`）供 `ToolExecutor` 使用，实现“工具调用 → Widget → AgentState 历史写入”的闭环。

## WidgetCatalog 与 ToolCatalog 协作

1. **Widget 注册**：启动阶段创建 `WidgetCatalog`，注册所有 Widget 实例。
2. **工具汇总**：`WidgetCatalog` 迭代各 Widget 的 `Tools` 集合，生成 `WidgetToolAdapter`（实现现有 `ITool` 接口），统一交给 `ToolCatalog`。
3. **执行流程**：
   - LLM 工具调用 → `ToolExecutor` → `WidgetToolAdapter.ExecuteAsync`
   - Adapter 根据 `toolName` 回调对应 Widget 的 `ExecuteToolAsync`
4. **Live Screen 注入**：AgentState 渲染上下文时遍历 Widget，调用 `RenderLiveScreenAsync` 获取 Markdown，并按蓝图约定将其附着在最新的 ModelInput/ToolResult 消息上。

## 历史注入与多层级呈现

为了减少 Widget 维护历史副本的负担，同时让上下文可以针对不同模型动态调整细节，我们在“写入阶段”一次性生成多层级的视图，在“渲染阶段”再决定展示粒度。

- **固定快照**：每个 Widget 在变更发生时，更新自身持有的“最新状态”对象，并将该快照注入到最近一次 `ModelInputEntry` 的扩展字段中。渲染层只在“最后一条待发给模型的用户消息”上，通过 `LiveScreenDecoratedMessage` 暴露此全量快照，其余历史消息默认隐藏但仍保留数据，便于回放或高保真调试。
- **分层信息**：Widget 在处理工具调用时，同时生成 `Live / Recent / Gist` 三种粒度的数据：
    - `Live`：与当前快照一致的全量信息（通常作为 Live Screen 片段）。
    - `Recent`：针对近几次操作的精细 diff 或上下文（可含局部文本对比、锚点信息等）。
    - `Gist`：更长远历史的摘要或确认信息，例如“第 3 次编辑已完成”。
 这些数据可以直接写入关联的 `ToolResultsEntry.Metadata` 或专用的 Widget 历史条目，作为后续渲染的原料。
- **渲染时选择细节**：`RenderLiveContext()` 根据 Provider 或调用方传入的参数（例如上下文窗口大小、任务场景），在 `Live / Recent / Gist` 之间按需裁剪：
    - 上下文预算充足 → 展示 `Live + Recent`。
    - 预算紧张 → 仅保留 `Live + Gist` 或只保留 `Live`。
    - 未来可扩展为支持动态阈值（如“最近 N 次操作”）。
- **历史压缩（可选）**：旧的 `Recent`/`Gist` 数据在超过阈值后，可以由 AgentState 主动压缩或丢弃，防止 Metadata 无限增长。由于写入阶段已经生成多层级数据，压缩操作不会影响最新状态展示。

这一方案使 Widget 专注于“生成最新状态 + 多层级描述”，无需维护时间序列；而动态呈现策略保留在 AgentState 的渲染阶段，实现 FIR 式的“渐淡记忆”。

### MultiLevelSections 抽象

为避免在不同细节层级之间复制文本，我们引入共用的数据结构 `MultiLevelSections`，同时存放三档内容：

```csharp
internal enum DetailLevel {
    Live,
    Recent,
    Gist
}

internal sealed class MultiLevelSections {
    public IReadOnlyList<KeyValuePair<string, string>> Live { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Recent { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Gist { get; }

    public IReadOnlyList<KeyValuePair<string, string>> GetSections(DetailLevel level) => level switch {
        DetailLevel.Live => Live,
        DetailLevel.Recent => Recent,
        DetailLevel.Gist => Gist,
        _ => Live
    };
}
```

- 三个列表可共享同一批字符串实例（内部约定由构造逻辑保证），减少不同层级之间的内存复制。
- `ModelInputEntry` 与 `ToolCallResult` 改为持有 `MultiLevelSections Sections` 字段，原有的 `ContentSections`/`Result` 字段可逐步退场或转为 `Live` 层级的兼容包装。
- 渲染阶段调用 `GetSections(level)` 获得对应粒度的内容，Provider 无需感知内部细节划分。

### 渲染策略

`RenderLiveContext()` 在遍历历史条目时，根据目标模型的上下文预算、任务类型等因素决定使用的 `DetailLevel`，并将选中的 `Section` 暂存于轻量包装对象中：

- 最后一条准备发送给模型的用户输入 → `DetailLevel.Live`。
- 最近几条输入/工具结果 → `DetailLevel.Recent`（diff、上下文补丁）。
- 更早的条目 → `DetailLevel.Gist`（摘要、确认信息）。

包装对象依旧实现 `IModelInputMessage` / `IToolResultsMessage` 接口，只是将 `ContentSections` 映射为对应层级，保持历史条目的不可变性。

### 写入端协作

- Widget 在工具调用成功后构造 `MultiLevelSections`：
  - `Live`：全量最新状态（Memory Notebook 为整本文本）。
  - `Recent`：数次操作的 diff 或局部上下文。
  - `Gist`：较旧操作的概述（“第 5 次编辑成功”）。
- `ToolHandlerResult.Metadata` 可继续记录操作类型、长度变化等诊断信息，与 `MultiLevelSections` 的 textual 数据互补。
- AgentState 统一负责将 `MultiLevelSections` 写入 `ModelInputEntry` / `ToolResultsEntry`，调用方无需关心渲染策略细节。

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
| Live Screen | AgentState 手工注入 | Widget 渲染并返回 Live Screen 片段 |
| 遥测/调试 | 调用方自行写入 Metadata | Widget 统一封装，便于记录与回溯 |

## 迭代路线建议

1. **Phase A**：定义 `IWidget`、`WidgetCatalog`、`WidgetToolAdapter`，迁移 Memory Notebook 工具与渲染逻辑。
2. **Phase B**：将现有 Sample/Stub 工具替换为 Widget 版本；为 Planner 提供 Widget 列表导出。
3. **Phase C**：探索 Widget 遥测、事件化（记录状态变更历史）与 Planner 提示词生成。
4. **Phase D**：引入高级 Widget（如任务面板、通知中心），完善 UI 与上下文的协同策略。

## 待定问题

- **Widget 生命周期**：是否需要显式的 `InitializeAsync` / `DisposeAsync`，例如监听外部事件或订阅存储更新？
- **多 Widget Live Screen 排序**：是否按注册顺序输出，或提供优先级字段？
- **跨 Widget 协作**：是否允许 Widget 之间相互调用工具/共享状态？若允许需要定义边界与安全策略。
- **遥测统一化**：如何将 Widget 输出的 Metadata 汇总到全局遥测系统？
- **Planner 集成**：是否需要 Widget 提供结构化提示（例如 JSON schema），以便生成更精确的 LLM 提示词？

## 下一步

- 与 Memory Notebook 迁移设计对齐，补充更详细的 API 与示例代码。
- 梳理 Widget ↔ AgentState ↔ Provider 的调用序列图，验证工具调用与 Live Screen 注入的时序。
- 制定单元测试策略：例如 Widget 工具调用的输入输出、Live Screen 渲染幂等性等。

---

> 本文为初稿，欢迎在后续迭代中补充接口签名、示意图与实现注意事项。
