# LiveContextProto 工具框架设计提案

*版本：0.1-draft · 更新日期：2025-10-13*

> 关联文档：
> - 《Conversation History 抽象重构蓝图 · V2》：AgentState ↔ Provider ↔ Tool 协作的总体设计真相源。
> - 《ToolCallArgumentHandling.md》：当前的宽进严出参数解析策略，本文将扩展其描述。
> - `prototypes/LiveContextProto/Tools/ToolExecutor.cs`：现有工具执行框架实现。

---

## 文档目的

- 给出统一的工具抽象 (`ITool`) 与参数描述契约，为 LiveContextProto 及后续实现提供清晰的扩展点。
- 明确工具声明与 Provider 之间的数据契约，确保模型输出被解析为期望类型的同时保留“宽进严出”策略。
- 指导将现有 `IToolHandler` 生态迁移至新的 `ITool` 接口，并规划与 `ToolExecutor`、`ToolResultMetadataHelper` 等现有组件的协作方式。

## 背景现状

| 现象 | 影响 |
| --- | --- |
| `ToolExecutor` 仅依赖 `IToolHandler`（`ToolName + ExecuteAsync`），缺乏统一的工具元数据声明 | Provider 无法在调用前获知参数类型，导致解析策略只能基于 JSON 结构推断，出现布尔字符串被错判等问题。 |
| 工具参数解析策略已在《ToolCallArgumentHandling.md》中定义为“宽进严出”，但缺少与工具声明的联动 | Provider 无法根据各工具需求动态调节解析容错度，扩展成本高。 |
| 新增工具需要在多个文件中重复编写描述信息（README、脚本、手册） | 易于产生陈旧信息，调试阶段难以诊断参数错配。
| 未来计划引入动态工具清单与 Planner 协调 | 缺少统一接口将工具清单暴露给 Planner / Provider。

综上，需要一个带参数元数据的 `ITool` 接口来承载“工具定义 + 执行”两种职责，并为 Provider 层提供精确的解析提示。

## 设计目标

1. **统一声明**：每个工具以 `ITool` 定义自身元数据（名称、描述、参数）。
2. **类型指引**：参数声明包含强类型枚举指示，Provider 可据此进行容错解析（例如将 `"true"` 转换成布尔 `true`，或在期望字符串时保持原样）。
3. **兼容现有执行栈**：`ToolExecutor`、`ToolHandlerResult` 等现有类型无需大幅重写，只需接受 `ITool` 适配。
4. **供应商无关**：参数声明脱离具体 Provider，实现统一解析逻辑（Stub/OpenAI/Anthropic 等）。
5. **渐进迁移**：允许现有 `IToolHandler` 与新接口短期共存，通过适配器保障无缝过渡。

## 设计原则

- **契约先行**：工具声明必须可静态读取，避免仅在运行时动态生成，便于 Provider 在构造请求前进行验证。
- **元数据自描述**：命名、描述、参数等信息需要覆盖 Planner、UI 以及调试输出需求。
- **宽进严出**：保持现有解析策略，新增的类型枚举仅提供指引，不强制拒绝非标准输入，但要记录 `ParseWarning`/`ParseError`。
- **最小依赖**：避免引入外部 schema 引擎，保持 `.NET` 内部枚举和 record 足以表达常见类型。
- **向后兼容**：保留 `ToolExecutor` 输出的 `ToolCallResult`、`ToolExecutionRecord` 格式，减少历史记录和测试的改动量。

## 核心抽象设计

### `ITool` 接口

```csharp
internal interface ITool {
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ToolParameter> Parameters { get; }
    ValueTask<ToolHandlerResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}
```

- **Name**：唯一标识，用于匹配 `ToolCallRequest.ToolName`。
- **Description**：面向模型与 Planner 的自然语言说明。
- **Parameters**：参数声明集合（见下节）。
- **ExecuteAsync**：执行逻辑，返回 `ToolHandlerResult`（复用现有 record），输入改为 `ToolExecutionContext`，提供更丰富的上下文。

#### `ToolExecutionContext`

为避免接口与底层 `ToolCallRequest` 强耦合，我们提供轻量包装：

```csharp
internal sealed record ToolExecutionContext(
    ToolCallRequest Request,
    ImmutableDictionary<string, object?> Environment
);
```

- `Request`：原始模型调用信息，保留 `Arguments`、`RawArguments`、`ParseWarning` 等字段。
- `Environment`：执行环境上下文（如 AgentState、当前会话 ID、调试标记），默认可传空字典，为后续扩展留钩子。

### 参数声明 `ToolParameter`

```csharp
internal sealed record ToolParameter(
    string Name,
    ToolParameterValueKind ValueKind,
    ToolParameterCardinality Cardinality,
    bool IsRequired,
    string Description,
    ToolParameterEnumConstraint? EnumConstraint = null,
    string? Example = null
);
```

- **Name**：参数名，对应 `Arguments` 字典键。
- **ValueKind**：由枚举 `ToolParameterValueKind` 指示预期类型。
- **Cardinality**：单值 / 列表 / 对象等基数信息。
- **IsRequired**：是否为必填字段。
- **Description**：Natural language 描述，供 Planner 与模型提示使用。
- **EnumConstraint**：可选枚举约束，当值类型为字符串或整数枚举时提供合法选项。
- **Example**：示例值，用于提示生成与文档。

#### 类型枚举 `ToolParameterValueKind`

```csharp
internal enum ToolParameterValueKind {
    String,
    Boolean,
    Integer,
    Number,
    JsonObject,
    JsonArray,
    Timestamp,
    Uri,
    EnumToken,
    AttachmentReference
}
```

- `String`：要求 Provider 保留原始文本，不进行布尔/数值提升。
- `Boolean`：允许 Provider 将 `"true"`、`"false"` 等字符串提升为布尔值，同时保留 `ParseWarning`。
- `Integer` / `Number`：指示优先解析为 `long` / `double`，字符串数值需提升并记录警告。
- `Timestamp`：推荐 ISO-8601 字符串，如无法解析则回写 `ParseError`。
- `Uri`：解析为绝对 URI，失败则保留原文本并警告。
- `EnumToken`：与 `EnumConstraint` 配合，限制值集。
- `AttachmentReference`：预留给附件体系（Phase 4），当前 Provider 可视为字符串。

#### 基数枚举 `ToolParameterCardinality`

```csharp
internal enum ToolParameterCardinality {
    Single,
    Optional,
    List,
    Map
}
```

- `Single`：必填单值；
- `Optional`：可空，但若提供需满足类型；
- `List`：数组结构，Provider 在容错时可将标量提升为单元素列表；
- `Map`：键值对象，要求 JSON Object。

#### 枚举约束 `ToolParameterEnumConstraint`

```csharp
internal sealed record ToolParameterEnumConstraint(
    IReadOnlyList<string> AllowedValues,
    bool CaseSensitive = false
);
```

- 对 `String`/`EnumToken` 值进行白名单限制；
- Provider 在解析时可进行大小写规范化，并在违规时写入 `ParseError`。

### Provider 协作契约

新的参数声明需与《ToolCallArgumentHandling.md》的宽进严出策略配合：

| ValueKind | Provider 容错策略 | 示例 |
| --- | --- | --- |
| `Boolean` | 接受布尔、布尔字符串（记录警告）、数值 `0/1`（记录警告） | `"true"` → `true`；`1` → `true` 并写入 `ParseWarning="number coerced to boolean"` |
| `String` | 禁止自动提升；若模型输出 JSON 布尔/数值，保持原文本并写入 `ParseWarning="non-string literal retained"` | `true` → 保存为字符串 `"true"` |
| `Integer` | 优先 `long`，遇到小数截断须写入警告；无法转换时写入 `ParseError` | `"42"` → `42` + `ParseWarning` |
| `JsonObject` | 要求对象结构；若模型返回字符串，则尝试二次解析（失败则 `ParseError`） | `"{\"a\":1}"` → 解析为字典 |
| `List` 基数 | 标量自动包裹成单元素列表并写警告 | `"foo"` → `new[] { "foo" }` |

Provider 需要在构造 `ToolCallRequest` 时查找工具声明：

1. 根据 `ToolName` 获取 `ITool`；
2. 根据参数声明生成解析上下文；
3. 遍历 JSON 参数，将值填入 `Arguments`
   - 若解析成功 → 写入目标类型；
   - 轻微偏差 → 写入修正值并附 `ParseWarning`；
   - 无法解析 → 保留原文本并写 `ParseError`。
4. `ToolCallRequest` 保持 `RawArguments` 原样。

若 Provider 无法找到工具声明，则回退到旧逻辑（宽松解析 + `ParseWarning="tool_definition_missing"`），并在日志中提示。

### 执行框架整合

为兼容现有 `ToolExecutor`，引入以下组件：

1. **`ToolCatalog`**（新）：维护 `ITool` 列表，为 Provider、ToolExecutor 提供查询。
2. **`ToolHandlerAdapter`**（过渡）：将 `ITool` 转换为旧接口 `IToolHandler`，内部调用 `tool.ExecuteAsync(new ToolExecutionContext(...))`。
3. **`ToolRegistryBuilder`**：在启动时注册工具，生成 `ToolCatalog` 与适配器集合：

```csharp
IEnumerable<ITool> tools = new ITool[] { new MemorySearchTool(), new DiagnosticsTool() };
var catalog = ToolCatalog.Create(tools);
var executor = new ToolExecutor(catalog.CreateHandlers());
```

- `ToolExecutor` 的构造函数保持不变（接收 `IEnumerable<IToolHandler>`）。
- Provider 通过 `catalog.Get(name)` 读取声明，用于参数解析。
- 将来若完全迁移，可让 `ToolExecutor` 直接接受 `ToolCatalog`。

### 生命周期与数据流

```
LLM 输出 tool_call → Provider 查 ToolCatalog → 解析参数（参考 ValueKind）
    → 构造 ToolCallRequest → ToolExecutor.ExecuteAsync → ITool.ExecuteAsync
    → ToolHandlerResult → ToolResultsEntry 写入历史 → 调试/遥测
```

- `ToolResultMetadataHelper` 可保持不变，继续消费 `ToolExecutionRecord`。
- Debug 日志类别仍使用 `Tools`，并在参数解析阶段追加分类 `ToolParse`（可选）。

## 实施计划

| 阶段 | 工作项 | 备注 |
| --- | --- | --- |
| Phase A（本迭代） | 定义 `ITool`、`ToolParameter` 等类型，编写 `ToolFrameworkDesign`（本文），实现 `ToolCatalog` 与适配器 | 属于设计 + 基础设施建设 |
| Phase B | 更新 Stub Provider，使其读取 `ToolCatalog` 并按 `ValueKind` 解析参数；补充单元测试覆盖布尔/字符串混淆场景 | 需同步更新《ToolCallArgumentHandling.md》中的流程图 |
| Phase C | 替换示例工具（`SampleMemorySearchToolHandler` 等）为新 `ITool` 实现，观察回归 | `ToolExecutor` 保持兼容 |
| Phase D | 在 Orchestrator 中暴露工具清单，为 Planner / UI 提供列表（待 LiveInfo 扩展） | 与 Conversation History 蓝图 Phase 4 对齐 |

## 边界与注意事项

- **默认系统指令**：工具描述将纳入上下文提示，需关注 Token 占用；建议在 Provider 端做裁剪。
- **多 Provider 支持**：同一工具声明应适用于 OpenAI/Anthropic，本方案将解析策略集中在公共层。
- **错误处理**：当 `ParseError` 存在时，工具可根据 `ToolParameter` 的 `IsRequired` 判定是否拒绝执行；必要时由 `ToolExecutor` 在调用前拦截。
- **可选参数**：`Cardinality = Optional` 且缺失时，工具无需访问 `Arguments`；若模型填入空字符串，应视为提供了值，需要工具自行判断。
- **性能影响**：工具枚举数量有限，`ToolCatalog` 使用不可变字典缓存查找结果，避免在 Provider 热路径中重复构造。

## 后续扩展

- **Schema 导出**：可根据 `ToolParameter` 自动生成 JSON Schema 或 Markdown 文档，减少重复维护。
- **动态注册**：未来 LiveContextProto 支持运行时加载工具时，可扩展 `ToolCatalog.Builder` 动态增删。
- **遥测对齐**：在工具执行结果中附加 `ParseWarning` 摘要，便于调试模型输出质量。
- **UI 集成**：Planner 或 UI 可读取参数枚举，生成表单式工具调用界面。

## 未决问题

- `ToolExecutionContext.Environment` 的内容范围仍待确定（是否包含 AgentState 引用）。
- `ToolParameter` 是否需要 `DefaultValue` 字段？当前建议在工具内部处理。
- 当 `Cardinality = Map` 且内部需要类型提示时，是否需要嵌套 `ToolParameter` 描述？可能在 Phase 4 引入。
- 附件体系 (`AttachmentReference`) 的具体结构需等待 Conversation History 蓝图的 LiveInfo/附件设计落地。

## 附录：示例工具迁移草图

```csharp
internal sealed class MemorySearchTool : ITool {
    public string Name => "memory.search";
    public string Description => "根据关键词在记忆库中查找相关片段，并返回摘要";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new[] {
        new ToolParameter(
            Name: "query",
            ValueKind: ToolParameterValueKind.String,
            Cardinality: ToolParameterCardinality.Single,
            IsRequired: true,
            Description: "用户输入的检索关键词",
            Example: "agent state consistency"
        ),
        new ToolParameter(
            Name: "limit",
            ValueKind: ToolParameterValueKind.Integer,
            Cardinality: ToolParameterCardinality.Optional,
            IsRequired: false,
            Description: "需要返回的最大片段数",
            Example: "3"
        )
    };

    public async ValueTask<ToolHandlerResult> ExecuteAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken
    ) {
        var args = context.Request.Arguments;
        var query = args?["query"]?.ToString() ?? "(empty)";
        var limit = args is not null && args.TryGetValue("limit", out var rawLimit) && rawLimit is long l
            ? (int)l
            : 3;

        // 业务逻辑略
        await Task.Yield();
        return new ToolHandlerResult(ToolExecutionStatus.Success, $"命中 {limit} 条记录", ImmutableDictionary<string, object?>.Empty);
    }
}
```

> 提示：`ToolExecutor` 在 Phase C 迁移时将通过 `ToolHandlerAdapter` 调用该实现，现有单元测试可直接复用。
