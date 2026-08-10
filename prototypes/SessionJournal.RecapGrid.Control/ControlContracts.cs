using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Control;

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

public sealed class RecapGridControlAdmission {
    private readonly HashSet<FamilyDefinitionDigest> _familyAllowlist;
    private readonly HashSet<string> _capabilityAllowlist;
    private readonly HashSet<ContextHeaderCarrier> _targetCarrierAllowlist;
    private readonly ReadOnlyCollection<string> _logicalColumnPrefixes;

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
            or > HistoryTimelineStoreLimits.MaximumRowCount) {
            throw new ArgumentOutOfRangeException(nameof(maximumBootstrapRows));
        }
        if (maximumProjectedCalls is < 0 or > 1_000_000) {
            throw new ArgumentOutOfRangeException(nameof(maximumProjectedCalls));
        }
        MaximumBootstrapRows = maximumBootstrapRows;
        MaximumProjectedCalls = maximumProjectedCalls;
    }

    public RecapGridControlPermission Permissions { get; }
    public int MaximumBootstrapRows { get; }
    public int MaximumProjectedCalls { get; }

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
