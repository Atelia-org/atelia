using System.Collections.Immutable;

namespace Atelia.SessionJournal.MemoPod;

internal sealed class MemoPodWorkingAggregate {
    private readonly SortedDictionary<uint, Memo> _memos;
    private ulong _nextMemoOrdinal;
    private int _activeExactTextUtf8Bytes;

    private MemoPodWorkingAggregate(
        MemoPodId podId,
        string topic,
        ulong nextMemoOrdinal,
        SortedDictionary<uint, Memo> memos,
        int activeExactTextUtf8Bytes
    ) {
        PodId = podId;
        Topic = topic;
        _nextMemoOrdinal = nextMemoOrdinal;
        _memos = memos;
        _activeExactTextUtf8Bytes = activeExactTextUtf8Bytes;
    }

    internal MemoPodId PodId { get; }
    internal string Topic { get; }
    internal ulong NextMemoOrdinal => _nextMemoOrdinal;

    internal static MemoPodWorkingAggregate CreateNew(
        MemoPodId podId,
        string topic
    ) => new(
        MemoPodSyntax.RequirePodId(podId, nameof(podId)),
        MemoPodSyntax.RequireTopic(topic, nameof(topic)),
        nextMemoOrdinal: 1,
        new SortedDictionary<uint, Memo>(),
        activeExactTextUtf8Bytes: 0
    );

    internal static MemoPodWorkingAggregate FromDocument(
        MemoPodDocument document
    ) {
        ArgumentNullException.ThrowIfNull(document);
        var memos = new SortedDictionary<uint, Memo>();
        foreach (Memo memo in document.Memos) {
            memos.Add(memo.Id.Ordinal, memo);
        }
        return new MemoPodWorkingAggregate(
            document.PodId,
            document.Topic,
            document.NextMemoOrdinal,
            memos,
            document.ActiveExactTextUtf8Bytes
        );
    }

    internal MemoId Append(string exactText) {
        int exactTextUtf8ByteCount = MemoPodSyntax.RequireMemoExactText(
            exactText,
            nameof(exactText)
        );
        if (_nextMemoOrdinal > uint.MaxValue) {
            throw new InvalidOperationException(
                "The MemoId space is exhausted."
            );
        }
        if (_memos.Count == MemoPodLimits.MaximumActiveMemoCount) {
            throw new InvalidOperationException(
                "The active memo count limit has been reached."
            );
        }
        if ((long)_activeExactTextUtf8Bytes + exactTextUtf8ByteCount
            > MemoPodLimits.MaximumActiveExactTextUtf8Bytes) {
            throw new InvalidOperationException(
                "The active memo exact-text byte limit has been reached."
            );
        }

        MemoId id = MemoId.FromOrdinal((uint)_nextMemoOrdinal);
        var memo = new Memo(id, exactText);
        _memos.Add(id.Ordinal, memo);
        _nextMemoOrdinal++;
        _activeExactTextUtf8Bytes += exactTextUtf8ByteCount;
        return id;
    }

    internal void Remove(MemoId id) {
        MemoPodSyntax.RequireMemoId(id, nameof(id));
        if (!_memos.TryGetValue(id.Ordinal, out Memo? memo)) {
            throw new KeyNotFoundException(
                $"Active memo '{id}' does not exist."
            );
        }
        _memos.Remove(id.Ordinal);
        _activeExactTextUtf8Bytes -= memo.ExactTextUtf8ByteCount;
    }

    internal Memo Get(MemoId id) {
        MemoPodSyntax.RequireMemoId(id, nameof(id));
        return _memos.TryGetValue(id.Ordinal, out Memo? memo)
            ? memo
            : throw new KeyNotFoundException(
                $"Active memo '{id}' does not exist."
            );
    }

    internal bool TryGet(MemoId id, out Memo? memo) {
        MemoPodSyntax.RequireMemoId(id, nameof(id));
        return _memos.TryGetValue(id.Ordinal, out memo);
    }

    internal ImmutableArray<Memo> List()
        => ImmutableArray.CreateRange(_memos.Values);

    internal MemoPodDocument CaptureDocument()
        => new(PodId, Topic, _nextMemoOrdinal, _memos.Values);
}
