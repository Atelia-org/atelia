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
        var previous = new Dictionary<GridBuildRecipeDigest, BuiltRow>();
        BuiltRow? requestedFinal = null;
        for (int rowIndex = 0;
             rowIndex < frozen.RootToThrough.Count;
             rowIndex++) {
            HistoryTimelineSelectedRow selected =
                frozen.RootToThrough[rowIndex];
            if (state.HasElapsed()) {
                return new RecapGridBuildResult.BudgetExceeded(
                    RecapGridBuildBudgetKind.Elapsed,
                    selected.Descriptor.RowId
                );
            }
            if (cancellationToken.IsCancellationRequested) {
                return new RecapGridBuildResult.Cancelled();
            }
            (HistorySegmentContent? content,
                RecapGridBuildResult? contentError) = OpenHistorySegment(
                frozen,
                selected,
                state,
                cancellationToken
            );
            if (contentError is not null) {
                return contentError;
            }
            var current = new Dictionary<GridBuildRecipeDigest, BuiltRow>();
            foreach (FrozenRecipePlan plan in frozen.BaseToCandidate) {
                if (rowIndex > plan.RequiredThroughIndex) {
                    continue;
                }
                previous.TryGetValue(
                    plan.Recipe.Digest,
                    out BuiltRow? previousRow
                );
                BuiltRow? baseRow = null;
                if (plan.Recipe.BaseRecipeDigest is { } baseDigest
                    && rowIndex <= plan.BootstrapIndex
                    && !current.TryGetValue(baseDigest, out baseRow)) {
                    return Invalid(
                        "BaseSameRowUnavailable",
                        "The row-major base recipe result is unavailable."
                    );
                }
                state.RecipeRowSteps++;
                RowAttempt attempt = await BuildRecipeRowAsync(
                    frozen,
                    plan,
                    selected,
                    content!,
                    rowIndex,
                    previousRow,
                    baseRow,
                    executor,
                    state,
                    cancellationToken
                ).ConfigureAwait(false);
                if (attempt.Error is { } error) {
                    return error;
                }
                current.Add(plan.Recipe.Digest, attempt.Value!);
                if (plan.Recipe.Digest
                    == frozen.RequestedRecipe.Recipe.Digest) {
                    requestedFinal = attempt.Value;
                }
            }
            foreach ((GridBuildRecipeDigest digest, BuiltRow built)
                     in current) {
                previous[digest] = built;
            }
            if (cancellationToken.IsCancellationRequested) {
                return new RecapGridBuildResult.Cancelled();
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
        HistorySegmentContent content,
        int rowIndex,
        BuiltRow? previousRow,
        BuiltRow? baseRow,
        IRecapCellBatchExecutor executor,
        BuildState state,
        CancellationToken cancellationToken
    ) {
        HistorySegmentDescriptor descriptor = selected.Descriptor;
        if ((rowIndex == 0) != (previousRow is null)) {
            return RowError(Invalid(
                "PreviousCandidateViewMismatch",
                "Candidate row provenance does not match Timeline order."
            ));
        }
        PriorInputProjection? projection = null;
        PriorInputReference prior = PriorInputReference.FirstRow.Value;
        RowViewDigest? previousDigest = null;
        IReadOnlyList<RecapCellArtifact> previousCells = [];
        if (previousRow is not null) {
            RecapGridBuildResult? previousError = ValidateBuiltRow(
                previousRow,
                plan,
                frozen.RootToThrough[rowIndex - 1].Descriptor
            );
            if (previousError is not null) {
                return RowError(previousError);
            }
            previousCells = previousRow.Cells;
            projection = PriorInputProjection.Create(
                previousCells.Select(cell => new PriorProjectedContent(
                    cell.LogicalColumnId,
                    cell.ContentDigest
                ))
            );
            prior = new PriorInputReference.Projection(projection.Digest);
            previousDigest = previousRow.View.Digest;
        }

        RowBuildAssignment[] assignments;
        try {
            assignments = DeriveAssignments(
                plan,
                descriptor,
                rowIndex,
                prior,
                baseRow
            );
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return RowError(Invalid(
                "RowBuildSpecDerivationInvalid",
                exception.Message
            ));
        }
        RowBuildSpec spec;
        try {
            spec = plan.Recipe.Kind switch {
                GridBuildRecipeKind.Full => RowBuildSpec.CreateFull(
                    plan.Recipe,
                    descriptor.RowId,
                    descriptor.DescriptorDigest,
                    previousDigest,
                    prior,
                    assignments
                ),
                GridBuildRecipeKind.Overlay
                    when rowIndex <= plan.BootstrapIndex
                    => RowBuildSpec.CreateOverlayBootstrap(
                        plan.Recipe,
                        descriptor.RowId,
                        descriptor.DescriptorDigest,
                        previousDigest,
                        prior,
                        assignments
                    ),
                GridBuildRecipeKind.Overlay
                    => RowBuildSpec.CreateNormal(
                        plan.Recipe,
                        descriptor.RowId,
                        descriptor.DescriptorDigest,
                        previousDigest,
                        prior,
                        assignments
                    ),
                _ => throw new InvalidOperationException(
                    "The recipe kind is unsupported."
                )
            };
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return RowError(Invalid(
                "RowBuildSpecInvalid",
                exception.Message
            ));
        }

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
            RecapGridBuildResult? fence = CheckTimelineFence(
                frozen.TimelineHead
            ) ?? CheckControlFence(frozen);
            if (fence is not null) {
                return RowError(fence);
            }
            var batch = new FrozenRowBatch(
                frozen.TimelineHead,
                frozen.ControlSnapshot.Head,
                frozen.StoreIdentity,
                plan.Recipe,
                content,
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

}
