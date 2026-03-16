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
        for (int i = 0; i < 5; i++) { handles[i] = pool.Store(i * 10); }

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

    // ───────────────────── Rebuild ─────────────────────

    [Fact]
    public void Rebuild_EmptySpan_ReturnsEmptyPool() {
        var pool = GcPool<string>.Rebuild(ReadOnlySpan<(SlotHandle, string)>.Empty);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Rebuild_WithEntries_ThenMarkSweep_CollectsUnmarked() {
        var hA = new SlotHandle(1, 1);
        var hB = new SlotHandle(2, 2);
        var hC = new SlotHandle(3, 3);

        var pool = GcPool<string>.Rebuild([(hA, "A"), (hB, "B"), (hC, "C")]);
        Assert.Equal(3, pool.Count);

        pool.BeginMark();
        pool.MarkReachable(hA);
        pool.MarkReachable(hC);
        int swept = pool.Sweep();

        Assert.Equal(1, swept);
        Assert.Equal(2, pool.Count);
        Assert.True(pool.Validate(hA));
        Assert.False(pool.Validate(hB));
        Assert.True(pool.Validate(hC));
    }

    [Fact]
    public void Rebuild_ThenStore_AllocatesFromLowestFreeIndex() {
        var h3 = new SlotHandle(4, 3);
        var pool = GcPool<string>.Rebuild([(h3, "existing")]);

        // Index 0 is the lowest free after Rebuild
        SlotHandle fill0 = pool.Store("fill0");
        Assert.Equal(0, fill0.Index);

        // Next lowest free is index 1
        SlotHandle newH = pool.Store("new");
        Assert.Equal(1, newH.Index);
        Assert.Equal("new", pool[newH]);
    }

    [Fact]
    public void Rebuild_PreservesGeneration_ValidateReturnsTrue() {
        var h1 = new SlotHandle(7, 5);
        var h2 = new SlotHandle(200, 10);

        var pool = GcPool<string>.Rebuild([(h1, "x"), (h2, "y")]);

        Assert.True(pool.Validate(h1));
        Assert.True(pool.Validate(h2));
        Assert.Equal("x", pool[h1]);
        Assert.Equal("y", pool[h2]);
    }

    [Fact]
    public void Rebuild_IndexZero_IsAllowed() {
        var h0 = new SlotHandle(0, 0);
        var h2 = new SlotHandle(9, 2);
        var pool = GcPool<string>.Rebuild([(h0, "zero"), (h2, "two")]);

        Assert.Equal(2, pool.Count);
        Assert.True(pool.Validate(h0));
        Assert.True(pool.Validate(h2));
        Assert.Equal("zero", pool[h0]);
        Assert.Equal("two", pool[h2]);
    }

    // ───────────────────── Compact ─────────────────────

    [Fact]
    public void Compact_MovesHighSlotToLowHole() {
        var pool = new GcPool<string>();
        var h0 = pool.Store("A"); // index 0
        var h1 = pool.Store("B"); // index 1
        var h2 = pool.Store("C"); // index 2

        // Mark-Sweep: keep h1 and h2, sweep h0 (creating a hole at index 0)
        pool.BeginMark();
        pool.MarkReachable(h1);
        pool.MarkReachable(h2);
        pool.Sweep();

        // Compact — should move h2 (highest) to index 0 (hole)
        var moves = pool.Compact(10);

        Assert.Single(moves);
        Assert.Equal(h2.Index, moves[0].Old.Index);
        Assert.Equal(0, moves[0].New.Index);
        Assert.Equal("C", pool[moves[0].New]);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Compact_RespectsMaxMoves() {
        var pool = new GcPool<string>();
        var handles = new SlotHandle[6];
        for (int i = 0; i < 6; i++) { handles[i] = pool.Store($"v{i}"); }

        // Sweep handles 0, 1, 2 → holes at 0, 1, 2; data at 3, 4, 5
        pool.BeginMark();
        pool.MarkReachable(handles[3]);
        pool.MarkReachable(handles[4]);
        pool.MarkReachable(handles[5]);
        pool.Sweep();

        // Compact with maxMoves=1: only one move
        var moves = pool.Compact(1);

        Assert.Single(moves);
    }

    [Fact]
    public void Compact_NoHoles_NoMoves() {
        var pool = new GcPool<string>();
        pool.Store("A");
        pool.Store("B");
        pool.Store("C");

        // Mark all reachable — no sweeps
        pool.BeginMark();
        foreach (var _ in new[] { 0, 1, 2 }) { } // dummy
        pool.MarkReachable(new SlotHandle(0, 0));
        pool.MarkReachable(new SlotHandle(0, 1));
        pool.MarkReachable(new SlotHandle(0, 2));
        pool.Sweep();

        var moves = pool.Compact(10);
        Assert.Empty(moves);
    }

    [Fact]
    public void Compact_MaxMovesZero_NoMoves() {
        var pool = new GcPool<string>();
        var h0 = pool.Store("A");
        pool.Store("B");

        pool.BeginMark();
        pool.MarkReachable(new SlotHandle(0, 1));
        pool.Sweep();

        var moves = pool.Compact(0);
        Assert.Empty(moves);
    }

    [Fact]
    public void Compact_HandlesMultipleMoves() {
        var pool = new GcPool<string>();
        var handles = new SlotHandle[6];
        for (int i = 0; i < 6; i++) { handles[i] = pool.Store($"v{i}"); }

        // Sweep even indices → holes at 0, 2, 4; data at 1, 3, 5
        pool.BeginMark();
        pool.MarkReachable(handles[1]);
        pool.MarkReachable(handles[3]);
        pool.MarkReachable(handles[5]);
        pool.Sweep();

        var moves = pool.Compact(10);

        // Two-Finger: hole=0→data=5, hole=2→data=3. index 1 stays.
        // The exact pairs depend on CompactionEnumerator, but moves should be ≤ 2
        Assert.True(moves.Count <= 2);
        Assert.Equal(3, pool.Count);

        // All values should be readable
        foreach (var (_, newH) in moves) {
            Assert.True(pool.Validate(newH));
        }
    }
}
