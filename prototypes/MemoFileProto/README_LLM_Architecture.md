# MemoFileProto LLM Client Architecture

## 架构概述

本项目实现了一个 Provider 无关的 LLM 客户端架构，支持 OpenAI 和 Anthropic 两种协议。

```
┌─────────────────────────────────────────┐
│          LlmAgent (高层应用)             │
│   使用 Universal* 类型进行对话管理       │
└──────────────┬──────────────────────────┘
               │
               │ ILLMClient 接口
               │
       ┌───────┴────────┐
       │                │
┌──────▼───────┐  ┌────▼──────────┐
│ OpenAIClient │  │AnthropicClient│
│              │  │               │
│  协议转换    │  │  协议转换+    │
│  Universal→  │  │  工具聚合     │
│  OpenAI      │  │  Universal→   │
│              │  │  Anthropic    │
└──────────────┘  └───────────────┘
```

## 核心类型

### Provider 无关的核心模型 (Models/)

- `UniversalMessage` - 通用消息格式
  - 支持文本内容
  - 支持工具调用（ToolCalls）
  - 支持工具结果（ToolResults），可聚合多个结果

- `UniversalRequest` - 通用请求格式
- `UniversalResponseDelta` - 通用流式响应增量
- `UniversalTool` - 通用工具定义

### Provider 特定模型

#### OpenAI (Models/OpenAI/)
- `OpenAIMessage` - OpenAI 消息格式
- `OpenAIRequest` - OpenAI 请求格式
- `OpenAIResponse` - OpenAI 响应格式

#### Anthropic (Models/Anthropic/)
- `AnthropicMessage` - Anthropic 消息格式（content 数组）
- `AnthropicRequest` - Anthropic 请求格式
- `AnthropicResponse` - Anthropic 流式事件格式

## 关键设计点

### 1. 工具调用结果的处理

**存储格式**（在 LlmAgent 中）：
- 每个工具调用结果创建一条独立的消息
- 每条消息包含一个 `UniversalToolResult`

**OpenAI 格式**：
- 每个工具结果对应一条 `role="tool"` 的消息
- 需要 `tool_call_id` 和 `name` 字段

**Anthropic 格式**：
- 所有工具结果聚合到一条 `role="user"` 的消息中
- Content 包含多个 `type="tool_result"` 的内容块

### 2. 协议转换

`OpenAIClient`:
```csharp
Universal → OpenAI (发送)
OpenAI → Universal (接收)
```

`AnthropicClient`:
```csharp
Universal → 聚合 tool 消息 → Anthropic (发送)
Anthropic 流式事件 → Universal (接收)
```

### 3. Anthropic 特殊处理

- **工具结果聚合**：`AggregateToolMessages()` 方法将连续的 tool 消息合并
- **流式响应解析**：处理多种事件类型（content_block_start、content_block_delta 等）
- **工具调用累积**：使用 `ToolCallBuilder` 累积参数的 JSON 片段

## 使用示例

### 创建客户端

```csharp
// OpenAI Client
ILLMClient openaiClient = new OpenAIClient(
    baseUrl: "http://localhost:4000/openai/v1",
    defaultModel: "vscode-lm-proxy"
);

// Anthropic Client
ILLMClient anthropicClient = new AnthropicClient(
    baseUrl: "http://localhost:4000/anthropic/v1",
    defaultModel: "vscode-lm-proxy"
);

// 使用 LlmAgent
var agent = new LlmAgent(openaiClient); // 或 anthropicClient
```

### 发送消息

```csharp
var request = new UniversalRequest {
    Model = "vscode-lm-proxy",
    Messages = new List<UniversalMessage> {
        new UniversalMessage {
            Role = "user",
            Content = "Hello, world!"
        }
    },
    Tools = tools, // 可选
    Stream = true
};

await foreach (var delta in client.StreamChatCompletionAsync(request)) {
    if (delta.Content != null) {
        Console.Write(delta.Content);
    }
    if (delta.ToolCalls != null) {
        // 处理工具调用
    }
    if (delta.FinishReason != null) {
        Console.WriteLine($"\n[完成: {delta.FinishReason}]");
    }
}
```

## 扩展新的 Provider

要添加新的 Provider（如 Google Gemini）：

1. 在 `Models/Gemini/` 创建 Provider 特定的模型类
2. 创建 `GeminiClient : ILLMClient`
3. 实现协议转换逻辑：
   - `ConvertToGeminiRequest(UniversalRequest)`
   - `ConvertToUniversalDelta(GeminiResponse)`
4. 处理该 Provider 的特殊要求（如消息格式、工具调用格式等）

## 注意事项

1. **线程安全**：客户端实现使用 `HttpClient`，是线程安全的
2. **资源管理**：客户端实现了 `IDisposable`，使用完毕后需要 Dispose
3. **错误处理**：流式响应中的错误会通过异常抛出，调用方需要适当处理
4. **工具结果格式**：始终使用 `UniversalToolResult`，转换逻辑由客户端处理
