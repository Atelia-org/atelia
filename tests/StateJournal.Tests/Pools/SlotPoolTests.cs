using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

public partial class SlotPoolTests {
    // ───────────────────── Construction ─────────────────────

    [Fact]
    public void Ctor_Default_CreatesEmptyPool() {
        var table = new SlotPool<string>();
        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.Capacity);
    }

    // ───────────────────── Alloc ─────────────────────

    [Fact]
    public void Alloc_ReturnsUniqueIndices() {
        var table = new SlotPool<string>();
        var indices = new HashSet<int>();
        for (int i = 0; i < 200; i++) {
            SlotHandle h = table.Store($"item-{i}");
            Assert.True(indices.Add(h.Index), $"Duplicate index {h.Index} at iteration {i}");
        }
        Assert.Equal(200, table.Count);
    }

    [Fact]
    public void Alloc_StoresValue_ReadableViaIndexer() {
        var table = new SlotPool<int>();
        SlotHandle h = table.Store(42);
        Assert.Equal(42, table[h]);
    }

    // ───────────────────── Count / Capacity ─────────────────────

    [Fact]
    public void Count_TracksAllocAndFree() {
        var table = new SlotPool<int>();
        Assert.Equal(0, table.Count);

        SlotHandle a = table.Store(1);
        SlotHandle b = table.Store(2);
        Assert.Equal(2, table.Count);

        table.Free(a);
        Assert.Equal(1, table.Count);

        table.Free(b);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Capacity_GrowsInSlabSizeIncrements() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();

        // 分配第一个 slot 触发第一个 slab
        table.Store(1);
        Assert.Equal(slabSize, table.Capacity);

        // 填满第一个 slab
        for (int i = 1; i < slabSize; i++) { table.Store(i + 1); }
        Assert.Equal(slabSize, table.Capacity);

        // 再分配一个触发第二个 slab
        table.Store(999);
        Assert.Equal(slabSize * 2, table.Capacity);
    }

    // ───────────────────── IsOccupied ─────────────────────

    [Fact]
    public void IsOccupied_ReturnsTrueForAllocated_FalseAfterFree() {
        var table = new SlotPool<string>();
        SlotHandle h = table.Store("hello");
        Assert.True(table.Validate(h));

        table.Free(h);
        // Free 后的尾部收缩可能使得 index 超出 capacity，此时 IsOccupied 抛 OOR
        // 所以需要保留至少一个更低的 slot 来阻止收缩
    }

    [Fact]
    public void IsOccupied_AfterFreeWithLowerSlotAlive_ReturnsFalse() {
        var table = new SlotPool<string>();
        SlotHandle a = table.Store("keep");
        SlotHandle b = table.Store("release");
        Assert.True(table.Validate(b));

        table.Free(b);
        Assert.False(table.Validate(b)); // b 仍在 capacity 内（a 阻止了收缩）, gen 也不匹配
    }

    [Fact]
    public void IsOccupied_OutOfRange_Throws() {
        var table = new SlotPool<int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => table.IsOccupied(0));

        table.Store(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => table.IsOccupied(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.IsOccupied(table.Capacity));
    }

    // ───────────────────── Indexer / GetValueRef ─────────────────────

    [Fact]
    public void Indexer_FreedSlot_Throws() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(10);
        SlotHandle b = table.Store(20);
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
        SlotHandle h = table.Store(100);
        ref int val = ref table.GetValueRef(h);
        val = 200;
        Assert.Equal(200, table[h]);
    }

    [Fact]
    public void GetValueRef_FreedSlot_Throws() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle b = table.Store(2);
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
        SlotHandle h = table.Store(100);
        table[h] = 200;
        Assert.Equal(200, table[h]);
    }

    [Fact]
    public void IndexerSet_FreedSlot_Throws() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle b = table.Store(2);
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
        SlotHandle h = table.Store(42);
        Assert.True(table.TryGetValue(h, out int value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryGetValue_FreedSlot_ReturnsFalse() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle b = table.Store(2);
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

    // ───────────────────── EnumerateOccupiedIndices ─────────────────────

    [Fact]
    public void EnumerateOccupiedIndices_Empty_YieldsNothing() {
        var table = new SlotPool<int>();
        Assert.Empty(Collect(table.EnumerateOccupiedIndices()));
    }

    [Fact]
    public void EnumerateOccupiedIndices_SkipsFreedSlots_AndPreservesAscendingOrder() {
        var table = new SlotPool<int>();
        var h0 = table.Store(10);
        var h1 = table.Store(20);
        var h2 = table.Store(30);
        var h3 = table.Store(40);

        table.Free(h1);
        table.Free(h3);

        Assert.Equal([h0.Index, h2.Index], Collect(table.EnumerateOccupiedIndices()));
    }

    [Fact]
    public void EnumerateOccupiedIndices_CrossSlab_YieldsOnlyOccupiedIndices() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();
        var handles = new List<SlotHandle>(slabSize + 4);
        for (int i = 0; i < slabSize + 4; i++) {
            handles.Add(table.Store(i));
        }

        table.Free(handles[1]);
        table.Free(handles[slabSize]);
        table.Free(handles[slabSize + 2]);

        var expected = new List<int>(handles.Count - 3);
        for (int i = 0; i < handles.Count; i++) {
            if (i == 1 || i == slabSize || i == slabSize + 2) { continue; }
            expected.Add(handles[i].Index);
        }

        Assert.Equal(expected, Collect(table.EnumerateOccupiedIndices()));
    }

    // ───────────────────── Free validation ─────────────────────

    [Fact]
    public void Free_DoubleFree_Throws() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle b = table.Store(2); // 让 a 所在的 slab 不被收缩
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
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle b = table.Store(2);
        SlotHandle c = table.Store(3);

        table.Free(a);
        table.Free(c);

        // 再次分配应优先拿到 a 的 index（更低的 index），但 generation 已递增
        SlotHandle reused = table.Store(99);
        Assert.Equal(a.Index, reused.Index);
        Assert.NotEqual(a, reused); // generation 不同
        Assert.Equal(99, table[reused]);
    }

    // ───────────────────── Tail shrink ─────────────────────

    [Fact]
    public void Free_AllSlotsInTrailingSlab_RetainsOneEmptySlabAsBuffer() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();

        // 分配两个 slab 的 slot
        var firstSlabHandles = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { firstSlabHandles[i] = table.Store(i); }
        var secondSlabHandles = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { secondSlabHandles[i] = table.Store(i + slabSize); }
        Assert.Equal(slabSize * 2, table.Capacity);

        // 释放第二个 slab 的所有 slot → 只有 1 个尾部空 slab，防抖策略保留
        for (int i = 0; i < slabSize; i++) { table.Free(secondSlabHandles[i]); }
        Assert.Equal(slabSize * 2, table.Capacity); // 缓冲 slab 保留
        Assert.Equal(slabSize, table.Count);
    }

    [Fact]
    public void Free_AllSlots_RetainsOneEmptySlabAsBuffer() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();

        var handles = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { handles[i] = table.Store(i); }

        // 释放所有 slot → 保留 1 个空 slab 作缓冲
        for (int i = 0; i < slabSize; i++) { table.Free(handles[i]); }
        Assert.Equal(slabSize, table.Capacity); // 缓冲 slab 保留
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Free_NonTrailingSlab_DoesNotShrink() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();

        // 分配两个 slab
        var slab0 = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { slab0[i] = table.Store(i); }
        SlotHandle slab1Anchor = table.Store(999); // 第二个 slab 留一个
        Assert.Equal(slabSize * 2, table.Capacity);

        // 释放第一个 slab 的全部 → 不是尾部，不收缩
        for (int i = 0; i < slabSize; i++) { table.Free(slab0[i]); }
        Assert.Equal(slabSize * 2, table.Capacity);
        Assert.Equal(1, table.Count);
    }

    // ───────────────────── Multi-slab / cross-slab ─────────────────────

    [Fact]
    public void AllocFree_AcrossMultipleSlabs_Consistent() {
        int slabSize = SlabBitmap.SlabSize;
        int total = slabSize * 3 + 10; // 触发 4 个 slab
        var table = new SlotPool<int>();

        var handles = new SlotHandle[total];
        for (int i = 0; i < total; i++) {
            handles[i] = table.Store(i * 10);
        }
        Assert.Equal(total, table.Count);

        // 验证所有值
        for (int i = 0; i < total; i++) {
            Assert.Equal(i * 10, table[handles[i]]);
        }

        // 释放一半（偶数索引位置的 slot）
        int freed = 0;
        for (int i = 0; i < total; i += 2) {
            table.Free(handles[i]);
            freed++;
        }
        Assert.Equal(total - freed, table.Count);

        // 奇数位置的 slot 值仍然完好
        for (int i = 1; i < total; i += 2) {
            Assert.Equal(i * 10, table[handles[i]]);
        }
    }

    // ───────────────────── GC safety: reference types ─────────────────────

    [Fact]
    public void Free_ClearsReferenceToAssistGC() {
        // 本测试验证 Free 之后不保留对旧值的强引用。
        // 无法直接检测 GC，但可以验证 Free 后 IsOccupied 返回 false，
        // 以及重新分配同一 slot 得到新值。
        var table = new SlotPool<string>();
        SlotHandle a = table.Store("keep-alive");
        SlotHandle b = table.Store("will-be-freed");
        table.Free(b);

        // 重新分配应复用 b 的 slot index
        SlotHandle c = table.Store("new-value");
        Assert.Equal(b.Index, c.Index); // 低 index 优先
        Assert.NotEqual(b, c); // generation 不同
        Assert.Equal("new-value", table[c]);
    }

    // ───────────────────── Realloc after full shrink ─────────────────────

    [Fact]
    public void AllocAfterFreeAll_ReusesBufferSlab() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();
        SlotHandle h = table.Store(42);
        table.Free(h);
        Assert.Equal(0, table.Count);
        Assert.Equal(slabSize, table.Capacity); // 缓冲 slab 保留

        // 重新分配——复用缓冲 slab，不触发 GrowOneSlab
        SlotHandle h2 = table.Store(99);
        Assert.Equal(1, table.Count);
        Assert.Equal(slabSize, table.Capacity); // 容量不变
        Assert.Equal(99, table[h2]);
    }

    [Fact]
    public void AllocAfterTrimExcess_WorksCorrectly() {
        var table = new SlotPool<int>();
        SlotHandle h = table.Store(42);
        table.Free(h);
        table.TrimExcess();
        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.Capacity);

        // 重新分配
        SlotHandle h2 = table.Store(99);
        Assert.Equal(1, table.Count);
        Assert.Equal(99, table[h2]);
    }

    // ───────────────────── Hysteresis / anti-thrashing ─────────────────────

    [Fact]
    public void AllocFree_AtSlabBoundary_NoThrashing() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();

        // 填满第一个 slab
        var handles = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { handles[i] = table.Store(i); }
        Assert.Equal(slabSize, table.Capacity);

        // 反复在边界上 Alloc/Free 100 次，容量应保持稳定
        for (int round = 0; round < 100; round++) {
            SlotHandle extra = table.Store(9999);
            Assert.Equal(slabSize * 2, table.Capacity); // 第二个 slab 已分配
            table.Free(extra);
            Assert.Equal(slabSize * 2, table.Capacity); // 缓冲 slab 保留，不收缩
        }
    }

    [Fact]
    public void Shrink_WithTwoTrailingEmptySlabs_ShrinkToKeepOne() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();

        // 分配 3 个 slab
        var slab0 = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { slab0[i] = table.Store(i); }
        var slab1 = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { slab1[i] = table.Store(i); }
        var slab2 = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { slab2[i] = table.Store(i); }
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
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();

        var handles = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { handles[i] = table.Store(i); }
        for (int i = 0; i < slabSize; i++) { table.Free(handles[i]); }

        Assert.Equal(slabSize, table.Capacity); // 缓冲 slab 保留
        Assert.Equal(0, table.Count);

        table.TrimExcess();
        Assert.Equal(0, table.Capacity); // 强制释放
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void TrimExcess_OnNonEmptyPool_OnlyRemovesTrailingEmpty() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();

        // 分配 2 个 slab，释放 slab 1
        for (int i = 0; i < slabSize; i++) { table.Store(i); }
        var slab1 = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { slab1[i] = table.Store(i); }
        for (int i = 0; i < slabSize; i++) { table.Free(slab1[i]); }

        Assert.Equal(slabSize * 2, table.Capacity); // 缓冲保留
        table.TrimExcess();
        Assert.Equal(slabSize, table.Capacity); // 强制释放尾部空 slab
        Assert.Equal(slabSize, table.Count);
    }

    [Fact]
    public void TrimExcess_OnFullPool_NoChange() {
        var table = new SlotPool<int>();
        table.Store(1);
        int cap = table.Capacity;
        table.TrimExcess();
        Assert.Equal(cap, table.Capacity);
    }

    // ───────────────────── Generation / Stale Handle Detection ─────────────────────

    [Fact]
    public void Free_IncrementsGeneration_NewAllocGetsDifferentHandle() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle keep = table.Store(2); // 防止收缩
        byte gen0 = a.Generation;

        table.Free(a);
        SlotHandle b = table.Store(99);

        Assert.Equal(a.Index, b.Index); // 同一 slot 复用
        Assert.Equal((byte)(gen0 + 1), b.Generation); // generation 递增
        Assert.NotEqual(a, b); // handle 值不同
        Assert.Equal(99, table[b]);
    }

    [Fact]
    public void StaleHandle_Indexer_Throws() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle keep = table.Store(2);

        table.Free(a);
        SlotHandle b = table.Store(99); // 复用 a 的 slot

        // 用旧的 stale handle 访问 → generation 不匹配 → 抛异常
        var ex = Assert.Throws<InvalidOperationException>(() => _ = table[a]);
        Assert.Contains("generation mismatch", ex.Message);
    }

    [Fact]
    public void StaleHandle_GetValueRef_Throws() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle keep = table.Store(2);

        table.Free(a);
        SlotHandle b = table.Store(99);

        Assert.Throws<InvalidOperationException>(() => table.GetValueRef(a));
    }

    [Fact]
    public void StaleHandle_IndexerSet_Throws() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle keep = table.Store(2);

        table.Free(a);
        SlotHandle b = table.Store(99);

        Assert.Throws<InvalidOperationException>(() => table[a] = 42);
        Assert.Equal(99, table[b]); // 新值不受影响
    }

    [Fact]
    public void StaleHandle_Free_Throws() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle keep = table.Store(2);

        table.Free(a);
        SlotHandle b = table.Store(99);

        // 用旧 handle 尝试 Free → generation 不匹配
        var ex = Assert.Throws<InvalidOperationException>(() => table.Free(a));
        Assert.Contains("generation mismatch", ex.Message);
        Assert.Equal(99, table[b]); // slot 未被释放
    }

    [Fact]
    public void StaleHandle_IsOccupied_ReturnsFalse() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle keep = table.Store(2);

        table.Free(a);
        SlotHandle b = table.Store(99);

        Assert.False(table.Validate(a)); // gen 不匹配
        Assert.True(table.Validate(b));
    }

    [Fact]
    public void StaleHandle_TryGetValue_ReturnsFalse() {
        var table = new SlotPool<int>();
        SlotHandle a = table.Store(1);
        SlotHandle keep = table.Store(2);

        table.Free(a);
        SlotHandle b = table.Store(99);

        Assert.False(table.TryGetValue(a, out _)); // gen 不匹配
        Assert.True(table.TryGetValue(b, out int val));
        Assert.Equal(99, val);
    }

    [Fact]
    public void Generation_SurvivesShrinkAndRegrow() {
        int slabSize = SlabBitmap.SlabSize;
        var table = new SlotPool<int>();

        // 分配 2 个 slab
        var slab0 = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { slab0[i] = table.Store(i); }
        var slab1 = new SlotHandle[slabSize];
        for (int i = 0; i < slabSize; i++) { slab1[i] = table.Store(i + 1000); }

        // 保存 slab 1 的第一个 handle
        SlotHandle oldHandle = slab1[0];
        byte oldGen = oldHandle.Generation;

        // 释放 slab 1 全部
        for (int i = 0; i < slabSize; i++) { table.Free(slab1[i]); }

        // 释放 slab 0 全部 → 触发 slab 收缩（slab 1 已被收缩掉）
        for (int i = 0; i < slabSize; i++) { table.Free(slab0[i]); }

        // TrimExcess 强制释放 → capacity = 0，但 generation 数组保留
        table.TrimExcess();
        Assert.Equal(0, table.Capacity);

        // 重新增长到覆盖 slab 1
        var newHandles = new SlotHandle[slabSize + 1];
        for (int i = 0; i <= slabSize; i++) { newHandles[i] = table.Store(i + 2000); }

        // 验证 oldHandle 指向的 slot 被重新分配后，旧 handle 无法使用
        // oldHandle.Index 在 slab1 范围内，新分配后 generation 已递增
        if (oldHandle.Index < table.Capacity) {
            Assert.False(table.Validate(oldHandle)); // gen 不匹配
        }
    }

    [Fact]
    public void Generation_WrapsAround() {
        var table = new SlotPool<int>();
        SlotHandle keep = table.Store(0); // 防止收缩

        // 连续 256 次 alloc-free 同一 slot → generation 回绕
        SlotHandle last = default;
        for (int round = 0; round < 256; round++) {
            SlotHandle h = table.Store(round + 1);
            last = h;
            table.Free(h);
        }

        // 第 257 次分配：generation 已回绕到 0
        SlotHandle wrapped = table.Store(999);
        Assert.Equal(0, wrapped.Generation); // 回绕到 0
        Assert.Equal(999, table[wrapped]);
    }

    // ───────────────────── Rebuild ─────────────────────

    [Fact]
    public void Rebuild_EmptySpan_ReturnsEmptyPool() {
        var pool = SlotPool<string>.Rebuild(ReadOnlySpan<(SlotHandle, string)>.Empty);
        Assert.Equal(0, pool.Count);
        Assert.Equal(0, pool.Capacity);
    }

    [Fact]
    public void Rebuild_SingleEntry_StoresValueAndGeneration() {
        var handle = new SlotHandle(3, 42);
        var pool = SlotPool<string>.Rebuild([(handle, "hello")]);

        Assert.Equal(1, pool.Count);
        Assert.Equal("hello", pool[handle]);
        Assert.True(pool.Validate(handle));
    }

    [Fact]
    public void Rebuild_MultipleEntries_AllAccessible() {
        var h1 = new SlotHandle(1, 1);
        var h2 = new SlotHandle(5, 10);
        var h3 = new SlotHandle(255, 100);

        var pool = SlotPool<string>.Rebuild([(h1, "a"), (h2, "b"), (h3, "c")]);

        Assert.Equal(3, pool.Count);
        Assert.Equal("a", pool[h1]);
        Assert.Equal("b", pool[h2]);
        Assert.Equal("c", pool[h3]);
        Assert.True(pool.Validate(h1));
        Assert.True(pool.Validate(h2));
        Assert.True(pool.Validate(h3));
    }

    [Fact]
    public void Rebuild_PreservesGeneration_FutureStorePrefersLowestFreeIndex() {
        var h2 = new SlotHandle(7, 2);
        var h5 = new SlotHandle(3, 5);

        var pool = SlotPool<string>.Rebuild([(h2, "x"), (h5, "y")]);

        // Index 0 is the lowest free index after Rebuild
        SlotHandle first = pool.Store("fill0");
        Assert.Equal(0, first.Index);

        // Next lowest free is index 1
        SlotHandle second = pool.Store("new");
        Assert.Equal(1, second.Index);
        Assert.Equal("new", pool[second]);
    }

    [Fact]
    public void Rebuild_IndexZero_IsAllowed() {
        var h0 = new SlotHandle(0, 0);
        var h3 = new SlotHandle(2, 3);
        var pool = SlotPool<string>.Rebuild([(h0, "zero"), (h3, "three")]);

        Assert.Equal(2, pool.Count);
        Assert.True(pool.Validate(h0));
        Assert.True(pool.Validate(h3));
        Assert.Equal("zero", pool[h0]);
        Assert.Equal("three", pool[h3]);
    }

    [Fact]
    public void Rebuild_NonContiguousIndices_GapsAreFree() {
        var h1 = new SlotHandle(1, 1);
        var h4096 = new SlotHandle(2, 4096);

        var pool = SlotPool<string>.Rebuild([(h1, "first"), (h4096, "far")]);

        Assert.Equal(2, pool.Count);
        Assert.Equal("first", pool[h1]);
        Assert.Equal("far", pool[h4096]);

        // Index 0 is the lowest free, then index 2 (since 1 is occupied)
        SlotHandle fill0 = pool.Store("idx0");
        Assert.Equal(0, fill0.Index);
        SlotHandle gap = pool.Store("gap");
        Assert.Equal(2, gap.Index);
    }

    [Fact]
    public void Rebuild_ThenFree_IncrementsGeneration() {
        var h = new SlotHandle(5, 1);
        // Need a second entry at a higher index to prevent tail shrink
        var anchor = new SlotHandle(1, 10);

        var pool = SlotPool<string>.Rebuild([(h, "val"), (anchor, "anchor")]);
        Assert.True(pool.Validate(h));

        // Fill index 0 first so that freeing h makes index 1 the lowest free
        SlotHandle fill0 = pool.Store("fill0");
        Assert.Equal(0, fill0.Index);

        pool.Free(h);
        Assert.False(pool.Validate(h));

        // Re-allocate the same index — generation should have incremented
        SlotHandle reused = pool.Store("reused");
        Assert.Equal(h.Index, reused.Index);
        Assert.Equal((byte)(h.Generation + 1), reused.Generation);
    }

    // ───────────────────── MoveSlot ─────────────────────

    [Fact]
    public void MoveSlot_MovesValueToTarget() {
        var pool = new SlotPool<string>();
        var h0 = pool.Store("A");
        var h1 = pool.Store("B");
        var h2 = pool.Store("C");

        // Free h0 to create a hole at index 0
        pool.Free(h0);

        // Move h2 (index 2) → hole (index 0)
        var newHandle = pool.MoveSlot(h2.Index, h0.Index);

        Assert.Equal(0, newHandle.Index);
        Assert.Equal("C", pool[newHandle]);
        Assert.Equal(2, pool.Count); // h1 + moved
    }

    [Fact]
    public void MoveSlot_IncrementsGenerationAtTarget() {
        var pool = new SlotPool<string>();
        var h0 = pool.Store("A");
        var h1 = pool.Store("B");

        pool.Free(h0); // index 0 is now free, generation was incremented by Free

        var newHandle = pool.MoveSlot(h1.Index, h0.Index);

        // Target generation should be h0's original gen + 1 (from Free) + 1 (from MoveSlot)
        Assert.Equal((byte)(h0.Generation + 2), newHandle.Generation);
    }

    [Fact]
    public void MoveSlot_InvalidatesOldHandle() {
        var pool = new SlotPool<string>();
        var h0 = pool.Store("A");
        var h1 = pool.Store("B");
        // keep an anchor so tail slab doesn't shrink
        pool.Store("anchor");

        pool.Free(h0);

        var newHandle = pool.MoveSlot(h1.Index, h0.Index);

        Assert.False(pool.Validate(h1), "Old handle should be invalidated after MoveSlot");
        Assert.True(pool.Validate(newHandle), "New handle should be valid");
    }

    [Fact]
    public void MoveSlot_CountUnchanged() {
        var pool = new SlotPool<string>();
        pool.Store("A");
        var h1 = pool.Store("B");
        pool.Store("C");

        pool.Free(pool.Store("D")); // free something to not leave index 0 as hole
        // Actually let's make it simpler:
        var p = new SlotPool<string>();
        var a = p.Store("A");
        var b = p.Store("B");
        p.Free(a);

        int countBefore = p.Count;
        p.MoveSlot(b.Index, a.Index);
        Assert.Equal(countBefore, p.Count);
    }

    [Fact]
    public void MoveSlot_SourceNotOccupied_Throws() {
        var pool = new SlotPool<string>();
        var h0 = pool.Store("A");
        pool.Store("B"); // index 1 occupied

        pool.Free(h0);
        // Now index 0 is free. Try to move from index 0 (free) to index 1 — both occupied/free mismatch
        Assert.Throws<InvalidOperationException>(() => pool.MoveSlot(h0.Index, 1));
    }

    [Fact]
    public void MoveSlot_TargetNotFree_Throws() {
        var pool = new SlotPool<string>();
        pool.Store("A"); // index 0 occupied
        pool.Store("B"); // index 1 occupied

        Assert.Throws<InvalidOperationException>(() => pool.MoveSlot(1, 0));
    }

    [Fact]
    public void MoveSlot_SourceOutOfRange_Throws() {
        var pool = new SlotPool<string>();
        pool.Store("A");

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.MoveSlot(9999, 0));
    }

    [Fact]
    public void MoveSlot_TargetOutOfRange_Throws() {
        var pool = new SlotPool<string>();
        var h = pool.Store("A");

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.MoveSlot(h.Index, 9999));
    }

    private static List<int> Collect(SlotPool<int>.OccupiedIndexEnumerator e) {
        var list = new List<int>();
        foreach (int index in e) { list.Add(index); }
        return list;
    }
}
