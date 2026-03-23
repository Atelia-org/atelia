using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

/// <summary>
/// <see cref="IMarkSweepPool{T}"/> 的契约测试。
/// 验证 Mark-Sweep GC 三阶段协议的正确性，由具体实现提供工厂方法。
/// </summary>
public abstract class MarkSweepPoolContractTests {

    /// <summary>
    /// 创建待测池实例。返回 <c>object</c> 以避免 internal 接口出现在 public 方法签名中。
    /// 实现方 MUST 返回 <see cref="IMarkSweepPool{T}"/> 实例。
    /// </summary>
    protected abstract object CreatePoolBoxed<T>() where T : notnull;

    private IMarkSweepPool<T> CreatePool<T>() where T : notnull
        => (IMarkSweepPool<T>)CreatePoolBoxed<T>();

    // ───────────────────── Basic GC cycle ─────────────────────

    [Fact]
    public void Sweep_ReclaimsUnreachable() {
        var pool = CreatePool<string>();
        SlotHandle a = pool.Store("alive");
        SlotHandle b = pool.Store("dead");
        SlotHandle c = pool.Store("also-alive");

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
        var pool = CreatePool<int>();
        SlotHandle a = pool.Store(1);
        SlotHandle b = pool.Store(2);

        pool.BeginMark();
        pool.MarkReachable(a);
        pool.MarkReachable(b);

        int freed = pool.Sweep();

        Assert.Equal(0, freed);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Sweep_NoneReachable_FreesAll() {
        var pool = CreatePool<int>();
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
        var pool = CreatePool<int>();
        pool.Store(1);
        Assert.Throws<InvalidOperationException>(() => pool.Sweep());
    }

    [Fact]
    public void Sweep_CalledTwice_Throws() {
        var pool = CreatePool<int>();
        pool.Store(1);
        pool.BeginMark();
        pool.Sweep();
        Assert.Throws<InvalidOperationException>(() => pool.Sweep());
    }

    // ───────────────────── Multi-cycle ─────────────────────

    [Fact]
    public void MultipleCycles_WorkCorrectly() {
        var pool = CreatePool<string>();
        SlotHandle a = pool.Store("a");
        SlotHandle b = pool.Store("b");

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
        SlotHandle c = pool.Store("c");
        pool.BeginMark();
        pool.MarkReachable(c);
        Assert.Equal(1, pool.Sweep());
        Assert.False(pool.Validate(a));
        Assert.True(pool.Validate(c));
    }

    // ───────────────────── Cross-slab ─────────────────────

    [Fact]
    public void Sweep_CrossSlab_SweepsCorrectly() {
        int slabSize = SlabBitmap.SlabSize;
        var pool = CreatePool<int>();

        // Fill 2 slabs
        var handles = new SlotHandle[slabSize * 2];
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
        var pool = CreatePool<string>();
        SlotHandle a = pool.Store("alpha");
        SlotHandle b = pool.Store("beta");
        SlotHandle c = pool.Store("gamma");

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
        var pool = CreatePool<int>();
        SlotHandle a = pool.Store(100);
        SlotHandle b = pool.Store(200);

        // Sweep b away
        pool.BeginMark();
        pool.MarkReachable(a);
        pool.Sweep();

        // Store new value — should reuse b's slot (lowest free)
        SlotHandle c = pool.Store(300);
        Assert.True(pool.Validate(c));
        Assert.Equal(300, pool[c]);
        Assert.Equal(2, pool.Count);
    }
}

// ───────────────────── Concrete implementations ─────────────────────

public class GcPool_MarkSweepContractTests : MarkSweepPoolContractTests {
    protected override object CreatePoolBoxed<T>() => new GcPool<T>();
}
