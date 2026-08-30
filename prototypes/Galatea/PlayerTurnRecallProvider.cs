using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;

namespace Atelia.Galatea.Server;

internal sealed record GalateaPlayerTurnRecallBarriers(
    RecallBarrier RecallBarrier,
    CharacterNoteOriginBarrier CharacterNoteOriginBarrier
);

internal sealed record GalateaPlayerTurnRecallRequest {
    internal GalateaPlayerTurnRecallRequest(
        GalateaUserConfig user,
        EventAddress completionBoundary,
        string playerText,
        IReadOnlyList<PlayerTurnNotice> notices,
        RecallBarrier recallBarrier,
        CharacterNoteOriginBarrier characterNoteOriginBarrier
    ) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(recallBarrier);
        ArgumentNullException.ThrowIfNull(characterNoteOriginBarrier);
        if (completionBoundary == default) {
            throw new ArgumentException(
                "Completion boundary cannot be the default EventAddress.",
                nameof(completionBoundary)
            );
        }
        var observation = new PlayerTurnObservation(playerText, notices);

        User = user;
        CompletionBoundary = completionBoundary;
        PlayerText = observation.PlayerText;
        Notices = observation.Notices;
        RecallBarrier = recallBarrier;
        CharacterNoteOriginBarrier = characterNoteOriginBarrier;
    }

    internal GalateaUserConfig User { get; }
    internal EventAddress CompletionBoundary { get; }
    internal string PlayerText { get; }
    internal IReadOnlyList<PlayerTurnNotice> Notices { get; }
    internal RecallBarrier RecallBarrier { get; }
    internal CharacterNoteOriginBarrier CharacterNoteOriginBarrier {
        get;
    }
}

internal interface IGalateaPlayerTurnRecallProvider {
    ValueTask<IReadOnlyList<PlayerTurnRecall>> SelectRecallsAsync(
        GalateaPlayerTurnRecallRequest request,
        CancellationToken cancellationToken
    );
}

internal sealed class DisabledGalateaPlayerTurnRecallProvider
    : IGalateaPlayerTurnRecallProvider {
    private DisabledGalateaPlayerTurnRecallProvider() { }

    internal static DisabledGalateaPlayerTurnRecallProvider Instance {
        get;
    } = new();

    public ValueTask<IReadOnlyList<PlayerTurnRecall>> SelectRecallsAsync(
        GalateaPlayerTurnRecallRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<PlayerTurnRecall>>(
            Array.Empty<PlayerTurnRecall>()
        );
    }
}
