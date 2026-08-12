using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Getter;

public static class RecapGridGetterLimits {
    public const int MaximumNthPrevious = 4_096;
    public const int MaximumProvenanceRows = 128;
    public const int MaximumProvenanceCells =
        MaximumProvenanceRows * RecapGridLimits.MaximumColumnCount;
    public const int MaximumProvenanceCanonicalUtf8Bytes = 16 * 1024 * 1024;
}

public enum RecapGridContextComponent {
    RawAuthority,
    Timeline,
    Cadence,
    Control,
    Store
}

public enum RecapGridProvenanceStatus {
    Verified,
    NotSatisfied,
    Incomplete
}

public sealed record RecapGridContextProvenance(
    RecapGridProvenanceStatus MembershipComplete,
    RecapGridProvenanceStatus PriorInputAligned,
    RecapGridProvenanceStatus FullRebuildChain,
    int ExaminedRows,
    int ExaminedCells,
    int ExaminedCanonicalUtf8Bytes
);

public abstract record RecapGridContextOpenResult {
    private RecapGridContextOpenResult() { }

    public sealed record Opened(RecapGridContextHandle Handle)
        : RecapGridContextOpenResult;
    public sealed record TimelineAbsent : RecapGridContextOpenResult;
    public sealed record CadenceAbsent : RecapGridContextOpenResult;
    public sealed record ControlAbsent : RecapGridContextOpenResult;
    public sealed record Busy(RecapGridContextComponent Component)
        : RecapGridContextOpenResult;
    public sealed record UnsupportedSchema(
        RecapGridContextComponent Component,
        int SchemaVersion
    ) : RecapGridContextOpenResult;
    public sealed record DisposedRawAuthority : RecapGridContextOpenResult;
    public sealed record Invalid(
        RecapGridContextComponent Component,
        string Code,
        string Detail
    ) : RecapGridContextOpenResult;
}

public sealed class RecapGridContextSelection {
    internal RecapGridContextSelection(
        EventAddress completionBoundary,
        int nthPrevious,
        TimelineHeadRef timelineHead,
        RecapGridCadenceHeadRef cadenceHead,
        ControlHeadRef controlHead,
        RecapGridStoreIdentity storeIdentity,
        GridBuildRecipe recipe,
        HistoryTimelineSelectedRow selectedRow,
        RecapRowView selectedView,
        FulfilledViewKey currentFulfilledKey,
        RowViewDigest currentViewDigest,
        GetterLifetime owner,
        string ownerNonce,
        string handleToken,
        string snapshotToken
    ) {
        CompletionBoundary = completionBoundary;
        NthPrevious = nthPrevious;
        TimelineHead = timelineHead;
        CadenceHead = cadenceHead;
        ControlHead = controlHead;
        StoreIdentity = storeIdentity;
        Recipe = recipe;
        SelectedRow = selectedRow;
        SelectedView = selectedView;
        CurrentFulfilledKey = currentFulfilledKey;
        CurrentViewDigest = currentViewDigest;
        Owner = owner;
        OwnerNonce = ownerNonce;
        HandleToken = handleToken;
        SnapshotToken = snapshotToken;
    }

    public EventAddress CompletionBoundary { get; }
    public int NthPrevious { get; }
    public TimelineHeadRef TimelineHead { get; }
    public RecapGridCadenceHeadRef CadenceHead { get; }
    public ControlHeadRef ControlHead { get; }
    public RecapGridStoreIdentity StoreIdentity { get; }
    public GridBuildRecipe Recipe { get; }
    public HistoryRowId SelectedRowId => SelectedRow.Descriptor.RowId;
    public HistorySegmentDescriptorDigest SelectedDescriptorDigest =>
        SelectedRow.Descriptor.DescriptorDigest;
    public RowViewDigest SelectedViewDigest => SelectedView.Digest;
    public FulfilledViewKey CurrentFulfilledKey { get; }
    public RowViewDigest CurrentViewDigest { get; }

    internal HistoryTimelineSelectedRow SelectedRow { get; }
    internal RecapRowView SelectedView { get; }
    internal GetterLifetime Owner { get; }
    internal string OwnerNonce { get; }
    internal string HandleToken { get; }
    internal string SnapshotToken { get; }
}

internal sealed record GetterProvenanceReadBudget(
    int MaximumRows,
    int MaximumCells,
    int MaximumCanonicalUtf8Bytes
) {
    internal static GetterProvenanceReadBudget Production { get; } = new(
        RecapGridGetterLimits.MaximumProvenanceRows,
        RecapGridGetterLimits.MaximumProvenanceCells,
        RecapGridGetterLimits.MaximumProvenanceCanonicalUtf8Bytes
    );

    internal void Validate() {
        if (MaximumRows < 1
            || MaximumCells < 1
            || MaximumCanonicalUtf8Bytes < 1) {
            throw new ArgumentOutOfRangeException(nameof(MaximumRows));
        }
    }
}

internal sealed record GetterTestHooks(
    Action<RecapGridContextResolveResult>? BeforeTerminalFence = null,
    GetterProvenanceReadBudget? ProvenanceBudget = null,
    Action? BeforeProvenancePredecessorLookup = null
) {
    internal static GetterTestHooks None { get; } = new();
}

public abstract record RecapGridContextResolveResult {
    private RecapGridContextResolveResult() { }

    public sealed record RawHistoryAuthorized
        : RecapGridContextResolveResult;
    public sealed record ReserveBootstrapRawOnly(
        RecapGridReserveBootstrapEvidence Evidence
    ) : RecapGridContextResolveResult;
    public sealed record Selected(RecapGridContextSelection Selection)
        : RecapGridContextResolveResult;
    public sealed record OrdinalUnavailable
        : RecapGridContextResolveResult;
    public sealed record LimitExceeded(string Limit)
        : RecapGridContextResolveResult;
    public sealed record Unfulfilled(FulfilledViewKey Key)
        : RecapGridContextResolveResult;
    public sealed record Stale(
        RecapGridContextComponent Component,
        string Detail
    ) : RecapGridContextResolveResult;
    public sealed record NotOnSelectedPath(HistoryRowId RowId)
        : RecapGridContextResolveResult;
    public sealed record Busy(RecapGridContextComponent Component)
        : RecapGridContextResolveResult;
    public sealed record Disposed(RecapGridContextComponent Component)
        : RecapGridContextResolveResult;
    public sealed record UnsupportedSchema(
        RecapGridContextComponent Component,
        int SchemaVersion
    ) : RecapGridContextResolveResult;
    public sealed record Invalid(
        RecapGridContextComponent Component,
        string Code,
        string Detail
    ) : RecapGridContextResolveResult;
}

public sealed record RecapGridReserveBootstrapEvidence(
    TimelineHeadRef TimelineHead,
    RecapGridCadenceHeadRef CadenceHead,
    ControlHeadRef ControlHead,
    RecapGridStoreIdentity StoreIdentity,
    HistoryLoadUnit RetainedHistoryLoad,
    HistoryLoadUnit RequiredHistoryLoad,
    int VerifiedRows,
    HistoryRecentReserveAnchorMetrics Metrics
);

public abstract record RecapGridContextMaterializeResult {
    private RecapGridContextMaterializeResult() { }

    public sealed record Available(
        SessionContextCandidate Candidate,
        RecapGridContextProvenance Provenance
    ) : RecapGridContextMaterializeResult;
    public sealed record Stale(
        RecapGridContextComponent Component,
        string Detail
    ) : RecapGridContextMaterializeResult;
    public sealed record Busy(RecapGridContextComponent Component)
        : RecapGridContextMaterializeResult;
    public sealed record Disposed(RecapGridContextComponent Component)
        : RecapGridContextMaterializeResult;
    public sealed record Invalid(
        RecapGridContextComponent Component,
        string Code,
        string Detail
    ) : RecapGridContextMaterializeResult;
}
