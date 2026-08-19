using Atelia.SessionJournal.MemoPod;

namespace Atelia.SessionJournal.MemoPod.Tests;

public sealed class MemoPodDocumentTests {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "33333333333333333333333333333333"
    );

    [Fact]
    public void EmptyDocumentAndAscendingGapsAreValid() {
        var empty = new MemoPodDocument(
            PodId,
            "topic",
            1,
            Array.Empty<Memo>()
        );
        Memo[] withGap = [
            new Memo(MemoId.FromOrdinal(1), "one"),
            new Memo(MemoId.FromOrdinal(3), "three")
        ];
        var gapped = new MemoPodDocument(PodId, "topic", 4, withGap);

        Assert.Empty(empty.Memos);
        Assert.Equal(1UL, empty.NextMemoOrdinal);
        Assert.Equal(
            ["m1:00000001", "m1:00000003"],
            gapped.Memos.Select(static memo => memo.Id.Value).ToArray()
        );
    }

    [Fact]
    public void DocumentDefensivelyMaterializesMemoSequence() {
        var source = new List<Memo> {
            new(MemoId.FromOrdinal(1), "one")
        };
        var document = new MemoPodDocument(PodId, "topic", 2, source);

        source.Clear();
        source.Add(new Memo(MemoId.FromOrdinal(9), "nine"));

        Memo memo = Assert.Single(document.Memos);
        Assert.Equal("m1:00000001", memo.Id.Value);
        Assert.Equal("one", memo.ExactText);
    }

    [Fact]
    public void DocumentRejectsInvalidIdentityTopicAndHighWater() {
        Assert.Throws<ArgumentException>(() => new MemoPodDocument(
            default,
            "topic",
            1,
            Array.Empty<Memo>()
        ));
        Assert.Throws<ArgumentException>(() => new MemoPodDocument(
            PodId,
            " topic",
            1,
            Array.Empty<Memo>()
        ));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoPodDocument(
            PodId,
            "topic",
            0,
            Array.Empty<Memo>()
        ));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoPodDocument(
            PodId,
            "topic",
            MemoPodDocument.ExhaustedNextMemoOrdinal + 1,
            Array.Empty<Memo>()
        ));
    }

    [Fact]
    public void DocumentRejectsUnorderedDuplicateOrUncommittedIds() {
        Memo first = new(MemoId.FromOrdinal(1), "one");
        Memo second = new(MemoId.FromOrdinal(2), "two");

        Assert.Throws<ArgumentException>(() => new MemoPodDocument(
            PodId,
            "topic",
            3,
            [second, first]
        ));
        Assert.Throws<ArgumentException>(() => new MemoPodDocument(
            PodId,
            "topic",
            2,
            [first, first]
        ));
        Assert.Throws<ArgumentException>(() => new MemoPodDocument(
            PodId,
            "topic",
            2,
            [second]
        ));
    }

    [Fact]
    public void DocumentRejectsNullAndTooManyMemos() {
        Assert.Throws<ArgumentNullException>(() => new MemoPodDocument(
            PodId,
            "topic",
            1,
            null!
        ));
        Assert.Throws<ArgumentException>(() => new MemoPodDocument(
            PodId,
            "topic",
            2,
            [null!]
        ));

        Memo[] tooMany = Enumerable
            .Range(1, MemoPodLimits.MaximumActiveMemoCount + 1)
            .Select(ordinal => new Memo(
                MemoId.FromOrdinal((uint)ordinal),
                "x"
            ))
            .ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoPodDocument(
                PodId,
                "topic",
                (ulong)tooMany.Length + 1,
                tooMany
            ));
    }

    [Fact]
    public void DocumentRejectsExcessiveActiveExactTextBytes() {
        string maximumMemo = new(
            'x',
            MemoPodLimits.MaximumMemoExactTextUtf8Bytes
        );
        Memo[] overByteLimit = Enumerable.Range(1, 17)
            .Select(ordinal => new Memo(
                MemoId.FromOrdinal((uint)ordinal),
                ordinal == 17 ? "x" : maximumMemo
            ))
            .ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoPodDocument(PodId, "topic", 18, overByteLimit));
    }

    [Fact]
    public void MemoRejectsDefaultId() {
        Assert.Throws<ArgumentException>(() => new Memo(default, "text"));
    }
}
