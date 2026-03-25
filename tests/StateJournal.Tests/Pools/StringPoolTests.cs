using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

public class StringPoolTests {
    [Fact]
    // [InlineData(typeof(StringPoolDirect64x4))]
    public void StoreVariant_SameLongStringReference_ReusesHandle() {
        var pool = new StringPool();
        string value = new('p', 88);

        SlotHandle h1 = Store(pool, value);
        SlotHandle h2 = Store(pool, value);

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    // [InlineData(typeof(StringPoolDirect64x4))]
    public void StoreVariant_LongStringsWithSameContent_DeduplicatesByContent() {
        var pool = new StringPool();
        string a = new('r', 72);
        string b = string.Concat(new('r', 48), new('r', 24));

        SlotHandle h1 = Store(pool, a);
        SlotHandle h2 = Store(pool, b);

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Store_SameLongStringReference_ReusesHandle() {
        var pool = new StringPool();
        string value = new('x', 64);

        SlotHandle h1 = pool.Store(value);
        SlotHandle h2 = pool.Store(value);

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Store_SameLongStringReference_AfterSweep_ReusesCachedHash() {
        var pool = new StringPool();
        string value = new('y', 80);

        SlotHandle oldHandle = pool.Store(value);

        pool.BeginMark();
        Assert.Equal(1, pool.Sweep());
        Assert.False(pool.Validate(oldHandle));

        SlotHandle newHandle = pool.Store(value);

        Assert.True(pool.Validate(newHandle));
        Assert.Equal(value, pool[newHandle]);
    }

    [Fact]
    public void Store_SameLongStringReference_AcrossManySweepCycles_RemainsCorrect() {
        var pool = new StringPool();
        string value = new('w', 96);

        for (int i = 0; i < 300; i++) {
            SlotHandle handle = pool.Store(value);
            Assert.True(pool.Validate(handle));
            Assert.Equal(value, pool[handle]);

            pool.BeginMark();
            Assert.Equal(1, pool.Sweep());
            Assert.False(pool.Validate(handle));
        }

        SlotHandle finalHandle = pool.Store(value);
        Assert.True(pool.Validate(finalHandle));
        Assert.Equal(value, pool[finalHandle]);
    }

    [Fact]
    public void Store_LongStringsWithSameContent_DeduplicatesByContent() {
        var pool = new StringPool();
        string a = new('z', 72);
        string b = string.Concat(new('z', 36), new('z', 36));

        SlotHandle h1 = pool.Store(a);
        SlotHandle h2 = pool.Store(b);

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Store_ShortStrings_PassThroughToInnerPool() {
        var pool = new StringPool();
        string value = new('q', 16);

        SlotHandle h1 = pool.Store(value);
        SlotHandle h2 = pool.Store(string.Concat("qqqq", "qqqq", "qqqq", "qqqq"));

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    private static SlotHandle Store(StringPool pool, string value) => pool.Store(value);

    // ───────────────────── Rebuild ─────────────────────

    [Fact]
    public void Rebuild_Empty_ReturnsEmptyPool() {
        var pool = StringPool.Rebuild([]);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Rebuild_RoundTrips() {
        var original = new StringPool();
        SlotHandle h1 = original.Store("alpha");
        SlotHandle h2 = original.Store("bravo");

        var rebuilt = StringPool.Rebuild([(h1, "alpha"), (h2, "bravo")]);
        Assert.Equal(2, rebuilt.Count);
        Assert.Equal("alpha", rebuilt[h1]);
        Assert.Equal("bravo", rebuilt[h2]);
    }

    [Fact]
    public void Rebuild_ThenStore_DedupWorks() {
        var original = new StringPool();
        SlotHandle h1 = original.Store("existing");

        var rebuilt = StringPool.Rebuild([(h1, "existing")]);
        SlotHandle h2 = rebuilt.Store("existing");
        Assert.Equal(h1, h2);
        Assert.Equal(1, rebuilt.Count);
    }

    // ───────────────────── Sweep<THandler> ─────────────────────

    readonly struct StringCollector : ISweepCollectHandler<string> {
        internal static int Collected;
        public static void OnCollect(string value) => Collected++;
    }

    [Fact]
    public void SweepWithHandler_CollectsSweptValues() {
        var pool = new StringPool();
        SlotHandle h1 = pool.Store("keep");
        pool.Store("drop1");
        pool.Store("drop2");

        pool.BeginMark();
        pool.MarkReachable(h1);

        StringCollector.Collected = 0;
        int freed = pool.Sweep<StringCollector>();
        Assert.Equal(2, freed);
        Assert.Equal(2, StringCollector.Collected);
        Assert.Equal(1, pool.Count);
    }

    // ───────────────────── IsMarkedReachable ─────────────────────

    [Fact]
    public void IsMarkedReachable_ReflectsState() {
        var pool = new StringPool();
        SlotHandle h1 = pool.Store("a");
        SlotHandle h2 = pool.Store("b");

        pool.BeginMark();
        Assert.False(pool.IsMarkedReachable(h1));
        pool.MarkReachable(h1);
        Assert.True(pool.IsMarkedReachable(h1));
        Assert.False(pool.IsMarkedReachable(h2));
        pool.Sweep();
    }

    // ───────────────────── CompactWithUndo + RollbackCompaction ─────────────────────

    [Fact]
    public void CompactWithUndo_ThenLookup_Works() {
        var pool = new StringPool();
        SlotHandle h0 = pool.Store("zero");
        SlotHandle h1 = pool.Store("one");
        SlotHandle h2 = pool.Store("two");

        // Sweep "one" → gap
        pool.BeginMark();
        pool.MarkReachable(h0);
        pool.MarkReachable(h2);
        pool.Sweep();

        var journal = pool.CompactWithUndo(10);

        // Remaining values accessible
        Assert.True(pool.Validate(pool.Store("zero"))); // dedup hit
        Assert.True(pool.Validate(pool.Store("two")));
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void RollbackCompaction_RestoresHandles() {
        var pool = new StringPool();
        SlotHandle h0 = pool.Store("alpha");
        SlotHandle h1 = pool.Store("beta");
        SlotHandle h2 = pool.Store("gamma");

        // Sweep beta
        pool.BeginMark();
        pool.MarkReachable(h0);
        pool.MarkReachable(h2);
        pool.Sweep();

        // Capture pre-compact handles
        SlotHandle preH0, preH2;
        Assert.True(pool.TryGetValue(h0, out _)); preH0 = h0;
        Assert.True(pool.TryGetValue(h2, out _)); preH2 = h2;

        var journal = pool.CompactWithUndo(10);
        pool.RollbackCompaction(journal);

        // Original handles valid again
        Assert.True(pool.Validate(preH0));
        Assert.True(pool.Validate(preH2));
        Assert.Equal("alpha", pool[preH0]);
        Assert.Equal("gamma", pool[preH2]);
    }

    // ───────────────────── TrimExcessCapacity ─────────────────────

    [Fact]
    public void TrimExcessCapacity_Works() {
        var pool = new StringPool();
        int slabSize = SlabBitmap.SlabSize;

        // Fill 2 slabs with distinct strings
        for (int i = 0; i < slabSize * 2; i++) { pool.Store($"item_{i}"); }
        int capBefore = pool.Capacity;

        // Sweep all in slab 2
        pool.BeginMark();
        for (int i = 0; i < slabSize; i++) { pool.MarkReachable(pool.Store($"item_{i}")); }
        pool.Sweep();

        pool.CompactWithUndo(slabSize);
        pool.TrimExcessCapacity();

        Assert.True(pool.Capacity < capBefore);
    }
}
