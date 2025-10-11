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
┌────────────────────────┐
│    Agent Orchestrator  │
└──────────┬─────────────┘
           │ append events
           ▼
┌────────────────────────┐
│ [1] AgentHistory       │
└──────────┬─────────────┘
           │ RenderLiveContext()
           ▼
┌────────────────────────┐
│ [2] Context Projection │
│   (IContextMessage list) │
└──────────┬─────────────┘
           │ CallModelAsync()
           ▼
┌────────────────────────┐
│ [3] Provider Router    │
└──────┬─────────┬───────┘
       │         │
 ┌─────▼───┐ ┌───▼─────┐
 │OpenAI   │ │Anthropic│ …
 └─────────┘ └─────────┘
       ▲         ▲
       └────┬────┴─ model deltas & tool results
            │
            ▼ aggregate & append
┌────────────────────────┐
│  Orchestrator feedback │
└──────────┬─────────────┘
           │
           └────────────▶ AgentHistory (new events)
```

这幅图展示了最新的三段式请求流水线：

- **[1] AgentHistory**：负责维护仅追加的事件日志和运行时快照（System Instruction、Memory Notebook 等），是唯一的真相源。
- **[2] Context Projection**：由 `RenderLiveContext()` 按当前调用需要生成 `IContextMessage` 只读列表，注入 LiveScreen、内存摘录等易变信息，但不修改历史。
- **[3] Provider Router**：根据调用策略挑选具体的 `IProviderClient`，把 `IContextMessage` 投影重写成供应商协议（OpenAI、Anthropic 等），并将流式增量解析为统一的 `ModelOutputDelta`。

底部的 **Orchestrator feedback** 表示调用协调层在流式推理结束后聚合 `ModelOutputDelta` 与工具结果，再次通过领域方法将新事件追加到 AgentHistory，从而闭合“读取视图 → 调用模型 → 回写事实”的循环。

上述三个阶段共同构成完整的模型调用循环，职责细节在后文“History ↔ Context ↔ Provider 协作”一节中展开。这种分层有助于在不修改 Long History 的情况下灵活调整 Live 层注入策略或替换底层 Provider，也便于后续对不同模型实现差异化处理。

## 与经典设计模式的关系

本方案的架构设计借鉴了多个成熟的软件设计模式，理解这些关联有助于快速把握系统意图并预测其演进方向。

### MVVM（Model-View-ViewModel）模式映射

我们的三阶段流水线与 MVVM 有着天然的对应关系：

| MVVM 层次 | 本方案对应层 | 职责 |
| --- | --- | --- |
| **Model** | **AgentHistory** | 持有不可变的原始数据，是"唯一的真相源"（Single Source of Truth） |
| **ViewModel** | **RenderLiveContext() 输出** | 将 Model 转换为适合特定场景的视图；可动态注入、聚合、过滤数据；不修改 Model |
| **View** | **Provider Client** | 消费 ViewModel 提供的接口，转换为特定平台格式（OpenAI API、Anthropic API 等） |

**核心思想一致性**：

- **单向数据流**：History → Context → Provider Request，避免循环依赖。
- **分离关注点**：History 不知道 Provider 的协议差异，Provider 通过 `IContextMessage` 接口间接消费历史。
- **多视图支持**：同一份 AgentHistory 可渲染为不同 Provider 所需的格式，就像同一个 Model 可绑定多个 UI 框架。

### Event Sourcing（事件溯源）模式

`AgentHistory` 的设计直接借鉴了 Event Sourcing 的核心理念：

- **不可变事件日志**：`EventLog` 字段（`List<HistoryEntry>`）以仅追加方式记录所有历史条目，永不修改已有数据。
- **状态重建能力**：当前的 `_systemInstruction`、`_memoryNotebookContent` 等状态字段可视为"聚合根的内存快照"，后续可通过重放事件日志完整恢复任意时刻的状态（尽管当前阶段暂不实现时间穿越）。
- **系统指令的暂时策略**：目前系统指令仅以运行时字段形式维护，并在上下文渲染时生成 `SystemInstructionMessage`，重放时需依赖默认配置或额外快照；待真实需求出现后再补充专门的历史条目或稳定标识。
- **领域事件语义**：`ModelInputEntry`、`ModelOutputEntry` 等类型明确表达"发生了什么"，而非简单的 CRUD 操作。
- **顺序号延后引入**：单调递增的序列号将交由后续的正式存储/审计层管理，本轮原型暂不实现，避免维护未显式需求的特性。

### CQRS（命令查询职责分离）

尽管本方案未显式引入命令/查询对象，但职责分离的思想贯穿始终：

- **写入侧（Command）**：`SetSystemInstruction()`、`AppendModelInput()` 等方法专注于修改状态并追加条目，内部封装序列号递增与时间戳注入。
- **查询侧（Query）**：`RenderLiveContext()` 及后续可能的 `GetRecentEntries()` 等方法专注于只读视图构建，不产生副作用。
- **分离的价值**：两者可独立优化（例如查询侧可引入缓存，写入侧可批量提交），且测试边界清晰。

### Repository（仓储）模式

`AgentHistory` 本质上扮演了"内存中的领域对象仓储"角色：

- **封装持久化细节**：尽管当前阶段仅在内存中操作，但接口设计（追加、查询、状态管理）为后续引入持久化层（JSON 文件、SQLite、事件存储）预留了空间。
- **领域驱动接口**：`AppendModelInput()` 等方法使用领域术语而非泛型的 `Add()`/`Save()`，提升代码可读性。
- **聚合根管理**：`AgentHistory` 统一管理所有条目与运行时状态，外部无法绕过它直接修改内部集合。

### Strategy（策略）+ Adapter（适配器）模式

`IProviderClient` 接口及其多个实现（OpenAI、Anthropic 等）综合运用了这两种模式：

- **Strategy 模式**：`ProviderRouter`（计划中）可根据模型类型、任务阶段（规划/执行）等条件动态选择 Provider 实现，各策略算法（协议转换逻辑）可独立封装与替换。
- **Adapter 模式**：每个 `IProviderClient` 实现都是对特定厂商 SDK 的适配器，将异构的底层 API（OpenAI 的 Chat Completion、Anthropic 的 Messages API）统一适配为 `IReadOnlyList<IContextMessage> → IAsyncEnumerable<ModelOutputDelta>` 的标准接口。

### Decorator（装饰器）模式

`ContextMessageLiveScreenHelper.AttachLiveScreen()` 是装饰器模式的典型应用：

- **透明扩展**：通过 `LiveScreenDecoratedMessage` 包装原始条目，动态附加 `ILiveScreenCarrier` 能力，而无需修改原始 `HistoryEntry` 定义。
- **组合优于继承**：避免为"带 LiveScreen 的条目"单独定义子类，保持类型层次的简洁。
- **按需装饰**：仅在 `RenderLiveContext()` 需要时才创建装饰对象，原始历史条目保持不变。

### 模式协同的价值

这些模式的组合并非偶然，而是为了支撑以下架构目标：

- **可测试性**：每层职责单一，可独立 mock 与断言。
- **可扩展性**：新增 Provider、调整上下文渲染策略、引入持久化均可局部修改。
- **可维护性**：领域术语贯穿代码，新成员可通过模式名快速理解设计意图。
- **演进友好**：Event Sourcing 保留完整历史，未来可支持撤销、审计、A/B 测试等高级功能。

---

**小结**：本方案并非"为了用模式而用模式"，而是在解决实际问题（多模型支持、历史可追溯、协议差异隔离）时自然收敛到这些经过验证的设计模式上。理解这些关联有助于你在后续实现中做出符合架构意图的决策。

## AgentHistory 角色划分

- **不可篡改的追加式结构**：对外仅公开读取与追加 API，不提供任意位置编辑或删除历史的能力。
- **顺序号与稳定标识**：具体策略见后文“顺序与稳定标识策略”，本阶段不引入 `SequenceNumber`、`StableId` 等字段。
- **持有运行时状态**：内部维护 `_systemInstruction`、`_memoryNotebookContent` 等"最新状态"字段；`SetSystemInstruction()` 直接更新该字段（当前阶段不追加历史条目），`MemoryNotebookEntry` 则通过追加的方式同步更新。
- **Live 数据渲染**：保留 `BuildLiveContext()` 风格的读取接口（产出 `IReadOnlyList<IContextMessage>`），在渲染阶段合并 [Memory Notebook] 等动态信息并生成只读副本；此过程不会修改历史中原始的 `HistoryEntry`。
- **单线程约束**：默认挂靠在单线程 Agent 管理循环上，不支持并发写入；如需并发需在调用方自行加锁。本阶段利用该前提直接在 `AgentHistory` 内部渲染上下文，而不额外生成快照对象。
- **暂不支持历史回放**：本阶段不提供历史状态重建/时间穿越能力，后续再根据原型需求拆分。

### 顺序与稳定标识策略

- 当前原型阶段不维护单调序列号或稳定 Guid，避免为尚未启用的持久化功能增加状态负担。
- 随着事件存储或快照需求明确，将在统一的持久化层内重新引入 `SequenceNumber`、`StableId` 等字段，并配套回放/审计机制。
- 为便于未来迁移，领域方法保留集中注入时间戳的路径，后续在同一位置扩展顺序号写入即可。

## HistoryEntry 类型层次

建议的类型（以 C# `record` 示意）：

```csharp
abstract record HistoryEntry {
    public DateTimeOffset Timestamp { get; init; }
    public abstract HistoryEntryKind Kind { get; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }
}

abstract record ContextualHistoryEntry : HistoryEntry, IContextMessage {
    public abstract ContextMessageRole Role { get; }
}

record MemoryNotebookEntry(string Content) : HistoryEntry {
    public override HistoryEntryKind Kind => HistoryEntryKind.MemoryNotebook;
}

record ModelInputEntry(
    IReadOnlyList<KeyValuePair<string, string>> ContentSections
) : ContextualHistoryEntry, IModelInputMessage {
    public override ContextMessageRole Role => ContextMessageRole.ModelInput;
    public override HistoryEntryKind Kind => HistoryEntryKind.ModelInput;
    IReadOnlyList<KeyValuePair<string, string>> IModelInputMessage.ContentSections => ContentSections;
    public IReadOnlyList<IContextAttachment> Attachments { get; init; } = Array.Empty<IContextAttachment>();
}

record ModelInvocationDescriptor(
    string ProviderId,
    string Specification,  // 例如 "openai-v1"、"openai-responses"、"qwen3"、"qwen3-coder" 等协议规范标识
    string Model
);

record ModelOutputEntry(
    string? Thinking,
    IReadOnlyList<string> Contents,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    ModelInvocationDescriptor Invocation
) : ContextualHistoryEntry, IModelOutputMessage, IToolCallCarrier {
    public override ContextMessageRole Role => ContextMessageRole.ModelOutput;
    public override HistoryEntryKind Kind => HistoryEntryKind.ModelOutput;
    IReadOnlyList<string> IModelOutputMessage.Contents => Contents;
    ModelInvocationDescriptor IModelOutputMessage.Invocation => Invocation;
    IReadOnlyList<ToolCallRequest> IToolCallCarrier.ToolCalls => ToolCalls;
}

record ToolCallResult(
    string ToolName,
    string ToolCallId,
    ToolExecutionStatus Status,
    string Result, // 未来如果工具调用结果支持多模态就在此处替换string为Parts
    TimeSpan? Elapsed
);

record ToolResultsEntry(
    string OverallResult,
    IReadOnlyList<ToolCallResult> Results,
    string? ExecuteError // 非null表示遇到错误导致未全部执行
) : ContextualHistoryEntry, IToolResultsMessage {
    public override ContextMessageRole Role => ContextMessageRole.ToolResult;
    public override HistoryEntryKind Kind => HistoryEntryKind.ToolResult;
    string? IToolResultsMessage.ExecuteError => ExecuteError;
}

record ToolCallRequest(
    string ToolName,
    string RawArguments,
    IReadOnlyDictionary<string, string>? Arguments,  // null = 解析失败
    string? ParseError  // 非 null 时说明解析出错
);

record SystemInstructionMessage(
    string Instruction
) : ISystemMessage {
    public ContextMessageRole Role => ContextMessageRole.System;
    public DateTimeOffset Timestamp { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }
        = ImmutableDictionary<string, object?>.Empty;
}

static class ContextMessageLiveScreenHelper {
    public static IContextMessage AttachLiveScreen(IContextMessage message, string? liveScreen) =>
        string.IsNullOrEmpty(liveScreen)
            ? message
            : new LiveScreenDecoratedMessage(message, liveScreen);

    private sealed record LiveScreenDecoratedMessage(
        IContextMessage Inner,
        string? LiveScreen
    ) : IContextMessage, ILiveScreenCarrier {
        public ContextMessageRole Role => Inner.Role;
        public DateTimeOffset Timestamp => Inner.Timestamp;
        public IReadOnlyDictionary<string, object?> Metadata => Inner.Metadata;
        string? ILiveScreenCarrier.LiveScreen => LiveScreen;
        IContextMessage ILiveScreenCarrier.InnerMessage => Inner;
    }
}
```

说明：

- **顺序策略**：如“顺序与稳定标识策略”小节所述，当前不在 `HistoryEntry` 中维护序列号，后续会随持久化层补齐。
- `Kind` 属性由各派生类型以常量形式覆写，调用方无需在构造时手动赋值，同时为日志与序列化保留稳定标签。
- **Metadata** 用于附加可选信息（例如来源、耗时、Token 统计），保持向后兼容。
- **ToolCallRequest.RawArguments** 永远保留模型输出的原始文本；`Arguments` 则由 Provider 成功解析后提供的弱类型键值对（解析失败时为 `null`），工具层只消费该字典。`ParseError` 字段在解析成功时为 `null`，失败时包含错误信息，从而区分"工具不需要参数"与"参数解析失败"两种情况。
- **ModelOutputEntry.Invocation** 记录触发本次响应时所选用的 Provider / 规范 / 模型三元组，便于在历史层面还原调用上下文，而无需在每个工具调用上重复写入。
- **ModelInvocationDescriptor** 记录 Provider-Specification-Model 三元组。`Specification` 字段用于区分同一 Provider 的不同协议规范（例如 OpenAI 的 "v1" vs "responses" 端点，Qwen 系列的 "json" vs "xml" 格式），这是快速演进的 AI 行业带来的必要复杂性。注意：`IProviderClient` 的一个实现对应 (Provider, Specification) 组合，如 `OpenAiV1Client` 和 `OpenAiResponsesClient` 是两个独立实现。
- **ModelInputEntry.ContentSections** 用有序键值对承载提示分段，可用于区分 Planner 指令、环境变量、任务描述等；列表默认为空，Provider 在最终拼装时再决定呈现方式。
- **ModelOutputEntry** 内部聚合同一轮推理生成的 `ToolCallRequest` 序列，并通过 `Invocation` 字段标记本次模型调用的供应商语义，同时以 `Contents` 保留分段文本，便于 Provider 自行组装提示或多模态片段。
- **ToolCallResult** 一一对应单个工具调用的执行结果，并记录耗时等诊断信息；`ToolResultsEntry.Results` 代表一次模型响应内的整体结果汇总，按顺序与 `ModelOutputEntry.ToolCalls` 配对，`OverallResult` 概述整轮调用状态，若整体失败可通过 `ExecuteError` 给出说明。
- **MemoryNotebookEntry** 负责记录全局状态类变更并通过 `AgentHistory` 领域方法写入；系统指令则以 `_systemInstruction` 字段维护，并在渲染阶段生成对应的 `SystemInstructionMessage` 注入上下文。
- **SystemInstructionMessage** 仅在上下文渲染阶段创建，用于向 Provider 暴露当前系统指令；后续若需要分版本或多段系统指令，可再引入稳定标识或扩展结构。
- **补充说明**：`ToolCallRequest` 仅作为 `ModelOutputEntry.ToolCalls` 的嵌套结构存在，不会追加独立的 `ToolCallRequest` 历史条目。
- **RenderLiveContext 副本**：如需注入 LiveScreen 内容，`ContextMessageLiveScreenHelper` 会返回实现 `ILiveScreenCarrier` 的包装对象，原始历史条目保持精简且不会被误追加回历史。
- `LiveScreenDecoratedMessage` 是一个仅实现 `IContextMessage` 与 `ILiveScreenCarrier` 的轻量包装，可携带 LiveScreen，并通过 `ILiveScreenCarrier.InnerMessage` 引用原始条目，从而既避免被再次追加到 `AgentHistory`，又允许 Provider 继续访问原始条目的角色化接口。

## ContextMessage 层接口深化

在三阶段流水线中，`IContextMessage` 承接了“长久历史”与“Provider 请求拼装”之间的缓冲层。我们希望它既能表达不同角色的语义，又能被 Provider 以最小假设消费，因此按“基础接口 + 角色化派生 + 可选能力”的方式拆解。

### 设计目标

- 保持 `IContextMessage` 的供应商无关性，禁止直接出现 `assistant`、`tool` 等 API 术语。
- 同一条上下文信息在 History 层与 Context 层之间尽可能复用数据，不引入额外复制成本。
- Provider 通过接口检测（`is IModelOutputMessage` 等）即可获知所需字段，无须了解历史实体类型。

基础接口仅保留角色、时间戳与元数据，具体内容字段完全交由派生接口定义，从而避免对“消息正文”施加过早约束。

### 基础接口 `IContextMessage`

```csharp
interface IContextMessage {
    ContextMessageRole Role { get; }
    DateTimeOffset Timestamp { get; }
    IReadOnlyDictionary<string, object?> Metadata { get; }
}

enum ContextMessageRole {
    System,
    ModelInput,
    ModelOutput,
    ToolResult
}
```

- `Role`：使用供应商无关的枚举值，作为渲染和 Provider 分支判断的第一关键字。
- （无统一正文字段）基础接口不再提供 `Content`，具体内容由派生接口自行定义，便于适配不同 Provider 的格式要求。
- `Timestamp`：沿用 History 层的时间戳，便于 Provider/调试器做顺序校验。
- `Metadata`：附带 token 统计、耗时、实验开关等辅助信息；键名以蛇形命名规范，保持向后兼容。

### 角色化派生接口

| 接口 | 额外成员 | 说明 |
| --- | --- | --- |
| `ISystemMessage` | `string Instruction` | 当前系统指令原文，按照各 Provider 的最小公约数设计。 |
| `IModelInputMessage` | `IReadOnlyList<KeyValuePair<string,string>> ContentSections`、`IReadOnlyList<IContextAttachment> Attachments`* | 表达输入通道与分段内容；附件接口暂待后续实现。 |
| `IModelOutputMessage` | `ModelInvocationDescriptor Invocation`、`IReadOnlyList<string> Contents`、`IReadOnlyList<ToolCallRequest> ToolCalls` | 记录本次响应使用的 Provider 语义、文本片段及潜在工具调用请求。 |
| `IToolResultsMessage` | `IReadOnlyList<ToolCallResult> Results`、`string? ExecuteError` | 汇总一次模型调用的工具返回；`ExecuteError` 为整体失败原因（若有）。 |

> ⚠️ 附件接口 `IContextAttachment` 暂不实现（见“结构化附属数据”小节）。为了避免本阶段的复杂度，我们在实现时可令 `Attachments` 返回空集合或通过辅助类型返回空枚举。

示例定义：

```csharp
interface ISystemMessage : IContextMessage {
    string Instruction { get; }
}

interface IModelInputMessage : IContextMessage {
    IReadOnlyList<KeyValuePair<string, string>> ContentSections { get; }
    IReadOnlyList<IContextAttachment> Attachments { get; }
}

interface IModelOutputMessage : IContextMessage {
    ModelInvocationDescriptor Invocation { get; }
    IReadOnlyList<string> Contents { get; }
    IReadOnlyList<ToolCallRequest> ToolCalls { get; }
}

interface IToolResultsMessage : IContextMessage {
    IReadOnlyList<ToolCallResult> Results { get; }
    string? ExecuteError { get; }
}

`Instruction` 保持为单字符串，满足当前主要 Provider 的共同约束；未来若需要多段或参数化系统提示，可在此接口基础上扩展。`ContentSections` 采用有序键值对列表，键名可视作分区标题，值目前为纯文本，占位未来的富内容对象。`Contents` 保留模型输出的自然顺序，为多模态输出预留通道。`ExecuteError` 与 `ToolCallResult.Status`/`Elapsed` 搭配使用，可区分局部失败与整体失败的情形。
```

### 可选能力接口（mix-in）

- `ILiveScreenCarrier`：`string? LiveScreen` 和 `IContextMessage InnerMessage`。LiveScreen 承载 Planner/工具的临时屏幕渲染信息；`InnerMessage` 在存在装饰器时指向被包装的实际消息（否则返回自身），便于 Provider 在需要时继续访问 `IModelInputMessage`、`IModelOutputMessage` 等角色化接口。**设计理由**：不同厂商对 LiveScreen 注入位置和格式要求不同（例如 Gemini 可用 Content/Parts 显式分隔并用不可见 Token 结构化；Anthropic 聚合多个 ToolResult 时 LiveScreen 应注入哪一条尚未确定），因此用独立字段呈现给 `IProviderClient`，由其决定最终拼装策略，而非在 History 层写死到单一文本字段中。不同模型的提示词工程可能也不同。
- `IToolCallCarrier`：`IReadOnlyList<ToolCallRequest> ToolCalls`，供 Provider 探测输出中是否包含工具调用；由 `IModelOutputMessage` 默认实现。
- `ITokenUsageCarrier`：`TokenUsage? Usage`，用于 Provider 在响应结束后补写 token 统计。

这些 mix-in 接口只在需要的条目上实现，保持基础接口的简洁度，也便于单元测试针对能力组合进行覆盖。

> **关于 `IRevisionableEntry`**：原设计包含 `long SequenceNumber` + `Guid StableId`，但 `StableId` 的使用场景（跨会话稳定引用、分布式追踪）目前不存在，且会增加内存开销。建议**暂不引入** `IRevisionableEntry`、`StableId` 或任何顺序号字段；等到持久化/快照功能明确后再评估是否需要。

示例定义：

```csharp
interface ILiveScreenCarrier {
    string? LiveScreen { get; }
    IContextMessage InnerMessage { get; }
}

interface IToolCallCarrier {
    IReadOnlyList<ToolCallRequest> ToolCalls { get; }
}

interface ITokenUsageCarrier {
    TokenUsage? Usage { get; }
}
```

### 结构化附属数据（暂缓实现）

- **`IContextAttachment`**：规划为描述富媒体、JSON 片段等结构化输入的接口，避免 Provider 用字符串猜测格式。为了减轻本轮重构范围，附件机制仅在接口层预留，文档中记为“暂不实现”。本文档底部保留一个空接口占位符，后续迭代（预计 Phase 4 之后）将补充具体的附件类型与序列化策略。

### Tool 描述动态注入（暂缓实现）

- 我们希望在长生命周期会话中，根据当前可用工具集动态生成提示词摘要，避免静态写死工具描述。该能力依赖 Planner/Executor 的实时注册信息以及 ContextMessage 中的工具声明。目前暂将该需求记录在文档中，计划在 Provider 路由器稳定后引入“Tool Manifest Message”或独立上下文条目来表达。

### 接口协同要点

- `RenderLiveContext()` 输出的条目应尽量沿用 History 层对象，必要时通过轻量包装类附加 `ILiveScreenCarrier` 等能力。
- `Metadata` 作为双方共享的轻量信息通道，禁止写入体量过大的结构（> 2 KB）；较大的数据应改用附件或单独条目。
- Provider 可通过接口检测来决定拼装逻辑：例如仅对实现了 `IToolCallCarrier` 的条目尝试解析 `ToolCalls`。
- 当消息实现了 `ILiveScreenCarrier` 时，Provider 应先读取 `InnerMessage` 再尝试角色化接口的强制转换，确保 LiveScreen 装饰不会影响后续逻辑。
- `ModelOutputEntry` 原样实现接口，用于传递模型响应及潜在的工具调用请求。
- `MemoryNotebookEntry` 等永远不应该出现在上下文中的历史条目不实现 `IContextMessage`，从编译期阻止它们进入 Provider 管线。

### 辅助类型

```csharp
enum HistoryEntryKind {
    MemoryNotebook,
    ModelInput,
    ModelOutput,
    ToolResult,
    // 后续可扩展：LiveContextSnapshot、PlannerDirective 等
}

enum ToolExecutionStatus { Success, Failed, Skipped }

record TokenUsage(
    int PromptTokens,
    int CompletionTokens, // 考虑进一步区分Thinking和正文各自的Token数。正文Token数有助于按Token数限制来从History尾部取Entry。总Token数有助于计费计算。单独的Thinking Token数暂时看不出用处。
    int? CachedPromptTokens
);

// 附件接口将于后续阶段收敛，目前仅作为占位符保留名称。
interface IContextAttachment { }
```

## History ↔ Context ↔ Provider 协作

- **History → Context**：`AgentHistory.RenderLiveContext()` 会把符合条件的 `HistoryEntry` 映射为对应的消息接口；当历史条目本身已实现目标接口时直接复用，无法复用时则通过只读包装补足字段，并在必要时附加 `ILiveScreenCarrier` 等能力。系统指令视为运行时投影，通过单独的 `SystemInstructionMessage` 注入。`Timestamp` 等核心字段直接传递，避免复制。
- **Context → Provider**：Provider 仅消费 `IContextMessage` 层接口，即可完成请求拼装与 delta 解析。它们通过接口检测判定角色（`IModelInputMessage`、`IModelOutputMessage` 等），按需解释 `ContentSections`、`Contents` 等字段，对实现 `IToolCallCarrier` 的条目解析 `ToolCalls`，并在推理结束后根据 `ITokenUsageCarrier` 或 `Metadata` 回写统计信息。
- **Provider → History**：模型响应结束后，由调用协调层基于 Provider 返回的 delta 聚合出新的 `ModelOutputEntry`、`ToolResultsEntry` 等，并追加到 History。这样可以保证 History 层仅包含最终定稿的条目，而流式阶段的装饰数据只存在于 Context 层。
- **附件/富内容**：附件体系说明见“结构化附属数据（暂缓实现）”，目前 `Attachments` 返回空集合，待后续统一扩展。

动态工具描述的暂缓策略统一记录在“Tool 描述动态注入（暂缓实现）”一节。

## Provider 客户端设计

接口示意：

```csharp
interface IProviderClient {
    IAsyncEnumerable<ModelOutputDelta> CallModelAsync(
        IReadOnlyList<IContextMessage> context,
        CancellationToken cancellationToken
    );
}
```

- 流式 delta 统一表示为 `ModelOutputDelta`，关于更名与字段约束详见下文“ModelOutputDelta 管线”。
- Provider 客户端封装底层 SDK 或 HTTP 细节，对外统一为流式 delta；测试场景可通过假实现返回预设 delta 序列。
- Provider 实现需把 `AgentHistory` 视为只读，并消费预先构建好的 `IContextMessage` 列表；同时负责将 `ToolCallRequest.RawArguments` 解析为 `Arguments` 字典（失败时留空并记录错误），补全 `ModelOutputEntry.Invocation`，并禁止直接修改或回写历史条目。
- 处理上下文时若遇到实现了 `ILiveScreenCarrier` 的条目，应先读取 `InnerMessage` 再做角色化接口判定，防止 LiveScreen 装饰影响原有逻辑。
- 解析失败时的错误处理约定：Provider 需要生成失败状态的 `ToolCallResult`，在结果文本中附上原始参数与错误原因；上层可以据此选择重试、提示 Planner 调整输出或请求人工介入。
- 为减少重复解析代码，计划提供共享的 `ToolArgumentParser` 辅助器（例如 `GetRequired(...)`、`ParseList(...)`、`ParseJsonSafely(...)` 等），供 Provider 和工具实现重复利用。
- Provider 层维护"规范 → 参数解析策略"的静态映射：`ModelOutputEntry.Invocation` 提供规范标识，具体的格式解析与工具调用出参推断交由各 Provider 内部实现，使 History 保持供应商无关的抽象。
- Provider 在汇总阶段负责写入 `ToolCallResult.Elapsed`、`ToolResultsEntry.ExecuteError` 等诊断信息，保证上层可以重建工具链路的耗时与失败原因。

### OpenAI 客户端行为

1. 消费传入的 `IReadOnlyList<IContextMessage>`，将其中的系统指令、输入、输出、工具结果等信息映射为 OpenAI 所需的消息数组；当前原型阶段，OpenAI v1 客户端可把 `ContentSections` 以“Heading + Content” 的 Markdown 结构拼接为单条文本供模型消费。
2. 解析流式返回，将底层 `ChatResponseDelta` 映射为 `ModelOutputDelta`（文本、工具调用等），并驱动上层追加带 `Invocation` 描述、`Contents` 片段聚合的 `ModelOutputEntry`；同时尝试把工具调用的原始参数解析成 `Arguments` 字典，失败时保留 `RawArguments` 并把字典留空、记录错误。
3. 多个工具调用按 OpenAI 风格作为独立 delta 产出，推理结束时合并为匹配的 `ToolResultsEntry`（其中的 `ToolCallResult` 与 `ModelOutputEntry.ToolCalls` 一一对应）。

### Anthropic 客户端行为

1. 同样消费 `IReadOnlyList<IContextMessage>`，在内部将多工具调用聚合成单条工具消息，保证 Claude 的奇偶交错约定。
2. 输出 `ModelOutputDelta` 序列，在流式阶段完成工具调用解析与信息注入，并使用与 OpenAI 相同的策略填充 `Arguments` 字典（失败时留空并汇报原因），同时写入 Claude 侧的 `Invocation` 描述并聚合文本片段到 `Contents`。
3. 推理结束后，把聚合后的结果汇总成一条 `ToolResultsEntry`，内部保留逐项 `ToolCallResult`，维持与 OpenAI 的配对精度。

### 其他供应商

- **Azure OpenAI**：共享 OpenAI 客户端实现或复用消息构造逻辑。
- **本地 vLLM/Transformers**：由客户端内部决定是否直接执行 Python/CUDA 组件，外部 API 不受影响。

### ModelOutputDelta 管线

- **最小化改动**：短期内仅将现有 `ChatResponseDelta` 重命名为 `ModelOutputDelta`，沿用既有字段（文本增量、工具调用片段等），将复杂的细粒度分类推迟到后续迭代。
- **历史落盘策略**：在模型流式完成后，再由上层把累计的 delta 汇总成单个 `ModelOutputEntry` 与一条 `ToolResultsEntry`（其中含多个 `ToolCallResult`），保持一次推理完整性。
- **信息注入**：如需在流式阶段插入额外调试或分析数据，先在 delta 管线完成后再统一写入 AgentHistory，避免 Provider 客户端与历史结构耦合。

## AgentHistory API 草案

```csharp
class AgentHistory {
    private readonly List<HistoryEntry> _eventLog = new();
    private string _systemInstruction = LlmAgent.DefaultSystemInstruction;
    private string _memoryNotebookContent = "（尚无内容）";

    public IReadOnlyList<HistoryEntry> EventLog => _eventLog;
    public string SystemInstruction => _systemInstruction;
    public string MemoryNotebookContent => _memoryNotebookContent;

    public void SetSystemInstruction(string instruction) {
        _systemInstruction = instruction;
        // TODO: 待引入 SystemInstruction 相关历史条目或快照后，再在此处追加持久化逻辑。
    }

    public void SetMemoryNotebookContent(string content) {
        var entry = new MemoryNotebookEntry(content) {
            Timestamp = DateTimeOffset.Now
        };
        _eventLog.Add(entry);
        _memoryNotebookContent = content;
    }

    public void AppendModelInput(ModelInputEntry entry) {
        _eventLog.Add(entry with {
            Timestamp = DateTimeOffset.Now
        });
    }

    public void AppendModelOutput(ModelOutputEntry entry) {
        _eventLog.Add(entry with {
            Timestamp = DateTimeOffset.Now
        });
    }

    public void AppendToolResults(ToolResultsEntry entry) {
        _eventLog.Add(entry with {
            Timestamp = DateTimeOffset.Now
        });
    }

    public IReadOnlyList<IContextMessage> RenderLiveContext() {
        // 当前阶段：单 pass 反向遍历，照搬 LlmAgent.BuildLiveContext() 实现
        // 暂不实现 Recent 截断功能，等需求分析完成后在后续阶段引入
        var context = new List<IContextMessage>(_eventLog.Count + 1);
        bool hasDecoratedLastSense = false;

        for (int i = _eventLog.Count; --i >= 0;) {
            var entry = _eventLog[i];
            if (entry is not ContextualHistoryEntry ctx) continue;
            if (ctx.Role == ContextMessageRole.System) continue;

            if (!hasDecoratedLastSense &&
                (ctx.Role == ContextMessageRole.ModelInput || ctx.Role == ContextMessageRole.ToolResult)) {
                var liveScreen = BuildVolatileSection();
                context.Add(ContextMessageLiveScreenHelper.AttachLiveScreen(ctx, liveScreen));
                hasDecoratedLastSense = true;
            } else {
                context.Add(ctx);
            }
        }

        // 注入 System Instruction
        var systemMessage = new SystemInstructionMessage(
            Instruction: _systemInstruction
        ) {
            Timestamp = DateTimeOffset.Now
        };
        context.Add(systemMessage);

        context.Reverse();
        return context;
    }

    private string BuildVolatileSection() {
        if (string.IsNullOrEmpty(_memoryNotebookContent)) return string.Empty;
        return $"# [Live Screen]:\n## [Memory Notebook]:\n\n{_memoryNotebookContent}";
    }
}
```

**设计要点**：
- **领域方法优于通用 Append**：`SetSystemInstruction`、`AppendModelInput` 等专用方法提供类型安全和清晰的调用语义，避免调用方直接构造 Entry 导致的错误。
- **顺序策略复用**：当前示例仅演示时间戳注入，关于顺序号/稳定标识的安排沿用“顺序与稳定标识策略”的统一约定。
- **EventLog 语义**：`_eventLog` / `EventLog` 明确了"不可变事件日志"的领域语义，与 Event Sourcing 术语一致，强调这是历史条目的原始存储而非展示视图。
- **状态同步封装**：`SetSystemInstruction` 等方法内部同步更新对应的运行时字段，保持数据一致性。
- **时间戳自动注入**：避免调用方手动传递 `Timestamp`，减少出错机会。
- **暂不提供 Rollback**：历史回滚的语义（如何恢复 `_systemInstruction` 等状态？）尚未明确，推迟到历史快照/回放功能设计完成后，在独立重构中引入。

当前阶段将潜在的 `AgentHistorySnapshot` 设计推迟，以缩小本轮重构的落地范围；待历史回放或跨线程读取需求明确后，再集中梳理持久化格式与快照语义，从而一次性引入更内聚的方案。

## 迁移计划（分阶段）

### Phase 0：准备
- 梳理现有 `_conversationHistory` 使用点，标记读取/写入的位置。
- 编写临时 Adapter，便于在迁移期间同时维护旧结构和新结构。
- **注意**：当前项目没有存储会话功能，因此无需担心旧数据迁移问题。

### Phase 1：引入 HistoryEntry 类型与核心接口
- 实现新的 `HistoryEntry` 层次和 `AgentHistory` 类。
- **核心接口落地**：在 Phase 1 内实现 `IContextMessage`、`ISystemMessage`、`IModelInputMessage`、`IModelOutputMessage`、`IToolResultsMessage`、`IToolCallCarrier`、`ILiveScreenCarrier` 等接口，确保示例代码可编译并支撑新的类型体系。
- **轻量实现约束**：此阶段的接口实现保持最小化——例如 `IContextAttachment` 返回空集合、`IToolResultsMessage.ExecuteError` 允许为 null、`ILiveScreenCarrier` 仅作为装饰存在；后续迭代再按需扩展 `ITokenUsageCarrier` 等附加能力，避免接口爆炸。
- **顺序与标识策略**：依照“顺序与稳定标识策略”的约定，Phase 1 不引入 `IRevisionableEntry`、`Guid StableId` 或顺序号字段，后续随持久化能力一并评估。
- **命名约定**：继续沿用 EventLog 术语（`_eventLog` / `EventLog`），保持与 Event Sourcing 最佳实践的一致性；顺序号将由未来的存储层提供。
- 在 `LlmAgent` 中新增 `_history` 字段，写入与 `_conversationHistory` 同步的条目（双写期）。
- 编写单元测试覆盖基本条目追加行为，验证时间戳与状态同步正确性；顺序相关断言留待存储层接入后补充。

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
- **后续独立评估**：历史回滚（`Rollback`）、快照/回放能力、`StableId` 引入等，需等明确使用场景后再设计。

## 回归测试要点

- OpenAI 流程：确认多工具调用仍然按多条 `tool` 消息输出，`finish_reason=tool_calls` 工作正常。
- Anthropic 流程：单条 Tool 消息包含所有工具结果，保证奇偶交错。
- 混合会话：先运行 Claude 规划，再调用 OpenAI 工具执行；历史应按时间顺序正确记录。
- 状态同步：`SetSystemInstruction` / `SetMemoryNotebookContent` 后，`RenderLiveContext()` 应反映最新值。

## 风险与缓解

| 风险 | 说明 | 缓解策略 |
| --- | --- | --- |
| 类型膨胀导致维护负担 | `HistoryEntry` 细分过多 | 以最小足够集合起步，并通过 Metadata 兼容扩展 |
| Provider 实现差异 | 多供应商接口各有约束 | 编写测试用例逐条验证协议约定，并集中维护文档 |
| 双写期间状态同步 | 迁移阶段 `_conversationHistory` 与 `_history` 需同步 | 通过装饰器或中间层统一写入，确保二者一致 |
| 性能开销 | 流式 delta 解析与构造增加遍历次数 | 历史条目数量有限（本地代理），影响可忽略；如需优化可缓存最近输出 |

## 未来扩展

在正式实现过程中，还需遵循以下通用约定，以保持各层职责清晰：

- 历史层始终只落原始文本（`RawArguments` 等），不承担结构化解析。
- Provider 扮演"解释器"，负责把原始文本解析成统一的 `IReadOnlyDictionary<string, string>`，解析失败时透明地返回错误信息。
- 工具接口仅依赖解析后的键值对输入，避免直接接触原始文本；工具输出同样使用字符串或键值对，保持输入输出的一致性（关于动态工具描述的规划请参见“Tool 描述动态注入（暂缓实现）”一节）。
- TODO：待消息存储层落地后，按“顺序与稳定标识策略”统一恢复顺序号生成与持久化逻辑，并补回相关测试。
- **Live Context + History 协调**：HistoryEntry 可以新增 `LiveContextInjectedEntry`，记录每次调用附带的 Live Context，方便调试。
- **Memory Notebook**：将 Memory 操作也抽象为 `HistoryEntry`，便于回溯记忆编辑过程。
- **多模型策略**：在 ProviderRouter 上层增加 Strategy 层，例如"规划模型""执行模型""评审模型"各自使用不同 Provider 客户端。
- **与 Semantic Kernel 集成评估**：若未来需要 Planner/Skill 生态，可将 SK 作为 orchestrator，内部依旧透过 Provider 客户端调用底层模型。

## 下一步建议

1. 完成 Phase 0~1 的代码草稿，把 HistoryEntry 与 AgentHistory 骨架跑通。
2. 对已有会话数据做一次"格式转换"演练，验证类型设计能覆盖现状。
3. 改造 OpenAI/Anthropic Provider 客户端，确认 `ModelOutputDelta` 流程跑通并通过回归测试。
4. 更新 CLI `/history` 输出，让开发者在调试时直观看出新的术语与 HistoryEntry 类型。
5. 在重构过程中配合 DebugUtil 记录新的历史条目，方便检查。

---

*附注：本文档定位为纲领性方案，后续每个 Phase 应补充更细的设计与测试用例说明。*
