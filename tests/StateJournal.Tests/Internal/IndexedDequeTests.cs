using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class IndexedDequeTests {
    [Fact]
    public void PushAndPop_BothEnds_MaintainsExpectedOrder() {
        var deque = new IndexedDeque<int>(capacity: 2);

        deque.PushBack(2);   // [2]
        deque.PushFront(1);  // [1,2]
        deque.PushBack(3);   // [1,2,3] (grow)
        deque.PushFront(0);  // [0,1,2,3]

        Assert.Equal(4, deque.Count);
        Assert.Equal(0, deque[0]);
        Assert.Equal(1, deque[1]);
        Assert.Equal(2, deque[2]);
        Assert.Equal(3, deque[3]);

        Assert.Equal(0, deque.PopFront()); // [1,2,3]
        Assert.Equal(3, deque.PopBack());  // [1,2]
        Assert.Equal(1, deque.PopFront()); // [2]
        Assert.Equal(2, deque.PopBack());  // []
        Assert.Equal(0, deque.Count);
    }

    [Fact]
    public void GrowthAfterWrapAround_PreservesLogicalSequence() {
        var deque = new IndexedDeque<int>(capacity: 4);

        deque.PushBack(1);
        deque.PushBack(2);
        deque.PushBack(3);
        deque.PushBack(4);
        Assert.Equal(1, deque.PopFront());
        Assert.Equal(2, deque.PopFront());

        deque.PushBack(5);
        deque.PushBack(6); // wrapped: logical [3,4,5,6]
        deque.PushBack(7); // grow: logical [3,4,5,6,7]

        Assert.Equal(5, deque.Count);
        Assert.Equal(3, deque[0]);
        Assert.Equal(4, deque[1]);
        Assert.Equal(5, deque[2]);
        Assert.Equal(6, deque[3]);
        Assert.Equal(7, deque[4]);
    }

    [Fact]
    public void Indexer_CanReadAndWrite_AfterWrapAround() {
        var deque = new IndexedDeque<string>(capacity: 4);

        deque.PushBack("A");
        deque.PushBack("B");
        deque.PushBack("C");
        Assert.Equal("A", deque.PopFront()); // [B,C]
        deque.PushBack("D");                 // [B,C,D]
        deque.PushBack("E");                 // wrap/grow as needed

        deque[1] = "X"; // [B,X,D,E]
        deque[3] = "Y"; // [B,X,D,Y]

        Assert.Equal("B", deque[0]);
        Assert.Equal("X", deque[1]);
        Assert.Equal("D", deque[2]);
        Assert.Equal("Y", deque[3]);
    }

    [Fact]
    public void Clear_ResetsCount_AndDequeCanBeReused() {
        var deque = new IndexedDeque<int>();

        deque.PushBack(10);
        deque.PushBack(20);
        deque.PushFront(5);
        deque.Clear();

        Assert.Equal(0, deque.Count);
        Assert.Throws<InvalidOperationException>(() => deque.PopFront());
        Assert.Throws<InvalidOperationException>(() => deque.PopBack());
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = deque[0]);

        deque.PushBack(1);
        deque.PushFront(0);
        deque.PushBack(2);

        Assert.Equal(3, deque.Count);
        Assert.Equal(0, deque[0]);
        Assert.Equal(1, deque[1]);
        Assert.Equal(2, deque[2]);
    }

    [Fact]
    public void TrimFront_RemovesElementsFromFront() {
        var deque = new IndexedDeque<int>();
        for (int i = 1; i <= 5; i++) { deque.PushBack(i); }

        deque.TrimFront(2);

        Assert.Equal(3, deque.Count);
        Assert.Equal(3, deque[0]);
        Assert.Equal(4, deque[1]);
        Assert.Equal(5, deque[2]);
    }

    [Fact]
    public void TrimBack_RemovesElementsFromBack() {
        var deque = new IndexedDeque<int>();
        for (int i = 1; i <= 5; i++) { deque.PushBack(i); }

        deque.TrimBack(2);

        Assert.Equal(3, deque.Count);
        Assert.Equal(1, deque[0]);
        Assert.Equal(2, deque[1]);
        Assert.Equal(3, deque[2]);
    }

    [Fact]
    public void TrimFront_AfterWrapAround_PreservesLogicalSequence() {
        var deque = new IndexedDeque<int>(capacity: 4);
        deque.PushBack(1);
        deque.PushBack(2);
        deque.PushBack(3);
        deque.PushBack(4);
        deque.PopFront(); // remove 1
        deque.PopFront(); // remove 2 → [3,4]
        deque.PushBack(5);
        deque.PushBack(6); // wrapped: [3,4,5,6]

        deque.TrimFront(1);

        Assert.Equal(3, deque.Count);
        Assert.Equal(4, deque[0]);
        Assert.Equal(5, deque[1]);
        Assert.Equal(6, deque[2]);
    }

    [Fact]
    public void TrimBack_AfterWrapAround_PreservesLogicalSequence() {
        var deque = new IndexedDeque<int>(capacity: 4);
        deque.PushBack(1);
        deque.PushBack(2);
        deque.PushBack(3);
        deque.PushBack(4);
        deque.PopFront(); // remove 1
        deque.PopFront(); // remove 2 → [3,4]
        deque.PushBack(5);
        deque.PushBack(6); // wrapped: [3,4,5,6]

        deque.TrimBack(1);

        Assert.Equal(3, deque.Count);
        Assert.Equal(3, deque[0]);
        Assert.Equal(4, deque[1]);
        Assert.Equal(5, deque[2]);
    }

    [Fact]
    public void TrimFront_AllElements_ResetsToEmpty() {
        var deque = new IndexedDeque<int>();
        deque.PushBack(1);
        deque.PushBack(2);
        deque.PushBack(3);

        deque.TrimFront(3);

        Assert.Equal(0, deque.Count);
    }

    [Fact]
    public void TrimBack_AllElements_ResetsToEmpty() {
        var deque = new IndexedDeque<int>();
        deque.PushBack(1);
        deque.PushBack(2);
        deque.PushBack(3);

        deque.TrimBack(3);

        Assert.Equal(0, deque.Count);
    }

    [Fact]
    public void TrimFront_Zero_IsNoOp() {
        var deque = new IndexedDeque<int>();
        deque.PushBack(1);
        deque.PushBack(2);

        deque.TrimFront(0);

        Assert.Equal(2, deque.Count);
        Assert.Equal(1, deque[0]);
        Assert.Equal(2, deque[1]);
    }

    [Fact]
    public void TrimFront_ExceedsCount_Throws() {
        var deque = new IndexedDeque<int>();
        deque.PushBack(1);
        deque.PushBack(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => deque.TrimFront(3));
    }

    [Fact]
    public void GetSegments_OnContiguousDeque_ReturnsSingleSegment() {
        var deque = new IndexedDeque<int>(capacity: 4);
        deque.PushBack(1);
        deque.PushBack(2);
        deque.PushBack(3);

        deque.GetSegments(out int firstStartIndex, out var first, out int secondStartIndex, out var second);

        Assert.Equal(0, firstStartIndex);
        Assert.Equal([1, 2, 3], first.ToArray());
        Assert.Equal(3, secondStartIndex);
        Assert.Empty(second.ToArray());
    }

    [Fact]
    public void GetSegments_OnWrappedDeque_ReturnsTwoSegmentsInLogicalOrder() {
        var deque = new IndexedDeque<int>(capacity: 4);
        deque.PushBack(1);
        deque.PushBack(2);
        deque.PushBack(3);
        deque.PushBack(4);
        Assert.Equal(1, deque.PopFront());
        Assert.Equal(2, deque.PopFront());
        deque.PushBack(5);
        deque.PushBack(6); // logical [3,4,5,6], wrapped in backing buffer

        deque.GetSegments(out int firstStartIndex, out var first, out int secondStartIndex, out var second);

        Assert.Equal(0, firstStartIndex);
        Assert.Equal([3, 4], first.ToArray());
        Assert.Equal(2, secondStartIndex);
        Assert.Equal([5, 6], second.ToArray());
    }

    [Fact]
    public void GetSegments_ForWrappedSubRange_SplitsAtBufferBoundary() {
        var deque = new IndexedDeque<int>(capacity: 4);
        deque.PushBack(1);
        deque.PushBack(2);
        deque.PushBack(3);
        deque.PushBack(4);
        Assert.Equal(1, deque.PopFront());
        Assert.Equal(2, deque.PopFront());
        deque.PushBack(5);
        deque.PushBack(6); // logical [3,4,5,6]

        deque.GetSegments(index: 1, count: 3, out int firstStartIndex, out var first, out int secondStartIndex, out var second);

        Assert.Equal(1, firstStartIndex);
        Assert.Equal([4], first.ToArray());
        Assert.Equal(2, secondStartIndex);
        Assert.Equal([5, 6], second.ToArray());
    }

    [Fact]
    public void GetSegments_WithEmptyRange_ReturnsEmptySegments() {
        var deque = new IndexedDeque<int>();
        deque.PushBack(1);
        deque.PushBack(2);

        deque.GetSegments(index: 1, count: 0, out int firstStartIndex, out var first, out int secondStartIndex, out var second);

        Assert.Equal(1, firstStartIndex);
        Assert.Empty(first.ToArray());
        Assert.Equal(1, secondStartIndex);
        Assert.Empty(second.ToArray());
    }

    [Fact]
    public void GetSegments_WithInvalidRange_Throws() {
        var deque = new IndexedDeque<int>();
        deque.PushBack(1);
        deque.PushBack(2);

        Assert.Throws<ArgumentOutOfRangeException>(Act);
        return;

        void Act() => deque.GetSegments(index: 1, count: 2, out _, out _, out _, out _);
    }

    [Fact]
    public void ReserveBack_OnWrappedDeque_AppendsInLogicalOrder() {
        var deque = new IndexedDeque<int>(capacity: 4);
        deque.PushBack(1);
        deque.PushBack(2);
        deque.PushBack(3);
        deque.PushBack(4);
        Assert.Equal(1, deque.PopFront());
        Assert.Equal(2, deque.PopFront());

        deque.ReserveBack(3, out var first, out var second);
        FillSequential(first, second, [5, 6, 7]);

        Assert.Equal([3, 4, 5, 6, 7], Collect(deque));
    }

    [Fact]
    public void ReserveFront_OnWrappedDeque_PrependsInLogicalOrder() {
        var deque = new IndexedDeque<int>(capacity: 4);
        deque.PushBack(3);
        deque.PushBack(4);
        deque.PushBack(5);
        deque.PushBack(6);
        Assert.Equal(6, deque.PopBack());
        Assert.Equal(5, deque.PopBack());
        deque.PushFront(2);

        deque.ReserveFront(2, out var first, out var second);
        FillSequential(first, second, [0, 1]);

        Assert.Equal([0, 1, 2, 3, 4], Collect(deque));
    }

    [Fact]
    public void ReserveFrontAndBack_Zero_DoNotChangeDeque() {
        var deque = new IndexedDeque<int>();
        deque.PushBack(1);
        deque.PushBack(2);

        deque.ReserveFront(0, out var frontFirst, out var frontSecond);
        deque.ReserveBack(0, out var backFirst, out var backSecond);

        Assert.Empty(frontFirst.ToArray());
        Assert.Empty(frontSecond.ToArray());
        Assert.Empty(backFirst.ToArray());
        Assert.Empty(backSecond.ToArray());
        Assert.Equal([1, 2], Collect(deque));
    }

    private static void FillSequential(Span<int> first, Span<int> second, int[] values) {
        int index = 0;
        foreach (ref var slot in first) {
            slot = values[index++];
        }
        foreach (ref var slot in second) {
            slot = values[index++];
        }
        Assert.Equal(values.Length, index);
    }

    private static int[] Collect(IndexedDeque<int> deque) {
        var result = new int[deque.Count];
        for (int i = 0; i < deque.Count; ++i) {
            result[i] = deque[i];
        }
        return result;
    }
}
