using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.FamilyChat.Server;

internal sealed record FamilyChatTurnOptions(
    bool AutoPrefillThinkOpenTag,
    string ConnectionId
);

internal sealed record FamilyChatTurnBehavior(
    bool AutoPrefillThinkOpenTag
) {
    public static FamilyChatTurnBehavior FromUserAndTurn(FamilyChatUserConfig user, FamilyChatTurnOptions options) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(options);
        return new FamilyChatTurnBehavior(options.AutoPrefillThinkOpenTag);
    }
}

internal static class FamilyChatThinkRepairDefaults {
    public const string UnslothQwen36ModelId = "unsloth/qwen3.6";

    public static bool ShouldEnableForModel(string modelId)
        => string.Equals(modelId?.Trim(), UnslothQwen36ModelId, StringComparison.OrdinalIgnoreCase);
}

internal static class FamilyChatCompletionExecutionContext {
    private static readonly AsyncLocal<FamilyChatTurnBehavior?> CurrentSlot = new();

    public static FamilyChatTurnBehavior? Current => CurrentSlot.Value;

    public static IDisposable Push(FamilyChatTurnBehavior behavior) {
        ArgumentNullException.ThrowIfNull(behavior);

        var previous = CurrentSlot.Value;
        CurrentSlot.Value = behavior;
        return new Scope(previous);
    }

    private sealed class Scope(FamilyChatTurnBehavior? previous) : IDisposable {
        private readonly FamilyChatTurnBehavior? _previous = previous;
        private bool _disposed;

        public void Dispose() {
            if (_disposed) { return; }

            _disposed = true;
            CurrentSlot.Value = _previous;
        }
    }
}

internal sealed class FamilyChatCompletionClientDecorator : ICompletionClient {
    private const string DebugCategory = "FamilyChat.ThinkRepair";
    private const string ThinkOpenTag = "<think>";
    private readonly ICompletionClient _inner;

    public FamilyChatCompletionClientDecorator(ICompletionClient inner) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string Name => _inner.Name;

    public string ApiSpecId => _inner.ApiSpecId;

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        var behavior = FamilyChatCompletionExecutionContext.Current;
        var effectiveRequest = behavior?.AutoPrefillThinkOpenTag == true
            ? MaybeAppendAssistantThinkPrefill(request)
            : request;

        var result = await _inner.StreamCompletionAsync(effectiveRequest, observer, cancellationToken).ConfigureAwait(false);
        if (behavior?.AutoPrefillThinkOpenTag != true) { return result; }

        if (!TryRepairMissingLeadingThinkOpenTag(result.Message, out var repairedMessage)) { return result; }

        DebugUtil.Warning(
            DebugCategory,
            $"Repaired missing leading <think> tag for model={request.ModelId}, provider={_inner.Name}/{_inner.ApiSpecId}, preview={Preview(result.Message.GetFlattenedText())}"
        );

        return result with { Message = repairedMessage };
    }

    private static CompletionRequest MaybeAppendAssistantThinkPrefill(CompletionRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        if (!ShouldAppendAssistantThinkPrefill(request.Context)) { return request; }

        var effectiveContext = new List<IHistoryMessage>(request.Context.Count + 1);
        effectiveContext.AddRange(request.Context);
        effectiveContext.Add(new ActionMessage([new ActionBlock.Text(ThinkOpenTag)]));

        DebugUtil.Info(
            DebugCategory,
            $"Prefilled assistant {ThinkOpenTag} for model={request.ModelId}, contextCount={request.Context.Count}"
        );

        return request with { Context = effectiveContext };
    }

    private static bool ShouldAppendAssistantThinkPrefill(IReadOnlyList<IHistoryMessage> context) {
        if (context.Count == 0) { return false; }

        return context[^1] is ObservationMessage and not ToolResultsMessage;
    }

    private static bool TryRepairMissingLeadingThinkOpenTag(ActionMessage message, out ActionMessage repairedMessage) {
        ArgumentNullException.ThrowIfNull(message);

        repairedMessage = message;
        if (message.Blocks.Any(static block => block is ActionBlock.ReasoningBlock)) { return false; }

        int firstTextIndex = -1;
        var flattenedText = new StringBuilder();
        for (int i = 0; i < message.Blocks.Count; i++) {
            if (message.Blocks[i] is not ActionBlock.Text text) { continue; }

            if (firstTextIndex < 0) {
                firstTextIndex = i;
            }

            flattenedText.Append(text.Content);
        }

        if (firstTextIndex < 0 || !HasMissingLeadingThinkOpenTag(flattenedText.ToString())) { return false; }

        var repairedBlocks = message.Blocks.ToArray();
        var firstText = (ActionBlock.Text)repairedBlocks[firstTextIndex];
        repairedBlocks[firstTextIndex] = firstText with { Content = "<think>" + firstText.Content };
        repairedMessage = new ActionMessage(repairedBlocks);
        return true;
    }

    private static bool HasMissingLeadingThinkOpenTag(string text) {
        if (string.IsNullOrEmpty(text)) { return false; }

        int firstNonWhitespaceIndex = 0;
        while (firstNonWhitespaceIndex < text.Length && char.IsWhiteSpace(text[firstNonWhitespaceIndex])) {
            firstNonWhitespaceIndex++;
        }

        if (firstNonWhitespaceIndex < text.Length
            && text.AsSpan(firstNonWhitespaceIndex).StartsWith("<think>", StringComparison.Ordinal)) { return false; }

        int firstCloseIndex = text.IndexOf("</think>", StringComparison.Ordinal);
        if (firstCloseIndex < 0) { return false; }

        int firstOpenIndex = text.IndexOf("<think>", StringComparison.Ordinal);
        return firstOpenIndex < 0 || firstOpenIndex > firstCloseIndex;
    }

    private static string Preview(string? text) {
        if (string.IsNullOrWhiteSpace(text)) { return "<null>"; }

        string normalized = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }
}
