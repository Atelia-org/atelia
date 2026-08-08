using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.DerivedRecap.Abstractions;

/// <summary>
/// One immutable, set-level input shared by every Maintainer in a recap
/// maintenance epoch.
/// </summary>
public sealed class RecapMaintenanceEpochInput {
    public RecapMaintenanceEpochInput(
        ContextHeaderSnapshot priorContext,
        IReadOnlyList<IHistoryMessage> historyMessages,
        string? sourceId = null,
        ulong? estimatedTokens = null
    ) {
        PriorContext = priorContext
            ?? throw new ArgumentNullException(nameof(priorContext));
        ArgumentNullException.ThrowIfNull(historyMessages);
        if (historyMessages.Any(static message => message is null)) {
            throw new ArgumentException(
                "History messages cannot contain null elements.",
                nameof(historyMessages)
            );
        }
        HistoryMessages = Array.AsReadOnly([
            .. historyMessages
        ]);
        SourceId = sourceId;
        EstimatedTokens = estimatedTokens;
    }

    public ContextHeaderSnapshot PriorContext { get; }

    public IReadOnlyList<IHistoryMessage> HistoryMessages { get; }

    public string? SourceId { get; }

    public ulong? EstimatedTokens { get; }
}

/// <summary>
/// Closed set of successful Maintainer outcomes. Failures are represented by
/// exceptions and the outer execution result, never as a third success case.
/// </summary>
public abstract record RecapMaintenanceSuccess {
    private RecapMaintenanceSuccess() {
    }

    public sealed record Updated : RecapMaintenanceSuccess {
        public Updated(string content) {
            Content = content
                ?? throw new ArgumentNullException(nameof(content));
        }

        public string Content { get; }
    }

    public sealed record KeepUnchanged : RecapMaintenanceSuccess {
        private KeepUnchanged() {
        }

        public static KeepUnchanged Instance { get; } = new();
    }
}

public interface IRecapBlockMaintainer {
    string Id { get; }

    ContextHeaderBlockPath Target { get; }

    /// <summary>
    /// Opaque semantic capability identity frozen by derived-recap plans.
    /// </summary>
    string CapabilityFingerprint { get; }

    ValueTask<RecapMaintenanceSuccess> MaintainAsync(
        RecapMaintenanceEpochInput input,
        CancellationToken cancellationToken
    );
}
