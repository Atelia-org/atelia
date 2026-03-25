using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

public class InternPoolTests {
    readonly struct Int32StaticEqualityComparer : IStaticEqualityComparer<int> {
        public static bool Equals(int a, int b) => a == b;
        public static int GetHashCode(int obj) => obj;
    }

    // ───────────────────── Store / Dedup ─────────────────────

    [Fact]
    public void Store_SameValue_ReturnsSameHandle() {
        var pool = new InternPool<string, OrdinalStaticEqualityComparer>();
        SlotHandle h1 = pool.Store("hello");
        SlotHandle h2 = pool.Store("hello");
        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Store_DifferentValues_ReturnsDifferentHandles() {
        var pool = new InternPool<string, OrdinalStaticEqualityComparer>();
        SlotHandle h1 = pool.Store("alpha");
        SlotHandle h2 = pool.Store("beta");
        Assert.NotEqual(h1, h2);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Store_IntValues_Deduplicates() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        SlotHandle a = pool.Store(42);
        SlotHandle b = pool.Store(99);
        SlotHandle c = pool.Store(42);
        Assert.Equal(a, c);
        Assert.NotEqual(a, b);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Store_ManyDuplicates_CountReflectsDistinct() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        for (int i = 0; i < 100; i++) { pool.Store(i % 10); }
        Assert.Equal(10, pool.Count);
    }

    // ───────────────────── Indexer / TryGetValue ─────────────────────

    [Fact]
    public void Indexer_ReturnsStoredValue() {
        var pool = new InternPool<string, OrdinalStaticEqualityComparer>();
        SlotHandle h = pool.Store("world");
        Assert.Equal("world", pool[h]);
    }

    [Fact]
    public void TryGetValue_ValidHandle_ReturnsTrue() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        SlotHandle h = pool.Store(7);
        Assert.True(pool.TryGetValue(h, out int val));
        Assert.Equal(7, val);
    }

    [Fact]
    public void TryGetValue_InvalidHandle_ReturnsFalse() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        Assert.False(pool.TryGetValue(new SlotHandle(0, 0), out _));
    }

    // ───────────────────── Validate ─────────────────────

    [Fact]
    public void Validate_ValidHandle_ReturnsTrue() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        SlotHandle h = pool.Store(1);
        Assert.True(pool.Validate(h));
    }

    [Fact]
    public void Validate_InvalidHandle_ReturnsFalse() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        Assert.False(pool.Validate(new SlotHandle(0, 0)));
        Assert.False(pool.Validate(new SlotHandle(99, 12345)));
    }

    // ───────────────────── TryGetIndex / Contains ─────────────────────

    [Fact]
    public void TryGetIndex_ExistingValue_ReturnsTrueAndCorrectHandle() {
        var pool = new InternPool<string, OrdinalStaticEqualityComparer>();
        SlotHandle stored = pool.Store("findme");
        Assert.True(pool.TryGetIndex("findme", out SlotHandle found));
        Assert.Equal(stored, found);
    }

    [Fact]
    public void TryGetIndex_NonExistingValue_ReturnsFalse() {
        var pool = new InternPool<string, OrdinalStaticEqualityComparer>();
        pool.Store("present");
        Assert.False(pool.TryGetIndex("absent", out _));
    }

    [Fact]
    public void Contains_ExistingValue_ReturnsTrue() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        pool.Store(42);
        Assert.True(pool.Contains(42));
    }

    [Fact]
    public void Contains_NonExistingValue_ReturnsFalse() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        pool.Store(42);
        Assert.False(pool.Contains(99));
    }

    [Fact]
    public void Contains_EmptyPool_ReturnsFalse() {
        var pool = new InternPool<string, OrdinalStaticEqualityComparer>();
        Assert.False(pool.Contains("anything"));
    }

    // ───────────────────── Custom comparer ─────────────────────

    [Fact]
    public void Store_WithCaseInsensitiveComparer_DeduplicatesByCase() {
        var pool = new InternPool<string, IgnoreCaseStaticEqualityComparer>();
        SlotHandle h1 = pool.Store("Hello");
        SlotHandle h2 = pool.Store("HELLO");
        SlotHandle h3 = pool.Store("hello");
        Assert.Equal(h1, h2);
        Assert.Equal(h1, h3);
        Assert.Equal(1, pool.Count);
        // 返回的值是首次存入的那个
        Assert.Equal("Hello", pool[h1]);
    }

    [Fact]
    public void TryGetIndex_WithCaseInsensitiveComparer_FindsByCase() {
        var pool = new InternPool<string, IgnoreCaseStaticEqualityComparer>();
        SlotHandle stored = pool.Store("Key");
        Assert.True(pool.TryGetIndex("KEY", out SlotHandle found));
        Assert.Equal(stored, found);
    }

    [Fact]
    public void Contains_WithCaseInsensitiveComparer_MatchesByCase() {
        var pool = new InternPool<string, IgnoreCaseStaticEqualityComparer>();
        pool.Store("Test");
        Assert.True(pool.Contains("test"));
        Assert.True(pool.Contains("TEST"));
    }

    // ───────────────────── Rehash (large insert) ─────────────────────

    [Fact]
    public void Store_ManyDistinctValues_TriggersRehashAndRemainsCorrect() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        const int n = 200; // 远超 InitialBucketCount=4，触发多次 rehash

        var handles = new SlotHandle[n];
        for (int i = 0; i < n; i++) { handles[i] = pool.Store(i); }

        Assert.Equal(n, pool.Count);

        // 所有值可正确读回
        for (int i = 0; i < n; i++) {
            Assert.Equal(i, pool[handles[i]]);
            Assert.True(pool.Contains(i));
        }

        // 重复 Store 返回已有 handle
        for (int i = 0; i < n; i++) {
            Assert.Equal(handles[i], pool.Store(i));
        }

        Assert.Equal(n, pool.Count); // 数量不变
    }

    // ───────────────────── Sweep + dedup consistency ─────────────────────

    [Fact]
    public void Sweep_RemovesUnreachable_ThenReStoreGetsNewHandle() {
        var pool = new InternPool<string, OrdinalStaticEqualityComparer>();
        SlotHandle h1 = pool.Store("ephemeral");
        pool.Store("keeper");

        // sweep 掉 ephemeral
        pool.BeginMark();
        pool.MarkReachable(pool.Store("keeper")); // Store("keeper") 去重命中
        pool.Sweep();

        Assert.False(pool.Contains("ephemeral"));
        Assert.Equal(1, pool.Count);

        // 重新 Store 相同值 → 应分配新 slot
        SlotHandle h2 = pool.Store("ephemeral");
        Assert.True(pool.Contains("ephemeral"));
        Assert.Equal(2, pool.Count);
        Assert.Equal("ephemeral", pool[h2]);
        // 新 handle 不等于旧 handle（generation 已变或 index 不同）
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Sweep_HashChainIntegrity_AfterPartialSweep() {
        // 构造多个哈希冲突的 entry 然后部分 sweep，验证链表完整性
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        const int n = 20;
        var handles = new SlotHandle[n];
        for (int i = 0; i < n; i++) { handles[i] = pool.Store(i); }

        // sweep 偶数值
        pool.BeginMark();
        for (int i = 1; i < n; i += 2) { pool.MarkReachable(handles[i]); }
        int freed = pool.Sweep();

        Assert.Equal(n / 2, freed);
        Assert.Equal(n / 2, pool.Count);

        // 奇数值仍可通过 Contains 找到（哈希桶链未损坏）
        for (int i = 0; i < n; i++) {
            if (i % 2 == 1) {
                Assert.True(pool.Contains(i));
                Assert.True(pool.TryGetIndex(i, out SlotHandle found));
                Assert.Equal(i, pool[found]);
            }
            else {
                Assert.False(pool.Contains(i));
            }
        }
    }

    [Fact]
    public void Sweep_ThenStore_DedupStillWorks() {
        var pool = new InternPool<string, OrdinalStaticEqualityComparer>();
        pool.Store("a");
        pool.Store("b");
        pool.Store("c");

        // sweep all
        pool.BeginMark();
        pool.Sweep();

        Assert.Equal(0, pool.Count);

        // 重建 → dedup 仍然生效
        SlotHandle x = pool.Store("new");
        SlotHandle y = pool.Store("new");
        Assert.Equal(x, y);
        Assert.Equal(1, pool.Count);
    }

    // ───────────────────── Cross-slab dedup ─────────────────────

    [Fact]
    public void Store_CrossSlab_DedupWorksAcrossSlabs() {
        int slabSize = SlabBitmap.SlabSize;
        var pool = new InternPool<int, Int32StaticEqualityComparer>();

        // 存满 2 个 slab
        for (int i = 0; i < slabSize * 2; i++) { pool.Store(i); }
        Assert.Equal(slabSize * 2, pool.Count);

        // 全部重复 Store → 不增长
        for (int i = 0; i < slabSize * 2; i++) { pool.Store(i); }
        Assert.Equal(slabSize * 2, pool.Count);
    }

    // ───────────────────── Rebuild ─────────────────────

    [Fact]
    public void Rebuild_Empty_ReturnsEmptyPool() {
        var pool = InternPool<int, Int32StaticEqualityComparer>.Rebuild([]);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Rebuild_SingleEntry_RoundTrips() {
        var original = new InternPool<string, OrdinalStaticEqualityComparer>();
        SlotHandle h = original.Store("hello");

        var rebuilt = InternPool<string, OrdinalStaticEqualityComparer>.Rebuild([(h, "hello")]);
        Assert.Equal(1, rebuilt.Count);
        Assert.Equal("hello", rebuilt[h]);
        Assert.True(rebuilt.Contains("hello"));
        Assert.True(rebuilt.Validate(h));
    }

    [Fact]
    public void Rebuild_ManyEntries_AllAccessible() {
        var original = new InternPool<int, Int32StaticEqualityComparer>();
        var handles = new SlotHandle[50];
        for (int i = 0; i < 50; i++) { handles[i] = original.Store(i * 7); }

        var entries = new (SlotHandle, int)[50];
        for (int i = 0; i < 50; i++) { entries[i] = (handles[i], i * 7); }

        var rebuilt = InternPool<int, Int32StaticEqualityComparer>.Rebuild(entries);
        Assert.Equal(50, rebuilt.Count);

        for (int i = 0; i < 50; i++) {
            Assert.Equal(i * 7, rebuilt[handles[i]]);
            Assert.True(rebuilt.Contains(i * 7));
            Assert.True(rebuilt.TryGetIndex(i * 7, out SlotHandle found));
            Assert.Equal(handles[i], found);
        }
    }

    [Fact]
    public void Rebuild_ThenStore_DedupWorks() {
        var original = new InternPool<string, OrdinalStaticEqualityComparer>();
        SlotHandle h1 = original.Store("existing");

        var rebuilt = InternPool<string, OrdinalStaticEqualityComparer>.Rebuild([(h1, "existing")]);
        SlotHandle h2 = rebuilt.Store("existing");
        Assert.Equal(h1, h2);
        Assert.Equal(1, rebuilt.Count);

        // New value gets new handle
        SlotHandle h3 = rebuilt.Store("brand_new");
        Assert.NotEqual(h1, h3);
        Assert.Equal(2, rebuilt.Count);
    }

    [Fact]
    public void Rebuild_AfterSweepCausesGap_HandlesPreserved() {
        var original = new InternPool<int, Int32StaticEqualityComparer>();
        var h0 = original.Store(0);
        var h1 = original.Store(1);
        var h2 = original.Store(2);

        // Sweep away h1 → creates gap
        original.BeginMark();
        original.MarkReachable(h0);
        original.MarkReachable(h2);
        original.Sweep();

        // Rebuild with remaining entries (gap at h1's index)
        var rebuilt = InternPool<int, Int32StaticEqualityComparer>.Rebuild([(h0, 0), (h2, 2)]);
        Assert.Equal(2, rebuilt.Count);
        Assert.Equal(0, rebuilt[h0]);
        Assert.Equal(2, rebuilt[h2]);
        Assert.True(rebuilt.Contains(0));
        Assert.True(rebuilt.Contains(2));
        Assert.False(rebuilt.Contains(1));
    }

    // ───────────────────── Sweep<THandler> ─────────────────────

    readonly struct CountingSweepHandler : ISweepCollectHandler<int> {
        internal static int Collected;
        public static void OnCollect(int value) => Collected++;
    }

    [Fact]
    public void SweepWithHandler_CallsOnCollectForEachSwept() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        for (int i = 0; i < 5; i++) { pool.Store(i); }

        pool.BeginMark();
        pool.MarkReachable(pool.Store(0));
        pool.MarkReachable(pool.Store(2));

        CountingSweepHandler.Collected = 0;
        int freed = pool.Sweep<CountingSweepHandler>();

        Assert.Equal(3, freed);
        Assert.Equal(3, CountingSweepHandler.Collected);
        Assert.Equal(2, pool.Count);
    }

    // ───────────────────── IsMarkedReachable ─────────────────────

    [Fact]
    public void IsMarkedReachable_ReflectsMarkState() {
        var pool = new InternPool<string, OrdinalStaticEqualityComparer>();
        SlotHandle h1 = pool.Store("a");
        SlotHandle h2 = pool.Store("b");

        pool.BeginMark();
        Assert.False(pool.IsMarkedReachable(h1));
        Assert.False(pool.IsMarkedReachable(h2));

        pool.MarkReachable(h1);
        Assert.True(pool.IsMarkedReachable(h1));
        Assert.False(pool.IsMarkedReachable(h2));

        pool.Sweep();
    }

    // ───────────────────── CompactWithUndo ─────────────────────

    /// <summary>让所有 int 碰撞到同一 bucket 的 comparer，用于验证 chain rewrite 正确性。</summary>
    readonly struct CollisionInt32Comparer : IStaticEqualityComparer<int> {
        public static bool Equals(int a, int b) => a == b;
        public static int GetHashCode(int obj) => 42; // 全部碰撞
    }

    [Fact]
    public void CompactWithUndo_NoGap_NoMoves() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        pool.Store(1);
        pool.Store(2);
        pool.Store(3);

        // No sweep → no gaps → compact should produce 0 moves
        pool.BeginMark();
        pool.MarkReachable(pool.Store(1));
        pool.MarkReachable(pool.Store(2));
        pool.MarkReachable(pool.Store(3));
        pool.Sweep();

        var journal = pool.CompactWithUndo(10);
        Assert.Empty(journal.Records);
    }

    [Fact]
    public void CompactWithUndo_WithGap_MovesData() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        var h0 = pool.Store(100);
        var h1 = pool.Store(200);
        var h2 = pool.Store(300);

        // Sweep h1 → creates gap at index 1
        pool.BeginMark();
        pool.MarkReachable(h0);
        pool.MarkReachable(h2);
        pool.Sweep();

        Assert.Equal(2, pool.Count);

        var journal = pool.CompactWithUndo(10);
        Assert.True(journal.Records.Count > 0);

        // After compact, all remaining values accessible and dedup works
        Assert.True(pool.Contains(100));
        Assert.True(pool.Contains(300));
        Assert.False(pool.Contains(200));
        Assert.Equal(2, pool.Count);

        // Store dedup still works
        var check100 = pool.Store(100);
        Assert.Equal(100, pool[check100]);
    }

    [Fact]
    public void CompactWithUndo_HashCollision_ChainRewriteCorrect() {
        // All values share bucket → long chain → verify chain rewrite
        var pool = new InternPool<int, CollisionInt32Comparer>();
        var handles = new SlotHandle[6];
        for (int i = 0; i < 6; i++) { handles[i] = pool.Store(i); }

        // Sweep even indices → interleaved gaps
        pool.BeginMark();
        for (int i = 1; i < 6; i += 2) { pool.MarkReachable(handles[i]); }
        pool.Sweep();

        Assert.Equal(3, pool.Count);

        var journal = pool.CompactWithUndo(10);

        // Remaining values still found through hash chain
        for (int i = 1; i < 6; i += 2) {
            Assert.True(pool.Contains(i), $"Value {i} not found after compact");
            Assert.True(pool.TryGetIndex(i, out SlotHandle found));
            Assert.Equal(i, pool[found]);
        }
        Assert.Equal(3, pool.Count);
    }

    [Fact]
    public void CompactWithUndo_MaxMovesLimitsWork() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        for (int i = 0; i < 10; i++) { pool.Store(i); }

        // Sweep every other → 5 gaps
        pool.BeginMark();
        for (int i = 0; i < 10; i += 2) { pool.MarkReachable(pool.Store(i)); }
        pool.Sweep();

        // Compact with limit
        var journal = pool.CompactWithUndo(2);
        Assert.True(journal.Records.Count <= 2);

        // All surviving values still accessible
        for (int i = 0; i < 10; i += 2) {
            Assert.True(pool.Contains(i), $"Value {i} not found");
        }
    }

    // ───────────────────── RollbackCompaction ─────────────────────

    [Fact]
    public void RollbackCompaction_RestoresOriginalState() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        var handles = new SlotHandle[6];
        for (int i = 0; i < 6; i++) { handles[i] = pool.Store(i * 10); }

        // Sweep odd indices
        pool.BeginMark();
        for (int i = 0; i < 6; i += 2) { pool.MarkReachable(handles[i]); }
        pool.Sweep();

        // Snapshot pre-compact state
        var preCompactHandles = new SlotHandle[3];
        for (int j = 0, i = 0; i < 6; i += 2, j++) {
            Assert.True(pool.TryGetIndex(i * 10, out preCompactHandles[j]));
        }

        var journal = pool.CompactWithUndo(10);
        Assert.True(journal.Records.Count > 0);

        // Rollback
        pool.RollbackCompaction(journal);

        // Original handles should work again
        for (int j = 0, i = 0; i < 6; i += 2, j++) {
            Assert.True(pool.Validate(preCompactHandles[j]), $"Handle for {i * 10} invalid after rollback");
            Assert.Equal(i * 10, pool[preCompactHandles[j]]);
        }

        // Hash lookup still correct
        for (int i = 0; i < 6; i += 2) {
            Assert.True(pool.Contains(i * 10));
        }
    }

    [Fact]
    public void RollbackCompaction_EmptyJournal_IsNoOp() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        pool.Store(1);
        pool.RollbackCompaction(new InternPool<int, Int32StaticEqualityComparer>.CompactionJournal([]));
        Assert.Equal(1, pool[pool.Store(1)]);
    }

    // ───────────────────── TrimExcessCapacity ─────────────────────

    [Fact]
    public void TrimExcessCapacity_ReducesCapacity() {
        var pool = new InternPool<int, Int32StaticEqualityComparer>();
        int slabSize = SlabBitmap.SlabSize;
        // Fill 2 slabs
        for (int i = 0; i < slabSize * 2; i++) { pool.Store(i); }
        int capBefore = pool.Capacity;

        // Sweep all in second slab → free entire trailing slab
        pool.BeginMark();
        for (int i = 0; i < slabSize; i++) { pool.MarkReachable(pool.Store(i)); }
        pool.Sweep();

        pool.CompactWithUndo(slabSize);
        pool.TrimExcessCapacity();

        Assert.True(pool.Capacity < capBefore, $"Capacity should shrink from {capBefore} but got {pool.Capacity}");
    }
}
