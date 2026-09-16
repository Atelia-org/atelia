using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

/// <summary>A stored row winner confirmed after validating its requested assignment.</summary>
public sealed record RecapGridRowCommitProgress(
    GridBuildRecipeDigest RecipeDigest,
    HistoryRowId RowId,
    RowResultId RowResultId,
    bool AlreadyPresent
);

public abstract record RecapGridBuildSelection {
    private RecapGridBuildSelection() { }

    public sealed record LiveActive : RecapGridBuildSelection;

    public sealed record ExplicitCandidate : RecapGridBuildSelection {
        public ExplicitCandidate(GridBuildRecipeDigest recipeDigest)
        {
            if (recipeDigest.Value is null) {
                throw new ArgumentException(
                    "Recipe digest must not be default.",
                    nameof(recipeDigest)
                );
            }
            RecipeDigest = recipeDigest;
        }

        public GridBuildRecipeDigest RecipeDigest { get; }
    }
}

public sealed record RecapGridBuildBudget {
    public RecapGridBuildBudget(
        int maximumRecipeRowSteps,
        int maximumNewCalls,
        TimeSpan maximumElapsed
    ) {
        if (maximumRecipeRowSteps is < 0 or > 1_000_000) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRecipeRowSteps)
            );
        }
        if (maximumNewCalls is < 0 or > 1_000_000) {
            throw new ArgumentOutOfRangeException(nameof(maximumNewCalls));
        }
        if (maximumElapsed <= TimeSpan.Zero
            || maximumElapsed > TimeSpan.FromDays(1)) {
            throw new ArgumentOutOfRangeException(nameof(maximumElapsed));
        }
        MaximumRecipeRowSteps = maximumRecipeRowSteps;
        MaximumNewCalls = maximumNewCalls;
        MaximumElapsed = maximumElapsed;
    }

    public int MaximumRecipeRowSteps { get; }
    public int MaximumNewCalls { get; }
    public TimeSpan MaximumElapsed { get; }
}

public sealed record RecapGridBuildRequest {
    public RecapGridBuildRequest(
        RecapGridBuildSelection selection,
        HistoryRowId? throughRowId,
        RecapGridBuildBudget budget,
        BuildTarget? liveProducerTarget = null
    ) {
        Selection = selection
            ?? throw new ArgumentNullException(nameof(selection));
        if (throughRowId is { Value: null }) {
            throw new ArgumentException(
                "Through row must not be default.",
                nameof(throughRowId)
            );
        }
        ThroughRowId = throughRowId;
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        if (liveProducerTarget is not null
            && selection is not RecapGridBuildSelection.LiveActive) {
            throw new ArgumentException(
                "An explicit candidate chooses its producer through its candidate recipe.",
                nameof(liveProducerTarget)
            );
        }
        LiveProducerTarget = liveProducerTarget;
    }

    public RecapGridBuildSelection Selection { get; }
    public HistoryRowId? ThroughRowId { get; }
    public RecapGridBuildBudget Budget { get; }
    public BuildTarget? LiveProducerTarget { get; }
}

public sealed class FrozenRecapCellWork {
    internal FrozenRecapCellWork(
        int ordinal,
        CellSlot slot,
        MaintainerDefinitionRevision definition,
        FamilyDefinition family
    ) {
        Ordinal = ordinal;
        Slot = slot ?? throw new ArgumentNullException(nameof(slot));
        Definition = definition;
        Family = family;
    }

    public int Ordinal { get; }
    public LogicalColumnId LogicalColumnId => Slot.LogicalColumnId;
    public CellSlot Slot { get; }
    public MaintainerDefinitionRevision Definition { get; }
    public FamilyDefinition Family { get; }
}

public sealed class FrozenRowBatch {
    internal FrozenRowBatch(
        TimelineHeadRef timelineHead,
        ControlHeadRef controlHead,
        RecapGridStoreIdentity storeIdentity,
        GridBuildRecipe recipe,
        HistorySegmentContent historySegment,
        RowBuildSpec spec,
        RecapRowView? previousView,
        IReadOnlyList<RecapCellArtifact> previousCells,
        IReadOnlyList<FrozenRecapCellWork> orderedMissingWork,
        IReadOnlyDictionary<MaintainerDefinitionDigest,
            MaintainerDefinitionRevision>? previousDefinitions = null
    ) {
        TimelineHead = timelineHead;
        ControlHead = controlHead;
        StoreIdentity = storeIdentity;
        Recipe = recipe;
        HistorySegment = historySegment;
        Spec = spec;
        PreviousView = previousView;
        PreviousCells = previousCells;
        OrderedMissingWork = orderedMissingWork;
        PreviousDefinitions = previousDefinitions
            ?? new Dictionary<MaintainerDefinitionDigest,
                MaintainerDefinitionRevision>();
    }

    public TimelineHeadRef TimelineHead { get; }
    public ControlHeadRef ControlHead { get; }
    public RecapGridStoreIdentity StoreIdentity { get; }
    public GridBuildRecipe Recipe { get; }
    public HistorySegmentContent HistorySegment { get; }
    public RowBuildSpec Spec { get; }
    public RecapRowView? PreviousView { get; }
    public IReadOnlyList<RecapCellArtifact> PreviousCells { get; }
    public IReadOnlyList<FrozenRecapCellWork> OrderedMissingWork { get; }
    public IReadOnlyDictionary<MaintainerDefinitionDigest,
        MaintainerDefinitionRevision> PreviousDefinitions { get; }
}

/// <summary>
/// Pure, retryable evaluation of frozen missing Cell work. The current V1
/// contract may start at most one provider invocation for each work item and
/// must not mutate Control, Store, Timeline, raw journals, or external memory.
/// A future multi-call protocol requires an expanded admission, settlement,
/// cancellation/drain, and operation-total evidence contract first.
/// </summary>
public interface IRecapCellBatchExecutor {
    ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
        FrozenRowBatch batch,
        CancellationToken cancellationToken
    );
}

public abstract record RecapCellBatchExecutionResult {
    private RecapCellBatchExecutionResult() { }

    public sealed record RejectedBeforeDispatch
        : RecapCellBatchExecutionResult {
        public RejectedBeforeDispatch(string code, string detail) {
            Code = RequireText(code, nameof(code));
            Detail = RequireText(detail, nameof(detail));
        }

        public string Code { get; }
        public string Detail { get; }

        private static string RequireText(
            string value,
            string parameterName
        ) {
            if (string.IsNullOrWhiteSpace(value)) {
                throw new ArgumentException(
                    "A non-empty value is required.",
                    parameterName
                );
            }
            return value;
        }
    }

    public sealed record Completed(
        IReadOnlyList<RecapCellExecutionOutcome> OrderedOutcomes
    ) : RecapCellBatchExecutionResult;
}

public abstract record RecapCellExecutionOutcome {
    private RecapCellExecutionOutcome(CellSlot slot) {
        Slot = slot ?? throw new ArgumentNullException(nameof(slot));
    }

    public CellSlot Slot { get; }

    public sealed record Updated : RecapCellExecutionOutcome {
        public Updated(CellSlot slot, string content)
            : base(slot) {
            Content = content
                ?? throw new ArgumentNullException(nameof(content));
        }

        public string Content { get; }
    }

    public sealed record KeepUnchanged : RecapCellExecutionOutcome {
        public KeepUnchanged(CellSlot slot)
            : base(slot) { }
    }

    public sealed record Failed : RecapCellExecutionOutcome {
        public Failed(
            CellSlot slot,
            string code,
            string detail
        ) : base(slot) {
            Code = RequireText(code, nameof(code));
            Detail = RequireText(detail, nameof(detail));
        }

        public string Code { get; }
        public string Detail { get; }
    }

    public sealed record NotStartedDueToCallerCancellation
        : RecapCellExecutionOutcome {
        public NotStartedDueToCallerCancellation(
            CellSlot slot
        ) : base(slot) { }
    }

    private static string RequireText(string value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                "A non-empty value is required.",
                parameterName
            );
        }
        return value;
    }
}

public sealed record RecapGridBuildMetrics(
    int SelectedRows,
    int RecipeRowSteps,
    int NewCalls,
    int CellsCommitted,
    int RowViewsCommitted
) {
    public static RecapGridBuildMetrics Empty { get; } = new(
        0,
        0,
        0,
        0,
        0
    );
}

public sealed class RecapGridFulfillmentReceipt {
    internal RecapGridFulfillmentReceipt(
        TimelineHeadRef timelineHead,
        RecapGridStoreIdentity storeIdentity,
        GridBuildRecipeDigest recipeDigest,
        HistoryRowId throughRowId,
        FulfilledViewKey fulfilledKey,
        RowResultId rowResultId
    ) {
        TimelineHead = timelineHead;
        StoreIdentity = storeIdentity;
        RecipeDigest = recipeDigest;
        ThroughRowId = throughRowId;
        FulfilledKey = fulfilledKey;
        RowResultId = rowResultId;
    }

    public TimelineHeadRef TimelineHead { get; }
    public RecapGridStoreIdentity StoreIdentity { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public HistoryRowId ThroughRowId { get; }
    public FulfilledViewKey FulfilledKey { get; }
    public RowResultId RowResultId { get; }
}

public sealed class RecapGridPromotableProof {
    internal RecapGridPromotableProof(
        ControlHeadRef controlHead,
        TimelineHeadRef timelineHead,
        RecapGridStoreIdentity storeIdentity,
        GridBuildRecipeDigest recipeDigest,
        HistoryRowId throughRowId,
        FulfilledViewKey fulfilledKey,
        RowResultId rowResultId
    ) {
        ControlHead = controlHead;
        TimelineHead = timelineHead;
        StoreIdentity = storeIdentity;
        RecipeDigest = recipeDigest;
        ThroughRowId = throughRowId;
        FulfilledKey = fulfilledKey;
        RowResultId = rowResultId;
    }

    public ControlHeadRef ControlHead { get; }
    public TimelineHeadRef TimelineHead { get; }
    public RecapGridStoreIdentity StoreIdentity { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public HistoryRowId ThroughRowId { get; }
    public FulfilledViewKey FulfilledKey { get; }
    public RowResultId RowResultId { get; }
}

public enum RecapGridBuildBudgetKind {
    RecipeRowSteps,
    NewCalls,
    Elapsed
}

public enum RecapGridBuildDependency {
    Timeline,
    RawHistory,
    Control,
    Store
}

public enum RecapGridBuildCommitKind {
    Cell,
    RowView,
    Fulfilled
}

public sealed record RecapGridCellFailure(
    int Ordinal,
    CellSlot Slot,
    string Code,
    string Detail,
    bool NotStarted
);

public abstract record RecapGridBuildResult {
    private RecapGridBuildResult() { }

    public RecapGridBuildMetrics Metrics { get; init; }
        = RecapGridBuildMetrics.Empty;

    public sealed record Fulfilled(
        RecapGridPromotableProof Proof
    ) : RecapGridBuildResult;

    public sealed record FulfilledThrough(
        RecapGridFulfillmentReceipt Receipt
    ) : RecapGridBuildResult;

    public sealed record NoRows(
        TimelineHeadRef TimelineHead,
        GridBuildRecipeDigest RecipeDigest
    ) : RecapGridBuildResult;

    public sealed record NoActiveRecipe : RecapGridBuildResult;

    public sealed record RecipeAbsent(GridBuildRecipeDigest RecipeDigest)
        : RecapGridBuildResult;

    public sealed record ProducerPolicyRequired(
        GridBuildRecipeDigest RootRecipeDigest,
        HistoryRowId RowId
    ) : RecapGridBuildResult;

    public sealed record OverlaySourceIncompatible(
        GridBuildRecipeDigest RootRecipeDigest,
        HistoryRowId RowId,
        LogicalColumnId LogicalColumnId,
        string Reason
    ) : RecapGridBuildResult;

    public sealed record ThroughRowNotSelected(HistoryRowId RowId)
        : RecapGridBuildResult;

    public sealed record BudgetExceeded(
        RecapGridBuildBudgetKind Kind,
        HistoryRowId? AtRow
    ) : RecapGridBuildResult;

    public sealed record Cancelled : RecapGridBuildResult;

    public sealed record Incomplete(
        HistoryRowId RowId,
        IReadOnlyList<RecapGridCellFailure> Failures
    ) : RecapGridBuildResult;

    public sealed record ExecutorRejected(string Code, string Detail)
        : RecapGridBuildResult;

    public sealed record ExecutorFailed(string Code, string Detail)
        : RecapGridBuildResult;

    public sealed record Unavailable(
        RecapGridBuildDependency Dependency,
        string Code,
        string Detail
    ) : RecapGridBuildResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : RecapGridBuildResult;

    public sealed record StaleControlAuthority(ControlHeadRef Actual)
        : RecapGridBuildResult;

    public sealed record SettlementRequired(
        RecapGridBuildCommitKind Kind,
        string IntendedIdentity,
        string? ObservedIdentity
    ) : RecapGridBuildResult;

    public sealed record Disposed : RecapGridBuildResult;

    public sealed record Invalid(string Code, string Detail)
        : RecapGridBuildResult;
}

public abstract record RecapGridManagerOpenResult {
    private RecapGridManagerOpenResult() { }

    public sealed record Opened(RecapGridManagerHandle Handle)
        : RecapGridManagerOpenResult;

    public sealed record Absent(RecapGridBuildDependency Dependency)
        : RecapGridManagerOpenResult;

    public sealed record Busy(RecapGridBuildDependency Dependency)
        : RecapGridManagerOpenResult;

    public sealed record UnsupportedSchema(
        RecapGridBuildDependency Dependency,
        int SchemaVersion
    ) : RecapGridManagerOpenResult;

    public sealed record PlatformUnsupported(
        RecapGridBuildDependency Dependency
    ) : RecapGridManagerOpenResult;

    public sealed record Invalid(
        RecapGridBuildDependency Dependency,
        string Code,
        string Detail
    ) : RecapGridManagerOpenResult;
}

public static class RecapGridBuildProgressLimits {
    public const int MaximumFrontierAssignments =
        RecapGridLimits.MaximumColumnCount;
}

public readonly record struct RecapGridBuildProgressMetrics(
    int SelectedRows,
    int RecipeRowSteps,
    int ExaminedAssignments,
    int MissingAssignments
);

public sealed record RecapGridBuildProgressAuthority {
    internal RecapGridBuildProgressAuthority(
        TimelineHeadRef timelineHead,
        ControlHeadRef controlHead,
        RecapGridStoreIdentity storeIdentity,
        GridBuildRecipeDigest recipeDigest,
        HistoryRowId throughRowId
    ) {
        TimelineHead = timelineHead;
        ControlHead = controlHead;
        StoreIdentity = storeIdentity;
        RecipeDigest = recipeDigest;
        ThroughRowId = throughRowId;
    }

    public TimelineHeadRef TimelineHead { get; }
    public ControlHeadRef ControlHead { get; }
    public RecapGridStoreIdentity StoreIdentity { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public HistoryRowId ThroughRowId { get; }
}

public sealed record RecapGridMissingAssignmentProgress {
    internal RecapGridMissingAssignmentProgress(
        int ordinal,
        HistoryRowId rowId,
        GridBuildRecipeDigest recipeDigest,
        LogicalColumnId logicalColumnId
    ) {
        Ordinal = ordinal;
        RowId = rowId;
        RecipeDigest = recipeDigest;
        LogicalColumnId = logicalColumnId;
    }

    public int Ordinal { get; }
    public HistoryRowId RowId { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public LogicalColumnId LogicalColumnId { get; }
}

/// <summary>
/// One resumable Manager work unit. A unit always names exactly one frozen
/// recipe at one selected Timeline row; cell calls are children of this unit.
/// </summary>
public sealed record RecapGridRecipeRowWork {
    internal RecapGridRecipeRowWork(
        HistoryRowId rowId,
        GridBuildRecipeDigest recipeDigest,
        BuildTargetDigest producerTargetDigest,
        RowWorkId? workId,
        bool isOverlayBootstrap
    ) {
        RowId = rowId;
        RecipeDigest = recipeDigest;
        ProducerTargetDigest = producerTargetDigest;
        WorkId = workId;
        IsOverlayBootstrap = isOverlayBootstrap;
    }

    public HistoryRowId RowId { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    /// <summary>
    /// The actual target for this row. A proposed work has no durable
    /// <see cref="WorkId"/> yet.
    /// </summary>
    public BuildTargetDigest ProducerTargetDigest { get; }
    public RowWorkId? WorkId { get; }
    public bool IsOverlayBootstrap { get; }
}

public abstract record RecapGridBuildProgressResult {
    private RecapGridBuildProgressResult() { }

    public RecapGridBuildProgressMetrics Metrics { get; internal init; }

    public sealed record Complete(
        RecapGridBuildProgressAuthority Authority,
        RowResultId ThroughRowResultId,
        RecapGridPromotableProof? Proof
    ) : RecapGridBuildProgressResult {
        public bool FulfillmentPresent => Proof is not null;
    }

    public sealed record Frontier(
        RecapGridBuildProgressAuthority Authority,
        HistoryRowId? AnchorRowId,
        RecapGridRecipeRowWork NextWork,
        int PendingRecipeRows,
        IReadOnlyList<RecapGridMissingAssignmentProgress> OrderedMissing
    ) : RecapGridBuildProgressResult {
        public HistoryRowId RowId => NextWork.RowId;
        public GridBuildRecipeDigest RecipeDigest =>
            NextWork.RecipeDigest;
    }

    public sealed record Blocked(
        RecapGridBuildProgressAuthority Authority,
        HistoryRowId RowId,
        GridBuildRecipeDigest RecipeDigest,
        string Code,
        string Detail
    ) : RecapGridBuildProgressResult;

    public sealed record NoRows(
        TimelineHeadRef TimelineHead,
        GridBuildRecipeDigest RecipeDigest
    ) : RecapGridBuildProgressResult;

    public sealed record NoActiveRecipe : RecapGridBuildProgressResult;

    public sealed record RecipeAbsent(GridBuildRecipeDigest RecipeDigest)
        : RecapGridBuildProgressResult;

    public sealed record ProducerPolicyRequired(
        GridBuildRecipeDigest RootRecipeDigest,
        HistoryRowId RowId
    ) : RecapGridBuildProgressResult;

    public sealed record OverlaySourceIncompatible(
        GridBuildRecipeDigest RootRecipeDigest,
        HistoryRowId RowId,
        LogicalColumnId LogicalColumnId,
        string Reason
    ) : RecapGridBuildProgressResult;

    public sealed record ThroughRowNotSelected(HistoryRowId RowId)
        : RecapGridBuildProgressResult;

    public sealed record BudgetExceeded(
        RecapGridBuildBudgetKind Kind,
        HistoryRowId? AtRow
    ) : RecapGridBuildProgressResult;

    public sealed record Cancelled : RecapGridBuildProgressResult;

    public sealed record Unavailable(
        RecapGridBuildDependency Dependency,
        string Code,
        string Detail
    ) : RecapGridBuildProgressResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : RecapGridBuildProgressResult;

    public sealed record StaleControlAuthority(ControlHeadRef Actual)
        : RecapGridBuildProgressResult;

    public sealed record Disposed : RecapGridBuildProgressResult;

    public sealed record Invalid(string Code, string Detail)
        : RecapGridBuildProgressResult;
}
