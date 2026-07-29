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

public abstract record RecapBlockPlan {
    protected RecapBlockPlan(
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

public sealed record InheritRecapBlockPlan : RecapBlockPlan {
    public InheritRecapBlockPlan(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        EventAddress sourceSetAnchor,
        string sourcePublicationEnvelopeSha256,
        string sourceInputPayloadSha256,
        int maxContentUtf8Bytes =
            SessionContextContributionContract.MaxContributionUtf8Bytes
    ) : base(recapBlockId, target, maxContentUtf8Bytes) {
        SourceSetAnchor = sourceSetAnchor;
        SourcePublicationEnvelopeSha256 =
            sourcePublicationEnvelopeSha256;
        SourceInputPayloadSha256 = sourceInputPayloadSha256;
    }

    public EventAddress SourceSetAnchor { get; }
    public string SourcePublicationEnvelopeSha256 { get; }
    public string SourceInputPayloadSha256 { get; }
}

public abstract record RecapMaintainSource;

public sealed record ExistingRecapMaintainSource(
    EventAddress SourceSetAnchor,
    string SourcePublicationEnvelopeSha256,
    string SourceInputPayloadSha256
) : RecapMaintainSource;

public sealed record EmptyRecapMaintainSource(
    EventAddress ReplayStartExclusive
) : RecapMaintainSource;

public abstract record RecapPriorContext;

public sealed record EmptyRecapPriorContext : RecapPriorContext {
    public static EmptyRecapPriorContext Instance { get; } = new();
}

public sealed record InlineRecapPriorContext(
    EventAddress AdmissionAnchor,
    ContextHeaderSnapshot Snapshot
) : RecapPriorContext;

public sealed record MaintainRecapBlockPlan : RecapBlockPlan {
    public MaintainRecapBlockPlan(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        string maintainerId,
        RecapMaintainSource source,
        IReadOnlyList<EventAddress> catchUpThrough,
        RecapPriorContext priorContext,
        int maxContentUtf8Bytes =
            SessionContextContributionContract.MaxContributionUtf8Bytes
    ) : base(recapBlockId, target, maxContentUtf8Bytes) {
        MaintainerId = maintainerId;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(catchUpThrough);
        CatchUpThrough = Array.AsReadOnly(catchUpThrough.ToArray());
        PriorContext = priorContext
            ?? throw new ArgumentNullException(nameof(priorContext));
    }

    public string MaintainerId { get; }
    public RecapMaintainSource Source { get; }
    public IReadOnlyList<EventAddress> CatchUpThrough { get; }
    public RecapPriorContext PriorContext { get; }
}

public sealed record DerivedRecapSetManifest(
    string Schema,
    RefId RefId,
    EventAddress SetAdmissionAnchor,
    IReadOnlyList<RecapBlockPlan> Blocks,
    string ManifestPayloadSha256
);

public sealed record DerivedRecapFrozenInput(
    string Schema,
    RecapBlockId RecapBlockId,
    ContextHeaderBlockPath Target,
    EventAddress AbsorbedThrough,
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

public sealed record DerivedRecapMaterialization(
    EventAddress SetAdmissionAnchor,
    IReadOnlyList<SessionContextContribution> Contributions
);

public sealed record RecapStructuralDefect(
    string Code,
    string Detail
);

public sealed record RecapPublishability(
    bool IsPublishable,
    IReadOnlyList<RecapStructuralDefect> Defects
) {
    public static RecapPublishability Publishable { get; } =
        new(true, Array.Empty<RecapStructuralDefect>());
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
}

public sealed record BuildingBlockInspection(
    BuildingDescriptor Building,
    RecapBlockPlan Plan,
    DerivedRecapFrozenInput? FrozenInput,
    FinalRecapBlockHealth Final,
    RollingRecapCheckpointHealth Checkpoint
);

public abstract record CheckpointWriteResult {
    private CheckpointWriteResult() {
    }

    public sealed record Updated(string StateToken)
        : CheckpointWriteResult;

    public sealed record AlreadyCurrent(string StateToken)
        : CheckpointWriteResult;

    public sealed record Stale(string CurrentStateToken)
        : CheckpointWriteResult;
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
}
