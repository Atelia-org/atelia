using System.Collections.ObjectModel;
using System.Text.Json;
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
        GridBuildRecipeDigest? originRootRecipeDigest,
        LogicalColumnId[] recomputedColumns,
        GridBuildRecipeDigest digest,
        byte[] canonicalBytes,
        int schemaVersion
    ) {
        TimelineId = timelineId;
        BootstrapThroughRowId = bootstrapThroughRowId;
        Target = target;
        BaseRecipeDigest = baseRecipeDigest;
        OriginRootRecipeDigest = originRootRecipeDigest;
        _recomputedColumns = Array.AsReadOnly(recomputedColumns);
        Digest = digest;
        _canonicalBytes = canonicalBytes;
        SchemaVersion = schemaVersion;
    }

    public GridBuildRecipeKind Kind => BaseRecipeDigest is null
        ? GridBuildRecipeKind.Full
        : GridBuildRecipeKind.Overlay;
    public TimelineId TimelineId { get; }
    public HistoryRowId? BootstrapThroughRowId { get; }
    public BuildTarget Target { get; }
    public GridBuildRecipeDigest? BaseRecipeDigest { get; }
    /// <summary>
    /// The adopted root observed when an explicit Full V2 rebuild was started.
    /// It is candidate identity evidence, never a live adoption pointer or a
    /// predecessor edge.
    /// </summary>
    public GridBuildRecipeDigest? OriginRootRecipeDigest { get; }
    public int SchemaVersion { get; }
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
            null,
            target.OrderedColumns.Select(static column =>
                column.LogicalColumnId).ToArray()
        );
    }

    /// <summary>
    /// Creates an explicit Full V2 candidate. Normal maintenance must keep
    /// using the adopted root and therefore must not call this factory.
    /// </summary>
    public static GridBuildRecipe CreateFull(
        TimelineId timelineId,
        HistoryRowId? bootstrapThroughRowId,
        BuildTarget target,
        GridBuildRecipeDigest? originRootRecipeDigest
    ) {
        ArgumentNullException.ThrowIfNull(target);
        return CreateCore(
            timelineId,
            bootstrapThroughRowId,
            target,
            null,
            originRootRecipeDigest,
            target.OrderedColumns.Select(static column =>
                column.LogicalColumnId).ToArray(),
            useV2: true
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
            null,
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
        int schemaVersion;
        try {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
            schemaVersion = document.RootElement.GetProperty("schemaVersion")
                .GetInt32();
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException or KeyNotFoundException) {
            throw new InvalidDataException("The recipe schema is invalid.", exception);
        }
        if (schemaVersion == 1) {
            GridBuildRecipeDto dto = RecapGridCanonical.DecodeExact<GridBuildRecipeDto>(
                bytes,
                RecapGridLimits.MaximumRecipeCanonicalUtf8Bytes,
                nameof(bytes)
            );
            return DecodeCore(
                dto.SchemaVersion,
                dto.Digest,
                dto.TimelineId,
                dto.BootstrapThroughRowId,
                dto.Target,
                dto.BaseRecipeDigest,
                originRootRecipeDigest: null,
                dto.RecomputedColumns
            );
        }
        if (schemaVersion == 2) {
            GridBuildRecipeV2Dto dto = RecapGridCanonical.DecodeExact<GridBuildRecipeV2Dto>(
                bytes,
                RecapGridLimits.MaximumRecipeCanonicalUtf8Bytes,
                nameof(bytes)
            );
            return DecodeCore(
                dto.SchemaVersion,
                dto.Digest,
                dto.TimelineId,
                dto.BootstrapThroughRowId,
                dto.Target,
                dto.BaseRecipeDigest,
                dto.OriginRootRecipeDigest,
                dto.RecomputedColumns
            );
        }
        throw new InvalidDataException("The recipe schema is invalid.");
    }

    private static GridBuildRecipe DecodeCore(
        int schemaVersion,
        string digest,
        string timelineId,
        string? bootstrapThroughRowId,
        byte[] targetBytes,
        string? baseRecipeDigest,
        string? originRootRecipeDigest,
        string[] recomputedColumns
    ) {
        if (targetBytes is null || recomputedColumns is null) {
            throw new InvalidDataException("The recipe schema is invalid.");
        }
        BuildTarget target = BuildTarget.DecodeCanonical(targetBytes);
        GridBuildRecipeDigest? baseDigest = baseRecipeDigest is null
            ? null
            : new GridBuildRecipeDigest(baseRecipeDigest);
        GridBuildRecipeDigest? originDigest = originRootRecipeDigest is null
            ? null
            : new GridBuildRecipeDigest(originRootRecipeDigest);
        if (schemaVersion == 1 && originDigest is not null
            || schemaVersion == 2 && baseDigest is not null) {
            throw new InvalidDataException("The recipe schema is invalid.");
        }
        LogicalColumnId[] recomputed = recomputedColumns
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
                nameof(digest)
            );
        }
        GridBuildRecipe value = CreateCore(
            new TimelineId(timelineId),
            bootstrapThroughRowId is null
                ? null
                : new HistoryRowId(bootstrapThroughRowId),
            target,
            baseDigest,
            originDigest,
            recomputed,
            useV2: schemaVersion == 2
        );
        if (!string.Equals(value.Digest.Value, digest, StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The recipe digest does not match its body.",
                nameof(digest)
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
        GridBuildRecipeDigest? originRootRecipeDigest,
        LogicalColumnId[] recomputedColumns,
        bool useV2 = false
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
        if ((originRootRecipeDigest is not null || useV2)
            && baseRecipeDigest is not null) {
            throw new ArgumentException("Only Full V2 recipes may have an origin root.", nameof(originRootRecipeDigest));
        }
        int schemaVersion = useV2 ? 2 : 1;
        byte[] bodyBytes = schemaVersion == 1
            ? RecapGridCanonical.Encode(new GridBuildRecipeBodyDto(
                1, timelineId.Value, bootstrapThroughRowId?.Value,
                target.ToCanonicalBytes(), baseRecipeDigest?.Value,
                recomputedColumns.Select(static value => value.Value).ToArray()))
            : RecapGridCanonical.Encode(new GridBuildRecipeV2BodyDto(
                2, timelineId.Value, bootstrapThroughRowId?.Value,
                target.ToCanonicalBytes(), null, originRootRecipeDigest?.Value,
                recomputedColumns.Select(static value => value.Value).ToArray()));
        GridBuildRecipeDigest digest = new(RecapGridHash.Compute(
            schemaVersion == 1 ? "atelia.recap-grid.build-recipe.v1" : "atelia.recap-grid.build-recipe.v2",
            bodyBytes
        ));
        byte[] canonical = schemaVersion == 1
            ? RecapGridCanonical.Encode(new GridBuildRecipeDto(
                1, digest.Value, timelineId.Value, bootstrapThroughRowId?.Value,
                target.ToCanonicalBytes(), baseRecipeDigest?.Value,
                recomputedColumns.Select(static value => value.Value).ToArray()))
            : RecapGridCanonical.Encode(new GridBuildRecipeV2Dto(
                2, digest.Value, timelineId.Value, bootstrapThroughRowId?.Value,
                target.ToCanonicalBytes(), null, originRootRecipeDigest?.Value,
                recomputedColumns.Select(static value => value.Value).ToArray()));
        if (canonical.Length > RecapGridLimits.MaximumRecipeCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        return new GridBuildRecipe(
            timelineId,
            bootstrapThroughRowId,
            target,
            baseRecipeDigest,
            originRootRecipeDigest,
            recomputedColumns,
            digest,
            canonical,
            schemaVersion
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

internal sealed record GridBuildRecipeV2BodyDto(
    int SchemaVersion,
    string TimelineId,
    string? BootstrapThroughRowId,
    byte[] Target,
    string? BaseRecipeDigest,
    string? OriginRootRecipeDigest,
    string[] RecomputedColumns
);

internal sealed record GridBuildRecipeV2Dto(
    int SchemaVersion,
    string Digest,
    string TimelineId,
    string? BootstrapThroughRowId,
    byte[] Target,
    string? BaseRecipeDigest,
    string? OriginRootRecipeDigest,
    string[] RecomputedColumns
);
