using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

// ai:facade `src/StateJournal/Pools/SlabBitmap.cs`
// ai:impl `src/StateJournal/Pools/SlabBitmap.Impl.cs`
public partial class SlabBitmapTests {
    private const int SlabSize = SlabBitmap.SlabSize;

    // ───────────────────── Construction ─────────────────────

    [Fact]
    public void Ctor_CreatesEmptyBitmap() {
        var bm = new SlabBitmap();
        Assert.Equal(0, bm.SlabCount);
        Assert.Equal(0, bm.Capacity);
    }

    // ───────────────────── GrowOneSlab / ShrinkLastSlab ─────────────────────

    [Fact]
    public void GrowOneSlab_AllClear_IncreasesCapacity() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        Assert.Equal(1, bm.SlabCount);
        Assert.Equal(SlabSize, bm.Capacity);
        Assert.Equal(0, bm.GetOneCount(0));
    }

    [Fact]
    public void GrowOneSlab_AllSet_AllBitsSet() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllOne();
        Assert.Equal(SlabSize, bm.GetOneCount(0));
        Assert.Equal(SlabSize, bm.TotalOneCount());
    }

    [Fact]
    public void GrowMultipleSlabs_TracksCorrectly() {
        var bm = new SlabBitmap();
        for (int i = 0; i < 5; i++) {
            if (i % 2 == 0) {
                bm.GrowSlabAllOne();
            }
            else {
                bm.GrowSlabAllZero();
            }
        }
        Assert.Equal(5, bm.SlabCount);
        Assert.Equal(5 * SlabSize, bm.Capacity);
        Assert.Equal(3 * SlabSize, bm.TotalOneCount()); // slabs 0, 2, 4 are set
    }

    [Fact]
    public void ShrinkLastSlab_RemovesLastSlab() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        Assert.Equal(2, bm.SlabCount);

        bm.ShrinkLastSlab();
        Assert.Equal(1, bm.SlabCount);
        Assert.Equal(SlabSize, bm.Capacity);
    }

    [Fact]
    public void ShrinkLastSlab_Empty_Throws() {
        var bm = new SlabBitmap();
        Assert.Throws<InvalidOperationException>(() => bm.ShrinkLastSlab());
    }

    // ───────────────────── #9: Grow→Shrink→Grow Cycle ─────────────────────

    [Fact]
    public void GrowShrinkGrow_L1L2Consistent() {
        var bm = new SlabBitmap();

        // Grow 2 slabs (allOne)
        bm.GrowSlabAllOne();
        bm.GrowSlabAllOne();
        Assert.Equal(2, bm.SlabCount);
        Assert.Equal(SlabSize * 2, bm.TotalOneCount());

        // Shrink 1 slab
        bm.ShrinkLastSlab();
        Assert.Equal(1, bm.SlabCount);
        Assert.Equal(SlabSize, bm.TotalOneCount());

        // Grow 1 slab (allZero) — reuses the slot that was shrunk
        bm.GrowSlabAllZero();
        Assert.Equal(2, bm.SlabCount);
        Assert.Equal(0, bm.GetOneCount(1));                  // slab 1 is all-zero
        Assert.Equal(0, bm.FindFirstOne());                   // first one in slab 0
        Assert.Equal(SlabSize * 2 - 1, bm.FindLastZero());   // last zero at end of slab 1

        // Set a bit in slab 1, verify full consistency
        int target = SlabSize + 7;
        bm.Set(target);

        Assert.True(bm.Test(target));
        Assert.Equal(1, bm.GetOneCount(1));
        Assert.Equal(SlabSize + 1, bm.TotalOneCount());
        Assert.Equal(0, bm.FindFirstOne());                   // still slab 0
        Assert.Equal(SlabSize * 2 - 1, bm.FindLastZero());   // still end of slab 1
    }

    [Fact]
    public void GrowShrinkGrow_MultiCycle_StaysConsistent() {
        var bm = new SlabBitmap();

        for (int cycle = 0; cycle < 3; cycle++) {
            // Grow 2 slabs
            bm.GrowSlabAllZero();
            bm.GrowSlabAllZero();
            int slabCount = bm.SlabCount;

            // Set one bit in each of the two new slabs
            int bit1 = (slabCount - 2) * SlabSize + 10;
            int bit2 = (slabCount - 1) * SlabSize + 20;
            bm.Set(bit1);
            bm.Set(bit2);

            int onesBeforeShrink = bm.TotalOneCount();

            // FindFirstOne / FindLastZero must be valid
            int first = bm.FindFirstOne();
            Assert.True(first >= 0);
            Assert.True(bm.Test(first));

            int lastZ = bm.FindLastZero();
            Assert.True(lastZ >= 0);
            Assert.False(bm.Test(lastZ));

            // Shrink last slab → loses bit2
            bm.ShrinkLastSlab();
            Assert.Equal(onesBeforeShrink - 1, bm.TotalOneCount());

            int onesAfterShrink = bm.TotalOneCount();

            // Grow back (allZero) — no new ones
            bm.GrowSlabAllZero();
            Assert.Equal(onesAfterShrink, bm.TotalOneCount());

            // Verify consistency after grow-back
            first = bm.FindFirstOne();
            Assert.True(first >= 0);
            Assert.True(bm.Test(first));

            lastZ = bm.FindLastZero();
            Assert.True(lastZ >= 0);
            Assert.False(bm.Test(lastZ));
        }
    }

    // ───────────────────── Set / Clear / Test ─────────────────────

    [Fact]
    public void SetClearTest_BasicRoundTrip() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();

        Assert.False(bm.Test(0));
        bm.Set(0);
        Assert.True(bm.Test(0));
        Assert.Equal(1, bm.GetOneCount(0));

        bm.Clear(0);
        Assert.False(bm.Test(0));
        Assert.Equal(0, bm.GetOneCount(0));
    }

    [Fact]
    public void Set_Idempotent_DoesNotDoubleCount() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        bm.Set(5);
        bm.Set(5);
        Assert.Equal(1, bm.GetOneCount(0));
    }

    [Fact]
    public void Clear_Idempotent_DoesNotDoubleCount() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllOne();
        bm.Clear(5);
        bm.Clear(5);
        Assert.Equal(SlabSize - 1, bm.GetOneCount(0));
    }

    [Fact]
    public void SetClear_AcrossMultipleSlabs() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();

        bm.Set(0);
        bm.Set(SlabSize + 10);
        Assert.Equal(1, bm.GetOneCount(0));
        Assert.Equal(1, bm.GetOneCount(1));
        Assert.Equal(2, bm.TotalOneCount());

        bm.Clear(0);
        Assert.Equal(0, bm.GetOneCount(0));
        Assert.Equal(1, bm.TotalOneCount());
    }

    // ───────────────────── SetAll / ClearAll ─────────────────────

    [Fact]
    public void SetAll_SetsAllBits() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        bm.SetAll();
        Assert.Equal(SlabSize * 2, bm.TotalOneCount());
        Assert.True(bm.Test(0));
        Assert.True(bm.Test(SlabSize - 1));
        Assert.True(bm.Test(SlabSize));
        Assert.True(bm.Test(SlabSize * 2 - 1));
    }

    [Fact]
    public void ClearAll_ClearsAllBits() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllOne();
        bm.GrowSlabAllOne();
        bm.ClearAll();
        Assert.Equal(0, bm.TotalOneCount());
        Assert.False(bm.Test(0));
        Assert.False(bm.Test(SlabSize * 2 - 1));
    }

    // ───────────────────── Not ─────────────────────

    [Fact]
    public void Not_FlipsAllBits() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        bm.Set(5);

        bm.Not();

        Assert.False(bm.Test(5));
        Assert.Equal(SlabSize - 1, bm.TotalOneCount());

        // All other bits should be set
        Assert.True(bm.Test(0));
        Assert.True(bm.Test(4));
        Assert.True(bm.Test(6));
    }

    [Fact]
    public void Not_Twice_RestoresOriginal() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        bm.Set(10);
        bm.Set(20);
        bm.Set(30);
        int originalCount = bm.TotalOneCount();

        bm.Not();
        bm.Not();

        Assert.Equal(originalCount, bm.TotalOneCount());
        Assert.True(bm.Test(10));
        Assert.True(bm.Test(20));
        Assert.True(bm.Test(30));
    }

    // ───────────────────── Validation ─────────────────────

    // ───────────────────── TotalSetCount ─────────────────────

    [Fact]
    public void TotalSetCount_MultiSlab_Accurate() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllOne();
        bm.GrowSlabAllZero();
        bm.Set(SlabSize + 5);
        Assert.Equal(SlabSize + 1, bm.TotalOneCount());
    }

    // ───────────────────── FindFirstOne / FindLastZero ─────────────────────

    [Fact]
    public void FindFirstOne_Empty_ReturnsNegative() {
        var bm = new SlabBitmap();
        Assert.Equal(-1, bm.FindFirstOne());
    }

    [Fact]
    public void FindFirstOne_NoBitsSet_ReturnsNegative() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        Assert.Equal(-1, bm.FindFirstOne());
    }

    [Fact]
    public void FindFirstOne_SingleBit_ReturnsIndex() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        bm.Set(42);
        Assert.Equal(42, bm.FindFirstOne());
    }

    [Fact]
    public void FindFirstOne_MultipleBits_ReturnsLowest() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        bm.Set(SlabSize + 10);
        bm.Set(5);
        Assert.Equal(5, bm.FindFirstOne());
    }

    [Fact]
    public void FindFirstOne_OnlyInSecondSlab() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero(); // empty
        bm.GrowSlabAllZero();
        bm.Set(SlabSize + 3);
        Assert.Equal(SlabSize + 3, bm.FindFirstOne());
    }

    [Fact]
    public void FindLastZero_AllOnes_ReturnsNegative() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllOne();
        Assert.Equal(-1, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_LastBitIsClear() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllOne();
        bm.Clear(SlabSize - 1);
        Assert.Equal(SlabSize - 1, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_MultipleClear_ReturnsHighest() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllOne();
        bm.Clear(5);
        bm.Clear(42);
        Assert.Equal(42, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_Empty_ReturnsNegative() {
        var bm = new SlabBitmap();
        Assert.Equal(-1, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_OnlyInFirstSlab() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllOne();
        bm.GrowSlabAllOne();
        bm.Clear(3);
        Assert.Equal(3, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_MultiSlab_ReturnsHighest() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllOne();
        bm.GrowSlabAllOne();
        bm.Clear(5);
        bm.Clear(SlabSize + 10);
        Assert.Equal(SlabSize + 10, bm.FindLastZero());
    }

    // ───────────────────── FindLastZero on all-zero bitmap ─────────────────────

    [Fact]
    public void FindLastZero_AllZero_ReturnsLastIndex() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        Assert.Equal(SlabSize - 1, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_MultiSlab_AllZero_ReturnsLastIndex() {
        var bm = new SlabBitmap();
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        Assert.Equal(SlabSize * 2 - 1, bm.FindLastZero());
    }

    // ───────────────────── #6: L2 Cross-Word Boundary (65 slabs) ─────────────────────

    [Fact]
    public void FindFirstOne_65Slabs_CrossL2Word() {
        var bm = new SlabBitmap();
        // 65 slabs: first 64 all-zero, 65th all-zero with one bit set.
        // Slab 64 lives in L2 word[1], forcing FindSlabWithOneForward's for-loop.
        for (int i = 0; i < 65; i++) { bm.GrowSlabAllZero(); }

        int target = 64 * SlabSize + 42;
        bm.Set(target);

        Assert.Equal(target, bm.FindFirstOne());
    }

    [Fact]
    public void FindLastZero_65Slabs_CrossL2Word() {
        var bm = new SlabBitmap();
        // 65 slabs all-one, then clear one bit in slab 64 (L2 word[1]).
        for (int i = 0; i < 65; i++) { bm.GrowSlabAllOne(); }

        int target = 64 * SlabSize + 100;
        bm.Clear(target);

        Assert.Equal(target, bm.FindLastZero());
    }

    [Fact]
    public void EnumerateOnes_65Slabs_CrossL2Word() {
        var bm = new SlabBitmap();
        for (int i = 0; i < 65; i++) { bm.GrowSlabAllZero(); }

        int bitInSlab0 = 7;
        int bitInSlab64 = 64 * SlabSize + 42;
        bm.Set(bitInSlab0);
        bm.Set(bitInSlab64);

        // Enumerator must cross L2 word boundary from word[0] to word[1].
        Assert.Equal([bitInSlab0, bitInSlab64], Collect(bm.EnumerateOnes()));
    }

    [Fact]
    public void And_65Slabs_CrossL2Word() {
        var a = new SlabBitmap();
        var b = new SlabBitmap();
        for (int i = 0; i < 65; i++) {
            a.GrowSlabAllZero();
            b.GrowSlabAllZero();
        }

        // Intersection only in slab 64 (L2 word[1]).
        int shared = 64 * SlabSize + 20;
        int onlyA = 64 * SlabSize + 10;
        int onlyB = 64 * SlabSize + 30;
        a.Set(shared);
        a.Set(onlyA);
        b.Set(shared);
        b.Set(onlyB);

        a.And(b);

        Assert.True(a.Test(shared));
        Assert.False(a.Test(onlyA));
        Assert.False(a.Test(onlyB));
        Assert.Equal(1, a.TotalOneCount());
    }

    // ───────────────────── Set/Clear at word boundaries ─────────────────────

    [Theory]
    [InlineData(63)]    // last bit of first word
    [InlineData(64)]    // first bit of second word
    [InlineData(4095)]  // last bit of first slab
    [InlineData(4096)]  // first bit of second slab
    public void SetClear_WordAndSlabBoundary(int index) {
        var bm = new SlabBitmap();
        // Ensure enough capacity
        int neededSlabs = (index / SlabSize) + 1;
        for (int i = 0; i < neededSlabs; i++) { bm.GrowSlabAllZero(); }

        Assert.False(bm.Test(index));

        bm.Set(index);
        Assert.True(bm.Test(index));
        Assert.Equal(1, bm.TotalOneCount());
        Assert.Equal(index, bm.FindFirstOne());

        bm.Clear(index);
        Assert.False(bm.Test(index));
        Assert.Equal(0, bm.TotalOneCount());
        Assert.Equal(-1, bm.FindFirstOne());
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(4095)]
    [InlineData(4096)]
    public void Clear_WordAndSlabBoundary_FromAllOne(int index) {
        var bm = new SlabBitmap();
        int neededSlabs = (index / SlabSize) + 1;
        for (int i = 0; i < neededSlabs; i++) { bm.GrowSlabAllOne(); }

        Assert.True(bm.Test(index));

        bm.Clear(index);
        Assert.False(bm.Test(index));
        Assert.Equal(neededSlabs * SlabSize - 1, bm.TotalOneCount());
        Assert.Equal(index, bm.FindLastZero());

        bm.Set(index);
        Assert.True(bm.Test(index));
        Assert.Equal(neededSlabs * SlabSize, bm.TotalOneCount());
        Assert.Equal(-1, bm.FindLastZero());
    }
}
