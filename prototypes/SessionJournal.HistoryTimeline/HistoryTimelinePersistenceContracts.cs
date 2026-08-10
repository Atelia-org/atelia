using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

public static class HistoryTimelineStoreLimits {
    public const int MaximumHeadUtf8Bytes = 4 * 1024;
    public const int MaximumLocatorUtf8Bytes = 4 * 1024;
    public const int MaximumBackupManifestUtf8Bytes = 16 * 1024;
    public const int MaximumPolicyCount = 65_536;
    public const int MaximumRowCount = 65_536;
    public const int MaximumTrieNodeCount = 3_276_800;
    public const int MaximumPathPageRows = 128;
    public const int MaximumPathPageUtf8Bytes = 4 * 1024 * 1024;
    public const long MaximumDatabaseBytes =
        8L * 1024 * 1024 * 1024;
    public const long MaximumRestoreCopyBytes = MaximumDatabaseBytes;
}

/// <summary>
/// Timeline-independent input used only when a factory creates a fresh
/// Timeline identity and its initial policy revision.
/// </summary>
public sealed record HistoryTimelineInitialPolicySpec {
    public HistoryTimelineInitialPolicySpec(
        string partitionAlgorithmId,
        string historyLoadEstimatorId,
        HistoryLoadUnit targetHistoryLoad,
        int maxRawEvents,
        int maxRenderedBytes
    ) {
        PartitionAlgorithmId = HistoryTimelineSyntax.RequireIdentifier(
            partitionAlgorithmId,
            nameof(partitionAlgorithmId)
        );
        HistoryLoadEstimatorId = HistoryTimelineSyntax.RequireIdentifier(
            historyLoadEstimatorId,
            nameof(historyLoadEstimatorId)
        );
        if (targetHistoryLoad.Value < 1) {
            throw new ArgumentOutOfRangeException(
                nameof(targetHistoryLoad)
            );
        }
        if (maxRawEvents is < 1
            or > HistoryPartitionPolicyLimits.MaximumRawEvents) {
            throw new ArgumentOutOfRangeException(nameof(maxRawEvents));
        }
        if (maxRenderedBytes is < 1
            or > HistoryPartitionPolicyLimits.MaximumRenderedBytes) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRenderedBytes)
            );
        }
        TargetHistoryLoad = targetHistoryLoad;
        MaxRawEvents = maxRawEvents;
        MaxRenderedBytes = maxRenderedBytes;
    }

    public string PartitionAlgorithmId { get; }
    public string HistoryLoadEstimatorId { get; }
    public HistoryLoadUnit TargetHistoryLoad { get; }
    public int MaxRawEvents { get; }
    public int MaxRenderedBytes { get; }

    internal PartitionPolicyRevision CreatePolicy(TimelineId timelineId)
        => PartitionPolicyRevision.Create(
            timelineId,
            PartitionAlgorithmId,
            HistoryLoadEstimatorId,
            TargetHistoryLoad,
            MaxRawEvents,
            MaxRenderedBytes
        );
}

public sealed record ActiveTimelineLocator {
    public ActiveTimelineLocator(
        RefId refId,
        TimelineId activeTimelineId,
        long generation
    ) {
        RefId = HistoryTimelineSyntax.RequireRefId(refId);
        ActiveTimelineId = HistoryTimelineSyntax.RequireTimelineId(
            activeTimelineId
        );
        if (generation < 0) {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        Generation = generation;
    }

    public RefId RefId { get; }
    public TimelineId ActiveTimelineId { get; }
    public long Generation { get; }

    public byte[] ToCanonicalBytes()
        => HistoryTimelineCanonicalCodec.Encode(this);
}

public abstract record HistoryTimelineSnapshotResult {
    private HistoryTimelineSnapshotResult() { }

    public sealed record Available(TimelineHeadRef Head)
        : HistoryTimelineSnapshotResult;

    public sealed record Busy : HistoryTimelineSnapshotResult;

    public sealed record UnsupportedSchema(int SchemaVersion)
        : HistoryTimelineSnapshotResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineSnapshotResult;
}

public abstract record HistoryTimelineCreateResult {
    private HistoryTimelineCreateResult() { }

    public sealed record Created(
        ActiveTimelineLocator Locator,
        TimelineHeadRef InitialHead
    ) : HistoryTimelineCreateResult;

    public sealed record AlreadyExists(ActiveTimelineLocator Locator)
        : HistoryTimelineCreateResult;

    public sealed record Busy : HistoryTimelineCreateResult;

    public sealed record LimitExceeded(string Limit)
        : HistoryTimelineCreateResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineCreateResult;
}

public abstract record HistoryTimelineOpenResult {
    private HistoryTimelineOpenResult() { }

    public sealed record Opened(HistoryTimelineHandle Handle)
        : HistoryTimelineOpenResult;

    public sealed record Absent : HistoryTimelineOpenResult;

    public sealed record Busy : HistoryTimelineOpenResult;

    public sealed record UnsupportedSchema(int SchemaVersion)
        : HistoryTimelineOpenResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineOpenResult;
}

public abstract record HistoryTimelineReaderOpenResult {
    private HistoryTimelineReaderOpenResult() { }

    public sealed record Opened(HistoryTimelineReaderHandle Handle)
        : HistoryTimelineReaderOpenResult;

    public sealed record Absent : HistoryTimelineReaderOpenResult;
    public sealed record Busy : HistoryTimelineReaderOpenResult;
    public sealed record UnsupportedSchema(int SchemaVersion)
        : HistoryTimelineReaderOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineReaderOpenResult;
}

public sealed class HistoryTimelineHandle : IDisposable {
    private readonly HistoryTimelineLifetime _lifetime;

    internal HistoryTimelineHandle(
        ActiveTimelineLocator locator,
        HistoryTimelineCoordinator coordinator,
        HistoryTimelineReader reader,
        HistoryTimelineLifetime lifetime
    ) {
        Locator = locator;
        Coordinator = coordinator;
        Reader = reader;
        _lifetime = lifetime;
    }

    public ActiveTimelineLocator Locator { get; }
    public HistoryTimelineCoordinator Coordinator { get; }
    public HistoryTimelineReader Reader { get; }

    public void Dispose() => _lifetime.Dispose();
}

public sealed class HistoryTimelineReaderHandle : IDisposable {
    private readonly HistoryTimelineLifetime _lifetime;

    internal HistoryTimelineReaderHandle(
        ActiveTimelineLocator locator,
        HistoryTimelineReader reader,
        HistoryTimelineLifetime lifetime
    ) {
        Locator = locator;
        Reader = reader;
        _lifetime = lifetime;
    }

    public ActiveTimelineLocator Locator { get; }
    public HistoryTimelineReader Reader { get; }

    public void Dispose() => _lifetime.Dispose();
}

public readonly record struct HistoryTimelinePathCursor {
    internal HistoryTimelinePathCursor(
        TimelineId timelineId,
        RefId refId,
        long generation,
        HistoryRowId nextRowId
    ) {
        TimelineId = timelineId;
        RefId = refId;
        Generation = generation;
        NextRowId = nextRowId;
    }

    internal TimelineId TimelineId { get; }
    internal RefId RefId { get; }
    internal long Generation { get; }
    internal HistoryRowId NextRowId { get; }
}

public sealed class HistoryTimelineAncestorWitness {
    internal HistoryTimelineAncestorWitness(
        string canonicalRepositoryPath,
        TimelineHeadRef wholeHead,
        HistorySegmentDescriptor descriptor
    ) {
        CanonicalRepositoryPath = canonicalRepositoryPath;
        WholeHead = wholeHead;
        RowId = descriptor.RowId;
        DescriptorDigest = descriptor.DescriptorDigest;
    }

    internal string CanonicalRepositoryPath { get; }
    public TimelineHeadRef WholeHead { get; }
    public HistoryRowId RowId { get; }
    public HistorySegmentDescriptorDigest DescriptorDigest { get; }
}

public sealed class HistoryTimelineSelectedRow {
    internal HistoryTimelineSelectedRow(
        HistorySegmentDescriptor descriptor,
        HistoryTimelineAncestorWitness witness
    ) {
        if (descriptor.RowId != witness.RowId
            || descriptor.DescriptorDigest
                != witness.DescriptorDigest) {
            throw new ArgumentException(
                "The selected row and ancestor witness must bind the same descriptor."
            );
        }
        Descriptor = descriptor;
        Witness = witness;
    }

    public HistorySegmentDescriptor Descriptor { get; }
    public HistoryTimelineAncestorWitness Witness { get; }
}

public abstract record HistoryTimelineReaderRowResult {
    private HistoryTimelineReaderRowResult() { }

    public sealed record Selected(HistoryTimelineSelectedRow Row)
        : HistoryTimelineReaderRowResult;

    public sealed record NotOnSelectedPath(HistoryRowId RowId)
        : HistoryTimelineReaderRowResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryTimelineReaderRowResult;

    public sealed record Busy : HistoryTimelineReaderRowResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineReaderRowResult;
}

public sealed record HistoryTimelinePathPage(
    IReadOnlyList<HistoryTimelineSelectedRow> Rows,
    HistoryTimelinePathCursor? Next
);

public abstract record HistoryTimelinePathPageResult {
    private HistoryTimelinePathPageResult() { }

    public sealed record Page(HistoryTimelinePathPage Value)
        : HistoryTimelinePathPageResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryTimelinePathPageResult;

    public sealed record Busy : HistoryTimelinePathPageResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelinePathPageResult;
}

public sealed record HistoryTimelineActiveConfirmation {
    public HistoryTimelineActiveConfirmation(
        ActiveTimelineLocator locator,
        TimelineHeadRef head
    ) {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(head);
        if (locator.RefId != head.RefId
            || locator.ActiveTimelineId != head.TimelineId) {
            throw new ArgumentException(
                "The locator and head must bind the same active Timeline."
            );
        }
        Locator = locator;
        Head = head;
    }

    public ActiveTimelineLocator Locator { get; }
    public TimelineHeadRef Head { get; }
}

public sealed record HistoryTimelineBackupManifest {
    internal HistoryTimelineBackupManifest(
        ActiveTimelineLocator locator,
        TimelineHeadRef head,
        string headSha256,
        string databaseSha256,
        long databaseBytes
    ) {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(head);
        if (locator.RefId != head.RefId
            || locator.ActiveTimelineId != head.TimelineId) {
            throw new ArgumentException(
                "The backup locator and head bind different Timeline scopes."
            );
        }
        Locator = locator;
        Head = head;
        HeadSha256 = HistoryTimelineSyntax.RequireSha256(
            headSha256,
            nameof(headSha256)
        );
        DatabaseSha256 = HistoryTimelineSyntax.RequireSha256(
            databaseSha256,
            nameof(databaseSha256)
        );
        string expectedHeadSha256 = HistoryTimelineHash.Compute(
            SqliteHistoryTimelineLedger.HeadHashDomain,
            head.ToCanonicalBytes()
        );
        if (!string.Equals(
                HeadSha256,
                expectedHeadSha256,
                StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The backup head digest differs from its canonical head.",
                nameof(headSha256)
            );
        }
        if (databaseBytes is < 1
            or > HistoryTimelineStoreLimits.MaximumDatabaseBytes) {
            throw new ArgumentOutOfRangeException(
                nameof(databaseBytes)
            );
        }
        DatabaseBytes = databaseBytes;
    }

    public ActiveTimelineLocator Locator { get; }
    public TimelineHeadRef Head { get; }
    public string HeadSha256 { get; }
    public string DatabaseSha256 { get; }
    public long DatabaseBytes { get; }
}

public abstract record HistoryTimelineInspectResult {
    private HistoryTimelineInspectResult() { }

    public sealed record Available(
        ActiveTimelineLocator Locator,
        TimelineHeadRef Head
    ) : HistoryTimelineInspectResult;

    public sealed record Absent : HistoryTimelineInspectResult;
    public sealed record Busy : HistoryTimelineInspectResult;
    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineInspectResult;
}

public sealed record HistoryTimelineExportPage(
    ActiveTimelineLocator Locator,
    TimelineHeadRef Head,
    HistoryTimelinePathPage Path
);

public abstract record HistoryTimelineExportResult {
    private HistoryTimelineExportResult() { }

    public sealed record Page(HistoryTimelineExportPage Value)
        : HistoryTimelineExportResult;

    public sealed record Absent : HistoryTimelineExportResult;
    public sealed record Busy : HistoryTimelineExportResult;
    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryTimelineExportResult;
    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineExportResult;
}

public abstract record HistoryTimelineBackupResult {
    private HistoryTimelineBackupResult() { }

    public sealed record Created(
        HistoryTimelineBackupManifest Manifest,
        string BackupDirectory
    ) : HistoryTimelineBackupResult;

    public sealed record Absent : HistoryTimelineBackupResult;
    public sealed record Busy : HistoryTimelineBackupResult;
    public sealed record LimitExceeded(string Limit)
        : HistoryTimelineBackupResult;
    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineBackupResult;
}

public abstract record HistoryTimelineRestoreResult {
    private HistoryTimelineRestoreResult() { }

    public sealed record Restored(
        ActiveTimelineLocator Locator,
        TimelineHeadRef Head
    ) : HistoryTimelineRestoreResult;

    public sealed record ConfirmationMismatch(
        ActiveTimelineLocator ActualLocator,
        TimelineHeadRef ActualHead
    ) : HistoryTimelineRestoreResult;

    public sealed record Busy : HistoryTimelineRestoreResult;
    public sealed record LimitExceeded(string Limit)
        : HistoryTimelineRestoreResult;
    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineRestoreResult;
}

public abstract record HistoryTimelineAbandonResult {
    private HistoryTimelineAbandonResult() { }

    public sealed record Abandoned(
        ActiveTimelineLocator Locator,
        TimelineHeadRef InitialHead
    ) : HistoryTimelineAbandonResult;

    public sealed record ConfirmationMismatch(
        ActiveTimelineLocator ActualLocator
    ) : HistoryTimelineAbandonResult;

    public sealed record Busy : HistoryTimelineAbandonResult;
    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineAbandonResult;
}

internal sealed record HistoryTimelineSelectedPathSnapshotBody {
    internal HistoryTimelineSelectedPathSnapshotBody(
        HistoryRowId headRowId,
        string rowRootDigest,
        string endRootDigest,
        int memberCount
    ) {
        HistoryTimelineSyntax.RequireHistoryRowId(headRowId);
        HeadRowId = headRowId;
        RowRootDigest = HistoryTimelineSyntax.RequireSha256(
            rowRootDigest,
            nameof(rowRootDigest)
        );
        EndRootDigest = HistoryTimelineSyntax.RequireSha256(
            endRootDigest,
            nameof(endRootDigest)
        );
        if (memberCount < 1) {
            throw new ArgumentOutOfRangeException(nameof(memberCount));
        }
        MemberCount = memberCount;
    }

    internal HistoryRowId HeadRowId { get; }
    internal string RowRootDigest { get; }
    internal string EndRootDigest { get; }
    internal int MemberCount { get; }
}
