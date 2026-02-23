using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

// ai:facade `src/StateJournal/Pools/SlabBitmap.cs`
// ai:impl `src/StateJournal/Pools/SlabBitmap.BinaryOp.cs`
partial class SlabBitmapTests {
    // ───────────────────── And ─────────────────────

    [Fact]
    public void And_IntersectsBitmaps() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(1);
        a.Set(2);
        a.Set(3);
        b.Set(2);
        b.Set(3);
        b.Set(4);

        a.And(b);

        Assert.False(a.Test(1));
        Assert.True(a.Test(2));
        Assert.True(a.Test(3));
        Assert.False(a.Test(4));
        Assert.Equal(2, a.TotalOneCount());
    }

    [Fact]
    public void And_ExtraSlabs_AreClearedInThis() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllOne();
        a.GrowSlabAllOne(); // extra slab
        b.GrowSlabAllOne();

        a.And(b);

        Assert.Equal(SlabSize, a.GetOneCount(0)); // common slab: intersection
        Assert.Equal(0, a.GetOneCount(1)); // extra slab: cleared
    }

    // ───────────────────── Or ─────────────────────

    [Fact]
    public void Or_UnionsBitmaps() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(1);
        a.Set(2);
        b.Set(3);
        b.Set(4);

        a.Or(b);

        Assert.True(a.Test(1));
        Assert.True(a.Test(2));
        Assert.True(a.Test(3));
        Assert.True(a.Test(4));
        Assert.Equal(4, a.TotalOneCount());
    }

    // ───────────────────── Xor ─────────────────────

    [Fact]
    public void Xor_SymmetricDifference() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(1);
        a.Set(2);
        b.Set(2);
        b.Set(3);

        a.Xor(b);

        Assert.True(a.Test(1));
        Assert.False(a.Test(2));
        Assert.True(a.Test(3));
        Assert.Equal(2, a.TotalOneCount());
    }

    // ───────────────────── AndNot ─────────────────────

    [Fact]
    public void AndNot_RemovesBitsFromOther() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllOne();
        b.GrowSlabAllZero();

        b.Set(0);
        b.Set(10);
        b.Set(63);

        a.AndNot(b);

        Assert.False(a.Test(0));
        Assert.True(a.Test(1));
        Assert.False(a.Test(10));
        Assert.True(a.Test(11));
        Assert.False(a.Test(63));
        Assert.Equal(SlabSize - 3, a.TotalOneCount());
    }

    [Fact]
    public void AndNot_ExtraSlabs_Unchanged() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllOne();
        a.GrowSlabAllOne();
        b.GrowSlabAllZero(); // b 只有 1 slab

        a.AndNot(b);

        Assert.Equal(SlabSize, a.GetOneCount(0)); // AND NOT 0 = unchanged
        Assert.Equal(SlabSize, a.GetOneCount(1)); // extra slab: unchanged
    }

    // ───────────────────── OrNot ─────────────────────

    [Fact]
    public void OrNot_SetsUnsetBitsFromOther() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(0);
        a.Set(1);
        b.Set(1);
        b.Set(2);

        a.OrNot(b);

        // a | ~b: bit 0: 1|1=1, bit 1: 1|0=1, bit 2: 0|0=0, bit 3+: 0|1=1
        Assert.True(a.Test(0));
        Assert.True(a.Test(1));
        Assert.False(a.Test(2)); // only bit where both a=0 and b=1
        Assert.True(a.Test(3)); // 0 | ~0 = 1
        Assert.Equal(SlabSize - 1, a.TotalOneCount()); // all 1 except bit 2
    }

    [Fact]
    public void OrNot_ExtraSlabs_Filled() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        a.GrowSlabAllZero(); // extra slab
        b.GrowSlabAllZero();

        a.OrNot(b);

        // extra slab: a | ~0 = all 1s
        Assert.Equal(SlabSize, a.GetOneCount(1));
    }

    // ───────────────────── Intersects ─────────────────────

    [Fact]
    public void Intersects_ReturnsTrue_WhenOverlap() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(2);
        a.Set(5);
        b.Set(5);
        b.Set(10);

        Assert.True(a.Intersects(b));
    }

    [Fact]
    public void Intersects_ReturnsFalse_WhenDisjoint() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(1);
        a.Set(2);
        b.Set(3);
        b.Set(4);

        Assert.False(a.Intersects(b));
    }

    [Fact]
    public void Intersects_ReturnsFalse_WhenBothEmpty() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        Assert.False(a.Intersects(b));
    }

    [Fact]
    public void Intersects_DifferentSlabCounts_OnlyCheckCommon() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        a.GrowSlabAllOne(); // extra slab with all 1s
        b.GrowSlabAllZero(); // only 1 slab

        // common slab is all zero in both
        Assert.False(a.Intersects(b));
    }

    // ───────────────────── IsSubsetOf ─────────────────────

    [Fact]
    public void IsSubsetOf_ReturnsTrue_WhenSubset() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(2);
        a.Set(5);
        b.Set(2);
        b.Set(5);
        b.Set(10);

        Assert.True(a.IsSubsetOf(b));
    }

    [Fact]
    public void IsSubsetOf_ReturnsFalse_WhenNotSubset() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(1);
        a.Set(2);
        b.Set(2);

        Assert.False(a.IsSubsetOf(b));
    }

    [Fact]
    public void IsSubsetOf_ReturnsFalse_WhenExtraSlabHasOnes() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        a.GrowSlabAllZero();
        b.GrowSlabAllOne(); // only 1 slab

        a.Set(SlabSize + 1); // set bit in extra slab

        Assert.False(a.IsSubsetOf(b));
    }

    [Fact]
    public void IsSubsetOf_ReturnsTrue_WhenEmpty() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        Assert.True(a.IsSubsetOf(b)); // ∅ ⊆ anything
    }

    // ───────────────────── IsDisjointWith ─────────────────────

    [Fact]
    public void IsDisjointWith_ReturnsTrue_WhenNoOverlap() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(0);
        b.Set(1);

        Assert.True(a.IsDisjointWith(b));
    }

    [Fact]
    public void IsDisjointWith_ReturnsFalse_WhenOverlap() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(5);
        b.Set(5);

        Assert.False(a.IsDisjointWith(b));
    }

    // ───────────────────── CountAnd ─────────────────────

    [Fact]
    public void CountAnd_ReturnsIntersectionCardinality() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(1);
        a.Set(2);
        a.Set(3);
        b.Set(2);
        b.Set(3);
        b.Set(4);

        Assert.Equal(2, a.CountAnd(b)); // bits 2,3
    }

    [Fact]
    public void CountAnd_ReturnsZero_WhenDisjoint() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        a.Set(0);
        b.Set(1);

        Assert.Equal(0, a.CountAnd(b));
    }

    // ───────────────────── CopyFrom ─────────────────────

    [Fact]
    public void CopyFrom_ReplicatesContent() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        b.Set(0);
        b.Set(10);
        b.Set(63);

        a.CopyFrom(b);

        Assert.True(a.Test(0));
        Assert.False(a.Test(1));
        Assert.True(a.Test(10));
        Assert.True(a.Test(63));
        Assert.Equal(3, a.TotalOneCount());
    }

    [Fact]
    public void CopyFrom_UpdatesSummaryBitmaps() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllOne();
        b.GrowSlabAllZero();

        a.CopyFrom(b);

        Assert.Equal(0, a.TotalOneCount());
        Assert.Equal(-1, a.FindFirstOne()); // _slabHasOne properly cleared
    }

    [Fact]
    public void CopyFrom_ThrowsOnSlabCountMismatch() {
        var a = new SlabBitmap(Shift);
        var b = new SlabBitmap(Shift);
        a.GrowSlabAllZero();
        a.GrowSlabAllZero();
        b.GrowSlabAllZero();

        Assert.Throws<ArgumentException>(() => a.CopyFrom(b));
    }
}
