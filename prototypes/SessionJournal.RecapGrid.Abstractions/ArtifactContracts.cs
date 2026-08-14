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
        public Evaluate(
            LogicalColumnId logicalColumnId,
            EvaluationKey evaluationKey
        ) : base(logicalColumnId) {
            EvaluationKey = evaluationKey
                ?? throw new ArgumentNullException(nameof(evaluationKey));
        }

        public EvaluationKey EvaluationKey { get; }
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
public sealed class RowViewCoordinate {
    public RowViewCoordinate(
        RefId refId,
        TimelineId timelineId,
        HistoryRowId historyRowId,
        HistorySegmentDescriptorDigest historySegmentDigest,
        GridBuildRecipeDigest recipeDigest,
        BuildTargetDigest targetDigest,
        HistoryRowId? previousHistoryRowId,
        RowViewDigest? previousViewDigest,
        bool bootstrapCompleted
    ) {
        if (refId.IsDefault) {
            throw new ArgumentException("RefId must not be default.", nameof(refId));
        }
        RecapGridSyntax.RequireTypedValue(timelineId.Value, 32, nameof(timelineId));
        RecapGridSyntax.RequireTypedValue(historyRowId.Value, 64, nameof(historyRowId));
        RecapGridSyntax.RequireTypedValue(
            historySegmentDigest.Value,
            64,
            nameof(historySegmentDigest)
        );
        RecapGridSyntax.RequireTypedValue(recipeDigest.Value, 64, nameof(recipeDigest));
        RecapGridSyntax.RequireTypedValue(targetDigest.Value, 64, nameof(targetDigest));
        if (previousHistoryRowId is { } previousRow) {
            RecapGridSyntax.RequireTypedValue(
                previousRow.Value,
                64,
                nameof(previousHistoryRowId)
            );
        }
        if (previousViewDigest is { } previousView) {
            RecapGridSyntax.RequireTypedValue(
                previousView.Value,
                64,
                nameof(previousViewDigest)
            );
        }
        if ((previousHistoryRowId is null) != (previousViewDigest is null)) {
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
        HistorySegmentDigest = historySegmentDigest;
        RecipeDigest = recipeDigest;
        TargetDigest = targetDigest;
        PreviousHistoryRowId = previousHistoryRowId;
        PreviousViewDigest = previousViewDigest;
        BootstrapCompleted = bootstrapCompleted;
    }

    public RefId RefId { get; }
    public TimelineId TimelineId { get; }
    public HistoryRowId HistoryRowId { get; }
    public HistorySegmentDescriptorDigest HistorySegmentDigest { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }
    public BuildTargetDigest TargetDigest { get; }
    public HistoryRowId? PreviousHistoryRowId { get; }
    public RowViewDigest? PreviousViewDigest { get; }
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
        PriorInputReference priorInput,
        RowBuildAssignment[] orderedAssignments,
        MaintainerDefinitionDigest[] orderedDefinitionDigests
    ) {
        Recipe = recipe;
        Coordinate = coordinate;
        PriorInput = priorInput;
        _orderedAssignments = Array.AsReadOnly(orderedAssignments);
        _orderedDefinitionDigests = Array.AsReadOnly(
            orderedDefinitionDigests
        );
    }

    public GridBuildRecipe Recipe { get; }
    public RowViewCoordinate Coordinate { get; }
    public RefId RefId => Coordinate.RefId;
    public TimelineId TimelineId => Coordinate.TimelineId;
    public HistoryRowId HistoryRowId => Coordinate.HistoryRowId;
    public HistorySegmentDescriptorDigest HistorySegmentDigest =>
        Coordinate.HistorySegmentDigest;
    public GridBuildRecipeDigest RecipeDigest => Coordinate.RecipeDigest;
    public BuildTargetDigest TargetDigest => Coordinate.TargetDigest;
    public HistoryRowId? PreviousHistoryRowId =>
        Coordinate.PreviousHistoryRowId;
    public RowViewDigest? PreviousViewDigest => Coordinate.PreviousViewDigest;
    public bool BootstrapCompleted => Coordinate.BootstrapCompleted;
    public PriorInputReference PriorInput { get; }
    public IReadOnlyList<RowBuildAssignment> OrderedAssignments =>
        _orderedAssignments;

    public static RowBuildSpec CreateFull(
        GridBuildRecipe recipe,
        RowViewCoordinate coordinate,
        PriorInputReference priorInput,
        IEnumerable<RowBuildAssignment> orderedAssignments
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
            priorInput,
            assignments
        );
    }

    public static RowBuildSpec CreateOverlayBootstrap(
        GridBuildRecipe recipe,
        RowViewCoordinate coordinate,
        PriorInputReference priorInput,
        IEnumerable<RowBuildAssignment> orderedAssignments
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
            priorInput,
            assignments
        );
    }

    public static RowBuildSpec CreateNormal(
        GridBuildRecipe recipe,
        RowViewCoordinate coordinate,
        PriorInputReference priorInput,
        IEnumerable<RowBuildAssignment> orderedAssignments
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
            priorInput,
            assignments
        );
    }

    private static RowBuildSpec CreateCore(
        GridBuildRecipe recipe,
        RowViewCoordinate coordinate,
        PriorInputReference priorInput,
        RowBuildAssignment[] assignments
    ) {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(coordinate);
        ArgumentNullException.ThrowIfNull(priorInput);
        if (coordinate.TimelineId != recipe.TimelineId
            || coordinate.RecipeDigest != recipe.Digest
            || coordinate.TargetDigest != recipe.Target.Digest) {
            throw new ArgumentException(
                "The row coordinate must bind the exact recipe and target.",
                nameof(coordinate)
            );
        }
        if ((coordinate.PreviousViewDigest is null)
            != (priorInput is PriorInputReference.FirstRow)) {
            throw new ArgumentException(
                "First-row prior input must exactly match absent predecessor provenance.",
                nameof(priorInput)
            );
        }
        BuildTargetColumn[] target = recipe.Target.OrderedColumns.ToArray();
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
                    when evaluate.EvaluationKey.DefinitionDigest
                            == target[index].DefinitionDigest
                        && evaluate.EvaluationKey.HistorySegmentDigest
                            == coordinate.HistorySegmentDigest
                        && SamePrior(
                            evaluate.EvaluationKey.PriorInput,
                            priorInput
                        ):
                    break;
                case RowBuildAssignment.Reuse reuse
                    when reuse.Cell.LogicalColumnId
                            == target[index].LogicalColumnId
                        && reuse.Cell.DefinitionDigest
                            == target[index].DefinitionDigest
                        && reuse.Cell.EvaluationKey.HistorySegmentDigest
                            == coordinate.HistorySegmentDigest:
                    break;
                default:
                    throw new ArgumentException(
                        "Every assignment must use the exact current row, column, definition, and applicable prior input.",
                        nameof(assignments)
                    );
            }
        }
        return new RowBuildSpec(
            recipe,
            coordinate,
            priorInput,
            assignments,
            target.Select(static column => column.DefinitionDigest).ToArray()
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

    private static bool SamePrior(
        PriorInputReference left,
        PriorInputReference right
    ) => (left, right) switch {
        (PriorInputReference.FirstRow, PriorInputReference.FirstRow) => true,
        (PriorInputReference.Projection first,
            PriorInputReference.Projection second)
            => first.Digest == second.Digest,
        _ => false
    };
}

public enum RecapCellOutcome {
    Updated,
    KeepUnchanged
}

/// <summary>
/// Immutable, content-addressed output of one pure derived evaluation. A Cell
/// is not proof that an external side effect happened and must remain safe to
/// recompute after cancellation, crash, or an indeterminate local commit.
/// </summary>
public sealed class RecapCellArtifact {
    private readonly byte[] _canonicalBytes;

    private RecapCellArtifact(
        LogicalColumnId logicalColumnId,
        MaintainerDefinitionDigest definitionDigest,
        EvaluationKey evaluationKey,
        RecapCellOutcome outcome,
        string content,
        ContentDigest contentDigest,
        CellDigest cellDigest,
        byte[] canonicalBytes
    ) {
        LogicalColumnId = logicalColumnId;
        DefinitionDigest = definitionDigest;
        EvaluationKey = evaluationKey;
        Outcome = outcome;
        Content = content;
        ContentDigest = contentDigest;
        CellDigest = cellDigest;
        _canonicalBytes = canonicalBytes;
    }

    public LogicalColumnId LogicalColumnId { get; }
    public MaintainerDefinitionDigest DefinitionDigest { get; }
    public EvaluationKey EvaluationKey { get; }
    public RecapCellOutcome Outcome { get; }
    public string Content { get; }
    public ContentDigest ContentDigest { get; }
    public CellDigest CellDigest { get; }

    public static RecapCellArtifact Create(
        LogicalColumnId logicalColumnId,
        MaintainerDefinitionDigest definitionDigest,
        EvaluationKey evaluationKey,
        RecapCellOutcome outcome,
        string content,
        int maxContentUtf8Bytes
    ) {
        ArgumentNullException.ThrowIfNull(evaluationKey);
        RecapGridSyntax.RequireIdentifier(
            logicalColumnId.Value
                ?? throw new ArgumentException(
                    "LogicalColumnId must not be default.",
                    nameof(logicalColumnId)
                ),
            nameof(logicalColumnId)
        );
        RecapGridSyntax.RequireTypedValue(
            definitionDigest.Value,
            64,
            nameof(definitionDigest)
        );
        if (definitionDigest != evaluationKey.DefinitionDigest) {
            throw new ArgumentException(
                "The cell definition differs from its evaluation key.",
                nameof(definitionDigest)
            );
        }
        if (!Enum.IsDefined(outcome)) {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        if (maxContentUtf8Bytes is < 1
            or > RecapGridLimits.MaximumContentUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(maxContentUtf8Bytes));
        }
        content = RecapGridSyntax.RequireText(
            content,
            maxContentUtf8Bytes,
            nameof(content),
            allowEmpty: true
        );
        ContentDigest contentDigest = new(RecapGridHash.Compute(
            "atelia.recap-grid.content.v1",
            System.Text.Encoding.UTF8.GetBytes(content)
        ));
        RecapCellArtifactBodyDto body = new(
            1,
            logicalColumnId.Value,
            definitionDigest.Value,
            evaluationKey.ToCanonicalBytes(),
            OutcomeToken(outcome),
            content,
            contentDigest.Value
        );
        CellDigest cellDigest = new(RecapGridHash.Compute(
            "atelia.recap-grid.cell.v1",
            RecapGridCanonical.Encode(body)
        ));
        byte[] canonical = RecapGridCanonical.Encode(new RecapCellArtifactDto(
            1,
            cellDigest.Value,
            body.LogicalColumnId,
            body.DefinitionDigest,
            body.EvaluationKey,
            body.Outcome,
            body.Content,
            body.ContentDigest
        ));
        if (canonical.Length
            > RecapGridLimits.MaximumCellArtifactCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(content));
        }
        return new RecapCellArtifact(
            logicalColumnId,
            definitionDigest,
            evaluationKey,
            outcome,
            content,
            contentDigest,
            cellDigest,
            canonical
        );
    }

    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public static RecapCellArtifact DecodeCanonical(ReadOnlySpan<byte> bytes) {
        try {
            return DecodeCanonicalCore(bytes);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NullReferenceException) {
            throw new InvalidDataException(
                "The cell canonical value is invalid.",
                exception
            );
        }
    }

    private static RecapCellArtifact DecodeCanonicalCore(
        ReadOnlySpan<byte> bytes
    ) {
        RecapCellArtifactDto dto = RecapGridCanonical
            .DecodeExact<RecapCellArtifactDto>(
                bytes,
                RecapGridLimits.MaximumCellArtifactCanonicalUtf8Bytes,
                nameof(bytes)
            );
        if (dto.SchemaVersion != 1 || dto.EvaluationKey is null) {
            throw new InvalidDataException("The cell schema is invalid.");
        }
        RecapCellArtifact value = Create(
            new LogicalColumnId(dto.LogicalColumnId),
            new MaintainerDefinitionDigest(dto.DefinitionDigest),
            EvaluationKey.DecodeCanonical(dto.EvaluationKey),
            ParseOutcome(dto.Outcome),
            dto.Content,
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        if (!string.Equals(value.ContentDigest.Value, dto.ContentDigest, StringComparison.Ordinal)
            || !string.Equals(value.CellDigest.Value, dto.CellDigest, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The cell digests do not match its canonical content."
            );
        }
        return value;
    }

    private static string OutcomeToken(RecapCellOutcome value) => value switch {
        RecapCellOutcome.Updated => "updated",
        RecapCellOutcome.KeepUnchanged => "keep-unchanged",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static RecapCellOutcome ParseOutcome(string value) => value switch {
        "updated" => RecapCellOutcome.Updated,
        "keep-unchanged" => RecapCellOutcome.KeepUnchanged,
        _ => throw new InvalidDataException("The cell outcome is invalid.")
    };
}

public sealed class RecapRowViewCell {
    public RecapRowViewCell(
        LogicalColumnId logicalColumnId,
        MaintainerDefinitionDigest definitionDigest,
        CellDigest cellDigest
    ) {
        RecapGridSyntax.RequireIdentifier(
            logicalColumnId.Value
                ?? throw new ArgumentException(
                    "LogicalColumnId must not be default.",
                    nameof(logicalColumnId)
                ),
            nameof(logicalColumnId)
        );
        RecapGridSyntax.RequireTypedValue(definitionDigest.Value, 64, nameof(definitionDigest));
        RecapGridSyntax.RequireTypedValue(cellDigest.Value, 64, nameof(cellDigest));
        LogicalColumnId = logicalColumnId;
        DefinitionDigest = definitionDigest;
        CellDigest = cellDigest;
    }

    public LogicalColumnId LogicalColumnId { get; }
    public MaintainerDefinitionDigest DefinitionDigest { get; }
    public CellDigest CellDigest { get; }
}

public sealed class RecapRowView {
    private readonly ReadOnlyCollection<RecapRowViewCell> _orderedCells;
    private readonly byte[] _canonicalBytes;

    private RecapRowView(
        RowViewCoordinate coordinate,
        RecapRowViewCell[] orderedCells,
        RowViewDigest digest,
        byte[] canonicalBytes
    ) {
        Coordinate = coordinate;
        _orderedCells = Array.AsReadOnly(orderedCells);
        Digest = digest;
        _canonicalBytes = canonicalBytes;
    }

    public RowViewCoordinate Coordinate { get; }
    public RefId RefId => Coordinate.RefId;
    public TimelineId TimelineId => Coordinate.TimelineId;
    public HistoryRowId HistoryRowId => Coordinate.HistoryRowId;
    public HistorySegmentDescriptorDigest RowDescriptorDigest =>
        Coordinate.HistorySegmentDigest;
    public GridBuildRecipeDigest RecipeDigest => Coordinate.RecipeDigest;
    public BuildTargetDigest TargetDigest => Coordinate.TargetDigest;
    public HistoryRowId? PreviousHistoryRowId =>
        Coordinate.PreviousHistoryRowId;
    public RowViewDigest? PreviousViewDigest => Coordinate.PreviousViewDigest;
    public bool BootstrapCompleted => Coordinate.BootstrapCompleted;
    public IReadOnlyList<RecapRowViewCell> OrderedCells => _orderedCells;
    public RowViewDigest Digest { get; }

    public static RecapRowView Create(
        RowBuildSpec spec,
        IEnumerable<RecapCellArtifact> selectedCells
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        RecapCellArtifact[] selected = RecapGridSyntax.MaterializeBounded(
            selectedCells,
            RecapGridLimits.MaximumColumnCount,
            nameof(selectedCells)
        );
        if (selected.Length != spec.OrderedAssignments.Count
            || selected.Any(static value => value is null)
            || !selected.Select(static value => value.LogicalColumnId)
                .SequenceEqual(spec.OrderedAssignments.Select(static value =>
                    value.LogicalColumnId))) {
            throw new ArgumentException(
                "Row-view cells must exactly cover the build spec.",
                nameof(selectedCells)
            );
        }
        var cells = new RecapRowViewCell[selected.Length];
        for (int index = 0; index < selected.Length; index++) {
            RowBuildAssignment assignment = spec.OrderedAssignments[index];
            RecapCellArtifact cell = selected[index];
            bool exactAssignment = assignment switch {
                RowBuildAssignment.Evaluate evaluate
                    => cell.EvaluationKey.Digest
                        == evaluate.EvaluationKey.Digest,
                RowBuildAssignment.Reuse reuse
                    => cell.CellDigest == reuse.Cell.CellDigest
                        && cell.ToCanonicalBytes().SequenceEqual(
                            reuse.Cell.ToCanonicalBytes()
                        ),
                _ => false
            };
            if (cell.DefinitionDigest != spec.DefinitionAt(index)
                || cell.EvaluationKey.HistorySegmentDigest
                    != spec.HistorySegmentDigest
                || !exactAssignment) {
                throw new ArgumentException(
                    "A row-view cell differs from its exact assignment.",
                    nameof(selectedCells)
                );
            }
            cells[index] = new RecapRowViewCell(
                cell.LogicalColumnId,
                cell.DefinitionDigest,
                cell.CellDigest
            );
        }
        RecapRowViewBodyDto body = new(
            2,
            spec.RefId.Packed,
            spec.TimelineId.Value,
            spec.HistoryRowId.Value,
            spec.HistorySegmentDigest.Value,
            spec.RecipeDigest.Value,
            spec.TargetDigest.Value,
            spec.PreviousHistoryRowId?.Value,
            spec.PreviousViewDigest?.Value,
            spec.BootstrapCompleted,
            cells.Select(static cell => new RecapRowViewCellDto(
                cell.LogicalColumnId.Value,
                cell.DefinitionDigest.Value,
                cell.CellDigest.Value
            )).ToArray()
        );
        RowViewDigest digest = new(RecapGridHash.Compute(
            "atelia.recap-grid.row-view.v2",
            RecapGridCanonical.Encode(body)
        ));
        byte[] canonical = RecapGridCanonical.Encode(new RecapRowViewDto(
            2,
            digest.Value,
            body.RefId,
            body.TimelineId,
            body.HistoryRowId,
            body.RowDescriptorDigest,
            body.RecipeDigest,
            body.TargetDigest,
            body.PreviousHistoryRowId,
            body.PreviousViewDigest,
            body.BootstrapCompleted,
            body.OrderedCells
        ));
        if (canonical.Length > RecapGridLimits.MaximumRowViewCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(selectedCells));
        }
        return new RecapRowView(
            spec.Coordinate,
            cells,
            digest,
            canonical
        );
    }

    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public static RecapRowView DecodeCanonical(ReadOnlySpan<byte> bytes) {
        try {
            return DecodeCanonicalCore(bytes);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NullReferenceException) {
            throw new InvalidDataException(
                "The row-view canonical value is invalid.",
                exception
            );
        }
    }

    public static RecapRowView DecodeCanonical(
        RowBuildSpec spec,
        IEnumerable<RecapCellArtifact> selectedCells,
        ReadOnlySpan<byte> bytes
    ) {
        try {
            return DecodeCanonicalCore(spec, selectedCells, bytes);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NullReferenceException) {
            throw new InvalidDataException(
                "The row-view canonical value is invalid.",
                exception
            );
        }
    }

    private static RecapRowView DecodeCanonicalCore(
        RowBuildSpec spec,
        IEnumerable<RecapCellArtifact> selectedCells,
        ReadOnlySpan<byte> bytes
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        RecapRowView decoded = DecodeCanonicalCore(bytes);
        RecapRowView value = Create(spec, selectedCells);
        if (!decoded.ToCanonicalBytes().SequenceEqual(
                value.ToCanonicalBytes())) {
            throw new InvalidDataException(
                "The row view differs from its exact build spec or cells."
            );
        }
        return decoded;
    }

    private static RecapRowView DecodeCanonicalCore(
        ReadOnlySpan<byte> bytes
    ) {
        RecapRowViewDto dto = RecapGridCanonical.DecodeExact<RecapRowViewDto>(
            bytes,
            RecapGridLimits.MaximumRowViewCanonicalUtf8Bytes,
            nameof(bytes)
        );
        if (dto.SchemaVersion != 2 || dto.OrderedCells is null
            || dto.OrderedCells.Length > RecapGridLimits.MaximumColumnCount) {
            throw new InvalidDataException(
                "The row-view schema or manifest is invalid."
            );
        }
        RecapRowViewCell[] cells = dto.OrderedCells.Select(static cell =>
            new RecapRowViewCell(
                new LogicalColumnId(cell.LogicalColumnId),
                new MaintainerDefinitionDigest(cell.DefinitionDigest),
                new CellDigest(cell.CellDigest)
            )).ToArray();
        if (cells.Select(static cell => cell.LogicalColumnId)
            .Distinct().Count() != cells.Length) {
            throw new InvalidDataException(
                "The row-view manifest contains duplicate columns."
            );
        }
        var refId = new RefId(dto.RefId);
        var timelineId = new TimelineId(dto.TimelineId);
        var historyRowId = new HistoryRowId(dto.HistoryRowId);
        var rowDescriptorDigest = new HistorySegmentDescriptorDigest(
            dto.RowDescriptorDigest
        );
        var recipeDigest = new GridBuildRecipeDigest(dto.RecipeDigest);
        var targetDigest = new BuildTargetDigest(dto.TargetDigest);
        RowViewDigest? previousViewDigest = dto.PreviousViewDigest is null
            ? null
            : new RowViewDigest(dto.PreviousViewDigest);
        HistoryRowId? previousHistoryRowId = dto.PreviousHistoryRowId is null
            ? null
            : new HistoryRowId(dto.PreviousHistoryRowId);
        var coordinate = new RowViewCoordinate(
            refId,
            timelineId,
            historyRowId,
            rowDescriptorDigest,
            recipeDigest,
            targetDigest,
            previousHistoryRowId,
            previousViewDigest,
            dto.BootstrapCompleted
        );
        var body = new RecapRowViewBodyDto(
            2,
            refId.Packed,
            timelineId.Value,
            historyRowId.Value,
            rowDescriptorDigest.Value,
            recipeDigest.Value,
            targetDigest.Value,
            previousHistoryRowId?.Value,
            previousViewDigest?.Value,
            dto.BootstrapCompleted,
            dto.OrderedCells
        );
        RowViewDigest digest = new(RecapGridHash.Compute(
            "atelia.recap-grid.row-view.v2",
            RecapGridCanonical.Encode(body)
        ));
        if (!string.Equals(
                digest.Value,
                dto.Digest,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The row-view digest does not match its body."
            );
        }
        return new RecapRowView(
            coordinate,
            cells,
            digest,
            bytes.ToArray()
        );
    }
}

public sealed class FulfilledViewKey {
    private readonly byte[] _canonicalBytes;

    private FulfilledViewKey(
        RefId refId,
        TimelineId timelineId,
        long timelineHeadGeneration,
        HistorySegmentDescriptorDigest throughRowDescriptorDigest,
        GridBuildRecipeDigest recipeDigest,
        byte[] canonicalBytes
    ) {
        RefId = refId;
        TimelineId = timelineId;
        TimelineHeadGeneration = timelineHeadGeneration;
        ThroughRowDescriptorDigest = throughRowDescriptorDigest;
        RecipeDigest = recipeDigest;
        _canonicalBytes = canonicalBytes;
    }

    public RefId RefId { get; }
    public TimelineId TimelineId { get; }
    public long TimelineHeadGeneration { get; }
    public HistorySegmentDescriptorDigest ThroughRowDescriptorDigest { get; }
    public GridBuildRecipeDigest RecipeDigest { get; }

    public static FulfilledViewKey Create(
        RefId refId,
        TimelineHeadRef timelineHead,
        HistorySegmentDescriptorDigest throughRowDescriptorDigest,
        GridBuildRecipe recipe
    ) {
        ArgumentNullException.ThrowIfNull(timelineHead);
        ArgumentNullException.ThrowIfNull(recipe);
        if (refId.IsDefault
            || timelineHead.RefId != refId
            || timelineHead.TimelineId != recipe.TimelineId) {
            throw new ArgumentException(
                "The fulfilled-view scope must bind one Ref and Timeline."
            );
        }
        RecapGridSyntax.RequireTypedValue(
            throughRowDescriptorDigest.Value,
            64,
            nameof(throughRowDescriptorDigest)
        );
        byte[] canonical = RecapGridCanonical.Encode(new FulfilledViewKeyDto(
            1,
            refId.Packed,
            timelineHead.TimelineId.Value,
            timelineHead.Generation,
            throughRowDescriptorDigest.Value,
            recipe.Digest.Value
        ));
        if (canonical.Length
            > RecapGridLimits.MaximumFulfilledViewKeyCanonicalUtf8Bytes) {
            throw new InvalidOperationException(
                "The fulfilled-view key exceeds its code-owned cap."
            );
        }
        return new FulfilledViewKey(
            refId,
            timelineHead.TimelineId,
            timelineHead.Generation,
            throughRowDescriptorDigest,
            recipe.Digest,
            canonical
        );
    }

    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public static FulfilledViewKey DecodeCanonical(
        ReadOnlySpan<byte> bytes
    ) {
        try {
            return DecodeCanonicalCore(bytes);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NullReferenceException) {
            throw new InvalidDataException(
                "The fulfilled-view key canonical value is invalid.",
                exception
            );
        }
    }

    public static FulfilledViewKey DecodeCanonical(
        GridBuildRecipe recipe,
        TimelineHeadRef timelineHead,
        ReadOnlySpan<byte> bytes
    ) {
        try {
            return DecodeCanonicalCore(recipe, timelineHead, bytes);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NullReferenceException) {
            throw new InvalidDataException(
                "The fulfilled-view key canonical value is invalid.",
                exception
            );
        }
    }

    private static FulfilledViewKey DecodeCanonicalCore(
        GridBuildRecipe recipe,
        TimelineHeadRef timelineHead,
        ReadOnlySpan<byte> bytes
    ) {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(timelineHead);
        FulfilledViewKey value = DecodeCanonicalCore(bytes);
        if (value.RefId != timelineHead.RefId
            || value.TimelineId != timelineHead.TimelineId
            || value.TimelineHeadGeneration != timelineHead.Generation
            || value.TimelineId != recipe.TimelineId
            || value.RecipeDigest != recipe.Digest) {
            throw new InvalidDataException(
                "The fulfilled-view key differs from its exact scope."
            );
        }
        return value;
    }

    private static FulfilledViewKey DecodeCanonicalCore(
        ReadOnlySpan<byte> bytes
    ) {
        FulfilledViewKeyDto dto = RecapGridCanonical
            .DecodeExact<FulfilledViewKeyDto>(
                bytes,
                RecapGridLimits.MaximumFulfilledViewKeyCanonicalUtf8Bytes,
                nameof(bytes)
            );
        if (dto.SchemaVersion != 1 || dto.TimelineHeadGeneration < 0) {
            throw new InvalidDataException(
                "The fulfilled-view key schema is invalid."
            );
        }
        var refId = new RefId(dto.RefId);
        if (refId.IsDefault) {
            throw new InvalidDataException(
                "The fulfilled-view RefId must not be default."
            );
        }
        var timelineId = new TimelineId(dto.TimelineId);
        var through = new HistorySegmentDescriptorDigest(
            dto.ThroughRowDescriptorDigest
        );
        var recipeDigest = new GridBuildRecipeDigest(dto.RecipeDigest);
        byte[] canonical = RecapGridCanonical.Encode(new FulfilledViewKeyDto(
            1,
            refId.Packed,
            timelineId.Value,
            dto.TimelineHeadGeneration,
            through.Value,
            recipeDigest.Value
        ));
        if (!bytes.SequenceEqual(canonical)) {
            throw new InvalidDataException(
                "The fulfilled-view key is not its exact canonical value."
            );
        }
        return new FulfilledViewKey(
            refId,
            timelineId,
            dto.TimelineHeadGeneration,
            through,
            recipeDigest,
            canonical
        );
    }
}

internal sealed record RecapCellArtifactBodyDto(
    int SchemaVersion,
    string LogicalColumnId,
    string DefinitionDigest,
    byte[] EvaluationKey,
    string Outcome,
    string Content,
    string ContentDigest
);

internal sealed record RecapCellArtifactDto(
    int SchemaVersion,
    string CellDigest,
    string LogicalColumnId,
    string DefinitionDigest,
    byte[] EvaluationKey,
    string Outcome,
    string Content,
    string ContentDigest
);

internal sealed record RecapRowViewCellDto(
    string LogicalColumnId,
    string DefinitionDigest,
    string CellDigest
);

internal sealed record RecapRowViewBodyDto(
    int SchemaVersion,
    ulong RefId,
    string TimelineId,
    string HistoryRowId,
    string RowDescriptorDigest,
    string RecipeDigest,
    string TargetDigest,
    string? PreviousHistoryRowId,
    string? PreviousViewDigest,
    bool BootstrapCompleted,
    RecapRowViewCellDto[] OrderedCells
);

internal sealed record RecapRowViewDto(
    int SchemaVersion,
    string Digest,
    ulong RefId,
    string TimelineId,
    string HistoryRowId,
    string RowDescriptorDigest,
    string RecipeDigest,
    string TargetDigest,
    string? PreviousHistoryRowId,
    string? PreviousViewDigest,
    bool BootstrapCompleted,
    RecapRowViewCellDto[] OrderedCells
);

internal sealed record FulfilledViewKeyDto(
    int SchemaVersion,
    ulong RefId,
    string TimelineId,
    long TimelineHeadGeneration,
    string ThroughRowDescriptorDigest,
    string RecipeDigest
);
