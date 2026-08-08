using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// One replay-safe endpoint of a shared recap epoch.
/// </summary>
public sealed record RecapEpochBoundary(
    EventAddress Address,
    SessionContextAnchorSetupReferences Setups
);

/// <summary>
/// The previous recap pack frozen into one epoch input. Empty is legal only
/// for the bootstrap epoch.
/// </summary>
public abstract record RecapEpochPrevious {
    private RecapEpochPrevious() {
    }

    public sealed record Empty : RecapEpochPrevious {
        private Empty() {
        }

        public static Empty Instance { get; } = new();
    }

    public sealed record Prior(PriorRecapPackSnapshot Pack)
        : RecapEpochPrevious;
}

public sealed record PublishedRecapEpochDescriptor(
    RefId RefId,
    EventAddress AdmissionAnchor,
    string EnvelopeSha256
);

public sealed record PriorRecapBlockSnapshot(
    RecapBlockId RecapBlockId,
    ContextHeaderBlockPath Target,
    string Content,
    string ContentSha256,
    string SourceEpochBlockExecutionSha256,
    string SourcePayloadSha256
);

/// <summary>
/// Structured previous-state authority. The ordered block contents are the
/// only durable source for both shared prior-context projection and
/// KeepUnchanged.
/// </summary>
public sealed record PriorRecapPackSnapshot(
    PublishedRecapEpochDescriptor Source,
    string ProjectionSchema,
    IReadOnlyList<PriorRecapBlockSnapshot> Blocks,
    string PayloadSha256
);

/// <summary>
/// The one immutable execution input shared by the complete epoch roster.
/// HistoryMessages is an already-frozen provider-facing projection, not a
/// recipe for a later raw read.
/// </summary>
public sealed record DerivedRecapEpochInput(
    string Schema,
    RecapEpochBoundary StartBoundary,
    RecapEpochBoundary AdmissionBoundary,
    int RawEventCount,
    string RawRangeCommitmentSha256,
    string HistoryProjectionSchema,
    IReadOnlyList<IHistoryMessage> HistoryMessages,
    RecapEpochPrevious Previous,
    string PayloadSha256
);

/// <summary>
/// One member of the ordered, complete maintenance roster.
/// </summary>
public sealed record RecapEpochBlockDefinition(
    RecapBlockId RecapBlockId,
    ContextHeaderBlockPath Target,
    string MaintainerId,
    string MaintainerCapabilityFingerprint,
    int MaxContentUtf8Bytes,
    int Ordinal
);

/// <summary>
/// Manifest contains exactly one epoch-input commitment and one complete
/// ordered roster. Per-block cursors and update modes do not exist.
/// </summary>
public sealed record DerivedRecapEpochManifest(
    string Schema,
    RefId RefId,
    EventAddress AdmissionAnchor,
    string EpochInputPayloadSha256,
    IReadOnlyList<RecapEpochBlockDefinition> Blocks,
    string ManifestPayloadSha256
);

/// <summary>
/// Direct final for one block. Execution identity binds this final to one
/// manifest, ordinal, and canonical block definition even when content is
/// copied by KeepUnchanged.
/// </summary>
public sealed record DerivedRecapFinalBlock(
    string Schema,
    RecapBlockId RecapBlockId,
    ContextHeaderBlockPath Target,
    string EpochBlockExecutionSha256,
    string Content,
    string ContentSha256,
    string PayloadSha256
);

public sealed record RecapEpochBlockCommitment(
    RecapBlockId RecapBlockId,
    ContextHeaderBlockPath Target,
    int Ordinal,
    string EpochBlockExecutionSha256,
    string PayloadSha256
);

public sealed record PublishedRecapEpoch(
    string Schema,
    RefId RefId,
    EventAddress AdmissionAnchor,
    DerivedRecapEpochManifest FrozenManifest,
    IReadOnlyList<RecapEpochBlockCommitment> BlockCommitments,
    string EnvelopeSha256
);
