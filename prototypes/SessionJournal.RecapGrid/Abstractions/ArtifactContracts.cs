using System.Collections.ObjectModel;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid;

public abstract class RowBuildAssignment {
    private protected RowBuildAssignment(LogicalColumnId logicalColumnId) {
        RecapGridSyntax.RequireIdentifier(
            logicalColumnId.Value
                ?? throw new ArgumentException(
                    "LogicalColumnId must not be default.",
                    nameof(logicalColumnId)
                ),
            nameof(logicalColumnId)
        );
        LogicalColumnId = logicalColumnId;
    }

    public LogicalColumnId LogicalColumnId { get; }

    public sealed class Evaluate : RowBuildAssignment {
        public Evaluate(CellSlot slot) : base((slot ?? throw new ArgumentNullException(nameof(slot))).LogicalColumnId) {
            Slot = slot;
        }

        public CellSlot Slot { get; }
    }

    public sealed class Reuse : RowBuildAssignment {
        public Reuse(
            LogicalColumnId logicalColumnId,
            RecapCellArtifact cell
        ) : base(logicalColumnId) {
            Cell = cell ?? throw new ArgumentNullException(nameof(cell));
        }

        public RecapCellArtifact Cell { get; }
    }
}

/// <summary>
/// Exact immutable Store coordinate for one recipe-row progression assignment.
/// Timeline selection remains an outer-owner responsibility; this value commits
/// the recurrence that the Store can validate without reading Timeline state.
/// </summary>
public sealed record RowViewCoordinate {
    public RowViewCoordinate(
        RefId refId,
        TimelineId timelineId,
        HistoryRowId historyRowId,
        GridBuildRecipeDigest recipeDigest,
        BuildTargetDigest targetDigest,
        HistoryRowId? previousHistoryRowId,
        RowResultId? previousRowResultId,
        bool bootstrapCompleted
    ) {
        if (refId.IsDefault) {
            throw new ArgumentException("RefId must not be default.", nameof(refId));
        }
        RecapGridSyntax.RequireTypedValue(timelineId.Value, 32, nameof(timelineId));
        RecapGridSyntax.RequireTypedValue(historyRowId.Value, 64, nameof(historyRowId));
        RecapGridSyntax.RequireTypedValue(recipeDigest.Value, 64, nameof(recipeDigest));
        RecapGridSyntax.RequireTypedValue(targetDigest.Value, 64, nameof(targetDigest));
        if (previousHistoryRowId is { } previousRow) {
            RecapGridSyntax.RequireTypedValue(
                previousRow.Value,
                64,
                nameof(previousHistoryRowId)
            );
        }
        if (previousRowResultId is { } previousView) {
            RecapGridSyntax.RequireTypedValue(
                previousView.Value,
                32,
                nameof(previousRowResultId)
            );
        }
        if ((previousHistoryRowId is null) != (previousRowResultId is null)) {
            throw new ArgumentException(
                "Previous row and previous view must be present or absent together."
            );
        }
        if (previousHistoryRowId == historyRowId) {
            throw new ArgumentException(
                "A row-view assignment cannot name itself as its predecessor.",
                nameof(previousHistoryRowId)
            );
        }
        RefId = refId;
        TimelineId = timelineId;
        HistoryRowId = historyRowId;
        RecipeDigest = recipeDigest;
        TargetDigest = targetDigest;
        PreviousHistoryRowId = previousHistoryRowId;
        PreviousRowResultId = previousRowResultId;
        BootstrapCompleted = bootstrapCompleted;
    }

    public RefId RefId { get; }
    public TimelineId TimelineId { get; }
    public HistoryRowId HistoryRowId { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public BuildTargetDigest TargetDigest { get; }
    public HistoryRowId? PreviousHistoryRowId { get; }
    public RowResultId? PreviousRowResultId { get; }
    public bool BootstrapCompleted { get; }

    public RowViewAssignmentKey AssignmentKey => new(
        RefId,
        TimelineId,
        RecipeDigest,
        HistoryRowId
    );
}

public sealed record RowViewAssignmentKey {
    public RowViewAssignmentKey(
        RefId refId,
        TimelineId timelineId,
        GridBuildRecipeDigest recipeDigest,
        HistoryRowId historyRowId
    ) {
        if (refId.IsDefault) {
            throw new ArgumentException("RefId must not be default.", nameof(refId));
        }
        RecapGridSyntax.RequireTypedValue(timelineId.Value, 32, nameof(timelineId));
        RecapGridSyntax.RequireTypedValue(recipeDigest.Value, 64, nameof(recipeDigest));
        RecapGridSyntax.RequireTypedValue(historyRowId.Value, 64, nameof(historyRowId));
        RefId = refId;
        TimelineId = timelineId;
        RecipeDigest = recipeDigest;
        HistoryRowId = historyRowId;
    }

    public RefId RefId { get; }
    public TimelineId TimelineId { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public HistoryRowId HistoryRowId { get; }
}

/// <summary>
/// Pure validated build input. It is deliberately not a durable identity or
/// canonical wire owner; WP-04 derives it from frozen Timeline and Control
/// snapshots.
/// </summary>
public sealed class RowBuildSpec {
    private readonly ReadOnlyCollection<RowBuildAssignment> _orderedAssignments;
    private readonly ReadOnlyCollection<MaintainerDefinitionDigest>
        _orderedDefinitionDigests;

    private RowBuildSpec(
        GridBuildRecipe recipe,
        RowViewCoordinate coordinate,
        RowBuildAssignment[] orderedAssignments,
        MaintainerDefinitionDigest[] orderedDefinitionDigests,
        RowWork? work
    ) {
        Recipe = recipe;
        Coordinate = coordinate;
        _orderedAssignments = Array.AsReadOnly(orderedAssignments);
        _orderedDefinitionDigests = Array.AsReadOnly(
            orderedDefinitionDigests
        );
        Work = work;
    }

    public GridBuildRecipe Recipe { get; }
    public RowViewCoordinate Coordinate { get; }
    /// <summary>V5 durable selection backing this spec, when it is a new work-addressed row.</summary>
    public RowWork? Work { get; }
    public RefId RefId => Coordinate.RefId;
    public TimelineId TimelineId => Coordinate.TimelineId;
    public HistoryRowId HistoryRowId => Coordinate.HistoryRowId;
    public GridBuildRecipeDigest RecipeDigest => Coordinate.RecipeDigest;
    public BuildTargetDigest TargetDigest => Coordinate.TargetDigest;
    public HistoryRowId? PreviousHistoryRowId =>
        Coordinate.PreviousHistoryRowId;
    public RowResultId? PreviousRowResultId => Coordinate.PreviousRowResultId;
    public bool BootstrapCompleted => Coordinate.BootstrapCompleted;
    public IReadOnlyList<RowBuildAssignment> OrderedAssignments =>
        _orderedAssignments;

    public static RowBuildSpec CreateFull(
        GridBuildRecipe recipe,
        RowViewCoordinate coordinate,
        IEnumerable<RowBuildAssignment> orderedAssignments,
        RowWork? work = null
    ) {
        if (recipe?.Kind != GridBuildRecipeKind.Full) {
            throw new ArgumentException(
                "CreateFull requires a full recipe.",
                nameof(recipe)
            );
        }
        if (!coordinate.BootstrapCompleted) {
            throw new ArgumentException(
                "A full-recipe row must have completed bootstrap.",
                nameof(coordinate)
            );
        }
        RowBuildAssignment[] assignments = MaterializeAssignments(
            orderedAssignments
        );
        if (assignments.Any(static assignment =>
                assignment is not RowBuildAssignment.Evaluate)) {
            throw new ArgumentException(
                "A full-recipe spec must evaluate every target column.",
                nameof(orderedAssignments)
            );
        }
        return CreateCore(
            recipe,
            coordinate,
            assignments,
            work
        );
    }

    public static RowBuildSpec CreateOverlayBootstrap(
        GridBuildRecipe recipe,
        RowViewCoordinate coordinate,
        IEnumerable<RowBuildAssignment> orderedAssignments,
        RowWork? work = null
    ) {
        if (recipe?.Kind != GridBuildRecipeKind.Overlay) {
            throw new ArgumentException(
                "CreateOverlayBootstrap requires an overlay recipe.",
                nameof(recipe)
            );
        }
        bool reachesBootstrap = recipe.BootstrapThroughRowId
            == coordinate.HistoryRowId;
        if (coordinate.BootstrapCompleted != reachesBootstrap) {
            throw new ArgumentException(
                "An overlay-bootstrap row completes bootstrap exactly at its bootstrap row.",
                nameof(coordinate)
            );
        }
        RowBuildAssignment[] assignments = MaterializeAssignments(
            orderedAssignments
        );
        HashSet<LogicalColumnId> recomputed = recipe.RecomputedColumns
            .ToHashSet();
        foreach (RowBuildAssignment assignment in assignments) {
            bool mustEvaluate = recomputed.Contains(
                assignment.LogicalColumnId
            );
            if (mustEvaluate
                    && assignment is not RowBuildAssignment.Evaluate
                || !mustEvaluate
                    && assignment is not RowBuildAssignment.Reuse) {
                throw new ArgumentException(
                    "Overlay bootstrap must evaluate exactly recomputed columns and reuse every other target column.",
                    nameof(orderedAssignments)
                );
            }
        }
        return CreateCore(
            recipe,
            coordinate,
            assignments,
            work
        );
    }

    public static RowBuildSpec CreateNormal(
        GridBuildRecipe recipe,
        RowViewCoordinate coordinate,
        IEnumerable<RowBuildAssignment> orderedAssignments,
        RowWork? work = null
    ) {
        ArgumentNullException.ThrowIfNull(recipe);
        if (!coordinate.BootstrapCompleted) {
            throw new ArgumentException(
                "A normal row requires completed bootstrap.",
                nameof(coordinate)
            );
        }
        RowBuildAssignment[] assignments = MaterializeAssignments(
            orderedAssignments
        );
        if (assignments.Any(static assignment =>
                assignment is not RowBuildAssignment.Evaluate)) {
            throw new ArgumentException(
                "A normal spec must evaluate every target column.",
                nameof(orderedAssignments)
            );
        }
        return CreateCore(
            recipe,
            coordinate,
            assignments,
            work
        );
    }

    private static RowBuildSpec CreateCore(
        GridBuildRecipe recipe,
        RowViewCoordinate coordinate,
        RowBuildAssignment[] assignments,
        RowWork? work
    ) {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(coordinate);
        BuildTarget actualTarget = work?.ProducerTarget ?? recipe.Target;
        if (coordinate.TimelineId != recipe.TimelineId
            || coordinate.RecipeDigest != recipe.Digest
            || coordinate.TargetDigest != actualTarget.Digest) {
            throw new ArgumentException(
                "The row coordinate must bind the exact recipe and target.",
                nameof(coordinate)
            );
        }
        BuildTargetColumn[] target = actualTarget.OrderedColumns.ToArray();
        if (assignments.Length != target.Length
            || assignments.Any(static value => value is null)
            || !assignments.Select(static value => value.LogicalColumnId)
                .SequenceEqual(target.Select(static value =>
                    value.LogicalColumnId))) {
            throw new ArgumentException(
                "Assignments must exactly cover the target in target order.",
                nameof(assignments)
            );
        }
        for (int index = 0; index < assignments.Length; index++) {
            switch (assignments[index]) {
                case RowBuildAssignment.Evaluate evaluate
                    when evaluate.Slot.RecipeDigest == recipe.Digest
                        && evaluate.Slot.HistoryRowId == coordinate.HistoryRowId
                        && evaluate.Slot.LogicalColumnId == target[index].LogicalColumnId:
                    break;
                case RowBuildAssignment.Reuse reuse
                    when reuse.Cell.LogicalColumnId == target[index].LogicalColumnId
                        && reuse.Cell.DefinitionDigest == target[index].DefinitionDigest
                        && reuse.Cell.Slot.HistoryRowId == coordinate.HistoryRowId:
                    break;
                default:
                    throw new ArgumentException(
                        "Every assignment must use the exact current row, column, definition, and applicable prior input.",
                        nameof(assignments)
                    );
            }
        }
        if (work is not null) {
            if (work.Key.RefId != coordinate.RefId
                || work.Key.TimelineId != coordinate.TimelineId
                || work.Key.RootRecipeDigest != coordinate.RecipeDigest
                || work.Key.HistoryRowId != coordinate.HistoryRowId
                || work.ProducerTarget.Digest != coordinate.TargetDigest
                || work.PreviousHistoryRowId != coordinate.PreviousHistoryRowId
                || work.PreviousRowResultId != coordinate.PreviousRowResultId
                || work.OrderedAssignments.Count != assignments.Length) {
                throw new ArgumentException("The RowWork differs from the exact row coordinate.", nameof(work));
            }
            for (int index = 0; index < assignments.Length; index++) {
                RowWorkAssignment frozen = work.OrderedAssignments[index];
                bool exact = assignments[index] switch {
                    RowBuildAssignment.Evaluate evaluate => frozen.IsEvaluate
                        && evaluate.Slot.WorkId == work.WorkId,
                    RowBuildAssignment.Reuse reuse => frozen.ReusedCellId == reuse.Cell.Id,
                    _ => false
                };
                if (!exact || frozen.LogicalColumnId != assignments[index].LogicalColumnId) {
                    throw new ArgumentException("Assignments differ from the durable RowWork.", nameof(work));
                }
            }
        }
        return new RowBuildSpec(
            recipe,
            coordinate,
            assignments,
            target.Select(static column => column.DefinitionDigest).ToArray(),
            work
        );
    }

    private static RowBuildAssignment[] MaterializeAssignments(
        IEnumerable<RowBuildAssignment> assignments
    ) => RecapGridSyntax.MaterializeBounded(
        assignments,
        RecapGridLimits.MaximumColumnCount,
        nameof(assignments)
    );

    internal MaintainerDefinitionDigest DefinitionAt(int index)
        => _orderedDefinitionDigests[index];

}

public enum RecapCellOutcome {
    Updated,
    KeepUnchanged
}
