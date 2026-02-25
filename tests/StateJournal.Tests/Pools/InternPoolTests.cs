using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

public class InternPoolTests {

    // ───────────────────── Store / Dedup ─────────────────────

    [Fact]
    public void Store_SameValue_ReturnsSameHandle() {
        var pool = new InternPool<string>();
        SlotHandle h1 = pool.Store("hello");
        SlotHandle h2 = pool.Store("hello");
        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Store_DifferentValues_ReturnsDifferentHandles() {
        var pool = new InternPool<string>();
        SlotHandle h1 = pool.Store("alpha");
        SlotHandle h2 = pool.Store("beta");
        Assert.NotEqual(h1, h2);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Store_IntValues_Deduplicates() {
        var pool = new InternPool<int>();
        SlotHandle a = pool.Store(42);
        SlotHandle b = pool.Store(99);
        SlotHandle c = pool.Store(42);
        Assert.Equal(a, c);
        Assert.NotEqual(a, b);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Store_ManyDuplicates_CountReflectsDistinct() {
        var pool = new InternPool<int>();
        for (int i = 0; i < 100; i++) { pool.Store(i % 10); }
        Assert.Equal(10, pool.Count);
    }

    // ───────────────────── Indexer / TryGetValue ─────────────────────

    [Fact]
    public void Indexer_ReturnsStoredValue() {
        var pool = new InternPool<string>();
        SlotHandle h = pool.Store("world");
        Assert.Equal("world", pool[h]);
    }

    [Fact]
    public void TryGetValue_ValidHandle_ReturnsTrue() {
        var pool = new InternPool<int>();
        SlotHandle h = pool.Store(7);
        Assert.True(pool.TryGetValue(h, out int val));
        Assert.Equal(7, val);
    }

    [Fact]
    public void TryGetValue_InvalidHandle_ReturnsFalse() {
        var pool = new InternPool<int>();
        Assert.False(pool.TryGetValue(new SlotHandle(0, 0), out _));
    }

    // ───────────────────── Validate ─────────────────────

    [Fact]
    public void Validate_ValidHandle_ReturnsTrue() {
        var pool = new InternPool<int>();
        SlotHandle h = pool.Store(1);
        Assert.True(pool.Validate(h));
    }

    [Fact]
    public void Validate_InvalidHandle_ReturnsFalse() {
        var pool = new InternPool<int>();
        Assert.False(pool.Validate(new SlotHandle(0, 0)));
        Assert.False(pool.Validate(new SlotHandle(99, 12345)));
    }

    // ───────────────────── TryGetIndex / Contains ─────────────────────

    [Fact]
    public void TryGetIndex_ExistingValue_ReturnsTrueAndCorrectHandle() {
        var pool = new InternPool<string>();
        SlotHandle stored = pool.Store("findme");
        Assert.True(pool.TryGetIndex("findme", out SlotHandle found));
        Assert.Equal(stored, found);
    }

    [Fact]
    public void TryGetIndex_NonExistingValue_ReturnsFalse() {
        var pool = new InternPool<string>();
        pool.Store("present");
        Assert.False(pool.TryGetIndex("absent", out _));
    }

    [Fact]
    public void Contains_ExistingValue_ReturnsTrue() {
        var pool = new InternPool<int>();
        pool.Store(42);
        Assert.True(pool.Contains(42));
    }

    [Fact]
    public void Contains_NonExistingValue_ReturnsFalse() {
        var pool = new InternPool<int>();
        pool.Store(42);
        Assert.False(pool.Contains(99));
    }

    [Fact]
    public void Contains_EmptyPool_ReturnsFalse() {
        var pool = new InternPool<string>();
        Assert.False(pool.Contains("anything"));
    }

    // ───────────────────── Custom comparer ─────────────────────

    [Fact]
    public void Store_WithCaseInsensitiveComparer_DeduplicatesByCase() {
        var pool = new InternPool<string>(StringComparer.OrdinalIgnoreCase);
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
        var pool = new InternPool<string>(StringComparer.OrdinalIgnoreCase);
        SlotHandle stored = pool.Store("Key");
        Assert.True(pool.TryGetIndex("KEY", out SlotHandle found));
        Assert.Equal(stored, found);
    }

    [Fact]
    public void Contains_WithCaseInsensitiveComparer_MatchesByCase() {
        var pool = new InternPool<string>(StringComparer.OrdinalIgnoreCase);
        pool.Store("Test");
        Assert.True(pool.Contains("test"));
        Assert.True(pool.Contains("TEST"));
    }

    // ───────────────────── Rehash (large insert) ─────────────────────

    [Fact]
    public void Store_ManyDistinctValues_TriggersRehashAndRemainsCorrect() {
        var pool = new InternPool<int>();
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
        var pool = new InternPool<string>();
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
        var pool = new InternPool<int>();
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
        var pool = new InternPool<string>();
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
        var pool = new InternPool<int>();

        // 存满 2 个 slab
        for (int i = 0; i < slabSize * 2; i++) { pool.Store(i); }
        Assert.Equal(slabSize * 2, pool.Count);

        // 全部重复 Store → 不增长
        for (int i = 0; i < slabSize * 2; i++) { pool.Store(i); }
        Assert.Equal(slabSize * 2, pool.Count);
    }
}
