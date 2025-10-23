# Anthropic Provider 快速参考

## 文件清单

```
Provider/Anthropic/
├── AnthropicProviderClient.cs           # 主客户端实现
├── AnthropicMessageConverter.cs         # 上下文转换器
├── AnthropicStreamParser.cs             # SSE 流式解析器
├── AnthropicApiModels.cs                # API DTO 定义
├── AnthropicIntegrationExample.cs       # 集成示例代码
└── README.md                            # 详细文档
```

## 核心类型速查

### AnthropicProviderClient

```csharp
// 构造函数
new AnthropicProviderClient(
    apiKey: string,
    httpClient: HttpClient? = null,
    apiVersion: string? = "2023-06-01"
)

// IProviderClient 接口
IAsyncEnumerable<ModelOutputDelta> CallModelAsync(
    ProviderRequest request,
    CancellationToken cancellationToken
)
```

### ModelInvocationDescriptor (for Anthropic)

```csharp
new ModelInvocationDescriptor(
    ProviderId: "anthropic",
    Specification: "messages-v1",
    Model: "claude-3-5-sonnet-20241022" // 或其他模型
)
```

### 支持的模型（截至 2024-10）

| 模型名称 | 上下文窗口 | 特性 |
|---------|-----------|------|
| `claude-3-5-sonnet-20241022` | 200K | 最新旗舰，支持提示缓存 |
| `claude-3-5-haiku-20241022` | 200K | 快速轻量 |
| `claude-3-opus-20240229` | 200K | 最强推理（旧版） |

## 转换规则速查表

| 输入类型 | Anthropic 消息角色 | Content Blocks |
|---------|------------------|----------------|
| `ISystemMessage` | (独立 `system` 参数) | - |
| `IModelInputMessage` | `user` | `[text]` |
| `IModelOutputMessage` | `assistant` | `[text, tool_use]` |
| `IToolResultsMessage` | `user` | `[tool_result, ...]` |

## 常见场景代码片段

### 1. 最简对话

```csharp
var client = new AnthropicProviderClient(apiKey);

var context = new List<IContextMessage> {
    new SystemInstructionMessage("You are helpful.") { Timestamp = DateTimeOffset.UtcNow },
    new ModelInputEntry(new() { new("", "Hello!") }) { Timestamp = DateTimeOffset.UtcNow }
};

var request = new ProviderRequest(
    "anthropic-v1",
    new("anthropic", "messages-v1", "claude-3-5-sonnet-20241022"),
    context,
    null
);

await foreach (var delta in client.CallModelAsync(request, ct)) {
    if (delta.Kind == ModelOutputDeltaKind.Content) {
        Console.Write(delta.ContentFragment);
    }
}
```

### 2. 捕获工具调用

```csharp
var toolCalls = new List<ToolCallRequest>();

await foreach (var delta in client.CallModelAsync(request, ct)) {
    switch (delta.Kind) {
        case ModelOutputDeltaKind.ToolCallDeclared:
            toolCalls.Add(delta.ToolCallRequest!);
            break;
    }
}
```

### 3. 返回工具结果

```csharp
context.Add(new ToolResultsEntry(
    Results: new[] {
        new ToolCallResult("tool_name", "call_id", ToolExecutionStatus.Success, "result", null)
    },
    ExecuteError: null
) { Timestamp = DateTimeOffset.UtcNow });

// 继续对话...
```

### 4. 注入 Window

```csharp
var input = new ModelInputEntry(...);
var decorated = ContextMessageWindowHelper.AttachWindow(input, WindowText);
context.Add(decorated);
```

## 错误处理

### API 错误

```csharp
try {
    await foreach (var delta in client.CallModelAsync(request, ct)) {
        if (delta.Kind == ModelOutputDeltaKind.ExecuteError) {
            Console.Error.WriteLine($"API Error: {delta.ExecuteError}");
        }
    }
} catch (HttpRequestException ex) {
    // 网络错误或 HTTP 状态码错误
}
```

### 常见 HTTP 状态码

| 状态码 | 含义 | 处理建议 |
|-------|------|---------|
| 400 | 请求格式错误 | 检查消息格式和参数 |
| 401 | API Key 无效 | 验证 ANTHROPIC_API_KEY |
| 429 | 速率限制 | 实现重试与退避 |
| 500 | 服务器错误 | 重试或联系支持 |

## 调试技巧

### 启用调试日志

```powershell
# Windows PowerShell
$env:ATELIA_DEBUG_CATEGORIES = "Provider"

# Bash
export ATELIA_DEBUG_CATEGORIES="Provider"
```

### 日志位置

```
.codecortex/ldebug-logs/Provider.log
```

### 关键日志示例

```
[Provider] [Anthropic] Client initialized version=2023-06-01
[Provider] [Anthropic] Starting call model=claude-3-5-sonnet-20241022
[Provider] [Anthropic] Converted 5 context messages to 3 API messages
[Provider] [Anthropic] Normalized to 3 messages
[Provider] [Anthropic] Request payload length=1234
[Provider] [Anthropic] Stream completed
```

## 性能考虑

### 提示缓存（Prompt Caching）

Anthropic 的提示缓存会自动启用，适用于：
- 长系统指令
- 大量上下文（> 1024 tokens）

查看缓存统计：

```csharp
if (delta.Kind == ModelOutputDeltaKind.TokenUsage && delta.TokenUsage?.CachedPromptTokens > 0) {
    Console.WriteLine($"Cache hit: {delta.TokenUsage.CachedPromptTokens} tokens");
}
```

### 流式响应优化

- 默认启用流式（`Stream = true`）
- 使用 `IAsyncEnumerable` 避免缓冲整个响应
- 及时处理 delta，避免内存积压

## 与其他 Provider 的差异

| 特性 | Anthropic | OpenAI |
|-----|-----------|--------|
| 系统指令 | 独立 `system` 参数 | 首条 `system` 消息 |
| 工具结果 | 聚合到一条 `user` 消息 | 每个结果独立 `tool` 消息 |
| 消息交错 | 严格 user ↔ assistant | 较宽松 |
| 工具调用格式 | `tool_use` content block | `tool_calls` 数组 |
| Token 统计 | 详细缓存统计 | 简化统计 |

## 下一步

- 查看 `AnthropicIntegrationExample.cs` 获取完整示例
- 阅读 `README.md` 了解架构细节
- 参考 [Anthropic 官方文档](https://docs.anthropic.com/claude/reference/messages_post)
