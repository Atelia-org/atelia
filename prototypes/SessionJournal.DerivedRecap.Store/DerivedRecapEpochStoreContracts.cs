using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

public sealed record DerivedRecapEpochStoreLimits {
    public DerivedRecapEpochStoreLimits(
        int maxRecapBlockCount =
            SessionContextContributionContract.MaxContributionCount,
        int maxTotalRecapPackUtf8Bytes = 2 * 1024 * 1024,
        int maxCanonicalPriorPackBytes = 5 * 1024 * 1024,
        int maxEpochInputBytes = 8 * 1024 * 1024,
        int maxManifestBytes = 2 * 1024 * 1024,
        int maxFinalBlockBytes = 512 * 1024,
        int maxPublicationBytes = 3 * 1024 * 1024
    ) {
        MaxRecapBlockCount = RequirePositive(
            maxRecapBlockCount,
            nameof(maxRecapBlockCount)
        );
        if (MaxRecapBlockCount
            > SessionContextContributionContract.MaxContributionCount) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRecapBlockCount)
            );
        }
        MaxTotalRecapPackUtf8Bytes = RequirePositive(
            maxTotalRecapPackUtf8Bytes,
            nameof(maxTotalRecapPackUtf8Bytes)
        );
        MaxCanonicalPriorPackBytes = RequirePositive(
            maxCanonicalPriorPackBytes,
            nameof(maxCanonicalPriorPackBytes)
        );
        MaxEpochInputBytes = RequirePositive(
            maxEpochInputBytes,
            nameof(maxEpochInputBytes)
        );
        MaxManifestBytes = RequirePositive(
            maxManifestBytes,
            nameof(maxManifestBytes)
        );
        MaxFinalBlockBytes = RequirePositive(
            maxFinalBlockBytes,
            nameof(maxFinalBlockBytes)
        );
        MaxPublicationBytes = RequirePositive(
            maxPublicationBytes,
            nameof(maxPublicationBytes)
        );
    }

    public int MaxRecapBlockCount { get; }
    public int MaxTotalRecapPackUtf8Bytes { get; }
    public int MaxCanonicalPriorPackBytes { get; }
    public int MaxEpochInputBytes { get; }
    public int MaxManifestBytes { get; }
    public int MaxFinalBlockBytes { get; }
    public int MaxPublicationBytes { get; }

    private static int RequirePositive(int value, string name)
        => value > 0
            ? value
            : throw new ArgumentOutOfRangeException(name);
}

public sealed record RecapEpochBuildingDescriptor(
    RefId RefId,
    EventAddress AdmissionAnchor,
    string ManifestPayloadSha256
);

public enum RecapEpochFinalStage {
    Building = 1,
    Published = 2,
}

public enum RecapEpochPublishedAuthorityKind {
    Publication = 1,
    ManifestWitness = 2,
}

public sealed class RecapEpochPublishedRepairAuthority {
    internal RecapEpochPublishedRepairAuthority(
        string ownerPath,
        EventAddress admissionAnchor,
        string manifestPayloadSha256,
        RecapEpochPublishedAuthorityKind kind,
        string stateToken
    ) {
        OwnerPath = ownerPath;
        AdmissionAnchor = admissionAnchor;
        ManifestPayloadSha256 = manifestPayloadSha256;
        Kind = kind;
        StateToken = stateToken;
    }

    internal string OwnerPath { get; }
    internal string StateToken { get; }
    public EventAddress AdmissionAnchor { get; }
    public string ManifestPayloadSha256 { get; }
    public RecapEpochPublishedAuthorityKind Kind { get; }
}

public abstract record RecapEpochFinalHealth {
    public abstract string StateToken { get; init; }

    public sealed record Missing(string StateToken)
        : RecapEpochFinalHealth;

    public sealed record Healthy(
        DerivedRecapFinalBlock Block,
        string StateToken
    ) : RecapEpochFinalHealth;

    public sealed record Damaged(
        string Detail,
        string StateToken
    ) : RecapEpochFinalHealth;

    public sealed record Unavailable(string Detail)
        : RecapEpochFinalHealth {
        public override string StateToken { get; init; } = "unavailable";
    }
}

public sealed class RecapEpochFinalWriteAuthority {
    internal RecapEpochFinalWriteAuthority(
        string ownerPath,
        RecapEpochFinalStage stage,
        RecapEpochBuildingDescriptor building,
        RecapBlockId recapBlockId,
        string stateToken,
        string? publishedAuthorityStateToken
    ) {
        OwnerPath = ownerPath;
        Stage = stage;
        Building = building;
        RecapBlockId = recapBlockId;
        StateToken = stateToken;
        PublishedAuthorityStateToken = publishedAuthorityStateToken;
    }

    internal string OwnerPath { get; }
    internal RecapEpochFinalStage Stage { get; }
    internal RecapEpochBuildingDescriptor Building { get; }
    internal RecapBlockId RecapBlockId { get; }
    internal string StateToken { get; }
    internal string? PublishedAuthorityStateToken { get; }
}

public sealed record RecapEpochBlockInspection(
    RecapEpochBlockDefinition Definition,
    RecapEpochFinalHealth Final,
    RecapEpochFinalWriteAuthority? WriteAuthority
);

public sealed record RecapEpochStoreSnapshot(
    RecapEpochFinalStage Stage,
    RecapEpochBuildingDescriptor Descriptor,
    DerivedRecapEpochManifest Manifest,
    DerivedRecapEpochInput EpochInput,
    IReadOnlyList<RecapEpochBlockInspection> Blocks,
    PublishedRecapEpoch? Publication,
    RecapEpochPublishedRepairAuthority? PublishedRepairAuthority
);

public abstract record RecapEpochStoreReadResult {
    private RecapEpochStoreReadResult() {
    }

    public sealed record Available(RecapEpochStoreSnapshot Snapshot)
        : RecapEpochStoreReadResult;

    public sealed record Missing(EventAddress AdmissionAnchor)
        : RecapEpochStoreReadResult;

    public sealed record Invalid(
        EventAddress AdmissionAnchor,
        string Detail
    ) : RecapEpochStoreReadResult;
}

public abstract record InstallRecapEpochBuildingResult {
    private InstallRecapEpochBuildingResult() {
    }

    public sealed record Installed(RecapEpochBuildingDescriptor Descriptor)
        : InstallRecapEpochBuildingResult;

    public sealed record PreviousChanged(string Detail)
        : InstallRecapEpochBuildingResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : InstallRecapEpochBuildingResult;

    public sealed record Conflict(EventAddress AdmissionAnchor)
        : InstallRecapEpochBuildingResult;

    public sealed record Invalid(string Detail)
        : InstallRecapEpochBuildingResult;
}

public abstract record WriteRecapEpochFinalResult {
    private WriteRecapEpochFinalResult() {
    }

    public sealed record Installed(string StateToken)
        : WriteRecapEpochFinalResult;

    public sealed record AlreadyHealthy(DerivedRecapFinalBlock Block)
        : WriteRecapEpochFinalResult;

    public sealed record HealthyConflict(DerivedRecapFinalBlock Block)
        : WriteRecapEpochFinalResult;

    public sealed record Stale(string CurrentStateToken)
        : WriteRecapEpochFinalResult;

    public sealed record Invalid(string Detail)
        : WriteRecapEpochFinalResult;
}

public abstract record PublishRecapEpochResult {
    private PublishRecapEpochResult() {
    }

    public sealed record Published(PublishedRecapEpochDescriptor Descriptor)
        : PublishRecapEpochResult;

    public sealed record AlreadyPublished(
        PublishedRecapEpochDescriptor Descriptor
    ) : PublishRecapEpochResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : PublishRecapEpochResult;

    public sealed record NotPublishable(string Detail)
        : PublishRecapEpochResult;

    public sealed record Stale(string Detail)
        : PublishRecapEpochResult;
}

public abstract record RecapEpochSelectionResult {
    private RecapEpochSelectionResult() {
    }

    public sealed record Selected(PublishedRecapEpochDescriptor Descriptor)
        : RecapEpochSelectionResult;

    public sealed record Empty : RecapEpochSelectionResult;

    public sealed record Invalid(
        EventAddress AdmissionAnchor,
        string Detail
    ) : RecapEpochSelectionResult;
}
