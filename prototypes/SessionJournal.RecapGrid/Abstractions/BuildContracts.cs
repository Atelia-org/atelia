using System.Collections.ObjectModel;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid;

public sealed class BuildTargetColumn {
    public BuildTargetColumn(
        LogicalColumnId logicalColumnId,
        MaintainerDefinitionDigest definitionDigest
    ) {
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
        LogicalColumnId = logicalColumnId;
        DefinitionDigest = definitionDigest;
    }

    public LogicalColumnId LogicalColumnId { get; }
    public MaintainerDefinitionDigest DefinitionDigest { get; }

    internal BuildTargetColumnDto ToDto()
        => new(LogicalColumnId.Value, DefinitionDigest.Value);
}

public sealed class BuildTarget {
    private readonly ReadOnlyCollection<BuildTargetColumn> _orderedColumns;
    private readonly byte[] _canonicalBytes;

    private BuildTarget(
        BuildTargetColumn[] orderedColumns,
        BuildTargetDigest digest,
        byte[] canonicalBytes
    ) {
        _orderedColumns = Array.AsReadOnly(orderedColumns);
        Digest = digest;
        _canonicalBytes = canonicalBytes;
    }

    public IReadOnlyList<BuildTargetColumn> OrderedColumns => _orderedColumns;
    public BuildTargetDigest Digest { get; }

    public static BuildTarget Create(
        IEnumerable<BuildTargetColumn> orderedColumns
    ) {
        ArgumentNullException.ThrowIfNull(orderedColumns);
        BuildTargetColumn[] columns = RecapGridSyntax.MaterializeBounded(
            orderedColumns,
            RecapGridLimits.MaximumColumnCount,
            nameof(orderedColumns)
        );
        if (columns.Any(static column => column is null)
            || columns.Select(static column => column.LogicalColumnId)
                .Distinct().Count() != columns.Length) {
            throw new ArgumentException(
                "Build target columns must be non-null and logically unique.",
                nameof(orderedColumns)
            );
        }
        BuildTargetBodyDto body = new(
            1,
            columns.Select(static column => column.ToDto()).ToArray()
        );
        BuildTargetDigest digest = new(RecapGridHash.Compute(
            "atelia.recap-grid.build-target.v1",
            RecapGridCanonical.Encode(body)
        ));
        byte[] canonical = RecapGridCanonical.Encode(new BuildTargetDto(
            1,
            digest.Value,
            body.OrderedColumns
        ));
        if (canonical.Length > RecapGridLimits.MaximumTargetCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(orderedColumns));
        }
        return new BuildTarget(columns, digest, canonical);
    }

    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public static BuildTarget DecodeCanonical(ReadOnlySpan<byte> bytes) {
        try {
            return DecodeCanonicalCore(bytes);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NullReferenceException) {
            throw new InvalidDataException(
                "The build target canonical value is invalid.",
                exception
            );
        }
    }

    private static BuildTarget DecodeCanonicalCore(ReadOnlySpan<byte> bytes) {
        BuildTargetDto dto = RecapGridCanonical.DecodeExact<BuildTargetDto>(
            bytes,
            RecapGridLimits.MaximumTargetCanonicalUtf8Bytes,
            nameof(bytes)
        );
        if (dto.SchemaVersion != 1 || dto.OrderedColumns is null) {
            throw new InvalidDataException(
                "The build-target schema is invalid."
            );
        }
        BuildTarget value = Create(dto.OrderedColumns.Select(static column =>
            new BuildTargetColumn(
                new LogicalColumnId(column.LogicalColumnId),
                new MaintainerDefinitionDigest(column.DefinitionDigest)
            )));
        if (!string.Equals(value.Digest.Value, dto.Digest, StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The build target digest does not match its body.",
                nameof(bytes)
            );
        }
        return value;
    }
}

public enum GridBuildRecipeKind {
    Full,
    Overlay
}

public sealed class GridBuildRecipe {
    private readonly ReadOnlyCollection<LogicalColumnId> _recomputedColumns;
    private readonly byte[] _canonicalBytes;

    private GridBuildRecipe(
        TimelineId timelineId,
        HistoryRowId? bootstrapThroughRowId,
        BuildTarget target,
        GridBuildRecipeDigest? baseRecipeDigest,
        LogicalColumnId[] recomputedColumns,
        GridBuildRecipeDigest digest,
        byte[] canonicalBytes
    ) {
        TimelineId = timelineId;
        BootstrapThroughRowId = bootstrapThroughRowId;
        Target = target;
        BaseRecipeDigest = baseRecipeDigest;
        _recomputedColumns = Array.AsReadOnly(recomputedColumns);
        Digest = digest;
        _canonicalBytes = canonicalBytes;
    }

    public GridBuildRecipeKind Kind => BaseRecipeDigest is null
        ? GridBuildRecipeKind.Full
        : GridBuildRecipeKind.Overlay;
    public TimelineId TimelineId { get; }
    public HistoryRowId? BootstrapThroughRowId { get; }
    public BuildTarget Target { get; }
    public GridBuildRecipeDigest? BaseRecipeDigest { get; }
    public IReadOnlyList<LogicalColumnId> RecomputedColumns =>
        _recomputedColumns;
    public GridBuildRecipeDigest Digest { get; }

    public static GridBuildRecipe CreateFull(
        TimelineId timelineId,
        HistoryRowId? bootstrapThroughRowId,
        BuildTarget target
    ) {
        ArgumentNullException.ThrowIfNull(target);
        return CreateCore(
            timelineId,
            bootstrapThroughRowId,
            target,
            null,
            target.OrderedColumns.Select(static column =>
                column.LogicalColumnId).ToArray()
        );
    }

    public static GridBuildRecipe CreateOverlay(
        GridBuildRecipe baseRecipe,
        HistoryRowId? bootstrapThroughRowId,
        BuildTarget target,
        IEnumerable<LogicalColumnId> recomputedColumns
    ) {
        ArgumentNullException.ThrowIfNull(baseRecipe);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(recomputedColumns);
        if (target.OrderedColumns.Count == 0) {
            throw new ArgumentException(
                "An overlay target must not be empty.",
                nameof(target)
            );
        }
        LogicalColumnId[] recomputed = RecapGridSyntax.MaterializeBounded(
            recomputedColumns,
            RecapGridLimits.MaximumColumnCount,
            nameof(recomputedColumns)
        );
        if (recomputed.Length == 0) {
            throw new ArgumentException(
                "An overlay must recompute at least one target column.",
                nameof(recomputedColumns)
            );
        }
        ValidateRecomputed(target, recomputed);
        Dictionary<LogicalColumnId, MaintainerDefinitionDigest> baseColumns =
            baseRecipe.Target.OrderedColumns.ToDictionary(
                static value => value.LogicalColumnId,
                static value => value.DefinitionDigest
            );
        HashSet<LogicalColumnId> recomputedSet = recomputed.ToHashSet();
        foreach (BuildTargetColumn column in target.OrderedColumns) {
            bool unchanged = baseColumns.TryGetValue(
                column.LogicalColumnId,
                out MaintainerDefinitionDigest previous
            ) && previous == column.DefinitionDigest;
            if (!unchanged && !recomputedSet.Contains(column.LogicalColumnId)) {
                throw new ArgumentException(
                    "Every new or changed overlay column must be recomputed.",
                    nameof(target)
                );
            }
        }
        return CreateCore(
            baseRecipe.TimelineId,
            bootstrapThroughRowId,
            target,
            baseRecipe.Digest,
            recomputed
        );
    }

    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public static GridBuildRecipe DecodeCanonical(ReadOnlySpan<byte> bytes) {
        try {
            return DecodeCanonicalCore(bytes);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NullReferenceException) {
            throw new InvalidDataException(
                "The recipe canonical value is invalid.",
                exception
            );
        }
    }

    private static GridBuildRecipe DecodeCanonicalCore(
        ReadOnlySpan<byte> bytes
    ) {
        GridBuildRecipeDto dto = RecapGridCanonical.DecodeExact<GridBuildRecipeDto>(
            bytes,
            RecapGridLimits.MaximumRecipeCanonicalUtf8Bytes,
            nameof(bytes)
        );
        if (dto.SchemaVersion != 1
            || dto.Target is null
            || dto.RecomputedColumns is null) {
            throw new InvalidDataException("The recipe schema is invalid.");
        }
        BuildTarget target = BuildTarget.DecodeCanonical(dto.Target);
        GridBuildRecipeDigest? baseDigest = dto.BaseRecipeDigest is null
            ? null
            : new GridBuildRecipeDigest(dto.BaseRecipeDigest);
        LogicalColumnId[] recomputed = dto.RecomputedColumns
            .Select(static value => new LogicalColumnId(value))
            .ToArray();
        if (baseDigest is not null && recomputed.Length == 0) {
            throw new InvalidDataException(
                "An overlay must recompute at least one target column."
            );
        }
        ValidateRecomputed(target, recomputed);
        if (baseDigest is null
            && !recomputed.SequenceEqual(target.OrderedColumns.Select(
                static column => column.LogicalColumnId))) {
            throw new ArgumentException(
                "A full recipe must recompute every target column in order.",
                nameof(bytes)
            );
        }
        GridBuildRecipe value = CreateCore(
            new TimelineId(dto.TimelineId),
            dto.BootstrapThroughRowId is null
                ? null
                : new HistoryRowId(dto.BootstrapThroughRowId),
            target,
            baseDigest,
            recomputed
        );
        if (!string.Equals(value.Digest.Value, dto.Digest, StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The recipe digest does not match its body.",
                nameof(bytes)
            );
        }
        return value;
    }

    public void ValidateBase(GridBuildRecipe? baseRecipe) {
        if (Kind == GridBuildRecipeKind.Full) {
            if (baseRecipe is not null) {
                throw new ArgumentException("A full recipe has no base.");
            }
            return;
        }
        if (baseRecipe is null
            || baseRecipe.Digest != BaseRecipeDigest
            || baseRecipe.TimelineId != TimelineId) {
            throw new ArgumentException(
                "The overlay base is absent or belongs to another recipe graph."
            );
        }
        CreateOverlay(
            baseRecipe,
            BootstrapThroughRowId,
            Target,
            RecomputedColumns
        );
    }

    private static GridBuildRecipe CreateCore(
        TimelineId timelineId,
        HistoryRowId? bootstrapThroughRowId,
        BuildTarget target,
        GridBuildRecipeDigest? baseRecipeDigest,
        LogicalColumnId[] recomputedColumns
    ) {
        RecapGridSyntax.RequireTypedValue(
            timelineId.Value,
            32,
            nameof(timelineId)
        );
        if (bootstrapThroughRowId is { } throughRow) {
            RecapGridSyntax.RequireTypedValue(
                throughRow.Value,
                64,
                nameof(bootstrapThroughRowId)
            );
        }
        GridBuildRecipeBodyDto body = new(
            1,
            timelineId.Value,
            bootstrapThroughRowId?.Value,
            target.ToCanonicalBytes(),
            baseRecipeDigest?.Value,
            recomputedColumns.Select(static value => value.Value).ToArray()
        );
        GridBuildRecipeDigest digest = new(RecapGridHash.Compute(
            "atelia.recap-grid.build-recipe.v1",
            RecapGridCanonical.Encode(body)
        ));
        byte[] canonical = RecapGridCanonical.Encode(new GridBuildRecipeDto(
            1,
            digest.Value,
            body.TimelineId,
            body.BootstrapThroughRowId,
            body.Target,
            body.BaseRecipeDigest,
            body.RecomputedColumns
        ));
        if (canonical.Length > RecapGridLimits.MaximumRecipeCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        return new GridBuildRecipe(
            timelineId,
            bootstrapThroughRowId,
            target,
            baseRecipeDigest,
            recomputedColumns,
            digest,
            canonical
        );
    }

    private static void ValidateRecomputed(
        BuildTarget target,
        LogicalColumnId[] recomputed
    ) {
        if (recomputed.Distinct().Count() != recomputed.Length) {
            throw new ArgumentException(
                "Recomputed columns must be non-empty and unique.",
                nameof(recomputed)
            );
        }
        LogicalColumnId[] targetOrder = target.OrderedColumns
            .Select(static column => column.LogicalColumnId)
            .ToArray();
        int previous = -1;
        foreach (LogicalColumnId column in recomputed) {
            int index = Array.IndexOf(targetOrder, column);
            if (index <= previous) {
                throw new ArgumentException(
                    "Recomputed columns must form an ordered subset of the target.",
                    nameof(recomputed)
                );
            }
            previous = index;
        }
    }
}

internal sealed record BuildTargetColumnDto(
    string LogicalColumnId,
    string DefinitionDigest
);

internal sealed record BuildTargetBodyDto(
    int SchemaVersion,
    BuildTargetColumnDto[] OrderedColumns
);

internal sealed record BuildTargetDto(
    int SchemaVersion,
    string Digest,
    BuildTargetColumnDto[] OrderedColumns
);

internal sealed record GridBuildRecipeBodyDto(
    int SchemaVersion,
    string TimelineId,
    string? BootstrapThroughRowId,
    byte[] Target,
    string? BaseRecipeDigest,
    string[] RecomputedColumns
);

internal sealed record GridBuildRecipeDto(
    int SchemaVersion,
    string Digest,
    string TimelineId,
    string? BootstrapThroughRowId,
    byte[] Target,
    string? BaseRecipeDigest,
    string[] RecomputedColumns
);
