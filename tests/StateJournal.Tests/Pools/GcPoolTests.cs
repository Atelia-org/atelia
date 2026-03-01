using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

public class GcPoolTests {

    // ───────────────────── Store / Read / Validate ─────────────────────

    [Fact]
    public void Store_ReturnsHandle_ReadableViaIndexer() {
        var pool = new GcPool<string>();
        SlotHandle h = pool.Store("hello");
        Assert.Equal("hello", pool[h]);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void TryGetValue_ValidHandle_ReturnsTrue() {
        var pool = new GcPool<int>();
        SlotHandle h = pool.Store(42);
        Assert.True(pool.TryGetValue(h, out int val));
        Assert.Equal(42, val);
    }

    [Fact]
    public void TryGetValue_InvalidHandle_ReturnsFalse() {
        var pool = new GcPool<int>();
        // 用一个伪造的 handle 测试
        Assert.False(pool.TryGetValue(new SlotHandle(0, 0), out _));
    }

    [Fact]
    public void Validate_ValidHandle_ReturnsTrue() {
        var pool = new GcPool<int>();
        SlotHandle h = pool.Store(99);
        Assert.True(pool.Validate(h));
    }

    [Fact]
    public void Validate_InvalidHandle_ReturnsFalse() {
        var pool = new GcPool<int>();
        Assert.False(pool.Validate(new SlotHandle(0, 0)));
        Assert.False(pool.Validate(new SlotHandle(99, 12345)));
    }

    // GC 语义测试已提取到 MarkSweepPoolContractTests（契约测试，GcPool 与 InternPool 共用）

    // ───────────────────── Manual Free ─────────────────────

    [Fact]
    public void Free_ValidHandle_DecreasesCount() {
        var pool = new GcPool<string>();
        SlotHandle h = pool.Store("hello");
        Assert.Equal(1, pool.Count);

        pool.Free(h);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Free_ValidHandle_InvalidatesHandle() {
        var pool = new GcPool<string>();
        SlotHandle h = pool.Store("hello");
        pool.Free(h);

        Assert.False(pool.Validate(h));
        Assert.False(pool.TryGetValue(h, out _));
    }

    [Fact]
    public void Free_SlotCanBeReusedBySubsequentStore() {
        var pool = new GcPool<int>();
        SlotHandle h1 = pool.Store(100);
        pool.Free(h1);

        SlotHandle h2 = pool.Store(200);
        Assert.Equal(200, pool[h2]);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Free_StaleHandle_Throws() {
        var pool = new GcPool<int>();
        SlotHandle h1 = pool.Store(42);
        pool.Free(h1);
        // h1 过期后尝试再次 Free
        Assert.ThrowsAny<Exception>(() => pool.Free(h1));
    }

    [Fact]
    public void Free_DoubleFree_Throws() {
        var pool = new GcPool<int>();
        SlotHandle h = pool.Store(42);
        pool.Free(h);
        Assert.ThrowsAny<Exception>(() => pool.Free(h));
    }

    [Fact]
    public void Free_DuringMarkPhase_DoesNotCauseDoubleFreeInSweep() {
        var pool = new GcPool<int>();
        SlotHandle h1 = pool.Store(10);
        SlotHandle h2 = pool.Store(20);
        SlotHandle h3 = pool.Store(30);

        pool.BeginMark();
        pool.MarkReachable(h2); // h2 可达

        // 在 mark 阶段手动释放 h1（模拟独占 slot 的 inplace 替换）
        pool.Free(h1);

        // Sweep 应该只释放 h3（不可达），不应 double-free h1
        int swept = pool.Sweep();
        Assert.Equal(1, swept); // 只有 h3 被 sweep
        Assert.Equal(1, pool.Count); // 只剩 h2
        Assert.True(pool.Validate(h2));
    }

    [Fact]
    public void Free_MultipleThenSweep_ConsistentCount() {
        var pool = new GcPool<int>();
        var handles = new SlotHandle[5];
        for (int i = 0; i < 5; i++) handles[i] = pool.Store(i * 10);

        // 手动 Free h0 和 h1
        pool.Free(handles[0]);
        pool.Free(handles[1]);
        Assert.Equal(3, pool.Count);

        // Mark-Sweep: 只标记 h2 可达
        pool.BeginMark();
        pool.MarkReachable(handles[2]);
        int swept = pool.Sweep();

        Assert.Equal(2, swept); // h3, h4 被 sweep
        Assert.Equal(1, pool.Count); // 只剩 h2
    }
}
