using System.Collections.Generic;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

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
    MemoryPackBlockPath Target,
    MemoryPackBlock OldBlock
);

public sealed record MemoryMaintenanceNotice(
    string Code,
    string Message
);

public sealed record MemoryBlockMaintenanceResult(
    string MaintainerId,
    MemoryPackBlockPath Target,
    MemoryPackBlock NewBlock,
    IReadOnlyList<MemoryMaintenanceNotice> Notices,
    IReadOnlyList<string> Diagnostics,
    CompletionDescriptor? Invocation = null,
    IReadOnlyList<string>? Errors = null,
    int ToolCallsExecuted = 0
);

public sealed class CompletionMemoryBlockMaintainer : IMemoryBlockMaintainer {
    private const int MaxToolLoopIterations = 16;

    public CompletionMemoryBlockMaintainer(
        string id,
        MemoryPackBlockPath target,
        ICompletionClient completionClient,
        string modelId,
        string systemPrompt,
        string userPrompt,
        ToolSession toolSession
    ) {
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("Memory maintainer id cannot be empty.", nameof(id))
            : id;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        CompletionClient = completionClient ?? throw new ArgumentNullException(nameof(completionClient));
        ModelId = string.IsNullOrWhiteSpace(modelId)
            ? throw new ArgumentException("Model id cannot be empty.", nameof(modelId))
            : modelId;
        SystemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        UserPrompt = userPrompt ?? throw new ArgumentNullException(nameof(userPrompt));
        ToolSession = toolSession ?? throw new ArgumentNullException(nameof(toolSession));
    }

    public string Id { get; }
    public MemoryPackBlockPath Target { get; }
    public ICompletionClient CompletionClient { get; }
    public string ModelId { get; }
    public string SystemPrompt { get; }
    public string UserPrompt { get; }
    public ToolSession ToolSession { get; }

    public async ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
        MemoryBlockMaintenanceRequest request,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(request);
        if (!Equals(Target, request.Target)) { throw new ArgumentException("Maintenance request target does not match maintainer target.", nameof(request)); }

        var workingContext = BuildWorkingContext(request);
        ActionMessage? finalMessage = null;
        CompletionDescriptor? invocation = null;
        List<string>? errors = null;
        int totalToolCallsExecuted = 0;

        for (int iteration = 0; iteration < MaxToolLoopIterations; iteration++) {
            ct.ThrowIfCancellationRequested();

            var completionRequest = new CompletionRequest(
                ModelId: ModelId,
                SystemPrompt: SystemPrompt,
                Context: workingContext,
                Tools: ToolSession.VisibleDefinitions
            );

            var result = await CompletionClient.StreamCompletionAsync(completionRequest, null, ct)
                .ConfigureAwait(false);

            invocation = result.Invocation;
            if (result.Errors is { Count: > 0 }) {
                errors ??= [];
                errors.AddRange(result.Errors);
            }

            if (!result.Termination.IsSuccess) {
                throw new ChatSessionTurnAbortedException(
                    BuildTurnAbortMessage(result.Termination),
                    result.Termination,
                    result.Errors
                );
            }

            finalMessage = StripReasoningBlocks(result.Message);
            workingContext.Add(finalMessage);

            var toolCalls = finalMessage.ToolCalls;
            if (toolCalls.Count == 0) { break; }

            var toolResults = new ToolResult[toolCalls.Count];
            int executed = 0;
            for (int i = 0; i < toolCalls.Count; i++) {
                ct.ThrowIfCancellationRequested();
                var callResult = await ToolSession.ExecuteAsync(toolCalls[i], ct).ConfigureAwait(false);
                toolResults[i] = callResult.ToToolResult();
                executed++;
            }

            totalToolCallsExecuted += executed;
            workingContext.Add(new ToolResultsMessage(content: null, results: toolResults));
        }

        if (finalMessage is not null && finalMessage.ToolCalls.Count > 0) {
            throw new InvalidOperationException(
                $"Memory maintainer '{Id}' tool loop exceeded the hard limit of {MaxToolLoopIterations} iterations."
            );
        }

        var updatedText = InlineThinkTextFilter.StripInlineThinkBlocks(finalMessage?.GetFlattenedText() ?? string.Empty).Trim();
        return new MemoryBlockMaintenanceResult(
            MaintainerId: Id,
            Target: Target,
            NewBlock: new MemoryPackBlock(updatedText),
            Notices: Array.Empty<MemoryMaintenanceNotice>(),
            Diagnostics: Array.Empty<string>(),
            Invocation: invocation ?? new CompletionDescriptor("none", "none", ModelId),
            Errors: errors?.AsReadOnly(),
            ToolCallsExecuted: totalToolCallsExecuted
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
        workingContext.Add(new ObservationMessage(BuildMaintenancePrompt(request)));
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

    private string BuildMaintenancePrompt(MemoryBlockMaintenanceRequest request) {
        var builder = new StringBuilder();
        if (request.Target is null) { throw new ArgumentNullException(nameof(request.Target)); }

        builder.AppendLine("Maintain this memory block.");
        builder.Append("Target: ")
            .Append(MemoryPackCarrierTokens.ToStorageToken(request.Target.Carrier))
            .Append('/')
            .AppendLine(request.Target.BlockKey);
        builder.AppendLine();
        builder.AppendLine("Current block:");
        builder.AppendLine("```text");
        builder.AppendLine(request.OldBlock.Text);
        builder.AppendLine("```");
        builder.AppendLine();
        builder.Append("Instruction:").AppendLine();
        builder.Append(UserPrompt);
        return builder.ToString();
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

public static class MemoryMaintenanceOrchestrator {
    public static async Task<IReadOnlyList<MemoryBlockMaintenanceResult>> RunAsync(
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
            var request = new MemoryBlockMaintenanceRequest(recentHistory, maintainer.Target, oldBlock);
            tasks[i] = maintainer.MaintainAsync(request, ct).AsTask();
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static MemoryPack ApplyResults(MemoryPack memoryPack, IReadOnlyList<MemoryBlockMaintenanceResult> results) {
        ArgumentNullException.ThrowIfNull(memoryPack);
        ArgumentNullException.ThrowIfNull(results);

        var draft = new MemoryPackDraft(memoryPack);
        for (int i = 0; i < results.Count; i++) {
            draft.UpsertBlock(results[i].Target, results[i].NewBlock.Text);
        }

        return draft.Build();
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
