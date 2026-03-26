using Xunit;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Tests;

public class DurableDequeApiTests {
    [Fact]
    public void TypedDeque_Int_SupportsPushPeekSetAndPop() {
        var deque = Durable.Deque<int>();

        deque.PushBack(2);
        deque.PushFront(1);
        deque.PushBack(3);

        Assert.Equal(3, deque.Count);
        Assert.True(deque.TryPeekFront(out int front));
        Assert.True(deque.TryPeekBack(out int back));
        Assert.Equal(1, front);
        Assert.Equal(3, back);

        deque.SetFrontOrThrow(10);
        deque.SetBackOrThrow(30);
        Assert.True(deque.TrySetAt(1, 20));
        Assert.Equal(GetIssue.None, deque.GetAt(0, out int v0));
        Assert.Equal(GetIssue.None, deque.GetAt(1, out int v1));
        Assert.Equal(GetIssue.None, deque.GetAt(2, out int v2));
        Assert.Equal(10, v0);
        Assert.Equal(20, v1);
        Assert.Equal(30, v2);
        Assert.Equal(10, deque.GetAt(0));
        Assert.Equal(20, deque.GetAt(1));
        Assert.Equal(30, deque.GetAt(2));
        Assert.Equal(10, deque.GetFrontOrThrow());
        Assert.Equal(30, deque.GetBackOrThrow());

        Assert.True(deque.TryPopFront(out int poppedFront));
        Assert.True(deque.TryPopBack(out int poppedBack));
        Assert.Equal(10, poppedFront);
        Assert.Equal(30, poppedBack);
        Assert.Equal(1, deque.Count);
        Assert.Equal(20, deque.GetFrontOrThrow());
    }

    [Fact]
    public void MixedDeque_SupportsGenericAndTypedViews() {
        var rev = new Revision(1);
        var deque = rev.CreateDeque();

        deque.PushBack("tail");
        deque.PushFront(42);

        Assert.Equal(2, deque.Count);
        Assert.True(deque.TryPeekFront(out int front));
        Assert.Equal(42, front);
        Assert.True(deque.TryPeekBack(out string? back));
        Assert.Equal("tail", back);

        Assert.True(deque.OfInt32.TrySetFront(7));
        Assert.True(deque.OfString.TrySetBack("new-tail"));
        Assert.True(deque.TrySetAt<int>(0, 9));
        Assert.True(deque.OfString.TrySetAt(1, "tail-2"));

        Assert.Equal(GetIssue.None, deque.GetAt<int>(0, out int typedFront));
        Assert.Equal(9, typedFront);
        Assert.Equal(GetIssue.None, deque.OfString.GetAt(1, out string? typedBack));
        Assert.Equal("tail-2", typedBack);
        Assert.Equal(9, deque.GetAt<int>(0));
        Assert.Equal("tail-2", deque.OfString.GetAt(1));

        Assert.True(deque.TryPeekFrontValueKind(out var frontKind));
        Assert.True(deque.TryPeekBackValueKind(out var backKind));
        Assert.Equal(ValueKind.NonnegativeInteger, frontKind);
        Assert.Equal(ValueKind.String, backKind);

        Assert.True(deque.TryPopFront(out int poppedFront));
        Assert.True(deque.TryPopBack(out string? poppedBack));
        Assert.Equal(9, poppedFront);
        Assert.Equal("tail-2", poppedBack);
    }

    [Fact]
    public void MixedDeque_PopFront_HeapAllocatedUInt64_ReturnsValueCorrectly() {
        var deque = Durable.Deque();

        deque.PushBack(1UL);
        deque.PushFront(ulong.MaxValue);

        Assert.True(deque.TryPeekFront(out ulong front));
        Assert.Equal(ulong.MaxValue, front);

        Assert.True(deque.TryPopFront(out ulong popped));
        Assert.Equal(ulong.MaxValue, popped);
        Assert.Equal(1, deque.Count);
        Assert.Equal(1UL, deque.OfUInt64.GetFrontOrThrow());
    }

    [Fact]
    public void MixedDeque_ExactDoubleEdgeSetters_UseTryStyle() {
        var deque = Durable.Deque();

        Assert.False(deque.TrySetFrontExactDouble(1.5));
        Assert.False(deque.TrySetBackExactDouble(2.5));

        deque.PushBack(0.0);
        Assert.True(deque.TrySetFrontExactDouble(double.MaxValue));
        Assert.True(deque.TryPeekFront(out double front));
        Assert.Equal(double.MaxValue, front);

        deque.PushBack(1.0);
        Assert.True(deque.TrySetBackExactDouble(double.MinValue));
        Assert.True(deque.TryPeekBack(out double back));
        Assert.Equal(double.MinValue, back);
    }

    [Fact]
    public void TypedDeque_GetAtAndTrySetAt_OutOfRange_ReturnsIssue_AndThrowingConvenienceStillThrows() {
        var deque = Durable.Deque<int>();
        deque.PushBack(1);

        Assert.Equal(GetIssue.OutOfRange, deque.GetAt(1, out int _));
        Assert.False(deque.TryGetAt(1, out int _));
        Assert.Throws<ArgumentOutOfRangeException>(() => deque.GetAt(1));
        Assert.False(deque.TrySetAt(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => deque.SetAtOrThrow(1, 2));
    }

    [Fact]
    public void TypedDeque_TrySetFrontAndTrySetBack_OnEmptyDeque_ReturnFalse_AndThrowingApisStillThrow() {
        var deque = Durable.Deque<int>();

        Assert.False(deque.TrySetFront(1));
        Assert.False(deque.TrySetBack(1));
        Assert.Throws<InvalidOperationException>(() => deque.SetFrontOrThrow(1));
        Assert.Throws<InvalidOperationException>(() => deque.SetBackOrThrow(1));
    }

    [Fact]
    public void TypedDeque_TrySetFrontAndTrySetBack_OnNonEmptyDeque_UpdateEdges() {
        var deque = Durable.Deque<int>();
        deque.PushBack(1);
        deque.PushBack(2);

        Assert.True(deque.TrySetFront(10));
        Assert.True(deque.TrySetBack(20));
        Assert.Equal(10, deque.GetFrontOrThrow());
        Assert.Equal(20, deque.GetBackOrThrow());
    }

    [Fact]
    public void TypedDeque_InterfaceView_AlsoExposesCount() {
        IDeque<int> deque = Durable.Deque<int>();
        deque.PushBack(1);
        deque.PushBack(2);

        Assert.Equal(2, deque.Count);
    }

    [Fact]
    public void MixedDeque_TypedFront_TypeMismatch_DoesNotPretendDequeIsEmpty() {
        var rev = new Revision(1);
        var deque = rev.CreateDeque();
        deque.PushFront("title");

        Assert.Equal(GetIssue.TypeMismatch, deque.OfInt32.PeekFront(out int _));
        Assert.Throws<InvalidCastException>(() => _ = deque.OfInt32.GetFrontOrThrow());
    }

    [Fact]
    public void MixedDeque_TypedBack_TypeMismatch_DoesNotPretendDequeIsEmpty() {
        var rev = new Revision(1);
        var deque = rev.CreateDeque();
        deque.PushBack("tail");

        Assert.Equal(GetIssue.TypeMismatch, deque.OfInt32.PeekBack(out int _));
        Assert.Throws<InvalidCastException>(() => _ = deque.OfInt32.GetBackOrThrow());
    }

    [Fact]
    public void MixedDeque_DurableSubtypeAccess_AllowsNullValue() {
        var deque = Durable.Deque();
        deque.PushBack((DurableObject?)null);

        Assert.Equal(GetIssue.None, deque.PeekFront<DurableDict<int, int>>(out var front));
        Assert.Null(front);

        Assert.Equal(GetIssue.None, deque.GetAt<DurableDict<int, int>>(0, out var at0));
        Assert.Null(at0);

        Assert.Equal(GetIssue.None, deque.PopFront<DurableDict<int, int>>(out var popped));
        Assert.Null(popped);
        Assert.Equal(0, deque.Count);
    }

    [Fact]
    public void MixedDeque_DurableSubtype_GenericConvenienceApis_WorkWithoutOfView() {
        var rev = new Revision(1);
        var deque = rev.CreateDeque();
        var child = rev.CreateDict<int, int>();
        child.Upsert(7, 70);

        deque.PushBack(child);

        Assert.True(deque.TryGetAt<DurableDict<int, int>>(0, out var at0));
        Assert.Same(child, at0);

        Assert.True(deque.TryPeekFront<DurableDict<int, int>>(out var front));
        Assert.Same(child, front);
        Assert.Same(child, deque.GetFront<DurableDict<int, int>>());
        Assert.Same(child, deque.GetBack<DurableDict<int, int>>());

        Assert.True(deque.TryPopBack<DurableDict<int, int>>(out var popped));
        Assert.Same(child, popped);
        Assert.Equal(0, deque.Count);
    }

    [Fact]
    public void MixedDeque_OfDurableSubtype_RemainsUnsupported_ToKeepExactViewSemantics() {
        var deque = Durable.Deque();

        Assert.Throws<NotSupportedException>(() => deque.Of<DurableDict<int, int>>());
    }
}
