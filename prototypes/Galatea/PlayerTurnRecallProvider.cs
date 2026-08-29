using Atelia.EventJournal;

namespace Atelia.Galatea.Server;

internal sealed record GalateaPlayerTurnRecallRequest {
    internal GalateaPlayerTurnRecallRequest(
        GalateaUserConfig user,
        EventAddress completionBoundary,
        string playerText,
        IReadOnlyList<PlayerTurnNotice> notices,
        RecallBarrier barrier
    ) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(barrier);
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
        Barrier = barrier;
    }

    internal GalateaUserConfig User { get; }
    internal EventAddress CompletionBoundary { get; }
    internal string PlayerText { get; }
    internal IReadOnlyList<PlayerTurnNotice> Notices { get; }
    internal RecallBarrier Barrier { get; }
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
