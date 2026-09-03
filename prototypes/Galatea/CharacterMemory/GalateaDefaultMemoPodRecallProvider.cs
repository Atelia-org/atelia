using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed class GalateaDefaultMemoPodRecallProvider
    : IGalateaPlayerTurnRecallProvider {
    private readonly CharacterNoteDefaultPodReconciler _reconciler;
    private readonly CompletionConnectionConfig _connection;
    private readonly Func<ICompletionClient> _completionClientAccessor;
    private readonly MemoRecallOptions _options;

    internal GalateaDefaultMemoPodRecallProvider(
        CharacterNoteDefaultPodReconciler reconciler,
        CompletionConnectionConfig connection,
        Func<ICompletionClient> completionClientAccessor
    ) {
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(completionClientAccessor);
        ArgumentException.ThrowIfNullOrWhiteSpace(connection.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(connection.ModelId);

        _reconciler = reconciler;
        _connection = connection;
        _completionClientAccessor = completionClientAccessor;
        _options = new MemoRecallOptions(
            GalateaMemoRecallMvpPolicy.MaxResults,
            GalateaMemoRecallMvpPolicy.MaximumFrozenPromptUtf8Bytes,
            GalateaMemoRecallMvpPolicy.MaximumHydratedExactTextUtf8Bytes
        );
    }

    public async ValueTask<IReadOnlyList<PlayerTurnRecall>>
        SelectRecallsAsync(
        GalateaPlayerTurnRecallRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string query = GalateaMemoRecallQueryRenderer.Render(
            request.User.CharacterName,
            request.CurrentObservation,
            request.Context
        );
        ICompletionClient completionClient =
            _completionClientAccessor()
            ?? throw new InvalidOperationException(
                "The Memo recall Completion client accessor returned null."
            );
        MemoRecallResult result =
            await _reconciler.RecallSettledDefaultPodAsync(
                    completionClient,
                    _connection.ModelId,
                    query,
                    _options,
                    cancellationToken
                )
                .ConfigureAwait(false);

        return GalateaDefaultMemoPodRecallPlanner.Select(
            request,
            CharacterNoteDefaultPodV1.PodId,
            result.Memos
        );
    }
}

internal static class GalateaDefaultMemoPodRecallPlanner {
    internal static IReadOnlyList<PlayerTurnRecall> Select(
        GalateaPlayerTurnRecallRequest request,
        MemoPodId podId,
        IReadOnlyList<Memo> memos
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(memos);
        if (podId != CharacterNoteDefaultPodV1.PodId) {
            throw new InvalidDataException(
                "Galatea Memo recall only accepts the Character Note Default Pod."
            );
        }

        _ = PlayerTurnObservationEnvelope.Wrap(
            request.CurrentObservation
        );
        foreach (Memo memo in memos) {
            ArgumentNullException.ThrowIfNull(memo);
            if (memo.Title is null) { continue; }

            string sourceId = GalateaMemoRecallSourceIdCodec.Format(
                podId,
                memo.Id
            );
            var entry = new RecallEntry(
                RecallType.MemoExactText,
                sourceId
            );
            if (request.Context.CharacterNoteOriginBarrier.Contains(
                    podId,
                    memo.Id)) {
                continue;
            }
            if (request.Context.RecallBarrier.Contains(entry)) {
                continue;
            }

            string body = GalateaMemoExactTextBodyRenderer.Render(
                memo.Title,
                memo.ExactText
            );
            var recall = new PlayerTurnRecall(entry, body);
            var finalObservation = new PlayerTurnObservation(
                request.CurrentObservation.PlayerText,
                request.CurrentObservation.ExternalLocalTimestamp!.Value,
                request.CurrentObservation.Notices,
                [recall]
            );
            try {
                _ = PlayerTurnObservationEnvelope.Wrap(finalObservation);
            }
            catch (ArgumentOutOfRangeException exception) when (
                string.Equals(
                    exception.ParamName,
                    "rendered",
                    StringComparison.Ordinal
                )) {
                continue;
            }
            return Array.AsReadOnly([recall]);
        }
        return Array.AsReadOnly(Array.Empty<PlayerTurnRecall>());
    }
}
