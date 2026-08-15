using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession;

/// <summary>
/// Describes how a turn talks to the LLM: the completion client (carrying its
/// <see cref="ICompletionClient.ApiSpecId"/>), the completion surface, the model id,
/// and the tool session.  A runtime is <b>not</b> baked into the persisted session;
/// callers may swap it between turns via <see cref="ChatSessionEngine.UseRuntime"/>
/// so the same session history can be continued with different LLM connections.
/// </summary>
public sealed record ChatSessionRuntime(
    ICompletionClient CompletionClient,
    string CompletionSurfaceId,
    string ModelId,
    ToolSession ToolSession
);
