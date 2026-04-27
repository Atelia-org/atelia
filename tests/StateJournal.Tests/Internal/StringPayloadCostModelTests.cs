using System.Runtime.InteropServices;
using Atelia.StateJournal.Tests;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

/// <summary>
/// CMS Step 2 回归覆盖：<see cref="ValueBox.EstimateBareSize"/> 对 StringPayload
/// 改用 <c>1B tag + VarUInt(L*2) + L*2</c> 的 O(1) 上界算法（Length×2 = UTF-16 字节数）。
/// 这些场景守护：
/// <list type="bullet">
///   <item>inplace 多次改长度时 dirty 字节统计不 underflow（DEBUG slow recompute 自动断言）。</item>
///   <item>跨 kind (string ↔ Symbol / heap-bits64) 时 oldBareBytes 来自 mutate 前 snapshot。</item>
///   <item>discard / commit / fork 维持 estimate 等价（DEBUG slow recompute）。</item>
///   <item>estimate 始终 ≥ 实际写出的 wire size（同量级上界，UTF-8 路径写出更短时 estimate 略偏大）。</item>
///   <item>空 string、长 string、含 emoji 的高码点 string 各类边界。</item>
/// </list>
/// </summary>
[Collection("ValueBox")]
public class StringPayloadCostModelTests {
    private static int OwnedStringCount => ValuePools.OfOwnedString.Count;

    // ── tracker 推动器：复刻 MixedDictImpl.UpsertCore 的关键步骤 ─────────────────────

    private static void DriveStringUpsert(ref DictChangeTracker<int, ValueBox> tracker, int key, string value) {
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out bool exists);
        if (!ValueBox.StringPayloadFace.UpdateOrInit(ref slot, value, out uint oldBareBytes)) { return; }
        uint keyBytes = Int32Helper.EstimateBareSize(key, asKey: true);
        uint oldEntryBytes = exists ? checked(keyBytes + oldBareBytes) : 0u;
        tracker.AfterUpsert<ValueBoxHelper>(key, oldEntryBytes, exists, slot, keyBytes);
    }

    private static void DriveSymbolUpsert(ref DictChangeTracker<int, ValueBox> tracker, int key, SymbolId symbolId) {
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out bool exists);
        if (!ValueBox.SymbolIdFace.UpdateOrInit(ref slot, symbolId, out uint oldBareBytes)) { return; }
        uint keyBytes = Int32Helper.EstimateBareSize(key, asKey: true);
        uint oldEntryBytes = exists ? checked(keyBytes + oldBareBytes) : 0u;
        tracker.AfterUpsert<ValueBoxHelper>(key, oldEntryBytes, exists, slot, keyBytes);
    }

    private static void DriveUInt64Upsert(ref DictChangeTracker<int, ValueBox> tracker, int key, ulong value) {
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out bool exists);
        if (!ValueBox.UInt64Face.UpdateOrInit(ref slot, value, out uint oldBareBytes)) { return; }
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
        // 释放 dict 中所有可能占用 owned heap 的 slot，避免污染池计数。
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

    private static uint SerializedDeltify(in DictChangeTracker<int, ValueBox> tracker) {
        var t = tracker;
        return EstimateAssert.SerializedBodyBytes(w => t.WriteDeltify<Int32Helper, ValueBoxHelper>(w, DiffWriteContext.UserPrimary));
    }

    // ════════════════════════ 算法本体：直接 box.EstimateBareSize ════════════════════════

    [Fact]
    public void EstimateBareSize_EmptyString_Is2u() {
        var box = ValueBox.StringPayloadFace.From("");
        try {
            Assert.Equal(2u, box.EstimateBareSize());
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(box);
        }
    }

    [Fact]
    public void EstimateBareSize_AsciiString_UpperBoundFromLengthTimesTwo() {
        // "hello" → utf16Bytes=10, header=VarUInt(10)=1B, total=1+1+10=12u
        var box = ValueBox.StringPayloadFace.From("hello");
        try {
            uint estimate = box.EstimateBareSize();
            Assert.Equal(12u, estimate);
            // 实际 wire 走 UTF-8 时只需 1+1+5=7B；estimate ≥ wire（上界）
            uint wire = EstimateAssert.SerializedBodyBytes(w => box.Write(w));
            Assert.True(estimate >= wire, $"estimate={estimate} < wire={wire}");
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(box);
        }
    }

    [Fact]
    public void EstimateBareSize_HighSurrogateString_UpperBoundFromLengthTimesTwo() {
        // emoji: "🌟" 占 2 个 UTF-16 code unit，Length=2 → utf16Bytes=4
        var box = ValueBox.StringPayloadFace.From("🌟");
        try {
            uint estimate = box.EstimateBareSize();
            Assert.Equal(1u + 1u + 4u, estimate); // tag + VarUInt(4) + 4
            uint wire = EstimateAssert.SerializedBodyBytes(w => box.Write(w));
            Assert.True(estimate >= wire, $"emoji estimate={estimate} < wire={wire}");
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(box);
        }
    }

    [Fact]
    public void EstimateBareSize_Grows_WithStringLength_Strictly() {
        // estimate 与 Length 单调相关：长字符串 estimate 严格大于短字符串。
        var shortBox = ValueBox.StringPayloadFace.From("a");
        var midBox = ValueBox.StringPayloadFace.From(new string('b', 100));
        var longBox = ValueBox.StringPayloadFace.From(new string('c', 10000));
        try {
            uint shortEst = shortBox.EstimateBareSize();
            uint midEst = midBox.EstimateBareSize();
            uint longEst = longBox.EstimateBareSize();
            Assert.True(shortEst < midEst, $"short={shortEst} not < mid={midEst}");
            Assert.True(midEst < longEst, $"mid={midEst} not < long={longEst}");
            // VarUInt header 越界检查：100*2=200>127 → 2B header；10000*2=20000>16383 → 3B header
            Assert.Equal(1u + 2u + 200u, midEst);
            Assert.Equal(1u + 3u + 20000u, longEst);
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(shortBox);
            ValueBox.ReleaseOwnedHeapSlot(midBox);
            ValueBox.ReleaseOwnedHeapSlot(longBox);
        }
    }

    // ════════════════════════ tracker 路径：dirty 收支 + estimate ≥ wire ════════════════════════

    [Fact]
    public void Tracker_StringInsert_EstimateUpperBoundsWireSize() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            DriveStringUpsert(ref tracker, 1, "alpha");
            DriveStringUpsert(ref tracker, 2, "中文 + 🌟");
            DriveStringUpsert(ref tracker, 3, new string('x', 500));

            // EstimatedRebaseBytes 在 DEBUG 下会自动跑 slow recompute；本身覆盖 drift 检测。
            uint estimate = EstimateRebase(in tracker);
            uint wire = SerializedRebase(in tracker);
            Assert.True(estimate >= wire, $"rebase: estimate={estimate} < wire={wire}");
            // 上界但同量级：不应大于 wire 的 3 倍（UTF-16 vs UTF-8 ASCII 最多 2× + VarUInt header 余量）。
            Assert.True(estimate <= wire * 3u, $"rebase: estimate={estimate} > wire*3={wire * 3u} (上界过松？)");
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_StringInplaceUpdate_DifferentLengths_NoDriftAndPoolStable() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            DriveStringUpsert(ref tracker, 1, "short");
            tracker.Commit<ValueBoxHelper>();
            int ownedAfterCommit = OwnedStringCount;

            // 多次改长度：第一次 dirty mutation 走 free-old + alloc-new（旧 slot 被 committed 共享，frozen 不可改），
            // 之后的 mutation 都是 dirty exclusive → 走 inplace pool overwrite。
            DriveStringUpsert(ref tracker, 1, new string('L', 4000));
            // 此时 committed (frozen) + dirty (exclusive) 各 1 个 owned slot
            Assert.Equal(ownedAfterCommit + 1, OwnedStringCount);

            DriveStringUpsert(ref tracker, 1, "x");
            DriveStringUpsert(ref tracker, 1, "");
            DriveStringUpsert(ref tracker, 1, new string('M', 200));

            // 后续 inplace 复用 dirty slot，pool 不增。
            Assert.Equal(ownedAfterCommit + 1, OwnedStringCount);
            Assert.True(tracker.HasChanges);

            // estimate 路径会触发 DEBUG slow recompute；通过即说明 dirty 收支无 drift。
            uint estimate = EstimateDeltify(in tracker);
            uint wire = SerializedDeltify(in tracker);
            Assert.True(estimate >= wire, $"deltify after inplace churn: estimate={estimate} < wire={wire}");

            tracker.Commit<ValueBoxHelper>();
            Assert.False(tracker.HasChanges);
            Assert.Equal(ownedAfterCommit, OwnedStringCount);
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_StringToSymbolToString_CrossKind_NoDrift() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            DriveStringUpsert(ref tracker, 1, "first-string");
            tracker.Commit<ValueBoxHelper>();
            int ownedAfterCommit = OwnedStringCount;

            // string → Symbol（face 内部 free 旧 string slot，分配 SymbolId 槽，无 owned）
            DriveSymbolUpsert(ref tracker, 1, new SymbolId(42));
            Assert.True(tracker.HasChanges);

            // EstimatedDeltifyBytes 触发 DEBUG slow recompute；通过即无 drift。
            _ = EstimateDeltify(in tracker);
            uint wire1 = SerializedDeltify(in tracker);
            Assert.True(EstimateDeltify(in tracker) >= wire1);

            // Symbol → string（反向，重新分配 owned slot）
            DriveStringUpsert(ref tracker, 1, "second-string-much-longer");
            uint estimate2 = EstimateDeltify(in tracker);
            uint wire2 = SerializedDeltify(in tracker);
            Assert.True(estimate2 >= wire2, $"after Symbol→string: estimate={estimate2} < wire={wire2}");

            tracker.Commit<ValueBoxHelper>();
            // 旧 commit 时分配的 owned slot 已释放；新 commit 时重新分配 1 个。
            Assert.Equal(ownedAfterCommit, OwnedStringCount);
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_StringToHeapInteger_CrossKind_NoDrift() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        int ownedBefore = OwnedStringCount;
        int bits64Before = ValuePools.OfBits64.Count;
        try {
            DriveStringUpsert(ref tracker, 1, new string('z', 256));
            Assert.Equal(ownedBefore + 1, OwnedStringCount);

            // 直接在 dirty 状态下 string → heap-bits64 (ulong.MaxValue 走 heap)
            DriveUInt64Upsert(ref tracker, 1, ulong.MaxValue);
            Assert.Equal(ownedBefore, OwnedStringCount);
            Assert.Equal(bits64Before + 1, ValuePools.OfBits64.Count);

            uint estimate = EstimateDeltify(in tracker);
            uint wire = SerializedDeltify(in tracker);
            Assert.True(estimate >= wire);

            // 反向：bits64 → string
            DriveStringUpsert(ref tracker, 1, "back-to-string");
            Assert.Equal(ownedBefore + 1, OwnedStringCount);
            Assert.Equal(bits64Before, ValuePools.OfBits64.Count);

            tracker.Commit<ValueBoxHelper>();
            Assert.Equal(ownedBefore + 1, OwnedStringCount);
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_String_RevertRestoresEstimateAndPool() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            DriveStringUpsert(ref tracker, 1, "base");
            tracker.Commit<ValueBoxHelper>();
            int ownedAfterCommit = OwnedStringCount;

            uint estimateBaseline = EstimateRebase(in tracker);

            // dirty mutation
            DriveStringUpsert(ref tracker, 1, new string('y', 5000));
            DriveStringUpsert(ref tracker, 2, "added");
            Assert.True(tracker.HasChanges);
            Assert.NotEqual(estimateBaseline, EstimateRebase(in tracker));

            tracker.Revert<ValueBoxHelper>();
            Assert.False(tracker.HasChanges);
            Assert.Equal(ownedAfterCommit, OwnedStringCount);
            Assert.Equal(estimateBaseline, EstimateRebase(in tracker));
            // dirty 段也清零：
            Assert.Equal(SerializedDeltify(in tracker), EstimateDeltify(in tracker)); // 全是空头：估计精确
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_String_Remove_AfterDirtyUpsert_NoDrift() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            DriveStringUpsert(ref tracker, 1, "committed");
            tracker.Commit<ValueBoxHelper>();
            int ownedAfterCommit = OwnedStringCount;

            DriveStringUpsert(ref tracker, 2, new string('x', 1000)); // 新增 dirty
            DriveStringUpsert(ref tracker, 1, "much longer ......"); // 改 committed key

            DriveRemove(ref tracker, 1); // 删 committed key（dirty）
            DriveRemove(ref tracker, 2); // 删 dirty-only key

            // 触发 estimate 计算 → DEBUG slow recompute 守护 drift。
            _ = EstimateDeltify(in tracker);
            _ = EstimateRebase(in tracker);

            tracker.Commit<ValueBoxHelper>();
            Assert.Equal(ownedAfterCommit - 1, OwnedStringCount); // commit 释放 1 个旧 owned
        }
        finally {
            Cleanup(ref tracker);
        }
    }

    [Fact]
    public void Tracker_String_NoOpUpdate_KeepsEstimate() {
        var tracker = new DictChangeTracker<int, ValueBox>();
        try {
            DriveStringUpsert(ref tracker, 1, "value");
            tracker.Commit<ValueBoxHelper>();

            uint estimateBefore = EstimateRebase(in tracker);

            // 写同样的内容，UpdateOrInit 返回 false → 不进 AfterUpsert。
            DriveStringUpsert(ref tracker, 1, "value");
            Assert.False(tracker.HasChanges);
            Assert.Equal(estimateBefore, EstimateRebase(in tracker));
        }
        finally {
            Cleanup(ref tracker);
        }
    }
}
