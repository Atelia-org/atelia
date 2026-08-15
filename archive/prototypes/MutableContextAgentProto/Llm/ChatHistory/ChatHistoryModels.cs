using Atelia.MutableContextAgentProto.Protocol;

namespace Atelia.MutableContextAgentProto.Llm.ChatHistory;

public enum ChatMessageRole {
    System,
    User,
    Assistant,
    Tool,
}

public abstract record ChatHistoryMessage(ChatMessageRole Role);

public sealed record SystemChatHistoryMessage(string Content)
    : ChatHistoryMessage(ChatMessageRole.System);

public sealed record UserChatHistoryMessage(string Content)
    : ChatHistoryMessage(ChatMessageRole.User);

public sealed record AssistantChatHistoryMessage(
    string? Content,
    string? ReasoningContent = null,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null
) : ChatHistoryMessage(ChatMessageRole.Assistant);

public sealed record ToolChatHistoryMessage(
    string ToolCallId,
    string Name,
    string Content
) : ChatHistoryMessage(ChatMessageRole.Tool);

public sealed record ChatHistoryRequest(
    IReadOnlyList<ChatHistoryMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    ChatToolChoice ToolChoice = ChatToolChoice.Auto
) {
    public ChatHistoryRequest(IReadOnlyList<ChatHistoryMessage> messages)
        : this(messages, []) {
    }
}

public sealed record ChatHistoryResponse(
    AssistantChatHistoryMessage AssistantMessage,
    string? FinishReason,
    string RawResponse
);
