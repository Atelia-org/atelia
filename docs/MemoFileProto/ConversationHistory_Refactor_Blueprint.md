# Memo: Conversation History 抽象重构蓝图

*版本：0.1 · 更新日期：2025-10-10*

## 背景与目标

MemoFileProto 的早期实现直接把 OpenAI Chat Completion 所需的消息结构当作内部对话历史。这种做法在与 OpenAI 模型交互时足够简单，但当我们尝试切换到 Anthropic Claude 或希望在同一会话里混用多家模型时，就暴露出以下问题：

- **角色约定不兼容**：OpenAI 将一次批量工具调用拆成若干 `tool` 消息；Anthropic 要求 Assistant ↔ Tool 消息严格一一交错（单条工具响应），导致奇偶序列被破坏。
- **历史语义扁平化**：所有角色共享一套字段，难以表达工具调用、工具结果、调试信息等特有语义，后续功能很难扩展。
- **缺少供应商适配层**：历史消息一旦以 OpenAI 结构存储，就很难在发送前再做转换，从而失去“一份历史，多种格式消费”的灵活性。

本次重构的目标：

1. 引入 **强类型的 HistoryEntry 分层**，让不同事件拥有清晰的数据模型，并以追加式的 AgentHistory 统一持有。
2. 将“历史存储”与“供应商调用”解耦，Provider 客户端只感知 AgentHistory 并负责完成模型调用。
3. 支撑在单个会话内 **混合多种模型/功能接口**（例如规划走 Claude，执行走 OpenAI Function Calling），同时保持一致的历史视图。
4. 为后续的 Live Context、工具遥测、记忆系统等能力打好基础，同时保持原型期的实现简洁。

## 当前痛点梳理

| 痛点 | 现象 | 后果 |
| --- | --- | --- |
| 批量工具调用破坏消息交错 | Claude 期望一次 Tool 调用→一次 Tool 响应，而我们返回多条 `tool` 消息 | Claude 端解析失败或导致上下文错乱 |
| 历史消息缺乏结构 | 所有消息都只有 `role + content` | 难以附加例如 `ToolCallId`、执行状态、环境信息等元数据 |
| 历史与请求耦合 | `_conversationHistory` 已经是 OpenAI 消息格式 | 新增供应商时需要改遍全链路 |
| 缺少统一仓储 | 调用栈、调试、记忆注入写在多个方法里 | 难以追踪和复用 |

## 目标架构概览

```
┌──────────────────┐
│  History Store    │  <— 强类型 HistoryEntry
└────────┬──────────┘
         │
    │  IProviderClient
         ▼
┌──────────────────┐      ┌─────────────────────┐
│ OpenAI Client    │◀────▶│ Anthropic Client   │ (等)
└──────────────────┘      └─────────────────────┘
         │                          │
         ▼                          ▼
   OpenAI 消息数组             Claude ToolMessage
```

核心思想：

- **History Store** 仅追加记录抽象事件（系统指令、模型输入、模型输出、工具调用/结果、调试日志等），同时持有当前系统指令与内存状态。
- **Provider 客户端** 从 AgentHistory 读取最新视图，内部决定如何组合为目标供应商需要的调用参数。
- 当一次调用需要多个供应商时（例如先让 Claude 规划、再让 OpenAI 执行代码修改），每次调用前动态选择 Provider 实现，而 AgentHistory 不做任何区分。

### 三阶段请求流水线

为了保持责任边界清晰，本方案把一次完整的模型调用拆成三个阶段：

1. **Long History (AgentHistory)**：长期累积的原始事实，只追加不修改，记录所有 SetSystemInstruction、ModelInput、ModelOutput 等事件。
2. **RenderLiveContext() 输出**：根据当前调用需求，将长历史转化为“最近上下文 + LiveScreen”视图；此阶段会克隆必要条目并注入 System Instruction、Memory Notebook 等动态信息，产出 `IContextEntry` 列表。
3. **CallModelAsync (Provider 内部)**：Provider 客户端接收 `IReadOnlyList<IContextEntry>`，进一步适配为 OpenAI、Anthropic 或其他后端要求的最终请求格式，并负责流式解析。

这种分层有助于在不修改 Long History 的情况下灵活调整 Live 层注入策略或替换底层 Provider，也便于后续对不同模型实现差异化处理。

## AgentHistory 角色划分

- **不可篡改的追加式结构**：对外仅公开读取与追加 API，不提供任意位置编辑或删除历史的能力。
- **自增版本号**：每次追加 Entry 时递增 `HistoryVersion`，便于调试与未来的持久化拓展。
- **持有运行时状态**：内部维护 `_systemInstruction`、`_memoryNotebookContent` 等“最新状态”字段；追加 `SetSystemInstructionEntry` / `SetMemoryNotebookEntry` 时通过 `Append` 统一同步更新字段。
- **Live 数据渲染**：保留 `BuildLiveContext()` 风格的读取接口（产出 `IReadOnlyList<IContextEntry>`），在渲染阶段合并 [Memory Notebook] 等动态信息并生成只读副本；此过程不会修改历史中原始的 `HistoryEntry`。
- **单线程约束**：默认挂靠在单线程 Agent 管理循环上，不支持并发写入；如需并发需在调用方自行加锁。
- **暂不支持历史回放**：本阶段不提供历史状态重建/时间穿越能力，后续再根据原型需求拆分。

## HistoryEntry 类型层次

建议的类型（以 C# `record` 示意）：

```csharp
abstract record HistoryEntry {
    long SequenceNumber { get; init; }
    DateTimeOffset Timestamp { get; init; }
    HistoryEntryKind Kind { get; }
    IReadOnlyDictionary<string, object?> Metadata { get; init; }
}

abstract record ContextualHistoryEntry : HistoryEntry, IContextEntry {
    public abstract ContextEntryRole Role { get; }
    public abstract string Content { get; }
    public virtual IReadOnlyList<ToolCallRequest>? ToolCalls => null;
    public virtual string? LiveScreen => null;
}

record SetSystemInstructionEntry(string Text) : HistoryEntry;
record SetMemoryNotebookEntry(string Content) : HistoryEntry;
record DebugLogEntry(string Category, string Message) : HistoryEntry;

record ModelInputEntry(
    string Channel,
    string RawInput,
    string StructuredContent
) : ContextualHistoryEntry {
    public override ContextEntryRole Role => ContextEntryRole.ModelInput;
    public override string Content => StructuredContent;

    public IContextEntry WithLiveScreen(string? liveScreen) =>
        new DecoratedContextEntry(this, liveScreen);
}

record ModelOutputEntry(
    string? Plan,
    string ResponseMarkdown,
    IReadOnlyList<ToolCallRequest> ToolCalls
) : ContextualHistoryEntry {
    public override ContextEntryRole Role => ContextEntryRole.ModelOutput;
    public override string Content => ResponseMarkdown;
    public override IReadOnlyList<ToolCallRequest>? ToolCalls => ToolCalls;
}

record ToolCallResult(
    string ToolName,
    string ToolCallId,
    ToolExecutionStatus Status,
    string ResultMarkdown
);

record ToolCallResultsEntry(
    string OverallResult,
    IReadOnlyList<ToolCallResult> ToolCalls
) : ContextualHistoryEntry {
    public override ContextEntryRole Role => ContextEntryRole.ToolResult;
    public override string Content => OverallResult;
    public override IReadOnlyList<ToolCallRequest>? ToolCalls => null;

    public IContextEntry WithLiveScreen(string? liveScreen) =>
        new DecoratedContextEntry(this, liveScreen);
}

record ToolCallRequest(string ToolName, string ToolCallId, JsonDocument Arguments);
```

`DecoratedContextEntry` 是一个仅实现 `IContextEntry` 的轻量包装，可携带 LiveScreen，并引用原始 `ContextualHistoryEntry`，从而避免被再次追加到 `AgentHistory`。

说明：

- `SequenceNumber` 与 `AgentHistory.Append(...)` 时自增的版本号保持一致，便于基于顺序做调试或回滚。
- **Metadata** 用于附加可选信息（例如来源、耗时、Token 统计），保持向后兼容。
- **ModelOutputEntry** 内部聚合同一轮推理生成的 `ToolCallRequest` 序列，方便在不同供应商之间成组处理。
- **ToolCallResult** 一一对应单个工具调用的执行结果；`ToolCallResultsEntry` 则代表一次模型响应内的整体结果汇总，按顺序与 `ModelOutputEntry.ToolCalls` 配对。
- **SetSystemInstructionEntry / SetMemoryNotebookEntry** 负责记录全局状态类变更，两者都通过 `AgentHistory.Append(...)` 写入；`Append` 在追加时会同步更新内存中“当前值”并递增版本号。
- Debug 等特殊条目也在 History 层保留，方便做审计或回放。
- **补充说明**：`ToolCallRequest` 仅作为 `ModelOutputEntry.ToolCalls` 的嵌套结构存在，不会追加独立的 `ToolCallRequest` 历史条目。
- **RenderLiveContext 副本**：如需注入 LiveScreen 内容，`RenderLiveContext()` 通过返回仅实现 `IContextEntry` 的包装对象（例如 `DecoratedContextEntry`）来承载额外数据，原始历史条目保持精简且不会被误追加回历史。

## ContextEntry 接口与角色

为了在类型上区分“可持久化的长历史”与“供 Provider 消费的即时上下文”，我们增加 `IContextEntry` 接口，定义最小可用字段：

```csharp
interface IContextEntry {
        ContextEntryRole Role { get; }
        string Content { get; }
        IReadOnlyList<ToolCallRequest>? ToolCalls { get; }
        string? LiveScreen { get; }
}

enum ContextEntryRole {
        System,
        ModelInput,
        ModelOutput,
        ToolResult
}
```

- Role 使用与供应商无关的中立枚举值，避免在此层引入 `assistant`（OpenAI）、`model`（Gemini）等特定 API 术语。
- `LiveScreen` 独立承载波动信息，由各 `IProviderClient` 在最终拼装阶段决定如何与 `Content` 组合。Anthropic 客户端可选择把 LiveScreen 注入到哪条 ToolResult，Gemini 客户端则可以复用 Content/Part 的结构化表达。
- 只有 `ModelInputEntry`、`ModelOutputEntry`、`ToolCallResultsEntry` 等参与模型调用的历史条目实现 `IContextEntry`，例如：
    - `ModelInputEntry.WithLiveScreen()` 返回一个仅实现 `IContextEntry` 的封装类型（如 `DecoratedContextEntry`），既能带上动态的 LiveScreen，又不会被误传入 `AgentHistory.Append`。
    - `ModelOutputEntry` 原样实现接口，用于传递模型响应及潜在的工具调用请求。
- `SetMemoryNotebookEntry`、`DebugLogEntry` 等永远不应该出现在上下文中的历史条目不实现 `IContextEntry`，从编译期阻止它们进入 Provider 管线。

得益于接口分层，`BuildLiveContext()` 可以直接产出 `IReadOnlyList<IContextEntry>`，并根据需要对最近的感知消息做装饰，而无需在函数内部先拼接字符串。

### 辅助类型

```csharp
enum HistoryEntryKind {
    SetSystemInstruction,
    SetMemoryNotebook,
    ModelInput,
    ModelOutput,
    ToolCallResult,
    DebugLog,
    // 后续可扩展：LiveContextSnapshot、PlannerDirective 等
}

enum ToolExecutionStatus { Success, Failed, Skipped }
```

## Provider 客户端设计

接口示意：

```csharp
interface IProviderClient {
    IAsyncEnumerable<ModelOutputDelta> CallModelAsync(
        AgentHistory history,
        IReadOnlyList<IContextEntry> context,
        ProviderCallOptions options,
        CancellationToken cancellationToken
    );
}
```

- `ModelOutputDelta` 是对原有 `ChatResponseDelta` 的更名，当前阶段保持最小字段集合（文本片段、工具调用片段等），为未来细分铺路。
- `ProviderCallOptions` 控制是否注入 Live Context、是否开启附加调试信息等。
- Provider 客户端封装底层 SDK 或 HTTP 细节，对外统一为流式 delta；测试场景可通过假实现返回预设 delta 序列。
- Provider 实现需把 `AgentHistory` 视为只读，并消费预先构建好的 `IContextEntry` 列表；禁止直接修改或回写历史条目。

### OpenAI 客户端行为

1. 消费传入的 `IReadOnlyList<IContextEntry>`，将其中的系统指令、输入、输出、工具结果等信息映射为 OpenAI 所需的消息数组。
2. 解析流式返回，将底层 `ChatResponseDelta` 映射为 `ModelOutputDelta`（文本、工具调用等），并驱动上层追加 `ModelOutputEntry`。
3. 多个工具调用按 OpenAI 风格作为独立 delta 产出，推理结束时合并为匹配的 `ToolCallResultsEntry`（其中的 `ToolCallResult` 与 `ModelOutputEntry.ToolCalls` 一一对应）。

### Anthropic 客户端行为

1. 同样消费 `IReadOnlyList<IContextEntry>`，在内部将多工具调用聚合成单条工具消息，保证 Claude 的奇偶交错约定。
2. 输出 `ModelOutputDelta` 序列，在流式阶段完成工具调用解析与信息注入。
3. 推理结束后，把聚合后的结果汇总成一条 `ToolCallResultsEntry`，内部保留逐项 `ToolCallResult`，维持与 OpenAI 的配对精度。

### 其他供应商

- **Azure OpenAI**：共享 OpenAI 客户端实现或复用消息构造逻辑。
- **本地 vLLM/Transformers**：由客户端内部决定是否直接执行 Python/CUDA 组件，外部 API 不受影响。

### ModelOutputDelta 管线

- **最小化改动**：短期内仅将现有 `ChatResponseDelta` 重命名为 `ModelOutputDelta`，沿用既有字段（文本增量、工具调用片段等），将复杂的细粒度分类推迟到后续迭代。
- **历史落盘策略**：在模型流式完成后，再由上层把累计的 delta 汇总成单个 `ModelOutputEntry` 与一条 `ToolCallResultsEntry`（其中含多个 `ToolCallResult`），保持一次推理完整性。
- **信息注入**：如需在流式阶段插入额外调试或分析数据，先在 delta 管线完成后再统一写入 AgentHistory，避免 Provider 客户端与历史结构耦合。

## AgentHistory API 草案

```csharp
class AgentHistory {
    private readonly List<HistoryEntry> _entries = new();
    private long _version;
    private string _systemInstruction = "";
    private string _memoryNotebookContent = "（尚无内容）";

    public long Version => _version;
    public IReadOnlyList<HistoryEntry> Entries => _entries;
    public string SystemInstruction => _systemInstruction;
    public string MemoryNotebookContent => _memoryNotebookContent;

    public void Append(HistoryEntry entry) {
        _entries.Add(entry);
        _version++;
    }

    public void SetSystemInstruction(string instruction) {
        Append(new SetSystemInstructionEntry(instruction));
        _systemInstruction = setSystemInstruction.Text;
    }

    public void SetMemoryNotebookContent(string content) {
        Append(new SetMemoryNotebookEntry(content));
        _memoryNotebookContent = setMemoryNotebook.Content;
    }

    public AgentHistorySnapshot CreateSnapshot() => new(
        _version,
        _entries.ToList(),
        _systemInstruction,
        _memoryNotebookContent
    );
}

record AgentHistorySnapshot(
    long Version,
    IReadOnlyList<HistoryEntry> Entries,
    string SystemInstruction,
    string MemoryNotebookContent
);
```

- Agent 逻辑只操作 `AgentHistory`，不再直接拼 `ChatMessage`。
- `RenderLiveContext()` 等方法从 `AgentHistorySnapshot` 中读取数据，生成带 LiveScreen 的只读副本并注入 System Instruction、Memory Notebook 等动态信息。

## 迁移计划（分阶段）

### Phase 0：准备
- 梳理现有 `_conversationHistory` 使用点，标记读取/写入的位置。
- 编写临时 Adapter，便于在迁移期间同时维护旧结构和新结构。

### Phase 1：引入 HistoryEntry 类型
- 实现新的 `HistoryEntry` 层次和 `AgentHistory` 类。
- 在 `LlmAgent` 中新增 `_history` 字段，写入与 `_conversationHistory` 同步的事件。
- 编写单元测试覆盖基本事件追加、回滚行为，并验证 `HistoryVersion` 自增。

### Phase 2：Provider 客户端改造
- 定义统一的 `IProviderClient.CallModelAsync` 接口，返回 `ModelOutputDelta` 序列（同步完成 `ChatResponseDelta` → `ModelOutputDelta` 更名）。
- 基于现有 OpenAI 客户端实现适配，确保只需要少量改动即可输出新的 delta 类型。
- 为 Anthropic 客户端实现聚合逻辑，保证奇偶交错同时减轻上层负担。

### Phase 3：供应商切换管线
- 引入一个 `ProviderRouter`，根据所需模型类型选择对应 Provider 客户端。
- 替换 `_client.StreamChatCompletionAsync` 的调用入口，使之调用统一的 Provider 接口。
- 验证同一会话内轮流调用 OpenAI/Anthropic 的可行性。

### Phase 4：清理与增强
- 移除旧的 `_conversationHistory` 结构。
- 扩展工具执行管线，记录更多元数据（耗时、输入参数摘要、错误堆栈）。
- 更新 CLI `/history` 命令，使其按照新的术语（ModelInput/ModelOutput 等）展示。

## 回归测试要点

- OpenAI 流程：确认多工具调用仍然按多条 `tool` 消息输出，`finish_reason=tool_calls` 工作正常。
- Anthropic 流程：单条 Tool 消息包含所有工具结果，保证奇偶交错。
- 混合会话：先运行 Claude 规划，再调用 OpenAI 工具执行；历史应按时间顺序正确记录。
- 回滚场景：异常时 `Rollback` 能恢复历史并保持数据类型一致。

## 风险与缓解

| 风险 | 说明 | 缓解策略 |
| --- | --- | --- |
| 类型膨胀导致维护负担 | `HistoryEntry` 细分过多 | 以最小足够集合起步，并通过 Metadata 兼容扩展 |
| Provider 实现差异 | 多供应商接口各有约束 | 编写测试用例逐条验证协议约定，并集中维护文档 |
| 双写期间状态同步 | 迁移阶段 `_conversationHistory` 与 `_history` 需同步 | 通过装饰器或中间层统一写入，确保二者一致 |
| 性能开销 | 流式 delta 解析与构造增加遍历次数 | 历史条目数量有限（本地代理），影响可忽略；如需优化可缓存最近输出 |

## 未来扩展

- **Live Context + History 协调**：HistoryEntry 可以新增 `LiveContextInjectedEntry`，记录每次调用附带的 Live Context，方便调试。
- **Memory Notebook**：将 Memory 操作也抽象为 `HistoryEntry`，便于回溯记忆编辑过程。
- **多模型策略**：在 ProviderRouter 上层增加 Strategy 层，例如“规划模型”“执行模型”“评审模型”各自使用不同 Provider 客户端。
- **与 Semantic Kernel 集成评估**：若未来需要 Planner/Skill 生态，可将 SK 作为 orchestrator，内部依旧透过 Provider 客户端调用底层模型。

## 下一步建议

1. 完成 Phase 0~1 的代码草稿，把 HistoryEntry 与 AgentHistory 骨架跑通。
2. 对已有会话数据做一次“格式转换”演练，验证类型设计能覆盖现状。
3. 改造 OpenAI/Anthropic Provider 客户端，确认 `ModelOutputDelta` 流程跑通并通过回归测试。
4. 更新 CLI `/history` 输出，让开发者在调试时直观看出新的术语与 HistoryEntry 类型。
5. 在重构过程中配合 DebugUtil 记录新的历史事件，方便检查。

---

*附注：本文档定位为纲领性方案，后续每个 Phase 应补充更细的设计与测试用例说明。*
