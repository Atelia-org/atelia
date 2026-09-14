using System.Collections.ObjectModel;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid;

public sealed record CellSlot {
    public CellSlot(GridBuildRecipeDigest recipeDigest, HistoryRowId historyRowId, LogicalColumnId logicalColumnId) {
        RecapGridSyntax.RequireTypedValue(recipeDigest.Value, 64, nameof(recipeDigest));
        RecapGridSyntax.RequireTypedValue(historyRowId.Value, 64, nameof(historyRowId));
        RecapGridSyntax.RequireIdentifier(logicalColumnId.Value, nameof(logicalColumnId));
        RecipeDigest = recipeDigest;
        HistoryRowId = historyRowId;
        LogicalColumnId = logicalColumnId;
    }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public HistoryRowId HistoryRowId { get; }
    public LogicalColumnId LogicalColumnId { get; }
}

internal sealed class RecapCellDraft {
    private RecapCellDraft(CellSlot slot, MaintainerDefinitionDigest definitionDigest, RecapCellOutcome outcome, string content) {
        Slot = slot;
        DefinitionDigest = definitionDigest;
        Outcome = outcome;
        Content = content;
    }
    public CellSlot Slot { get; }
    public LogicalColumnId LogicalColumnId => Slot.LogicalColumnId;
    public MaintainerDefinitionDigest DefinitionDigest { get; }
    public RecapCellOutcome Outcome { get; }
    public string Content { get; }
    public static RecapCellDraft Create(CellSlot slot, MaintainerDefinitionDigest definitionDigest,
        RecapCellOutcome outcome, string content, int maxContentUtf8Bytes) {
        ArgumentNullException.ThrowIfNull(slot);
        RecapGridSyntax.RequireTypedValue(definitionDigest.Value, 64, nameof(definitionDigest));
        return new RecapCellDraft(slot, definitionDigest, outcome,
            RecapCellArtifact.ValidateContent(outcome, content, maxContentUtf8Bytes));
    }
}

/// <summary>An immutable stored value. Constructing its representation does not publish or allocate an ID.</summary>
public sealed record RecapCellArtifact {
    public RecapCellArtifact(CellId id, CellSlot slot, MaintainerDefinitionDigest definitionDigest,
        RecapCellOutcome outcome, string content, int maxContentUtf8Bytes = RecapGridLimits.MaximumContentUtf8Bytes) {
        RecapGridSyntax.RequireTypedValue(id.Value, 32, nameof(id));
        ArgumentNullException.ThrowIfNull(slot);
        RecapGridSyntax.RequireTypedValue(definitionDigest.Value, 64, nameof(definitionDigest));
        Id = id;
        Slot = slot;
        DefinitionDigest = definitionDigest;
        Outcome = outcome;
        Content = ValidateContent(outcome, content, maxContentUtf8Bytes);
    }
    public CellId Id { get; }
    public CellSlot Slot { get; }
    public LogicalColumnId LogicalColumnId => Slot.LogicalColumnId;
    public MaintainerDefinitionDigest DefinitionDigest { get; }
    public RecapCellOutcome Outcome { get; }
    public string Content { get; }

    internal static string ValidateContent(RecapCellOutcome outcome, string content, int maximum) {
        if (!Enum.IsDefined(outcome)) { throw new ArgumentOutOfRangeException(nameof(outcome)); }
        if (maximum is < 1 or > RecapGridLimits.MaximumContentUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }
        return RecapGridSyntax.RequireText(content, maximum, nameof(content), allowEmpty: true);
    }
}

public sealed record RecapRowViewCell {
    public RecapRowViewCell(LogicalColumnId logicalColumnId, MaintainerDefinitionDigest definitionDigest, CellId cellId) {
        RecapGridSyntax.RequireIdentifier(logicalColumnId.Value, nameof(logicalColumnId));
        RecapGridSyntax.RequireTypedValue(definitionDigest.Value, 64, nameof(definitionDigest));
        RecapGridSyntax.RequireTypedValue(cellId.Value, 32, nameof(cellId));
        LogicalColumnId = logicalColumnId;
        DefinitionDigest = definitionDigest;
        CellId = cellId;
    }
    public LogicalColumnId LogicalColumnId { get; }
    public MaintainerDefinitionDigest DefinitionDigest { get; }
    public CellId CellId { get; }
}

public sealed class RecapRowView {
    private readonly ReadOnlyCollection<RecapRowViewCell> _orderedCells;
    internal RecapRowView(RowResultId id, RowViewCoordinate coordinate, IEnumerable<RecapRowViewCell> orderedCells) {
        RecapGridSyntax.RequireTypedValue(id.Value, 32, nameof(id));
        ArgumentNullException.ThrowIfNull(coordinate);
        RecapRowViewCell[] cells = RecapGridSyntax.MaterializeBounded(orderedCells,
            RecapGridLimits.MaximumColumnCount, nameof(orderedCells));
        if (cells.Any(static value => value is null)
            || cells.Select(static value => value.LogicalColumnId).Distinct().Count() != cells.Length) {
            throw new ArgumentException("Row members must be non-null and logically unique.", nameof(orderedCells));
        }
        if (coordinate.PreviousRowResultId == id) {
            throw new ArgumentException("A row result cannot be its own predecessor.", nameof(coordinate));
        }
        Id = id;
        Coordinate = coordinate;
        _orderedCells = Array.AsReadOnly(cells);
    }
    public RowResultId Id { get; }
    public RowViewCoordinate Coordinate { get; }
    public RefId RefId => Coordinate.RefId;
    public TimelineId TimelineId => Coordinate.TimelineId;
    public HistoryRowId HistoryRowId => Coordinate.HistoryRowId;
    public GridBuildRecipeDigest RecipeDigest => Coordinate.RecipeDigest;
    public BuildTargetDigest TargetDigest => Coordinate.TargetDigest;
    public HistoryRowId? PreviousHistoryRowId => Coordinate.PreviousHistoryRowId;
    public RowResultId? PreviousRowResultId => Coordinate.PreviousRowResultId;
    public bool BootstrapCompleted => Coordinate.BootstrapCompleted;
    public IReadOnlyList<RecapRowViewCell> OrderedCells => _orderedCells;

    public static RecapRowView Create(RowResultId id, RowBuildSpec spec, IReadOnlyList<RecapCellArtifact> selectedCells) {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(selectedCells);
        if (selectedCells.Count != spec.OrderedAssignments.Count) {
            throw new ArgumentException("Row cells must exactly cover the build spec.", nameof(selectedCells));
        }
        var members = new RecapRowViewCell[selectedCells.Count];
        for (int index = 0; index < members.Length; index++) {
            RecapCellArtifact cell = selectedCells[index];
            RowBuildAssignment assignment = spec.OrderedAssignments[index];
            bool exact = cell is not null && (assignment switch {
                RowBuildAssignment.Evaluate evaluate => cell.Slot == evaluate.Slot,
                RowBuildAssignment.Reuse reuse => cell == reuse.Cell,
                _ => false
            });
            if (!exact || cell!.LogicalColumnId != assignment.LogicalColumnId
                || cell.DefinitionDigest != spec.DefinitionAt(index)
                || cell.Slot.HistoryRowId != spec.HistoryRowId) {
                throw new ArgumentException("A row cell differs from its exact assignment.", nameof(selectedCells));
            }
            members[index] = new RecapRowViewCell(cell.LogicalColumnId, cell.DefinitionDigest, cell.Id);
        }
        return new RecapRowView(id, spec.Coordinate, members);
    }

    internal bool HasSameAssignment(RecapRowView other) => Coordinate == other.Coordinate
        && OrderedCells.SequenceEqual(other.OrderedCells);
}

public sealed record FulfilledViewKey {
    internal FulfilledViewKey(RefId refId, TimelineId timelineId, long timelineHeadGeneration,
        HistoryRowId throughRowId, GridBuildRecipeDigest recipeDigest) {
        if (refId.IsDefault) { throw new ArgumentException("Ref must not be default.", nameof(refId)); }
        RecapGridSyntax.RequireTypedValue(timelineId.Value, 32, nameof(timelineId));
        RecapGridSyntax.RequireTypedValue(throughRowId.Value, 64, nameof(throughRowId));
        RecapGridSyntax.RequireTypedValue(recipeDigest.Value, 64, nameof(recipeDigest));
        if (timelineHeadGeneration < 0) { throw new ArgumentOutOfRangeException(nameof(timelineHeadGeneration)); }
        RefId = refId;
        TimelineId = timelineId;
        TimelineHeadGeneration = timelineHeadGeneration;
        ThroughRowId = throughRowId;
        RecipeDigest = recipeDigest;
    }
    public RefId RefId { get; }
    public TimelineId TimelineId { get; }
    public long TimelineHeadGeneration { get; }
    public HistoryRowId ThroughRowId { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public static FulfilledViewKey Create(RefId refId, TimelineHeadRef timelineHead,
        HistoryRowId throughRowId, GridBuildRecipe recipe) {
        ArgumentNullException.ThrowIfNull(timelineHead);
        ArgumentNullException.ThrowIfNull(recipe);
        if (timelineHead.RefId != refId || timelineHead.TimelineId != recipe.TimelineId) {
            throw new ArgumentException("The fulfillment must bind one Ref and Timeline.");
        }
        return new FulfilledViewKey(refId, timelineHead.TimelineId, timelineHead.Generation,
            throughRowId, recipe.Digest);
    }
}
