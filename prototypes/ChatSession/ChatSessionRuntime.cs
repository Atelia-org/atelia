using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession;

public sealed record ChatSessionRuntime(
    ICompletionClient CompletionClient,
    string CompletionSurfaceId,
    ToolSession ToolSession
);
