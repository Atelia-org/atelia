using System.Text;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;

namespace Atelia.Galatea.Server;

internal sealed record GalateaRecentVisibleAction {
    internal GalateaRecentVisibleAction(string text) {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        try {
            _ = GalateaBoundedJson.StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Recent visible Action must contain valid Unicode.",
                nameof(text),
                exception
            );
        }
        Text = text;
    }

    internal string Text { get; }
}

internal sealed record GalateaPlayerTurnRecallContext {
    internal GalateaPlayerTurnRecallContext(
        RecallBarrier recallBarrier,
        CharacterNoteOriginBarrier characterNoteOriginBarrier,
        IEnumerable<GalateaRecentVisibleAction>? recentVisibleActions = null
    ) {
        ArgumentNullException.ThrowIfNull(recallBarrier);
        ArgumentNullException.ThrowIfNull(characterNoteOriginBarrier);
        GalateaRecentVisibleAction[] frozen = recentVisibleActions?.Select(
            static action => action ?? throw new ArgumentException(
                "Recall context Action collections must not contain null items.",
                nameof(recentVisibleActions)
            )
        ).ToArray() ?? [];

        RecallBarrier = recallBarrier;
        CharacterNoteOriginBarrier = characterNoteOriginBarrier;
        RecentVisibleActions = Array.AsReadOnly(frozen);
    }

    internal RecallBarrier RecallBarrier { get; }
    internal CharacterNoteOriginBarrier CharacterNoteOriginBarrier { get; }
    internal IReadOnlyList<GalateaRecentVisibleAction> RecentVisibleActions {
        get;
    }
}

internal sealed record GalateaPlayerTurnRecallRequest {
    internal GalateaPlayerTurnRecallRequest(
        GalateaUserConfig user,
        EventAddress completionBoundary,
        PlayerTurnObservation currentObservation,
        GalateaPlayerTurnRecallContext context
    ) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(currentObservation);
        ArgumentNullException.ThrowIfNull(context);
        if (completionBoundary == default) {
            throw new ArgumentException(
                "Completion boundary cannot be the default EventAddress.",
                nameof(completionBoundary)
            );
        }
        if (currentObservation.Recalls.Count != 0) {
            throw new ArgumentException(
                "A preliminary recall Observation must not already contain recalls.",
                nameof(currentObservation)
            );
        }
        if (currentObservation.ExternalLocalTimestamp is null) {
            throw new ArgumentException(
                "A preliminary recall Observation requires its sampled external local timestamp.",
                nameof(currentObservation)
            );
        }

        User = user;
        CompletionBoundary = completionBoundary;
        CurrentObservation = currentObservation;
        Context = context;
    }

    internal GalateaUserConfig User { get; }
    internal EventAddress CompletionBoundary { get; }
    internal PlayerTurnObservation CurrentObservation { get; }
    internal GalateaPlayerTurnRecallContext Context { get; }
}

internal interface IGalateaPlayerTurnRecallProvider {
    ValueTask<IReadOnlyList<PlayerTurnRecall>> SelectRecallsAsync(
        GalateaPlayerTurnRecallRequest request,
        CancellationToken cancellationToken
    );
}

internal delegate IGalateaPlayerTurnRecallProvider
    GalateaPlayerTurnRecallProviderFactory(
        GalateaUserConfig user,
        CharacterNoteDefaultPodReconciler? characterMemory
    );

internal sealed class DisabledGalateaPlayerTurnRecallProvider
    : IGalateaPlayerTurnRecallProvider {
    private DisabledGalateaPlayerTurnRecallProvider() { }

    internal static DisabledGalateaPlayerTurnRecallProvider Instance {
        get;
    } = new();

    public ValueTask<IReadOnlyList<PlayerTurnRecall>> SelectRecallsAsync(
        GalateaPlayerTurnRecallRequest request,
        CancellationToken cancellationToken
    ) => throw new InvalidOperationException(
        "The disabled player-turn recall provider must be bypassed before recall context construction."
    );
}
