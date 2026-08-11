using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    public RecapGridBuildProgressResult InspectBuildProgress(
        RecapGridBuildRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        using ManagerLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridBuildProgressResult.Disposed();
        }
        var state = new BuildState(request.Budget, _timeProvider);
        int examinedAssignments = 0;
        int missingAssignments = 0;
        RecapGridBuildProgressResult Finish(
            RecapGridBuildProgressResult result
        ) => result with {
            Metrics = new RecapGridBuildProgressMetrics(
                state.SelectedRows,
                state.RecipeRowSteps,
                examinedAssignments,
                missingAssignments
            )
        };

        FreezeAttempt frozenAttempt = FreezeOperation(
            request,
            state,
            cancellationToken
        );
        if (frozenAttempt.Error is { } freezeError) {
            return Finish(MapProgressError(freezeError));
        }
        FrozenOperation frozen = frozenAttempt.Value!;
        HistoryTimelineSelectedRow through = frozen.RootToThrough[^1];
        var authority = new RecapGridBuildProgressAuthority(
            frozen.TimelineHead,
            frozen.ControlSnapshot.Head,
            frozen.StoreIdentity,
            frozen.RequestedRecipe.Recipe.Digest,
            through.Descriptor.RowId,
            through.Descriptor.DescriptorDigest
        );
        var previous = new Dictionary<GridBuildRecipeDigest, BuiltRow>();
        BuiltRow? requestedFinal = null;
        for (int rowIndex = 0;
             rowIndex < frozen.RootToThrough.Count;
             rowIndex++) {
            HistoryTimelineSelectedRow selected =
                frozen.RootToThrough[rowIndex];
            if (cancellationToken.IsCancellationRequested) {
                return Finish(new RecapGridBuildProgressResult.Cancelled());
            }
            if (state.HasElapsed()) {
                return Finish(new RecapGridBuildProgressResult
                    .BudgetExceeded(
                        RecapGridBuildBudgetKind.Elapsed,
                        selected.Descriptor.RowId
                    ));
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
                    return Finish(new RecapGridBuildProgressResult.Invalid(
                        "BaseSameRowUnavailable",
                        "The row-major base recipe result is unavailable."
                    ));
                }
                state.RecipeRowSteps++;
                (DerivedRowPlan? derived,
                    RecapGridBuildResult? deriveError) = DeriveRowPlan(
                        frozen,
                        plan,
                        selected,
                        rowIndex,
                        previousRow,
                        baseRow
                    );
                if (deriveError is not null) {
                    return Finish(MapProgressError(deriveError));
                }
                RowBuildSpec spec = derived!.Spec;
                examinedAssignments = checked(
                    examinedAssignments + spec.OrderedAssignments.Count
                );
                RecapGridMissingResult missingRead =
                    _store.Reader.FindMissingAssignments(spec);
                if (missingRead
                    is RecapGridMissingResult.PrerequisiteMissing missing) {
                    RecapGridBuildProgressResult? fence =
                        MapProgressFence(CheckFinalFences(frozen));
                    return Finish(fence
                        ?? new RecapGridBuildProgressResult.Blocked(
                            authority,
                            selected.Descriptor.RowId,
                            plan.Recipe.Digest,
                            "ReusePrerequisiteMissing",
                            $"{missing.LogicalColumnId}:{missing.CellDigest}"
                        ));
                }
                if (missingRead is RecapGridMissingResult.Busy) {
                    return Finish(new RecapGridBuildProgressResult.Unavailable(
                        RecapGridBuildDependency.Store,
                        "StoreBusy",
                        "The Store is busy."
                    ));
                }
                if (missingRead is RecapGridMissingResult.Disposed) {
                    return Finish(new RecapGridBuildProgressResult.Unavailable(
                        RecapGridBuildDependency.Store,
                        "StoreDisposed",
                        "The Store handle has been disposed."
                    ));
                }
                if (missingRead is RecapGridMissingResult.Invalid invalid) {
                    return Finish(new RecapGridBuildProgressResult.Unavailable(
                        RecapGridBuildDependency.Store,
                        invalid.Code,
                        invalid.Detail
                    ));
                }
                EvaluationKey[] missingKeys;
                if (missingRead is RecapGridMissingResult.Complete) {
                    missingKeys = [];
                }
                else if (missingRead is RecapGridMissingResult.Missing value) {
                    missingKeys = value.OrderedKeys.ToArray();
                }
                else {
                    return Finish(new RecapGridBuildProgressResult.Invalid(
                        "ProgressMissingReadOutcomeInvalid",
                        "The Store returned an unknown missing result."
                    ));
                }
                (FrozenRecapCellWork[]? work,
                    RecapGridBuildResult? workError) = CreateMissingWork(
                        plan,
                        spec,
                        missingKeys
                    );
                if (workError is not null) {
                    return Finish(MapProgressError(workError));
                }
                if (work!.Length > 0) {
                    missingAssignments = work.Length;
                    if (state.NewCalls + work.Length
                            > request.Budget.MaximumNewCalls
                        || work.Length
                            > RecapGridBuildProgressLimits
                                .MaximumFrontierAssignments) {
                        return Finish(new RecapGridBuildProgressResult
                            .BudgetExceeded(
                                RecapGridBuildBudgetKind.NewCalls,
                                selected.Descriptor.RowId
                            ));
                    }
                    RecapGridBuildProgressResult? fence =
                        MapProgressFence(CheckFinalFences(frozen));
                    if (fence is not null) {
                        return Finish(fence);
                    }
                    return Finish(new RecapGridBuildProgressResult.Frontier(
                        authority,
                        selected.Descriptor.RowId,
                        plan.Recipe.Digest,
                        Array.AsReadOnly(work.Select(item =>
                            new RecapGridMissingAssignmentProgress(
                                item.Ordinal,
                                selected.Descriptor.RowId,
                                plan.Recipe.Digest,
                                item.LogicalColumnId,
                                item.EvaluationKey.Digest
                            )).ToArray())
                    ));
                }
                (RecapCellArtifact[]? cells,
                    RecapGridBuildResult? cellsError) =
                    ResolveSelectedCells(
                        plan,
                        spec,
                        new Dictionary<EvaluationKeyDigest,
                            RecapCellArtifact>(),
                        derived.PreviousCells
                    );
                if (cellsError is not null) {
                    return Finish(MapProgressError(cellsError));
                }
                RecapRowView view;
                try {
                    view = RecapRowView.Create(spec, cells!);
                }
                catch (Exception exception) when (
                    IsContractFailure(exception)) {
                    return Finish(new RecapGridBuildProgressResult.Invalid(
                        "ProgressRowViewInvalid",
                        exception.Message
                    ));
                }
                var built = new BuiltRow(view, cells!);
                current.Add(plan.Recipe.Digest, built);
                if (plan.Recipe.Digest
                    == frozen.RequestedRecipe.Recipe.Digest) {
                    requestedFinal = built;
                }
            }
            foreach ((GridBuildRecipeDigest digest, BuiltRow built)
                     in current) {
                previous[digest] = built;
            }
        }
        if (requestedFinal is null) {
            return Finish(new RecapGridBuildProgressResult.Invalid(
                "ProgressRequestedViewUnavailable",
                "The requested recipe has no derivable through-row view."
            ));
        }
        if (_store.Identity != frozen.StoreIdentity) {
            return Finish(new RecapGridBuildProgressResult.Invalid(
                "StoreIdentityChanged",
                "The Store identity changed during progress inspection."
            ));
        }
        FulfilledViewKey key;
        try {
            key = FulfilledViewKey.Create(
                frozen.TimelineHead.RefId,
                frozen.TimelineHead,
                through.Descriptor.DescriptorDigest,
                frozen.RequestedRecipe.Recipe
            );
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return Finish(new RecapGridBuildProgressResult.Invalid(
                "ProgressFulfilledKeyInvalid",
                exception.Message
            ));
        }
        bool fulfillmentPresent;
        switch (_store.Reader.ReadFulfilled(key)) {
            case RecapGridStoreReadResult<RecapGridFulfilledView>.Found found
                when found.Value.ViewDigest == requestedFinal.View.Digest:
                fulfillmentPresent = true;
                break;
            case RecapGridStoreReadResult<RecapGridFulfilledView>.Found:
                return Finish(new RecapGridBuildProgressResult.Invalid(
                    "ProgressFulfillmentMismatch",
                    "The exact fulfilled key points to another row view."
                ));
            case RecapGridStoreReadResult<RecapGridFulfilledView>.Missing:
                fulfillmentPresent = false;
                break;
            case RecapGridStoreReadResult<RecapGridFulfilledView>.Busy:
                return Finish(new RecapGridBuildProgressResult.Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreBusy",
                    "The Store is busy."
                ));
            case RecapGridStoreReadResult<RecapGridFulfilledView>.Disposed:
                return Finish(new RecapGridBuildProgressResult.Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreDisposed",
                    "The Store handle has been disposed."
                ));
            case RecapGridStoreReadResult<RecapGridFulfilledView>.Invalid invalid:
                return Finish(new RecapGridBuildProgressResult.Unavailable(
                    RecapGridBuildDependency.Store,
                    invalid.Code,
                    invalid.Detail
                ));
            default:
                return Finish(new RecapGridBuildProgressResult.Invalid(
                    "ProgressFulfilledReadOutcomeInvalid",
                    "The Store returned an unknown fulfilled read outcome."
                ));
        }
        RecapGridBuildProgressResult? finalFence =
            MapProgressFence(CheckFinalFences(frozen));
        return Finish(finalFence
            ?? new RecapGridBuildProgressResult.Complete(
                authority,
                requestedFinal.View.Digest,
                fulfillmentPresent
            ));
    }

    private static RecapGridBuildProgressResult? MapProgressFence(
        RecapGridBuildResult? fence
    ) => fence is null ? null : MapProgressError(fence);

    private static RecapGridBuildProgressResult MapProgressError(
        RecapGridBuildResult error
    ) => error switch {
        RecapGridBuildResult.NoRows value
            => new RecapGridBuildProgressResult.NoRows(
                value.TimelineHead,
                value.RecipeDigest
            ),
        RecapGridBuildResult.NoActiveRecipe
            => new RecapGridBuildProgressResult.NoActiveRecipe(),
        RecapGridBuildResult.RecipeAbsent value
            => new RecapGridBuildProgressResult.RecipeAbsent(
                value.RecipeDigest
            ),
        RecapGridBuildResult.ThroughRowNotSelected value
            => new RecapGridBuildProgressResult.ThroughRowNotSelected(
                value.RowId
            ),
        RecapGridBuildResult.BudgetExceeded value
            => new RecapGridBuildProgressResult.BudgetExceeded(
                value.Kind,
                value.AtRow
            ),
        RecapGridBuildResult.Cancelled
            => new RecapGridBuildProgressResult.Cancelled(),
        RecapGridBuildResult.Unavailable value
            => new RecapGridBuildProgressResult.Unavailable(
                value.Dependency,
                value.Code,
                value.Detail
            ),
        RecapGridBuildResult.StaleTimelineHead value
            => new RecapGridBuildProgressResult.StaleTimelineHead(
                value.Actual
            ),
        RecapGridBuildResult.StaleControlAuthority value
            => new RecapGridBuildProgressResult.StaleControlAuthority(
                value.Actual
            ),
        RecapGridBuildResult.Disposed
            => new RecapGridBuildProgressResult.Disposed(),
        RecapGridBuildResult.Invalid value
            => new RecapGridBuildProgressResult.Invalid(
                value.Code,
                value.Detail
            ),
        _ => new RecapGridBuildProgressResult.Invalid(
            "ProgressBuildOutcomeInvalid",
            "A write-only build outcome reached progress inspection."
        )
    };
}
