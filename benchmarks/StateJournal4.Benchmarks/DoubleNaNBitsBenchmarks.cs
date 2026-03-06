using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

/// <summary>
/// 对比三种 double raw-bits NaN 判定写法：原始掩码版、branchless 花式版、经典 abs&gt;inf 版。
/// 重点看吞吐量与反汇编，不预设“无分支”一定更快。
/// </summary>
[OperationsPerSecond]
[DisassemblyDiagnoser(maxDepth: 3)]
public class DoubleNaNBitsBenchmarks {
    private const int N = 100_000;
    private const ulong ExponentMask = 0x7FF0_0000_0000_0000UL;
    private const ulong MantissaMask = 0x000F_FFFF_FFFF_FFFFUL;
    private const ulong AbsMask = 0x7FFF_FFFF_FFFF_FFFFUL;

    [Params("finite", "mixed", "nanHeavy")]
    public string Distribution { get; set; } = "mixed";

    private ulong[] _values = null!;

    [GlobalSetup]
    public void Setup() {
        _values = new ulong[N];
        ulong state = 0x9E37_79B9_7F4A_7C15UL;

        for (int i = 0; i < N; i++) {
            _values[i] = Distribution switch {
                "finite" => NextFinite(ref state),
                "nanHeavy" => NextNaNHeavy(ref state),
                _ => NextMixed(ref state),
            };
        }
    }

    [Benchmark(Baseline = true)]
    public int Original_MaskAndCompare() {
        int count = 0;
        ulong checksum = 0;
        var values = _values;

        for (int i = 0; i < values.Length; i++) {
            ulong value = values[i];
            if (IsNaNBits_Original(value)) {
                count++;
                checksum ^= value;
            }
        }

        return count ^ (int)checksum;
    }

    [Benchmark]
    public int User_XorShiftSub() {
        int count = 0;
        ulong checksum = 0;
        var values = _values;

        for (int i = 0; i < values.Length; i++) {
            ulong value = values[i];
            if (IsNaNBits_User(value)) {
                count++;
                checksum ^= value;
            }
        }

        return count ^ (int)checksum;
    }

    [Benchmark]
    public int Classic_AbsGreaterThanInf() {
        int count = 0;
        ulong checksum = 0;
        var values = _values;

        for (int i = 0; i < values.Length; i++) {
            ulong value = values[i];
            if (IsNaNBits_Classic(value)) {
                count++;
                checksum ^= value;
            }
        }

        return count ^ (int)checksum;
    }

    [Benchmark]
    public int User_ShiftCompare() {
        int count = 0;
        ulong checksum = 0;
        var values = _values;

        for (int i = 0; i < values.Length; i++) {
            ulong value = values[i];
            if (IsNaNBits_ShiftCompare(value)) {
                count++;
                checksum ^= value;
            }
        }

        return count ^ (int)checksum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNaNBits_Original(ulong doubleBits) =>
        (doubleBits & ExponentMask) == ExponentMask
        && (doubleBits & MantissaMask) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNaNBits_User(ulong doubleBits) {
        doubleBits ^= ExponentMask;
        doubleBits <<= 1;
        doubleBits -= 2UL;
        return doubleBits < (MantissaMask << 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNaNBits_Classic(ulong doubleBits) =>
        (doubleBits & AbsMask) > ExponentMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNaNBits_ShiftCompare(ulong doubleBits) =>
        (doubleBits << 1) > (ExponentMask<<1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NextFinite(ref ulong state) {
        ulong bits = NextUInt64(ref state);
        ulong sign = bits & (1UL << 63);
        ulong exponent = ((bits >> 52) & 0x7FEUL) << 52;
        ulong mantissa = bits & MantissaMask;
        return sign | exponent | mantissa;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NextMixed(ref ulong state) {
        ulong roll = NextUInt64(ref state) % 100;
        if (roll < 92) { return NextFinite(ref state); }
        if (roll < 97) { return (NextUInt64(ref state) & (1UL << 63)) | ExponentMask; }

        ulong mantissa = (NextUInt64(ref state) & MantissaMask) | 1UL;
        return (NextUInt64(ref state) & (1UL << 63)) | ExponentMask | mantissa;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NextNaNHeavy(ref ulong state) {
        ulong roll = NextUInt64(ref state) % 100;
        if (roll < 70) {
            ulong mantissa = (NextUInt64(ref state) & MantissaMask) | 1UL;
            return (NextUInt64(ref state) & (1UL << 63)) | ExponentMask | mantissa;
        }
        if (roll < 85) { return (NextUInt64(ref state) & (1UL << 63)) | ExponentMask; }
        return NextFinite(ref state);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NextUInt64(ref ulong state) {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return state * 0x2545_F491_4F6C_DD1DUL;
    }
}
