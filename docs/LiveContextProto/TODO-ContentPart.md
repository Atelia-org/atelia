
# prototypes\Completion.Abstractions ContentPart 化

更侧重于**模型的实际处理流程**和**未来多模态扩展性**，融入一些 Google API 设计理念。

### 核心理念：从“消息流”到“内容块”

从“对话消息”（Message）的视角，转变为更底层的“内容块”（Content Part）视角。这更贴近模型处理输入的方式，并为多模态（文本、图片、工具调用等）提供了一个天然的、统一的抽象。

### 1. 消息（Message）与内容部分（Content Part）

**问题分析：**
`IContextMessage` 的实现（如 `ModelInputMessage`）直接包含了 `Notifications` 和 `Windows` 这种与特定应用场景强绑定的 `string` 字段。这限制了复用性和扩展性。

**Gemini 风格建议：**

1.  **引入 `ContentPart`：** 将所有输入都视为不同类型的“内容部分”。

    ```csharp
    // 替代 IContextMessage 的 payload
    public abstract record ContentPart;

    public sealed record TextPart(string Text) : ContentPart;
    public sealed record ToolCallPart(string ToolName, string CallId, JsonElement Arguments) : ContentPart;
    public sealed record ToolResultPart(string CallId, object Result) : ContentPart;
    // 未来可轻松扩展
    // public sealed record ImagePart(byte[] Data, string MimeType) : ContentPart;
    ```

2.  **重构消息结构：** 消息本身只是一个“内容块”的容器，并附带角色信息。

    ```csharp
    // 替代 IContextMessage, ModelInputMessage, IModelOutputMessage
    public sealed record PromptMessage(
        MessageRole Role,
        ImmutableList<ContentPart> Parts
    );

    public enum MessageRole {
        Prompt, // 使用 Prompt 而非 User，更中性
        Model, // 使用 Model 而非 Assistant，更中性
        Tool
    }
    ```

**优势：**
*   **高度可扩展**：增加新的多模态能力，只需增加一个新的 `ContentPart` 子类，无需修改消息结构。
*   **逻辑解耦**：`Notifications` 和 `Windows` 的拼接逻辑从数据模型中移除，转移到构建 `PromptMessage` 的上层业务逻辑中，使得核心抽象更纯粹。
*   **结构清晰**：一条用户输入可以包含多个 `TextPart`（例如，一个来自事件，一个来自状态），一条模型输出也可以包含 `TextPart` 和 `ToolCallPart`，结构非常统一。

---

### 2. 流式响应：从“增量”到“事件”

**Gemini 风格建议：**

进一步强调其“**响应片段**”的本质。

```csharp
// 替代 ModelOutputDelta
public sealed record CompletionChunk {
    // Chunk 的具体内容，可以是文本增量，也可以是完整的工具调用声明
    public ContentPart Part { get; }

    // 可选的元数据，只在特定 Chunk 中出现
    public TokenUsage? Usage { get; init; }
    public string? FinishReason { get; init; }
    public string? Error { get; init; }
}
```

**如何使用：**
*   当模型流式输出文本时，你会收到一系列 `CompletionChunk`，其 `Part` 类型为 `TextPart`。
*   当模型决定调用工具时，你会收到一个 `CompletionChunk`，其 `Part` 类型为 `ToolCallPart`。
*   在流的末尾，可能会收到一个包含 `Usage` 和 `FinishReason` 的 `CompletionChunk`。

**优势：**
*   **统一数据模型**：流式响应的“内容”部分复用了 `ContentPart` 模型，与输入（`PromptMessage`）形成了完美对称。
*   **语义更强**：“Chunk”（块/片段）比“Delta”（增量）更能体现它既可能是部分数据（文本片段），也可能是完整数据（工具调用）。

这个方案的**最大特点**是引入了 `ContentPart` 这一中心抽象，使得整个输入输出、流式处理的数据模型高度统一和对称，并为未来的多模态扩展铺平了道路，而无需对现有核心模型进行破坏性修改。
