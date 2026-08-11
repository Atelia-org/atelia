using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

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
        int maximumSelectedRows,
        int maximumRecipeRowSteps,
        int maximumNewCalls,
        TimeSpan maximumElapsed
    ) {
        if (maximumSelectedRows is < 0
            or > HistoryTimelineStoreLimits.MaximumRowCount) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSelectedRows)
            );
        }
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
        MaximumSelectedRows = maximumSelectedRows;
        MaximumRecipeRowSteps = maximumRecipeRowSteps;
        MaximumNewCalls = maximumNewCalls;
        MaximumElapsed = maximumElapsed;
    }

    public int MaximumSelectedRows { get; }
    public int MaximumRecipeRowSteps { get; }
    public int MaximumNewCalls { get; }
    public TimeSpan MaximumElapsed { get; }
}

public sealed record RecapGridBuildRequest {
    public RecapGridBuildRequest(
        RecapGridBuildSelection selection,
        HistoryRowId? throughRowId,
        RecapGridBuildBudget budget
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
    }

    public RecapGridBuildSelection Selection { get; }
    public HistoryRowId? ThroughRowId { get; }
    public RecapGridBuildBudget Budget { get; }
}

public sealed class FrozenRecapCellWork {
    internal FrozenRecapCellWork(
        int ordinal,
        LogicalColumnId logicalColumnId,
        EvaluationKey evaluationKey,
        MaintainerDefinitionRevision definition,
        FamilyDefinition family
    ) {
        Ordinal = ordinal;
        LogicalColumnId = logicalColumnId;
        EvaluationKey = evaluationKey;
        Definition = definition;
        Family = family;
    }

    public int Ordinal { get; }
    public LogicalColumnId LogicalColumnId { get; }
    public EvaluationKey EvaluationKey { get; }
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
        PriorInputProjection? priorProjection,
        IReadOnlyList<FrozenRecapCellWork> orderedMissingWork
    ) {
        TimelineHead = timelineHead;
        ControlHead = controlHead;
        StoreIdentity = storeIdentity;
        Recipe = recipe;
        HistorySegment = historySegment;
        Spec = spec;
        PreviousView = previousView;
        PreviousCells = previousCells;
        PriorProjection = priorProjection;
        OrderedMissingWork = orderedMissingWork;
    }

    public TimelineHeadRef TimelineHead { get; }
    public ControlHeadRef ControlHead { get; }
    public RecapGridStoreIdentity StoreIdentity { get; }
    public GridBuildRecipe Recipe { get; }
    public HistorySegmentContent HistorySegment { get; }
    public RowBuildSpec Spec { get; }
    public RecapRowView? PreviousView { get; }
    public IReadOnlyList<RecapCellArtifact> PreviousCells { get; }
    public PriorInputProjection? PriorProjection { get; }
    public IReadOnlyList<FrozenRecapCellWork> OrderedMissingWork { get; }
}

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
    private RecapCellExecutionOutcome(EvaluationKeyDigest evaluationKey) {
        if (evaluationKey.Value is null) {
            throw new ArgumentException(
                "Evaluation key must not be default.",
                nameof(evaluationKey)
            );
        }
        EvaluationKey = evaluationKey;
    }

    public EvaluationKeyDigest EvaluationKey { get; }

    public sealed record Updated : RecapCellExecutionOutcome {
        public Updated(EvaluationKeyDigest evaluationKey, string content)
            : base(evaluationKey) {
            Content = content
                ?? throw new ArgumentNullException(nameof(content));
        }

        public string Content { get; }
    }

    public sealed record KeepUnchanged : RecapCellExecutionOutcome {
        public KeepUnchanged(EvaluationKeyDigest evaluationKey)
            : base(evaluationKey) { }
    }

    public sealed record Failed : RecapCellExecutionOutcome {
        public Failed(
            EvaluationKeyDigest evaluationKey,
            string code,
            string detail
        ) : base(evaluationKey) {
            Code = RequireText(code, nameof(code));
            Detail = RequireText(detail, nameof(detail));
        }

        public string Code { get; }
        public string Detail { get; }
    }

    public sealed record NotStartedDueToCallerCancellation
        : RecapCellExecutionOutcome {
        public NotStartedDueToCallerCancellation(
            EvaluationKeyDigest evaluationKey
        ) : base(evaluationKey) { }
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
        HistorySegmentDescriptorDigest throughDescriptorDigest,
        FulfilledViewKey fulfilledKey,
        RowViewDigest viewDigest
    ) {
        TimelineHead = timelineHead;
        StoreIdentity = storeIdentity;
        RecipeDigest = recipeDigest;
        ThroughRowId = throughRowId;
        ThroughDescriptorDigest = throughDescriptorDigest;
        FulfilledKey = fulfilledKey;
        ViewDigest = viewDigest;
    }

    public TimelineHeadRef TimelineHead { get; }
    public RecapGridStoreIdentity StoreIdentity { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public HistoryRowId ThroughRowId { get; }
    public HistorySegmentDescriptorDigest ThroughDescriptorDigest { get; }
    public FulfilledViewKey FulfilledKey { get; }
    public RowViewDigest ViewDigest { get; }
}

public sealed class RecapGridPromotableProof {
    internal RecapGridPromotableProof(
        ControlHeadRef controlHead,
        TimelineHeadRef timelineHead,
        RecapGridStoreIdentity storeIdentity,
        GridBuildRecipeDigest recipeDigest,
        HistoryRowId throughRowId,
        HistorySegmentDescriptorDigest throughDescriptorDigest,
        FulfilledViewKey fulfilledKey,
        RowViewDigest viewDigest
    ) {
        ControlHead = controlHead;
        TimelineHead = timelineHead;
        StoreIdentity = storeIdentity;
        RecipeDigest = recipeDigest;
        ThroughRowId = throughRowId;
        ThroughDescriptorDigest = throughDescriptorDigest;
        FulfilledKey = fulfilledKey;
        ViewDigest = viewDigest;
    }

    public ControlHeadRef ControlHead { get; }
    public TimelineHeadRef TimelineHead { get; }
    public RecapGridStoreIdentity StoreIdentity { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public HistoryRowId ThroughRowId { get; }
    public HistorySegmentDescriptorDigest ThroughDescriptorDigest { get; }
    public FulfilledViewKey FulfilledKey { get; }
    public RowViewDigest ViewDigest { get; }
}

public enum RecapGridBuildBudgetKind {
    SelectedRows,
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
    EvaluationKeyDigest EvaluationKey,
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
