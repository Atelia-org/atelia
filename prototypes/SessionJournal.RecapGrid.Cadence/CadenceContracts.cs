using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Cadence;

public static class RecapGridCadenceLimits {
    public const int MaximumCanonicalUtf8Bytes = 4 * 1024;
    public const long MaximumHistoryLoad = long.MaxValue;
}

public readonly record struct RecapGridCadenceDomainDigest {
    public RecapGridCadenceDomainDigest(string value) {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(static value =>
                value is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))) {
            throw new ArgumentException(
                "A cadence domain digest must be lowercase SHA-256.",
                nameof(value));
        }
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public sealed record RecapGridCadenceHeadRef {
    public RecapGridCadenceHeadRef(
        RefId refId,
        long generation,
        RecapGridCadenceDomainDigest domainDigest
    ) {
        if (refId.IsDefault) {
            throw new ArgumentException("RefId must not be default.", nameof(refId));
        }
        if (generation < 0) {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        if (domainDigest.Value is null) {
            throw new ArgumentException(
                "Cadence domain digest must not be default.",
                nameof(domainDigest));
        }
        RefId = refId;
        Generation = generation;
        DomainDigest = domainDigest;
    }

    public RefId RefId { get; }
    public long Generation { get; }
    public RecapGridCadenceDomainDigest DomainDigest { get; }
}

public sealed class RecapGridCadencePolicySpec {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public RecapGridCadencePolicySpec(
        long minimumRecentHistoryLoad,
        string partitionAlgorithmId,
        string historyLoadEstimatorId,
        long targetHistoryLoad,
        int maxRawEvents,
        int maxRenderedBytes
    ) {
        if (minimumRecentHistoryLoad is < 1
            or > RecapGridCadenceLimits.MaximumHistoryLoad) {
            throw new ArgumentOutOfRangeException(nameof(minimumRecentHistoryLoad));
        }
        if (targetHistoryLoad is < 1
            or > RecapGridCadenceLimits.MaximumHistoryLoad) {
            throw new ArgumentOutOfRangeException(nameof(targetHistoryLoad));
        }
        _ = checked(minimumRecentHistoryLoad + targetHistoryLoad);
        if (!string.Equals(
                partitionAlgorithmId,
                HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
                StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The partition algorithm is unavailable.",
                nameof(partitionAlgorithmId));
        }
        int estimatorUtf8Bytes;
        try {
            estimatorUtf8Bytes = StrictUtf8.GetByteCount(
                historyLoadEstimatorId ?? string.Empty);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "HistoryLoad estimator ID must be strict UTF-8 text.",
                nameof(historyLoadEstimatorId), exception);
        }
        if (string.IsNullOrWhiteSpace(historyLoadEstimatorId)
            || estimatorUtf8Bytes > 128
            || !string.Equals(historyLoadEstimatorId,
                historyLoadEstimatorId.Trim(), StringComparison.Ordinal)
            || historyLoadEstimatorId.Any(char.IsControl)) {
            throw new ArgumentException(
                "HistoryLoad estimator ID must be bounded canonical text.",
                nameof(historyLoadEstimatorId));
        }
        if (maxRawEvents is < 1
            or > HistoryPartitionPolicyLimits.MaximumRawEvents) {
            throw new ArgumentOutOfRangeException(nameof(maxRawEvents));
        }
        if (maxRenderedBytes is < 1
            or > HistoryPartitionPolicyLimits.MaximumRenderedBytes) {
            throw new ArgumentOutOfRangeException(nameof(maxRenderedBytes));
        }
        MinimumRecentHistoryLoad = minimumRecentHistoryLoad;
        PartitionAlgorithmId = partitionAlgorithmId;
        HistoryLoadEstimatorId = historyLoadEstimatorId;
        TargetHistoryLoad = targetHistoryLoad;
        MaxRawEvents = maxRawEvents;
        MaxRenderedBytes = maxRenderedBytes;
    }

    public long MinimumRecentHistoryLoad { get; }
    public string PartitionAlgorithmId { get; }
    public string HistoryLoadEstimatorId { get; }
    public long TargetHistoryLoad { get; }
    public int MaxRawEvents { get; }
    public int MaxRenderedBytes { get; }
}

public sealed class RecapGridCadenceSnapshot {
    internal RecapGridCadenceSnapshot(
        RecapGridCadenceHeadRef head,
        RecapGridCadencePolicySpec policy,
        byte[] canonicalBytes
    ) {
        Head = head;
        Policy = policy;
        _canonicalBytes = canonicalBytes;
    }

    private readonly byte[] _canonicalBytes;
    public RecapGridCadenceHeadRef Head { get; }
    public RecapGridCadencePolicySpec Policy { get; }
    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public static RecapGridCadenceSnapshot DecodeCanonical(
        ReadOnlySpan<byte> bytes
    ) => CadenceCanonicalCodec.Decode(bytes);
}

public abstract record RecapGridCadenceCreateResult {
    private RecapGridCadenceCreateResult() { }
    public sealed record Created(RecapGridCadenceSnapshot Snapshot)
        : RecapGridCadenceCreateResult;
    public sealed record AlreadyExists(RecapGridCadenceSnapshot Snapshot)
        : RecapGridCadenceCreateResult;
    public sealed record Busy : RecapGridCadenceCreateResult;
    public sealed record CommitIndeterminate(
        RecapGridCadenceHeadRef Intended,
        RecapGridCadenceHeadRef? Observed
    ) : RecapGridCadenceCreateResult;
    public sealed record UnsupportedSchema(int Version)
        : RecapGridCadenceCreateResult;
    public sealed record PlatformUnsupported : RecapGridCadenceCreateResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridCadenceCreateResult;
}

public abstract record RecapGridCadenceOpenResult {
    private RecapGridCadenceOpenResult() { }
    public sealed record Opened(RecapGridCadenceHandle Handle)
        : RecapGridCadenceOpenResult;
    public sealed record Absent : RecapGridCadenceOpenResult;
    public sealed record Busy : RecapGridCadenceOpenResult;
    public sealed record UnsupportedSchema(int Version)
        : RecapGridCadenceOpenResult;
    public sealed record PlatformUnsupported : RecapGridCadenceOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridCadenceOpenResult;
}

public abstract record RecapGridCadenceReaderOpenResult {
    private RecapGridCadenceReaderOpenResult() { }
    public sealed record Opened(RecapGridCadenceReaderHandle Handle)
        : RecapGridCadenceReaderOpenResult;
    public sealed record Absent : RecapGridCadenceReaderOpenResult;
    public sealed record Busy : RecapGridCadenceReaderOpenResult;
    public sealed record UnsupportedSchema(int Version)
        : RecapGridCadenceReaderOpenResult;
    public sealed record PlatformUnsupported
        : RecapGridCadenceReaderOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridCadenceReaderOpenResult;
}

public abstract record RecapGridCadenceInspectResult {
    private RecapGridCadenceInspectResult() { }
    public sealed record Available(RecapGridCadenceSnapshot Snapshot)
        : RecapGridCadenceInspectResult;
    public sealed record Absent : RecapGridCadenceInspectResult;
    public sealed record Busy : RecapGridCadenceInspectResult;
    public sealed record UnsupportedSchema(int Version)
        : RecapGridCadenceInspectResult;
    public sealed record PlatformUnsupported : RecapGridCadenceInspectResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridCadenceInspectResult;
}

public abstract record RecapGridCadenceReadResult {
    private RecapGridCadenceReadResult() { }
    public sealed record Available(RecapGridCadenceSnapshot Snapshot)
        : RecapGridCadenceReadResult;
    public sealed record Disposed : RecapGridCadenceReadResult;
    public sealed record Busy : RecapGridCadenceReadResult;
    public sealed record UnsupportedSchema(int Version)
        : RecapGridCadenceReadResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridCadenceReadResult;
}

public abstract record RecapGridCadenceCompareExchangeResult {
    private RecapGridCadenceCompareExchangeResult() { }
    public sealed record Updated(RecapGridCadenceSnapshot Snapshot)
        : RecapGridCadenceCompareExchangeResult;
    public sealed record Unchanged(RecapGridCadenceSnapshot Snapshot)
        : RecapGridCadenceCompareExchangeResult;
    public sealed record Stale(RecapGridCadenceHeadRef Actual)
        : RecapGridCadenceCompareExchangeResult;
    public sealed record Busy : RecapGridCadenceCompareExchangeResult;
    public sealed record CommitIndeterminate(
        RecapGridCadenceHeadRef Intended,
        RecapGridCadenceHeadRef? Observed
    ) : RecapGridCadenceCompareExchangeResult;
    public sealed record Disposed : RecapGridCadenceCompareExchangeResult;
    public sealed record UnsupportedSchema(int Version)
        : RecapGridCadenceCompareExchangeResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridCadenceCompareExchangeResult;
}
