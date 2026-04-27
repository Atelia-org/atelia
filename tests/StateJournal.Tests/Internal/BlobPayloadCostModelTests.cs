using System.Runtime.InteropServices;
using Atelia.StateJournal.Tests;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

/// <summary>
/// CMS Step 3c 验收：mixed 容器中 BlobPayload 走完整 cost-model + wire 路径，
/// estimate（O(1) <c>1B + VarUInt(L) + L</c>，<see cref="ValueBox.EstimateBareSize"/> Blob 分支完全 exact）
/// 与实际写出 wire size 同量级；DEBUG 下 <see cref="DictChangeTracker{TKey,TValue}.RecomputeEstimateSummarySlow"/>
/// 自动守门 dirty 收支 drift。
/// </summary>
[Collection("ValueBox")]
public class BlobPayloadCostModelTests {
    private static int OwnedBlobCount => ValuePools.OfOwnedBlob.Count;

    private static void DriveBlobUpsert(ref DictChangeTracker<int, ValueBox> tracker, int key, ByteString value) {
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out bool exists);
        if (!ValueBox.BlobPayloadFace.UpdateOrInit(ref slot, value, out uint oldBareBytes)) { return; }
        uint keyBytes = Int32Helper.EstimateBareSize(key, asKey: true);
        uint oldEntryBytes = exists ? checked(keyBytes + oldBareBytes) : 0u;
        tracker.AfterUpsert<ValueBoxHelper>(key, oldEntryBytes, exists, slot, keyBytes);
    }

    private static void DriveStringUpsert(ref DictChangeTracker<int, ValueBox> tracker, int key, string value) {
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out bool exists);
        if (!ValueBox.StringPayloadFace.UpdateOrInit(ref slot, value, out uint oldBareBytes)) { return; }
        uint keyBytes = Int32Helper.EstimateBareSize(key, asKey: true);
        uint oldEntryBytes = exists ? checked(keyBytes + oldBareBytes) : 0u;
        tracker.AfterUpsert<ValueBoxHelper>(key, oldEntryBytes, exists, slot, keyBytes);
    }

    private static void DriveRemove(ref DictChangeTracker<int, ValueBox> tracker, int key) {
        if (!tracker.Current.TryGetValue(key, out ValueBox box)) { return; }
        uint keyBytes = Int32Helper.EstimateBareSize(key, asKey: true);
        uint removedEntryBytes = checked(keyBytes + box.EstimateBareSize());
        Assert.True(tracker.Current.Remove(key, out var removedValue));
        tracker.AfterRemove<ValueBoxHelper>(key, removedValue, removedEntryBytes, keyBytes);
    }

    private static void Cleanup(ref DictChangeTracker<int, ValueBox> tracker) {
        var keys = new List<int>(tracker.Current.Keys);
        foreach (var k in keys) { DriveRemove(ref tracker, k); }
        tracker.Commit<ValueBoxHelper>();
    }

    private static uint EstimateRebase(in DictChangeTracker<int, ValueBox> tracker) {
        var t = tracker;
        return t.EstimatedRebaseBytes<Int32Helper, ValueBoxHelper>();
    }

    private static uint EstimateDeltify(in DictChangeTracker<int, ValueBox> tracker) {
        var t = tracker;
        return t.EstimatedDeltifyBytes<Int32Helper, ValueBoxHelper>();
    }

    private static uint SerializedRebase(in DictChangeTracker<int, ValueBox> tracker) {
        var t = tracker;
        return EstimateAssert.SerializedBodyBytes(w => t.WriteRebase<Int32Helper, ValueBoxHelper>(w, DiffWriteContext.UserPrimary));
    }

    // ════════════════════════ Estimate 算法本体 ════════════════════════

    [Fact]
    public void EstimateBareSize_BlobPayload_IsExact_ForVariousLengths() {
        // 算法：1B tag + VarUInt(L) + L，完全 exact（不像 string 的 *2 上界）。
        foreach (int len in new[] { 0, 1, 16, 127, 128, 1024, 16384, 65535 }) {
            byte[] data = Enumerable.Range(0, len).Select(i => (byte)i).ToArray();
            var box = ValueBox.BlobPayloadFace.From(new ByteString(data));
            try {
                uint estimate = box.EstimateBareSize();
                uint expected = checked(1u + CostEstimateUtil.VarIntSize((uint)len) + (uint)len);
                Assert.Equal(expected, estimate);

                // wire 也应等于 estimate（exact）
                uint wire = EstimateAssert.SerializedBodyBytes(w => box.Write(w));
                Assert.Equal(estimate, wire);
            }
            finally { ValueBox.ReleaseOwnedHeapSlot(box); }
        }
    }

    // ════════════════════════ tracker：dirty 收支 + estimate == wire ════════════════════════

    [Fact]
    public void Tracker_BlobInsert_EstimateMatchesWireSize_For1KBBlob() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            byte[] payload = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
            DriveBlobUpsert(ref tracker, 1, new ByteString(payload));

            uint estimate = EstimateRebase(in tracker);
            uint wire = SerializedRebase(in tracker);
            // Blob 估算 exact：estimate 与 wire 完全相等（不仅是同量级上界）。
            Assert.Equal(wire, estimate);
            // 量级 sanity：估算应远大于固定开销（>= payload 长度）。
            Assert.True(estimate >= 1024u, $"estimate={estimate} should cover 1KB payload");
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_BlobInsert_EstimateMatchesWireSize_For1MBBlob() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            byte[] payload = new byte[1 * 1024 * 1024];
            for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i * 7); }
            DriveBlobUpsert(ref tracker, 1, new ByteString(payload));

            uint estimate = EstimateRebase(in tracker);
            uint wire = SerializedRebase(in tracker);
            Assert.Equal(wire, estimate);
            Assert.True(estimate >= 1024u * 1024u, $"estimate={estimate} should cover 1MB payload");
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_BlobInplaceUpdate_DifferentLengths_NoDriftAndPoolStable() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            DriveBlobUpsert(ref tracker, 1, new ByteString(new byte[] { 1, 2, 3 }));
            tracker.Commit<ValueBoxHelper>();
            int ownedAfterCommit = OwnedBlobCount;

            // 第一次 dirty mutation：committed slot frozen → 走 free-old + alloc-new
            DriveBlobUpsert(ref tracker, 1, new ByteString(new byte[4000]));
            Assert.Equal(ownedAfterCommit + 1, OwnedBlobCount);

            // 后续 inplace pool overwrite
            DriveBlobUpsert(ref tracker, 1, new ByteString(new byte[] { 0xFF }));
            DriveBlobUpsert(ref tracker, 1, ByteString.Empty);
            DriveBlobUpsert(ref tracker, 1, new ByteString(new byte[200]));
            Assert.Equal(ownedAfterCommit + 1, OwnedBlobCount);

            // EstimatedDeltifyBytes 触发 DEBUG slow recompute；通过即说明 dirty 收支无 drift。
            uint estimate = EstimateDeltify(in tracker);
            Assert.True(estimate > 0u);

            tracker.Commit<ValueBoxHelper>();
            Assert.Equal(ownedAfterCommit, OwnedBlobCount);
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_BlobToString_CrossKind_NoDrift() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            DriveBlobUpsert(ref tracker, 1, new ByteString(new byte[256]));
            tracker.Commit<ValueBoxHelper>();
            int ownedAfterCommit = OwnedBlobCount;

            // blob → string：committed (frozen) blob slot 不释放（仍属 committed 共享）；
            // 替换发生在 dirty 槽，旧 frozen blob 槽要等到下一次 Commit 才被回收。
            DriveStringUpsert(ref tracker, 1, "now-string");
            Assert.Equal(ownedAfterCommit, OwnedBlobCount);

            uint estimate = EstimateDeltify(in tracker);
            Assert.True(estimate > 0u);

            // string → blob：再次跨 kind，blob slot 已让位给 string；现在重新分配 dirty blob slot。
            DriveBlobUpsert(ref tracker, 1, new ByteString(new byte[] { 0xDE, 0xAD }));
            Assert.Equal(ownedAfterCommit + 1, OwnedBlobCount);
            uint estimate2 = EstimateDeltify(in tracker);
            Assert.True(estimate2 > 0u);

            // Commit 释放旧 committed blob slot（被 string 替换的那个），dirty blob slot 升格为 committed。
            tracker.Commit<ValueBoxHelper>();
            Assert.Equal(ownedAfterCommit, OwnedBlobCount);
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_BlobUpsert_VariableLengths_CostModelAccumulates() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            uint expectedPayloadBytes = 0u;
            for (int i = 0; i < 100; i++) {
                int len = (i * 13) % 257; // 0..256 各种长度
                byte[] data = new byte[len];
                for (int j = 0; j < len; j++) { data[j] = (byte)((i + j) & 0xFF); }
                DriveBlobUpsert(ref tracker, i, new ByteString(data));
                // 每条记录的 wire 贡献：keyBytes + 1B tag + VarUInt(len) + len
                expectedPayloadBytes += checked((uint)len);
            }

            uint estimate = EstimateRebase(in tracker);
            uint wire = SerializedRebase(in tracker);
            // exact estimate
            Assert.Equal(wire, estimate);
            // sanity：至少覆盖所有 payload 字节（不算 tag/header/key/容器开销）
            Assert.True(estimate >= expectedPayloadBytes, $"estimate={estimate} < payloadBytes={expectedPayloadBytes}");
        }
        finally {
            Cleanup(ref tracker);
        }
    }
}
