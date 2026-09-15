using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.MemoPod;
using System.Runtime.ExceptionServices;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed class GalateaDefaultMemoPodRecallProvider
    : IGalateaPlayerTurnRecallPlanningProvider {
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
        try {
            GalateaMemoRecallPlanningResult result = await PlanRecallsAsync(
                    request,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return result.Recalls;
        }
        catch (GalateaMemoRecallStageException exception) {
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
    }

    public async ValueTask<GalateaMemoRecallPlanningResult>
        PlanRecallsAsync(
        GalateaPlayerTurnRecallRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string query;
        try {
            query = GalateaMemoRecallQueryRenderer.Render(
                request.Character.CharacterName,
                request.CurrentObservation,
                request.Context,
                request.CurrentInput
            );
        }
        catch (Exception exception) when (ShouldClassify(exception)) {
            throw Classified(
                GalateaMemoRecallFailureStage.QueryRendering,
                exception
            );
        }

        ICompletionClient completionClient;
        try {
            completionClient = _completionClientAccessor()
                ?? throw new InvalidOperationException(
                    "The Memo recall Completion client accessor returned null."
                );
        }
        catch (Exception exception) when (ShouldClassify(exception)) {
            throw Classified(
                GalateaMemoRecallFailureStage.ClientResolution,
                exception
            );
        }

        GalateaSettledMemoRecallResult result;
        try {
            result = await _reconciler.RecallSettledDefaultPodAsync(
                    completionClient,
                    _connection.ModelId,
                    query,
                    _options,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (ShouldClassify(exception)) {
            throw Classified(
                GalateaMemoRecallFailureStage.SelectorExecution,
                exception
            );
        }

        try {
            return GalateaDefaultMemoPodRecallPlanner.Plan(
                request,
                CharacterNoteDefaultPodV1.PodId,
                result.Memos,
                result.PodStateIdentity
            );
        }
        catch (Exception exception) when (ShouldClassify(exception)) {
            throw Classified(
                GalateaMemoRecallFailureStage.PlannerEvaluation,
                exception
            );
        }
    }

    private static bool ShouldClassify(Exception exception) =>
        exception is not OperationCanceledException
        && GalateaExceptionClassifier.IsNonFatal(exception);

    private static GalateaMemoRecallStageException Classified(
        GalateaMemoRecallFailureStage stage,
        Exception exception
    ) => new(
        stage,
        GalateaMemoRecallFailureClassifier.Classify(exception),
        exception
    );
}

internal enum GalateaMemoRecallPlanningOutcome {
    NoMatch,
    AllFiltered,
    Selected,
}

internal sealed class GalateaMemoRecallPlanningResult {
    internal GalateaMemoRecallPlanningResult(
        GalateaMemoRecallPlanningOutcome outcome,
        IReadOnlyList<PlayerTurnRecall> recalls,
        int nominatedCount,
        int evaluatedCount,
        int missingTitleSkipCount,
        int originVisibleSkipCount,
        int priorRecallSkipCount,
        int observationBudgetSkipCount
    ) {
        ArgumentNullException.ThrowIfNull(recalls);
        int[] counts = [
            nominatedCount,
            evaluatedCount,
            missingTitleSkipCount,
            originVisibleSkipCount,
            priorRecallSkipCount,
            observationBudgetSkipCount,
        ];
        if (counts.Any(static count => count < 0)) {
            throw new ArgumentOutOfRangeException(
                nameof(nominatedCount),
                "Memo recall planning counts must be non-negative."
            );
        }
        if (recalls.Count > 1) {
            throw new ArgumentOutOfRangeException(
                nameof(recalls),
                "Memo recall planning must select zero or one recall."
            );
        }
        int selectedCount = recalls.Count;
        int skipCount = checked(
            missingTitleSkipCount
                + originVisibleSkipCount
                + priorRecallSkipCount
                + observationBudgetSkipCount
        );
        if (evaluatedCount != checked(skipCount + selectedCount)) {
            throw new ArgumentException(
                "Evaluated Memo recall candidates must equal first-hit skips plus selections.",
                nameof(evaluatedCount)
            );
        }
        if (evaluatedCount > nominatedCount) {
            throw new ArgumentException(
                "Evaluated Memo recall candidates cannot exceed nominations.",
                nameof(evaluatedCount)
            );
        }
        bool validOutcome = outcome switch {
            GalateaMemoRecallPlanningOutcome.NoMatch =>
                nominatedCount == 0 && selectedCount == 0,
            GalateaMemoRecallPlanningOutcome.AllFiltered =>
                nominatedCount > 0
                && evaluatedCount == nominatedCount
                && selectedCount == 0,
            GalateaMemoRecallPlanningOutcome.Selected => selectedCount == 1,
            _ => false,
        };
        if (!validOutcome) {
            throw new ArgumentException(
                "Memo recall planning outcome does not match its counts.",
                nameof(outcome)
            );
        }

        Outcome = outcome;
        Recalls = recalls;
        NominatedCount = nominatedCount;
        EvaluatedCount = evaluatedCount;
        SelectedCount = selectedCount;
        MissingTitleSkipCount = missingTitleSkipCount;
        OriginVisibleSkipCount = originVisibleSkipCount;
        PriorRecallSkipCount = priorRecallSkipCount;
        ObservationBudgetSkipCount = observationBudgetSkipCount;
        UnexaminedCount = nominatedCount - evaluatedCount;
    }

    internal GalateaMemoRecallPlanningOutcome Outcome { get; }
    internal IReadOnlyList<PlayerTurnRecall> Recalls { get; }
    internal int NominatedCount { get; }
    internal int EvaluatedCount { get; }
    internal int SelectedCount { get; }
    internal int MissingTitleSkipCount { get; }
    internal int OriginVisibleSkipCount { get; }
    internal int PriorRecallSkipCount { get; }
    internal int ObservationBudgetSkipCount { get; }
    internal int UnexaminedCount { get; }
}

internal static class GalateaDefaultMemoPodRecallPlanner {
    internal static IReadOnlyList<PlayerTurnRecall> Select(
        GalateaPlayerTurnRecallRequest request,
        MemoPodId podId,
        IReadOnlyList<Memo> memos,
        string podStateIdentity
    ) => Plan(request, podId, memos, podStateIdentity).Recalls;

    internal static GalateaMemoRecallPlanningResult Plan(
        GalateaPlayerTurnRecallRequest request,
        MemoPodId podId,
        IReadOnlyList<Memo> memos,
        string podStateIdentity
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(memos);
        if (podId != CharacterNoteDefaultPodV1.PodId) {
            throw new InvalidDataException(
                "Galatea Memo recall only accepts the Character Note Default Pod."
            );
        }

        GalateaInputContentValidation.RequireText(podStateIdentity, 256, nameof(podStateIdentity), singleLine: true);
        int evaluatedCount = 0;
        int missingTitleSkipCount = 0;
        int originVisibleSkipCount = 0;
        int priorRecallSkipCount = 0;
        int observationBudgetSkipCount = 0;
        foreach (Memo memo in memos) {
            evaluatedCount = checked(evaluatedCount + 1);
            ArgumentNullException.ThrowIfNull(memo);
            if (memo.Title is null) {
                missingTitleSkipCount = checked(
                    missingTitleSkipCount + 1
                );
                continue;
            }

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
                originVisibleSkipCount = checked(
                    originVisibleSkipCount + 1
                );
                continue;
            }
            if (request.Context.RecallBarrier.Contains(entry)) {
                priorRecallSkipCount = checked(
                    priorRecallSkipCount + 1
                );
                continue;
            }

            var recall = new PlayerTurnRecall(entry, podStateIdentity, memo.Title, memo.ExactText);
            PlayerTurnObservation finalObservation = request
                .CurrentObservation.WithRecalls([recall]);
            if (!(request.FitsRecalls?.Invoke([recall])
                    ?? GalateaObservationContent.FitsPlayerTurnContent(finalObservation))) {
                observationBudgetSkipCount = checked(
                    observationBudgetSkipCount + 1
                );
                continue;
            }
            return new GalateaMemoRecallPlanningResult(
                GalateaMemoRecallPlanningOutcome.Selected,
                Array.AsReadOnly([recall]),
                memos.Count,
                evaluatedCount,
                missingTitleSkipCount,
                originVisibleSkipCount,
                priorRecallSkipCount,
                observationBudgetSkipCount
            );
        }
        return new GalateaMemoRecallPlanningResult(
            memos.Count == 0
                ? GalateaMemoRecallPlanningOutcome.NoMatch
                : GalateaMemoRecallPlanningOutcome.AllFiltered,
            Array.AsReadOnly(Array.Empty<PlayerTurnRecall>()),
            memos.Count,
            evaluatedCount,
            missingTitleSkipCount,
            originVisibleSkipCount,
            priorRecallSkipCount,
            observationBudgetSkipCount
        );
    }
}
