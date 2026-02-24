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
}
