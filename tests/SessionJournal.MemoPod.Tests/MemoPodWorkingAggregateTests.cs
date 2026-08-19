using Atelia.SessionJournal.MemoPod;

namespace Atelia.SessionJournal.MemoPod.Tests;

public sealed class MemoPodWorkingAggregateTests {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "22222222222222222222222222222222"
    );

    [Fact]
    public void AppendAllocatesAscendingIdsAndAllowsDuplicateExactText() {
        MemoPodWorkingAggregate aggregate = CreateAggregate();

        MemoId first = aggregate.Append("same text");
        MemoId second = aggregate.Append("same text");

        Assert.Equal("m1:00000001", first.Value);
        Assert.Equal("m1:00000002", second.Value);
        Assert.Equal(
            [first, second],
            aggregate.List().Select(static memo => memo.Id).ToArray()
        );
        Assert.Same(aggregate.Get(first), aggregate.List()[0]);
        Assert.True(aggregate.TryGet(second, out Memo? found));
        Assert.Same(aggregate.Get(second), found);
    }

    [Fact]
    public void RemoveCreatesGapAndNeverReusesId() {
        MemoPodWorkingAggregate aggregate = CreateAggregate();
        MemoId first = aggregate.Append("old");
        MemoId second = aggregate.Append("keep");

        aggregate.Remove(first);
        MemoId third = aggregate.Append("new");
        MemoPodDocument document = aggregate.CaptureDocument();

        Assert.Equal("m1:00000003", third.Value);
        Assert.Equal(4UL, document.NextMemoOrdinal);
        Assert.Equal(
            [second, third],
            document.Memos.Select(static memo => memo.Id).ToArray()
        );
        Assert.Throws<KeyNotFoundException>(() => aggregate.Get(first));
        Assert.False(aggregate.TryGet(first, out Memo? removed));
        Assert.Null(removed);
    }

    [Fact]
    public void RemoveAndAppendCorrectionAppearsInOneCandidate() {
        MemoPodWorkingAggregate aggregate = CreateAggregate();
        MemoId oldId = aggregate.Append("order total is 10");

        aggregate.Remove(oldId);
        MemoId correctedId = aggregate.Append("order total is 12");

        MemoPodDocument candidate = aggregate.CaptureDocument();
        Memo corrected = Assert.Single(candidate.Memos);
        Assert.Equal(correctedId, corrected.Id);
        Assert.Equal("order total is 12", corrected.ExactText);
        Assert.NotEqual(oldId, correctedId);
    }

    [Fact]
    public void MissingRemoveAndDefaultIdsFailWithoutMutation() {
        MemoPodWorkingAggregate aggregate = CreateAggregate();
        MemoId existing = aggregate.Append("existing");
        MemoPodDocument before = aggregate.CaptureDocument();

        Assert.Throws<KeyNotFoundException>(() =>
            aggregate.Remove(MemoId.Parse("m1:00000002")));
        Assert.Throws<ArgumentException>(() => aggregate.Remove(default));
        Assert.Throws<ArgumentException>(() => aggregate.Get(default));
        Assert.Throws<ArgumentException>(() =>
            aggregate.TryGet(default, out _));

        MemoPodDocument after = aggregate.CaptureDocument();
        Assert.Equal(before.PodId, after.PodId);
        Assert.Equal(before.Topic, after.Topic);
        Assert.Equal(before.NextMemoOrdinal, after.NextMemoOrdinal);
        Assert.Equal(before.Memos.ToArray(), after.Memos.ToArray());
        Assert.Equal(existing, Assert.Single(after.Memos).Id);
    }

    [Fact]
    public void ActiveTextByteLimitFailureIsAtomic() {
        string maximumMemo = new(
            'x',
            MemoPodLimits.MaximumMemoExactTextUtf8Bytes
        );
        Memo[] memos = Enumerable.Range(1, 16)
            .Select(ordinal => new Memo(
                MemoId.FromOrdinal((uint)ordinal),
                maximumMemo
            ))
            .ToArray();
        var document = new MemoPodDocument(PodId, "topic", 17, memos);
        MemoPodWorkingAggregate aggregate =
            MemoPodWorkingAggregate.FromDocument(document);

        Assert.Throws<InvalidOperationException>(() => aggregate.Append("x"));
        Assert.Equal(17UL, aggregate.NextMemoOrdinal);
        Assert.Equal(16, aggregate.List().Length);

        aggregate.Remove(MemoId.FromOrdinal(1));
        Assert.Equal("m1:00000011", aggregate.Append("x").Value);
    }

    [Fact]
    public void ActiveCountLimitFailureIsAtomic() {
        Memo[] memos = Enumerable
            .Range(1, MemoPodLimits.MaximumActiveMemoCount)
            .Select(ordinal => new Memo(
                MemoId.FromOrdinal((uint)ordinal),
                "x"
            ))
            .ToArray();
        ulong next = (ulong)MemoPodLimits.MaximumActiveMemoCount + 1;
        var document = new MemoPodDocument(PodId, "topic", next, memos);
        MemoPodWorkingAggregate aggregate =
            MemoPodWorkingAggregate.FromDocument(document);

        Assert.Throws<InvalidOperationException>(() => aggregate.Append("x"));
        Assert.Equal(next, aggregate.NextMemoOrdinal);

        aggregate.Remove(MemoId.FromOrdinal(1));
        Assert.Equal("m1:00001001", aggregate.Append("x").Value);
    }

    [Fact]
    public void LastOrdinalAllocatesOnceThenRemainsExhaustedAfterRemove() {
        var document = new MemoPodDocument(
            PodId,
            "topic",
            uint.MaxValue,
            Array.Empty<Memo>()
        );
        MemoPodWorkingAggregate aggregate =
            MemoPodWorkingAggregate.FromDocument(document);

        MemoId last = aggregate.Append("last");

        Assert.Equal("m1:ffffffff", last.Value);
        Assert.Equal(
            MemoPodDocument.ExhaustedNextMemoOrdinal,
            aggregate.NextMemoOrdinal
        );
        Assert.Throws<InvalidOperationException>(() => aggregate.Append("no"));
        aggregate.Remove(last);
        Assert.Throws<InvalidOperationException>(() =>
            aggregate.Append("still no"));
        Assert.Equal(
            MemoPodDocument.ExhaustedNextMemoOrdinal,
            aggregate.NextMemoOrdinal
        );
    }

    [Fact]
    public void CapturedDocumentDoesNotAliasLaterWorkingChanges() {
        MemoPodWorkingAggregate aggregate = CreateAggregate();
        MemoId first = aggregate.Append("first");
        MemoPodDocument captured = aggregate.CaptureDocument();

        aggregate.Remove(first);
        aggregate.Append("second");

        Assert.Equal(first, Assert.Single(captured.Memos).Id);
        Assert.Equal(2UL, captured.NextMemoOrdinal);
    }

    private static MemoPodWorkingAggregate CreateAggregate()
        => MemoPodWorkingAggregate.CreateNew(PodId, "topic");
}
