using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Atelia.SessionJournal.RecapGrid.Store;

public static class RecapGridStoreLimits {
    public const int MaximumPageItems = 128;
    public const int MaximumPageBytes = 4 * 1024 * 1024;
    public const int MaximumVerificationErrors = 128;
}

public readonly record struct RecapGridStoreInstanceId {
    public RecapGridStoreInstanceId(string value) {
        Value = StoreSyntax.RequireLowerHex(value, 32, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;

    internal static RecapGridStoreInstanceId Generate() => new(
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
    );
}

public sealed record RecapGridStoreIdentity {
    public RecapGridStoreIdentity(
        RecapGridStoreInstanceId instanceId,
        int schemaVersion
    ) {
        if (instanceId.Value is null) {
            throw new ArgumentException(
                "Store instance identity must not be default.",
                nameof(instanceId)
            );
        }
        if (schemaVersion < 1) {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }
        InstanceId = instanceId;
        SchemaVersion = schemaVersion;
    }

    public RecapGridStoreInstanceId InstanceId { get; }
    public int SchemaVersion { get; }
}

public sealed record RecapGridStorePhysicalWitness {
    public RecapGridStorePhysicalWitness(long length, string sha256) {
        if (length < 1) {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        Length = length;
        Sha256 = StoreSyntax.RequireLowerHex(sha256, 64, nameof(sha256));
    }

    public long Length { get; }
    public string Sha256 { get; }
}

public sealed record RecapGridStoreInfo(
    RecapGridStoreIdentity Identity,
    long DatabaseBytes,
    long CellCount,
    long RowViewCount,
    long RowViewMemberCount,
    long FulfilledViewCount,
    string SqliteVersion,
    string SqliteSourceId,
    IReadOnlyList<string> CompileOptions
);

public sealed record RecapGridStoreExportCursor {
    private const byte WireVersion = 1;
    private const byte CellKind = 1;
    private const byte RowViewKind = 2;
    private const byte FulfilledKind = 3;

    private RecapGridStoreExportCursor(
        string value,
        byte kind,
        string key,
        string? refId,
        string? timelineId,
        long generation,
        string? through,
        string? recipe
    ) {
        Value = value;
        Kind = kind;
        Key = key;
        RefId = refId;
        TimelineId = timelineId;
        Generation = generation;
        Through = through;
        Recipe = recipe;
    }

    public string Value { get; }
    internal byte Kind { get; }
    internal string Key { get; }
    internal string? RefId { get; }
    internal string? TimelineId { get; }
    internal long Generation { get; }
    internal string? Through { get; }
    internal string? Recipe { get; }

    public static RecapGridStoreExportCursor Parse(string value) {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes;
        try {
            string padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException exception) {
            throw new ArgumentException(
                "The export cursor is not canonical base64url.",
                nameof(value),
                exception
            );
        }
        if (bytes.Length < 2 || bytes[0] != WireVersion) {
            throw new ArgumentException(
                "The export cursor has an invalid V1 header.",
                nameof(value)
            );
        }
        RecapGridStoreExportCursor cursor = bytes[1] switch {
            CellKind when bytes.Length == 66 => CreateDigest(
                "cell",
                ReadLowerHex(bytes.AsSpan(2), 64, nameof(value))
            ),
            RowViewKind when bytes.Length == 66 => CreateDigest(
                "row-view",
                ReadLowerHex(bytes.AsSpan(2), 64, nameof(value))
            ),
            FulfilledKind when bytes.Length == 186 => CreateFulfilled(
                ReadLowerHex(bytes.AsSpan(2, 16), 16, nameof(value)),
                ReadLowerHex(bytes.AsSpan(18, 32), 32, nameof(value)),
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(50, 8)),
                ReadLowerHex(bytes.AsSpan(58, 64), 64, nameof(value)),
                ReadLowerHex(bytes.AsSpan(122, 64), 64, nameof(value))
            ),
            _ => throw new ArgumentException(
                "The export cursor has an invalid V1 shape.",
                nameof(value)
            )
        };
        if (!string.Equals(cursor.Value, value, StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The export cursor is not canonical.",
                nameof(value)
            );
        }
        return cursor;
    }

    internal static RecapGridStoreExportCursor CreateDigest(
        string kind,
        string key
    ) {
        byte kindTag = kind switch {
            "cell" => CellKind,
            "row-view" => RowViewKind,
            _ => throw new InvalidDataException(
                "Unknown digest export cursor kind."
            )
        };
        key = StoreSyntax.RequireLowerHex(key, 64, nameof(key));
        var bytes = new byte[66];
        bytes[0] = WireVersion;
        bytes[1] = kindTag;
        WriteAscii(bytes.AsSpan(2), key);
        return new RecapGridStoreExportCursor(
            Encode(bytes),
            kindTag,
            key,
            null,
            null,
            0,
            null,
            null
        );
    }

    internal static RecapGridStoreExportCursor CreateFulfilled(
        string refId,
        string timelineId,
        long generation,
        string through,
        string recipe
    ) {
        refId = StoreSyntax.RequireLowerHex(refId, 16, nameof(refId));
        timelineId = StoreSyntax.RequireLowerHex(
            timelineId,
            32,
            nameof(timelineId)
        );
        if (generation < 0) {
            throw new InvalidDataException(
                "Fulfilled export generation must not be negative."
            );
        }
        through = StoreSyntax.RequireLowerHex(through, 64, nameof(through));
        recipe = StoreSyntax.RequireLowerHex(recipe, 64, nameof(recipe));
        var bytes = new byte[186];
        bytes[0] = WireVersion;
        bytes[1] = FulfilledKind;
        WriteAscii(bytes.AsSpan(2, 16), refId);
        WriteAscii(bytes.AsSpan(18, 32), timelineId);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(50, 8), generation);
        WriteAscii(bytes.AsSpan(58, 64), through);
        WriteAscii(bytes.AsSpan(122, 64), recipe);
        return new RecapGridStoreExportCursor(
            Encode(bytes),
            FulfilledKind,
            FulfilledDiagnosticKey(
                refId,
                timelineId,
                generation,
                through,
                recipe
            ),
            refId,
            timelineId,
            generation,
            through,
            recipe
        );
    }

    internal static string FulfilledDiagnosticKey(
        string refId,
        string timelineId,
        long generation,
        string through,
        string recipe
    ) => $"{refId}/{timelineId}/{generation}/{through}/{recipe}";

    internal bool IsCell => Kind == CellKind;
    internal bool IsRowView => Kind == RowViewKind;
    internal bool IsFulfilled => Kind == FulfilledKind;

    private static string ReadLowerHex(
        ReadOnlySpan<byte> bytes,
        int length,
        string parameterName
    ) {
        string value = System.Text.Encoding.ASCII.GetString(bytes);
        try {
            return StoreSyntax.RequireLowerHex(
                value,
                length,
                parameterName
            );
        }
        catch (ArgumentException exception) {
            throw new ArgumentException(
                "The export cursor contains an invalid typed key.",
                parameterName,
                exception
            );
        }
    }

    private static void WriteAscii(Span<byte> destination, string value) {
        int written = System.Text.Encoding.ASCII.GetBytes(value, destination);
        if (written != destination.Length) {
            throw new InvalidDataException(
                "An export cursor key has an invalid length."
            );
        }
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record RecapGridStoreExportItem(
    string Kind,
    string Key,
    int CanonicalBytes,
    byte[]? Canonical,
    RowViewDigest? FulfilledViewDigest = null
);

public sealed record RecapGridStoreExportPage(
    IReadOnlyList<RecapGridStoreExportItem> Items,
    RecapGridStoreExportCursor? NextCursor,
    bool Incomplete
);

public abstract record RecapGridStoreExportResult {
    private RecapGridStoreExportResult() { }
    public sealed record Page(RecapGridStoreExportPage Value)
        : RecapGridStoreExportResult;
    public sealed record Absent : RecapGridStoreExportResult;
    public sealed record Busy : RecapGridStoreExportResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridStoreExportResult;
    public sealed record PlatformUnsupported : RecapGridStoreExportResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridStoreExportResult;
}

public abstract record RecapGridStoreInspectResult {
    private RecapGridStoreInspectResult() { }
    public sealed record Available(RecapGridStoreInfo Info)
        : RecapGridStoreInspectResult;
    public sealed record Absent : RecapGridStoreInspectResult;
    public sealed record Busy : RecapGridStoreInspectResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridStoreInspectResult;
    public sealed record PlatformUnsupported : RecapGridStoreInspectResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridStoreInspectResult;
}

public abstract record RecapGridStoreVerifyResult {
    private RecapGridStoreVerifyResult() { }
    public sealed record Healthy(RecapGridStoreInfo Info)
        : RecapGridStoreVerifyResult;
    public sealed record Absent : RecapGridStoreVerifyResult;
    public sealed record Busy : RecapGridStoreVerifyResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridStoreVerifyResult;
    public sealed record PlatformUnsupported : RecapGridStoreVerifyResult;
    public sealed record Unhealthy(
        IReadOnlyList<string> Errors,
        bool Incomplete
    ) : RecapGridStoreVerifyResult;
}

public abstract record RecapGridStorePrepareResetResult {
    private RecapGridStorePrepareResetResult() { }
    public sealed record Prepared(RecapGridStorePhysicalWitness Witness)
        : RecapGridStorePrepareResetResult;
    public sealed record Absent : RecapGridStorePrepareResetResult;
    public sealed record Busy : RecapGridStorePrepareResetResult;
    public sealed record OfflineCleanupRequired(string Slot)
        : RecapGridStorePrepareResetResult;
    public sealed record Limit(string Name) : RecapGridStorePrepareResetResult;
    public sealed record PlatformUnsupported : RecapGridStorePrepareResetResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridStorePrepareResetResult;
}

public abstract record RecapGridStoreResetResult {
    private RecapGridStoreResetResult() { }
    public sealed record Reset(RecapGridStoreIdentity Identity)
        : RecapGridStoreResetResult;
    public sealed record Absent : RecapGridStoreResetResult;
    public sealed record Busy : RecapGridStoreResetResult;
    public sealed record StaleConfirmation(
        RecapGridStorePhysicalWitness Actual
    ) : RecapGridStoreResetResult;
    public sealed record OfflineCleanupRequired(string Slot)
        : RecapGridStoreResetResult;
    public sealed record Limit(string Name) : RecapGridStoreResetResult;
    public sealed record CommitIndeterminate(
        RecapGridStoreIdentity Intended,
        RecapGridStoreIdentity? Observed
    ) : RecapGridStoreResetResult;
    public sealed record PlatformUnsupported : RecapGridStoreResetResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridStoreResetResult;
}

public abstract record RecapGridStoreCreateResult {
    private RecapGridStoreCreateResult() { }
    public sealed record Created(RecapGridStoreIdentity Identity)
        : RecapGridStoreCreateResult;
    public sealed record AlreadyExists : RecapGridStoreCreateResult;
    public sealed record Busy : RecapGridStoreCreateResult;
    public sealed record Limit(string Name) : RecapGridStoreCreateResult;
    public sealed record CommitIndeterminate(
        RecapGridStoreIdentity Intended,
        RecapGridStoreIdentity? Observed
    ) : RecapGridStoreCreateResult;
    public sealed record PlatformUnsupported : RecapGridStoreCreateResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridStoreCreateResult;
}

public abstract record RecapGridStoreOpenResult {
    private RecapGridStoreOpenResult() { }
    public sealed record Opened(RecapGridStoreHandle Handle)
        : RecapGridStoreOpenResult;
    public sealed record Absent : RecapGridStoreOpenResult;
    public sealed record Busy : RecapGridStoreOpenResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridStoreOpenResult;
    public sealed record PlatformUnsupported : RecapGridStoreOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridStoreOpenResult;
}

public abstract record RecapGridStoreReaderOpenResult {
    private RecapGridStoreReaderOpenResult() { }
    public sealed record Opened(RecapGridStoreReaderHandle Handle)
        : RecapGridStoreReaderOpenResult;
    public sealed record Absent : RecapGridStoreReaderOpenResult;
    public sealed record Busy : RecapGridStoreReaderOpenResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridStoreReaderOpenResult;
    public sealed record PlatformUnsupported : RecapGridStoreReaderOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridStoreReaderOpenResult;
}

public abstract record RecapGridStoreReadResult<T> where T : class {
    private RecapGridStoreReadResult() { }
    public sealed record Found(T Value) : RecapGridStoreReadResult<T>;
    public sealed record Missing : RecapGridStoreReadResult<T>;
    public sealed record Busy : RecapGridStoreReadResult<T>;
    public sealed record Disposed : RecapGridStoreReadResult<T>;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridStoreReadResult<T>;
}

internal sealed record RecapGridFulfilledView(RowViewDigest ViewDigest);

internal abstract record RecapGridMissingResult {
    private RecapGridMissingResult() { }
    public sealed record Complete : RecapGridMissingResult;
    public sealed record Missing(IReadOnlyList<EvaluationKey> OrderedKeys)
        : RecapGridMissingResult;
    public sealed record PrerequisiteMissing(
        LogicalColumnId LogicalColumnId,
        CellDigest CellDigest
    ) : RecapGridMissingResult;
    public sealed record Busy : RecapGridMissingResult;
    public sealed record Disposed : RecapGridMissingResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridMissingResult;
}

internal abstract record RecapGridCellPutResult {
    private RecapGridCellPutResult() { }
    public sealed record Inserted : RecapGridCellPutResult;
    public sealed record AlreadyFilled(RecapCellArtifact Winner)
        : RecapGridCellPutResult;
    public sealed record Rejected(string Code) : RecapGridCellPutResult;
    public sealed record Busy : RecapGridCellPutResult;
    public sealed record Limit(string Name) : RecapGridCellPutResult;
    public sealed record CommitIndeterminate(
        EvaluationKeyDigest IntendedKey,
        RecapCellArtifact? Observed
    ) : RecapGridCellPutResult;
    public sealed record Disposed : RecapGridCellPutResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridCellPutResult;
}

internal abstract record RecapGridRowViewPutResult {
    private RecapGridRowViewPutResult() { }
    public sealed record Inserted : RecapGridRowViewPutResult;
    public sealed record AlreadyPresent : RecapGridRowViewPutResult;
    public sealed record Rejected(string Code) : RecapGridRowViewPutResult;
    public sealed record PrerequisiteMissing(string Code)
        : RecapGridRowViewPutResult;
    public sealed record Busy : RecapGridRowViewPutResult;
    public sealed record Limit(string Name) : RecapGridRowViewPutResult;
    public sealed record CommitIndeterminate(
        RowViewAssignmentKey IntendedAssignment,
        RowViewDigest Intended,
        RowViewDigest? Observed
    ) : RecapGridRowViewPutResult;
    public sealed record Disposed : RecapGridRowViewPutResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridRowViewPutResult;
}

internal abstract record RecapGridFulfilledPutResult {
    private RecapGridFulfilledPutResult() { }
    public sealed record Inserted : RecapGridFulfilledPutResult;
    public sealed record AlreadyPresent : RecapGridFulfilledPutResult;
    public sealed record Rejected(string Code) : RecapGridFulfilledPutResult;
    public sealed record PrerequisiteMissing(string Code)
        : RecapGridFulfilledPutResult;
    public sealed record Busy : RecapGridFulfilledPutResult;
    public sealed record Limit(string Name) : RecapGridFulfilledPutResult;
    public sealed record CommitIndeterminate(
        FulfilledViewKey Intended,
        RowViewDigest? Observed
    ) : RecapGridFulfilledPutResult;
    public sealed record Disposed : RecapGridFulfilledPutResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridFulfilledPutResult;
}

public sealed class RecapGridStoreHandle : IDisposable {
    private readonly StoreLifetime _lifetime;

    internal RecapGridStoreHandle(
        RecapGridStoreIdentity identity,
        RecapGridStoreReader reader,
        RecapGridStoreWriter writer,
        StoreLifetime lifetime
    ) {
        Identity = identity;
        Reader = reader;
        Writer = writer;
        _lifetime = lifetime;
    }

    public RecapGridStoreIdentity Identity { get; }
    public RecapGridStoreReader Reader { get; }
    internal RecapGridStoreWriter Writer { get; }
    public void Dispose() => _lifetime.Dispose();
}

public sealed class RecapGridStoreReaderHandle : IDisposable {
    private readonly StoreLifetime _lifetime;

    internal RecapGridStoreReaderHandle(
        RecapGridStoreIdentity identity,
        RecapGridStoreReader reader,
        StoreLifetime lifetime
    ) {
        Identity = identity;
        Reader = reader;
        _lifetime = lifetime;
    }

    public RecapGridStoreIdentity Identity { get; }
    public RecapGridStoreReader Reader { get; }
    public void Dispose() => _lifetime.Dispose();
}

internal static class StoreSyntax {
    internal static string RequireLowerHex(
        string value,
        int length,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != length || value.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))) {
            throw new ArgumentException(
                $"The value must contain {length} lowercase hexadecimal characters.",
                parameterName
            );
        }
        return value;
    }
}
