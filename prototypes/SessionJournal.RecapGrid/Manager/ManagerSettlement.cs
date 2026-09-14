using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private (RecapRowView? Winner, RecapGridBuildResult? Error) PutRowView(
        FrozenOperation frozen,
        RowBuildSpec spec,
        IReadOnlyList<RecapCellArtifact> cells,
        BuildState state
    ) {
        if (_store.Identity != frozen.StoreIdentity) {
            return (null, Invalid("StoreIdentityChanged",
                "The Store identity changed during the build operation."));
        }
        RecapGridRowViewPutResult put = _testHooks.PutRowView is null
            ? _store.Writer.PutRowView(spec, cells)
            : _testHooks.PutRowView(spec, cells, () => _store.Writer.PutRowView(spec, cells));
        RecapRowView? winner;
        switch (put) {
            case RecapGridRowViewPutResult.Inserted inserted:
                winner = inserted.Winner;
                state.RowViewsCommitted++;
                break;
            case RecapGridRowViewPutResult.AlreadyPresent already:
                winner = already.Winner;
                break;
            case RecapGridRowViewPutResult.CommitIndeterminate pending:
                if (pending.IntendedAssignment != spec.Coordinate.AssignmentKey) {
                    return (null, Invalid("RowViewSettlementIntendedMismatch",
                        "The indeterminate RowView assignment differs from the requested row."));
                }
                if (pending.Observed is { } reported && !MatchesRow(reported, spec, cells)) {
                    return (null, Invalid("RowViewSettlementObservedMismatch",
                        "The reported RowView differs from the requested business assignment."));
                }
                {
                    RecapGridStoreReadResult<RecapRowView> observed =
                        _store.Reader.ReadViewAt(pending.IntendedAssignment);
                    switch (observed) {
                        case RecapGridStoreReadResult<RecapRowView>.Found found:
                            winner = found.Value;
                            break;
                        case RecapGridStoreReadResult<RecapRowView>.Missing:
                        case RecapGridStoreReadResult<RecapRowView>.Busy:
                            return (null, Settlement(RecapGridBuildCommitKind.RowView,
                                pending.IntendedAssignment.ToString(), pending.Observed?.Id.Value, state));
                        case RecapGridStoreReadResult<RecapRowView>.Disposed:
                            return (null, Unavailable(RecapGridBuildDependency.Store, "StoreDisposed"));
                        case RecapGridStoreReadResult<RecapRowView>.Invalid invalid:
                            return (null, Unavailable(RecapGridBuildDependency.Store, invalid.Code, invalid.Detail));
                        default:
                            return (null, Invalid("RowViewSettlementMismatch", "The RowView observation is unsupported."));
                    }
                }
                state.RowViewsCommitted++;
                break;
            case RecapGridRowViewPutResult.Busy:
                return (null, Unavailable(RecapGridBuildDependency.Store, "StoreBusy"));
            case RecapGridRowViewPutResult.Limit limit:
                return (null, Unavailable(RecapGridBuildDependency.Store, "StoreLimit", limit.Name));
            case RecapGridRowViewPutResult.Disposed:
                return (null, Unavailable(RecapGridBuildDependency.Store, "StoreDisposed"));
            case RecapGridRowViewPutResult.Rejected rejected:
                return (null, Invalid("RowViewRejected", rejected.Code));
            case RecapGridRowViewPutResult.PrerequisiteMissing missing:
                return (null, Unavailable(RecapGridBuildDependency.Store, "RowViewPrerequisiteMissing", missing.Code));
            case RecapGridRowViewPutResult.Invalid invalid:
                return (null, Unavailable(RecapGridBuildDependency.Store, invalid.Code, invalid.Detail));
            default:
                return (null, Invalid("RowViewPutOutcomeInvalid", "The Store returned an unknown RowView put outcome."));
        }
        return MatchesRow(winner, spec, cells)
            ? (winner, null)
            : (null, Invalid("RowViewSettlementMismatch", "The stored RowView differs from the requested business assignment."));
    }

    private static bool MatchesRow(RecapRowView row, RowBuildSpec spec,
        IReadOnlyList<RecapCellArtifact> cells) {
        if (row.Coordinate != spec.Coordinate || row.OrderedCells.Count != cells.Count) {
            return false;
        }
        for (int index = 0; index < cells.Count; index++) {
            RecapRowViewCell member = row.OrderedCells[index];
            RecapCellArtifact cell = cells[index];
            if (member.CellId != cell.Id || member.LogicalColumnId != cell.LogicalColumnId
                || member.DefinitionDigest != cell.DefinitionDigest) {
                return false;
            }
        }
        return true;
    }

    private RecapGridBuildResult FinalizeFulfilled(
        FrozenOperation frozen,
        BuiltRow requestedFinal,
        BuildState state
    ) {
        if (_store.Identity != frozen.StoreIdentity) {
            return Invalid(
                "StoreIdentityChanged",
                "The Store identity changed during the build operation."
            );
        }
        if (state.HasElapsed()) {
            return new RecapGridBuildResult.BudgetExceeded(
                RecapGridBuildBudgetKind.Elapsed,
                frozen.Through.Descriptor.RowId
            );
        }
        RecapGridBuildResult? fence = CheckFinalFences(frozen);
        if (fence is not null) {
            return fence;
        }
        HistoryTimelineSelectedRow through = frozen.Through;
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
            return Invalid("FulfilledKeyInvalid", exception.Message);
        }
        RecapGridFulfilledPutResult put = _testHooks.PutFulfilled is null
            ? _store.Writer.PutFulfilled(
                key,
                requestedFinal.View.Id
            )
            : _testHooks.PutFulfilled(
                key,
                requestedFinal.View.Id,
                () => _store.Writer.PutFulfilled(
                    key,
                    requestedFinal.View.Id
                )
            );
        if (put is RecapGridFulfilledPutResult.CommitIndeterminate
            pending) {
            if (pending.Intended != key) {
                return Invalid(
                    "FulfilledSettlementIntendedMismatch",
                    "The indeterminate fulfillment key differs from the requested canonical key."
                );
            }
            if (pending.Observed is { } observedDigest
                && observedDigest != requestedFinal.View.Id) {
                return Invalid(
                    "FulfilledSettlementObservedMismatch",
                    "The observed fulfillment differs from the requested view."
                );
            }
        }
        if (put is RecapGridFulfilledPutResult.CommitIndeterminate
            { Observed: null } pendingRead) {
            RecapGridStoreReadResult<RecapGridFulfilledView> observed =
                _store.Reader.ReadFulfilled(key);
            switch (observed) {
                case RecapGridStoreReadResult<RecapGridFulfilledView>.Found
                    found when found.Value.RowResultId
                        == requestedFinal.View.Id:
                    put = new RecapGridFulfilledPutResult
                        .CommitIndeterminate(
                            pendingRead.Intended,
                            requestedFinal.View.Id
                        );
                    break;
                case RecapGridStoreReadResult<RecapGridFulfilledView>.Missing:
                case RecapGridStoreReadResult<RecapGridFulfilledView>.Busy:
                    break;
                case RecapGridStoreReadResult<
                    RecapGridFulfilledView>.Disposed:
                    return Unavailable(
                        RecapGridBuildDependency.Store,
                        "StoreDisposed"
                    );
                case RecapGridStoreReadResult<
                    RecapGridFulfilledView>.Invalid invalid:
                    return Unavailable(
                        RecapGridBuildDependency.Store,
                        invalid.Code,
                        invalid.Detail
                    );
                default:
                    return Invalid(
                        "FulfilledSettlementMismatch",
                        "The observed fulfillment differs from the indeterminate commit."
                    );
            }
        }
        switch (put) {
            case RecapGridFulfilledPutResult.Inserted:
            case RecapGridFulfilledPutResult.AlreadyPresent:
                break;
            case RecapGridFulfilledPutResult.CommitIndeterminate indeterminate
                when indeterminate.Observed == requestedFinal.View.Id:
                break;
            case RecapGridFulfilledPutResult.CommitIndeterminate indeterminate:
                return Settlement(
                    RecapGridBuildCommitKind.Fulfilled,
                    indeterminate.Intended.ToString(),
                    indeterminate.Observed?.Value,
                    state
                );
            case RecapGridFulfilledPutResult.Busy:
                return Unavailable(RecapGridBuildDependency.Store,
                    "StoreBusy");
            case RecapGridFulfilledPutResult.Limit limit:
                return Unavailable(RecapGridBuildDependency.Store,
                    "StoreLimit", limit.Name);
            case RecapGridFulfilledPutResult.Disposed:
                return Unavailable(RecapGridBuildDependency.Store,
                    "StoreDisposed");
            case RecapGridFulfilledPutResult.Rejected rejected:
                return Invalid("FulfilledRejected", rejected.Code);
            case RecapGridFulfilledPutResult.PrerequisiteMissing missing:
                return Unavailable(RecapGridBuildDependency.Store,
                    "FulfilledPrerequisiteMissing", missing.Code);
            case RecapGridFulfilledPutResult.Invalid invalid:
                return Unavailable(RecapGridBuildDependency.Store,
                    invalid.Code, invalid.Detail);
            default:
                return Invalid("FulfilledPutOutcomeInvalid",
                    "The Store returned an unknown Fulfilled put outcome.");
        }
        fence = CheckFinalFences(frozen);
        if (fence is not null) {
            return fence;
        }
        bool isSelectedHead = through.Descriptor.RowId
                == frozen.TimelineHead.HeadRowId
            && through.Descriptor == frozen.SelectedHead.Descriptor;
        if (!isSelectedHead) {
            return new RecapGridBuildResult.FulfilledThrough(
                new RecapGridFulfillmentReceipt(
                    frozen.TimelineHead,
                    frozen.StoreIdentity,
                    frozen.RequestedRecipe.Recipe.Digest,
                    through.Descriptor.RowId,
                    through.Descriptor.DescriptorDigest,
                    key,
                    requestedFinal.View.Id
                )
            );
        }
        return new RecapGridBuildResult.Fulfilled(
            new RecapGridPromotableProof(
                frozen.ControlSnapshot.Head,
                frozen.TimelineHead,
                frozen.StoreIdentity,
                frozen.RequestedRecipe.Recipe.Digest,
                through.Descriptor.RowId,
                through.Descriptor.DescriptorDigest,
                key,
                requestedFinal.View.Id
            )
        );
    }

    private RecapGridBuildResult? CheckFinalFences(
        FrozenOperation frozen
    ) {
        RecapGridBuildResult? fence = CheckTimelineFence(
            frozen.TimelineHead
        ) ?? CheckControlFence(frozen);
        if (fence is not null) {
            return fence;
        }
        HistoryTimelineRawHeadObservationResult observed =
            _timeline.ObserveRawHead();
        if (observed is not HistoryTimelineRawHeadObservationResult.Available
            available) {
            return MapRawHeadObservation(observed);
        }
        if (available.Head != frozen.FrozenRawHead) {
            return Unavailable(
                RecapGridBuildDependency.RawHistory,
                "RawHeadChanged",
                "The selected raw head changed during the build operation."
            );
        }
        return null;
    }

}
