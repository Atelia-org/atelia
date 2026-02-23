using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

public class GcPoolTests {

    // ───────────────────── Store / Read / Validate ─────────────────────

    [Fact]
    public void Store_ReturnsHandle_ReadableViaIndexer() {
        var pool = new GcPool<string>();
        int h = pool.Store("hello");
        Assert.Equal("hello", pool[h]);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void TryGetValue_ValidHandle_ReturnsTrue() {
        var pool = new GcPool<int>();
        int h = pool.Store(42);
        Assert.True(pool.TryGetValue(h, out int val));
        Assert.Equal(42, val);
    }

    [Fact]
    public void TryGetValue_InvalidHandle_ReturnsFalse() {
        var pool = new GcPool<int>();
        Assert.False(pool.TryGetValue(0, out _));
        Assert.False(pool.TryGetValue(-1, out _));
    }

    [Fact]
    public void Validate_ValidHandle_ReturnsTrue() {
        var pool = new GcPool<int>();
        int h = pool.Store(99);
        Assert.True(pool.Validate(h));
    }

    [Fact]
    public void Validate_InvalidHandle_ReturnsFalse() {
        var pool = new GcPool<int>();
        Assert.False(pool.Validate(0));
        Assert.False(pool.Validate(-1));
    }

    // ───────────────────── Basic GC cycle ─────────────────────

    [Fact]
    public void Sweep_ReclaimsUnreachable() {
        var pool = new GcPool<string>();
        int a = pool.Store("alive");
        int b = pool.Store("dead");
        int c = pool.Store("also-alive");

        pool.BeginMark();
        pool.MarkReachable(a);
        pool.MarkReachable(c);

        int freed = pool.Sweep();

        Assert.Equal(1, freed);
        Assert.Equal(2, pool.Count);
        Assert.True(pool.Validate(a));
        Assert.False(pool.Validate(b)); // swept
        Assert.True(pool.Validate(c));
    }

    [Fact]
    public void Sweep_AllReachable_FreesNothing() {
        var pool = new GcPool<int>();
        int a = pool.Store(1);
        int b = pool.Store(2);

        pool.BeginMark();
        pool.MarkReachable(a);
        pool.MarkReachable(b);

        int freed = pool.Sweep();

        Assert.Equal(0, freed);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Sweep_NoneReachable_FreesAll() {
        var pool = new GcPool<int>();
        pool.Store(1);
        pool.Store(2);
        pool.Store(3);

        pool.BeginMark();
        int freed = pool.Sweep();

        Assert.Equal(3, freed);
        Assert.Equal(0, pool.Count);
    }

    // ───────────────────── Sweep guard ─────────────────────

    [Fact]
    public void Sweep_WithoutBeginMark_Throws() {
        var pool = new GcPool<int>();
        pool.Store(1);
        Assert.Throws<InvalidOperationException>(() => pool.Sweep());
    }

    [Fact]
    public void Sweep_CalledTwice_Throws() {
        var pool = new GcPool<int>();
        pool.Store(1);
        pool.BeginMark();
        pool.Sweep();
        Assert.Throws<InvalidOperationException>(() => pool.Sweep());
    }

    // ───────────────────── Multi-cycle ─────────────────────

    [Fact]
    public void MultipleCycles_WorkCorrectly() {
        var pool = new GcPool<string>();
        int a = pool.Store("a");
        int b = pool.Store("b");

        // Cycle 1: keep both
        pool.BeginMark();
        pool.MarkReachable(a);
        pool.MarkReachable(b);
        Assert.Equal(0, pool.Sweep());

        // Cycle 2: drop b
        pool.BeginMark();
        pool.MarkReachable(a);
        Assert.Equal(1, pool.Sweep());
        Assert.True(pool.Validate(a));
        Assert.False(pool.Validate(b));

        // Allocate new, Cycle 3: drop a, keep c
        int c = pool.Store("c");
        pool.BeginMark();
        pool.MarkReachable(c);
        Assert.Equal(1, pool.Sweep());
        Assert.False(pool.Validate(a));
        Assert.True(pool.Validate(c));
    }

    // ───────────────────── Cross-slab ─────────────────────

    [Fact]
    public void Sweep_CrossSlab_SweepsCorrectly() {
        int shift = SlabBitmap.MinSlabShift; // 64
        int slabSize = 1 << shift;
        var pool = new GcPool<int>(shift);

        // Fill 2 slabs
        var handles = new int[slabSize * 2];
        for (int i = 0; i < handles.Length; i++) { handles[i] = pool.Store(i); }

        Assert.Equal(slabSize * 2, pool.Count);

        // Mark only odd-indexed handles as reachable
        pool.BeginMark();
        for (int i = 1; i < handles.Length; i += 2) { pool.MarkReachable(handles[i]); }

        int freed = pool.Sweep();

        Assert.Equal(slabSize, freed); // half freed
        Assert.Equal(slabSize, pool.Count);

        // Verify survivors
        for (int i = 0; i < handles.Length; i++) {
            if (i % 2 == 1) { Assert.True(pool.Validate(handles[i])); }
        }
    }

    // ───────────────────── Values preserved ─────────────────────

    [Fact]
    public void Values_PreservedAcrossGcCycle() {
        var pool = new GcPool<string>();
        int a = pool.Store("alpha");
        int b = pool.Store("beta");
        int c = pool.Store("gamma");

        pool.BeginMark();
        pool.MarkReachable(a);
        pool.MarkReachable(c);
        pool.Sweep();

        Assert.Equal("alpha", pool[a]);
        Assert.Equal("gamma", pool[c]);
    }

    // ───────────────────── Reuse after sweep ─────────────────────

    [Fact]
    public void Store_AfterSweep_ReusesFreedSlots() {
        var pool = new GcPool<int>(SlabBitmap.MinSlabShift);
        int a = pool.Store(100);
        int b = pool.Store(200);

        // Sweep b away
        pool.BeginMark();
        pool.MarkReachable(a);
        pool.Sweep();

        // Store new value — should reuse b's slot (lowest free)
        int c = pool.Store(300);
        Assert.True(pool.Validate(c));
        Assert.Equal(300, pool[c]);
        Assert.Equal(2, pool.Count);
    }
}
