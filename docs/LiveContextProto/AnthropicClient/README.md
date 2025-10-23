# Anthropic Provider Client

这是一个符合 Atelia LiveContextProto 架构的 Anthropic Messages API 客户端实现。

## 架构概览

实现包含以下核心组件：

### 1. **AnthropicProviderClient**
主要客户端类，实现 `IProviderClient` 接口。

**职责：**
- 发起 HTTP 请求到 Anthropic API
- 处理流式 SSE 响应
- 将事件流转换为 `ModelOutputDelta` 序列

**配置：**
```csharp
var client = new AnthropicProviderClient(
    apiKey: "sk-ant-...",
    httpClient: customHttpClient,  // 可选
    apiVersion: "2023-06-01"       // 可选，默认 2023-06-01
);
```

### 2. **AnthropicMessageConverter**
上下文转换器，负责将通用的 `IContextMessage` 转换为 Anthropic 协议格式。

**关键转换规则：**

| IContextMessage 类型 | Anthropic 格式 | 说明 |
|---------------------|---------------|------|
| `ISystemMessage` | `system` 参数 | 系统指令单独传递，不在消息数组中 |
| `IModelInputMessage` | `user` 消息 + text blocks | ContentSections 转为 Markdown 格式 |
| `IModelOutputMessage` | `assistant` 消息 + text/tool_use blocks | Contents + ToolCalls |
| `IToolResultsMessage` | `user` 消息 + tool_result blocks | 多个结果聚合到一条消息中 |

**Window 处理：**
- 检测 `IWindowCarrier` 接口
- 将 Window 内容作为额外的 text block 追加到对应消息末尾

**消息序列规范化：**
- 连续相同角色的消息会自动合并
- 确保首条消息为 `user` 角色（Anthropic 要求）

### 3. **AnthropicStreamParser**
流式事件解析器，处理 SSE 格式的响应。

**支持的事件类型：**
- `message_start` - 初始化并提取首次 usage
- `content_block_start` - 开始新的内容块（text/tool_use）
- `content_block_delta` - 增量内容（text_delta / input_json_delta）
- `content_block_stop` - 内容块结束，生成 `ToolCallRequest`
- `message_delta` - 更新 usage 统计
- `message_stop` - 消息结束
- `error` - 错误处理

**工具调用解析：**
- 累积 `input_json_delta` 片段
- 在 `content_block_stop` 时解析完整 JSON
- 生成 `ToolCallRequest` 并填充 `Arguments` 字典
- 解析失败时保留 `RawArguments` 并记录 `ParseError`

### 4. **AnthropicApiModels**
API DTO 定义，使用 `System.Text.Json` 的多态序列化。

**核心类型：**
- `AnthropicApiRequest` - 请求体
- `AnthropicMessage` - 消息对象
- `AnthropicContentBlock` (抽象) - 内容块基类
  - `AnthropicTextBlock` - 文本块
  - `AnthropicToolUseBlock` - 工具调用块（assistant）
  - `AnthropicToolResultBlock` - 工具结果块（user）

## 使用示例

### 基础调用

```csharp
var client = new AnthropicProviderClient(apiKey: Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!);

var request = new ProviderRequest(
    StrategyId: "anthropic-v1",
    Invocation: new ModelInvocationDescriptor(
        ProviderId: "anthropic",
        Specification: "messages-v1",
        Model: "claude-3-5-sonnet-20241022"
    ),
    Context: contextMessages,
    StubScriptName: null
);

await foreach (var delta in client.CallModelAsync(request, cancellationToken)) {
    switch (delta.Kind) {
        case ModelOutputDeltaKind.Content:
            Console.Write(delta.ContentFragment);
            break;
        case ModelOutputDeltaKind.ToolCallDeclared:
            Console.WriteLine($"\n[Tool Call] {delta.ToolCallRequest?.ToolName}");
            break;
        case ModelOutputDeltaKind.TokenUsage:
            Console.WriteLine($"\n[Usage] {delta.TokenUsage}");
            break;
    }
}
```

### 与 ProviderRouter 集成

在 `ProviderRouter` 中注册客户端：

```csharp
var anthropicClient = new AnthropicProviderClient(apiKey);

router.RegisterStrategy("anthropic-v1", (invocation) =>
    new ProviderInvocationPlan(
        StrategyId: "anthropic-v1",
        Client: anthropicClient,
        Invocation: invocation,
        StubScriptName: null
    )
);
```

## 协议特性

### 1. 消息交错约束
Anthropic 要求 `user` ↔ `assistant` 严格交错。转换器会自动：
- 合并连续的相同角色消息
- 在序列开头补充 `user` 消息（如需要）

### 2. 工具调用聚合
- **OpenAI 风格**：一次响应包含多个工具调用 → 每个工具结果独立返回
- **Anthropic 风格**：一次响应包含多个 `tool_use` blocks → 所有结果聚合到一条 `user` 消息

转换器通过 `BuildToolResultContent()` 实现这一聚合。

### 3. Token 计费
Anthropic 提供详细的缓存统计：
- `input_tokens` - 提示词 token 数
- `output_tokens` - 生成的 token 数
- `cache_read_input_tokens` - 从缓存读取的 token（提示缓存）
- `cache_creation_input_tokens` - 写入缓存的 token

这些统计会合并到 `TokenUsage.CachedPromptTokens` 字段。

## 调试支持

所有组件使用 `DebugUtil.Print()` 输出调试信息，类别为 `"Provider"`。

**启用调试输出：**
```powershell
$env:ATELIA_DEBUG_CATEGORIES = "Provider"
# 或启用所有类别
$env:ATELIA_DEBUG_CATEGORIES = "ALL"
```

**日志内容示例：**
```
[Provider] [Anthropic] Client initialized version=2023-06-01
[Provider] [Anthropic] Starting call model=claude-3-5-sonnet-20241022
[Provider] [Anthropic] Converted 5 context messages to 3 API messages
[Provider] [Anthropic] Request payload length=1234
[Provider] [Anthropic] Normalized to 3 messages
[Provider] [Anthropic] Stream completed
```

## 限制与未来工作

### 当前限制
1. **工具定义未注入** - `tools` 字段预留但未实现（等待 Phase 5）
2. **固定 max_tokens** - 当前硬编码为 4096
3. **无重试机制** - 网络错误会直接抛出异常
4. **无速率限制处理** - 429 响应未特殊处理

### 计划改进
- [ ] 从 ProviderRequest 读取 `max_tokens` 配置
- [ ] 支持 `temperature`、`top_p` 等采样参数
- [ ] 实现工具定义的动态注入（参考重构蓝图 Phase 5）
- [ ] 添加重试策略与指数退避
- [ ] 支持 Anthropic 的 Thinking 模式（如 `claude-3-7-sonnet` 系列）

## 参考资料

- [Anthropic Messages API 文档](https://docs.anthropic.com/claude/reference/messages_post)
- [Anthropic 流式响应规范](https://docs.anthropic.com/claude/reference/messages-streaming)
- [Atelia Conversation History 重构蓝图](../../docs/MemoFileProto/ConversationHistory_Refactor_Blueprint.md)
