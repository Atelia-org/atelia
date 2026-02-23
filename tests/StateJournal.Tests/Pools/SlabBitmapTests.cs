using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

// ai:facade `src/StateJournal/Pools/SlabBitmap.cs`
// ai:impl `src/StateJournal/Pools/SlabBitmap.Impl.cs`
public partial class SlabBitmapTests {
    private const int Shift = SlabBitmap.MinSlabShift; // 64 bits per slab
    private const int SlabSize = 1 << Shift; // 64

    // ───────────────────── Construction ─────────────────────

    [Fact]
    public void Ctor_CreatesEmptyBitmap() {
        var bm = new SlabBitmap(Shift);
        Assert.Equal(0, bm.SlabCount);
        Assert.Equal(0, bm.Capacity);
    }

    [Theory]
    [InlineData(SlabBitmap.MinSlabShift)]
    [InlineData(SlabBitmap.MaxSlabShift)]
    [InlineData(10)]
    public void Ctor_ValidShift_Succeeds(int shift) {
        var bm = new SlabBitmap(shift);
        Assert.Equal(1 << shift, bm.SlabSize);
    }

    [Theory]
    [InlineData(SlabBitmap.MinSlabShift - 1)]
    [InlineData(SlabBitmap.MaxSlabShift + 1)]
    [InlineData(-1)]
    public void Ctor_InvalidShift_Throws(int shift) {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlabBitmap(shift));
    }

    // ───────────────────── GrowOneSlab / ShrinkLastSlab ─────────────────────

    [Fact]
    public void GrowOneSlab_AllClear_IncreasesCapacity() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        Assert.Equal(1, bm.SlabCount);
        Assert.Equal(SlabSize, bm.Capacity);
        Assert.Equal(0, bm.GetOneCount(0));
    }

    [Fact]
    public void GrowOneSlab_AllSet_AllBitsSet() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        Assert.Equal(SlabSize, bm.GetOneCount(0));
        Assert.Equal(SlabSize, bm.TotalOneCount());
    }

    [Fact]
    public void GrowMultipleSlabs_TracksCorrectly() {
        var bm = new SlabBitmap(Shift);
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
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        Assert.Equal(2, bm.SlabCount);

        bm.ShrinkLastSlab();
        Assert.Equal(1, bm.SlabCount);
        Assert.Equal(SlabSize, bm.Capacity);
    }

    [Fact]
    public void ShrinkLastSlab_Empty_Throws() {
        var bm = new SlabBitmap(Shift);
        Assert.Throws<InvalidOperationException>(() => bm.ShrinkLastSlab());
    }

    // ───────────────────── Set / Clear / Test ─────────────────────

    [Fact]
    public void SetClearTest_BasicRoundTrip() {
        var bm = new SlabBitmap(Shift);
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
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        bm.Set(5);
        bm.Set(5);
        Assert.Equal(1, bm.GetOneCount(0));
    }

    [Fact]
    public void Clear_Idempotent_DoesNotDoubleCount() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.Clear(5);
        bm.Clear(5);
        Assert.Equal(SlabSize - 1, bm.GetOneCount(0));
    }

    [Fact]
    public void SetClear_AcrossMultipleSlabs() {
        var bm = new SlabBitmap(Shift);
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
        var bm = new SlabBitmap(Shift);
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
        var bm = new SlabBitmap(Shift);
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
        var bm = new SlabBitmap(Shift);
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
        var bm = new SlabBitmap(Shift);
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

    [Fact]
    public void BulkOp_DifferentShift_Throws() {
        var a = new SlabBitmap(SlabBitmap.MinSlabShift);
        var b = new SlabBitmap(SlabBitmap.MinSlabShift + 1);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        Assert.Throws<ArgumentException>(() => a.And(b));
        Assert.Throws<ArgumentException>(() => a.Or(b));
        Assert.Throws<ArgumentException>(() => a.Xor(b));
        Assert.Throws<ArgumentException>(() => a.AndNot(b));
    }

    // ───────────────────── TotalSetCount ─────────────────────

    [Fact]
    public void TotalSetCount_MultiSlab_Accurate() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.GrowSlabAllZero();
        bm.Set(SlabSize + 5);
        Assert.Equal(SlabSize + 1, bm.TotalOneCount());
    }

    // ───────────────────── FindFirstOne / FindLastZero ─────────────────────

    [Fact]
    public void FindFirstOne_Empty_ReturnsNegative() {
        var bm = new SlabBitmap(Shift);
        Assert.Equal(-1, bm.FindFirstOne());
    }

    [Fact]
    public void FindFirstOne_NoBitsSet_ReturnsNegative() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        Assert.Equal(-1, bm.FindFirstOne());
    }

    [Fact]
    public void FindFirstOne_SingleBit_ReturnsIndex() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        bm.Set(42);
        Assert.Equal(42, bm.FindFirstOne());
    }

    [Fact]
    public void FindFirstOne_MultipleBits_ReturnsLowest() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero();
        bm.GrowSlabAllZero();
        bm.Set(SlabSize + 10);
        bm.Set(5);
        Assert.Equal(5, bm.FindFirstOne());
    }

    [Fact]
    public void FindFirstOne_OnlyInSecondSlab() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllZero(); // empty
        bm.GrowSlabAllZero();
        bm.Set(SlabSize + 3);
        Assert.Equal(SlabSize + 3, bm.FindFirstOne());
    }

    [Fact]
    public void FindLastZero_AllOnes_ReturnsNegative() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        Assert.Equal(-1, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_LastBitIsClear() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.Clear(SlabSize - 1);
        Assert.Equal(SlabSize - 1, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_MultipleClear_ReturnsHighest() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.Clear(5);
        bm.Clear(42);
        Assert.Equal(42, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_Empty_ReturnsNegative() {
        var bm = new SlabBitmap(Shift);
        Assert.Equal(-1, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_OnlyInFirstSlab() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.GrowSlabAllOne();
        bm.Clear(3);
        Assert.Equal(3, bm.FindLastZero());
    }

    [Fact]
    public void FindLastZero_MultiSlab_ReturnsHighest() {
        var bm = new SlabBitmap(Shift);
        bm.GrowSlabAllOne();
        bm.GrowSlabAllOne();
        bm.Clear(5);
        bm.Clear(SlabSize + 10);
        Assert.Equal(SlabSize + 10, bm.FindLastZero());
    }
}
