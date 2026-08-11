using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private RecapGridBuildResult? PutRowView(
        FrozenOperation frozen,
        RowBuildSpec spec,
        RecapRowView view,
        BuildState state
    ) {
        if (_store.Identity != frozen.StoreIdentity) {
            return Invalid(
                "StoreIdentityChanged",
                "The Store identity changed during the build operation."
            );
        }
        RecapGridRowViewPutResult put = _testHooks.PutRowView is null
            ? _store.Writer.PutRowView(spec, view)
            : _testHooks.PutRowView(
                spec,
                view,
                () => _store.Writer.PutRowView(spec, view)
            );
        if (put is RecapGridRowViewPutResult.CommitIndeterminate
            pending) {
            if (pending.Intended != view.Digest) {
                return Invalid(
                    "RowViewSettlementIntendedMismatch",
                    "The indeterminate RowView identity differs from the proposed view."
                );
            }
            if (pending.Observed is { } observedDigest
                && observedDigest != view.Digest) {
                return Invalid(
                    "RowViewSettlementObservedMismatch",
                    "The observed RowView identity differs from the proposed view."
                );
            }
        }
        if (put is RecapGridRowViewPutResult.CommitIndeterminate
            { Observed: null } pendingRead) {
            RecapGridStoreReadResult<RecapRowView> observed =
                _store.Reader.ReadView(pendingRead.Intended);
            switch (observed) {
                case RecapGridStoreReadResult<RecapRowView>.Found found
                    when found.Value.Digest == pendingRead.Intended
                        && found.Value.ToCanonicalBytes().SequenceEqual(
                            view.ToCanonicalBytes()):
                    put = new RecapGridRowViewPutResult
                        .CommitIndeterminate(
                            pendingRead.Intended,
                            pendingRead.Intended
                        );
                    break;
                case RecapGridStoreReadResult<RecapRowView>.Missing:
                case RecapGridStoreReadResult<RecapRowView>.Busy:
                    break;
                case RecapGridStoreReadResult<RecapRowView>.Disposed:
                    return Unavailable(
                        RecapGridBuildDependency.Store,
                        "StoreDisposed"
                    );
                case RecapGridStoreReadResult<RecapRowView>.Invalid invalid:
                    return Unavailable(
                        RecapGridBuildDependency.Store,
                        invalid.Code,
                        invalid.Detail
                    );
                default:
                    return Invalid(
                        "RowViewSettlementMismatch",
                        "The observed RowView differs from the indeterminate commit."
                    );
            }
        }
        return put switch {
            RecapGridRowViewPutResult.Inserted => CountView(state),
            RecapGridRowViewPutResult.AlreadyPresent => null,
            RecapGridRowViewPutResult.CommitIndeterminate indeterminate
                when indeterminate.Observed == indeterminate.Intended
                    => CountView(state),
            RecapGridRowViewPutResult.CommitIndeterminate indeterminate
                => Settlement(
                    RecapGridBuildCommitKind.RowView,
                    indeterminate.Intended.Value,
                    indeterminate.Observed?.Value,
                    state
                ),
            RecapGridRowViewPutResult.Busy
                => Unavailable(RecapGridBuildDependency.Store,
                    "StoreBusy"),
            RecapGridRowViewPutResult.Limit limit
                => Unavailable(RecapGridBuildDependency.Store,
                    "StoreLimit", limit.Name),
            RecapGridRowViewPutResult.Disposed
                => Unavailable(RecapGridBuildDependency.Store,
                    "StoreDisposed"),
            RecapGridRowViewPutResult.Rejected rejected
                => Invalid("RowViewRejected", rejected.Code),
            RecapGridRowViewPutResult.PrerequisiteMissing missing
                => Unavailable(RecapGridBuildDependency.Store,
                    "RowViewPrerequisiteMissing", missing.Code),
            RecapGridRowViewPutResult.Invalid invalid
                => Unavailable(RecapGridBuildDependency.Store,
                    invalid.Code, invalid.Detail),
            _ => Invalid("RowViewPutOutcomeInvalid",
                "The Store returned an unknown RowView put outcome.")
        };
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
                frozen.RootToThrough[^1].Descriptor.RowId
            );
        }
        RecapGridBuildResult? fence = CheckFinalFences(frozen);
        if (fence is not null) {
            return fence;
        }
        HistoryTimelineSelectedRow through =
            frozen.RootToThrough[^1];
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
                requestedFinal.View.Digest
            )
            : _testHooks.PutFulfilled(
                key,
                requestedFinal.View.Digest,
                () => _store.Writer.PutFulfilled(
                    key,
                    requestedFinal.View.Digest
                )
            );
        if (put is RecapGridFulfilledPutResult.CommitIndeterminate
            pending) {
            if (!pending.Intended.ToCanonicalBytes().SequenceEqual(
                    key.ToCanonicalBytes())) {
                return Invalid(
                    "FulfilledSettlementIntendedMismatch",
                    "The indeterminate fulfillment key differs from the requested canonical key."
                );
            }
            if (pending.Observed is { } observedDigest
                && observedDigest != requestedFinal.View.Digest) {
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
                    found when found.Value.ViewDigest
                        == requestedFinal.View.Digest:
                    put = new RecapGridFulfilledPutResult
                        .CommitIndeterminate(
                            pendingRead.Intended,
                            requestedFinal.View.Digest
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
                when indeterminate.Observed == requestedFinal.View.Digest:
                break;
            case RecapGridFulfilledPutResult.CommitIndeterminate indeterminate:
                return Settlement(
                    RecapGridBuildCommitKind.Fulfilled,
                    Convert.ToHexStringLower(
                        indeterminate.Intended.ToCanonicalBytes()
                    ),
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
                    requestedFinal.View.Digest
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
                requestedFinal.View.Digest
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
        OnlineSelectedRawCaptureResult raw = _timeline.CaptureRaw(
            frozen.TimelineHead
        );
        if (raw is not OnlineSelectedRawCaptureResult.Captured captured) {
            return MapRawCapture(raw);
        }
        return captured.Capture.CapturedHead
            == frozen.RawCapture.CapturedHead
            ? null
            : Unavailable(
                RecapGridBuildDependency.RawHistory,
                "RawHeadChanged",
                "The selected raw head changed during the build operation."
            );
    }

}
