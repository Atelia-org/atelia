using Atelia.SessionJournal.MemoPod;

namespace Atelia.SessionJournal.MemoPod.Tests;

public sealed class MemoTextContractTests {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "11111111111111111111111111111111"
    );

    [Fact]
    public void TopicUsesStrictUtf8ByteBound() {
        string exactBoundary = new string('界', 1_365) + "a";
        string overBoundary = exactBoundary + "b";

        MemoPodWorkingAggregate aggregate =
            MemoPodWorkingAggregate.CreateNew(PodId, exactBoundary);

        Assert.Equal(exactBoundary, aggregate.Topic);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MemoPodWorkingAggregate.CreateNew(PodId, overBoundary));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("line\nbreak")]
    [InlineData("control\0character")]
    public void TopicRejectsBlankUntrimmedOrControlledText(string topic) {
        Assert.Throws<ArgumentException>(() =>
            MemoPodWorkingAggregate.CreateNew(PodId, topic));
    }

    [Fact]
    public void TopicRejectsInvalidUtf16() {
        Assert.Throws<ArgumentException>(() =>
            MemoPodWorkingAggregate.CreateNew(PodId, "bad\ud800text"));
    }

    [Fact]
    public void MemoExactTextPreservesWhitespaceNewlinesAndControls() {
        const string exactText = "  exact\r\ntext\0  ";
        MemoPodWorkingAggregate aggregate = CreateAggregate();

        MemoId id = aggregate.Append(exactText);

        Assert.Equal(exactText, aggregate.Get(id).ExactText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void MemoExactTextRejectsBlankTextWithoutAllocatingId(
        string exactText
    ) {
        MemoPodWorkingAggregate aggregate = CreateAggregate();

        Assert.Throws<ArgumentException>(() => aggregate.Append(exactText));

        Assert.Equal(1UL, aggregate.NextMemoOrdinal);
        Assert.Empty(aggregate.List());
        Assert.Equal("m1:00000001", aggregate.Append("valid").Value);
    }

    [Fact]
    public void MemoExactTextUsesStrictUtf8AndByteBound() {
        MemoPodWorkingAggregate aggregate = CreateAggregate();
        string exactBoundary = new(
            'x',
            MemoPodLimits.MaximumMemoExactTextUtf8Bytes
        );

        MemoId id = aggregate.Append(exactBoundary);

        Assert.Equal(exactBoundary, aggregate.Get(id).ExactText);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            aggregate.Append(exactBoundary + "x"));
        Assert.Throws<ArgumentException>(() =>
            aggregate.Append("bad\ud800text"));
        Assert.Equal(2UL, aggregate.NextMemoOrdinal);
    }

    [Fact]
    public void MemoMetadataRoundTripsAsNullableImmutableFields() {
        MemoPodWorkingAggregate aggregate = CreateAggregate();

        MemoId withoutMetadata = aggregate.Append("plain memo");
        MemoId withMetadata = aggregate.Append(
            "order 17 ships Friday",
            title: "Order 17",
            gist: "Ships Friday",
            summary: "The customer expects order 17 to ship on Friday."
        );

        Memo plain = aggregate.Get(withoutMetadata);
        Assert.Null(plain.Title);
        Assert.Null(plain.Gist);
        Assert.Null(plain.Summary);

        Memo annotated = aggregate.Get(withMetadata);
        Assert.Equal("Order 17", annotated.Title);
        Assert.Equal("Ships Friday", annotated.Gist);
        Assert.Equal(
            "The customer expects order 17 to ship on Friday.",
            annotated.Summary
        );
        Assert.Equal("order 17 ships Friday", annotated.ExactText);
    }

    [Fact]
    public void MemoMetadataRejectsBlankUntrimmedControlledOrInvalidUtf16() {
        MemoPodWorkingAggregate aggregate = CreateAggregate();

        Assert.Throws<ArgumentException>(() =>
            aggregate.Append("memo", title: string.Empty));
        Assert.Throws<ArgumentException>(() =>
            aggregate.Append("memo", title: " leading"));
        Assert.Throws<ArgumentException>(() =>
            aggregate.Append("memo", gist: "trailing "));
        Assert.Throws<ArgumentException>(() =>
            aggregate.Append("memo", summary: "line\nbreak"));
        Assert.Throws<ArgumentException>(() =>
            aggregate.Append("memo", summary: "bad\ud800text"));

        Assert.Equal(1UL, aggregate.NextMemoOrdinal);
        Assert.Empty(aggregate.List());
    }

    [Fact]
    public void MemoMetadataUsesStrictUtf8FieldAndAggregateBounds() {
        MemoPodWorkingAggregate fieldAggregate = CreateAggregate();
        string titleBoundary = new(
            'x',
            MemoPodLimits.MaximumMemoTitleUtf8Bytes
        );
        string gistBoundary = new(
            'x',
            MemoPodLimits.MaximumMemoGistUtf8Bytes
        );
        string summaryBoundary = new(
            'x',
            MemoPodLimits.MaximumMemoSummaryUtf8Bytes
        );

        MemoId id = fieldAggregate.Append(
            "memo",
            titleBoundary,
            gistBoundary,
            summaryBoundary
        );

        Memo memo = fieldAggregate.Get(id);
        Assert.Equal(titleBoundary, memo.Title);
        Assert.Equal(gistBoundary, memo.Gist);
        Assert.Equal(summaryBoundary, memo.Summary);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fieldAggregate.Append("memo", title: titleBoundary + "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fieldAggregate.Append("memo", gist: gistBoundary + "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fieldAggregate.Append("memo", summary: summaryBoundary + "x"));

        MemoPodWorkingAggregate aggregateAggregate = CreateAggregate();
        string chunk = new('x', MemoPodLimits.MaximumMemoTitleUtf8Bytes);
        int acceptedCount =
            MemoPodLimits.MaximumActiveMemoMetadataUtf8Bytes
            / MemoPodLimits.MaximumMemoTitleUtf8Bytes;
        for (int index = 0; index < acceptedCount; index++) {
            aggregateAggregate.Append("memo", title: chunk);
        }

        Assert.Throws<InvalidOperationException>(() =>
            aggregateAggregate.Append("memo", title: "x"));
        Assert.Equal((ulong)acceptedCount + 1, aggregateAggregate.NextMemoOrdinal);
    }

    private static MemoPodWorkingAggregate CreateAggregate()
        => MemoPodWorkingAggregate.CreateNew(PodId, "customer details");
}
