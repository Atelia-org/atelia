using Atelia.MutableContextAgentProto.Protocol;

namespace Atelia.MutableContextAgentProto.Llm;

public sealed record ChatTurnResponse(
    string? Content,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    string? FinishReason,
    string RawResponse
);
