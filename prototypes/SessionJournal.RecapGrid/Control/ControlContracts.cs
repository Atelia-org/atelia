using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Control;

internal static class RecapGridControlAdmissionLimits {
    public const int MaximumCanonicalUtf8Bytes = 64 * 1024;
    public const int MaximumBootstrapRows = 1_000_000;
}

public readonly record struct ControlInstanceId {
    public ControlInstanceId(string value) {
        Value = RequireHex(value, 32, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;

    internal static ControlInstanceId Generate() => new(
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
    );

    private static string RequireHex(
        string value,
        int length,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != length
            || value.Any(static character =>
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

public readonly record struct ControlStateDigest {
    public ControlStateDigest(string value) {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64
            || value.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))) {
            throw new ArgumentException(
                "A Control state digest must be lowercase SHA-256.",
                nameof(value)
            );
        }
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public sealed record ControlHeadRef {
    public ControlHeadRef(
        ControlInstanceId instanceId,
        RefId refId,
        TimelineId timelineId,
        long generation,
        ControlStateDigest stateDigest,
        GridBuildRecipeDigest? activeRecipeDigest
    ) {
        if (instanceId.Value is null) {
            throw new ArgumentException(
                "ControlInstanceId must not be default.",
                nameof(instanceId)
            );
        }
        if (refId.IsDefault) {
            throw new ArgumentException("RefId must not be default.", nameof(refId));
        }
        if (timelineId.Value is null) {
            throw new ArgumentException(
                "TimelineId must not be default.",
                nameof(timelineId)
            );
        }
        if (generation < 0) {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        if (stateDigest.Value is null) {
            throw new ArgumentException(
                "ControlStateDigest must not be default.",
                nameof(stateDigest)
            );
        }
        if (activeRecipeDigest is { Value: null }) {
            throw new ArgumentException(
                "GridBuildRecipeDigest must not be default.",
                nameof(activeRecipeDigest)
            );
        }
        InstanceId = instanceId;
        RefId = refId;
        TimelineId = timelineId;
        Generation = generation;
        StateDigest = stateDigest;
        ActiveRecipeDigest = activeRecipeDigest;
    }

    public ControlInstanceId InstanceId { get; }
    public RefId RefId { get; }
    public TimelineId TimelineId { get; }
    public long Generation { get; }
    public ControlStateDigest StateDigest { get; }
    public GridBuildRecipeDigest? ActiveRecipeDigest { get; }
}

[Flags]
public enum RecapGridControlPermission {
    None = 0,
    Create = 1,
    RegisterFamily = 2,
    RegisterDefinition = 4,
    RegisterRecipe = 8,
    Activate = 16,
    Promote = 32,
    All = Create | RegisterFamily | RegisterDefinition | RegisterRecipe
        | Activate | Promote
}

public enum RecapGridControlActivationPurpose {
    Direct,
    Promotion
}

/// <summary>
/// Stable authority for one recoverable Control mutation. The raw operation
/// id is validated and reduced to a bounded domain-separated digest before it
/// can enter Control persistence.
/// </summary>
public sealed class RecapGridControlOperation {
    private const int MaximumOperationIdUtf8Bytes = 512;

    private RecapGridControlOperation(
        string operationKey,
        long executionSequence,
        string runtimeIdentityDigest
    ) {
        OperationKey = operationKey;
        ExecutionSequence = executionSequence;
        RuntimeIdentityDigest = runtimeIdentityDigest;
    }

    public string OperationKey { get; }
    public long ExecutionSequence { get; }
    public string RuntimeIdentityDigest { get; }

    public static RecapGridControlOperation Create(
        string operationId,
        long executionSequence,
        string runtimeIdentityDigest
    ) {
        ArgumentNullException.ThrowIfNull(operationId);
        if (executionSequence <= 0) {
            throw new ArgumentOutOfRangeException(nameof(executionSequence));
        }
        if (string.IsNullOrWhiteSpace(operationId)
            || !string.Equals(operationId, operationId.Trim(),
                StringComparison.Ordinal)
            || operationId.Any(char.IsControl)) {
            throw new ArgumentException(
                "Operation id must be non-empty canonical text.",
                nameof(operationId)
            );
        }
        int operationBytes;
        try {
            operationBytes = new UTF8Encoding(false, true)
                .GetByteCount(operationId);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Operation id must be strict UTF-8 text.",
                nameof(operationId),
                exception
            );
        }
        if (operationBytes > MaximumOperationIdUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(operationId));
        }
        RequireSha256(runtimeIdentityDigest, nameof(runtimeIdentityDigest));
        return new RecapGridControlOperation(
            DomainHash(
                "atelia.recap-grid.control-operation-key.v1",
                Encoding.UTF8.GetBytes(operationId)
            ),
            executionSequence,
            runtimeIdentityDigest
        );
    }

    internal static void RequireSha256(string value, string parameterName) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64
            || value.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))) {
            throw new ArgumentException(
                "The value must be a lowercase SHA-256 digest.",
                parameterName
            );
        }
    }

    internal static string DomainHash(
        string domain,
        ReadOnlySpan<byte> value
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        Append(Encoding.UTF8.GetBytes(domain));
        Append(value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(ReadOnlySpan<byte> bytes) {
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                length,
                bytes.Length
            );
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}

public sealed record RecapGridControlRecipeRegistration(
    GridBuildRecipe Recipe,
    HistoryTimelineAncestorWitness? BootstrapWitness
);

/// <summary>
/// One atomic registration command. Collections are defensively materialized;
/// ordering is part of the canonical command and recipes must be base-first.
/// </summary>
public sealed class RecapGridControlRegistrationBundle {
    public RecapGridControlRegistrationBundle(
        IEnumerable<FamilyDefinition> families,
        IEnumerable<MaintainerDefinitionRevision> definitions,
        IEnumerable<RecapGridControlRecipeRegistration> recipes
    ) {
        Families = Materialize(families,
            ControlStorageLimits.MaximumFamilyCount, nameof(families));
        Definitions = Materialize(definitions,
            ControlStorageLimits.MaximumDefinitionCount, nameof(definitions));
        Recipes = Materialize(recipes,
            ControlStorageLimits.MaximumRecipeCount, nameof(recipes));
        if (Families.Count + Definitions.Count + Recipes.Count == 0) {
            throw new ArgumentException(
                "A registration bundle must contain at least one value."
            );
        }
        if (Families.Any(static value => value is null)
            || Definitions.Any(static value => value is null)
            || Recipes.Any(static value => value is null
                || value.Recipe is null)) {
            throw new ArgumentException(
                "Registration bundle values must not be null."
            );
        }
        RequireUnique(Families.Select(static value => value.Digest.Value));
        RequireUnique(Definitions.Select(static value => value.Digest.Value));
        RequireUnique(Recipes.Select(static value => value.Recipe.Digest.Value));
    }

    public IReadOnlyList<FamilyDefinition> Families { get; }
    public IReadOnlyList<MaintainerDefinitionRevision> Definitions { get; }
    public IReadOnlyList<RecapGridControlRecipeRegistration> Recipes { get; }

    /// <summary>
    /// Control-owned digest of the exact canonical registration command.
    /// Consumers may bind this value into a capability identity, but must not
    /// reproduce the registration command codec or hashing domain.
    /// </summary>
    public string CanonicalCommandDigest =>
        ControlOperationCanonicalizer.RegistrationDigest(this);

    /// <summary>
    /// Exact Control-owned canonical registration command bytes. The returned
    /// array is a fresh defensive value so capability owners can bind the
    /// command without reproducing this codec.
    /// </summary>
    public byte[] ToCanonicalCommandBytes() =>
        ControlOperationCanonicalizer.EncodeRegistration(this);

    private static IReadOnlyList<T> Materialize<T>(
        IEnumerable<T> source,
        int maximumCount,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        using IEnumerator<T> enumerator = source.GetEnumerator();
        var values = new List<T>(Math.Min(maximumCount, 16));
        while (values.Count <= maximumCount && enumerator.MoveNext()) {
            values.Add(enumerator.Current);
        }
        if (values.Count > maximumCount) {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return Array.AsReadOnly(values.ToArray());
    }

    private static void RequireUnique(IEnumerable<string> values) {
        if (values.Distinct(StringComparer.Ordinal).Count()
            != values.Count()) {
            throw new ArgumentException(
                "Registration bundle identities must be unique."
            );
        }
    }
}

public sealed class RecapGridControlAdmission {
    private readonly HashSet<FamilyDefinitionDigest> _familyAllowlist;
    private readonly HashSet<string> _capabilityAllowlist;
    private readonly HashSet<ContextHeaderCarrier> _targetCarrierAllowlist;
    private readonly ReadOnlyCollection<string> _logicalColumnPrefixes;
    private readonly byte[] _canonicalBytes;

    public RecapGridControlAdmission(
        RecapGridControlPermission permissions,
        IEnumerable<FamilyDefinitionDigest> familyAllowlist,
        IEnumerable<string> capabilityFingerprintAllowlist,
        IEnumerable<ContextHeaderCarrier> targetCarrierAllowlist,
        IEnumerable<string> logicalColumnPrefixes,
        int maximumBootstrapRows,
        int maximumProjectedCalls
    ) {
        if ((permissions & ~RecapGridControlPermission.All) != 0) {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }
        Permissions = permissions;
        FamilyDefinitionDigest[] families = MaterializeBounded(
            familyAllowlist,
            ControlStorageLimits.MaximumFamilyCount,
            nameof(familyAllowlist)
        );
        if (families.Any(static digest => digest.Value is null)) {
            throw new ArgumentException(
                "Family allowlist identities must not be default.",
                nameof(familyAllowlist)
            );
        }
        _familyAllowlist = families.ToHashSet();
        string[] capabilities = MaterializeBounded(
            capabilityFingerprintAllowlist,
            ControlStorageLimits.MaximumFamilyCount,
            nameof(capabilityFingerprintAllowlist)
        );
        if (capabilities.Any(static value => value is null
                || value.Length != 64
                || value.Any(static character =>
                    character is not (>= '0' and <= '9')
                        and not (>= 'a' and <= 'f')))) {
            throw new ArgumentException(
                "Capability allowlist entries must be lowercase SHA-256.",
                nameof(capabilityFingerprintAllowlist)
            );
        }
        _capabilityAllowlist = capabilities.ToHashSet(
            StringComparer.Ordinal
        );
        ContextHeaderCarrier[] carriers = MaterializeBounded(
            targetCarrierAllowlist,
            16,
            nameof(targetCarrierAllowlist)
        );
        if (carriers.Any(static carrier => !Enum.IsDefined(carrier))) {
            throw new ArgumentException(
                "Target carrier allowlist contains an unsupported carrier.",
                nameof(targetCarrierAllowlist)
            );
        }
        _targetCarrierAllowlist = carriers.ToHashSet();
        string[] prefixes = MaterializeBounded(
            logicalColumnPrefixes,
            RecapGridLimits.MaximumColumnCount,
            nameof(logicalColumnPrefixes)
        );
        if (prefixes.Length == 0
            || prefixes.Any(static prefix => !ValidPrefix(prefix))
            || prefixes.Distinct(StringComparer.Ordinal).Count()
                != prefixes.Length) {
            throw new ArgumentException(
                "At least one non-empty logical-column prefix is required.",
                nameof(logicalColumnPrefixes)
            );
        }
        _logicalColumnPrefixes = Array.AsReadOnly(prefixes);
        if (maximumBootstrapRows is < 0
            or > RecapGridControlAdmissionLimits.MaximumBootstrapRows) {
            throw new ArgumentOutOfRangeException(nameof(maximumBootstrapRows));
        }
        if (maximumProjectedCalls is < 0 or > 1_000_000) {
            throw new ArgumentOutOfRangeException(nameof(maximumProjectedCalls));
        }
        MaximumBootstrapRows = maximumBootstrapRows;
        MaximumProjectedCalls = maximumProjectedCalls;
        _canonicalBytes = EncodeCanonical();
        if (_canonicalBytes.Length
            > RecapGridControlAdmissionLimits.MaximumCanonicalUtf8Bytes) {
            throw new ArgumentException(
                "Control admission exceeds its canonical V1 byte bound."
            );
        }
    }

    public RecapGridControlPermission Permissions { get; }
    public int MaximumBootstrapRows { get; }
    public int MaximumProjectedCalls { get; }

    public byte[] ToCanonicalBytes() => _canonicalBytes.ToArray();

    public static RecapGridControlAdmission DecodeCanonical(
        ReadOnlySpan<byte> bytes
    ) {
        if (bytes.Length is < 2
            or > RecapGridControlAdmissionLimits.MaximumCanonicalUtf8Bytes) {
            throw new InvalidDataException(
                "Control admission canonical bytes exceed the V1 bound."
            );
        }
        ControlAdmissionDto? value;
        try {
            value = JsonSerializer.Deserialize<ControlAdmissionDto>(
                bytes,
                ControlJson.Options
            );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                "Control admission is not strict JSON.",
                exception
            );
        }
        if (value is null
            || value.SchemaVersion != 1
            || !bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(
                value,
                ControlJson.Options
            ))) {
            throw new InvalidDataException(
                "Control admission is not exact canonical V1 bytes."
            );
        }
        if (value.FamilyDigests is null
            || value.CapabilityFingerprints is null
            || value.TargetCarriers is null
            || value.LogicalColumnPrefixes is null) {
            throw new InvalidDataException(
                "Control admission sets must not be null."
            );
        }
        RequireSortedUnique(value.FamilyDigests, StringComparer.Ordinal);
        RequireSortedUnique(
            value.CapabilityFingerprints,
            StringComparer.Ordinal
        );
        RequireSortedUnique(
            value.LogicalColumnPrefixes,
            StringComparer.Ordinal
        );
        RequireSortedUnique(value.TargetCarriers, Comparer<int>.Default);
        return new RecapGridControlAdmission(
            (RecapGridControlPermission)value.Permissions,
            value.FamilyDigests.Select(static digest =>
                new FamilyDefinitionDigest(digest)),
            value.CapabilityFingerprints,
            value.TargetCarriers.Select(static carrier =>
                (ContextHeaderCarrier)carrier),
            value.LogicalColumnPrefixes,
            value.MaximumBootstrapRows,
            value.MaximumProjectedCalls
        );

        static void RequireSortedUnique<T>(
            IReadOnlyList<T> values,
            IComparer<T> comparer
        ) {
            for (int index = 1; index < values.Count; index++) {
                if (comparer.Compare(values[index - 1], values[index]) >= 0) {
                    throw new InvalidDataException(
                        "Control admission sets must be strictly sorted."
                    );
                }
            }
        }
    }

    private byte[] EncodeCanonical() => JsonSerializer.SerializeToUtf8Bytes(
        new ControlAdmissionDto(
            1,
            (int)Permissions,
            _familyAllowlist.Select(static value => value.Value)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            _capabilityAllowlist.Order(StringComparer.Ordinal).ToArray(),
            _targetCarrierAllowlist.Select(static value => (int)value)
                .Order()
                .ToArray(),
            _logicalColumnPrefixes.Order(StringComparer.Ordinal).ToArray(),
            MaximumBootstrapRows,
            MaximumProjectedCalls
        ),
        ControlJson.Options
    );

    internal bool Allows(RecapGridControlPermission permission)
        => (Permissions & permission) == permission;

    internal bool AllowsFamily(FamilyDefinitionDigest digest)
        => _familyAllowlist.Contains(digest);

    internal bool AllowsCapability(string fingerprint)
        => _capabilityAllowlist.Contains(fingerprint);

    internal bool AllowsTarget(ContextHeaderCarrier carrier)
        => _targetCarrierAllowlist.Contains(carrier);

    internal bool AllowsColumn(LogicalColumnId id)
        => _logicalColumnPrefixes.Any(prefix => id.Value.StartsWith(
            prefix,
            StringComparison.Ordinal
        ));

    private static T[] MaterializeBounded<T>(
        IEnumerable<T> source,
        int maximumCount,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        using IEnumerator<T> enumerator = source.GetEnumerator();
        var values = new List<T>(Math.Min(maximumCount, 16));
        while (values.Count <= maximumCount && enumerator.MoveNext()) {
            values.Add(enumerator.Current);
        }
        if (values.Count > maximumCount) {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return values.ToArray();
    }

    private static bool ValidPrefix(string? value) {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl)) {
            return false;
        }
        try {
            return new UTF8Encoding(false, true).GetByteCount(value)
                <= RecapGridLimits.MaximumIdentifierUtf8Bytes;
        }
        catch (EncoderFallbackException) {
            return false;
        }
    }
}

internal sealed record ControlAdmissionDto(
    int SchemaVersion,
    int Permissions,
    string[] FamilyDigests,
    string[] CapabilityFingerprints,
    int[] TargetCarriers,
    string[] LogicalColumnPrefixes,
    int MaximumBootstrapRows,
    int MaximumProjectedCalls
);

public sealed class RegisteredRecipeBootstrap {
    internal RegisteredRecipeBootstrap(
        TimelineHeadRef timelineHead,
        HistoryRowId? rowId,
        HistorySegmentDescriptorDigest? descriptorDigest
    ) {
        TimelineHead = timelineHead;
        RowId = rowId;
        DescriptorDigest = descriptorDigest;
    }

    public TimelineHeadRef TimelineHead { get; }
    public HistoryRowId? RowId { get; }
    public HistorySegmentDescriptorDigest? DescriptorDigest { get; }
    public bool IsEmpty => RowId is null;
}

public sealed class RegisteredGridRecipe {
    internal RegisteredGridRecipe(
        GridBuildRecipe recipe,
        RegisteredRecipeBootstrap bootstrap
    ) {
        Recipe = recipe;
        Bootstrap = bootstrap;
    }

    public GridBuildRecipe Recipe { get; }
    public RegisteredRecipeBootstrap Bootstrap { get; }
}

public sealed class RecapGridControlSnapshot {
    internal RecapGridControlSnapshot(
        ControlHeadRef head,
        IReadOnlyList<FamilyDefinition> families,
        IReadOnlyList<MaintainerDefinitionRevision> definitions,
        IReadOnlyList<RegisteredGridRecipe> recipes
    ) {
        Head = head;
        Families = families;
        Definitions = definitions;
        Recipes = recipes;
    }

    public ControlHeadRef Head { get; }
    public IReadOnlyList<FamilyDefinition> Families { get; }
    public IReadOnlyList<MaintainerDefinitionRevision> Definitions { get; }
    public IReadOnlyList<RegisteredGridRecipe> Recipes { get; }

    public RegisteredGridRecipe? ActiveRecipe => Head.ActiveRecipeDigest is { } digest
        ? Recipes.Single(recipe => recipe.Recipe.Digest == digest)
        : null;
}

public abstract record RecapGridControlCreateResult {
    private RecapGridControlCreateResult() { }
    public sealed record Created(ControlHeadRef Head)
        : RecapGridControlCreateResult;
    public sealed record CommitIndeterminate(
        ControlHeadRef Intended,
        ControlHeadRef? Observed
    ) : RecapGridControlCreateResult;
    public sealed record AlreadyExists : RecapGridControlCreateResult;
    public sealed record TimelineAbsent : RecapGridControlCreateResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlCreateResult;
    public sealed record ControlUnsupportedSchema(int SchemaVersion)
        : RecapGridControlCreateResult;
    public sealed record Unauthorized(string Rule)
        : RecapGridControlCreateResult;
    public sealed record Busy : RecapGridControlCreateResult;
    public sealed record LimitExceeded(string Limit)
        : RecapGridControlCreateResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlCreateResult;
}

public abstract record RecapGridControlOpenResult {
    private RecapGridControlOpenResult() { }
    public sealed record Opened(RecapGridControlHandle Handle)
        : RecapGridControlOpenResult;
    public sealed record Absent : RecapGridControlOpenResult;
    public sealed record TimelineAbsent : RecapGridControlOpenResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlOpenResult;
    public sealed record Busy : RecapGridControlOpenResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridControlOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlOpenResult;
}

public abstract record RecapGridControlReaderOpenResult {
    private RecapGridControlReaderOpenResult() { }
    public sealed record Opened(RecapGridControlReaderHandle Handle)
        : RecapGridControlReaderOpenResult;
    public sealed record Absent : RecapGridControlReaderOpenResult;
    public sealed record TimelineAbsent : RecapGridControlReaderOpenResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlReaderOpenResult;
    public sealed record Busy : RecapGridControlReaderOpenResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridControlReaderOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlReaderOpenResult;
}

public abstract record RecapGridControlSnapshotResult {
    private RecapGridControlSnapshotResult() { }
    public sealed record Available(RecapGridControlSnapshot Snapshot)
        : RecapGridControlSnapshotResult;
    public sealed record Busy : RecapGridControlSnapshotResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridControlSnapshotResult;
    public sealed record Disposed : RecapGridControlSnapshotResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlSnapshotResult;
}

public abstract record RecapGridControlPutResult {
    private RecapGridControlPutResult() { }
    public sealed record Stored(ControlHeadRef Head)
        : RecapGridControlPutResult;
    public sealed record CommitIndeterminate(
        ControlHeadRef Intended,
        ControlHeadRef? Observed
    ) : RecapGridControlPutResult;
    public sealed record AlreadyPresent(ControlHeadRef Head)
        : RecapGridControlPutResult;
    public sealed record Unauthorized(string Rule)
        : RecapGridControlPutResult;
    public sealed record StaleControlHead(ControlHeadRef Actual)
        : RecapGridControlPutResult;
    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : RecapGridControlPutResult;
    public sealed record NotOnSelectedPath(HistoryRowId RowId)
        : RecapGridControlPutResult;
    public sealed record Busy : RecapGridControlPutResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlPutResult;
    public sealed record Disposed : RecapGridControlPutResult;
    public sealed record LimitExceeded(string Limit)
        : RecapGridControlPutResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlPutResult;
}

public abstract record RecapGridControlActivateResult {
    private RecapGridControlActivateResult() { }
    public sealed record Applied(ControlHeadRef Head)
        : RecapGridControlActivateResult;
    public sealed record CommitIndeterminate(
        ControlHeadRef Intended,
        ControlHeadRef? Observed
    ) : RecapGridControlActivateResult;
    public sealed record AlreadyActive(ControlHeadRef Head)
        : RecapGridControlActivateResult;
    public sealed record Unauthorized(string Rule)
        : RecapGridControlActivateResult;
    public sealed record RecipeAbsent(GridBuildRecipeDigest RecipeDigest)
        : RecapGridControlActivateResult;
    public sealed record StaleControlHead(ControlHeadRef Actual)
        : RecapGridControlActivateResult;
    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : RecapGridControlActivateResult;
    public sealed record BootstrapNotSelected(HistoryRowId RowId)
        : RecapGridControlActivateResult;
    public sealed record Busy : RecapGridControlActivateResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlActivateResult;
    public sealed record Disposed : RecapGridControlActivateResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlActivateResult;
}

public abstract record RecapGridControlOperationResult {
    private RecapGridControlOperationResult() { }

    public sealed record Applied(
        ControlHeadRef Head,
        string ResultIdentity
    ) : RecapGridControlOperationResult;

    public sealed record Replayed(
        ControlHeadRef CurrentHead,
        ControlInstanceId OriginalInstanceId,
        long OriginalGeneration,
        string ResultIdentity,
        bool HeadAdvancedSinceApply,
        bool InstanceReplaced
    ) : RecapGridControlOperationResult;

    public sealed record Conflict(string OperationKey)
        : RecapGridControlOperationResult;
    public sealed record Unauthorized(string Rule)
        : RecapGridControlOperationResult;
    public sealed record RecipeAbsent(GridBuildRecipeDigest RecipeDigest)
        : RecapGridControlOperationResult;
    public sealed record StaleControlHead(ControlHeadRef Actual)
        : RecapGridControlOperationResult;
    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : RecapGridControlOperationResult;
    public sealed record NotOnSelectedPath(HistoryRowId RowId)
        : RecapGridControlOperationResult;
    public sealed record Busy : RecapGridControlOperationResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlOperationResult;
    public sealed record Disposed : RecapGridControlOperationResult;
    public sealed record LimitExceeded(string Limit)
        : RecapGridControlOperationResult;
    public sealed record CommitIndeterminate(
        string OperationKey,
        ControlHeadRef Intended,
        ControlHeadRef? Observed
    ) : RecapGridControlOperationResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlOperationResult;
}

public sealed class RecapGridControlHandle : IDisposable {
    private readonly ControlLifetime _lifetime;

    internal RecapGridControlHandle(
        RecapGridControlReader reader,
        RecapGridControlCoordinator coordinator,
        ControlLifetime lifetime
    ) {
        Reader = reader;
        Coordinator = coordinator;
        _lifetime = lifetime;
    }

    public RecapGridControlReader Reader { get; }
    public RecapGridControlCoordinator Coordinator { get; }
    public void Dispose() => _lifetime.Dispose();
}

public sealed class RecapGridControlReaderHandle : IDisposable {
    private readonly ControlLifetime _lifetime;

    internal RecapGridControlReaderHandle(
        RecapGridControlReader reader,
        ControlLifetime lifetime
    ) {
        Reader = reader;
        _lifetime = lifetime;
    }

    public RecapGridControlReader Reader { get; }
    public void Dispose() => _lifetime.Dispose();
}

public sealed record RecapGridControlBackupManifest {
    internal RecapGridControlBackupManifest(
        ControlHeadRef head,
        string stateFileSha256,
        long stateFileBytes
    ) {
        Head = head ?? throw new ArgumentNullException(nameof(head));
        StateFileSha256 = stateFileSha256;
        StateFileBytes = stateFileBytes;
    }

    public ControlHeadRef Head { get; }
    public string StateFileSha256 { get; }
    public long StateFileBytes { get; }
}

public abstract record RecapGridControlInspectResult {
    private RecapGridControlInspectResult() { }
    public sealed record Available(RecapGridControlSnapshot Snapshot)
        : RecapGridControlInspectResult;
    public sealed record Absent : RecapGridControlInspectResult;
    public sealed record TimelineAbsent : RecapGridControlInspectResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlInspectResult;
    public sealed record Busy : RecapGridControlInspectResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridControlInspectResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlInspectResult;
}

public abstract record RecapGridControlExportResult {
    private RecapGridControlExportResult() { }
    public sealed record Available(
        RecapGridControlSnapshot Snapshot,
        byte[] CanonicalState
    ) : RecapGridControlExportResult;
    public sealed record Absent : RecapGridControlExportResult;
    public sealed record TimelineAbsent : RecapGridControlExportResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlExportResult;
    public sealed record Busy : RecapGridControlExportResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : RecapGridControlExportResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlExportResult;
}

public abstract record RecapGridControlBackupResult {
    private RecapGridControlBackupResult() { }
    public sealed record Created(RecapGridControlBackupManifest Manifest)
        : RecapGridControlBackupResult;
    public sealed record PublishIndeterminate(
        RecapGridControlBackupManifest Intended,
        RecapGridControlBackupManifest? Observed
    ) : RecapGridControlBackupResult;
    public sealed record Absent : RecapGridControlBackupResult;
    public sealed record TimelineAbsent : RecapGridControlBackupResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlBackupResult;
    public sealed record ControlUnsupportedSchema(int SchemaVersion)
        : RecapGridControlBackupResult;
    public sealed record StaleControlHead(ControlHeadRef Actual)
        : RecapGridControlBackupResult;
    public sealed record Busy : RecapGridControlBackupResult;
    public sealed record LimitExceeded(string Limit)
        : RecapGridControlBackupResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlBackupResult;
}

public abstract record RecapGridControlAdminResult {
    private RecapGridControlAdminResult() { }
    public sealed record Applied(ControlHeadRef Head)
        : RecapGridControlAdminResult;
    public sealed record CommitIndeterminate(
        ControlHeadRef Intended,
        ControlHeadRef? Observed
    ) : RecapGridControlAdminResult;
    public sealed record Absent : RecapGridControlAdminResult;
    public sealed record TimelineAbsent : RecapGridControlAdminResult;
    public sealed record TimelineUnsupportedSchema(int SchemaVersion)
        : RecapGridControlAdminResult;
    public sealed record ControlUnsupportedSchema(int SchemaVersion)
        : RecapGridControlAdminResult;
    public sealed record StaleControlHead(ControlHeadRef Actual)
        : RecapGridControlAdminResult;
    public sealed record Busy : RecapGridControlAdminResult;
    public sealed record LimitExceeded(string Limit)
        : RecapGridControlAdminResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridControlAdminResult;
}
