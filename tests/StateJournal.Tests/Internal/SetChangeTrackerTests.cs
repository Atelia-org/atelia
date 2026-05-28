using System.Buffers;
using System.IO;
using Atelia.StateJournal.Serialization;
using Atelia.StateJournal.Tests;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class SetChangeTrackerTests {
    private static void AssertEstimateMatchesSerializedBody<TValue, VHelper>(in SetChangeTracker<TValue> tracker)
        where TValue : notnull
        where VHelper : unmanaged, ITypeHelper<TValue> {
        Revision? revision = typeof(TValue) == typeof(Symbol) ? new Revision(1) : null;

        var rebaseTracker = tracker;
        EstimateAssert.EqualSerializedBodySize(
            rebaseTracker.EstimatedRebaseBytes<VHelper>(),
            writer => rebaseTracker.WriteRebase<VHelper>(writer, DiffWriteContext.UserPrimary),
            revision
        );

        var deltaTracker = tracker;
        EstimateAssert.EqualSerializedBodySize(
            deltaTracker.EstimatedDeltifyBytes<VHelper>(),
            writer => deltaTracker.WriteDeltify<VHelper>(writer, DiffWriteContext.UserPrimary),
            revision
        );
    }

    private static byte[] WriteDeltifyPayload<TValue, VHelper>(in SetChangeTracker<TValue> tracker)
        where TValue : notnull
        where VHelper : unmanaged, ITypeHelper<TValue> {
        var copy = tracker;
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        copy.WriteDeltify<VHelper>(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteRebasePayload<TValue, VHelper>(in SetChangeTracker<TValue> tracker)
        where TValue : notnull
        where VHelper : unmanaged, ITypeHelper<TValue> {
        var copy = tracker;
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        copy.WriteRebase<VHelper>(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static SetChangeTracker<int> ReconstructCommittedOnly(params int[] committedItems) {
        var source = new SetChangeTracker<int>();
        foreach (int item in committedItems) {
            source.Add<Int32Helper>(item);
        }
        source.Commit<Int32Helper>();

        var target = new SetChangeTracker<int>();
        byte[] payload = WriteRebasePayload<int, Int32Helper>(source);
        var reader = new BinaryDiffReader(payload);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        return target;
    }

    private static void AssertIntDeltaPayload(byte[] payload, int[] expectedRemoved, int[] expectedAdded) {
        var reader = new BinaryDiffReader(payload);

        int removedCount = reader.ReadCount();
        Assert.Equal(expectedRemoved.Length, removedCount);
        var removed = new HashSet<int>();
        for (int i = 0; i < removedCount; ++i) {
            removed.Add(reader.BareInt32(asKey: true));
        }

        int addedCount = reader.ReadCount();
        Assert.Equal(expectedAdded.Length, addedCount);
        var added = new HashSet<int>();
        for (int i = 0; i < addedCount; ++i) {
            added.Add(reader.BareInt32(asKey: true));
        }

        reader.EnsureFullyConsumed();
        Assert.Equal(expectedRemoved.Length, removed.Count);
        Assert.Equal(expectedAdded.Length, added.Count);
        Assert.True(removed.SetEquals(expectedRemoved));
        Assert.True(added.SetEquals(expectedAdded));
    }

    [Fact]
    public void AddRemove_RecomputeDirty_BySetSemantics() {
        var tracker = new SetChangeTracker<int>();

        Assert.True(tracker.Add<Int32Helper>(1));
        Assert.True(tracker.Add<Int32Helper>(2));
        Assert.False(tracker.Add<Int32Helper>(1));
        tracker.Commit<Int32Helper>();

        Assert.True(tracker.Remove<Int32Helper>(1));
        Assert.True(tracker.Add<Int32Helper>(1));

        Assert.False(tracker.HasChanges);
        Assert.Equal(2, tracker.Count);
        Assert.True(tracker.Contains<Int32Helper>(1));
        Assert.True(tracker.Contains<Int32Helper>(2));
    }

    [Fact]
    public void WriteDeltify_AddOnly_WritesAddedSectionOnly() {
        var source = new SetChangeTracker<int>();
        source.Add<Int32Helper>(1);
        source.Add<Int32Helper>(2);
        source.Commit<Int32Helper>();
        source.Add<Int32Helper>(3);

        byte[] payload = WriteDeltifyPayload<int, Int32Helper>(source);
        AssertIntDeltaPayload(payload, [], [3]);

        var target = ReconstructCommittedOnly(1, 2);

        var reader = new BinaryDiffReader(payload);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted<Int32Helper>();

        Assert.False(target.HasChanges);
        Assert.Equal(3, target.Count);
        Assert.True(target.Contains<Int32Helper>(1));
        Assert.True(target.Contains<Int32Helper>(2));
        Assert.True(target.Contains<Int32Helper>(3));
    }

    [Fact]
    public void WriteDeltify_RemoveOnly_WritesRemovedSectionOnly() {
        var source = new SetChangeTracker<int>();
        source.Add<Int32Helper>(1);
        source.Add<Int32Helper>(2);
        source.Add<Int32Helper>(3);
        source.Commit<Int32Helper>();
        source.Remove<Int32Helper>(2);

        byte[] payload = WriteDeltifyPayload<int, Int32Helper>(source);
        AssertIntDeltaPayload(payload, [2], []);

        var target = ReconstructCommittedOnly(1, 2, 3);

        var reader = new BinaryDiffReader(payload);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted<Int32Helper>();

        Assert.False(target.HasChanges);
        Assert.Equal(2, target.Count);
        Assert.True(target.Contains<Int32Helper>(1));
        Assert.True(target.Contains<Int32Helper>(3));
        Assert.False(target.Contains<Int32Helper>(2));
    }

    [Fact]
    public void WriteDeltify_AddAndRemove_WritesBothSections() {
        var source = new SetChangeTracker<int>();
        source.Add<Int32Helper>(1);
        source.Add<Int32Helper>(2);
        source.Add<Int32Helper>(3);
        source.Commit<Int32Helper>();
        source.Remove<Int32Helper>(2);
        source.Add<Int32Helper>(4);

        byte[] payload = WriteDeltifyPayload<int, Int32Helper>(source);
        AssertIntDeltaPayload(payload, [2], [4]);

        var target = ReconstructCommittedOnly(1, 2, 3);

        var reader = new BinaryDiffReader(payload);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted<Int32Helper>();

        Assert.False(target.HasChanges);
        Assert.Equal(3, target.Count);
        Assert.True(target.Contains<Int32Helper>(1));
        Assert.True(target.Contains<Int32Helper>(3));
        Assert.True(target.Contains<Int32Helper>(4));
        Assert.False(target.Contains<Int32Helper>(2));
    }

    [Fact]
    public void AddRemove_AndRemoveAdd_BackToCommitted_WritesEmptyDelta() {
        var addThenRemove = new SetChangeTracker<int>();
        addThenRemove.Add<Int32Helper>(1);
        addThenRemove.Commit<Int32Helper>();

        Assert.True(addThenRemove.Add<Int32Helper>(2));
        Assert.True(addThenRemove.Remove<Int32Helper>(2));

        Assert.False(addThenRemove.HasChanges);
        AssertIntDeltaPayload(WriteDeltifyPayload<int, Int32Helper>(addThenRemove), [], []);

        var removeThenAdd = new SetChangeTracker<int>();
        removeThenAdd.Add<Int32Helper>(1);
        removeThenAdd.Commit<Int32Helper>();

        Assert.True(removeThenAdd.Remove<Int32Helper>(1));
        Assert.True(removeThenAdd.Add<Int32Helper>(1));

        Assert.False(removeThenAdd.HasChanges);
        AssertIntDeltaPayload(WriteDeltifyPayload<int, Int32Helper>(removeThenAdd), [], []);
    }

    [Fact]
    public void Estimate_RebuildsAcrossCommitFreezeAndFork() {
        var tracker = new SetChangeTracker<int>();

        AssertEstimateMatchesSerializedBody<int, Int32Helper>(tracker);

        tracker.Add<Int32Helper>(10);
        tracker.Add<Int32Helper>(20);
        AssertEstimateMatchesSerializedBody<int, Int32Helper>(tracker);

        tracker.Commit<Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, Int32Helper>(tracker);

        tracker.FreezeFromClean<Int32Helper>();
        Assert.Equal(2, tracker.Count);

        tracker.UnfreezeToMutableClean<Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, Int32Helper>(tracker);

        var fork = tracker.ForkMutableFromCommitted<Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, Int32Helper>(fork);
    }

    [Fact]
    public void StringTracker_RejectsNullAcrossAllMutationEntrances() {
        var tracker = new SetChangeTracker<string>();

        Assert.Throws<ArgumentNullException>(() => tracker.Add<StringHelper>(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.Contains<StringHelper>(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.Remove<StringHelper>(null!));
    }

    [Fact]
    public void DoubleTracker_UsesHelperBitEquality() {
        var tracker = new SetChangeTracker<double>();
        double posZero = 0.0;
        double negZero = BitConverter.Int64BitsToDouble(unchecked((long)0x8000_0000_0000_0000ul));
        double nan1 = BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8_0000_0000_0001ul));
        double nan2 = BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8_0000_0000_0002ul));

        Assert.True(tracker.Add<DoubleHelper>(posZero));
        Assert.True(tracker.Add<DoubleHelper>(negZero));
        Assert.True(tracker.Add<DoubleHelper>(nan1));
        Assert.True(tracker.Add<DoubleHelper>(nan2));

        Assert.False(tracker.Add<DoubleHelper>(posZero));
        Assert.False(tracker.Add<DoubleHelper>(negZero));
        Assert.False(tracker.Add<DoubleHelper>(nan1));
        Assert.False(tracker.Add<DoubleHelper>(nan2));

        Assert.Equal(4, tracker.Count);
        Assert.True(tracker.Contains<DoubleHelper>(posZero));
        Assert.True(tracker.Contains<DoubleHelper>(negZero));
        Assert.True(tracker.Contains<DoubleHelper>(nan1));
        Assert.True(tracker.Contains<DoubleHelper>(nan2));
    }

    [Fact]
    public void DoubleTracker_RemoveThenReadd_Canonicalization_KeepsSetSemanticsStable() {
        var tracker = new SetChangeTracker<double>();
        double posZero = 0.0;
        double negZero = BitConverter.Int64BitsToDouble(unchecked((long)0x8000_0000_0000_0000ul));

        Assert.True(tracker.Add<DoubleHelper>(negZero));
        tracker.Commit<DoubleHelper>();

        Assert.True(tracker.Remove<DoubleHelper>(negZero));
        Assert.True(tracker.Add<DoubleHelper>(posZero));
        Assert.True(tracker.HasChanges);
        Assert.True(tracker.Add<DoubleHelper>(negZero));
        Assert.True(tracker.HasChanges);
        Assert.Equal(2, tracker.Count);
        Assert.True(tracker.Contains<DoubleHelper>(negZero));
        Assert.True(tracker.Contains<DoubleHelper>(posZero));

        Assert.True(tracker.Remove<DoubleHelper>(negZero));
        Assert.True(tracker.Contains<DoubleHelper>(posZero));
        Assert.False(tracker.Contains<DoubleHelper>(negZero));
        Assert.True(tracker.Remove<DoubleHelper>(posZero));
        Assert.False(tracker.Contains<DoubleHelper>(posZero));
    }

    [Fact]
    public void SingleTracker_UsesHelperBitEquality() {
        var tracker = new SetChangeTracker<float>();
        float posZero = 0.0f;
        float negZero = BitConverter.Int32BitsToSingle(unchecked((int)0x8000_0000u));
        float nan1 = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC0_0001u));
        float nan2 = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC0_0002u));

        Assert.True(tracker.Add<SingleHelper>(posZero));
        Assert.True(tracker.Add<SingleHelper>(negZero));
        Assert.True(tracker.Add<SingleHelper>(nan1));
        Assert.True(tracker.Add<SingleHelper>(nan2));

        Assert.False(tracker.Add<SingleHelper>(posZero));
        Assert.False(tracker.Add<SingleHelper>(negZero));
        Assert.False(tracker.Add<SingleHelper>(nan1));
        Assert.False(tracker.Add<SingleHelper>(nan2));

        Assert.Equal(4, tracker.Count);
        Assert.True(tracker.Contains<SingleHelper>(posZero));
        Assert.True(tracker.Contains<SingleHelper>(negZero));
        Assert.True(tracker.Contains<SingleHelper>(nan1));
        Assert.True(tracker.Contains<SingleHelper>(nan2));
    }

    [Fact]
    public void HalfTracker_UsesHelperBitEquality() {
        var tracker = new SetChangeTracker<Half>();
        Half posZero = (Half)0;
        Half negZero = BitConverter.UInt16BitsToHalf(unchecked((ushort)0x8000));
        Half nan1 = BitConverter.UInt16BitsToHalf(unchecked((ushort)0x7E01));
        Half nan2 = BitConverter.UInt16BitsToHalf(unchecked((ushort)0x7E02));

        Assert.True(tracker.Add<HalfHelper>(posZero));
        Assert.True(tracker.Add<HalfHelper>(negZero));
        Assert.True(tracker.Add<HalfHelper>(nan1));
        Assert.True(tracker.Add<HalfHelper>(nan2));

        Assert.False(tracker.Add<HalfHelper>(posZero));
        Assert.False(tracker.Add<HalfHelper>(negZero));
        Assert.False(tracker.Add<HalfHelper>(nan1));
        Assert.False(tracker.Add<HalfHelper>(nan2));

        Assert.Equal(4, tracker.Count);
        Assert.True(tracker.Contains<HalfHelper>(posZero));
        Assert.True(tracker.Contains<HalfHelper>(negZero));
        Assert.True(tracker.Contains<HalfHelper>(nan1));
        Assert.True(tracker.Contains<HalfHelper>(nan2));
    }

    [Fact]
    public void ApplyDelta_RemoveMissingItem_ThrowsInvalidDataException() {
        var tracker = ReconstructCommittedOnly(1);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.WriteCount(1);
        writer.BareInt32(2, asKey: true);
        writer.WriteCount(0);

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        InvalidDataException ex;
        try {
            tracker.ApplyDelta<Int32Helper>(ref reader);
            throw new Xunit.Sdk.XunitException("Expected ApplyDelta to throw InvalidDataException.");
        }
        catch (InvalidDataException caught) {
            ex = caught;
        }

        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void ApplyDelta_DuplicateAddedPayload_ThrowsInvalidDataException() {
        var tracker = new SetChangeTracker<int>();

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.WriteCount(0);
        writer.WriteCount(2);
        writer.BareInt32(7, asKey: true);
        writer.BareInt32(7, asKey: true);

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        InvalidDataException ex;
        try {
            tracker.ApplyDelta<Int32Helper>(ref reader);
            throw new Xunit.Sdk.XunitException("Expected ApplyDelta to throw InvalidDataException.");
        }
        catch (InvalidDataException caught) {
            ex = caught;
        }
        Assert.Contains("duplicate", ex.Message);
    }

    [Fact]
    public void ApplyDelta_ThenSyncCurrent_MutateAndRevert_DoesNotPolluteCommitted() {
        var source = new SetChangeTracker<int>();
        source.Add<Int32Helper>(1);
        source.Add<Int32Helper>(2);
        source.Commit<Int32Helper>();
        source.Remove<Int32Helper>(1);
        source.Add<Int32Helper>(3);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        source.WriteDeltify<Int32Helper>(writer, DiffWriteContext.UserPrimary);

        var target = ReconstructCommittedOnly(1, 2);
        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        target.ApplyDelta<Int32Helper>(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted<Int32Helper>();

        Assert.True(target.Remove<Int32Helper>(3));
        Assert.True(target.Add<Int32Helper>(4));
        Assert.True(target.HasChanges);

        target.Revert<Int32Helper>();

        Assert.False(target.HasChanges);
        Assert.Equal(2, target.Count);
        Assert.True(target.Contains<Int32Helper>(2));
        Assert.True(target.Contains<Int32Helper>(3));
        Assert.False(target.Contains<Int32Helper>(1));
        Assert.False(target.Contains<Int32Helper>(4));
    }
}
