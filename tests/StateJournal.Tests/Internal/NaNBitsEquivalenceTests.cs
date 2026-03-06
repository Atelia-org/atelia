using Xunit;

namespace Atelia.StateJournal.Tests.Internal;

public class NaNBitsEquivalenceTests {
    private const ulong ExponentMask = 0x7FF0_0000_0000_0000UL;
    private const ulong MantissaMask = 0x000F_FFFF_FFFF_FFFFUL;
    private const ulong AbsMask = 0x7FFF_FFFF_FFFF_FFFFUL;

    [Theory]
    [InlineData(0x0000_0000_0000_0000UL, false)]
    [InlineData(0x8000_0000_0000_0000UL, false)]
    [InlineData(0x3FF0_0000_0000_0000UL, false)]
    [InlineData(0xBFF0_0000_0000_0000UL, false)]
    [InlineData(0x7FEF_FFFF_FFFF_FFFFUL, false)]
    [InlineData(0xFFE_FFFFF_FFFF_FFFFUL, false)]
    [InlineData(0x7FF0_0000_0000_0000UL, false)]
    [InlineData(0xFFF0_0000_0000_0000UL, false)]
    [InlineData(0x7FF0_0000_0000_0001UL, true)]
    [InlineData(0xFFF0_0000_0000_0001UL, true)]
    [InlineData(0x7FF8_0000_0000_0000UL, true)]
    [InlineData(0xFFF8_0000_0000_0000UL, true)]
    [InlineData(0x7FFF_FFFF_FFFF_FFFFUL, true)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFUL, true)]
    public void RepresentativePatterns_Agree(ulong bits, bool expected) {
        Assert.Equal(expected, IsNaNBits_Original(bits));
        Assert.Equal(expected, IsNaNBits_User(bits));
        Assert.Equal(expected, IsNaNBits_Classic(bits));
    }

    [Fact]
    public void AllExponentPatterns_WithRepresentativeMantissas_Agree() {
        ulong[] mantissas = [0UL, 1UL, 2UL, 3UL, 1UL << 51, MantissaMask - 1, MantissaMask];

        foreach (ulong sign in new[] { 0UL, 1UL << 63 }) {
            for (ulong exponent = 0; exponent <= 0x7FF; exponent++) {
                ulong exponentBits = exponent << 52;
                foreach (ulong mantissa in mantissas) {
                    ulong bits = sign | exponentBits | mantissa;
                    bool expected = IsNaNBits_Original(bits);
                    Assert.Equal(expected, IsNaNBits_User(bits));
                    Assert.Equal(expected, IsNaNBits_Classic(bits));
                }
            }
        }
    }

    [Fact]
    public void XorShiftSub_RandomizedMillionSamples_AgreesWithOriginalAndClassic() {
        ulong state = 0xD6E8_FD90_4321_7C5DUL;

        for (int i = 0; i < 1_000_000; i++) {
            ulong bits = NextUInt64(ref state);
            bool expected = IsNaNBits_Original(bits);
            Assert.Equal(expected, IsNaNBits_User(bits));
            Assert.Equal(expected, IsNaNBits_Classic(bits));
        }
    }

    private static bool IsNaNBits_Original(ulong doubleBits) =>
        (doubleBits & ExponentMask) == ExponentMask
        && (doubleBits & MantissaMask) != 0;

    private static bool IsNaNBits_User(ulong doubleBits) {
        doubleBits ^= ExponentMask;
        doubleBits <<= 1;
        doubleBits -= 2UL;
        return doubleBits < (MantissaMask << 1);
    }

    private static bool IsNaNBits_Classic(ulong doubleBits) =>
        (doubleBits & AbsMask) > ExponentMask;

    private static ulong NextUInt64(ref ulong state) {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return state * 0x2545_F491_4F6C_DD1DUL;
    }
}
