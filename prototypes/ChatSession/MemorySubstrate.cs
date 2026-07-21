using System.Collections.Generic;
using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.ChatSession;

public sealed record ContextHeaderSnapshot(
    string SystemPromptFragment,
    string ObservationMessage,
    string ActionMessage
) {
    public static ContextHeaderSnapshot Empty { get; } = new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty =>
        string.IsNullOrEmpty(SystemPromptFragment)
        && string.IsNullOrEmpty(ObservationMessage)
        && string.IsNullOrEmpty(ActionMessage);

    public static ContextHeaderSnapshot FromContextHeader(ContextHeader? header) {
        if (header is null) { return Empty; }

        return new ContextHeaderSnapshot(
            header.SystemPromptFragment ?? string.Empty,
            header.ObservationMessage ?? string.Empty,
            header.ActionMessage?.GetFlattenedText() ?? string.Empty
        );
    }

    public static ContextHeaderSnapshot FromRenderedMemoryPack(RenderedMemoryPack rendered)
        => rendered is null
            ? throw new ArgumentNullException(nameof(rendered))
            : new ContextHeaderSnapshot(
                rendered.SystemPromptFragment,
                rendered.ObservationMessage,
                rendered.ActionMessage
            );
}

public sealed record RecentHistorySlice {
    public RecentHistorySlice(
        ContextHeaderSnapshot PriorContext,
        IReadOnlyList<IHistoryMessage> Messages,
        string? SourceId = null,
        ulong? EstimatedTokens = null
    ) {
        this.PriorContext = PriorContext ?? throw new ArgumentNullException(nameof(PriorContext));
        this.Messages = FreezeMessages(Messages);
        this.SourceId = SourceId;
        this.EstimatedTokens = EstimatedTokens;
    }

    public ContextHeaderSnapshot PriorContext { get; }
    public IReadOnlyList<IHistoryMessage> Messages { get; }
    public string? SourceId { get; }
    public ulong? EstimatedTokens { get; }

    private static IReadOnlyList<IHistoryMessage> FreezeMessages(IReadOnlyList<IHistoryMessage> messages) {
        ArgumentNullException.ThrowIfNull(messages);
        return Array.AsReadOnly(messages.ToArray());
    }
}

public interface IRecentHistoryAnalyzer {
    string Id { get; }

    ValueTask AnalyzeAsync(
        RecentHistoryAnalysisContext context,
        CancellationToken ct
    );
}

public sealed record RecentHistoryAnalysisContext(
    RecentHistorySlice RecentHistory,
    IServiceProvider? Services = null
);

public enum MemoryPackCarrier {
    System,
    Observation,
    Action
}

public static class MemoryPackCarrierTokens {
    public const string System = "system";
    public const string Observation = "observation";
    public const string Action = "action";

    public static string ToStorageToken(MemoryPackCarrier carrier)
        => carrier switch {
            MemoryPackCarrier.System => System,
            MemoryPackCarrier.Observation => Observation,
            MemoryPackCarrier.Action => Action,
            _ => throw new ArgumentOutOfRangeException(nameof(carrier), carrier, "Unknown memory pack carrier.")
        };

    public static bool TryParseStorageToken(string? token, out MemoryPackCarrier carrier) {
        switch (token) {
            case System:
                carrier = MemoryPackCarrier.System;
                return true;
            case Observation:
                carrier = MemoryPackCarrier.Observation;
                return true;
            case Action:
                carrier = MemoryPackCarrier.Action;
                return true;
            default:
                carrier = default;
                return false;
        }
    }
}

public sealed record MemoryPackBlock(string Text) {
    public string Text { get; init; } = Text ?? throw new ArgumentNullException(nameof(Text));
}

public sealed record MemoryPackBlockPath(
    MemoryPackCarrier Carrier,
    string BlockKey
) {
    public string BlockKey { get; init; } = string.IsNullOrWhiteSpace(BlockKey)
        ? throw new ArgumentException("Memory pack block key cannot be empty.", nameof(BlockKey))
        : BlockKey;
}

public sealed class MemoryPack {
    public OrderedDictionary<string, MemoryPackBlock> System { get; } = new(StringComparer.Ordinal);
    public OrderedDictionary<string, MemoryPackBlock> Observation { get; } = new(StringComparer.Ordinal);
    public OrderedDictionary<string, MemoryPackBlock> Action { get; } = new(StringComparer.Ordinal);

    public bool TryGetBlock(MemoryPackBlockPath path, out MemoryPackBlock block) {
        ArgumentNullException.ThrowIfNull(path);
        return GetCarrier(path.Carrier).TryGetValue(path.BlockKey, out block!);
    }

    public RenderedMemoryPack Render()
        => new(
            RenderCarrier(System),
            RenderCarrier(Observation),
            RenderCarrier(Action)
        );

    internal OrderedDictionary<string, MemoryPackBlock> GetCarrier(MemoryPackCarrier carrier)
        => carrier switch {
            MemoryPackCarrier.System => System,
            MemoryPackCarrier.Observation => Observation,
            MemoryPackCarrier.Action => Action,
            _ => throw new ArgumentOutOfRangeException(nameof(carrier), carrier, "Unknown memory pack carrier.")
        };

    public MemoryPack Clone() {
        var clone = new MemoryPack();
        CopyCarrier(System, clone.System);
        CopyCarrier(Observation, clone.Observation);
        CopyCarrier(Action, clone.Action);
        return clone;
    }

    private static void CopyCarrier(
        OrderedDictionary<string, MemoryPackBlock> source,
        OrderedDictionary<string, MemoryPackBlock> destination
    ) {
        foreach (var pair in source) {
            destination.Add(pair.Key, new MemoryPackBlock(pair.Value.Text));
        }
    }

    private static string RenderCarrier(OrderedDictionary<string, MemoryPackBlock> carrier) {
        if (carrier.Count == 0) { return string.Empty; }

        var builder = new StringBuilder();
        foreach (var pair in carrier) {
            if (builder.Length > 0) { builder.AppendLine().AppendLine(); }

            builder.Append("## ").AppendLine(pair.Key);
            builder.AppendLine();
            builder.Append(pair.Value.Text);
        }

        return builder.ToString();
    }
}

public sealed record RenderedMemoryPack(
    string SystemPromptFragment,
    string ObservationMessage,
    string ActionMessage
) {
    public ContextHeader ToContextHeader()
        => new(
            string.IsNullOrEmpty(SystemPromptFragment) ? null : SystemPromptFragment,
            string.IsNullOrEmpty(ObservationMessage) ? null : ObservationMessage,
            string.IsNullOrEmpty(ActionMessage)
                ? null
                : new ActionMessage([new ActionBlock.Text(ActionMessage)])
        );
}

public sealed class MemoryPackDraft {
    private readonly MemoryPack _working;

    public MemoryPackDraft(MemoryPack @base) {
        Base = @base ?? throw new ArgumentNullException(nameof(@base));
        _working = @base.Clone();
    }

    public MemoryPack Base { get; }

    public void ReplaceBlock(MemoryPackBlockPath path, string newText) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newText);

        var carrier = _working.GetCarrier(path.Carrier);
        if (!carrier.ContainsKey(path.BlockKey)) { throw new KeyNotFoundException($"Memory pack block does not exist: {path.Carrier}/{path.BlockKey}"); }

        carrier[path.BlockKey] = new MemoryPackBlock(newText);
    }

    public void UpsertBlock(MemoryPackBlockPath path, string text, int? order = null) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);

        var carrier = _working.GetCarrier(path.Carrier);
        var block = new MemoryPackBlock(text);
        int? existingIndex = carrier.ContainsKey(path.BlockKey) ? carrier.IndexOf(path.BlockKey) : null;
        if (carrier.ContainsKey(path.BlockKey)) {
            carrier.Remove(path.BlockKey);
        }

        int? insertionOrder = order ?? existingIndex;
        if (insertionOrder is null || insertionOrder.Value >= carrier.Count) {
            carrier.Add(path.BlockKey, block);
            return;
        }

        if (insertionOrder.Value < 0) { throw new ArgumentOutOfRangeException(nameof(order), order, "Order cannot be negative."); }
        carrier.Insert(insertionOrder.Value, path.BlockKey, block);
    }

    public bool RemoveBlock(MemoryPackBlockPath path) {
        ArgumentNullException.ThrowIfNull(path);
        return _working.GetCarrier(path.Carrier).Remove(path.BlockKey);
    }

    public MemoryPack Build() => _working.Clone();
}

public interface IMemoryBlockMaintainer {
    string Id { get; }

    MemoryPackBlockPath Target { get; }

    ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
        MemoryBlockMaintenanceRequest request,
        CancellationToken ct
    );
}

public sealed record MemoryBlockMaintenanceRequest(
    RecentHistorySlice RecentHistory,
    MemoryPackBlock OldBlock
);

public sealed record MemoryBlockMaintenanceResult(
    string MaintainerId,
    MemoryPackBlockPath Target,
    MemoryPackBlock NewBlock,
    CompletionDescriptor? Invocation = null,
    IReadOnlyList<string>? Errors = null
);

public sealed record MemoryRewriteProfile(
    string Id,
    MemoryPackBlockPath Target,
    string SystemPrompt,
    string UserPrompt
) {
    public string Id { get; init; } = string.IsNullOrWhiteSpace(Id)
        ? throw new ArgumentException("Memory rewrite profile id cannot be empty.", nameof(Id))
        : Id;
    public MemoryPackBlockPath Target { get; init; } = Target ?? throw new ArgumentNullException(nameof(Target));
    public string SystemPrompt { get; init; } = SystemPrompt ?? throw new ArgumentNullException(nameof(SystemPrompt));
    public string UserPrompt { get; init; } = UserPrompt ?? throw new ArgumentNullException(nameof(UserPrompt));
}

public sealed class RewriteMemoryBlockMaintainer : IMemoryBlockMaintainer {
    private readonly MemoryRewriteProfile _profile;

    public RewriteMemoryBlockMaintainer(
        MemoryRewriteProfile profile,
        ICompletionClient completionClient,
        string modelId
    ) {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        CompletionClient = completionClient ?? throw new ArgumentNullException(nameof(completionClient));
        ModelId = string.IsNullOrWhiteSpace(modelId)
            ? throw new ArgumentException("Model id cannot be empty.", nameof(modelId))
            : modelId;
    }

    public string Id => _profile.Id;
    public MemoryPackBlockPath Target => _profile.Target;
    public ICompletionClient CompletionClient { get; }
    public string ModelId { get; }

    public async ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
        MemoryBlockMaintenanceRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        var workingContext = BuildWorkingContext(request);
        var result = await CompletionClient.StreamCompletionAsync(
            new CompletionRequest(
                ModelId: ModelId,
                SystemPrompt: _profile.SystemPrompt,
                Context: workingContext,
                Tools: []
            ),
            observer: null,
            ct
        ).ConfigureAwait(false);

        if (!result.Termination.IsSuccess) {
            throw new ChatSessionTurnAbortedException(
                BuildTurnAbortMessage(result.Termination),
                result.Termination,
                result.Errors
            );
        }

        var finalMessage = StripReasoningBlocks(result.Message);
        if (finalMessage.ToolCalls.Count > 0) { throw new InvalidOperationException($"Rewrite maintainer '{Id}' returned unexpected tool calls."); }

        var updatedText = NormalizeBlockText(finalMessage.GetFlattenedText());
        return new MemoryBlockMaintenanceResult(
            MaintainerId: Id,
            Target: Target,
            NewBlock: new MemoryPackBlock(updatedText),
            Invocation: result.Invocation,
            Errors: result.Errors
        );
    }

    private static string BuildTurnAbortMessage(CompletionTermination termination) {
        ArgumentNullException.ThrowIfNull(termination);

        return termination.Kind switch {
            CompletionTerminationKind.Incomplete =>
                $"Completion ended incompletely and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
            CompletionTerminationKind.Failed =>
                $"Completion failed and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
            _ =>
                $"Completion was aborted and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}"
        };
    }

    private List<IHistoryMessage> BuildWorkingContext(MemoryBlockMaintenanceRequest request) {
        var recentHistory = request.RecentHistory;
        var workingContext = new List<IHistoryMessage>(recentHistory.Messages.Count + 8);

        if (!string.IsNullOrWhiteSpace(recentHistory.PriorContext.SystemPromptFragment)) {
            workingContext.Add(new ObservationMessage(recentHistory.PriorContext.SystemPromptFragment));
        }

        if (!string.IsNullOrWhiteSpace(recentHistory.PriorContext.ObservationMessage)) {
            workingContext.Add(new ObservationMessage(recentHistory.PriorContext.ObservationMessage));
        }

        if (!string.IsNullOrWhiteSpace(recentHistory.PriorContext.ActionMessage)) {
            workingContext.Add(new ActionMessage([new ActionBlock.Text(recentHistory.PriorContext.ActionMessage)]));
        }

        AddProjectedMessages(workingContext, recentHistory.Messages);
        workingContext.Add(new ObservationMessage(BuildMaintenancePrompt()));
        return workingContext;
    }

    private static void AddProjectedMessages(List<IHistoryMessage> destination, IReadOnlyList<IHistoryMessage> messages) {
        for (int i = 0; i < messages.Count; i++) {
            var original = messages[i];
            switch (original.Kind) {
                case HistoryMessageKind.ContextHeader:
                    var header = (ContextHeader)original;
                    if (!string.IsNullOrWhiteSpace(header.SystemPromptFragment)) { destination.Add(new ObservationMessage(header.SystemPromptFragment)); }
                    if (!string.IsNullOrWhiteSpace(header.ObservationMessage)) { destination.Add(new ObservationMessage(header.ObservationMessage)); }
                    if (header.ActionMessage is not null) { destination.Add(StripReasoningBlocks(header.ActionMessage)); }
                    break;
                case HistoryMessageKind.Action:
                    destination.Add(StripReasoningBlocks((ActionMessage)original));
                    break;
                case HistoryMessageKind.Observation:
                case HistoryMessageKind.ToolResults:
                    destination.Add(original);
                    break;
            }
        }
    }

    private string BuildMaintenancePrompt() {
        var builder = new StringBuilder();
        builder.AppendLine("Maintain this memory block.");
        builder.Append("Target: ")
            .Append(MemoryPackCarrierTokens.ToStorageToken(Target.Carrier))
            .Append('/')
            .AppendLine(Target.BlockKey);
        builder.AppendLine();
        builder.Append("Instruction:").AppendLine();
        builder.Append(_profile.UserPrompt);
        return builder.ToString();
    }

    private static string NormalizeBlockText(string? text) {
        var trimmed = InlineThinkTextFilter.StripInlineThinkBlocks(text ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) { return trimmed; }

        int firstLineEnd = trimmed.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd < 0) { return trimmed; }

        int closingFenceStart = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceStart <= firstLineEnd) { return trimmed; }

        string trailing = trimmed[(closingFenceStart + 3)..].Trim();
        return trailing.Length == 0
            ? trimmed[(firstLineEnd + 1)..closingFenceStart].Trim()
            : trimmed;
    }

    private static ActionMessage StripReasoningBlocks(ActionMessage action) {
        var filtered = new List<ActionBlock>(action.Blocks.Count);
        for (int i = 0; i < action.Blocks.Count; i++) {
            switch (action.Blocks[i]) {
                case ActionBlock.Text text:
                    var visibleText = InlineThinkTextFilter.StripInlineThinkBlocks(text.Content);
                    if (!string.IsNullOrEmpty(visibleText)) {
                        filtered.Add(new ActionBlock.Text(visibleText));
                    }
                    break;
                case ActionBlock.ToolCall:
                    filtered.Add(action.Blocks[i]);
                    break;
            }
        }

        return new ActionMessage(filtered);
    }
}

public sealed record MemoryMaintenanceBatchResult(
    IReadOnlyList<MemoryBlockMaintenanceResult> Results,
    MemoryPack UpdatedMemoryPack
);

public static class MemoryMaintenanceOrchestrator {
    public static async Task<MemoryMaintenanceBatchResult> RunAsync(
        MemoryPack memoryPack,
        RecentHistorySlice recentHistory,
        IReadOnlyList<IMemoryBlockMaintainer> maintainers,
        CancellationToken ct = default
    ) {
        ArgumentNullException.ThrowIfNull(memoryPack);
        ArgumentNullException.ThrowIfNull(recentHistory);
        ArgumentNullException.ThrowIfNull(maintainers);

        ValidateMaintainers(maintainers);

        var tasks = new Task<MemoryBlockMaintenanceResult>[maintainers.Count];
        for (int i = 0; i < maintainers.Count; i++) {
            var maintainer = maintainers[i];
            var oldBlock = memoryPack.TryGetBlock(maintainer.Target, out var found)
                ? found
                : new MemoryPackBlock(string.Empty);
            var request = new MemoryBlockMaintenanceRequest(recentHistory, oldBlock);
            tasks[i] = maintainer.MaintainAsync(request, ct).AsTask();
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var draft = new MemoryPackDraft(memoryPack);
        for (int i = 0; i < results.Length; i++) {
            if (!string.Equals(results[i].MaintainerId, maintainers[i].Id, StringComparison.Ordinal)) { throw new InvalidOperationException($"Memory maintainer '{maintainers[i].Id}' returned mismatched id '{results[i].MaintainerId}'."); }
            if (!Equals(results[i].Target, maintainers[i].Target)) { throw new InvalidOperationException($"Memory maintainer '{maintainers[i].Id}' returned a mismatched target."); }
            draft.UpsertBlock(results[i].Target, results[i].NewBlock.Text);
        }

        return new MemoryMaintenanceBatchResult(results, draft.Build());
    }

    public static void ValidateMaintainers(IReadOnlyList<IMemoryBlockMaintainer> maintainers) {
        if (maintainers.Count == 0) { throw new ArgumentException("At least one memory maintainer is required.", nameof(maintainers)); }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<MemoryPackBlockPath>();
        for (int i = 0; i < maintainers.Count; i++) {
            var maintainer = maintainers[i] ?? throw new ArgumentException("Memory maintainer cannot be null.", nameof(maintainers));
            if (string.IsNullOrWhiteSpace(maintainer.Id)) { throw new ArgumentException("Memory maintainer id cannot be empty.", nameof(maintainers)); }
            if (!ids.Add(maintainer.Id)) { throw new ArgumentException($"Duplicate memory maintainer id: {maintainer.Id}", nameof(maintainers)); }
            if (!targets.Add(maintainer.Target)) { throw new ArgumentException($"Duplicate memory maintainer target: {maintainer.Target.Carrier}/{maintainer.Target.BlockKey}", nameof(maintainers)); }
        }
    }
}
