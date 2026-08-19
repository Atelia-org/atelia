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

    private static MemoPodWorkingAggregate CreateAggregate()
        => MemoPodWorkingAggregate.CreateNew(PodId, "customer details");
}
