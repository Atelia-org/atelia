using Atelia.MutableContextAgentProto.Protocol;

namespace Atelia.MutableContextAgentProto.Llm;

public sealed record ChatTurnRequest(
    string UserMessage,
    IReadOnlyList<ToolDefinition> Tools,
    ChatToolChoice ToolChoice = ChatToolChoice.Auto
) {
    public ChatTurnRequest(string userMessage)
        : this(userMessage, []) {
    }
}
