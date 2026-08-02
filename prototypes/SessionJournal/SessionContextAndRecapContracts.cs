using System.Collections.Generic;
using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

public sealed record SessionContextHeader(
    string? SystemPromptFragment,
    string? ObservationMessage,
    ActionMessage? ActionMessage
) : IHistoryMessage {
    public HistoryMessageKind Kind => HistoryMessageKind.ContextHeader;
}

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

    public static ContextHeaderSnapshot FromSessionContextHeader(SessionContextHeader? header) {
        if (header is null) { return Empty; }

        return new ContextHeaderSnapshot(
            header.SystemPromptFragment ?? string.Empty,
            header.ObservationMessage ?? string.Empty,
            header.ActionMessage?.GetFlattenedText() ?? string.Empty
        );
    }

    public SessionContextHeader ToSessionContextHeader()
        => new(
            string.IsNullOrEmpty(SystemPromptFragment) ? null : SystemPromptFragment,
            string.IsNullOrEmpty(ObservationMessage) ? null : ObservationMessage,
            string.IsNullOrEmpty(ActionMessage)
                ? null
                : new ActionMessage([new ActionBlock.Text(ActionMessage)])
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

public enum ContextHeaderCarrier {
    System,
    Observation,
    Action
}

public static class ContextHeaderCarrierTokens {
    public const string System = "system";
    public const string Observation = "observation";
    public const string Action = "action";

    public static string ToStorageToken(ContextHeaderCarrier carrier)
        => carrier switch {
            ContextHeaderCarrier.System => System,
            ContextHeaderCarrier.Observation => Observation,
            ContextHeaderCarrier.Action => Action,
            _ => throw new ArgumentOutOfRangeException(nameof(carrier), carrier, "Unknown context-header carrier.")
        };

    public static bool TryParseStorageToken(string? token, out ContextHeaderCarrier carrier) {
        switch (token) {
            case System:
                carrier = ContextHeaderCarrier.System;
                return true;
            case Observation:
                carrier = ContextHeaderCarrier.Observation;
                return true;
            case Action:
                carrier = ContextHeaderCarrier.Action;
                return true;
            default:
                carrier = default;
                return false;
        }
    }
}

public sealed record ContextHeaderBlock(string Text) {
    public string Text { get; init; } = Text ?? throw new ArgumentNullException(nameof(Text));
}

public sealed record ContextHeaderBlockPath(
    ContextHeaderCarrier Carrier,
    string BlockKey
) {
    public string BlockKey { get; init; } = string.IsNullOrWhiteSpace(BlockKey)
        ? throw new ArgumentException("Context-header block key cannot be empty.", nameof(BlockKey))
        : BlockKey;
}

public sealed class ContextHeaderPack {
    public OrderedDictionary<string, ContextHeaderBlock> System { get; } = new(StringComparer.Ordinal);
    public OrderedDictionary<string, ContextHeaderBlock> Observation { get; } = new(StringComparer.Ordinal);
    public OrderedDictionary<string, ContextHeaderBlock> Action { get; } = new(StringComparer.Ordinal);

    public bool TryGetBlock(ContextHeaderBlockPath path, out ContextHeaderBlock block) {
        ArgumentNullException.ThrowIfNull(path);
        return GetCarrier(path.Carrier).TryGetValue(path.BlockKey, out block!);
    }

    public ContextHeaderSnapshot Render()
        => new(
            RenderCarrier(System),
            RenderCarrier(Observation),
            RenderCarrier(Action)
        );

    internal OrderedDictionary<string, ContextHeaderBlock> GetCarrier(ContextHeaderCarrier carrier)
        => carrier switch {
            ContextHeaderCarrier.System => System,
            ContextHeaderCarrier.Observation => Observation,
            ContextHeaderCarrier.Action => Action,
            _ => throw new ArgumentOutOfRangeException(nameof(carrier), carrier, "Unknown context-header carrier.")
        };

    public ContextHeaderPack Clone() {
        var clone = new ContextHeaderPack();
        CopyCarrier(System, clone.System);
        CopyCarrier(Observation, clone.Observation);
        CopyCarrier(Action, clone.Action);
        return clone;
    }

    private static void CopyCarrier(
        OrderedDictionary<string, ContextHeaderBlock> source,
        OrderedDictionary<string, ContextHeaderBlock> destination
    ) {
        foreach (var pair in source) {
            destination.Add(pair.Key, new ContextHeaderBlock(pair.Value.Text));
        }
    }

    private static string RenderCarrier(OrderedDictionary<string, ContextHeaderBlock> carrier) {
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

public sealed class ContextHeaderPackDraft {
    private readonly ContextHeaderPack _working;

    public ContextHeaderPackDraft(ContextHeaderPack @base) {
        Base = @base ?? throw new ArgumentNullException(nameof(@base));
        _working = @base.Clone();
    }

    public ContextHeaderPack Base { get; }

    public void ReplaceBlock(ContextHeaderBlockPath path, string newText) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newText);

        var carrier = _working.GetCarrier(path.Carrier);
        if (!carrier.ContainsKey(path.BlockKey)) { throw new KeyNotFoundException($"Context-header block does not exist: {path.Carrier}/{path.BlockKey}"); }

        carrier[path.BlockKey] = new ContextHeaderBlock(newText);
    }

    public void UpsertBlock(ContextHeaderBlockPath path, string text, int? order = null) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);

        var carrier = _working.GetCarrier(path.Carrier);
        var block = new ContextHeaderBlock(text);
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

    public bool RemoveBlock(ContextHeaderBlockPath path) {
        ArgumentNullException.ThrowIfNull(path);
        return _working.GetCarrier(path.Carrier).Remove(path.BlockKey);
    }

    public ContextHeaderPack Build() => _working.Clone();
}

public interface IRecapBlockMaintainer {
    string Id { get; }

    ContextHeaderBlockPath Target { get; }

    /// <summary>
    /// Opaque semantic capability identity frozen by derived-recap plans.
    /// </summary>
    string CapabilityFingerprint { get; }

    ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
        RecapBlockMaintenanceRequest request,
        CancellationToken ct
    );
}

public sealed record RecapBlockMaintenanceRequest(
    RecentHistorySlice RecentHistory,
    ContextHeaderBlock OldBlock
);

public sealed record RecapBlockMaintenanceResult(
    string MaintainerId,
    ContextHeaderBlockPath Target,
    ContextHeaderBlock NewBlock,
    CompletionDescriptor? Invocation = null,
    IReadOnlyList<string>? Errors = null
);
