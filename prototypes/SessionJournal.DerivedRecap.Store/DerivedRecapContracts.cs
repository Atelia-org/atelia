using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

public sealed record RecapBlockId {
    public const int MaxLength = 128;

    public RecapBlockId(string value) {
        if (!IsValid(value)) {
            throw new ArgumentException(
                "RecapBlockId must match [a-z0-9][a-z0-9._-]{0,127}.",
                nameof(value)
            );
        }
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    internal static bool IsValid(string? value) {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength) {
            return false;
        }
        if (!IsLowerAlphaNumeric(value[0])) {
            return false;
        }
        for (int index = 1; index < value.Length; index++) {
            char ch = value[index];
            if (!IsLowerAlphaNumeric(ch)
                && ch is not ('.' or '_' or '-')) {
                return false;
            }
        }
        return true;
    }

    private static bool IsLowerAlphaNumeric(char ch)
        => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
}

public abstract class RecapBlockPlan {
    private protected RecapBlockPlan(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        int maxContentUtf8Bytes
    ) {
        RecapBlockId = recapBlockId
            ?? throw new ArgumentNullException(nameof(recapBlockId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (maxContentUtf8Bytes <= 0
            || maxContentUtf8Bytes
                > SessionContextContributionContract
                    .MaxContributionUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(maxContentUtf8Bytes)
            );
        }
        MaxContentUtf8Bytes = maxContentUtf8Bytes;
    }

    public RecapBlockId RecapBlockId { get; }
    public ContextHeaderBlockPath Target { get; }
    public int MaxContentUtf8Bytes { get; }
}

public sealed record RecapReplayBoundary(
    EventAddress Address,
    SessionContextAnchorSetupReferences Setups
);

public sealed class InheritRecapBlockPlan : RecapBlockPlan {
    public InheritRecapBlockPlan(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        EventAddress sourceSetAnchor,
        SessionContextAnchorSetupReferences sourceAbsorbedThroughSetups,
        string sourcePublicationEnvelopeSha256,
        string sourceInputPayloadSha256,
        int maxContentUtf8Bytes =
            SessionContextContributionContract.MaxContributionUtf8Bytes
    ) : base(recapBlockId, target, maxContentUtf8Bytes) {
        SourceSetAnchor = sourceSetAnchor;
        SourceAbsorbedThroughSetups = sourceAbsorbedThroughSetups
            ?? throw new ArgumentNullException(
                nameof(sourceAbsorbedThroughSetups)
            );
        SourcePublicationEnvelopeSha256 =
            sourcePublicationEnvelopeSha256;
        SourceInputPayloadSha256 = sourceInputPayloadSha256;
    }

    public EventAddress SourceSetAnchor { get; }
    public SessionContextAnchorSetupReferences
        SourceAbsorbedThroughSetups { get; }
    public string SourcePublicationEnvelopeSha256 { get; }
    public string SourceInputPayloadSha256 { get; }
}

public abstract class RecapMaintainSource {
    private protected RecapMaintainSource() {
    }
}

public sealed class ExistingRecapMaintainSource : RecapMaintainSource {
    public ExistingRecapMaintainSource(
        EventAddress sourceSetAnchor,
        SessionContextAnchorSetupReferences replayStartSetups,
        string sourcePublicationEnvelopeSha256,
        string sourceInputPayloadSha256
    ) {
        SourceSetAnchor = sourceSetAnchor;
        ReplayStartSetups = replayStartSetups
            ?? throw new ArgumentNullException(nameof(replayStartSetups));
        SourcePublicationEnvelopeSha256 =
            sourcePublicationEnvelopeSha256;
        SourceInputPayloadSha256 = sourceInputPayloadSha256;
    }

    public EventAddress SourceSetAnchor { get; }
    public SessionContextAnchorSetupReferences ReplayStartSetups { get; }
    public string SourcePublicationEnvelopeSha256 { get; }
    public string SourceInputPayloadSha256 { get; }
}

public sealed class EmptyRecapMaintainSource : RecapMaintainSource {
    public EmptyRecapMaintainSource(
        EventAddress replayStartExclusive,
        SessionContextAnchorSetupReferences replayStartSetups
    ) {
        ReplayStartExclusive = replayStartExclusive;
        ReplayStartSetups = replayStartSetups
            ?? throw new ArgumentNullException(nameof(replayStartSetups));
    }

    public EventAddress ReplayStartExclusive { get; }
    public SessionContextAnchorSetupReferences ReplayStartSetups { get; }
}

public abstract class RecapPriorContext {
    private protected RecapPriorContext() {
    }
}

public sealed class EmptyRecapPriorContext : RecapPriorContext {
    public static EmptyRecapPriorContext Instance { get; } = new();
}

public sealed class InlineRecapPriorContext : RecapPriorContext {
    public InlineRecapPriorContext(
        EventAddress admissionAnchor,
        ContextHeaderSnapshot snapshot
    ) {
        AdmissionAnchor = admissionAnchor;
        Snapshot = snapshot;
    }

    public EventAddress AdmissionAnchor { get; }
    public ContextHeaderSnapshot Snapshot { get; }
}

public sealed class MaintainRecapBlockPlan : RecapBlockPlan {
    public MaintainRecapBlockPlan(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        string maintainerId,
        string maintainerCapabilityFingerprint,
        RecapMaintainSource source,
        IReadOnlyList<RecapReplayBoundary> catchUpBoundaries,
        RecapPriorContext priorContext,
        int maxContentUtf8Bytes =
            SessionContextContributionContract.MaxContributionUtf8Bytes
    ) : base(recapBlockId, target, maxContentUtf8Bytes) {
        MaintainerId = maintainerId;
        MaintainerCapabilityFingerprint =
            maintainerCapabilityFingerprint;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(catchUpBoundaries);
        CatchUpBoundaries = Array.AsReadOnly(
            catchUpBoundaries.ToArray()
        );
        PriorContext = priorContext
            ?? throw new ArgumentNullException(nameof(priorContext));
    }

    public string MaintainerId { get; }
    public string MaintainerCapabilityFingerprint { get; }
    public RecapMaintainSource Source { get; }
    public IReadOnlyList<RecapReplayBoundary> CatchUpBoundaries { get; }
    public RecapPriorContext PriorContext { get; }
}

public sealed record DerivedRecapSetManifest(
    string Schema,
    RefId RefId,
    EventAddress SetAdmissionAnchor,
    SessionContextAnchorSetupReferences SetAdmissionAnchorSetups,
    IReadOnlyList<RecapBlockPlan> Blocks,
    string ManifestPayloadSha256
);

public sealed record DerivedRecapFrozenInput(
    string Schema,
    RecapBlockId RecapBlockId,
    ContextHeaderBlockPath Target,
    EventAddress AbsorbedThrough,
    SessionContextAnchorSetupReferences AbsorbedThroughSetups,
    string Content,
    string PayloadSha256
);

public sealed record DerivedRecapBlock(
    string Schema,
    RecapBlockId RecapBlockId,
    ContextHeaderBlockPath Target,
    string BlockPlanSha256,
    EventAddress AbsorbedThrough,
    string Content,
    string PayloadSha256
);

public sealed record RecapBlockCommitment(
    RecapBlockId RecapBlockId,
    ContextHeaderBlockPath Target,
    EventAddress AbsorbedThrough,
    string PayloadSha256
);

public sealed record PublishedRecapSet(
    string Schema,
    RefId RefId,
    EventAddress SetAdmissionAnchor,
    DerivedRecapSetManifest FrozenPlanSnapshot,
    IReadOnlyList<RecapBlockCommitment> BlockCommitments,
    string EnvelopeSha256
);

public sealed record PublishedRecapDescriptor(
    RefId RefId,
    EventAddress SetAdmissionAnchor,
    string EnvelopeSha256
);

public sealed record PublishedPlanSnapshot(
    PublishedRecapDescriptor Descriptor,
    DerivedRecapSetManifest FrozenPlan
) {
    /// <summary>
    /// Canonical publication commitments authenticated by the same envelope as FrozenPlan.
    /// Reading this metadata does not read final block files.
    /// </summary>
    public IReadOnlyList<RecapBlockCommitment> BlockCommitments {
        get;
        init;
    } = Array.Empty<RecapBlockCommitment>();
}

/// <summary>
/// Opaque metadata-phase authority for one exact Published restore epoch. It binds the
/// publication-or-witness state, frozen manifest, block roster, and any envelope-authenticated
/// final commitments before component payloads may be inspected.
/// </summary>
public sealed class PublishedRestorePlanAuthority {
    internal PublishedRestorePlanAuthority(
        string ownerPath,
        RefId refId,
        EventAddress setAdmissionAnchor,
        PublishedRestoreAuthorityKind authorityKind,
        string authorityStateToken,
        string manifestPayloadSha256,
        IReadOnlyList<RecapBlockId> blockRoster,
        IReadOnlyList<RecapBlockCommitment> blockCommitments
    ) {
        OwnerPath = ownerPath;
        RefId = refId;
        SetAdmissionAnchor = setAdmissionAnchor;
        AuthorityKind = authorityKind;
        AuthorityStateToken = authorityStateToken;
        ManifestPayloadSha256 = manifestPayloadSha256;
        BlockRoster = blockRoster;
        BlockCommitments = blockCommitments;
    }

    internal string OwnerPath { get; }
    internal string AuthorityStateToken { get; }
    internal IReadOnlyList<RecapBlockId> BlockRoster { get; }

    public RefId RefId { get; }
    public EventAddress SetAdmissionAnchor { get; }
    public PublishedRestoreAuthorityKind AuthorityKind { get; }
    public string ManifestPayloadSha256 { get; }
    public IReadOnlyList<RecapBlockCommitment> BlockCommitments { get; }
}

public abstract record PublishedPlanReadResult {
    private PublishedPlanReadResult() {
    }

    public sealed record Available(PublishedPlanSnapshot Snapshot)
        : PublishedPlanReadResult;

    public sealed record Changed(
        PublishedRecapDescriptor Expected,
        PublishedRecapDescriptor Observed
    ) : PublishedPlanReadResult;

    public sealed record Unavailable(
        PublishedRecapDescriptor Expected,
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishedPlanReadResult;
}

public abstract record PublishedPlanAtAnchorReadResult {
    private PublishedPlanAtAnchorReadResult() {
    }

    public sealed record Available(
        PublishedPlanSnapshot Snapshot,
        PublishedRestorePlanAuthority Authority
    )
        : PublishedPlanAtAnchorReadResult;

    public sealed record ManifestWitnessAvailable(
        DerivedRecapSetManifest FrozenPlan,
        PublishedRestorePlanAuthority Authority
    ) : PublishedPlanAtAnchorReadResult;

    public sealed record Missing(EventAddress SetAdmissionAnchor)
        : PublishedPlanAtAnchorReadResult;

    public sealed record Changed(
        PublishedRecapDescriptor Before,
        PublishedRecapDescriptor? After
    ) : PublishedPlanAtAnchorReadResult;

    public sealed record Unavailable(
        EventAddress SetAdmissionAnchor,
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishedPlanAtAnchorReadResult;
}

public abstract record PublishedMembershipInspectionResult {
    private PublishedMembershipInspectionResult() {
    }

    public sealed record Present(PublishedRecapDescriptor Descriptor)
        : PublishedMembershipInspectionResult;

    public sealed record Absent(EventAddress SetAdmissionAnchor)
        : PublishedMembershipInspectionResult;

    public sealed record Invalid(
        EventAddress SetAdmissionAnchor,
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishedMembershipInspectionResult;

    public sealed record StoreUnavailable(
        EventAddress SetAdmissionAnchor,
        string Reason
    ) : PublishedMembershipInspectionResult;
}

public sealed record DerivedRecapMaterialization(
    EventAddress SetAdmissionAnchor,
    IReadOnlyList<SessionContextContribution> Contributions
);

public sealed record RecapStructuralDefect(
    string Code,
    string Detail
);

public abstract record RecapPublishability {
    private RecapPublishability() {
    }

    public sealed record Publishable : RecapPublishability;

    public sealed record AlreadyPublished(
        PublishedRecapDescriptor Descriptor
    ) : RecapPublishability;

    public sealed record SourceChanged(
        BuildingDescriptor Expected,
        BuildingDescriptor? Observed
    ) : RecapPublishability;

    public sealed record NotPublishable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : RecapPublishability;

    public sealed record BeyondPrefix(
        SessionCurrentLineageBeyondPrefix Evidence
    ) : RecapPublishability;

    public sealed record StoreUnavailable(string Reason)
        : RecapPublishability;
}

public abstract record PublishRecapResult {
    private PublishRecapResult() {
    }

    public sealed record Published(PublishedRecapDescriptor Descriptor)
        : PublishRecapResult;

    public sealed record AlreadyPublished(
        PublishedRecapDescriptor Descriptor
    ) : PublishRecapResult;

    public sealed record SourceChanged(
        BuildingDescriptor Expected,
        BuildingDescriptor? Observed
    ) : PublishRecapResult;

    public sealed record NotPublishable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishRecapResult;

    public sealed record BeyondPrefix(
        SessionCurrentLineageBeyondPrefix Evidence
    ) : PublishRecapResult;

    public sealed record StoreUnavailable(string Reason)
        : PublishRecapResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : PublishRecapResult;
}

public abstract record DerivedRecapSelection {
    private DerivedRecapSelection() {
    }

    public sealed record Selected(PublishedRecapDescriptor Descriptor)
        : DerivedRecapSelection;

    public sealed record EmptyLineage : DerivedRecapSelection;

    public sealed record OrdinalUnavailable : DerivedRecapSelection;

    public sealed record ExactPublishedSetInvalid(
        EventAddress SetAdmissionAnchor,
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : DerivedRecapSelection;

    public sealed record BeyondPrefix(
        SessionCurrentLineageBeyondPrefix Evidence
    ) : DerivedRecapSelection;

    public sealed record StoreUnavailable(string Reason)
        : DerivedRecapSelection;
}

public sealed record PublishedRecapSourceSnapshot(
    PublishedRecapDescriptor Source,
    PublishedRecapSet Publication,
    IReadOnlyList<DerivedRecapFrozenInput> FrozenInputs
);

public abstract record PublishedRecapSourceReadResult {
    private PublishedRecapSourceReadResult() {
    }

    public sealed record Available(PublishedRecapSourceSnapshot Snapshot)
        : PublishedRecapSourceReadResult;

    public sealed record Missing(EventAddress SourceSetAnchor)
        : PublishedRecapSourceReadResult;

    public sealed record SnapshotTokenMismatch(
        string Expected,
        string? Observed
    ) : PublishedRecapSourceReadResult;

    public sealed record ChangedDuringRead(
        string Expected,
        string? Observed
    ) : PublishedRecapSourceReadResult;

    public sealed record Invalid(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishedRecapSourceReadResult;
}

public sealed record BuildingDescriptor(
    RefId RefId,
    EventAddress SetAdmissionAnchor,
    string ManifestPayloadSha256
);

public sealed record BuildingSnapshot(
    BuildingDescriptor Descriptor,
    DerivedRecapSetManifest Manifest,
    IReadOnlyDictionary<RecapBlockId, DerivedRecapFrozenInput>
        FrozenInputs
);

/// <summary>
/// Opaque capability for the second, content-reading phase of one exact
/// Building snapshot. The capability is bound to the normalized repository
/// path and RefId, so it remains portable across Store reopen/new instances
/// for that same durable Store identity and is rejected elsewhere.
/// </summary>
public sealed class BuildingPlanHandle {
    internal BuildingPlanHandle(
        string ownerPath,
        BuildingDescriptor descriptor
    ) {
        OwnerPath = ownerPath;
        Descriptor = descriptor;
    }

    internal string OwnerPath { get; }
    internal BuildingDescriptor Descriptor { get; }
}

/// <summary>
/// Opaque, Publisher-issued authority for diagnosing or publishing one exact
/// Building against one caller-frozen raw head. Preparation captures and
/// resolves the required raw lineage before component work begins; later
/// consumption never recaptures that lineage.
/// </summary>
public sealed class PreparedRecapPublication {
    internal PreparedRecapPublication(
        DerivedRecapPublisher owner,
        BuildingPlanHandle handle,
        DerivedRecapLineageView lineage,
        EventAddress expectedRawHead
    ) {
        Owner = owner;
        Handle = handle;
        Lineage = lineage;
        ExpectedRawHead = expectedRawHead;
    }

    internal DerivedRecapPublisher Owner { get; }
    internal BuildingPlanHandle Handle { get; }
    internal DerivedRecapLineageView Lineage { get; }
    internal EventAddress ExpectedRawHead { get; }
}

/// <summary>
/// Manifest-only Building metadata. Frozen input and block contents are deliberately absent.
/// </summary>
public sealed record BuildingPlanSnapshot(
    BuildingDescriptor Descriptor,
    DerivedRecapSetManifest Manifest,
    BuildingPlanHandle Handle
);

public abstract record BuildingPlanReadResult {
    private BuildingPlanReadResult() {
    }

    public sealed record Available(BuildingPlanSnapshot Snapshot)
        : BuildingPlanReadResult;

    public sealed record Missing : BuildingPlanReadResult;

    public sealed record Invalid(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : BuildingPlanReadResult;
}

public abstract record BuildingReadResult {
    private BuildingReadResult() {
    }

    public sealed record Available(BuildingSnapshot Snapshot)
        : BuildingReadResult;

    public sealed record Missing : BuildingReadResult;

    public sealed record Invalid(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : BuildingReadResult;
}

public abstract record CurrentLineageBuildingSelection {
    private CurrentLineageBuildingSelection() {
    }

    public sealed record None : CurrentLineageBuildingSelection;

    public sealed record Available(BuildingPlanSnapshot Snapshot)
        : CurrentLineageBuildingSelection;

    public sealed record Invalid(
        EventAddress SetAdmissionAnchor,
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : CurrentLineageBuildingSelection;

    public sealed record Stale(
        EventAddress SetAdmissionAnchor,
        EventAddress LatestPublishedAnchor
    ) : CurrentLineageBuildingSelection;

    public sealed record Multiple(
        IReadOnlyList<EventAddress> SetAdmissionAnchors
    ) : CurrentLineageBuildingSelection;

    public sealed record BeyondPrefix(
        SessionCurrentLineageBeyondPrefix Evidence
    ) : CurrentLineageBuildingSelection;

    public sealed record StoreUnavailable(string Reason)
        : CurrentLineageBuildingSelection;
}

public abstract record CreateBuildingResult {
    private CreateBuildingResult() {
    }

    public sealed record Created(BuildingDescriptor Descriptor)
        : CreateBuildingResult;

    public sealed record SourceUnavailable(
        PublishedRecapDescriptor Source,
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : CreateBuildingResult;

    public sealed record SourceChanged(
        PublishedRecapDescriptor Source,
        string? ObservedEnvelopeSha256
    ) : CreateBuildingResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : CreateBuildingResult;

    public sealed record ActiveBuildingConflict(
        IReadOnlyList<EventAddress> SetAdmissionAnchors
    ) : CreateBuildingResult;

    public sealed record BeyondPrefix(
        SessionCurrentLineageBeyondPrefix Evidence
    ) : CreateBuildingResult;

    public sealed record StoreUnavailable(string Reason)
        : CreateBuildingResult;

    public sealed record InvalidPlan(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : CreateBuildingResult;
}

public abstract record QuarantineBuildingResult {
    private QuarantineBuildingResult() {
    }

    public sealed record Quarantined(string QuarantineId)
        : QuarantineBuildingResult;

    public sealed record AlreadyAbsent : QuarantineBuildingResult;

    public sealed record PublishedConflict : QuarantineBuildingResult;

    public sealed record Unavailable(string Reason)
        : QuarantineBuildingResult;
}

public abstract record FinalRecapBlockHealth {
    public abstract string StateToken { get; init; }

    public sealed record Missing(string StateToken)
        : FinalRecapBlockHealth;

    public sealed record Healthy(
        DerivedRecapBlock Block,
        string StateToken
    ) : FinalRecapBlockHealth;

    public sealed record Damaged(
        IReadOnlyList<RecapStructuralDefect> Defects,
        string StateToken
    ) : FinalRecapBlockHealth;

    public sealed record Unavailable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : FinalRecapBlockHealth {
        public override string StateToken { get; init; } = string.Empty;
    }
}

public abstract record RollingRecapCheckpointHealth {
    public abstract string StateToken { get; init; }

    public sealed record Missing(string StateToken)
        : RollingRecapCheckpointHealth;

    public sealed record Healthy(
        DerivedRecapBlock Block,
        int EndpointIndex,
        string StateToken
    ) : RollingRecapCheckpointHealth;

    public sealed record Unusable(
        IReadOnlyList<RecapStructuralDefect> Defects,
        string StateToken
    ) : RollingRecapCheckpointHealth;

    public sealed record Unavailable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : RollingRecapCheckpointHealth {
        public override string StateToken { get; init; } = string.Empty;
    }
}

public sealed record BuildingBlockInspection(
    BuildingDescriptor Building,
    RecapBlockPlan Plan,
    DerivedRecapFrozenInput? FrozenInput,
    FinalRecapBlockHealth Final,
    RollingRecapCheckpointHealth Checkpoint
) {
    public BuildingBlockWriteAuthority WriteAuthority { get; init; }
        = null!;
}

/// <summary>
/// Store-issued, pre-write authority for the exact Building/block/component states observed by
/// one inspection. Callers can retain it but cannot construct or alter its binding.
/// </summary>
public sealed class BuildingBlockWriteAuthority {
    internal BuildingBlockWriteAuthority(
        string ownerPath,
        BuildingDescriptor building,
        RecapBlockId blockId,
        string checkpointStateToken,
        string finalStateToken
    ) {
        OwnerPath = ownerPath;
        Building = building;
        BlockId = blockId;
        CheckpointStateToken = checkpointStateToken;
        FinalStateToken = finalStateToken;
    }

    internal string OwnerPath { get; }
    internal BuildingDescriptor Building { get; }
    internal RecapBlockId BlockId { get; }
    internal string CheckpointStateToken { get; }
    internal string FinalStateToken { get; }
}

public abstract record CheckpointWriteResult {
    private CheckpointWriteResult() {
    }

    public sealed record Updated(string StateToken)
        : CheckpointWriteResult;

    public sealed record AlreadyCurrent(string StateToken)
        : CheckpointWriteResult;

    public sealed record Stale(string CurrentStateToken)
        : CheckpointWriteResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : CheckpointWriteResult;
}

public abstract record FinalBlockWriteResult {
    private FinalBlockWriteResult() {
    }

    public sealed record Installed(string StateToken)
        : FinalBlockWriteResult;

    public sealed record ReplacedDamaged(string StateToken)
        : FinalBlockWriteResult;

    public sealed record AlreadyHealthy(
        DerivedRecapBlock Block,
        string StateToken
    ) : FinalBlockWriteResult;

    public sealed record HealthyConflict(
        DerivedRecapBlock Existing,
        string StateToken
    ) : FinalBlockWriteResult;

    public sealed record Stale(string CurrentStateToken)
        : FinalBlockWriteResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : FinalBlockWriteResult;
}

public enum PublishedRestoreAuthorityKind {
    Publication,
    ManifestWitness,
}

public sealed class PublishedRestoreHandle {
    internal PublishedRestoreHandle(
        RefId refId,
        EventAddress setAdmissionAnchor,
        PublishedRestoreAuthorityKind authorityKind,
        string authorityStateToken,
        string manifestPayloadSha256,
        IReadOnlyList<RecapBlockId> blockRoster
    ) {
        RefId = refId;
        SetAdmissionAnchor = setAdmissionAnchor;
        AuthorityKind = authorityKind;
        AuthorityStateToken = authorityStateToken;
        ManifestPayloadSha256 = manifestPayloadSha256;
        BlockRoster = blockRoster;
    }

    public RefId RefId { get; }
    public EventAddress SetAdmissionAnchor { get; }
    public PublishedRestoreAuthorityKind AuthorityKind { get; }
    public string ManifestPayloadSha256 { get; }

    internal string AuthorityStateToken { get; }
    internal IReadOnlyList<RecapBlockId> BlockRoster { get; }
}

public abstract record FrozenRecapInputHealth {
    public abstract string StateToken { get; init; }

    public sealed record NotRequired(string StateToken)
        : FrozenRecapInputHealth;

    public sealed record Missing(string StateToken)
        : FrozenRecapInputHealth;

    public sealed record Healthy(
        DerivedRecapFrozenInput Input,
        string StateToken
    ) : FrozenRecapInputHealth;

    public sealed record Damaged(
        IReadOnlyList<RecapStructuralDefect> Defects,
        string StateToken
    ) : FrozenRecapInputHealth;

    public sealed record Unavailable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : FrozenRecapInputHealth {
        public override string StateToken { get; init; } = string.Empty;
    }
}

public abstract record PublishedBlockRestoreCapability {
    private PublishedBlockRestoreCapability() {
    }

    public sealed record KeepCommitted
        : PublishedBlockRestoreCapability;

    public sealed record AdoptPending
        : PublishedBlockRestoreCapability;

    public sealed record InstallFinalCheckpoint
        : PublishedBlockRestoreCapability;

    public sealed record ResumeSuffix(int NextEndpointIndex)
        : PublishedBlockRestoreCapability;

    public sealed record ReplayBlock
        : PublishedBlockRestoreCapability;

    public sealed record Unavailable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishedBlockRestoreCapability;
}

public sealed record PublishedBlockRestoreInspection(
    RecapBlockPlan Plan,
    FrozenRecapInputHealth FrozenInput,
    FinalRecapBlockHealth Final,
    RollingRecapCheckpointHealth Checkpoint,
    PublishedBlockRestoreCapability Capability
) {
    public PublishedBlockWriteAuthority WriteAuthority { get; init; }
        = null!;
}

/// <summary>
/// Store-issued, pre-write authority for the exact Published restore handle,
/// block, checkpoint, and final states observed by one inspection. Callers can
/// retain it but cannot construct or alter its binding.
/// </summary>
public sealed class PublishedBlockWriteAuthority {
    internal PublishedBlockWriteAuthority(
        string ownerPath,
        PublishedRestoreHandle handle,
        RecapBlockId blockId,
        string checkpointStateToken,
        string finalStateToken
    ) {
        OwnerPath = ownerPath;
        Handle = handle;
        BlockId = blockId;
        CheckpointStateToken = checkpointStateToken;
        FinalStateToken = finalStateToken;
    }

    internal string OwnerPath { get; }
    internal PublishedRestoreHandle Handle { get; }
    internal RecapBlockId BlockId { get; }
    internal string CheckpointStateToken { get; }
    internal string FinalStateToken { get; }
}

/// <summary>
/// Store-issued authority for committing one exact Published envelope after
/// every frozen-plan block has an exact, pre-write final-state authority.
/// </summary>
public sealed class PublishedEnvelopeCommitAuthority {
    internal PublishedEnvelopeCommitAuthority(
        string ownerPath,
        PublishedRestoreHandle handle,
        IReadOnlyDictionary<RecapBlockId, string> finalStateTokens
    ) {
        OwnerPath = ownerPath;
        Handle = handle;
        FinalStateTokens = finalStateTokens;
    }

    internal string OwnerPath { get; }
    internal PublishedRestoreHandle Handle { get; }
    internal IReadOnlyDictionary<RecapBlockId, string>
        FinalStateTokens { get; }
}

public sealed record PublishedRestoreInspection(
    PublishedRestoreHandle Handle,
    DerivedRecapSetManifest FrozenPlan,
    IReadOnlyDictionary<
        RecapBlockId,
        PublishedBlockRestoreInspection
    > Blocks
);

public abstract record PublishedRestoreInspectionResult {
    private PublishedRestoreInspectionResult() {
    }

    public sealed record Available(
        PublishedRestoreInspection Inspection
    ) : PublishedRestoreInspectionResult;

    public sealed record BeyondPrefix(
        SessionCurrentLineageBeyondPrefix Evidence
    ) : PublishedRestoreInspectionResult;

    public sealed record Unavailable(
        EventAddress SetAdmissionAnchor,
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishedRestoreInspectionResult;
}

public abstract record PublishedCheckpointWriteResult {
    private PublishedCheckpointWriteResult() {
    }

    public sealed record Updated(string StateToken)
        : PublishedCheckpointWriteResult {
        public PublishedBlockWriteAuthority WriteAuthority { get; init; }
            = null!;
    }

    public sealed record AlreadyCurrent(string StateToken)
        : PublishedCheckpointWriteResult {
        public PublishedBlockWriteAuthority WriteAuthority { get; init; }
            = null!;
    }

    public sealed record Stale(string? CurrentStateToken)
        : PublishedCheckpointWriteResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishedCheckpointWriteResult;
}

public abstract record PublishedFinalWriteResult {
    private PublishedFinalWriteResult() {
    }

    public sealed record Installed(string StateToken)
        : PublishedFinalWriteResult {
        public PublishedBlockWriteAuthority WriteAuthority { get; init; }
            = null!;
    }

    public sealed record ReplacedDamaged(string StateToken)
        : PublishedFinalWriteResult {
        public PublishedBlockWriteAuthority WriteAuthority { get; init; }
            = null!;
    }

    public sealed record AlreadyHealthy(
        DerivedRecapBlock Block,
        string StateToken
    ) : PublishedFinalWriteResult {
        public PublishedBlockWriteAuthority WriteAuthority { get; init; }
            = null!;
    }

    public sealed record HealthyConflict(
        DerivedRecapBlock Existing,
        string StateToken
    ) : PublishedFinalWriteResult;

    public sealed record Stale(string? CurrentStateToken)
        : PublishedFinalWriteResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishedFinalWriteResult;
}

public abstract record PublishedEnvelopeCommitResult {
    private PublishedEnvelopeCommitResult() {
    }

    public sealed record Committed(PublishedRecapDescriptor Descriptor)
        : PublishedEnvelopeCommitResult;

    public sealed record AlreadyCommitted(
        PublishedRecapDescriptor Descriptor
    ) : PublishedEnvelopeCommitResult;

    public sealed record Stale(string Code, string Detail)
        : PublishedEnvelopeCommitResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapStructuralDefect> Defects
    ) : PublishedEnvelopeCommitResult;

}
