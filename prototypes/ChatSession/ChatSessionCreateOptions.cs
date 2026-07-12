namespace Atelia.ChatSession;

/// <summary>
/// Options for creating a brand-new chat session. The model id and completion
/// surface are sourced from the <see cref="ChatSessionRuntime"/> passed to
/// <see cref="ChatSessionEngine.CreateAsync"/> (recorded as created-with metadata),
/// because those describe the LLM connection rather than the session itself.
/// </summary>
public sealed record ChatSessionCreateOptions(
    string SystemPrompt,
    string BranchName = "main"
);
