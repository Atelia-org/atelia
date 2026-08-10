using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

public static class HistoryPartitionAlgorithms {
    public const string FirstReplaySafeBoundaryAtTargetV1 =
        "atelia.history-timeline.partition.first-replay-safe-at-target.v1";
}

public readonly record struct TimelineId {
    public TimelineId(string value) {
        Value = HistoryTimelineSyntax.RequireLowerHex(
            value,
            32,
            nameof(value)
        );
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct HistoryRowId {
    public HistoryRowId(string value) {
        Value = HistoryTimelineSyntax.RequireLowerHex(
            value,
            64,
            nameof(value)
        );
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct HistorySegmentDescriptorDigest {
    public HistorySegmentDescriptorDigest(string value) {
        Value = HistoryTimelineSyntax.RequireLowerHex(
            value,
            64,
            nameof(value)
        );
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public static class HistoryPartitionPolicyLimits {
    public const int MaximumRawEvents =
        SJ.SessionSelectedLineageAuditLimits
            .MaximumForwardRangeEventCount;
    public const int MaximumRenderedBytes =
        HistoryLoadMeasurementSafety.V1
            .MaxBaselineRelativeWindowUtf8Bytes;
}

public sealed record PartitionPolicyRevision {
    private PartitionPolicyRevision(
        TimelineId timelineId,
        string partitionAlgorithmId,
        string historyLoadEstimatorId,
        HistoryLoadUnit targetHistoryLoad,
        int maxRawEvents,
        int maxRenderedBytes,
        string policyDigest
    ) {
        TimelineId = HistoryTimelineSyntax.RequireTimelineId(timelineId);
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
                nameof(targetHistoryLoad),
                "Target HistoryLoad must be at least one."
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
        PolicyDigest = HistoryTimelineSyntax.RequireSha256(
            policyDigest,
            nameof(policyDigest)
        );
    }

    public TimelineId TimelineId { get; }
    public string PartitionAlgorithmId { get; }
    public string HistoryLoadEstimatorId { get; }
    public HistoryLoadUnit TargetHistoryLoad { get; }
    public int MaxRawEvents { get; }
    public int MaxRenderedBytes { get; }
    public string PolicyDigest { get; }

    public static PartitionPolicyRevision Create(
        TimelineId timelineId,
        string partitionAlgorithmId,
        string historyLoadEstimatorId,
        HistoryLoadUnit targetHistoryLoad,
        int maxRawEvents,
        int maxRenderedBytes
    ) {
        HistoryTimelineSyntax.RequireTimelineId(timelineId);
        HistoryTimelineSyntax.RequireIdentifier(
            partitionAlgorithmId,
            nameof(partitionAlgorithmId)
        );
        HistoryTimelineSyntax.RequireIdentifier(
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
        byte[] body = HistoryTimelineCanonicalCodec.EncodePolicyBody(
            timelineId,
            partitionAlgorithmId,
            historyLoadEstimatorId,
            targetHistoryLoad,
            maxRawEvents,
            maxRenderedBytes
        );
        return new PartitionPolicyRevision(
            timelineId,
            partitionAlgorithmId,
            historyLoadEstimatorId,
            targetHistoryLoad,
            maxRawEvents,
            maxRenderedBytes,
            HistoryTimelineHash.Compute(
                HistoryTimelineHash.PolicyDomain,
                body
            )
        );
    }

    internal static PartitionPolicyRevision DecodeChecked(
        TimelineId timelineId,
        string partitionAlgorithmId,
        string historyLoadEstimatorId,
        HistoryLoadUnit targetHistoryLoad,
        int maxRawEvents,
        int maxRenderedBytes,
        string expectedPolicyDigest
    ) {
        PartitionPolicyRevision value = Create(
            timelineId,
            partitionAlgorithmId,
            historyLoadEstimatorId,
            targetHistoryLoad,
            maxRawEvents,
            maxRenderedBytes
        );
        if (!string.Equals(
                value.PolicyDigest,
                expectedPolicyDigest,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Partition policy digest does not match its canonical body."
            );
        }
        return value;
    }

    public byte[] ToCanonicalBytes()
        => HistoryTimelineCanonicalCodec.Encode(this);
}

public sealed record TimelineHeadRef {
    public TimelineHeadRef(
        TimelineId timelineId,
        RefId refId,
        HistoryRowId? headRowId,
        string activePartitionPolicyDigest,
        EventAddress? selectedRawHeadAtCommit,
        long generation
    ) {
        TimelineId = HistoryTimelineSyntax.RequireTimelineId(timelineId);
        RefId = HistoryTimelineSyntax.RequireRefId(refId);
        if (headRowId is { } rowId) {
            HistoryTimelineSyntax.RequireHistoryRowId(rowId);
            if (selectedRawHeadAtCommit is null) {
                throw new ArgumentException(
                    "A non-empty Timeline head requires its selected raw head fence.",
                    nameof(selectedRawHeadAtCommit)
                );
            }
            if (generation < 1) {
                throw new ArgumentOutOfRangeException(
                    nameof(generation),
                    "A non-empty Timeline head requires a positive generation."
                );
            }
        }
        else if (selectedRawHeadAtCommit is not null) {
            throw new ArgumentException(
                "An empty Timeline head has no selected raw head fence.",
                nameof(selectedRawHeadAtCommit)
            );
        }
        if (generation < 0) {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        if (selectedRawHeadAtCommit is { } selectedHead) {
            HistoryTimelineSyntax.RequireEventAddress(
                selectedHead,
                nameof(selectedRawHeadAtCommit)
            );
        }
        HeadRowId = headRowId;
        ActivePartitionPolicyDigest =
            HistoryTimelineSyntax.RequireSha256(
                activePartitionPolicyDigest,
                nameof(activePartitionPolicyDigest)
            );
        SelectedRawHeadAtCommit = selectedRawHeadAtCommit;
        Generation = generation;
    }

    public TimelineId TimelineId { get; }
    public RefId RefId { get; }
    public HistoryRowId? HeadRowId { get; }
    public string ActivePartitionPolicyDigest { get; }
    public EventAddress? SelectedRawHeadAtCommit { get; }
    public long Generation { get; }
}

public sealed record HistoryPartitionPoint {
    public HistoryPartitionPoint(
        TimelineId timelineId,
        string partitionPolicyDigest,
        EventAddress startExclusive,
        EventAddress endInclusive,
        SJ.SessionContextAnchorSetupReferences startSetups,
        SJ.SessionContextAnchorSetupReferences endSetups,
        int baselineCompletedUnitCount,
        int endCompletedUnitCount,
        HistoryLoadUnit measuredHistoryLoad,
        int rawEventCount,
        int measuredRenderedUtf8Bytes
    ) {
        TimelineId = HistoryTimelineSyntax.RequireTimelineId(timelineId);
        PartitionPolicyDigest = HistoryTimelineSyntax.RequireSha256(
            partitionPolicyDigest,
            nameof(partitionPolicyDigest)
        );
        StartExclusive = HistoryTimelineSyntax.RequireEventAddress(
            startExclusive,
            nameof(startExclusive)
        );
        EndInclusive = HistoryTimelineSyntax.RequireEventAddress(
            endInclusive,
            nameof(endInclusive)
        );
        StartSetups = HistoryTimelineSyntax.RequireSetups(
            startSetups,
            nameof(startSetups)
        );
        EndSetups = HistoryTimelineSyntax.RequireSetups(
            endSetups,
            nameof(endSetups)
        );
        if (baselineCompletedUnitCount < 0
            || endCompletedUnitCount <= baselineCompletedUnitCount) {
            throw new ArgumentOutOfRangeException(
                nameof(endCompletedUnitCount)
            );
        }
        if (measuredHistoryLoad.Value < 1) {
            throw new ArgumentOutOfRangeException(
                nameof(measuredHistoryLoad)
            );
        }
        if (rawEventCount < 1) {
            throw new ArgumentOutOfRangeException(nameof(rawEventCount));
        }
        if (measuredRenderedUtf8Bytes < 1) {
            throw new ArgumentOutOfRangeException(
                nameof(measuredRenderedUtf8Bytes)
            );
        }
        BaselineCompletedUnitCount = baselineCompletedUnitCount;
        EndCompletedUnitCount = endCompletedUnitCount;
        MeasuredHistoryLoad = measuredHistoryLoad;
        RawEventCount = rawEventCount;
        MeasuredRenderedUtf8Bytes = measuredRenderedUtf8Bytes;
    }

    public TimelineId TimelineId { get; }
    public string PartitionPolicyDigest { get; }
    public EventAddress StartExclusive { get; }
    public EventAddress EndInclusive { get; }
    public SJ.SessionContextAnchorSetupReferences StartSetups { get; }
    public SJ.SessionContextAnchorSetupReferences EndSetups { get; }
    public int BaselineCompletedUnitCount { get; }
    public int EndCompletedUnitCount { get; }
    public HistoryLoadUnit MeasuredHistoryLoad { get; }
    public int RawEventCount { get; }
    public int MeasuredRenderedUtf8Bytes { get; }
}

public enum HistoryPartitionLimitKind {
    MaxRawEvents = 1,
    MaxRenderedBytes = 2
}

public abstract record HistoryPartitionResult {
    private HistoryPartitionResult() { }

    public sealed record Selected : HistoryPartitionResult {
        public Selected(HistoryPartitionPoint point) {
            ArgumentNullException.ThrowIfNull(point);
            Point = point;
        }

        public HistoryPartitionPoint Point { get; }
    }

    public sealed record NotEnough : HistoryPartitionResult {
        public NotEnough(
            HistoryLoadUnit measuredHistoryLoad,
            int rawEventCount,
            int completedUnitCount,
            int measuredRenderedUtf8Bytes
        ) {
            ValidateCounts(
                rawEventCount,
                completedUnitCount,
                measuredRenderedUtf8Bytes
            );
            MeasuredHistoryLoad = measuredHistoryLoad;
            RawEventCount = rawEventCount;
            CompletedUnitCount = completedUnitCount;
            MeasuredRenderedUtf8Bytes = measuredRenderedUtf8Bytes;
        }

        public HistoryLoadUnit MeasuredHistoryLoad { get; }
        public int RawEventCount { get; }
        public int CompletedUnitCount { get; }
        public int MeasuredRenderedUtf8Bytes { get; }
    }

    public sealed record LimitExceeded : HistoryPartitionResult {
        public LimitExceeded(
            HistoryPartitionLimitKind limit,
            HistoryLoadUnit measuredHistoryLoad,
            int rawEventCount,
            int completedUnitCount,
            int measuredRenderedUtf8Bytes
        ) {
            if (!Enum.IsDefined(limit)) {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }
            ValidateCounts(
                rawEventCount,
                completedUnitCount,
                measuredRenderedUtf8Bytes
            );
            Limit = limit;
            MeasuredHistoryLoad = measuredHistoryLoad;
            RawEventCount = rawEventCount;
            CompletedUnitCount = completedUnitCount;
            MeasuredRenderedUtf8Bytes = measuredRenderedUtf8Bytes;
        }

        public HistoryPartitionLimitKind Limit { get; }
        public HistoryLoadUnit MeasuredHistoryLoad { get; }
        public int RawEventCount { get; }
        public int CompletedUnitCount { get; }
        public int MeasuredRenderedUtf8Bytes { get; }
    }

    private static void ValidateCounts(
        int rawEventCount,
        int completedUnitCount,
        int measuredRenderedUtf8Bytes
    ) {
        if (rawEventCount < 0) {
            throw new ArgumentOutOfRangeException(nameof(rawEventCount));
        }
        if (completedUnitCount < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(completedUnitCount)
            );
        }
        if (measuredRenderedUtf8Bytes < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(measuredRenderedUtf8Bytes)
            );
        }
    }
}

public sealed record BoundHistorySegmentRange {
    public BoundHistorySegmentRange(
        RefId refId,
        EventAddress startExclusive,
        EventAddress endInclusive,
        SJ.SessionContextAnchorSetupReferences startSetups,
        SJ.SessionContextAnchorSetupReferences endSetups,
        int baselineCompletedUnitCount,
        int endCompletedUnitCount,
        int rawEventCount,
        string rawRangeSha256
    ) {
        RefId = HistoryTimelineSyntax.RequireRefId(refId);
        StartExclusive = HistoryTimelineSyntax.RequireEventAddress(
            startExclusive,
            nameof(startExclusive)
        );
        EndInclusive = HistoryTimelineSyntax.RequireEventAddress(
            endInclusive,
            nameof(endInclusive)
        );
        StartSetups = HistoryTimelineSyntax.RequireSetups(
            startSetups,
            nameof(startSetups)
        );
        EndSetups = HistoryTimelineSyntax.RequireSetups(
            endSetups,
            nameof(endSetups)
        );
        if (baselineCompletedUnitCount < 0
            || endCompletedUnitCount <= baselineCompletedUnitCount) {
            throw new ArgumentOutOfRangeException(
                nameof(endCompletedUnitCount)
            );
        }
        if (rawEventCount < 1) {
            throw new ArgumentOutOfRangeException(nameof(rawEventCount));
        }
        BaselineCompletedUnitCount = baselineCompletedUnitCount;
        EndCompletedUnitCount = endCompletedUnitCount;
        RawEventCount = rawEventCount;
        RawRangeSha256 = HistoryTimelineSyntax.RequireSha256(
            rawRangeSha256,
            nameof(rawRangeSha256)
        );
    }

    public RefId RefId { get; }
    public EventAddress StartExclusive { get; }
    public EventAddress EndInclusive { get; }
    public SJ.SessionContextAnchorSetupReferences StartSetups { get; }
    public SJ.SessionContextAnchorSetupReferences EndSetups { get; }
    public int BaselineCompletedUnitCount { get; }
    public int EndCompletedUnitCount { get; }
    public int RawEventCount { get; }
    public string RawRangeSha256 { get; }
}

public sealed record HistorySegmentDescriptor {
    internal HistorySegmentDescriptor(
        TimelineId timelineId,
        string partitionPolicyDigestAtCreation,
        HistoryRowId rowId,
        HistoryRowId? previousRowId,
        RefId refId,
        EventAddress startExclusive,
        EventAddress endInclusive,
        SJ.SessionContextAnchorSetupReferences startSetups,
        SJ.SessionContextAnchorSetupReferences endSetups,
        string historyLoadEstimatorId,
        HistoryLoadUnit targetHistoryLoadAtCreation,
        HistoryLoadUnit measuredHistoryLoad,
        int rawEventCount,
        int measuredRenderedUtf8Bytes,
        string rawRangeSha256,
        HistorySegmentDescriptorDigest descriptorDigest
    ) {
        TimelineId = HistoryTimelineSyntax.RequireTimelineId(timelineId);
        PartitionPolicyDigestAtCreation =
            HistoryTimelineSyntax.RequireSha256(
                partitionPolicyDigestAtCreation,
                nameof(partitionPolicyDigestAtCreation)
            );
        RowId = HistoryTimelineSyntax.RequireHistoryRowId(rowId);
        if (previousRowId is { } previous) {
            HistoryTimelineSyntax.RequireHistoryRowId(previous);
        }
        PreviousRowId = previousRowId;
        RefId = HistoryTimelineSyntax.RequireRefId(refId);
        StartExclusive = HistoryTimelineSyntax.RequireEventAddress(
            startExclusive,
            nameof(startExclusive)
        );
        EndInclusive = HistoryTimelineSyntax.RequireEventAddress(
            endInclusive,
            nameof(endInclusive)
        );
        StartSetups = HistoryTimelineSyntax.RequireSetups(
            startSetups,
            nameof(startSetups)
        );
        EndSetups = HistoryTimelineSyntax.RequireSetups(
            endSetups,
            nameof(endSetups)
        );
        HistoryLoadEstimatorId = HistoryTimelineSyntax.RequireIdentifier(
            historyLoadEstimatorId,
            nameof(historyLoadEstimatorId)
        );
        if (targetHistoryLoadAtCreation.Value < 1) {
            throw new ArgumentOutOfRangeException(
                nameof(targetHistoryLoadAtCreation)
            );
        }
        if (measuredHistoryLoad.Value < targetHistoryLoadAtCreation.Value) {
            throw new ArgumentOutOfRangeException(
                nameof(measuredHistoryLoad),
                "A sealed segment must reach its target HistoryLoad."
            );
        }
        if (rawEventCount < 1) {
            throw new ArgumentOutOfRangeException(nameof(rawEventCount));
        }
        if (measuredRenderedUtf8Bytes < 1) {
            throw new ArgumentOutOfRangeException(
                nameof(measuredRenderedUtf8Bytes)
            );
        }
        TargetHistoryLoadAtCreation = targetHistoryLoadAtCreation;
        MeasuredHistoryLoad = measuredHistoryLoad;
        RawEventCount = rawEventCount;
        MeasuredRenderedUtf8Bytes = measuredRenderedUtf8Bytes;
        RawRangeSha256 = HistoryTimelineSyntax.RequireSha256(
            rawRangeSha256,
            nameof(rawRangeSha256)
        );
        DescriptorDigest =
            HistoryTimelineSyntax.RequireDescriptorDigest(
                descriptorDigest
            );
    }

    public TimelineId TimelineId { get; }
    public string PartitionPolicyDigestAtCreation { get; }
    public HistoryRowId RowId { get; }
    public HistoryRowId? PreviousRowId { get; }
    public RefId RefId { get; }
    public EventAddress StartExclusive { get; }
    public EventAddress EndInclusive { get; }
    public SJ.SessionContextAnchorSetupReferences StartSetups { get; }
    public SJ.SessionContextAnchorSetupReferences EndSetups { get; }
    public string HistoryLoadEstimatorId { get; }
    public HistoryLoadUnit TargetHistoryLoadAtCreation { get; }
    public HistoryLoadUnit MeasuredHistoryLoad { get; }
    public int RawEventCount { get; }
    public int MeasuredRenderedUtf8Bytes { get; }
    public string RawRangeSha256 { get; }
    public HistorySegmentDescriptorDigest DescriptorDigest { get; }

    public byte[] ToCanonicalBytes()
        => HistoryTimelineCanonicalCodec.Encode(this);
}

public static class HistorySegmentDescriptorFactory {
    public static HistorySegmentDescriptor Create(
        HistoryPartitionPoint point,
        BoundHistorySegmentRange boundRange,
        PartitionPolicyRevision policy,
        HistorySegmentDescriptor? predecessor
    ) {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(boundRange);
        ArgumentNullException.ThrowIfNull(policy);
        HistoryTimelineSyntax.RequireMatching(
            policy.TimelineId == point.TimelineId,
            "Partition point belongs to a different Timeline."
        );
        HistoryTimelineSyntax.RequireMatching(
            string.Equals(
                policy.PolicyDigest,
                point.PartitionPolicyDigest,
                StringComparison.Ordinal
            ),
            "Partition point belongs to a different policy revision."
        );
        HistoryTimelineSyntax.RequireMatching(
            point.RawEventCount <= policy.MaxRawEvents
            && point.MeasuredRenderedUtf8Bytes
                <= policy.MaxRenderedBytes,
            "Partition point exceeds its policy revision limits."
        );
        HistoryTimelineSyntax.RequireMatching(
            point.StartExclusive == boundRange.StartExclusive
            && point.EndInclusive == boundRange.EndInclusive
            && point.StartSetups == boundRange.StartSetups
            && point.EndSetups == boundRange.EndSetups
            && point.BaselineCompletedUnitCount
                == boundRange.BaselineCompletedUnitCount
            && point.EndCompletedUnitCount
                == boundRange.EndCompletedUnitCount
            && point.RawEventCount == boundRange.RawEventCount,
            "Bound raw range does not match the selected partition point."
        );

        HistoryRowId? previousRowId = null;
        if (predecessor is not null) {
            HistoryTimelineSyntax.RequireMatching(
                predecessor.TimelineId == policy.TimelineId
                && predecessor.RefId == boundRange.RefId
                && predecessor.EndInclusive == point.StartExclusive
                && predecessor.EndSetups == point.StartSetups,
                "Predecessor does not bind the selected segment start."
            );
            previousRowId = predecessor.RowId;
        }

        byte[] body = HistoryTimelineCanonicalCodec.EncodeDescriptorBody(
            policy.TimelineId,
            policy.PolicyDigest,
            previousRowId,
            boundRange.RefId,
            point.StartExclusive,
            point.EndInclusive,
            point.StartSetups,
            point.EndSetups,
            policy.HistoryLoadEstimatorId,
            policy.TargetHistoryLoad,
            point.MeasuredHistoryLoad,
            point.RawEventCount,
            point.MeasuredRenderedUtf8Bytes,
            boundRange.RawRangeSha256
        );
        var rowId = new HistoryRowId(HistoryTimelineHash.Compute(
            HistoryTimelineHash.RowIdDomain,
            body
        ));
        var descriptorDigest = new HistorySegmentDescriptorDigest(
            HistoryTimelineHash.Compute(
                HistoryTimelineHash.DescriptorDomain,
                body
            )
        );
        return new HistorySegmentDescriptor(
            policy.TimelineId,
            policy.PolicyDigest,
            rowId,
            previousRowId,
            boundRange.RefId,
            point.StartExclusive,
            point.EndInclusive,
            point.StartSetups,
            point.EndSetups,
            policy.HistoryLoadEstimatorId,
            policy.TargetHistoryLoad,
            point.MeasuredHistoryLoad,
            point.RawEventCount,
            point.MeasuredRenderedUtf8Bytes,
            boundRange.RawRangeSha256,
            descriptorDigest
        );
    }
}

public sealed record HistoryRowProposal {
    public HistoryRowProposal(
        TimelineHeadRef expectedHead,
        EventAddress capturedSelectedRawHead,
        HistorySegmentDescriptor descriptor
    ) {
        ArgumentNullException.ThrowIfNull(expectedHead);
        ArgumentNullException.ThrowIfNull(descriptor);
        CapturedSelectedRawHead =
            HistoryTimelineSyntax.RequireEventAddress(
                capturedSelectedRawHead,
                nameof(capturedSelectedRawHead)
            );
        HistoryTimelineSyntax.RequireMatching(
            expectedHead.TimelineId == descriptor.TimelineId
            && expectedHead.RefId == descriptor.RefId
            && expectedHead.HeadRowId == descriptor.PreviousRowId
            && string.Equals(
                expectedHead.ActivePartitionPolicyDigest,
                descriptor.PartitionPolicyDigestAtCreation,
                StringComparison.Ordinal
            ),
            "Row proposal does not extend its exact expected Timeline head."
        );
        ExpectedHead = expectedHead;
        Descriptor = descriptor;
    }

    public TimelineHeadRef ExpectedHead { get; }
    public EventAddress CapturedSelectedRawHead { get; }
    public HistorySegmentDescriptor Descriptor { get; }
    public ReadOnlyMemory<byte> CanonicalDescriptorBytes
        => Descriptor.ToCanonicalBytes();
}

internal static class HistoryTimelineSyntax {
    internal const int MaximumIdentifierUtf8Bytes = 128;
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static TimelineId RequireTimelineId(TimelineId value) {
        _ = RequireLowerHex(value.Value, 32, nameof(value));
        return value;
    }

    internal static HistoryRowId RequireHistoryRowId(HistoryRowId value) {
        _ = RequireLowerHex(value.Value, 64, nameof(value));
        return value;
    }

    internal static HistorySegmentDescriptorDigest RequireDescriptorDigest(
        HistorySegmentDescriptorDigest value
    ) {
        _ = RequireLowerHex(value.Value, 64, nameof(value));
        return value;
    }

    internal static RefId RequireRefId(RefId value) {
        if (value.IsDefault) {
            throw new ArgumentException("RefId cannot be default.", nameof(value));
        }
        return value;
    }

    internal static EventAddress RequireEventAddress(
        EventAddress value,
        string parameterName
    ) {
        _ = SJ.EventAddressTextCodec.Format(value);
        return value;
    }

    internal static SJ.SessionContextAnchorSetupReferences RequireSetups(
        SJ.SessionContextAnchorSetupReferences value,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        RequireSetup(value.RuntimeConfig, $"{parameterName}.RuntimeConfig");
        RequireSetup(value.SystemPrompt, $"{parameterName}.SystemPrompt");
        return value;
    }

    internal static void RequireSetup(
        SJ.SessionContextSetupReference value,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        RequireEventAddress(value.Address, $"{parameterName}.Address");
        if (value.BodySchemaVersion < 1) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Setup schema version must be positive."
            );
        }
        _ = RequireSha256(value.PayloadSha256, parameterName);
    }

    internal static string RequireIdentifier(
        string value,
        string parameterName
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        int byteCount;
        try {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (System.Text.EncoderFallbackException exception) {
            throw new ArgumentException(
                "Identifier contains invalid UTF-16 input.",
                parameterName,
                exception
            );
        }
        if (byteCount > MaximumIdentifierUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Identifier exceeds {MaximumIdentifierUtf8Bytes} UTF-8 bytes."
            );
        }
        return value;
    }

    internal static string RequireSha256(
        string value,
        string parameterName
    ) => RequireLowerHex(value, 64, parameterName);

    internal static string RequireLowerHex(
        string value,
        int expectedLength,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != expectedLength
            || value.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))) {
            throw new ArgumentException(
                $"Value must be exactly {expectedLength} lowercase hexadecimal characters.",
                parameterName
            );
        }
        return value;
    }

    internal static void RequireMatching(bool condition, string message) {
        if (!condition) {
            throw new InvalidDataException(message);
        }
    }
}
