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

        FreezeAttempt freeze = FreezeOperation(
            request,
            state,
            cancellationToken
        );
        if (freeze.Error is { } freezeError) {
            return Finish(MapProgressError(freezeError));
        }
        FrozenOperation frozen = freeze.Value!;
        var authority = new RecapGridBuildProgressAuthority(
            frozen.TimelineHead,
            frozen.ControlSnapshot.Head,
            frozen.StoreIdentity,
            frozen.RequestedRecipe.Recipe.Digest,
            frozen.Through.Descriptor.RowId,
            frozen.Through.Descriptor.DescriptorDigest
        );
        ProgressionAttempt discovery = DiscoverProgression(
            frozen,
            state,
            cancellationToken
        );
        if (discovery.Error is { } discoveryError) {
            return Finish(MapProgressError(discoveryError));
        }
        FrozenProgression progression = discovery.Value!;
        BuiltRow? requestedFinal = progression.RequestedExact;
        if (progression.OrderedUnits.Count > 0) {
            FrozenRecipeRowUnit unit = progression.OrderedUnits[0];
            _testHooks.AfterDiscoverProgression?.Invoke();
            if (state.HasElapsed()) {
                return Finish(new RecapGridBuildProgressResult.BudgetExceeded(
                    RecapGridBuildBudgetKind.Elapsed,
                    unit.Selected.Descriptor.RowId
                ));
            }
            if (cancellationToken.IsCancellationRequested) {
                return Finish(new RecapGridBuildProgressResult.Cancelled());
            }
            if (request.Budget.MaximumRecipeRowSteps == 0) {
                return Finish(new RecapGridBuildProgressResult.BudgetExceeded(
                    RecapGridBuildBudgetKind.RecipeRowSteps,
                    unit.Selected.Descriptor.RowId
                ));
            }
            progression.Anchors.TryGetValue(
                unit.Plan.Recipe.Digest,
                out BuiltRow? previous);
            BuiltRow? baseRow = null;
            if (unit.IsOverlayBootstrap
                && unit.Plan.Recipe.BaseRecipeDigest is { } baseDigest) {
                FrozenRecipePlan basePlan = frozen.BaseToCandidate.Single(
                    value => value.Recipe.Digest == baseDigest
                );
                (baseRow, RecapGridBuildResult? baseError) =
                    ReadAssignedView(basePlan, unit.Selected);
                if (baseError is not null) {
                    return Finish(MapProgressError(baseError));
                }
            }
            (DerivedRowPlan? derived, RecapGridBuildResult? deriveError) =
                DeriveRowPlan(
                    frozen,
                    unit.Plan,
                    unit.Selected,
                    unit.IsOverlayBootstrap,
                    previous,
                    baseRow
                );
            if (deriveError is not null) {
                return Finish(MapProgressError(deriveError));
            }
            examinedAssignments = checked(examinedAssignments
                + derived!.Spec.OrderedAssignments.Count);
            RecapGridMissingResult missing =
                _store.Reader.FindMissingAssignments(derived.Spec);
            if (missing is RecapGridMissingResult.PrerequisiteMissing absent) {
                RecapGridBuildProgressResult? blockedFence =
                    MapProgressFence(CheckFinalFences(frozen));
                return Finish(blockedFence
                    ?? new RecapGridBuildProgressResult.Blocked(
                    authority,
                    unit.Selected.Descriptor.RowId,
                    unit.Plan.Recipe.Digest,
                    "ReusePrerequisiteMissing",
                    $"{absent.LogicalColumnId}:{absent.CellDigest}"
                    ));
            }
            if (missing is RecapGridMissingResult.Busy) {
                return Finish(new RecapGridBuildProgressResult.Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreBusy",
                    "The Store is busy."
                ));
            }
            if (missing is RecapGridMissingResult.Disposed) {
                return Finish(new RecapGridBuildProgressResult.Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreDisposed",
                    "The Store handle has been disposed."
                ));
            }
            if (missing is RecapGridMissingResult.Invalid invalid) {
                return Finish(new RecapGridBuildProgressResult.Unavailable(
                    RecapGridBuildDependency.Store,
                    invalid.Code,
                    invalid.Detail
                ));
            }
            EvaluationKey[] keys = missing switch {
                RecapGridMissingResult.Missing value
                    => value.OrderedKeys.ToArray(),
                RecapGridMissingResult.Complete => [],
                _ => []
            };
            (FrozenRecapCellWork[]? work, RecapGridBuildResult? workError) =
                CreateMissingWork(unit.Plan, derived.Spec, keys);
            if (workError is not null) {
                return Finish(MapProgressError(workError));
            }
            missingAssignments = work!.Length;
            if (work.Length > request.Budget.MaximumNewCalls) {
                return Finish(new RecapGridBuildProgressResult.BudgetExceeded(
                    RecapGridBuildBudgetKind.NewCalls,
                    unit.Selected.Descriptor.RowId
                ));
            }
            RecapGridBuildProgressResult? fence =
                MapProgressFence(CheckFinalFences(frozen));
            return Finish(fence
                ?? new RecapGridBuildProgressResult.Frontier(
                    authority,
                    progression.Anchors.TryGetValue(
                        unit.Plan.Recipe.Digest,
                        out BuiltRow? anchor)
                            ? anchor.View.HistoryRowId
                            : null,
                    new RecapGridRecipeRowWork(
                        unit.Selected.Descriptor.RowId,
                        unit.Plan.Recipe.Digest,
                        unit.IsOverlayBootstrap
                    ),
                    progression.OrderedUnits.Count,
                    Array.AsReadOnly(work.Select(item =>
                        new RecapGridMissingAssignmentProgress(
                            item.Ordinal,
                            unit.Selected.Descriptor.RowId,
                            unit.Plan.Recipe.Digest,
                            item.LogicalColumnId,
                            item.EvaluationKey.Digest
                        )).ToArray())
                    ));
        }

        if (requestedFinal is null) {
            return Finish(new RecapGridBuildProgressResult.Invalid(
                "ProgressRequestedViewUnavailable",
                "The requested recipe has no exact through-row assignment."
            ));
        }
        bool fulfillmentPresent;
        FulfilledViewKey key;
        try {
            key = FulfilledViewKey.Create(
                frozen.TimelineHead.RefId,
                frozen.TimelineHead,
                frozen.Through.Descriptor.DescriptorDigest,
                frozen.RequestedRecipe.Recipe
            );
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return Finish(new RecapGridBuildProgressResult.Invalid(
                "ProgressFulfilledKeyInvalid",
                exception.Message
            ));
        }
        switch (_store.Reader.ReadFulfilled(key)) {
            case RecapGridStoreReadResult<RecapGridFulfilledView>.Found found
                when found.Value.ViewDigest
                    == requestedFinal.View.Digest:
                fulfillmentPresent = true;
                break;
            case RecapGridStoreReadResult<RecapGridFulfilledView>.Found:
                return Finish(new RecapGridBuildProgressResult.Invalid(
                    "ProgressFulfillmentMismatch",
                    "The exact fulfilled key points to another RowView."
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
        RecapGridPromotableProof? proof = fulfillmentPresent
            ? new RecapGridPromotableProof(
                frozen.ControlSnapshot.Head,
                frozen.TimelineHead,
                frozen.StoreIdentity,
                frozen.RequestedRecipe.Recipe.Digest,
                frozen.Through.Descriptor.RowId,
                frozen.Through.Descriptor.DescriptorDigest,
                key,
                requestedFinal.View.Digest
            )
            : null;
        return Finish(finalFence
            ?? new RecapGridBuildProgressResult.Complete(
                authority,
                requestedFinal.View.Digest,
                proof
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
                value.TimelineHead, value.RecipeDigest),
        RecapGridBuildResult.NoActiveRecipe
            => new RecapGridBuildProgressResult.NoActiveRecipe(),
        RecapGridBuildResult.RecipeAbsent value
            => new RecapGridBuildProgressResult.RecipeAbsent(value.RecipeDigest),
        RecapGridBuildResult.ThroughRowNotSelected value
            => new RecapGridBuildProgressResult.ThroughRowNotSelected(value.RowId),
        RecapGridBuildResult.BudgetExceeded value
            => new RecapGridBuildProgressResult.BudgetExceeded(
                value.Kind, value.AtRow),
        RecapGridBuildResult.Cancelled
            => new RecapGridBuildProgressResult.Cancelled(),
        RecapGridBuildResult.Unavailable value
            => new RecapGridBuildProgressResult.Unavailable(
                value.Dependency, value.Code, value.Detail),
        RecapGridBuildResult.StaleTimelineHead value
            => new RecapGridBuildProgressResult.StaleTimelineHead(value.Actual),
        RecapGridBuildResult.StaleControlAuthority value
            => new RecapGridBuildProgressResult.StaleControlAuthority(value.Actual),
        RecapGridBuildResult.Disposed
            => new RecapGridBuildProgressResult.Disposed(),
        RecapGridBuildResult.Invalid value
            => new RecapGridBuildProgressResult.Invalid(value.Code, value.Detail),
        _ => new RecapGridBuildProgressResult.Invalid(
            "ProgressBuildOutcomeInvalid",
            "A write-only build outcome reached progress inspection."
        )
    };
}
