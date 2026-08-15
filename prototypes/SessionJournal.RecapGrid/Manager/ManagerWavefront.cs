using System.Text;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private sealed record BuiltRow(
        RecapRowView View,
        IReadOnlyList<RecapCellArtifact> Cells
    );

    private sealed record RowAttempt(
        BuiltRow? Value,
        RecapGridBuildResult? Error
    );

    private async ValueTask<RecapGridBuildResult> RunWavefrontAsync(
        RecapGridBuildRequest request,
        IRecapCellBatchExecutor executor,
        CancellationToken cancellationToken
    ) {
        var state = new BuildState(request.Budget, _timeProvider);
        RecapGridBuildResult result = await RunWavefrontCoreAsync(
            request,
            executor,
            state,
            cancellationToken
        ).ConfigureAwait(false);
        return result with { Metrics = state.Metrics() };
    }

    private async ValueTask<RecapGridBuildResult> RunWavefrontCoreAsync(
        RecapGridBuildRequest request,
        IRecapCellBatchExecutor executor,
        BuildState state,
        CancellationToken cancellationToken
    ) {
        FreezeAttempt frozenAttempt = FreezeOperation(
            request,
            state,
            cancellationToken
        );
        if (frozenAttempt.Error is { } freezeError) {
            return freezeError;
        }
        FrozenOperation frozen = frozenAttempt.Value!;
        ProgressionAttempt progressionAttempt = DiscoverProgression(
            frozen,
            state,
            cancellationToken
        );
        if (progressionAttempt.Error is { } progressionError) {
            return progressionError;
        }
        FrozenProgression progression = progressionAttempt.Value!;
        var previous = progression.Anchors.ToDictionary();
        BuiltRow? requestedFinal = progression.RequestedExact;
        HistoryRowId? currentRowId = null;
        HistorySegmentContent? content = null;
        var current = new Dictionary<GridBuildRecipeDigest, BuiltRow>();
        foreach (FrozenRecipeRowUnit unit in progression.OrderedUnits) {
            HistoryTimelineSelectedRow selected = unit.Selected;
            FrozenRecipePlan plan = unit.Plan;
            if (state.HasElapsed()) {
                return new RecapGridBuildResult.BudgetExceeded(
                    RecapGridBuildBudgetKind.Elapsed,
                    selected.Descriptor.RowId
                );
            }
            if (cancellationToken.IsCancellationRequested) {
                return new RecapGridBuildResult.Cancelled();
            }
            if (state.RecipeRowSteps
                >= state.Budget.MaximumRecipeRowSteps) {
                return new RecapGridBuildResult.BudgetExceeded(
                    RecapGridBuildBudgetKind.RecipeRowSteps,
                    selected.Descriptor.RowId
                );
            }
            if (currentRowId != selected.Descriptor.RowId) {
                foreach ((GridBuildRecipeDigest digest, BuiltRow built)
                         in current) {
                    previous[digest] = built;
                }
                current.Clear();
                currentRowId = selected.Descriptor.RowId;
                content = null;
            }
            (HistorySegmentContent?, RecapGridBuildResult?) OpenContent() {
                if (content is not null) {
                    return (content, null);
                }
                (HistorySegmentContent? opened,
                    RecapGridBuildResult? error) = OpenHistorySegment(
                        frozen,
                        selected,
                        state,
                        cancellationToken
                    );
                if (error is null) {
                    content = opened;
                }
                return (opened, error);
            }
            previous.TryGetValue(
                plan.Recipe.Digest,
                out BuiltRow? previousRow
            );
            BuiltRow? baseRow = null;
            if (unit.IsOverlayBootstrap
                && plan.Recipe.BaseRecipeDigest is { } baseDigest
                && !current.TryGetValue(baseDigest, out baseRow)) {
                FrozenRecipePlan basePlan = frozen.BaseToCandidate.Single(
                    value => value.Recipe.Digest == baseDigest
                );
                (baseRow, RecapGridBuildResult? baseError) =
                    ReadAssignedView(basePlan, selected);
                if (baseError is not null) {
                    return baseError;
                }
            }
            RowAttempt attempt = await BuildRecipeRowAsync(
                frozen,
                plan,
                selected,
                OpenContent,
                unit.IsOverlayBootstrap,
                previousRow,
                baseRow,
                executor,
                state,
                cancellationToken
            ).ConfigureAwait(false);
            if (attempt.Error is { } error) {
                return error;
            }
            state.RecipeRowSteps++;
            current.Add(plan.Recipe.Digest, attempt.Value!);
            if (plan.Recipe.Digest
                == frozen.RequestedRecipe.Recipe.Digest) {
                requestedFinal = attempt.Value;
            }
        }
        if (requestedFinal is null) {
            return Invalid(
                "RequestedRecipeViewUnavailable",
                "The requested recipe did not produce its through-row view."
            );
        }
        return FinalizeFulfilled(frozen, requestedFinal, state);
    }

    private async ValueTask<RowAttempt> BuildRecipeRowAsync(
        FrozenOperation frozen,
        FrozenRecipePlan plan,
        HistoryTimelineSelectedRow selected,
        Func<(HistorySegmentContent?, RecapGridBuildResult?)> openContent,
        bool isOverlayBootstrap,
        BuiltRow? previousRow,
        BuiltRow? baseRow,
        IRecapCellBatchExecutor executor,
        BuildState state,
        CancellationToken cancellationToken
    ) {
        HistorySegmentDescriptor descriptor = selected.Descriptor;
        (DerivedRowPlan? derived,
            RecapGridBuildResult? deriveError) = DeriveRowPlan(
                frozen,
                plan,
                selected,
                isOverlayBootstrap,
                previousRow,
                baseRow
            );
        if (deriveError is not null) {
            return RowError(deriveError);
        }
        RowBuildSpec spec = derived!.Spec;
        IReadOnlyList<RecapCellArtifact> previousCells =
            derived.PreviousCells;
        PriorInputProjection? projection = derived.Projection;

        RecapGridMissingResult missingRead =
            _store.Reader.FindMissingAssignments(spec);
        EvaluationKey[] missing;
        switch (missingRead) {
            case RecapGridMissingResult.Complete:
                missing = [];
                break;
            case RecapGridMissingResult.Missing found:
                missing = found.OrderedKeys.ToArray();
                break;
            case RecapGridMissingResult.PrerequisiteMissing prerequisite:
                return RowError(Unavailable(
                    RecapGridBuildDependency.Store,
                    "ReusePrerequisiteMissing",
                    $"{prerequisite.LogicalColumnId}:{prerequisite.CellDigest}"
                ));
            case RecapGridMissingResult.Busy:
                return RowError(Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreBusy"
                ));
            case RecapGridMissingResult.Disposed:
                return RowError(Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreDisposed"
                ));
            case RecapGridMissingResult.Invalid invalid:
                return RowError(Unavailable(
                    RecapGridBuildDependency.Store,
                    invalid.Code,
                    invalid.Detail
                ));
            default:
                return RowError(Invalid(
                    "MissingOutcomeInvalid",
                    "The Store returned an unknown missing outcome."
                ));
        }
        (FrozenRecapCellWork[]? work,
            RecapGridBuildResult? workError) = CreateMissingWork(
                plan,
                spec,
                missing
            );
        if (workError is not null) {
            return RowError(workError);
        }
        FrozenRecapCellWork[] orderedWork = work!;
        var settled = new Dictionary<EvaluationKeyDigest,
            RecapCellArtifact>();
        if (orderedWork.Length > 0) {
            if (state.NewCalls + orderedWork.Length
                > state.Budget.MaximumNewCalls) {
                return RowError(
                    new RecapGridBuildResult.BudgetExceeded(
                        RecapGridBuildBudgetKind.NewCalls,
                        descriptor.RowId
                    )
                );
            }
            if (state.HasElapsed()) {
                return RowError(
                    new RecapGridBuildResult.BudgetExceeded(
                        RecapGridBuildBudgetKind.Elapsed,
                        descriptor.RowId
                    )
                );
            }
            if (cancellationToken.IsCancellationRequested) {
                return RowError(new RecapGridBuildResult.Cancelled());
            }
            RecapGridBuildResult? captureError = EnsureRawCapture(
                frozen,
                state,
                cancellationToken
            );
            if (captureError is not null) {
                return RowError(captureError);
            }
            RecapGridBuildResult? fence = CheckTimelineFence(
                frozen.TimelineHead
            ) ?? CheckControlFence(frozen);
            if (fence is not null) {
                return RowError(fence);
            }
            (HistorySegmentContent? content,
                RecapGridBuildResult? contentError) = openContent();
            if (contentError is not null) {
                return RowError(contentError);
            }
            var batch = new FrozenRowBatch(
                frozen.TimelineHead,
                frozen.ControlSnapshot.Head,
                frozen.StoreIdentity,
                plan.Recipe,
                content!,
                spec,
                previousRow?.View,
                previousCells,
                projection,
                Array.AsReadOnly(orderedWork)
            );
            RecapCellBatchExecutionResult execution;
            try {
                execution = await executor.ExecuteAsync(
                    batch,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception)) {
                return RowError(new RecapGridBuildResult.ExecutorFailed(
                    exception.GetType().Name,
                    exception.Message
                ));
            }
            if (execution is RecapCellBatchExecutionResult
                .RejectedBeforeDispatch rejected) {
                return RowError(
                    new RecapGridBuildResult.ExecutorRejected(
                        rejected.Code,
                        rejected.Detail
                    )
                );
            }
            if (execution is not RecapCellBatchExecutionResult.Completed
                completed) {
                return RowError(new RecapGridBuildResult.ExecutorFailed(
                    "ExecutorOutcomeInvalid",
                    "The executor returned an unsupported top-level outcome."
                ));
            }
            RecapCellExecutionOutcome[] outcomes =
                completed.OrderedOutcomes?.ToArray()
                ?? [];
            if (outcomes.Length != orderedWork.Length) {
                return RowError(new RecapGridBuildResult.ExecutorFailed(
                    "ExecutorOutcomeCoverageInvalid",
                    "The executor outcome set does not exactly cover the work batch."
                ));
            }
            for (int index = 0; index < outcomes.Length; index++) {
                RecapCellExecutionOutcome? outcome = outcomes[index];
                if (outcome is null
                    || outcome.EvaluationKey
                        != orderedWork[index].EvaluationKey.Digest) {
                    return RowError(
                        new RecapGridBuildResult.ExecutorFailed(
                            "ExecutorOutcomeOrderInvalid",
                            "Executor outcomes must match work order and exact keys."
                        )
                    );
                }
                if (outcome is not (
                    RecapCellExecutionOutcome.Updated
                    or RecapCellExecutionOutcome.KeepUnchanged
                    or RecapCellExecutionOutcome.Failed
                    or RecapCellExecutionOutcome
                        .NotStartedDueToCallerCancellation)) {
                    return RowError(
                        new RecapGridBuildResult.ExecutorFailed(
                            "ExecutorOutcomeInvalid",
                            "The executor returned an unsupported work outcome."
                        )
                    );
                }
            }
            state.NewCalls += outcomes.Count(static outcome =>
                outcome is not RecapCellExecutionOutcome
                    .NotStartedDueToCallerCancellation
            );
            if (!cancellationToken.IsCancellationRequested
                && outcomes.Any(static outcome => outcome is
                    RecapCellExecutionOutcome
                        .NotStartedDueToCallerCancellation)) {
                return RowError(
                    new RecapGridBuildResult.ExecutorFailed(
                        "ExecutorCancellationContractInvalid",
                        "NotStartedDueToCallerCancellation requires the caller token to be cancelled."
                    )
                );
            }
            var failures = new List<RecapGridCellFailure>();
            var localErrors = new List<(int Ordinal,
                RecapGridBuildResult Error)>();
            for (int index = 0; index < outcomes.Length; index++) {
                FrozenRecapCellWork item = orderedWork[index];
                RecapCellExecutionOutcome outcome = outcomes[index];
                if (outcome is RecapCellExecutionOutcome.Failed failed) {
                    failures.Add(new RecapGridCellFailure(
                        item.Ordinal,
                        outcome.EvaluationKey,
                        failed.Code,
                        failed.Detail,
                        NotStarted: false
                    ));
                    continue;
                }
                if (outcome is RecapCellExecutionOutcome
                    .NotStartedDueToCallerCancellation) {
                    failures.Add(new RecapGridCellFailure(
                        item.Ordinal,
                        outcome.EvaluationKey,
                        "CallerCancellation",
                        "The work item was not started due to caller cancellation.",
                        NotStarted: true
                    ));
                    continue;
                }
                (RecapCellArtifact? proposed,
                    RecapGridBuildResult? proposalError) = CreateCell(
                        item,
                        outcome,
                        previousCells
                );
                if (proposalError is not null) {
                    localErrors.Add((item.Ordinal, proposalError));
                    continue;
                }
                (RecapCellArtifact? winner,
                    RecapGridBuildResult? putError) = PutCell(
                        frozen,
                        item,
                        proposed!,
                        previousCells,
                        state
                );
                if (putError is not null) {
                    localErrors.Add((item.Ordinal, putError));
                    continue;
                }
                settled.Add(item.EvaluationKey.Digest, winner!);
            }
            RecapGridBuildResult? settlement = localErrors
                .OrderBy(static error => error.Ordinal)
                .Select(static error => error.Error)
                .FirstOrDefault(static error => error is
                    RecapGridBuildResult.SettlementRequired);
            if (settlement is not null) {
                return RowError(settlement);
            }
            failures.Sort(static (left, right) =>
                left.Ordinal.CompareTo(right.Ordinal));
            int localOrdinal = localErrors.Count == 0
                ? int.MaxValue
                : localErrors.Min(static error => error.Ordinal);
            int failureOrdinal = failures.Count == 0
                ? int.MaxValue
                : failures[0].Ordinal;
            if (localOrdinal < failureOrdinal) {
                return RowError(localErrors
                    .OrderBy(static error => error.Ordinal)
                    .First().Error);
            }
            if (failures.Count > 0) {
                return RowError(cancellationToken.IsCancellationRequested
                    && failures.All(static failure => failure.NotStarted)
                        ? new RecapGridBuildResult.Cancelled()
                        : new RecapGridBuildResult.Incomplete(
                            descriptor.RowId,
                            failures.AsReadOnly()
                        ));
            }
            if (localErrors.Count > 0) {
                return RowError(localErrors
                    .OrderBy(static error => error.Ordinal)
                    .First().Error);
            }
        }

        (RecapCellArtifact[]? selectedCells,
            RecapGridBuildResult? cellsError) = ResolveSelectedCells(
                plan,
                spec,
                settled,
                previousCells
            );
        if (cellsError is not null) {
            return RowError(cellsError);
        }
        RecapGridBuildResult? publishFence = CheckFinalFences(frozen);
        if (publishFence is not null) {
            return RowError(publishFence);
        }
        RecapRowView view;
        try {
            view = RecapRowView.Create(spec, selectedCells!);
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return RowError(Invalid(
                "RowViewCreationInvalid",
                exception.Message
            ));
        }
        RecapGridBuildResult? viewPut = PutRowView(
            frozen,
            spec,
            view,
            state
        );
        return viewPut is null
            ? new RowAttempt(
                new BuiltRow(view, Array.AsReadOnly(selectedCells!)),
                null
            )
            : RowError(viewPut);
    }

    private RecapGridBuildResult? EnsureRawCapture(
        FrozenOperation frozen,
        BuildState state,
        CancellationToken cancellationToken
    ) {
        if (state.RawCapture is not null) {
            return null;
        }
        OnlineSelectedRawCaptureResult capture;
        try {
            _testHooks.BeforeCaptureRaw?.Invoke();
            capture = _timeline.CaptureRaw(
                frozen.TimelineHead,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            return new RecapGridBuildResult.Cancelled();
        }
        if (capture is not OnlineSelectedRawCaptureResult.Captured
            captured) {
            return MapRawCapture(capture);
        }
        if (captured.Capture.CapturedHead != frozen.FrozenRawHead) {
            return Unavailable(
                RecapGridBuildDependency.RawHistory,
                "RawHeadChanged",
                "The selected raw head changed before build materialization."
            );
        }
        RecapGridBuildResult? fence = CheckTimelineFence(
            frozen.TimelineHead
        );
        if (fence is null) {
            state.RawCapture = captured.Capture;
        }
        return fence;
    }

}
