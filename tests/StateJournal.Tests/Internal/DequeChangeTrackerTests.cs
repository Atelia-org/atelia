using System.Buffers;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class DequeChangeTrackerTests {
    [Fact]
    public void PushAndPop_AtBothEnds_TracksPrefixSuffixAndKeepWindow() {
        var tracker = new DequeChangeTracker<int>();

        tracker.PushBack<Int32Helper>(1);
        tracker.PushBack<Int32Helper>(2);
        tracker.Commit<Int32Helper>();

        tracker.PushFront<Int32Helper>(0);
        tracker.PushBack<Int32Helper>(3);

        Assert.True(tracker.HasChanges);
        Assert.Equal(0, tracker.TrimFrontCount);
        Assert.Equal(0, tracker.TrimBackCount);
        Assert.Equal(1, tracker.PushFrontCount);
        Assert.Equal(1, tracker.PushBackCount);
        Assert.Equal(2, tracker.KeepCount);
        Assert.Equal([0, 1, 2, 3], Collect(tracker.Current));

        Assert.Equal(0, tracker.PopFront<Int32Helper>());
        Assert.Equal(0, tracker.PushFrontCount);
        Assert.Equal(1, tracker.PushBackCount);
        Assert.Equal([1, 2, 3], Collect(tracker.Current));

        Assert.Equal(3, tracker.PopBack<Int32Helper>());
        Assert.False(tracker.HasChanges);
        Assert.Equal(2, tracker.KeepCount);
        Assert.Equal([1, 2], Collect(tracker.Current));
    }

    [Fact]
    public void PopCommittedWindow_TracksTrimCounts() {
        var tracker = new DequeChangeTracker<int>();

        tracker.PushBack<Int32Helper>(1);
        tracker.PushBack<Int32Helper>(2);
        tracker.PushBack<Int32Helper>(3);
        tracker.PushBack<Int32Helper>(4);
        tracker.Commit<Int32Helper>();

        Assert.Equal(1, tracker.PopFront<Int32Helper>());
        Assert.Equal(4, tracker.PopBack<Int32Helper>());

        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.TrimFrontCount);
        Assert.Equal(1, tracker.TrimBackCount);
        Assert.Equal(0, tracker.PushFrontCount);
        Assert.Equal(0, tracker.PushBackCount);
        Assert.Equal(2, tracker.KeepCount);
        Assert.Equal([2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void Revert_RestoresCommittedSequence_AndClearsChanges() {
        var tracker = new DequeChangeTracker<int>();

        tracker.PushBack<Int32Helper>(1);
        tracker.PushBack<Int32Helper>(2);
        tracker.PushBack<Int32Helper>(3);
        tracker.Commit<Int32Helper>();

        tracker.PushFront<Int32Helper>(0);
        tracker.PushBack<Int32Helper>(4);
        tracker.PopFront<Int32Helper>();

        Assert.True(tracker.HasChanges);
        Assert.Equal([1, 2, 3, 4], Collect(tracker.Current));

        tracker.Revert<Int32Helper>();

        Assert.False(tracker.HasChanges);
        Assert.Equal(3, tracker.RebaseCount);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
        Assert.Equal([1, 2, 3], Collect(tracker.Committed));
    }

    [Fact]
    public void Commit_RebuildsCommittedSnapshot_AndResetsWindow() {
        var tracker = new DequeChangeTracker<int>();

        tracker.PushBack<Int32Helper>(1);
        tracker.PushBack<Int32Helper>(2);
        tracker.PushBack<Int32Helper>(3);
        tracker.Commit<Int32Helper>();

        tracker.PopFront<Int32Helper>();
        tracker.PushBack<Int32Helper>(4);
        tracker.Commit<Int32Helper>();

        Assert.False(tracker.HasChanges);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal([2, 3, 4], Collect(tracker.Current));
        Assert.Equal([2, 3, 4], Collect(tracker.Committed));
    }

    [Fact]
    public void WriteDeltify_AndApplyDelta_RoundTripsTrackedSequence() {
        var source = new DequeChangeTracker<int>();
        SeedCommitted(ref source, [1, 2, 3, 4]);

        source.PushBack<Int32Helper>(5);
        source.PushBack<Int32Helper>(6);
        source.PopFront<Int32Helper>();
        source.PushFront<Int32Helper>(0);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        source.WriteDeltify<Int32Helper>(writer, DiffWriteContext.UserPrimary);

        var target = new DequeChangeTracker<int>();
        SeedCommitted(ref target, [1, 2, 3, 4]);

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted<Int32Helper>();

        Assert.Equal([0, 2, 3, 4, 5, 6], Collect(target.Current));
        Assert.Equal([0, 2, 3, 4, 5, 6], Collect(target.Committed));
        Assert.False(target.HasChanges);
    }

    [Fact]
    public void WriteDeltify_WithMultipleFrontItems_RoundTripsInLogicalOrder() {
        var source = new DequeChangeTracker<int>();
        SeedCommitted(ref source, [3, 4]);

        source.PushFront<Int32Helper>(2);
        source.PushFront<Int32Helper>(1);
        source.PushBack<Int32Helper>(5);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        source.WriteDeltify<Int32Helper>(writer, DiffWriteContext.UserPrimary);

        var target = new DequeChangeTracker<int>();
        SeedCommitted(ref target, [3, 4]);

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted<Int32Helper>();

        Assert.Equal([1, 2, 3, 4, 5], Collect(target.Current));
        Assert.Equal([1, 2, 3, 4, 5], Collect(target.Committed));
        Assert.False(target.HasChanges);
    }

    [Fact]
    public void WriteRebase_AndApplyDelta_RoundTripsIntoEmptyCommitted() {
        var source = new DequeChangeTracker<int>();
        source.PushBack<Int32Helper>(10);
        source.PushBack<Int32Helper>(20);
        source.PushFront<Int32Helper>(5);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        source.WriteRebase<Int32Helper>(writer, DiffWriteContext.UserPrimary);

        var target = new DequeChangeTracker<int>();
        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted<Int32Helper>();

        Assert.Equal([5, 10, 20], Collect(target.Current));
        Assert.Equal([5, 10, 20], Collect(target.Committed));
        Assert.False(target.HasChanges);
    }

    [Fact]
    public void PushFront_MatchingTrimmedCommittedValue_ExtendsKeepWindow() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        Assert.Equal(1, tracker.PopFront<Int32Helper>());
        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.TrimFrontCount);
        Assert.Equal([2, 3], Collect(tracker.Current));

        tracker.PushFront<Int32Helper>(1);

        Assert.False(tracker.HasChanges);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void PushFront_SignedZeroMismatch_DoesNotExtendKeepWindow() {
        var tracker = new DequeChangeTracker<double>();
        SeedCommitted(ref tracker, [-0.0, 1.0]);

        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(tracker.PopFront<DoubleHelper>()));
        Assert.Equal(1, tracker.TrimFrontCount);
        Assert.Equal([1.0], Collect(tracker.Current));

        tracker.PushFront<DoubleHelper>(0.0);

        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.TrimFrontCount);
        Assert.Equal(1, tracker.PushFrontCount);
        Assert.Equal(1, tracker.KeepCount);
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(tracker.Current[0]));
        Assert.Equal(BitConverter.DoubleToInt64Bits(1.0), BitConverter.DoubleToInt64Bits(tracker.Current[1]));
    }

    [Fact]
    public void PushBack_MatchingTrimmedCommittedValue_ExtendsKeepWindow() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        Assert.Equal(3, tracker.PopBack<Int32Helper>());
        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.TrimBackCount);
        Assert.Equal([1, 2], Collect(tracker.Current));

        tracker.PushBack<Int32Helper>(3);

        Assert.False(tracker.HasChanges);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void PeekFrontAndPeekBack_ReturnCurrentEdgeValues() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        tracker.PushFront<Int32Helper>(0);
        tracker.PushBack<Int32Helper>(4);

        Assert.Equal(0, tracker.PeekFront());
        Assert.Equal(4, tracker.PeekBack());
    }

    [Fact]
    public void SetFront_OnKeepWindowBoundary_SplitsFrontElementIntoDirtyPrefix() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        tracker.SetFront<Int32Helper>(10);

        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.TrimFrontCount);
        Assert.Equal(1, tracker.PushFrontCount);
        Assert.Equal(2, tracker.KeepCount);
        Assert.Equal([10, 2, 3], Collect(tracker.Current));

        tracker.Revert<Int32Helper>();
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void SetFront_OnSingleDirtyPrefix_CanExtendKeepWindowBackToCommitted() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        tracker.PopFront<Int32Helper>();
        tracker.PushFront<Int32Helper>(9);
        Assert.True(tracker.HasChanges);

        tracker.SetFront<Int32Helper>(1);

        Assert.False(tracker.HasChanges);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void SetFront_OnSingleDirtySuffix_CanExtendKeepWindowBackToCommitted() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1]);

        tracker.PopFront<Int32Helper>();
        tracker.PushBack<Int32Helper>(9);
        Assert.True(tracker.HasChanges);

        tracker.SetFront<Int32Helper>(1);

        Assert.False(tracker.HasChanges);
        Assert.Equal(1, tracker.KeepCount);
        Assert.Equal([1], Collect(tracker.Current));
    }

    [Fact]
    public void SetBack_OnKeepWindowBoundary_SplitsBackElementIntoDirtySuffix() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        tracker.SetBack<Int32Helper>(30);

        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.TrimBackCount);
        Assert.Equal(1, tracker.PushBackCount);
        Assert.Equal(2, tracker.KeepCount);
        Assert.Equal([1, 2, 30], Collect(tracker.Current));

        tracker.Revert<Int32Helper>();
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void SetBack_OnSingleDirtySuffix_CanExtendKeepWindowBackToCommitted() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        tracker.PopBack<Int32Helper>();
        tracker.PushBack<Int32Helper>(9);
        Assert.True(tracker.HasChanges);

        tracker.SetBack<Int32Helper>(3);

        Assert.False(tracker.HasChanges);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void SetBack_OnSingleDirtyPrefix_CanExtendKeepWindowBackToCommitted() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1]);

        tracker.PopBack<Int32Helper>();
        tracker.PushFront<Int32Helper>(9);
        Assert.True(tracker.HasChanges);

        tracker.SetBack<Int32Helper>(1);

        Assert.False(tracker.HasChanges);
        Assert.Equal(1, tracker.KeepCount);
        Assert.Equal([1], Collect(tracker.Current));
    }

    private static void SeedCommitted(ref DequeChangeTracker<int> tracker, int[] values) {
        for (int i = 0; i < values.Length; i++) {
            tracker.PushBack<Int32Helper>(values[i]);
        }
        tracker.Commit<Int32Helper>();
    }

    private static void SeedCommitted(ref DequeChangeTracker<double> tracker, double[] values) {
        for (int i = 0; i < values.Length; i++) {
            tracker.PushBack<DoubleHelper>(values[i]);
        }
        tracker.Commit<DoubleHelper>();
    }

    private static int[] Collect(IndexedDeque<int> deque) {
        var result = new int[deque.Count];
        for (int i = 0; i < deque.Count; i++) {
            result[i] = deque[i];
        }
        return result;
    }

    private static double[] Collect(IndexedDeque<double> deque) {
        var result = new double[deque.Count];
        for (int i = 0; i < deque.Count; i++) {
            result[i] = deque[i];
        }
        return result;
    }
}
