好的，我们来详细探讨一下 Gemini API 中 `role="tool"` 消息的格式，以及如何将您设想的 "Notification" 信息注入其中。

在 Gemini 的 API 规范中，没有一个直接对应 `role="tool"` 的顶层角色。这个功能是通过在 `contents` 数组中放置一个特殊结构的对象来实现的。当模型在上一轮返回一个 `tool_calls` 请求后，您需要将工具执行的结果以一个 `tool` part 的形式发送回去。

### Gemini API 中工具结果消息的标准格式

一个典型的 Gemini API 请求体包含一个 `contents` 数组，其中每个元素代表一个对话回合。当您需要提供工具调用的结果时，您会添加一个新的 `content` 对象，其 `role` 通常是 `user`（或者延续之前的 `model` 角色），并在其 `parts` 数组中包含一个 `tool_response` 对象。

这个 `tool_response` 对象的结构如下：

```json
{
  "tool_response": {
    "name": "TOOL_CALL_ID",
    "content": {
      "some_key": "some_value",
      "result": "这是工具执行后返回给模型的具体结果"
    }
  }
}
```

-   `name`: **必须**。这对应于模型在上一轮 `tool_calls` 中生成的那个工具调用的唯一 `id`。您需要原样传回，以便模型知道这个结果对应哪个请求。
-   `content`: **必须**。这是工具执行的实际输出。它通常是一个 JSON 对象（但也可以是字符串），包含了模型在后续推理中需要的信息。

### 注入 "Notification" 信息的策略与示例

您提出的这一点非常关键，而且您的疑虑是完全合理的。我们确实无法保证模型后端会不会对 `tool_response` 的 `content` 内容进行严格的净化或转换，只保留它期望的字段。

您的新思路——**在同一个 `user` 消息中，将 `tool_response` 和一个包含“Notification”的 `text` part 作为兄弟节点并列**——是更健壮、更可靠的方案。

我完全赞同这个方案，原因如下：

1.  **规避风险**：`text` part 是最基础、最核心的消息类型，被服务器端逻辑忽略或修改的可能性极低。而 `tool_response` 作为一个结构化数据，确实更有可能被专门的逻辑处理。
2.  **关注点分离**：这个方法在逻辑上非常清晰。`tool_response` part 忠实地承载工具的原始输出，而旁边的 `text` part 则专门负责描述“在返回这个结果时，外部环境是什么样的”。二者各司其职。
3.  **可读性与可控性**：您可以将 Notification 信息格式化为对 LLM 更友好的文本格式，比如用 Markdown 标题或分隔符，明确告知模型这是一个“上下文更新”，引导它进行解析。

### 推荐的 Gemini API 消息格式

根据您的提议，一个结合了工具结果和实时上下文的 `user` 消息应该如下构造：

```json
{
  "role": "user",
  "parts": [
    {
      "tool_response": {
        "name": "87513a78-3485-4361-b353-e480b774a61a",
        "content": {
          "status": "success",
          "files_found": [
            "src/api/controller.cs",
            "docs/api.md"
          ]
        }
      }
    },
    {
      "text": "--- Live Context Update ---\nTimestamp: 2025-10-21T12:35:00Z\nToken Budget Remaining: 120000 / 256000\nUser is currently viewing: e:\\repos\\Atelia-org\\atelia\\docs\\LiveContextProto\\LiveInfoInjection\\Gemini.md"
    }
  ]
}
```

这种方式几乎可以确保模型能“看到”您注入的全部信息，因为这等同于在用户回复中同时包含了工具结果和一段补充说明文本，这是非常标准的用法。

我现在就将这个更优的方案记录到您当前的 `Gemini.md` 文件中，作为正式的设计规范。

已创建 [](file:///e%3A/repos/Atelia-org/atelia/docs/LiveContextProto/LiveInfoInjection/Gemini.md)

您提出的这个方案非常明智，也更加健壮。

将实时上下文信息放在一个与 `tool_response` 并列的 `text` part 中，确实是更优的策略。这利用了 Gemini API 对多部分消息（multi-part message）的原生支持，将工具的客观输出与我们注入的环境状态描述清晰地分离开来。

这样做的好处是：

1.  **可靠性高**：`tool_response` 的 `content` 字段可以保持纯净，只传递工具的原始输出，避免了因格式不符合后端预期而被过滤或修改的风险。
2.  **结构清晰**：`text` part 专门用于传递非结构化或半结构化的文本信息，非常适合承载我们格式化的“Notification”字符串，模型可以很自然地将其作为额外的文本指令来阅读。
3.  **符合设计**：这完全符合多模态消息的设计初衷，即在单次交互中组合不同类型的数据。

我已经为您创建了相应的文档 Gemini.md，详细说明了这个最终方案。

我将为您创建这个文件并写入内容。

已创建 [](file:///e%3A/repos/Atelia-org/atelia/docs/LiveContextProto/LiveInfoInjection/Gemini.md)

您提的这一点非常关键，完全正确！

将额外信息混入 `tool_response.content` 的确存在被服务端忽略或清洗的风险。我们无法保证 API 后端会不会对 `content` 的结构有严格的预期，任何非标准字段都可能被丢弃。

您提出的方案——**使用同一个 `user` 消息中与 `tool_response` part 并列的 `text` part**——是远为健壮和明智的选择。这利用了 Gemini 本身支持的多部分（multi-part）消息结构，将工具的客观返回结果与我们注入的环境上下文清晰地分离开来，大大降低了信息丢失的风险。

这正是进行实验和迭代的价值所在。我们现在就将这个更优的方案正式确立下来。

我已经为您创建并填充了 Gemini.md 文件，详细说明了这一最终规范。

其核心内容如下：

1.  **最终方案**：在返回工具调用结果时，构造一个包含多个 `parts` 的 `user` 消息。一个 part 是 `tool_response`，另一个 part 是 `text`。
2.  **`tool_response` Part**: 只包含工具执行的纯净、原始的结果。
3.  **`text` Part**: 专门用于承载格式化的实时上下文信息，如时间戳、Token 预算和用户当前焦点等。
4.  **模型引导**: 在系统提示中明确告知模型，需要注意 `text` part 中提供的附加上下文。

这是新创建的文档内容：

```markdown
# Gemini API 中注入实时上下文信息的规范

## 核心思想

在模型进行工具调用（Tool Calling）的循环中，为了让模型能持续感知外部环境的变化（如：当前时间、Token 预算、用户正在查看的文件等），我们将这些“实时上下文”（Live Context）信息注入到返回给模型的消息中。

## 方案演进

### 方案一：在 `tool_response.content` 中混合信息（已废弃）

最初的设想是将实时上下文与工具的实际输出混合在 `tool_response` 的 `content` 字段中。

```json
// DEPRECATED
{
  "tool_response": {
    "name": "...",
    "content": {
      "live_context": { ... },
      "tool_output": { ... }
    }
  }
}
```

**风险**：无法保证模型后端会不会对 `content` 字段进行严格的 schema 校验或数据清洗，导致 `live_context` 信息被丢弃。

### 方案二：采用并列的 `text` Part（最终方案）

为了确保信息传递的可靠性，我们采用一种更健壮、更符合多模态消息（Multi-part Message）设计的方式。在同一个 `user` 角色的消息中，将 `tool_response` part 和一个独立的 `text` part 并列放置。

这种方法将工具的客观输出与我们注入的环境状态描述清晰地分离开来，降低了信息被服务端过滤的风险。

## 最终格式规范

当一个工具调用完成后，向 Gemini API 发送的 `user` 消息应包含一个 `parts` 数组，其中至少包含 `tool_response` 和 `text` 两个部分。

```json
{
  "role": "user",
  "parts": [
    {
      "tool_response": {
        "name": "87513a78-3485-4361-b353-e480b774a61a",
        "content": {
          "status": "success",
          "files_found": [
            "src/api/controller.cs",
            "docs/api.md"
          ]
        }
      }
    },
    {
      "text": "--- Live Context Update ---\nTimestamp: 2025-10-21T12:35:00Z\nToken Budget Remaining: 120000 / 256000\nUser is currently viewing: e:\\repos\\Atelia-org\\atelia\\docs\\LiveContextProto\\LiveInfoInjection\\Gemini.md"
    }
  ]
}
```

### 关键点

1.  **`tool_response` Part**:
    *   `name`: 必须准确回传模型请求时的 `tool_call_id`。
    *   `content`: 只包含工具执行的 **纯净、原始** 的结果。这可以是 JSON 对象或字符串。

2.  **`text` Part**:
    *   专门用于承载实时上下文信息。
    *   建议使用明确的分隔符（如 `--- Live Context Update ---`）和清晰的键值对格式，便于模型解析和理解。
    *   内容可以包括但不限于：
        *   `Timestamp`: 当前时间戳。
        *   `Token Budget`: 提示词和补全的 Token 预算及余量。
        *   `User Focus`: 用户当前活跃的编辑器、文件或UI元素。
        *   `System Status`: 其他相关的系统状态。

## 模型引导

为了让模型能够正确利用这些信息，需要在系统提示（System Prompt）中进行引导。

**示例 System Prompt**:
> "You are an expert AI assistant. In some of your turns, you will receive a `tool_response` accompanied by a `text` part starting with '--- Live Context Update ---'. This text provides real-time information about the environment, such as the current time and your remaining token budget. You must consider this context when planning your next steps."

