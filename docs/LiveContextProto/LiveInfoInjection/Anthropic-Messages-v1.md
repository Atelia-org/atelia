# Anthropic Messages API v1

已提取 https://docs.anthropic.com/en/api/messages
已提取 https://docs.anthropic.com/en/docs/build-with-claude/tool-use
已提取 https://docs.anthropic.com/en/docs/build-with-claude/tool-use/implement-tool-use

## Anthropic Messages API 中的 Tool Result 格式详解

### 核心要点

在 Anthropic 的 API 中，**没有独立的 `role="tool"` 消息类型**。这与 OpenAI 的设计不同。工具调用结果通过 **`role="user"` 消息**中的特殊 content block 来承载。

### 📋 Tool Result 的完整结构

```json
{
  "role": "user",  // ⚠️ 必须是 "user"，不是 "tool"
  "content": [
    {
      "type": "tool_result",
      "tool_use_id": "toolu_01A09q90qw90lq917835lq9",  // 匹配 assistant 消息中的 tool_use.id
      "content": "15 degrees",  // 简单字符串
      // 或者使用结构化内容：
      // "content": [
      //   {"type": "text", "text": "Temperature: 15°C"},
      //   {"type": "image", "source": {...}},
      //   {"type": "document", "source": {...}}
      // ]
      "is_error": false  // 可选：标记工具执行是否出错
    },
    // 可以包含多个 tool_result（并行工具调用的结果）
    {
      "type": "tool_result",
      "tool_use_id": "toolu_xyz789",
      "content": [{"type": "text", "text": "Stock price: $150.32"}]
    },
    // ⚠️ 任何额外的文本必须在所有 tool_result 之后
    {
      "type": "text",
      "text": "Based on these results, what should I do next?"
    }
  ]
}
```

### 🔑 关键格式规则

1. **Role 必须是 `user`**：tool_result 必须包含在 `role="user"` 的消息中
2. **Content 的三种形式**：
   - 简单字符串：`"content": "result text"`
   - 嵌套 content blocks：`"content": [{"type": "text", "text": "..."}]`
   - Document blocks：`"content": [{"type": "document", "source": {...}}]`
3. **顺序约束**：
   - Tool result blocks **必须排在最前面**
   - 任何文本内容必须在所有 tool_result 之后
   - Tool result 消息必须紧跟在 assistant 的 tool_use 消息之后

### 💡 注入 Notification 的策略

基于这个格式，你有几种方式在工具调用循环中注入环境感知信息：

#### **方案 1：在 tool_result 后添加文本块（推荐）**

```json
{
  "role": "user",
  "content": [
    {
      "type": "tool_result",
      "tool_use_id": "toolu_abc123",
      "content": "Weather data: 15°C, cloudy"
    },
    {
      "type": "text",
      "text": "[SYSTEM STATUS] Current time: 2025-10-21 14:30:00 UTC | Token budget remaining: 8,542 tokens | Memory: 73% used"
    }
  ]
}
```

**优点**：
- 简单直接，符合 API 规范
- LLM 会自然地感知到这些信息
- 不干扰工具结果本身的语义

#### **方案 2：在 tool_result 的 content 中嵌入（需要工具支持）**

```json
{
  "type": "tool_result",
  "tool_use_id": "toolu_abc123",
  "content": [
    {"type": "text", "text": "Weather: 15°C, cloudy"},
    {"type": "text", "text": "---\n[Metadata] Execution time: 2025-10-21 14:30:00 | Tokens used: 127"}
  ]
}
```

#### **方案 3：通过 System Prompt 动态更新**

每次工具调用返回时，更新顶层的 `system` 参数：

```json
{
  "model": "claude-sonnet-4-5",
  "system": "Current time: 2025-10-21 14:30:00 UTC. Token budget: 8,542 remaining. Previous tools used: get_weather, get_stock_price.",
  "messages": [...]
}
```

**优点**：
- 全局可见，不污染对话历史
- 适合频繁变化的元数据

### 📝 完整示例：带 Notification 的工具循环

```json
// 第一轮：User 提问
{
  "model": "claude-sonnet-4-5",
  "max_tokens": 1024,
  "system": "[LiveContext] Session started: 2025-10-21 14:25:00 | Token budget: 10,000",
  "messages": [
    {"role": "user", "content": "What's the weather in SF and current BTC price?"}
  ],
  "tools": [...]
}

// Claude 响应：并行调用两个工具
{
  "role": "assistant",
  "content": [
    {"type": "text", "text": "I'll check both for you."},
    {"type": "tool_use", "id": "toolu_001", "name": "get_weather", "input": {"location": "SF"}},
    {"type": "tool_use", "id": "toolu_002", "name": "get_crypto_price", "input": {"symbol": "BTC"}}
  ]
}

// 第二轮：返回工具结果 + Notification
{
  "model": "claude-sonnet-4-5",
  "max_tokens": 1024,
  "system": "[LiveContext] Current: 2025-10-21 14:27:15 | Token budget: 8,542 remaining | Tools called: 2",
  "messages": [
    {"role": "user", "content": "What's the weather in SF and current BTC price?"},
    {
      "role": "assistant",
      "content": [
        {"type": "text", "text": "I'll check both for you."},
        {"type": "tool_use", "id": "toolu_001", "name": "get_weather", "input": {"location": "SF"}},
        {"type": "tool_use", "id": "toolu_002", "name": "get_crypto_price", "input": {"symbol": "BTC"}}
      ]
    },
    {
      "role": "user",
      "content": [
        {
          "type": "tool_result",
          "tool_use_id": "toolu_001",
          "content": "18°C, sunny"
        },
        {
          "type": "tool_result",
          "tool_use_id": "toolu_002",
          "content": "$67,432.50 USD"
        },
        {
          "type": "text",
          "text": "⏱️ [SYSTEM] Time: 14:27:15 | 💰 Budget: 8,542 tokens | 🔧 2 tools executed successfully"
        }
      ]
    }
  ],
  "tools": [...]
}
```

### ⚠️ 常见错误

```json
// ❌ 错误：文本在 tool_result 之前
{"role": "user", "content": [
  {"type": "text", "text": "Here are results:"},
  {"type": "tool_result", "tool_use_id": "..."}
]}

// ❌ 错误：使用了不存在的 role="tool"
{"role": "tool", "content": "..."}

// ✅ 正确
{"role": "user", "content": [
  {"type": "tool_result", "tool_use_id": "..."},
  {"type": "text", "text": "Additional context"}
]}
```

### 🎯 针对你的 LiveContextProto 项目的建议

基于你当前的 `ContentsBuilder.cs` 文件位置，你可能需要：

1. **在工具结果序列化时自动附加 Notification**
2. **设计一个 `NotificationInjector` 类**，在构建 user message 时插入环境信息
3. **考虑 Token 成本**：每次注入大约会消耗 20-50 tokens

示例设计：

```csharp
public class NotificationInjector
{
    public ContentBlock CreateNotificationBlock()
    {
        var timestamp = DateTime.UtcNow;
        var tokenBudget = TokenBudgetManager.GetRemaining();

        return new TextContentBlock
        {
            Type = "text",
            Text = $"[SYSTEM] {timestamp:O} | Budget: {tokenBudget} tokens"
        };
    }
}

// 在构建消息时
var userMessage = new Message
{
    Role = "user",
    Content = new List<ContentBlock>
    {
        // 先添加所有 tool_result
        ...toolResults,
        // 最后添加 Notification
        NotificationInjector.CreateNotificationBlock()
    }
};
```
