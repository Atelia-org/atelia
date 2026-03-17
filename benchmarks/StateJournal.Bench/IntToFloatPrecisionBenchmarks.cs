using System.Numerics;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

/// <summary>
/// 对比三种"整数是否可精确表示为浮点数"的判定策略的微观性能。
/// 每种策略都包含 (double)n 的转换成本（因为 ValueBox.Get 的输出参数始终需要它）。
/// </summary>
/// <remarks>
/// 方案:
/// A. LZC+TZC — 纯整数 ALU 判定有效位数，可与 CVTSI2SD 并行
/// B. Roundtrip — `(long)(double)n == n`，串行依赖链但代码最简
/// C. Hybrid — range fast-path `n &lt;= 2^53` + roundtrip fallback
/// </remarks>
[OperationsPerSecond]
[DisassemblyDiagnoser(maxDepth: 3)]
public class IntToFloatPrecisionBenchmarks {
    private const int N = 100_000;

    /// <summary>值分布场景。</summary>
    [Params("small", "medium", "large", "powers")]
    public string Distribution { get; set; } = "small";

    private long[] _values = null!;

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(42);
        _values = new long[N];

        switch (Distribution) {
            case "small":
                // [0, 2^20) — 所有值对 double/float 都精确
                for (int i = 0; i < N; i++)
                    _values[i] = rng.Next(0, 1 << 20);
                break;

            case "medium":
                // [0, 2^53) — 对 double 都精确，对 float 可能不精确
                for (int i = 0; i < N; i++)
                    _values[i] = (long)(rng.NextDouble() * (1L << 53));
                break;

            case "large":
                // [0, 2^62) — 完整 inline nonneg 范围，大多数不精确
                for (int i = 0; i < N; i++)
                    _values[i] = (long)(rng.NextDouble() * (1L << 62));
                break;

            case "powers":
                // 2^k, k ∈ [0, 61] — 全部精确，但 range check 对 k > 53 会误报
                for (int i = 0; i < N; i++)
                    _values[i] = 1L << rng.Next(0, 62);
                break;
        }
    }

    // ═══════════════════════ double 精度判定 ═══════════════════════

    [Benchmark(Baseline = true)]
    public int Roundtrip_Double() {
        int exact = 0;
        double sum = 0;
        var vals = _values;
        for (int i = 0; i < vals.Length; i++) {
            long n = vals[i];
            double d = (double)n;
            sum += d; // 防止 dead-code elimination
            if ((long)d == n) exact++;
        }
        return exact ^ (int)sum;
    }

    [Benchmark]
    public int LzcTzc_Double() {
        int exact = 0;
        double sum = 0;
        var vals = _values;
        for (int i = 0; i < vals.Length; i++) {
            long n = vals[i];
            double d = (double)n;
            sum += d;
            if (IsExactDouble_LzcTzc((ulong)n)) exact++;
        }
        return exact ^ (int)sum;
    }

    [Benchmark]
    public int Hybrid_Double() {
        int exact = 0;
        double sum = 0;
        var vals = _values;
        const long max = 1L << 53;
        for (int i = 0; i < vals.Length; i++) {
            long n = vals[i];
            double d = (double)n;
            sum += d;
            if (n <= max || (long)d == n) exact++;
        }
        return exact ^ (int)sum;
    }

    // ═══════════════════════ float 精度判定 ═══════════════════════

    [Benchmark]
    public int Roundtrip_Single() {
        int exact = 0;
        float sum = 0;
        var vals = _values;
        for (int i = 0; i < vals.Length; i++) {
            long n = vals[i];
            float f = (float)n;
            sum += f;
            if ((long)f == n) exact++;
        }
        return exact ^ (int)sum;
    }

    [Benchmark]
    public int LzcTzc_Single() {
        int exact = 0;
        float sum = 0;
        var vals = _values;
        for (int i = 0; i < vals.Length; i++) {
            long n = vals[i];
            float f = (float)n;
            sum += f;
            if (IsExactSingle_LzcTzc((ulong)n)) exact++;
        }
        return exact ^ (int)sum;
    }

    [Benchmark]
    public int Hybrid_Single() {
        int exact = 0;
        float sum = 0;
        var vals = _values;
        const long max = 1L << 24;
        for (int i = 0; i < vals.Length; i++) {
            long n = vals[i];
            float f = (float)n;
            sum += f;
            if (n <= max || (long)f == n) exact++;
        }
        return exact ^ (int)sum;
    }

    // ═══════════════════════ 辅助方法 ═══════════════════════

    /// <summary>判断非负整数是否可精确表示为 double (53-bit significand)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // private static bool IsExactDouble_LzcTzc(ulong value) {
    //     // value == 0: LZCNT=64, msb=-1, shift=-53, fast path ✓
    //     int msb = 63 - BitOperations.LeadingZeroCount(value);
    //     int shift = msb - 52;
    //     if (shift <= 0) return true;
    //     return BitOperations.TrailingZeroCount(value) >= shift;
    // }
    static bool IsExactDouble_LzcTzc(ulong value) {
        return BitOperations.LeadingZeroCount(value) + BitOperations.TrailingZeroCount(value) >= 64 - 53;
    }

    /// <summary>判断非负整数是否可精确表示为 float (24-bit significand)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // private static bool IsExactSingle_LzcTzc(ulong value) {
    //     int msb = 63 - BitOperations.LeadingZeroCount(value);
    //     int shift = msb - 23;
    //     if (shift <= 0) return true;
    //     return BitOperations.TrailingZeroCount(value) >= shift;
    // }
    private static bool IsExactSingle_LzcTzc(ulong value) {
        return BitOperations.LeadingZeroCount(value) + BitOperations.TrailingZeroCount(value) >= 64 - 24;
    }
}
