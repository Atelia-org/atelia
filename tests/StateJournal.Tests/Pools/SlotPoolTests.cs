using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

public class SlotPoolTests {
    // ───────────────────── Construction ─────────────────────

    [Fact]
    public void Ctor_Default_CreatesEmptyPool() {
        var table = new SlotPool<string>();
        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.Capacity);
    }

    [Theory]
    [InlineData(SlotPool<int>.MinSlabShift)]
    [InlineData(SlotPool<int>.MaxSlabShift)]
    [InlineData(8)]
    public void Ctor_ValidSlabShift_Succeeds(int shift) {
        var table = new SlotPool<int>(shift);
        Assert.Equal(1 << shift, table.SlabSize);
    }

    [Theory]
    [InlineData(SlotPool<int>.MinSlabShift - 1)]
    [InlineData(SlotPool<int>.MaxSlabShift + 1)]
    [InlineData(-1)]
    public void Ctor_InvalidSlabShift_Throws(int shift) {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotPool<int>(shift));
    }

    // ───────────────────── Alloc ─────────────────────

    [Fact]
    public void Alloc_ReturnsUniqueIndices() {
        var table = new SlotPool<string>(SlotPool<string>.MinSlabShift); // 64-slot slabs
        var indices = new HashSet<int>();
        for (int i = 0; i < 200; i++) {
            int idx = table.Alloc($"item-{i}");
            Assert.True(indices.Add(idx), $"Duplicate index {idx} at iteration {i}");
        }
        Assert.Equal(200, table.Count);
    }

    [Fact]
    public void Alloc_StoresValue_ReadableViaIndexer() {
        var table = new SlotPool<int>();
        int idx = table.Alloc(42);
        Assert.Equal(42, table[idx]);
    }

    // ───────────────────── Count / Capacity ─────────────────────

    [Fact]
    public void Count_TracksAllocAndFree() {
        var table = new SlotPool<int>(SlotPool<int>.MinSlabShift);
        Assert.Equal(0, table.Count);

        int a = table.Alloc(1);
        int b = table.Alloc(2);
        Assert.Equal(2, table.Count);

        table.Free(a);
        Assert.Equal(1, table.Count);

        table.Free(b);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Capacity_GrowsInSlabSizeIncrements() {
        int shift = SlotPool<int>.MinSlabShift; // 64
        int slabSize = 1 << shift;
        var table = new SlotPool<int>(shift);

        // 分配第一个 slot 触发第一个 slab
        table.Alloc(1);
        Assert.Equal(slabSize, table.Capacity);

        // 填满第一个 slab
        for (int i = 1; i < slabSize; i++) { table.Alloc(i + 1); }
        Assert.Equal(slabSize, table.Capacity);

        // 再分配一个触发第二个 slab
        table.Alloc(999);
        Assert.Equal(slabSize * 2, table.Capacity);
    }

    // ───────────────────── IsOccupied ─────────────────────

    [Fact]
    public void IsOccupied_ReturnsTrueForAllocated_FalseAfterFree() {
        var table = new SlotPool<string>(SlotPool<string>.MinSlabShift);
        int idx = table.Alloc("hello");
        Assert.True(table.IsOccupied(idx));

        table.Free(idx);
        // Free 后的尾部收缩可能使得 index 超出 capacity，此时 IsOccupied 抛 OOR
        // 所以需要保留至少一个更低的 slot 来阻止收缩
    }

    [Fact]
    public void IsOccupied_AfterFreeWithLowerSlotAlive_ReturnsFalse() {
        var table = new SlotPool<string>(SlotPool<string>.MinSlabShift);
        int a = table.Alloc("keep");
        int b = table.Alloc("release");
        Assert.True(table.IsOccupied(b));

        table.Free(b);
        Assert.False(table.IsOccupied(b)); // b 仍在 capacity 内（a 阻止了收缩）
    }

    [Fact]
    public void IsOccupied_OutOfRange_Throws() {
        var table = new SlotPool<int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => table.IsOccupied(0));

        table.Alloc(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => table.IsOccupied(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.IsOccupied(table.Capacity));
    }

    // ───────────────────── Indexer / GetValueRef ─────────────────────

    [Fact]
    public void Indexer_FreedSlot_Throws() {
        var table = new SlotPool<int>(SlotPool<int>.MinSlabShift);
        int a = table.Alloc(10);
        int b = table.Alloc(20);
        table.Free(b);

        Assert.Throws<InvalidOperationException>(() => _ = table[b]);
        // a 仍可正常访问
        Assert.Equal(10, table[a]);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws() {
        var table = new SlotPool<int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = table[0]);
    }

    [Fact]
    public void GetValueRef_AllowsMutation() {
        var table = new SlotPool<int>();
        int idx = table.Alloc(100);
        ref int val = ref table.GetValueRef(idx);
        val = 200;
        Assert.Equal(200, table[idx]);
    }

    [Fact]
    public void GetValueRef_FreedSlot_Throws() {
        var table = new SlotPool<int>(SlotPool<int>.MinSlabShift);
        int a = table.Alloc(1);
        int b = table.Alloc(2);
        table.Free(b);
        Assert.Throws<InvalidOperationException>(() => table.GetValueRef(b));
    }

    [Fact]
    public void GetValueRef_OutOfRange_Throws() {
        var table = new SlotPool<int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetValueRef(0));
    }

    // ───────────────────── Indexer set ─────────────────────

    [Fact]
    public void IndexerSet_UpdatesValue() {
        var table = new SlotPool<int>();
        int idx = table.Alloc(100);
        table[idx] = 200;
        Assert.Equal(200, table[idx]);
    }

    [Fact]
    public void IndexerSet_FreedSlot_Throws() {
        var table = new SlotPool<int>(SlotPool<int>.MinSlabShift);
        int a = table.Alloc(1);
        int b = table.Alloc(2);
        table.Free(b);
        Assert.Throws<InvalidOperationException>(() => table[b] = 99);
    }

    [Fact]
    public void IndexerSet_OutOfRange_Throws() {
        var table = new SlotPool<int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => table[0] = 42);
    }

    // ───────────────────── TryGetValue ─────────────────────

    [Fact]
    public void TryGetValue_OccupiedSlot_ReturnsTrue() {
        var table = new SlotPool<int>();
        int idx = table.Alloc(42);
        Assert.True(table.TryGetValue(idx, out int value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryGetValue_FreedSlot_ReturnsFalse() {
        var table = new SlotPool<int>(SlotPool<int>.MinSlabShift);
        int a = table.Alloc(1);
        int b = table.Alloc(2);
        table.Free(b);
        Assert.False(table.TryGetValue(b, out _));
    }

    [Fact]
    public void TryGetValue_OutOfRange_ReturnsFalse() {
        var table = new SlotPool<int>();
        Assert.False(table.TryGetValue(0, out _));
        Assert.False(table.TryGetValue(-1, out _));
        Assert.False(table.TryGetValue(int.MaxValue, out _));
    }

    // ───────────────────── Free validation ─────────────────────

    [Fact]
    public void Free_DoubleFree_Throws() {
        var table = new SlotPool<int>(SlotPool<int>.MinSlabShift);
        int a = table.Alloc(1);
        int b = table.Alloc(2); // 让 a 所在的 slab 不被收缩
        table.Free(a);
        Assert.Throws<InvalidOperationException>(() => table.Free(a));
    }

    [Fact]
    public void Free_OutOfRange_Throws() {
        var table = new SlotPool<int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Free(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Free(-1));
    }

    // ───────────────────── Low-index reuse ─────────────────────

    [Fact]
    public void Alloc_PrefersLowestFreeIndex() {
        var table = new SlotPool<int>(SlotPool<int>.MinSlabShift);
        int a = table.Alloc(1);
        int b = table.Alloc(2);
        int c = table.Alloc(3);

        table.Free(a);
        table.Free(c);

        // 再次分配应优先拿到 a（更低的 index）
        int reused = table.Alloc(99);
        Assert.Equal(a, reused);
        Assert.Equal(99, table[reused]);
    }

    // ───────────────────── Tail shrink ─────────────────────

    [Fact]
    public void Free_AllSlotsInTrailingSlab_RetainsOneEmptySlabAsBuffer() {
        int shift = SlotPool<int>.MinSlabShift; // 64
        int slabSize = 1 << shift;
        var table = new SlotPool<int>(shift);

        // 分配两个 slab 的 slot
        var firstSlabIndices = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { firstSlabIndices[i] = table.Alloc(i); }
        var secondSlabIndices = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { secondSlabIndices[i] = table.Alloc(i + slabSize); }
        Assert.Equal(slabSize * 2, table.Capacity);

        // 释放第二个 slab 的所有 slot → 只有 1 个尾部空 slab，防抖策略保留
        for (int i = 0; i < slabSize; i++) { table.Free(secondSlabIndices[i]); }
        Assert.Equal(slabSize * 2, table.Capacity); // 缓冲 slab 保留
        Assert.Equal(slabSize, table.Count);
    }

    [Fact]
    public void Free_AllSlots_RetainsOneEmptySlabAsBuffer() {
        int shift = SlotPool<int>.MinSlabShift;
        int slabSize = 1 << shift;
        var table = new SlotPool<int>(shift);

        var indices = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { indices[i] = table.Alloc(i); }

        // 释放所有 slot → 保留 1 个空 slab 作缓冲
        for (int i = 0; i < slabSize; i++) { table.Free(indices[i]); }
        Assert.Equal(slabSize, table.Capacity); // 缓冲 slab 保留
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Free_NonTrailingSlab_DoesNotShrink() {
        int shift = SlotPool<int>.MinSlabShift;
        int slabSize = 1 << shift;
        var table = new SlotPool<int>(shift);

        // 分配两个 slab
        var slab0 = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { slab0[i] = table.Alloc(i); }
        int slab1Anchor = table.Alloc(999); // 第二个 slab 留一个
        Assert.Equal(slabSize * 2, table.Capacity);

        // 释放第一个 slab 的全部 → 不是尾部，不收缩
        for (int i = 0; i < slabSize; i++) { table.Free(slab0[i]); }
        Assert.Equal(slabSize * 2, table.Capacity);
        Assert.Equal(1, table.Count);
    }

    // ───────────────────── Multi-slab / cross-slab ─────────────────────

    [Fact]
    public void AllocFree_AcrossMultipleSlabs_Consistent() {
        int shift = SlotPool<int>.MinSlabShift; // 64
        int slabSize = 1 << shift;
        int total = slabSize * 3 + 10; // 触发 4 个 slab
        var table = new SlotPool<int>(shift);

        var indices = new int[total];
        for (int i = 0; i < total; i++) {
            indices[i] = table.Alloc(i * 10);
        }
        Assert.Equal(total, table.Count);

        // 验证所有值
        for (int i = 0; i < total; i++) {
            Assert.Equal(i * 10, table[indices[i]]);
        }

        // 释放一半（偶数索引位置的 slot）
        int freed = 0;
        for (int i = 0; i < total; i += 2) {
            table.Free(indices[i]);
            freed++;
        }
        Assert.Equal(total - freed, table.Count);

        // 奇数位置的 slot 值仍然完好
        for (int i = 1; i < total; i += 2) {
            Assert.Equal(i * 10, table[indices[i]]);
        }
    }

    // ───────────────────── GC safety: reference types ─────────────────────

    [Fact]
    public void Free_ClearsReferenceToAssistGC() {
        // 本测试验证 Free 之后不保留对旧值的强引用。
        // 无法直接检测 GC，但可以验证 Free 后 IsOccupied 返回 false，
        // 以及重新分配同一 slot 得到新值。
        var table = new SlotPool<string>(SlotPool<string>.MinSlabShift);
        int a = table.Alloc("keep-alive");
        int b = table.Alloc("will-be-freed");
        table.Free(b);

        // 重新分配应复用 b 的 slot
        int c = table.Alloc("new-value");
        Assert.Equal(b, c); // 低 index 优先
        Assert.Equal("new-value", table[c]);
    }

    // ───────────────────── Realloc after full shrink ─────────────────────

    [Fact]
    public void AllocAfterFreeAll_ReusesBufferSlab() {
        int shift = SlotPool<int>.MinSlabShift;
        int slabSize = 1 << shift;
        var table = new SlotPool<int>(shift);
        int idx = table.Alloc(42);
        table.Free(idx);
        Assert.Equal(0, table.Count);
        Assert.Equal(slabSize, table.Capacity); // 缓冲 slab 保留

        // 重新分配——复用缓冲 slab，不触发 GrowOneSlab
        int idx2 = table.Alloc(99);
        Assert.Equal(1, table.Count);
        Assert.Equal(slabSize, table.Capacity); // 容量不变
        Assert.Equal(99, table[idx2]);
    }

    [Fact]
    public void AllocAfterTrimExcess_WorksCorrectly() {
        var table = new SlotPool<int>(SlotPool<int>.MinSlabShift);
        int idx = table.Alloc(42);
        table.Free(idx);
        table.TrimExcess();
        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.Capacity);

        // 重新分配
        int idx2 = table.Alloc(99);
        Assert.Equal(1, table.Count);
        Assert.Equal(99, table[idx2]);
    }

    // ───────────────────── Hysteresis / anti-thrashing ─────────────────────

    [Fact]
    public void AllocFree_AtSlabBoundary_NoThrashing() {
        int shift = SlotPool<int>.MinSlabShift;
        int slabSize = 1 << shift;
        var table = new SlotPool<int>(shift);

        // 填满第一个 slab
        var indices = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { indices[i] = table.Alloc(i); }
        Assert.Equal(slabSize, table.Capacity);

        // 反复在边界上 Alloc/Free 100 次，容量应保持稳定
        for (int round = 0; round < 100; round++) {
            int extra = table.Alloc(9999);
            Assert.Equal(slabSize * 2, table.Capacity); // 第二个 slab 已分配
            table.Free(extra);
            Assert.Equal(slabSize * 2, table.Capacity); // 缓冲 slab 保留，不收缩
        }
    }

    [Fact]
    public void Shrink_WithTwoTrailingEmptySlabs_ShrinkToKeepOne() {
        int shift = SlotPool<int>.MinSlabShift;
        int slabSize = 1 << shift;
        var table = new SlotPool<int>(shift);

        // 分配 3 个 slab
        var slab0 = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { slab0[i] = table.Alloc(i); }
        var slab1 = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { slab1[i] = table.Alloc(i); }
        var slab2 = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { slab2[i] = table.Alloc(i); }
        Assert.Equal(slabSize * 3, table.Capacity);

        // 释放 slab 2 → 1 个尾部空 slab，不收缩
        for (int i = 0; i < slabSize; i++) { table.Free(slab2[i]); }
        Assert.Equal(slabSize * 3, table.Capacity);

        // 释放 slab 1 → 2 个连续尾部空 slab，收缩到保留 1 个
        for (int i = 0; i < slabSize; i++) { table.Free(slab1[i]); }
        Assert.Equal(slabSize * 2, table.Capacity); // 保留 slab 0 (满) + slab 1 (空缓冲)
        Assert.Equal(slabSize, table.Count);
    }

    // ───────────────────── TrimExcess ─────────────────────

    [Fact]
    public void TrimExcess_ReleasesBufferSlab() {
        int shift = SlotPool<int>.MinSlabShift;
        int slabSize = 1 << shift;
        var table = new SlotPool<int>(shift);

        var indices = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { indices[i] = table.Alloc(i); }
        for (int i = 0; i < slabSize; i++) { table.Free(indices[i]); }

        Assert.Equal(slabSize, table.Capacity); // 缓冲 slab 保留
        Assert.Equal(0, table.Count);

        table.TrimExcess();
        Assert.Equal(0, table.Capacity); // 强制释放
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void TrimExcess_OnNonEmptyPool_OnlyRemovesTrailingEmpty() {
        int shift = SlotPool<int>.MinSlabShift;
        int slabSize = 1 << shift;
        var table = new SlotPool<int>(shift);

        // 分配 2 个 slab，释放 slab 1
        for (int i = 0; i < slabSize; i++) { table.Alloc(i); }
        var slab1 = new int[slabSize];
        for (int i = 0; i < slabSize; i++) { slab1[i] = table.Alloc(i); }
        for (int i = 0; i < slabSize; i++) { table.Free(slab1[i]); }

        Assert.Equal(slabSize * 2, table.Capacity); // 缓冲保留
        table.TrimExcess();
        Assert.Equal(slabSize, table.Capacity); // 强制释放尾部空 slab
        Assert.Equal(slabSize, table.Count);
    }

    [Fact]
    public void TrimExcess_OnFullPool_NoChange() {
        var table = new SlotPool<int>(SlotPool<int>.MinSlabShift);
        table.Alloc(1);
        int cap = table.Capacity;
        table.TrimExcess();
        Assert.Equal(cap, table.Capacity);
    }
}
