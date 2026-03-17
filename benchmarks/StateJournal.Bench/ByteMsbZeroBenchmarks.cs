using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

/// <summary>
/// 对比几种“判断 byte 的最高位是否为 0”的等价写法在 RyuJIT 下的实际生成代码。
/// 重点看 [DisassemblyDiagnoser] 产出的 asm，而不是微小的吞吐量差异。
/// </summary>
[OperationsPerSecond]
[DisassemblyDiagnoser(maxDepth: 3)]
public class ByteMsbZeroBenchmarks {
    private const int N = 100_000;

    private byte[] _values = null!;

    [GlobalSetup]
    public void Setup() {
        _values = new byte[N];
        uint state = 0xC0FF_EE11u;

        for (int i = 0; i < _values.Length; i++) {
            _values[i] = (byte)Next(ref state);
        }
    }

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int ShiftRightEqualsZero() {
        int count = 0;
        int checksum = 0;
        var values = _values;

        for (int i = 0; i < values.Length; i++) {
            byte value = values[i];
            if ((value >> 7) == 0) {
                count++;
                checksum += value;
            }
        }

        return count ^ checksum;
    }

    [Benchmark(Baseline = true)]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int AndMaskEqualsZero() {
        int count = 0;
        int checksum = 0;
        var values = _values;

        for (int i = 0; i < values.Length; i++) {
            byte value = values[i];
            if ((value & 0x80) == 0) {
                count++;
                checksum += value;
            }
        }

        return count ^ checksum;
    }

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int SignedCastNonNegative() {
        int count = 0;
        int checksum = 0;
        var values = _values;

        for (int i = 0; i < values.Length; i++) {
            byte value = values[i];
            if ((sbyte)value >= 0) {
                count++;
                checksum += value;
            }
        }

        return count ^ checksum;
    }

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int LessOrEqual127() {
        int count = 0;
        int checksum = 0;
        var values = _values;

        for (int i = 0; i < values.Length; i++) {
            byte value = values[i];
            if (value <= 127) {
                count++;
                checksum += value;
            }
        }

        return count ^ checksum;
    }

    [Benchmark]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int LessThan128() {
        int count = 0;
        int checksum = 0;
        var values = _values;

        for (int i = 0; i < values.Length; i++) {
            byte value = values[i];
            if (value < 128) {
                count++;
                checksum += value;
            }
        }

        return count ^ checksum;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Next(ref uint state) {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
