using System.Collections.Immutable;

namespace Atelia.SessionJournal.MemoPod;

internal sealed record MemoPodDocument {
    internal const ulong ExhaustedNextMemoOrdinal = (ulong)uint.MaxValue + 1;

    internal MemoPodDocument(
        MemoPodId podId,
        string topic,
        ulong nextMemoOrdinal,
        IEnumerable<Memo> memos
    ) {
        PodId = MemoPodSyntax.RequirePodId(podId, nameof(podId));
        Topic = MemoPodSyntax.RequireTopic(topic, nameof(topic));
        if (nextMemoOrdinal is < 1 or > ExhaustedNextMemoOrdinal) {
            throw new ArgumentOutOfRangeException(nameof(nextMemoOrdinal));
        }
        ArgumentNullException.ThrowIfNull(memos);

        var builder = ImmutableArray.CreateBuilder<Memo>();
        long activeExactTextUtf8Bytes = 0;
        long activeMemoMetadataUtf8Bytes = 0;
        uint previousOrdinal = 0;
        foreach (Memo? memo in memos) {
            if (memo is null) {
                throw new ArgumentException(
                    "The active memo sequence must not contain null.",
                    nameof(memos)
                );
            }
            if (builder.Count == MemoPodLimits.MaximumActiveMemoCount) {
                throw new ArgumentOutOfRangeException(
                    nameof(memos),
                    $"The active memo sequence exceeds {MemoPodLimits.MaximumActiveMemoCount} entries."
                );
            }

            MemoPodSyntax.RequireMemoId(memo.Id, nameof(memos));
            uint ordinal = memo.Id.Ordinal;
            if (ordinal <= previousOrdinal) {
                throw new ArgumentException(
                    "Active memos must be unique and ordered by ascending MemoId.",
                    nameof(memos)
                );
            }
            if (ordinal >= nextMemoOrdinal) {
                throw new ArgumentException(
                    "Every active MemoId must be below the next memo ordinal.",
                    nameof(memos)
                );
            }

            activeExactTextUtf8Bytes += memo.ExactTextUtf8ByteCount;
            activeMemoMetadataUtf8Bytes += memo.MetadataUtf8ByteCount;
            if (activeExactTextUtf8Bytes
                > MemoPodLimits.MaximumActiveExactTextUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    nameof(memos),
                    $"Active memo exact text exceeds {MemoPodLimits.MaximumActiveExactTextUtf8Bytes} UTF-8 bytes."
                );
            }
            if (activeMemoMetadataUtf8Bytes
                > MemoPodLimits.MaximumActiveMemoMetadataUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    nameof(memos),
                    $"Active memo metadata exceeds {MemoPodLimits.MaximumActiveMemoMetadataUtf8Bytes} UTF-8 bytes."
                );
            }

            builder.Add(memo);
            previousOrdinal = ordinal;
        }

        NextMemoOrdinal = nextMemoOrdinal;
        Memos = builder.ToImmutable();
        ActiveExactTextUtf8Bytes = checked((int)activeExactTextUtf8Bytes);
        ActiveMemoMetadataUtf8Bytes = checked(
            (int)activeMemoMetadataUtf8Bytes
        );
    }

    internal MemoPodId PodId { get; }
    internal string Topic { get; }
    internal ulong NextMemoOrdinal { get; }
    internal ImmutableArray<Memo> Memos { get; }
    internal int ActiveExactTextUtf8Bytes { get; }
    internal int ActiveMemoMetadataUtf8Bytes { get; }
}
