using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

// ai:facade `src/StateJournal/Pools/SlabBitmap.cs`
// ai:impl `src/StateJournal/Pools/SlabBitmap.Enumerator.cs`
partial class SlabBitmapTests {
    // ───────────────────── EnumerateOnes (Forward) ─────────────────────

    [Fact]
    public void EnumerateOnes_Empty_YieldsNothing() {
        var bm = new SlabBitmap(Shift);
        var list = Collect(bm.EnumerateOnes());
        Assert.Empty(list);
    }

    [Fact]
    public void EnumerateOnes_NoBitsSet_YieldsNothing() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        var list = Collect(bm.EnumerateOnes());
        Assert.Empty(list);
    }

    [Fact]
    public void EnumerateOnes_SingleBit() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        bm.Set(17);
        Assert.Equal([17], Collect(bm.EnumerateOnes()));
    }

    [Fact]
    public void EnumerateOnes_MultipleBits_AscendingOrder() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        bm.Set(63);
        bm.Set(0);
        bm.Set(31);
        Assert.Equal([0, 31, 63], Collect(bm.EnumerateOnes()));
    }

    [Fact]
    public void EnumerateOnes_AllBitsSet() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        var list = Collect(bm.EnumerateOnes());
        Assert.Equal(SlabSize, list.Count);
        for (int i = 0; i < SlabSize; i++) { Assert.Equal(i, list[i]); }
    }

    [Fact]
    public void EnumerateOnes_MultiSlab_SkipsEmptySlab() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();       // slab 0: empty
        bm.GrowSlabAllZero();       // slab 1: empty
        bm.GrowSlabAllZero();       // slab 2: has bits
        bm.Set(SlabSize * 2 + 5);
        bm.Set(SlabSize * 2 + 60);
        Assert.Equal([SlabSize * 2 + 5, SlabSize * 2 + 60], Collect(bm.EnumerateOnes()));
    }

    [Fact]
    public void EnumerateOnes_MultiSlab_SparseBits() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        bm.Set(0);
        bm.Set(SlabSize + SlabSize - 1);
        bm.Set(SlabSize * 2 + 32);
        Assert.Equal([0, SlabSize * 2 - 1, SlabSize * 2 + 32], Collect(bm.EnumerateOnes()));
    }

    // ───────────────────── EnumerateZerosReverse ─────────────────────

    [Fact]
    public void EnumerateZerosReverse_Empty_YieldsNothing() {
        var bm = new SlabBitmap(Shift);
        var list = Collect(bm.EnumerateZerosReverse());
        Assert.Empty(list);
    }

    [Fact]
    public void EnumerateZerosReverse_AllOnes_YieldsNothing() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        Assert.Empty(Collect(bm.EnumerateZerosReverse()));
    }

    [Fact]
    public void EnumerateZerosReverse_SingleZero() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.Clear(17);
        Assert.Equal([17], Collect(bm.EnumerateZerosReverse()));
    }

    [Fact]
    public void EnumerateZerosReverse_MultipleZeros_DescendingOrder() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.Clear(63);
        bm.Clear(0);
        bm.Clear(31);
        Assert.Equal([63, 31, 0], Collect(bm.EnumerateZerosReverse()));
    }

    [Fact]
    public void EnumerateZerosReverse_AllZeros() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        var list = Collect(bm.EnumerateZerosReverse());
        Assert.Equal(SlabSize, list.Count);
        for (int i = 0; i < SlabSize; i++) { Assert.Equal(SlabSize - 1 - i, list[i]); }
    }

    [Fact]
    public void EnumerateZerosReverse_MultiSlab_SkipsFullSlab() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();       // slab 0: has zeros
        bm.GrowSlabAllOne();       // slab 1: full (skip)
        bm.GrowSlabAllOne();       // slab 2: full (skip)
        bm.Clear(5);
        bm.Clear(60);
        Assert.Equal([60, 5], Collect(bm.EnumerateZerosReverse()));
    }

    [Fact]
    public void EnumerateZerosReverse_MultiSlab_SparseZeros() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.GrowSlabAllOne();
        bm.GrowSlabAllOne();
        bm.Clear(0);
        bm.Clear(SlabSize * 2 - 1);
        bm.Clear(SlabSize * 2 + 32);
        Assert.Equal([SlabSize * 2 + 32, SlabSize * 2 - 1, 0], Collect(bm.EnumerateZerosReverse()));
    }

    // ───────────────────── EnumerateOnes / ZerosReverse complementarity ─────────────────────

    [Fact]
    public void EnumerateOnes_And_ZerosReverse_CoverAllIndices() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        // Set scattered bits across slabs
        int[] indices = [0, 1, 30, 31, 32, 33, 62, 63, SlabSize, SlabSize + 1, SlabSize * 2 + 63];
        foreach (int i in indices) { bm.Set(i); }

        var ones = Collect(bm.EnumerateOnes());
        var zeros = Collect(bm.EnumerateZerosReverse());

        Assert.Equal(indices.Length, ones.Count);
        Assert.Equal(SlabSize * 3 - indices.Length, zeros.Count);

        // Union should cover all indices
        var all = new HashSet<int>(ones);
        all.UnionWith(zeros);
        Assert.Equal(SlabSize * 3, all.Count);
    }

    [Theory]
    [InlineData(7)]   // slabShift = 7 → 128 bits/slab, 2 words/slab
    [InlineData(8)]   // slabShift = 8 → 256 bits/slab, 4 words/slab
    public void Enumerate_LargerSlabShift_Works(int shift) {
        int slabSize = 1 << shift;
        var bm = new SlabBitmap(shift);
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        bm.Set(0);
        bm.Set(slabSize - 1);
        bm.Set(slabSize + 32);

        var onesForward = Collect(bm.EnumerateOnes());
        Assert.Equal([0, slabSize - 1, slabSize + 32], onesForward);

        // ZerosReverse: use all-one base with a few clears
        var bm2 = new SlabBitmap(shift);
        bm2.GrowSlabAllOne();
        bm2.GrowSlabAllOne();
        bm2.Clear(0);
        bm2.Clear(slabSize - 1);
        bm2.Clear(slabSize + 32);

        var zerosReverse = Collect(bm2.EnumerateZerosReverse());
        Assert.Equal([slabSize + 32, slabSize - 1, 0], zerosReverse);
    }

    // ───────────────────── CompactionEnumerator ─────────────────────

    [Fact]
    public void CompactionMoves_Empty_YieldsNothing() {
        var bm = new SlabBitmap(Shift);
        var list = CollectMoves(bm.EnumerateCompactionMoves());
        Assert.Empty(list);
    }

    [Fact]
    public void CompactionMoves_AllFree_YieldsNothing() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne(); // all free
        var list = CollectMoves(bm.EnumerateCompactionMoves());
        Assert.Empty(list);
    }

    [Fact]
    public void CompactionMoves_AllOccupied_YieldsNothing() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero(); // all occupied
        var list = CollectMoves(bm.EnumerateCompactionMoves());
        Assert.Empty(list);
    }

    [Fact]
    public void CompactionMoves_AlreadyCompact_YieldsNothing() {
        // [0,0,0,1,1,1] — occupied packed at head, free at tail
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        for (int i = SlabSize / 2; i < SlabSize; i++) { bm.Set(i); }
        var list = CollectMoves(bm.EnumerateCompactionMoves());
        Assert.Empty(list);
    }

    [Fact]
    public void CompactionMoves_SingleSwap() {
        // bit 0 = free(1), bit 63 = occupied(0), rest = free(1)
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.Clear(63); // one occupied at tail
        var list = CollectMoves(bm.EnumerateCompactionMoves());
        Assert.Single(list);
        Assert.Equal((0, 63), list[0]); // move occupied@63 to free@0
    }

    [Fact]
    public void CompactionMoves_MultipleSwaps() {
        // free at 0,1,2; occupied at 61,62,63; rest free
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.Clear(61);
        bm.Clear(62);
        bm.Clear(63);
        var list = CollectMoves(bm.EnumerateCompactionMoves());
        Assert.Equal(3, list.Count);
        // Each move: One < Zero
        foreach (var (one, zero) in list) { Assert.True(one < zero); }
        // free slots ascend, occupied slots descend
        Assert.Equal(0, list[0].One);
        Assert.Equal(63, list[0].Zero);
        Assert.Equal(1, list[1].One);
        Assert.Equal(62, list[1].Zero);
        Assert.Equal(2, list[2].One);
        Assert.Equal(61, list[2].Zero);
    }

    [Fact]
    public void CompactionMoves_CrossSlab() {
        // slab 0: all occupied (0); slab 1: all free (1) except bit SlabSize+3
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero(); // slab 0: all occupied
        bm.GrowSlabAllOne();  // slab 1: all free
        bm.Clear(SlabSize + 3); // one occupied in slab 1
        // The only compaction move: first free in slab 1 → occupied at SlabSize+3
        var list = CollectMoves(bm.EnumerateCompactionMoves());
        Assert.Single(list);
        Assert.Equal(SlabSize, list[0].One);      // first free = SlabSize (slab 1, bit 0)
        Assert.Equal(SlabSize + 3, list[0].Zero); // occupied at SlabSize+3
    }

    [Fact]
    public void CompactionMoves_Stops_When_Cursors_Meet() {
        // Interleaved: even bits = occupied(0), odd bits = free(1)
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        for (int i = 1; i < SlabSize; i += 2) { bm.Set(i); } // odd = free
        var list = CollectMoves(bm.EnumerateCompactionMoves());
        // free slots: 1,3,5,...,63 (32 total)
        // occupied slots from reverse: 62,60,...,0 (32 total)
        // Moves until free cursor >= occupied cursor
        // Move k: (2k+1, 62-2k) — stops when 2k+1 >= 62-2k → 4k >= 61 → k >= 16
        Assert.Equal(16, list.Count);
        for (int k = 0; k < 16; k++) {
            Assert.Equal(2 * k + 1, list[k].One);
            Assert.Equal(62 - 2 * k, list[k].Zero);
        }
    }

    [Fact]
    public void CompactionMoves_DoesNotModifyBitmap() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.Clear(60);
        bm.Clear(61);
        bm.Clear(62);
        int onesBefore = bm.TotalOneCount();

        var list = CollectMoves(bm.EnumerateCompactionMoves());
        Assert.True(list.Count > 0);

        // Bitmap unchanged
        Assert.Equal(onesBefore, bm.TotalOneCount());
        Assert.True(bm.Test(0));   // still free
        Assert.False(bm.Test(60)); // still occupied
    }

    // ───────────────────── Helpers ─────────────────────

    private static List<(int One, int Zero)> CollectMoves(SlabBitmap.CompactionEnumerator e) {
        var list = new List<(int, int)>();
        foreach (var move in e) { list.Add(move); }
        return list;
    }

    // ───────────────────── Helpers ─────────────────────

    private static List<int> Collect(SlabBitmap.OnesForwardEnumerator e) {
        var list = new List<int>();
        foreach (int i in e) { list.Add(i); }
        return list;
    }

    private static List<int> Collect(SlabBitmap.ZerosReverseEnumerator e) {
        var list = new List<int>();
        foreach (int i in e) { list.Add(i); }
        return list;
    }
}
