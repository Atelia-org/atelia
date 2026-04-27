using System.Buffers;
using Atelia.StateJournal.Serialization;
using Xunit;
using Atelia.StateJournal.Tests;

namespace Atelia.StateJournal.Internal.Tests;

[Collection("ValueBox")]
public class DequeChangeTrackerValueBoxTests {
    private static int Bits64Count => ValuePools.OfBits64.Count;

    private static void AssertEstimateMatchesSerializedBody(in DequeChangeTracker<ValueBox> tracker) {
        var rebaseTracker = tracker;
        EstimateAssert.EqualSerializedBodySize(
            rebaseTracker.EstimatedRebaseBytes<ValueBoxHelper>(),
            writer => rebaseTracker.WriteRebase<ValueBoxHelper>(writer, DiffWriteContext.UserPrimary)
        );

        var deltaTracker = tracker;
        EstimateAssert.EqualSerializedBodySize(
            deltaTracker.EstimatedDeltifyBytes<ValueBoxHelper>(),
            writer => deltaTracker.WriteDeltify<ValueBoxHelper>(writer, DiffWriteContext.UserPrimary)
        );
    }

    [Fact]
    public void Revert_ReleasesDirtyPrefixAndSuffixSlots_Symmetrically() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(101), Heap(202));
            int countAfterCommit = Bits64Count;

            tracker.PushFront<ValueBoxHelper>(Heap(303));
            tracker.PushBack<ValueBoxHelper>(Heap(404));

            Assert.Equal(countAfterCommit + 2, Bits64Count);

            tracker.Revert<ValueBoxHelper>();

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(101), HeapValue(202));
            AssertDequeEqual(tracker.Committed, HeapValue(101), HeapValue(202));
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Commit_ReleasesTrimmedCommittedSlot_AndRetainsFrozenIncomingSlot() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22), Heap(33));
            int countAfterCommit = Bits64Count;

            ValueBox removed = PopFront(ref tracker, out _);
            AssertBoxEquals(removed, HeapValue(11));
            Assert.Equal(countAfterCommit, Bits64Count);

            tracker.PushBack<ValueBoxHelper>(Heap(44));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            tracker.Commit<ValueBoxHelper>();

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(22), HeapValue(33), HeapValue(44));
            AssertDequeEqual(tracker.Committed, HeapValue(22), HeapValue(33), HeapValue(44));

            ValueBox committedTail = tracker.Committed[2];
            ValueBox currentTail = tracker.Current[2];
            Assert.Equal(ValueBox.Freeze(committedTail).GetBits(), committedTail.GetBits());
            Assert.Equal(committedTail.GetBits(), currentTail.GetBits());
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void PopFront_OnDirtyPrefix_TransfersOwnershipToCaller() {
        var tracker = new DequeChangeTracker<ValueBox>();
        ValueBox removed = default;

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22));
            int countAfterCommit = Bits64Count;

            tracker.PushFront<ValueBoxHelper>(ValueBox.UInt64Face.From(ulong.MaxValue));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            removed = PopFront(ref tracker, out bool callerOwned);

            Assert.True(callerOwned);
            Assert.Equal(countAfterCommit + 1, Bits64Count);
            Assert.Equal(GetIssue.None, ValueBox.UInt64Face.Get(removed, out ulong value));
            Assert.Equal(ulong.MaxValue, value);

            ValueBox.ReleaseOwnedHeapSlot(removed);
            removed = default;
            Assert.Equal(countAfterCommit, Bits64Count);
        }
        finally {
            if (removed.GetBits() != 0) {
                ValueBox.ReleaseOwnedHeapSlot(removed);
            }
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void PopFront_OnCleanKeep_BorrowsCommittedOwnership() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22));
            int countAfterCommit = Bits64Count;

            ValueBox removed = PopFront(ref tracker, out bool callerOwned);

            Assert.False(callerOwned);
            Assert.Equal(countAfterCommit, Bits64Count);
            AssertBoxEquals(removed, HeapValue(11));

            tracker.Commit<ValueBoxHelper>();

            Assert.Equal(countAfterCommit - 1, Bits64Count);
            AssertDequeEqual(tracker.Current, HeapValue(22));
            AssertDequeEqual(tracker.Committed, HeapValue(22));
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void GetRef_UpdateOrInit_NoChange_SkipsAfterSet_AndDoesNotAllocate() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22));
            int countAfterCommit = Bits64Count;

            ref ValueBox slot = ref tracker.GetRef(0);
            bool changed = ValueBox.Int64Face.UpdateOrInit(ref slot, HeapValue(11));

            Assert.False(changed);
            Assert.False(tracker.HasChanges);
            Assert.Equal(0, tracker.KeepDirtyCount);
            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.Equal(ValueBox.Freeze(slot).GetBits(), slot.GetBits());
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void ApplyDelta_ReleasesTrimmedCommittedSlot_AndSyncDoesNotAllocateExtraSlots() {
        var tracker = new DequeChangeTracker<ValueBox>();
        ValueBox deltaTail = default;

        try {
            SeedCommittedOnly(ref tracker, Heap(7), Heap(8), Heap(9));
            int countAfterCommit = Bits64Count;

            deltaTail = Heap(10);

            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BinaryDiffWriter(buffer);
            writer.WriteCount(1);
            writer.WriteCount(0);
            writer.WriteCount(0);
            writer.WriteCount(0);
            writer.WriteCount(1);
            ValueBoxHelper.Write(writer, deltaTail, false);
            ValueBox.ReleaseOwnedHeapSlot(deltaTail);
            deltaTail = default;

            var reader = new BinaryDiffReader(buffer.WrittenSpan);
            tracker.ApplyDelta<ValueBoxHelper>(ref reader);
            reader.EnsureFullyConsumed();

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.Equal(0, tracker.Current.Count);
            AssertDequeEqual(tracker.Committed, HeapValue(8), HeapValue(9), HeapValue(10));

            tracker.SyncCurrentFromCommitted<ValueBoxHelper>();

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(8), HeapValue(9), HeapValue(10));
        }
        finally {
            if (deltaTail.GetBits() != 0) {
                ValueBox.ReleaseOwnedHeapSlot(deltaTail);
            }
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void SetFront_OnDirtyPrefix_AbsorbingCommittedValue_ReleasesExclusiveSlot_AndClearsDirty() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22));
            int countAfterCommit = Bits64Count;

            PopFront(ref tracker, out _);
            tracker.PushFront<ValueBoxHelper>(Heap(33));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            Assert.True(UpdateFrontInt64(ref tracker, HeapValue(11)));

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(11), HeapValue(22));
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void SetFront_OnDirtySuffix_AbsorbingCommittedValue_ReleasesExclusiveSlot_AndClearsDirty() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11));
            int countAfterCommit = Bits64Count;

            PopFront(ref tracker, out _);
            tracker.PushBack<ValueBoxHelper>(Heap(33));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            Assert.True(UpdateFrontInt64(ref tracker, HeapValue(11)));

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(11));
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void SetBack_OnDirtyPrefix_AbsorbingCommittedValue_ReleasesExclusiveSlot_AndClearsDirty() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11));
            int countAfterCommit = Bits64Count;

            PopBack(ref tracker, out _);
            tracker.PushFront<ValueBoxHelper>(Heap(33));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            Assert.True(UpdateBackInt64(ref tracker, HeapValue(11)));

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(11));
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void SetFront_ThenSetBack_WithEmptyKeepAndBothDirtySides_ReleasesBothExclusiveSlots() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22));
            int countAfterCommit = Bits64Count;

            ValueBox removedFront = PopFront(ref tracker, out bool frontCallerOwned);
            if (frontCallerOwned) { ValueBox.ReleaseOwnedHeapSlot(removedFront); }

            ValueBox removedBack = PopBack(ref tracker, out bool backCallerOwned);
            if (backCallerOwned) { ValueBox.ReleaseOwnedHeapSlot(removedBack); }

            tracker.PushFront<ValueBoxHelper>(Heap(33));
            tracker.PushBack<ValueBoxHelper>(Heap(44));
            Assert.Equal(countAfterCommit + 2, Bits64Count);
            Assert.Equal(0, tracker.KeepCount);

            Assert.True(UpdateFrontInt64(ref tracker, HeapValue(11)));

            Assert.Equal(countAfterCommit + 1, Bits64Count);
            Assert.True(tracker.HasChanges);
            Assert.Equal(1, tracker.KeepCount);
            AssertDequeEqual(tracker.Current, HeapValue(11), HeapValue(44));

            Assert.True(UpdateBackInt64(ref tracker, HeapValue(22)));

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            Assert.Equal(2, tracker.KeepCount);
            AssertDequeEqual(tracker.Current, HeapValue(11), HeapValue(22));
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void EstimatedBytes_ValueBoxRemainInSync_AfterMutationAndLoad() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22), Heap(33));

            tracker.PushFront<ValueBoxHelper>(Heap(7));
            Assert.True(UpdateAtInt64(ref tracker, 2, HeapValue(44)));
            AssertEstimateMatchesSerializedBody(tracker);

            tracker.Revert<ValueBoxHelper>();
            AssertEstimateMatchesSerializedBody(tracker);

            var source = tracker;
            var target = new DequeChangeTracker<ValueBox>();
            try {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BinaryDiffWriter(buffer);
                source.WriteRebase<ValueBoxHelper>(writer, DiffWriteContext.UserPrimary);

                var reader = new BinaryDiffReader(buffer.WrittenSpan);
                target.ApplyDelta<ValueBoxHelper>(ref reader);
                reader.EnsureFullyConsumed();
                target.SyncCurrentFromCommitted<ValueBoxHelper>();

                AssertEstimateMatchesSerializedBody(target);
            }
            finally {
                Cleanup(ref target);
            }
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void SetBack_ThenSetFront_WithEmptyKeepAndBothDirtySides_ReleasesBothExclusiveSlots() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22));
            int countAfterCommit = Bits64Count;

            ValueBox removedFront = PopFront(ref tracker, out bool frontCallerOwned);
            if (frontCallerOwned) { ValueBox.ReleaseOwnedHeapSlot(removedFront); }

            ValueBox removedBack = PopBack(ref tracker, out bool backCallerOwned);
            if (backCallerOwned) { ValueBox.ReleaseOwnedHeapSlot(removedBack); }

            tracker.PushFront<ValueBoxHelper>(Heap(33));
            tracker.PushBack<ValueBoxHelper>(Heap(44));
            Assert.Equal(countAfterCommit + 2, Bits64Count);
            Assert.Equal(0, tracker.KeepCount);

            Assert.True(UpdateBackInt64(ref tracker, HeapValue(22)));

            Assert.Equal(countAfterCommit + 1, Bits64Count);
            Assert.True(tracker.HasChanges);
            Assert.Equal(1, tracker.KeepCount);
            AssertDequeEqual(tracker.Current, HeapValue(33), HeapValue(22));

            Assert.True(UpdateFrontInt64(ref tracker, HeapValue(11)));

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            Assert.Equal(2, tracker.KeepCount);
            AssertDequeEqual(tracker.Current, HeapValue(11), HeapValue(22));
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Revert_WithSparseKeepPatch_ReleasesExclusiveValueBoxSlot() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22), Heap(33));
            int countAfterCommit = Bits64Count;

            Assert.True(UpdateAtInt64(ref tracker, 1, HeapValue(222)));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            tracker.Revert<ValueBoxHelper>();

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(11), HeapValue(22), HeapValue(33));
            AssertDequeEqual(tracker.Committed, HeapValue(11), HeapValue(22), HeapValue(33));
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Commit_WithSparseKeepPatch_ReleasesReplacedCommittedSlot_AndSharesFrozenValue() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22), Heap(33));
            int countAfterCommit = Bits64Count;

            Assert.True(UpdateAtInt64(ref tracker, 1, HeapValue(222)));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            tracker.Commit<ValueBoxHelper>();

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(11), HeapValue(222), HeapValue(33));
            AssertDequeEqual(tracker.Committed, HeapValue(11), HeapValue(222), HeapValue(33));
            Assert.Equal(tracker.Current[1].GetBits(), tracker.Committed[1].GetBits());
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void PopFront_RemovingDirtyKeepValueBox_ReleasesRemovedSlot_AndShiftsRemainingPatch() {
        var tracker = new DequeChangeTracker<ValueBox>();

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22), Heap(33), Heap(44));
            int countAfterCommit = Bits64Count;

            Assert.True(UpdateAtInt64(ref tracker, 0, HeapValue(111)));
            Assert.True(UpdateAtInt64(ref tracker, 2, HeapValue(333)));
            Assert.Equal(countAfterCommit + 2, Bits64Count);

            ValueBox removedDirtyKeep = PopFront(ref tracker, out bool removedCallerOwned);
            if (removedCallerOwned) {
                ValueBox.ReleaseOwnedHeapSlot(removedDirtyKeep);
            }
            Assert.Equal(countAfterCommit + 1, Bits64Count);
            Assert.Equal(1, tracker.KeepDirtyCount);
            AssertDequeEqual(tracker.Current, HeapValue(22), HeapValue(333), HeapValue(44));

            tracker.Commit<ValueBoxHelper>();

            Assert.Equal(countAfterCommit - 1, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(22), HeapValue(333), HeapValue(44));
            AssertDequeEqual(tracker.Committed, HeapValue(22), HeapValue(333), HeapValue(44));
            Assert.Equal(tracker.Current[1].GetBits(), tracker.Committed[1].GetBits());
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    private static void SeedCommitted(ref DequeChangeTracker<ValueBox> tracker, params ValueBox[] values) {
        foreach (ValueBox value in values) {
            tracker.PushBack<ValueBoxHelper>(value);
        }
        tracker.Commit<ValueBoxHelper>();
    }

    private static void SeedCommittedOnly(ref DequeChangeTracker<ValueBox> tracker, params ValueBox[] values) {
        var source = new DequeChangeTracker<ValueBox>();
        try {
            SeedCommitted(ref source, values);

            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BinaryDiffWriter(buffer);
            source.WriteRebase<ValueBoxHelper>(writer, DiffWriteContext.UserPrimary);

            var reader = new BinaryDiffReader(buffer.WrittenSpan);
            tracker.ApplyDelta<ValueBoxHelper>(ref reader);
            reader.EnsureFullyConsumed();
            Assert.Equal(0, tracker.Current.Count);
        }
        finally {
            Cleanup(ref source);
        }
    }

    private static void Cleanup(ref DequeChangeTracker<ValueBox> tracker) {
        while (tracker.Current.Count > 0) {
            ValueBox removed = PopFront(ref tracker, out bool callerOwned);
            if (callerOwned) {
                ValueBox.ReleaseOwnedHeapSlot(removed);
            }
        }
        tracker.Commit<ValueBoxHelper>();
    }

    private static void AssertDequeEqual(IndexedDeque<ValueBox> deque, params long[] expected) {
        Assert.Equal(expected.Length, deque.Count);
        for (int i = 0; i < expected.Length; i++) {
            AssertBoxEquals(deque[i], expected[i]);
        }
    }

    private static void AssertBoxEquals(ValueBox actual, long expected) {
        GetIssue issue = ValueBox.Int64Face.Get(actual, out long value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, value);
    }

    private static ValueBox PopFront(ref DequeChangeTracker<ValueBox> tracker, out bool callerOwned) {
        Assert.True(tracker.TryPopFront<ValueBoxHelper>(out ValueBox value, out callerOwned));
        return value;
    }

    private static ValueBox PopBack(ref DequeChangeTracker<ValueBox> tracker, out bool callerOwned) {
        Assert.True(tracker.TryPopBack<ValueBoxHelper>(out ValueBox value, out callerOwned));
        return value;
    }

    private static bool UpdateFrontInt64(ref DequeChangeTracker<ValueBox> tracker, long value) => UpdateAtInt64(ref tracker, 0, value);

    private static bool UpdateBackInt64(ref DequeChangeTracker<ValueBox> tracker, long value) => UpdateAtInt64(ref tracker, tracker.Current.Count - 1, value);

    private static bool UpdateAtInt64(ref DequeChangeTracker<ValueBox> tracker, int index, long value) =>
        UpdateAt<long, ValueBox.Int64Face>(ref tracker, index, value);

    private static bool UpdateAt<TValue, TFace>(ref DequeChangeTracker<ValueBox> tracker, int index, TValue value)
        where TValue : notnull
        where TFace : ValueBox.ITypedFace<TValue> {
        ref ValueBox slot = ref tracker.GetRef(index);
        ValueBox oldValue = slot;
        if (!TFace.UpdateOrInit(ref slot, value)) { return false; }

        tracker.AfterSet<ValueBoxHelper>(index, ref slot, oldValue);
        return true;
    }

    private static long HeapValue(long value) => value + long.MaxValue - 1024;

    private static ValueBox Heap(long value) => ValueBox.Int64Face.From(HeapValue(value));
}
