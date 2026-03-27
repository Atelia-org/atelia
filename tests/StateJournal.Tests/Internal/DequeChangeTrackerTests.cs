using System.Buffers;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class DequeChangeTrackerTests {
    [Fact]
    public void TryApis_OnEmptyTracker_ReturnFalse() {
        var tracker = new DequeChangeTracker<int>();

        Assert.False(tracker.TryGetAt(0, out int _));
        Assert.False(tracker.TryPeekFront(out int _));
        Assert.False(tracker.TryPeekBack(out int _));
        Assert.False(tracker.TryPopFront<Int32Helper>(out int _, out bool frontCallerOwned));
        Assert.False(frontCallerOwned);
        Assert.False(tracker.TryPopBack<Int32Helper>(out int _, out bool backCallerOwned));
        Assert.False(backCallerOwned);
    }

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

        Assert.Equal(0, PopFront(ref tracker));
        Assert.Equal(0, tracker.PushFrontCount);
        Assert.Equal(1, tracker.PushBackCount);
        Assert.Equal([1, 2, 3], Collect(tracker.Current));

        Assert.Equal(3, PopBack(ref tracker));
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

        Assert.Equal(1, PopFront(ref tracker));
        Assert.Equal(4, PopBack(ref tracker));

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
        PopFront(ref tracker);

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
    public void Revert_WithWrappedDirtyPrefix_RestoresCommittedSequence() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [3, 4]);

        tracker.PushFront<Int32Helper>(2);
        tracker.PushFront<Int32Helper>(1); // current logical [1,2,3,4], backing buffer wrapped

        Assert.True(tracker.HasChanges);
        Assert.Equal([1, 2, 3, 4], Collect(tracker.Current));

        tracker.Revert<Int32Helper>();

        Assert.False(tracker.HasChanges);
        Assert.Equal([3, 4], Collect(tracker.Current));
        Assert.Equal([3, 4], Collect(tracker.Committed));
    }

    [Fact]
    public void Commit_RebuildsCommittedSnapshot_AndResetsWindow() {
        var tracker = new DequeChangeTracker<int>();

        tracker.PushBack<Int32Helper>(1);
        tracker.PushBack<Int32Helper>(2);
        tracker.PushBack<Int32Helper>(3);
        tracker.Commit<Int32Helper>();

        PopFront(ref tracker);
        tracker.PushBack<Int32Helper>(4);
        tracker.Commit<Int32Helper>();

        Assert.False(tracker.HasChanges);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal([2, 3, 4], Collect(tracker.Current));
        Assert.Equal([2, 3, 4], Collect(tracker.Committed));
    }

    [Fact]
    public void Commit_WithWrappedDirtySuffix_PreservesLogicalOrder() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3, 4]);

        Assert.Equal(1, PopFront(ref tracker));
        Assert.Equal(2, PopFront(ref tracker));
        tracker.PushBack<Int32Helper>(5);
        tracker.PushBack<Int32Helper>(6); // current logical [3,4,5,6], backing buffer wrapped

        tracker.Commit<Int32Helper>();

        Assert.False(tracker.HasChanges);
        Assert.Equal([3, 4, 5, 6], Collect(tracker.Current));
        Assert.Equal([3, 4, 5, 6], Collect(tracker.Committed));
    }

    [Fact]
    public void WriteDeltify_AndApplyDelta_RoundTripsTrackedSequence() {
        var source = new DequeChangeTracker<int>();
        SeedCommitted(ref source, [1, 2, 3, 4]);

        source.PushBack<Int32Helper>(5);
        source.PushBack<Int32Helper>(6);
        PopFront(ref source);
        source.PushFront<Int32Helper>(0);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        source.WriteDeltify<Int32Helper>(writer, DiffWriteContext.UserPrimary);

        var target = new DequeChangeTracker<int>();
        SeedCommittedOnly(ref target, [1, 2, 3, 4]);

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
        SeedCommittedOnly(ref target, [3, 4]);

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
    public void SetAt_OnKeepWindow_TracksSparsePatch_AndCanRevert() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3, 4]);

        Assert.True(AssignAt(ref tracker, 2, 30));
        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.KeepDirtyCount);
        Assert.Equal([1, 2, 30, 4], Collect(tracker.Current));
        Assert.Equal([1, 2, 3, 4], Collect(tracker.Committed));

        tracker.Revert<Int32Helper>();

        Assert.False(tracker.HasChanges);
        Assert.Equal(0, tracker.KeepDirtyCount);
        Assert.Equal([1, 2, 3, 4], Collect(tracker.Current));
        Assert.Equal([1, 2, 3, 4], Collect(tracker.Committed));
    }

    [Fact]
    public void Commit_WithSparseKeepPatch_UpdatesCommittedSnapshot() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3, 4]);

        Assert.True(AssignAt(ref tracker, 1, 20));
        tracker.Commit<Int32Helper>();

        Assert.False(tracker.HasChanges);
        Assert.Equal(0, tracker.KeepDirtyCount);
        Assert.Equal([1, 20, 3, 4], Collect(tracker.Current));
        Assert.Equal([1, 20, 3, 4], Collect(tracker.Committed));
    }

    [Fact]
    public void WriteDeltify_WithSparseKeepPatchAndFrontBias_RoundTrips() {
        var source = new DequeChangeTracker<int>();
        SeedCommitted(ref source, [1, 2, 3, 4, 5]);

        Assert.True(AssignAt(ref source, 3, 40));
        PopFront(ref source);
        source.PushFront<Int32Helper>(0);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        source.WriteDeltify<Int32Helper>(writer, DiffWriteContext.UserPrimary);

        var target = new DequeChangeTracker<int>();
        SeedCommittedOnly(ref target, [1, 2, 3, 4, 5]);

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted<Int32Helper>();

        Assert.Equal([0, 2, 3, 40, 5], Collect(target.Current));
        Assert.Equal([0, 2, 3, 40, 5], Collect(target.Committed));
        Assert.False(target.HasChanges);
    }

    [Fact]
    public void PopFront_RemovingDirtyKeepElement_ShiftsRemainingSparsePatches() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3, 4]);

        Assert.True(AssignAt(ref tracker, 0, 10));
        Assert.True(AssignAt(ref tracker, 2, 30));
        Assert.Equal(2, tracker.KeepDirtyCount);

        Assert.Equal(10, PopFront(ref tracker));

        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.KeepDirtyCount);
        Assert.Equal([2, 30, 4], Collect(tracker.Current));

        tracker.Commit<Int32Helper>();

        Assert.False(tracker.HasChanges);
        Assert.Equal(0, tracker.KeepDirtyCount);
        Assert.Equal([2, 30, 4], Collect(tracker.Current));
        Assert.Equal([2, 30, 4], Collect(tracker.Committed));
    }

    [Fact]
    public void SetFront_RevertingDirtyKeepEdge_PreservesOtherSparsePatches() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3, 4]);

        Assert.True(AssignAt(ref tracker, 0, 10));
        Assert.True(AssignAt(ref tracker, 2, 30));
        Assert.Equal(2, tracker.KeepDirtyCount);

        Assert.True(AssignFront(ref tracker, 1));

        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.KeepDirtyCount);
        Assert.Equal([1, 2, 30, 4], Collect(tracker.Current));
        Assert.Equal([1, 2, 3, 4], Collect(tracker.Committed));

        tracker.Commit<Int32Helper>();

        Assert.False(tracker.HasChanges);
        Assert.Equal(0, tracker.KeepDirtyCount);
        Assert.Equal([1, 2, 30, 4], Collect(tracker.Current));
        Assert.Equal([1, 2, 30, 4], Collect(tracker.Committed));
    }

    [Fact]
    public void WriteDeltify_WithMultipleSparseKeepPatchesAndEdgeBiases_RoundTrips() {
        var source = new DequeChangeTracker<int>();
        SeedCommitted(ref source, [1, 2, 3, 4, 5, 6]);

        Assert.True(AssignAt(ref source, 1, 20));
        Assert.True(AssignAt(ref source, 4, 50));

        PopFront(ref source);
        source.PushFront<Int32Helper>(0);
        PopBack(ref source);
        source.PushBack<Int32Helper>(7);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        source.WriteDeltify<Int32Helper>(writer, DiffWriteContext.UserPrimary);

        var target = new DequeChangeTracker<int>();
        SeedCommittedOnly(ref target, [1, 2, 3, 4, 5, 6]);

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted<Int32Helper>();

        Assert.Equal([0, 20, 3, 4, 50, 7], Collect(target.Current));
        Assert.Equal([0, 20, 3, 4, 50, 7], Collect(target.Committed));
        Assert.False(target.HasChanges);
    }

    [Fact]
    public void PushFront_MatchingTrimmedCommittedValue_ExtendsKeepWindow() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        Assert.Equal(1, PopFront(ref tracker));
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

        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(PopFrontDouble(ref tracker)));
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

        Assert.Equal(3, PopBack(ref tracker));
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

        Assert.Equal(0, PeekFront(ref tracker));
        Assert.Equal(4, PeekBack(ref tracker));
    }

    [Fact]
    public void SetFront_OnKeepWindowBoundary_MarksAsKeepPatch() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        Assert.True(AssignFront(ref tracker, 10));

        Assert.True(tracker.HasChanges);
        Assert.Equal(0, tracker.TrimFrontCount);
        Assert.Equal(0, tracker.PushFrontCount);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal(1, tracker.KeepDirtyCount);
        Assert.Equal([10, 2, 3], Collect(tracker.Current));

        tracker.Revert<Int32Helper>();
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void SetFront_OnDirtyPrefix_AbsorbsBackIntoKeep() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        PopFront(ref tracker);
        tracker.PushFront<Int32Helper>(9);
        Assert.True(tracker.HasChanges);

        Assert.True(AssignFront(ref tracker, 1));

        Assert.False(tracker.HasChanges);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void SetFront_OnDirtySuffix_AbsorbsBackIntoKeep() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1]);

        PopFront(ref tracker);
        tracker.PushBack<Int32Helper>(9);
        Assert.True(tracker.HasChanges);

        Assert.True(AssignFront(ref tracker, 1));

        Assert.False(tracker.HasChanges);
        Assert.Equal(1, tracker.KeepCount);
        Assert.Equal([1], Collect(tracker.Current));
    }

    [Fact]
    public void SetFront_ThenSetBack_WithEmptyKeepAndBothDirtySides_AbsorbsInTwoSteps() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2]);

        Assert.Equal(1, PopFront(ref tracker));
        Assert.Equal(2, PopBack(ref tracker));
        tracker.PushFront<Int32Helper>(0);
        tracker.PushBack<Int32Helper>(3);

        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.TrimFrontCount);
        Assert.Equal(1, tracker.TrimBackCount);
        Assert.Equal(1, tracker.PushFrontCount);
        Assert.Equal(1, tracker.PushBackCount);
        Assert.Equal(0, tracker.KeepCount);
        Assert.Equal([0, 3], Collect(tracker.Current));

        Assert.True(AssignFront(ref tracker, 1));

        Assert.True(tracker.HasChanges);
        Assert.Equal(0, tracker.TrimFrontCount);
        Assert.Equal(1, tracker.TrimBackCount);
        Assert.Equal(0, tracker.PushFrontCount);
        Assert.Equal(1, tracker.PushBackCount);
        Assert.Equal(1, tracker.KeepCount);
        Assert.Equal([1, 3], Collect(tracker.Current));

        Assert.True(AssignBack(ref tracker, 2));

        Assert.False(tracker.HasChanges);
        Assert.Equal(2, tracker.KeepCount);
        Assert.Equal([1, 2], Collect(tracker.Current));
    }

    [Fact]
    public void SetBack_OnKeepWindowBoundary_MarksAsKeepPatch() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        Assert.True(AssignBack(ref tracker, 30));

        Assert.True(tracker.HasChanges);
        Assert.Equal(0, tracker.TrimBackCount);
        Assert.Equal(0, tracker.PushBackCount);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal(1, tracker.KeepDirtyCount);
        Assert.Equal([1, 2, 30], Collect(tracker.Current));

        tracker.Revert<Int32Helper>();
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void SetBack_OnDirtySuffix_AbsorbsBackIntoKeep() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        PopBack(ref tracker);
        tracker.PushBack<Int32Helper>(9);
        Assert.True(tracker.HasChanges);

        Assert.True(AssignBack(ref tracker, 3));

        Assert.False(tracker.HasChanges);
        Assert.Equal(3, tracker.KeepCount);
        Assert.Equal([1, 2, 3], Collect(tracker.Current));
    }

    [Fact]
    public void SetBack_OnDirtyPrefix_AbsorbsBackIntoKeep() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1]);

        PopBack(ref tracker);
        tracker.PushFront<Int32Helper>(9);
        Assert.True(tracker.HasChanges);

        Assert.True(AssignBack(ref tracker, 1));

        Assert.False(tracker.HasChanges);
        Assert.Equal(1, tracker.KeepCount);
        Assert.Equal([1], Collect(tracker.Current));
    }

    [Fact]
    public void SetBack_ThenSetFront_WithEmptyKeepAndBothDirtySides_AbsorbsInTwoSteps() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2]);

        Assert.Equal(1, PopFront(ref tracker));
        Assert.Equal(2, PopBack(ref tracker));
        tracker.PushFront<Int32Helper>(0);
        tracker.PushBack<Int32Helper>(3);

        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.TrimFrontCount);
        Assert.Equal(1, tracker.TrimBackCount);
        Assert.Equal(1, tracker.PushFrontCount);
        Assert.Equal(1, tracker.PushBackCount);
        Assert.Equal(0, tracker.KeepCount);
        Assert.Equal([0, 3], Collect(tracker.Current));

        Assert.True(AssignBack(ref tracker, 2));

        Assert.True(tracker.HasChanges);
        Assert.Equal(1, tracker.TrimFrontCount);
        Assert.Equal(0, tracker.TrimBackCount);
        Assert.Equal(1, tracker.PushFrontCount);
        Assert.Equal(0, tracker.PushBackCount);
        Assert.Equal(1, tracker.KeepCount);
        Assert.Equal([0, 2], Collect(tracker.Current));

        Assert.True(AssignFront(ref tracker, 1));

        Assert.False(tracker.HasChanges);
        Assert.Equal(2, tracker.KeepCount);
        Assert.Equal([1, 2], Collect(tracker.Current));
    }

    [Fact]
    public void ApplyDelta_WhenCurrentIsNotEmpty_Throws() {
        var tracker = new DequeChangeTracker<int>();
        SeedCommitted(ref tracker, [1, 2, 3]);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.WriteCount(0);
        writer.WriteCount(0);
        writer.WriteCount(0);
        writer.WriteCount(0);
        writer.WriteCount(0);

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        InvalidOperationException? ex = null;
        try {
            tracker.ApplyDelta<Int32Helper>(ref reader);
        }
        catch (InvalidOperationException caught) {
            ex = caught;
        }

        Assert.NotNull(ex);
        Assert.Contains("_current", ex.Message, StringComparison.Ordinal);
    }

    private static void SeedCommitted(ref DequeChangeTracker<int> tracker, int[] values) {
        for (int i = 0; i < values.Length; i++) {
            tracker.PushBack<Int32Helper>(values[i]);
        }
        tracker.Commit<Int32Helper>();
    }

    private static void SeedCommittedOnly(ref DequeChangeTracker<int> tracker, int[] values) {
        var source = new DequeChangeTracker<int>();
        SeedCommitted(ref source, values);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        source.WriteRebase<Int32Helper>(writer, DiffWriteContext.UserPrimary);

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        tracker.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        Assert.Equal(0, tracker.Current.Count);
        Assert.Equal(values, Collect(tracker.Committed));
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

    private static int PopFront(ref DequeChangeTracker<int> tracker) {
        Assert.True(tracker.TryPopFront<Int32Helper>(out int value, out _));
        return value;
    }

    private static int PopBack(ref DequeChangeTracker<int> tracker) {
        Assert.True(tracker.TryPopBack<Int32Helper>(out int value, out _));
        return value;
    }

    private static double PopFrontDouble(ref DequeChangeTracker<double> tracker) {
        Assert.True(tracker.TryPopFront<DoubleHelper>(out double value, out _));
        return value;
    }

    private static int PeekFront(ref DequeChangeTracker<int> tracker) {
        Assert.True(tracker.TryPeekFront(out int value));
        return value;
    }

    private static int PeekBack(ref DequeChangeTracker<int> tracker) {
        Assert.True(tracker.TryPeekBack(out int value));
        return value;
    }

    private static bool AssignFront(ref DequeChangeTracker<int> tracker, int value) => AssignAt(ref tracker, 0, value);

    private static bool AssignBack(ref DequeChangeTracker<int> tracker, int value) => AssignAt(ref tracker, tracker.Current.Count - 1, value);

    private static bool AssignAt(ref DequeChangeTracker<int> tracker, int index, int value) =>
        tracker.SetAt<Int32Helper>(index, value);
}
