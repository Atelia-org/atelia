using System.Buffers;
using System.Runtime.InteropServices;
using Atelia.StateJournal.Serialization;
using Xunit;
using Atelia.StateJournal.Tests;

namespace Atelia.StateJournal.Internal.Tests;

[Collection("ValueBox")]
public class DictChangeTrackerTests {
    private static int Bits64Count => ValuePools.OfBits64.Count;

    private static void AssertEstimateMatchesSerializedBody<TKey, TValue, KHelper, VHelper>(in DictChangeTracker<TKey, TValue> tracker)
        where TKey : notnull
        where TValue : notnull
        where KHelper : unmanaged, ITypeHelper<TKey>
        where VHelper : unmanaged, ITypeHelper<TValue> {
        var rebaseTracker = tracker;
        EstimateAssert.EqualSerializedBodySize(
            rebaseTracker.EstimatedRebaseBytes<KHelper, VHelper>(),
            writer => rebaseTracker.WriteRebase<KHelper, VHelper>(writer, DiffWriteContext.UserPrimary)
        );

        var deltaTracker = tracker;
        EstimateAssert.EqualSerializedBodySize(
            deltaTracker.EstimatedDeltifyBytes<KHelper, VHelper>(),
            writer => deltaTracker.WriteDeltify<KHelper, VHelper>(writer, DiffWriteContext.UserPrimary)
        );
    }

    [Fact]
    public void Upsert_SameAsCurrent_IsNoOp() {
        var tracker = new DictChangeTracker<int, int>();

        Assert.Equal(UpsertStatus.Inserted, tracker.Upsert<Int32Helper, Int32Helper>(1, 7));
        Assert.True(tracker.HasChanges);

        tracker.Commit<Int32Helper>();
        Assert.False(tracker.HasChanges);

        Assert.Equal(UpsertStatus.Updated, tracker.Upsert<Int32Helper, Int32Helper>(1, 7));
        Assert.False(tracker.HasChanges);
        Assert.Equal(7, tracker.Current[1]);
    }

    [Fact]
    public void Upsert_BackToCommitted_ClearsDirty() {
        var tracker = new DictChangeTracker<int, int>();

        Assert.Equal(UpsertStatus.Inserted, tracker.Upsert<Int32Helper, Int32Helper>(1, 7));
        tracker.Commit<Int32Helper>();

        Assert.Equal(UpsertStatus.Updated, tracker.Upsert<Int32Helper, Int32Helper>(1, 9));
        Assert.True(tracker.HasChanges);

        Assert.Equal(UpsertStatus.Updated, tracker.Upsert<Int32Helper, Int32Helper>(1, 7));
        Assert.False(tracker.HasChanges);
        Assert.Equal(7, tracker.Current[1]);
    }

    [Fact]
    public void AfterUpsert_NoChange_CanonicalizesCurrent_AndReleasesTemporarySlot() {
        var tracker = new DictChangeTracker<byte, ValueBox>();
        const byte key = 1;

        var committed = ValueBox.Int64Face.From(long.MaxValue);
        tracker.Current[key] = committed;
        tracker.AfterUpsert<ValueBoxHelper>(key, 0u, existed: false, committed, ByteHelper.EstimateBareSize(key, asKey: true));
        tracker.Commit<ValueBoxHelper>();

        int countAfterCommit = Bits64Count;

        // 模拟真实 Upsert 流程：通过 ref 获取 slot，用 Update 尝试更新
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out _);
        bool changed = ValueBox.Int64Face.UpdateOrInit(ref slot, long.MaxValue, out _);
        Assert.False(changed); // 值未变，Update 返回 false

        // Update 返回 false 时跳过 AfterUpsert，slot 保持 frozen 不变
        Assert.False(tracker.HasChanges);
        Assert.Equal(countAfterCommit, Bits64Count); // 未分配新 slot

        ValueBox current = tracker.Current[key];
        Assert.Equal(ValueBox.Freeze(current).GetBits(), current.GetBits()); // 仍是 frozen
    }

    [Fact]
    public void AfterRemove_DirtyInsertedHeapValue_ReleasesSlot_AndClearsDirty() {
        var tracker = new DictChangeTracker<byte, ValueBox>();
        const byte key = 2;

        int before = Bits64Count;

        var inserted = ValueBox.Int64Face.From(long.MaxValue);
        tracker.Current[key] = inserted;
        tracker.AfterUpsert<ValueBoxHelper>(key, 0u, existed: false, inserted, ByteHelper.EstimateBareSize(key, asKey: true));

        Assert.True(tracker.HasChanges);
        Assert.Equal(before + 1, Bits64Count);

        uint keyBytes = ByteHelper.EstimateBareSize(key, asKey: true);
        ValueBox toRemove = tracker.Current[key];
        uint removedBytes = checked(keyBytes + ValueBoxHelper.EstimateBareSize(toRemove, asKey: false));
        bool removed = tracker.Current.Remove(key, out var removedValue);
        Assert.True(removed);
        tracker.AfterRemove<ValueBoxHelper>(key, removedValue, removedBytes, keyBytes);

        Assert.False(tracker.HasChanges);
        Assert.Equal(before, Bits64Count);
    }

    [Fact]
    public void AfterRemove_AfterNoChangeUpsert_DoesNotLeakExtraSlot() {
        var tracker = new DictChangeTracker<byte, ValueBox>();
        const byte key = 3;

        var committed = ValueBox.Int64Face.From(long.MaxValue);
        tracker.Current[key] = committed;
        tracker.AfterUpsert<ValueBoxHelper>(key, 0u, existed: false, committed, ByteHelper.EstimateBareSize(key, asKey: true));
        tracker.Commit<ValueBoxHelper>();
        int countAfterCommit = Bits64Count;

        // 模拟真实 Upsert 流程：Update 检测值相同 → 返回 false → 跳过 AfterUpsert
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out _);
        bool changed = ValueBox.Int64Face.UpdateOrInit(ref slot, long.MaxValue, out _);
        Assert.False(changed);
        // AfterUpsert 未被调用，状态不变
        Assert.False(tracker.HasChanges);

        uint keyBytes = ByteHelper.EstimateBareSize(key, asKey: true);
        ValueBox toRemove = tracker.Current[key];
        uint removedBytes = checked(keyBytes + ValueBoxHelper.EstimateBareSize(toRemove, asKey: false));
        bool removed = tracker.Current.Remove(key, out var removedValue);
        Assert.True(removed);
        tracker.AfterRemove<ValueBoxHelper>(key, removedValue, removedBytes, keyBytes);

        Assert.True(tracker.HasChanges);
        Assert.Equal(countAfterCommit, Bits64Count);
    }

    [Fact]
    public void Estimate_FollowsTypedLifecycleTransitions() {
        var tracker = new DictChangeTracker<int, int>();

        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(tracker);

        tracker.Upsert<Int32Helper, Int32Helper>(1, 7);
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(tracker);

        tracker.Commit<Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(tracker);

        tracker.Upsert<Int32Helper, Int32Helper>(1, 9);
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(tracker);

        tracker.Revert<Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(tracker);

        Assert.True(tracker.Current.Remove(1, out int removedValue));
        uint keyBytes1 = Int32Helper.EstimateBareSize(1, asKey: true);
        uint removedBytes1 = checked(keyBytes1 + Int32Helper.EstimateBareSize(removedValue, asKey: false));
        tracker.AfterRemove<Int32Helper>(1, removedValue, removedBytes1, keyBytes1);
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(tracker);

        tracker.Commit<Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(tracker);

        tracker.Upsert<Int32Helper, Int32Helper>(2, 11);
        tracker.Commit<Int32Helper>();
        tracker.FreezeFromClean<Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(tracker);

        tracker.UnfreezeToMutableClean<Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(tracker);
    }

    [Fact]
    public void DurableDictBase_EffectiveSize_IncludesTypeCodeLengthPrefix() {
        const uint estimatedRebaseBytes = 11;
        const uint estimatedDeltifyBytes = 7;
        const int typeCodeLength = 130;

        var fake = new FakeDurableDictForEstimate(estimatedRebaseBytes, estimatedDeltifyBytes, typeCodeLength, hasChanges: true);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        var context = new DiffWriteContext(FrameUsage.UserPayload, FrameSource.PrimaryCommit) { ForceRebase = true };

        fake.WritePendingDiff(writer, ref context);

        Assert.Equal(
            estimatedRebaseBytes + CostEstimateUtil.VarIntSize((uint)typeCodeLength) + typeCodeLength,
            context.EffectiveRebaseSize
        );
        Assert.Equal(
            estimatedDeltifyBytes + CostEstimateUtil.VarIntSize(0u),
            context.EffectiveDeltifySize
        );
    }

    [Fact]
    public void DurableDictBase_ApplyDelta_DoesNotAssumeZeroEstimate() {
        var fake = new FakeDurableDictForEstimate(estimatedRebaseBytes: 11, estimatedDeltifyBytes: 7, typeCodeLength: 130, hasChanges: false);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.BareUInt32(1, asKey: false); // cumulativeCost
        writer.BareUInt32(0, asKey: false); // objectFlags

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        fake.ApplyDelta(ref reader, default);
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void Estimate_RebuildsAcrossApplyDelta_SyncCurrentAndFork() {
        var source = new DictChangeTracker<int, int>();
        source.Upsert<Int32Helper, Int32Helper>(1, 7);
        source.Upsert<Int32Helper, Int32Helper>(2, 9);
        source.Commit<Int32Helper>();

        var rebaseBuffer = new ArrayBufferWriter<byte>();
        var rebaseWriter = new BinaryDiffWriter(rebaseBuffer);
        source.WriteRebase<Int32Helper, Int32Helper>(rebaseWriter, DiffWriteContext.UserPrimary);

        var loaded = new DictChangeTracker<int, int>();
        var reader = new BinaryDiffReader(rebaseBuffer.WrittenSpan);
        loaded.ApplyDelta<Int32Helper, Int32Helper>(ref reader);
        loaded.SyncCurrentFromCommitted<Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(loaded);

        var fork = loaded.ForkMutableFromCommitted<Int32Helper, Int32Helper>();
        AssertEstimateMatchesSerializedBody<int, int, Int32Helper, Int32Helper>(fork);
    }

    private sealed class FakeDurableDictForEstimate : DurableDictBase<int> {
        private readonly byte[] _typeCode;
        private readonly uint _estimatedRebaseBytes;
        private readonly uint _estimatedDeltifyBytes;
        private readonly bool _hasChanges;

        public FakeDurableDictForEstimate(uint estimatedRebaseBytes, uint estimatedDeltifyBytes, int typeCodeLength, bool hasChanges) {
            _estimatedRebaseBytes = estimatedRebaseBytes;
            _estimatedDeltifyBytes = estimatedDeltifyBytes;
            _typeCode = new byte[typeCodeLength];
            _hasChanges = hasChanges;
        }

        public override DurableObjectKind Kind => DurableObjectKind.TypedDict;
        public override bool HasChanges => _hasChanges;
        private protected override ReadOnlySpan<byte> TypeCode => _typeCode;
        private protected override uint EstimatedRebaseBytes => _estimatedRebaseBytes;
        private protected override uint EstimatedDeltifyBytes => _estimatedDeltifyBytes;
        private protected override void CommitCore() { }
        private protected override void SyncCurrentFromCommittedCore() { }
        private protected override void SyncFrozenCurrentFromCommittedCore() { }
        private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) { }
        private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) { }
        private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) { }
        internal override void DiscardChanges() { }
        internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) { }
    }
}
