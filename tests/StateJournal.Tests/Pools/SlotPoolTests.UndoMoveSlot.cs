using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

partial class SlotPoolTests {
    // ───────────────────── MoveSlotRecorded ─────────────────────

    [Fact]
    public void MoveSlotRecorded_ReturnsCorrectRecord() {
        var pool = new SlotPool<string>();
        var h0 = pool.Store("A");
        var h1 = pool.Store("B");
        pool.Free(h0);

        var record = pool.MoveSlotRecorded(h1.Index, h0.Index);

        Assert.Equal(h1.Index, record.FromIndex);
        Assert.Equal(h0.Index, record.ToIndex);
        Assert.Equal(h1.Generation, record.FromGenBefore);
        // h0 was freed: gen = h0.Generation + 1
        Assert.Equal((byte)(h0.Generation + 1), record.ToGenBefore);
        Assert.Equal("B", pool[record.NewHandle]);
    }

    // ───────────────────── UndoMoveSlot ─────────────────────

    [Fact]
    public void UndoMoveSlot_RestoresValueAndGeneration() {
        var pool = new SlotPool<string>();
        var h0 = pool.Store("A");
        var h1 = pool.Store("B");
        pool.Store("anchor"); // prevent tail shrink
        pool.Free(h0);

        var record = pool.MoveSlotRecorded(h1.Index, h0.Index);
        // Now: value at index 0 = "B", index 1 = free

        pool.UndoMoveSlot(record);

        // Value should be back at original index
        Assert.True(pool.Validate(h1), "Original handle h1 should be valid after undo");
        Assert.Equal("B", pool[h1]);
        Assert.Equal(2, pool.Count); // anchor + restored
    }

    [Fact]
    public void UndoMoveSlot_RestoresExactGenerations() {
        var pool = new SlotPool<string>();
        var h0 = pool.Store("A"); // gen at index 0 = h0.Generation
        var h1 = pool.Store("B"); // gen at index 1 = h1.Generation
        pool.Store("anchor");
        pool.Free(h0); // gen at index 0 becomes h0.Generation + 1

        var record = pool.MoveSlotRecorded(h1.Index, h0.Index);
        // After move: gen at index 0 = genAt0BeforeMove + 1, gen at index 1 = genAt1BeforeMove + 1

        pool.UndoMoveSlot(record);

        // h1 at original position with exact original generation is valid
        Assert.True(pool.Validate(h1), "h1 should be valid with original generation after undo");
        Assert.Equal(h1.Generation, pool.GetHandle(h1.Index).Generation);

        // index 0 should be free (was freed before the move) — the freed-gen is restored
        Assert.False(pool.Validate(h0), "h0 should still be invalid (it was freed before the move)");
    }

    [Fact]
    public void UndoMoveSlot_MultipleMoves_ReverseOrderRestores() {
        var pool = new SlotPool<string>();
        var h0 = pool.Store("A");
        var h1 = pool.Store("B");
        var h2 = pool.Store("C");
        var h3 = pool.Store("D");
        pool.Store("anchor");

        // Free h0 and h2 to create two holes
        pool.Free(h0);
        pool.Free(h2);

        // Move h3 → h0, then h1 → h2 wouldn't work since h1 is occupied at index1.
        // Let's do: move h3 (idx3) → hole h0 (idx0)
        var rec1 = pool.MoveSlotRecorded(h3.Index, h0.Index);
        // Now: idx0=D, idx1=B, idx2=free, idx3=free
        // Move h1 (idx1) → hole h2 (idx2)
        var rec2 = pool.MoveSlotRecorded(h1.Index, h2.Index);
        // Now: idx0=D, idx1=free, idx2=B, idx3=free

        // Undo in reverse order
        pool.UndoMoveSlot(rec2);
        pool.UndoMoveSlot(rec1);

        // All original handles should be valid and values correct
        Assert.True(pool.Validate(h0) == false); // h0 was freed, stays free
        Assert.True(pool.Validate(h1), "h1 should be restored");
        Assert.Equal("B", pool[h1]);
        Assert.True(pool.Validate(h2) == false); // h2 was freed, stays free
        Assert.True(pool.Validate(h3), "h3 should be restored");
        Assert.Equal("D", pool[h3]);
    }

    [Fact]
    public void UndoMoveSlot_RestoresGenerationsAcrossByteWrap_ForReservedSlotZero() {
        var pool = new SlotPool<string>();
        var h0 = pool.Store("target");
        var h1 = pool.Store("source");
        pool.Store("anchor");

        // Bump source handle generation to 255 while keeping it occupied at index 1.
        for (int i = 0; i < 255; i++) {
            pool.Free(h1);
            h1 = pool.Store("source");
        }
        Assert.Equal(255, h1.Generation);

        // Slot 0 是特例：每次重新分配时都会跳过 generation 0，因此占用态 handle 始终从 1 起步。
        for (int i = 0; i < 255; i++) {
            pool.Free(h0);
            h0 = pool.Store("target");
        }
        Assert.Equal(1, h0.Generation);
        pool.Free(h0); // free 后 generation 从 1 递增到 2

        var record = pool.MoveSlotRecorded(h1.Index, h0.Index);
        Assert.Equal((byte)255, record.FromGenBefore);
        Assert.Equal((byte)2, record.ToGenBefore);

        pool.UndoMoveSlot(record);

        // Source handle should be fully restored at generation 255.
        Assert.True(pool.Validate(h1));
        Assert.Equal("source", pool[h1]);

        // Target slot 仍保持 free，旧 handle 继续失效。
        Assert.False(pool.Validate(h0));
    }

    [Fact]
    public void UndoMoveSlot_RestoresGenerationsAcrossByteWrap_ForOrdinarySlot() {
        var pool = new SlotPool<string>();
        pool.Store("reserved-slot-zero");
        var h1 = pool.Store("target");
        var h2 = pool.Store("source");
        pool.Store("anchor");

        // Bump source handle generation to 255 while keeping it occupied at index 2.
        for (int i = 0; i < 255; i++) {
            pool.Free(h2);
            h2 = pool.Store("source");
        }
        Assert.Equal(255, h2.Generation);

        // 普通 slot 不跳过 generation 0：255 再 free 一次后回绕到 0。
        for (int i = 0; i < 255; i++) {
            pool.Free(h1);
            h1 = pool.Store("target");
        }
        Assert.Equal(255, h1.Generation);
        pool.Free(h1); // generation wraps: 255 -> 0

        var record = pool.MoveSlotRecorded(h2.Index, h1.Index);
        Assert.Equal((byte)255, record.FromGenBefore);
        Assert.Equal((byte)0, record.ToGenBefore);

        pool.UndoMoveSlot(record);

        Assert.True(pool.Validate(h2));
        Assert.Equal("source", pool[h2]);
        Assert.False(pool.Validate(h1));
    }

    // ───────────────────── EnsureCapacity ─────────────────────

    [Fact]
    public void EnsureCapacity_GrowsWhenNeeded() {
        var pool = new SlotPool<string>();
        Assert.Equal(0, pool.Capacity);

        pool.EnsureCapacity(100);

        Assert.True(pool.Capacity >= 100);
    }

    [Fact]
    public void EnsureCapacity_NoOpWhenAlreadySufficient() {
        var pool = new SlotPool<string>();
        pool.Store("A"); // triggers at least 1 slab
        int capBefore = pool.Capacity;

        pool.EnsureCapacity(capBefore);

        Assert.Equal(capBefore, pool.Capacity);
    }
}
