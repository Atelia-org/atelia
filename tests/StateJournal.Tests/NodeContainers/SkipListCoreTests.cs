using System.Buffers;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.NodeContainers;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Tests.NodeContainers;

public class SkipListCoreTests {

    #region Basic Operations

    [Fact]
    public void Empty_Count_IsZero() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        Assert.Equal(0, core.Count);
        Assert.False(core.TryGet(42, out _));
    }

    [Fact]
    public void Upsert_SingleItem_CanGet() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        Assert.Equal(1, core.Count);
        Assert.True(core.TryGet(10, out var v));
        Assert.Equal(100, v);
    }

    [Fact]
    public void Upsert_MultipleItems_AscendingOrder() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(30, 300);
        core.Upsert(10, 100);
        core.Upsert(20, 200);
        Assert.Equal(3, core.Count);

        var keys = core.GetAllKeys();
        Assert.Equal([10, 20, 30], keys);
    }

    [Fact]
    public void Upsert_DuplicateKey_UpdatesValue() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        core.Upsert(10, 999);
        Assert.Equal(1, core.Count);
        Assert.True(core.TryGet(10, out var v));
        Assert.Equal(999, v);
    }

    [Fact]
    public void Remove_ExistingKey_ReturnsTrue() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        core.Upsert(20, 200);
        core.Upsert(30, 300);

        Assert.True(core.Remove(20));
        Assert.Equal(2, core.Count);
        Assert.False(core.TryGet(20, out _));
        Assert.Equal([10, 30], core.GetAllKeys());
    }

    [Fact]
    public void Remove_Head_Works() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        core.Upsert(20, 200);
        core.Upsert(30, 300);

        Assert.True(core.Remove(10));
        Assert.Equal(2, core.Count);
        Assert.Equal([20, 30], core.GetAllKeys());
    }

    [Fact]
    public void Remove_Tail_Works() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        core.Upsert(20, 200);
        core.Upsert(30, 300);

        Assert.True(core.Remove(30));
        Assert.Equal(2, core.Count);
        Assert.Equal([10, 20], core.GetAllKeys());
    }

    [Fact]
    public void Remove_NonExistentKey_ReturnsFalse() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        Assert.False(core.Remove(42));
        Assert.Equal(1, core.Count);
    }

    [Fact]
    public void Remove_AllItems_LeavesEmpty() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        core.Upsert(20, 200);
        Assert.True(core.Remove(10));
        Assert.True(core.Remove(20));
        Assert.Equal(0, core.Count);
        Assert.False(core.TryGet(10, out _));
    }

    [Fact]
    public void ContainsKey_ReflectsState() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        Assert.False(core.ContainsKey(10));
        core.Upsert(10, 100);
        Assert.True(core.ContainsKey(10));
        core.Remove(10);
        Assert.False(core.ContainsKey(10));
    }

    #endregion

    #region Range Queries

    [Fact]
    public void ReadAscendingFrom_MiddleOfRange() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        for (int i = 1; i <= 10; i++) { core.Upsert(i * 10, i * 100); }

        var result = core.ReadAscendingFrom(35, 3);
        Assert.Equal(3, result.Count);
        Assert.Equal(40, result[0].Key);
        Assert.Equal(50, result[1].Key);
        Assert.Equal(60, result[2].Key);
    }

    [Fact]
    public void ReadAscendingFrom_BeyondMax_ReturnsEmpty() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        core.Upsert(20, 200);

        var result = core.ReadAscendingFrom(30, 10);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadAscendingFrom_ExactMatch() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        core.Upsert(20, 200);
        core.Upsert(30, 300);

        var result = core.ReadAscendingFrom(20, 10);
        Assert.Equal(2, result.Count);
        Assert.Equal(20, result[0].Key);
        Assert.Equal(30, result[1].Key);
    }

    [Fact]
    public void ReadAscendingFrom_NegativeMaxCount_ThrowsArgumentOutOfRange() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => core.ReadAscendingFrom(10, -1));
    }

    #endregion

    #region Commit / Revert

    [Fact]
    public void Commit_ThenRevert_RestoresState() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        core.Upsert(20, 200);
        core.Commit();

        // Post-commit modifications
        core.Upsert(30, 300);
        core.Remove(10);
        Assert.Equal(2, core.Count);

        core.Revert();
        Assert.Equal(2, core.Count);
        Assert.Equal([10, 20], core.GetAllKeys());
        Assert.True(core.TryGet(10, out var v));
        Assert.Equal(100, v);
    }

    [Fact]
    public void Commit_ClearsHasChanges() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        Assert.True(core.HasChanges);
        core.Commit();
        Assert.False(core.HasChanges);
    }

    [Fact]
    public void MultipleCommits_AccumulateCorrectly() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(10, 100);
        core.Commit();

        core.Upsert(20, 200);
        core.Commit();

        core.Upsert(30, 300);
        core.Commit();

        Assert.Equal(3, core.Count);
        Assert.Equal([10, 20, 30], core.GetAllKeys());
    }

    [Fact]
    public void Commit_AfterHeadDeletion_AndTailValueUpdate_RemainsConsistent() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        core.Upsert(1, 10);
        core.Upsert(2, 20);
        core.Upsert(3, 30);
        core.Commit();

        core.Remove(1);
        core.Upsert(3, 99);

        core.Commit();

        Assert.False(core.HasChanges);
        Assert.Equal(2, core.Count);
        Assert.Equal([2, 3], core.GetAllKeys());
        Assert.True(core.TryGet(3, out var value));
        Assert.Equal(99, value);
    }

    #endregion

    #region Serialization Round-Trip

    private static byte[] WriteDelta(ref SkipListCore<int, int, Int32Helper, Int32Helper> core) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        core.WriteDeltify(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteRebase(ref SkipListCore<int, int, Int32Helper, Int32Helper> core) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        core.WriteRebase(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static void ApplyAndSync(ref SkipListCore<int, int, Int32Helper, Int32Helper> target, byte[] payload) {
        var reader = new BinaryDiffReader(payload);
        target.ApplyDelta(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted();
    }

    [Fact]
    public void WriteRebase_RoundTrips_ToEmptyTarget() {
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        source.Upsert(10, 100);
        source.Upsert(20, 200);
        source.Upsert(30, 300);
        source.Commit();

        byte[] payload = WriteRebase(ref source);

        SkipListCore<int, int, Int32Helper, Int32Helper> target = new();
        ApplyAndSync(ref target, payload);

        Assert.Equal(3, target.Count);
        Assert.Equal([10, 20, 30], target.GetAllKeys());
        Assert.True(target.TryGet(20, out var v));
        Assert.Equal(200, v);
    }

    [Fact]
    public void WriteDeltify_InsertAndUpdate_RoundTrips() {
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        source.Upsert(10, 100);
        source.Upsert(20, 200);
        source.Commit();

        // Delta: insert 15, update 20
        source.Upsert(15, 150);
        source.Upsert(20, 999);

        byte[] payload = WriteDelta(ref source);

        SkipListCore<int, int, Int32Helper, Int32Helper> target = new();
        target.Upsert(10, 100);
        target.Upsert(20, 200);
        target.Commit();

        ApplyAndSync(ref target, payload);

        Assert.Equal(3, target.Count);
        Assert.True(target.TryGet(15, out var v15));
        Assert.Equal(150, v15);
        Assert.True(target.TryGet(20, out var v20));
        Assert.Equal(999, v20);
    }

    [Fact]
    public void WriteDeltify_ChainedDeltas_RoundTrip() {
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        source.Upsert(10, 100);
        source.Commit();

        // Delta 1: add 20
        source.Upsert(20, 200);
        byte[] delta1 = WriteDelta(ref source);
        source.Commit();

        // Delta 2: add 30, update 10
        source.Upsert(30, 300);
        source.Upsert(10, 999);
        byte[] delta2 = WriteDelta(ref source);

        // Replay on target
        SkipListCore<int, int, Int32Helper, Int32Helper> target = new();
        target.Upsert(10, 100);
        target.Commit();

        ApplyAndSync(ref target, delta1);
        target.Commit();

        ApplyAndSync(ref target, delta2);

        Assert.Equal(3, target.Count);
        Assert.True(target.TryGet(10, out var v10));
        Assert.Equal(999, v10);
        Assert.Equal([10, 20, 30], target.GetAllKeys());
    }

    #endregion

    #region Estimate

    [Fact]
    public void EstimatedRebaseBytes_MatchesPayloadLength_AfterDeleteExcludesDeadNodes() {
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        source.Upsert(1, 10);
        source.Upsert(2, 20);
        source.Upsert(3, 30);
        source.Commit();

        source.Remove(3);

        byte[] payload = WriteRebase(ref source);

        Assert.Equal(payload.Length, (int)source.EstimatedRebaseBytes());
    }

    [Fact]
    public void EstimatedDeltifyBytes_MatchesPayloadLength_ForLinkOnlyDelete() {
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        for (int i = 1; i <= 10; i++) {
            source.Upsert(i, i * 10);
        }
        source.Commit();

        source.Remove(5);

        byte[] payload = WriteDelta(ref source);

        Assert.Equal(payload.Length, (int)source.EstimatedDeltifyBytes());
    }

    [Fact]
    public void EstimatedDeltifyBytes_MatchesPayloadLength_ForMixedSections() {
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        source.Upsert(1, 10);
        source.Upsert(3, 30);
        source.Commit();

        source.Upsert(2, 20);
        source.Upsert(3, 99);

        byte[] payload = WriteDelta(ref source);

        Assert.Equal(payload.Length, (int)source.EstimatedDeltifyBytes());
    }

    [Fact]
    public void EstimatedBytes_MatchesPayloadLength_AcrossVarIntBoundaries() {
        // 跨 sequence 的 1-byte/2-byte VarInt 边界（128）以及多次 commit 推高 _nextAllocSequence。
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        for (int i = 0; i < 200; i++) {
            source.Upsert(i, i);
        }
        source.Commit();

        // Rebase 估算：序列号、key、value、count 都跨过 128 边界。
        byte[] rebasePayload = WriteRebase(ref source);
        Assert.Equal(rebasePayload.Length, (int)source.EstimatedRebaseBytes());

        // Deltify 估算：混合 link-only delete、value mutation、appended（新增 seq 均为 2 字节 VarInt）。
        source.Remove(50);            // 1 个 dirty link
        source.Upsert(100, 100_000);  // 1 个 dirty value (value 跨 1-byte zigzag 边界)
        source.Upsert(300, 300);      // 1 个 appended

        byte[] deltaPayload = WriteDelta(ref source);
        Assert.Equal(deltaPayload.Length, (int)source.EstimatedDeltifyBytes());
    }

    #endregion

    #region Scale & Stress

    [Fact]
    public void LargeInsert_MaintainsOrder() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        // Insert 200 items in pseudo-random order
        var rng = new Random(42);
        var expected = new HashSet<int>();
        for (int i = 0; i < 200; i++) {
            int key = rng.Next(0, 10000);
            if (expected.Add(key)) {
                core.Upsert(key, key * 10);
            }
        }

        Assert.Equal(expected.Count, core.Count);
        var keys = core.GetAllKeys();
        for (int i = 1; i < keys.Count; i++) {
            Assert.True(keys[i] > keys[i - 1], $"Keys not sorted at index {i}: {keys[i - 1]} >= {keys[i]}");
        }
    }

    [Fact]
    public void LargeInsert_ThenRemoveHalf_ThenVerify() {
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        for (int i = 0; i < 100; i++) { core.Upsert(i, i * 10); }
        for (int i = 0; i < 100; i += 2) { Assert.True(core.Remove(i)); }

        Assert.Equal(50, core.Count);
        var keys = core.GetAllKeys();
        Assert.All(keys, k => Assert.Equal(1, k % 2)); // all odd
    }

    [Fact]
    public void Rebase_LargeDataSet_RoundTrips() {
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        for (int i = 0; i < 100; i++) { source.Upsert(i * 3, i * 30); }
        source.Commit();

        byte[] payload = WriteRebase(ref source);

        SkipListCore<int, int, Int32Helper, Int32Helper> target = new();
        ApplyAndSync(ref target, payload);

        Assert.Equal(100, target.Count);
        for (int i = 0; i < 100; i++) {
            Assert.True(target.TryGet(i * 3, out var v));
            Assert.Equal(i * 30, v);
        }
    }

    [Fact]
    public void WriteRebase_AfterDelete_ExcludesDeadNodes() {
        // Rebase after delete should only contain live nodes,
        // not the physically-present-but-unreachable dead nodes.
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        source.Upsert(1, 10);
        source.Upsert(2, 20);
        source.Upsert(3, 30);
        source.Commit();

        source.Remove(3);
        // At this point, node for key=3 is unlinked but physically in the arena.
        // WriteRebase should NOT write it.
        byte[] payload = WriteRebase(ref source);

        SkipListCore<int, int, Int32Helper, Int32Helper> target = new();
        ApplyAndSync(ref target, payload);

        Assert.Equal(2, target.Count);
        Assert.Equal([1, 2], target.GetAllKeys());
        Assert.False(target.TryGet(3, out _));
    }

    [Fact]
    public void WriteRebase_AfterDeleteAndInsert_NoSequenceCollision() {
        // delete -> commit -> insert -> rebase: the new node must get a unique
        // sequence even if GC freed the old slot.
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        source.Upsert(1, 10);
        source.Upsert(2, 20);
        source.Upsert(3, 30);
        source.Commit();

        source.Remove(3);
        source.Commit();

        source.Upsert(4, 40);
        byte[] payload = WriteRebase(ref source);

        SkipListCore<int, int, Int32Helper, Int32Helper> target = new();
        ApplyAndSync(ref target, payload);

        Assert.Equal(3, target.Count);
        Assert.Equal([1, 2, 4], target.GetAllKeys());
        Assert.True(target.TryGet(4, out var v4));
        Assert.Equal(40, v4);
    }

    [Fact]
    public void DeltaChain_DeleteThenInsert_LoadGC_CleanMemory() {
        // After loading a delta chain with deletions, ApplyDelta should
        // canonicalize the committed window and remove dead nodes.
        SkipListCore<int, int, Int32Helper, Int32Helper> source = new();
        source.Upsert(1, 10);
        source.Upsert(2, 20);
        source.Upsert(3, 30);
        source.Commit();

        // Delta: delete key=3
        source.Remove(3);
        byte[] delta = WriteDelta(ref source);
        source.Commit();

        // Load: rebase + delta
        byte[] rebase;
        {
            SkipListCore<int, int, Int32Helper, Int32Helper> fresh = new();
            fresh.Upsert(1, 10);
            fresh.Upsert(2, 20);
            fresh.Upsert(3, 30);
            fresh.Commit();
            rebase = WriteRebase(ref fresh);
        }

        SkipListCore<int, int, Int32Helper, Int32Helper> target = new();
        // Apply rebase
        {
            var r = new BinaryDiffReader(rebase);
            target.ApplyDelta(ref r);
            r.EnsureFullyConsumed();
        }
        target.SyncCurrentFromCommitted();
        target.Commit();
        // Apply delta (delete key=3)
        {
            var r = new BinaryDiffReader(delta);
            target.ApplyDelta(ref r);
            r.EnsureFullyConsumed();
        }
        target.SyncCurrentFromCommitted();

        Assert.Equal(2, target.Count);
        Assert.Equal([1, 2], target.GetAllKeys());
        Assert.False(target.TryGet(3, out _));

        // Verify we can continue operating (insert + another rebase round-trip)
        target.Upsert(4, 40);
        target.Commit();

        byte[] rebase2 = WriteRebase(ref target);
        SkipListCore<int, int, Int32Helper, Int32Helper> final_ = new();
        ApplyAndSync(ref final_, rebase2);

        Assert.Equal(3, final_.Count);
        Assert.Equal([1, 2, 4], final_.GetAllKeys());
    }

    [Fact]
    public void RemoveMiddlePair_CommitInsert_DeltaRoundTrip() {
        // Reproduces the DeleteMiddle_CommitInsertNew_Reopen_CorrectOrder scenario
        SkipListCore<int, int, Int32Helper, Int32Helper> core = new();
        for (int i = 1; i <= 5; i++) { core.Upsert(i, i * 10); }
        core.Commit();

        // Delta 2: remove 3 and 4
        core.Remove(3);
        core.Remove(4);
        Assert.Equal([1, 2, 5], core.GetAllKeys());
        byte[] delta2 = WriteDelta(ref core);
        core.Commit();

        // Delta 3: insert 6 and 7
        core.Upsert(6, 60);
        core.Upsert(7, 70);
        Assert.Equal([1, 2, 5, 6, 7], core.GetAllKeys());
        byte[] delta3 = WriteDelta(ref core);
        core.Commit();

        // Reload: rebase from original state, then apply both deltas
        SkipListCore<int, int, Int32Helper, Int32Helper> fresh = new();
        for (int i = 1; i <= 5; i++) { fresh.Upsert(i, i * 10); }
        fresh.Commit();
        byte[] rebase = WriteRebase(ref fresh);

        SkipListCore<int, int, Int32Helper, Int32Helper> target = new();
        ApplyAndSync(ref target, rebase);
        target.Commit();

        // Apply delta 2
        {
            var r = new BinaryDiffReader(delta2);
            target.ApplyDelta(ref r);
            r.EnsureFullyConsumed();
        }
        target.SyncCurrentFromCommitted();
        target.Commit();

        // Apply delta 3
        {
            var r = new BinaryDiffReader(delta3);
            target.ApplyDelta(ref r);
            r.EnsureFullyConsumed();
        }
        target.SyncCurrentFromCommitted();

        Assert.Equal(5, target.Count);
        Assert.Equal([1, 2, 5, 6, 7], target.GetAllKeys());
    }

    #endregion
}
