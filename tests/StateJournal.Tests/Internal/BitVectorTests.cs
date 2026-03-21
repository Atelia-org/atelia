using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class BitVectorTests {
    [Fact]
    public void Count_IsMaintainedIncrementally() {
        var bitSet = new BitVector();
        bitSet.SetLength(130);

        Assert.True(bitSet.SetBit(0));
        Assert.True(bitSet.SetBit(64));
        Assert.True(bitSet.SetBit(129));
        Assert.Equal(3, bitSet.PopCount);

        Assert.False(bitSet.SetBit(64));
        Assert.Equal(3, bitSet.PopCount);

        Assert.True(bitSet.ClearBit(64));
        Assert.False(bitSet.ClearBit(64));
        Assert.Equal(2, bitSet.PopCount);

        bitSet.Clear();
        Assert.Equal(0, bitSet.PopCount);
        Assert.False(bitSet.TestBit(0));
        Assert.False(bitSet.TestBit(129));
    }

    [Fact]
    public void SetLength_CanShrinkByHalf_AndTruncatesBitsBeyondLength() {
        var bitSet = new BitVector();
        bitSet.SetLength(1024); // 16 words
        Assert.Equal(1024, bitSet.Capacity);
        Assert.Equal(1024, bitSet.Length);

        Assert.True(bitSet.SetBit(0));
        Assert.True(bitSet.SetBit(130)); // word index = 2
        Assert.True(bitSet.TestBit(0));
        Assert.True(bitSet.TestBit(130));

        bitSet.SetLength(1);

        Assert.Equal(512, bitSet.Capacity);
        Assert.Equal(1, bitSet.Length);
        Assert.Equal(1, bitSet.PopCount);
        Assert.True(bitSet.TestBit(0));
        Assert.False(bitSet.TestBit(130));

        bitSet.Clear();
        bitSet.SetLength(1);

        Assert.Equal(256, bitSet.Capacity);
        Assert.Equal(1, bitSet.Length);
        Assert.False(bitSet.TestBit(0));
        Assert.False(bitSet.TestBit(130));
    }

    [Fact]
    public void Ones_AfterShrink_DoesNotEnumerateBitsBeyondLength() {
        var bitSet = new BitVector();
        bitSet.SetLength(130);

        Assert.True(bitSet.SetBit(0));
        Assert.True(bitSet.SetBit(129));

        bitSet.SetLength(1);

        Assert.Equal([0], [.. bitSet.Ones()]);
    }

    [Fact]
    public void SetLength_ShrinkAtWordBoundary_ClearsTruncatedWordBits() {
        var bitSet = new BitVector();
        bitSet.SetLength(128);

        Assert.True(bitSet.SetBit(70));
        Assert.Equal(1, bitSet.PopCount);

        bitSet.SetLength(64);
        Assert.Equal(0, bitSet.PopCount);
        Assert.False(bitSet.TestBit(70));
        int[] onesAfterShrink = [.. bitSet.Ones()];
        Assert.Empty(onesAfterShrink);

        bitSet.SetLength(128);
        Assert.Equal(0, bitSet.PopCount);
        Assert.False(bitSet.TestBit(70));
        int[] onesAfterGrow = [.. bitSet.Ones()];
        Assert.Empty(onesAfterGrow);
    }

    [Fact]
    public void SetLength_Zero_ReleasesArrayAfterTruncatingAllBits() {
        var bitSet = new BitVector();
        bitSet.SetLength(256);
        Assert.Equal(256, bitSet.Capacity);
        Assert.Equal(256, bitSet.Length);

        bitSet.SetLength(0);
        Assert.Equal(0, bitSet.Capacity);
        Assert.Equal(0, bitSet.Length);

        bitSet.SetLength(128);
        Assert.True(bitSet.SetBit(70));
        Assert.Equal(128, bitSet.Capacity);
        Assert.Equal(128, bitSet.Length);

        bitSet.SetLength(0);
        Assert.Equal(0, bitSet.Capacity);
        Assert.Equal(0, bitSet.Length);
        Assert.Equal(0, bitSet.PopCount);
        Assert.False(bitSet.TestBit(70));

        bitSet.Clear();
        bitSet.SetLength(0);
        Assert.Equal(0, bitSet.Capacity);
        Assert.Equal(0, bitSet.Length);
    }
}
