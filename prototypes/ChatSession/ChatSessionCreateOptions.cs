namespace Atelia.ChatSession;

public sealed record ChatSessionCreateOptions(
    string ModelId,
    string SystemPrompt,
    string CompletionSurfaceId,
    string BranchName = "main"
);
