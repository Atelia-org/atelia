using System.Collections.ObjectModel;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid;

/// <summary>
/// Content identity of a frozen, per-history-row maintenance selection.
/// Unlike a CellId and RowResultId, this is deterministic: it proves which
/// producer, predecessor and assignments were selected before dispatch.
/// </summary>
public readonly record struct RowWorkId {
    public RowWorkId(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public sealed record RowWorkKey {
    public RowWorkKey(
        RefId refId,
        TimelineId timelineId,
        GridBuildRecipeDigest rootRecipeDigest,
        HistoryRowId historyRowId
    ) {
        if (refId.IsDefault) {
            throw new ArgumentException("RefId must not be default.", nameof(refId));
        }
        RecapGridSyntax.RequireTypedValue(timelineId.Value, 32, nameof(timelineId));
        RecapGridSyntax.RequireTypedValue(rootRecipeDigest.Value, 64, nameof(rootRecipeDigest));
        RecapGridSyntax.RequireTypedValue(historyRowId.Value, 64, nameof(historyRowId));
        RefId = refId;
        TimelineId = timelineId;
        RootRecipeDigest = rootRecipeDigest;
        HistoryRowId = historyRowId;
    }

    public RefId RefId { get; }
    public TimelineId TimelineId { get; }
    public GridBuildRecipeDigest RootRecipeDigest { get; }
    public HistoryRowId HistoryRowId { get; }
}

/// <summary>One persisted member of a RowWork. A null source means Evaluate.</summary>
public sealed record RowWorkAssignment {
    public RowWorkAssignment(LogicalColumnId logicalColumnId, CellId? reusedCellId) {
        RecapGridSyntax.RequireIdentifier(logicalColumnId.Value, nameof(logicalColumnId));
        if (reusedCellId is { } cell) {
            RecapGridSyntax.RequireTypedValue(cell.Value, 32, nameof(reusedCellId));
        }
        LogicalColumnId = logicalColumnId;
        ReusedCellId = reusedCellId;
    }

    public LogicalColumnId LogicalColumnId { get; }
    public CellId? ReusedCellId { get; }
    public bool IsEvaluate => ReusedCellId is null;
}

/// <summary>
/// The sole durable work selection for a root/history position. Its existence
/// is the pre-dispatch recovery fact; completion remains represented by cells
/// and a RowResult, not a second work state machine.
/// </summary>
public sealed class RowWork {
    private readonly ReadOnlyCollection<RowWorkAssignment> _orderedAssignments;
    private readonly byte[] _canonicalBytes;

    public RowWork(
        RowWorkKey key,
        BuildTarget producerTarget,
        HistoryRowId? previousHistoryRowId,
        RowResultId? previousRowResultId,
        IEnumerable<RowWorkAssignment> orderedAssignments
    ) {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(producerTarget);
        if ((previousHistoryRowId is null) != (previousRowResultId is null)) {
            throw new ArgumentException("Previous row and result must be present or absent together.");
        }
        if (previousHistoryRowId == key.HistoryRowId) {
            throw new ArgumentException("A RowWork cannot name itself as predecessor.", nameof(previousHistoryRowId));
        }
        if (previousHistoryRowId is { } previousHistory) {
            RecapGridSyntax.RequireTypedValue(previousHistory.Value, 64, nameof(previousHistoryRowId));
        }
        if (previousRowResultId is { } previousResult) {
            RecapGridSyntax.RequireTypedValue(previousResult.Value, 32, nameof(previousRowResultId));
        }
        RowWorkAssignment[] assignments = RecapGridSyntax.MaterializeBounded(
            orderedAssignments,
            RecapGridLimits.MaximumColumnCount,
            nameof(orderedAssignments)
        );
        if (assignments.Length != producerTarget.OrderedColumns.Count
            || assignments.Any(static assignment => assignment is null)
            || !assignments.Select(static assignment => assignment.LogicalColumnId)
                .SequenceEqual(producerTarget.OrderedColumns.Select(static column => column.LogicalColumnId))) {
            throw new ArgumentException("Assignments must cover the producer target in target order.", nameof(orderedAssignments));
        }
        Key = key;
        ProducerTarget = producerTarget;
        PreviousHistoryRowId = previousHistoryRowId;
        PreviousRowResultId = previousRowResultId;
        _orderedAssignments = Array.AsReadOnly(assignments);
        byte[] body = RecapGridCanonical.Encode(new RowWorkBodyDto(
            1,
            key.RefId.ToHexString(),
            key.TimelineId.Value,
            key.RootRecipeDigest.Value,
            key.HistoryRowId.Value,
            previousHistoryRowId?.Value,
            previousRowResultId?.Value,
            producerTarget.ToCanonicalBytes(),
            assignments.Select(static assignment => new RowWorkAssignmentDto(
                assignment.LogicalColumnId.Value,
                assignment.ReusedCellId?.Value
            )).ToArray()
        ));
        WorkId = new RowWorkId(RecapGridHash.Compute("atelia.recap-grid.row-work.v1", body));
        _canonicalBytes = RecapGridCanonical.Encode(new RowWorkDto(
            1,
            WorkId.Value,
            key.RefId.ToHexString(),
            key.TimelineId.Value,
            key.RootRecipeDigest.Value,
            key.HistoryRowId.Value,
            previousHistoryRowId?.Value,
            previousRowResultId?.Value,
            producerTarget.ToCanonicalBytes(),
            assignments.Select(static assignment => new RowWorkAssignmentDto(
                assignment.LogicalColumnId.Value,
                assignment.ReusedCellId?.Value
            )).ToArray()
        ));
    }

    public RowWorkKey Key { get; }
    public RowWorkId WorkId { get; }
    public BuildTarget ProducerTarget { get; }
    public HistoryRowId? PreviousHistoryRowId { get; }
    public RowResultId? PreviousRowResultId { get; }
    public IReadOnlyList<RowWorkAssignment> OrderedAssignments => _orderedAssignments;
    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public static RowWork DecodeCanonical(ReadOnlySpan<byte> bytes) {
        try {
            RowWorkDto dto = RecapGridCanonical.DecodeExact<RowWorkDto>(
                bytes,
                RecapGridLimits.MaximumRecipeCanonicalUtf8Bytes,
                nameof(bytes)
            );
            if (dto.SchemaVersion != 1 || dto.ProducerTarget is null
                || dto.OrderedAssignments is null) {
                throw new InvalidDataException("The RowWork schema is invalid.");
            }
            var value = new RowWork(
                new RowWorkKey(
                    new RefId(ulong.Parse(dto.RefId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)),
                    new TimelineId(dto.TimelineId),
                    new GridBuildRecipeDigest(dto.RootRecipeDigest),
                    new HistoryRowId(dto.HistoryRowId)
                ),
                BuildTarget.DecodeCanonical(dto.ProducerTarget),
                dto.PreviousHistoryRowId is null ? null : new HistoryRowId(dto.PreviousHistoryRowId),
                dto.PreviousRowResultId is null ? null : new RowResultId(dto.PreviousRowResultId),
                dto.OrderedAssignments.Select(static assignment => new RowWorkAssignment(
                    new LogicalColumnId(assignment.LogicalColumnId),
                    assignment.ReusedCellId is null ? null : new CellId(assignment.ReusedCellId)
                ))
            );
            if (value.WorkId.Value != dto.WorkId) {
                throw new InvalidDataException("The RowWork identity does not match its body.");
            }
            return value;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException
            or FormatException or OverflowException or NullReferenceException) {
            throw new InvalidDataException("The RowWork canonical value is invalid.", exception);
        }
    }
}

/// <summary>The V5 slot identity for newly evaluated cells.</summary>
public sealed record WorkCellSlot {
    public WorkCellSlot(RowWorkId workId, LogicalColumnId logicalColumnId) {
        RecapGridSyntax.RequireTypedValue(workId.Value, 64, nameof(workId));
        RecapGridSyntax.RequireIdentifier(logicalColumnId.Value, nameof(logicalColumnId));
        WorkId = workId;
        LogicalColumnId = logicalColumnId;
    }

    public RowWorkId WorkId { get; }
    public LogicalColumnId LogicalColumnId { get; }
}

internal sealed record RowWorkAssignmentDto(string LogicalColumnId, string? ReusedCellId);
internal sealed record RowWorkBodyDto(
    int SchemaVersion,
    string RefId,
    string TimelineId,
    string RootRecipeDigest,
    string HistoryRowId,
    string? PreviousHistoryRowId,
    string? PreviousRowResultId,
    byte[] ProducerTarget,
    RowWorkAssignmentDto[] OrderedAssignments
);
internal sealed record RowWorkDto(
    int SchemaVersion,
    string WorkId,
    string RefId,
    string TimelineId,
    string RootRecipeDigest,
    string HistoryRowId,
    string? PreviousHistoryRowId,
    string? PreviousRowResultId,
    byte[] ProducerTarget,
    RowWorkAssignmentDto[] OrderedAssignments
);
