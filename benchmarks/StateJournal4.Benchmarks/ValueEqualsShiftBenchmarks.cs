using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

/// <summary>
/// 对比 ValueEquals 中"取高32位并比较"的几种写法在 RyuJIT 下的实际生成代码。
///
/// 核心问题：
/// 1. shr+64bit-cmp vs shr+32bit-cmp — JIT 是否缩窄？
/// 2. 用 uint 局部变量能否引导 JIT 生成更紧凑的代码？
///
/// 重点看 [DisassemblyDiagnoser] 产出的 asm，而非吞吐量差异
/// （两种写法在现代 x86-64 上性能应几乎一致）。
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3)]
[OperationsPerSecond]
public class ValueEqualsShiftBenchmarks {
    private const int N = 100_000;
    private const int HeapHandleBitCount = 32;

    // 模拟 ValueBox：单 ulong 字段，readonly struct, Pack=4
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct Box {
        private readonly ulong _bits;
        public Box(ulong bits) => _bits = bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong GetBits() => _bits;
    }

    private Box[] _arrA = null!;
    private Box[] _arrB = null!;

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(42);
        _arrA = new Box[N];
        _arrB = new Box[N];

        for (int i = 0; i < N; i++) {
            // 生成有代表性的位模式
            ulong bits = (ulong)rng.NextInt64();
            _arrA[i] = new Box(bits);

            // ~50% 完全相等, ~25% 高位相等低位不同, ~25% 完全不等
            int roll = rng.Next(100);
            if (roll < 50) {
                _arrB[i] = new Box(bits); // 完全相等 → 快速路径
            } else if (roll < 75) {
                // 高 32 位相同，低 32 位不同 → 走慢路径分支
                _arrB[i] = new Box((bits & 0xFFFF_FFFF_0000_0000UL) | (uint)rng.Next());
            } else {
                _arrB[i] = new Box((ulong)rng.NextInt64()); // 完全不同
            }
        }
    }

    // ──── 方案 A: 当前写法 — 64-bit shift-assign + 64-bit compare ────

    /// <summary>
    /// 复刻 ValueBox.ValueEquals 当前的写法。
    /// ab >>= 32 复用给 SlowPath 的 (uint)ab 参数。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Equals_A_Shift64(Box a, Box b) {
        ulong ab = a.GetBits(), bb = b.GetBits();
        if (ab == bb) { return true; }
        if ((ab >>= HeapHandleBitCount) != (bb >> HeapHandleBitCount)) { return false; }
        return SlowPath(a, b, (uint)ab);
    }

    // ──── 方案 B: 显式 uint32 — 32-bit cast + 32-bit compare ────

    /// <summary>
    /// 用 uint 局部变量做 32-bit compare。
    /// 让 JIT 明确知道比较的值域只有 32 位。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Equals_B_Cast32(Box a, Box b) {
        ulong ab = a.GetBits(), bb = b.GetBits();
        if (ab == bb) { return true; }
        uint ahi = (uint)(ab >> HeapHandleBitCount);
        if (ahi != (uint)(bb >> HeapHandleBitCount)) { return false; }
        return SlowPath(a, b, ahi);
    }

    // ──── 方案 C: 用 Unsafe 做 "memory-style" 直接读高位 ────

    /// <summary>
    /// 通过 Unsafe.As 把 Box 当成 Span&lt;uint&gt; 来读，
    /// 测试 JIT 能否优化为 mov eax, [ptr+4] 直读高 32 位。
    /// （纯探索性——较高风险，可能妨碍 promotion。）
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Equals_C_UnsafeHigh(Box a, Box b) {
        ulong ab = a.GetBits(), bb = b.GetBits();
        if (ab == bb) { return true; }
        // 直接取高 32 位：LE 下 _bits 的高 4 字节在 offset 4
        uint ahi = Unsafe.Add(ref Unsafe.As<Box, uint>(ref a), 1);
        uint bhi = Unsafe.Add(ref Unsafe.As<Box, uint>(ref b), 1);
        if (ahi != bhi) { return false; }
        return SlowPath(a, b, ahi);
    }

    /// <summary>占位慢路径——防止 JIT 将整个函数优化掉。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SlowPath(Box a, Box b, uint tagAndKind) {
        // 模拟：仅当 tagAndKind 落在特定范围时才比较低 32 位
        return tagAndKind is >= 0x0080_0001u and <= 0x0080_0003u
            && (uint)a.GetBits() == (uint)b.GetBits();
    }

    // ══════ Benchmarks ══════

    [Benchmark(Baseline = true)]
    public int PairCompare_A_Shift64() {
        int count = 0;
        var arrA = _arrA;
        var arrB = _arrB;
        for (int i = 0; i < arrA.Length; i++) {
            if (Equals_A_Shift64(arrA[i], arrB[i])) count++;
        }
        return count;
    }

    [Benchmark]
    public int PairCompare_B_Cast32() {
        int count = 0;
        var arrA = _arrA;
        var arrB = _arrB;
        for (int i = 0; i < arrA.Length; i++) {
            if (Equals_B_Cast32(arrA[i], arrB[i])) count++;
        }
        return count;
    }

    [Benchmark]
    public int PairCompare_C_UnsafeHigh() {
        int count = 0;
        var arrA = _arrA;
        var arrB = _arrB;
        for (int i = 0; i < arrA.Length; i++) {
            if (Equals_C_UnsafeHigh(arrA[i], arrB[i])) count++;
        }
        return count;
    }
}
