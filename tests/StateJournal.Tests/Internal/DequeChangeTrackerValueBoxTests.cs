using System.Buffers;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

[Collection("ValueBox")]
public class DequeChangeTrackerValueBoxTests {
    private static int Bits64Count => ValuePools.OfBits64.Count;

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

            ValueBox removed = tracker.PopFront<ValueBoxHelper>();
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
    public void ApplyDelta_ReleasesTrimmedCommittedSlot_AndSyncDoesNotAllocateExtraSlots() {
        var tracker = new DequeChangeTracker<ValueBox>();
        ValueBox deltaTail = default;

        try {
            SeedCommitted(ref tracker, Heap(7), Heap(8), Heap(9));
            int countAfterCommit = Bits64Count;

            deltaTail = Heap(10);

            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BinaryDiffWriter(buffer);
            writer.WriteCount(1);
            writer.WriteCount(0);
            writer.WriteCount(0);
            writer.WriteCount(1);
            ValueBoxHelper.Write(writer, deltaTail, false);
            ValueBox.ReleaseBits64Slot(deltaTail);
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
                ValueBox.ReleaseBits64Slot(deltaTail);
            }
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void SetFront_MergingSingleDirtyPrefix_BackToCommitted_ReleasesBothExclusiveSlots() {
        var tracker = new DequeChangeTracker<ValueBox>();
        ValueBox incoming = default;

        try {
            SeedCommitted(ref tracker, Heap(11), Heap(22));
            int countAfterCommit = Bits64Count;

            tracker.PopFront<ValueBoxHelper>();
            tracker.PushFront<ValueBoxHelper>(Heap(33));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            incoming = Heap(11);
            Assert.Equal(countAfterCommit + 2, Bits64Count);

            tracker.SetFront<ValueBoxHelper>(incoming);
            incoming = default;

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(11), HeapValue(22));
            AssertDequeEqual(tracker.Committed, HeapValue(11), HeapValue(22));
        }
        finally {
            if (incoming.GetBits() != 0) {
                ValueBox.ReleaseBits64Slot(incoming);
            }
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void SetFront_MergingSingleDirtySuffix_BackToCommitted_ReleasesBothExclusiveSlots() {
        var tracker = new DequeChangeTracker<ValueBox>();
        ValueBox incoming = default;

        try {
            SeedCommitted(ref tracker, Heap(11));
            int countAfterCommit = Bits64Count;

            tracker.PopFront<ValueBoxHelper>();
            tracker.PushBack<ValueBoxHelper>(Heap(33));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            incoming = Heap(11);
            Assert.Equal(countAfterCommit + 2, Bits64Count);

            tracker.SetFront<ValueBoxHelper>(incoming);
            incoming = default;

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(11));
            AssertDequeEqual(tracker.Committed, HeapValue(11));
        }
        finally {
            if (incoming.GetBits() != 0) {
                ValueBox.ReleaseBits64Slot(incoming);
            }
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void SetBack_MergingSingleDirtyPrefix_BackToCommitted_ReleasesBothExclusiveSlots() {
        var tracker = new DequeChangeTracker<ValueBox>();
        ValueBox incoming = default;

        try {
            SeedCommitted(ref tracker, Heap(11));
            int countAfterCommit = Bits64Count;

            tracker.PopBack<ValueBoxHelper>();
            tracker.PushFront<ValueBoxHelper>(Heap(33));
            Assert.Equal(countAfterCommit + 1, Bits64Count);

            incoming = Heap(11);
            Assert.Equal(countAfterCommit + 2, Bits64Count);

            tracker.SetBack<ValueBoxHelper>(incoming);
            incoming = default;

            Assert.Equal(countAfterCommit, Bits64Count);
            Assert.False(tracker.HasChanges);
            AssertDequeEqual(tracker.Current, HeapValue(11));
            AssertDequeEqual(tracker.Committed, HeapValue(11));
        }
        finally {
            if (incoming.GetBits() != 0) {
                ValueBox.ReleaseBits64Slot(incoming);
            }
            Cleanup(ref tracker);
        }
    }

    private static void SeedCommitted(ref DequeChangeTracker<ValueBox> tracker, params ValueBox[] values) {
        foreach (ValueBox value in values) {
            tracker.PushBack<ValueBoxHelper>(value);
        }
        tracker.Commit<ValueBoxHelper>();
    }

    private static void Cleanup(ref DequeChangeTracker<ValueBox> tracker) {
        while (tracker.Current.Count > 0) {
            tracker.PopFront<ValueBoxHelper>();
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

    private static long HeapValue(long value) => value + long.MaxValue - 1024;

    private static ValueBox Heap(long value) => ValueBox.Int64Face.From(HeapValue(value));
}
