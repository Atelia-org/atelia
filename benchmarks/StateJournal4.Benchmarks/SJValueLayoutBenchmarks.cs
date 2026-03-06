using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

/// <summary>
/// SJValue 候选布局方案的读取性能对比。
/// 测试 4 种 struct 布局在数组遍历和 Dictionary-Entry 模拟场景下的性能差异。
/// </summary>
/// <remarks>
/// 方案:
/// A: ulong (align 8)
/// B: 2×uint, 读时 (ulong)_high &lt;&lt; 32 | _low (align 4)
/// D: ulong + [StructLayout(Pack=4)] (align 4, 读时直接访问)
/// </remarks>
[OperationsPerSecond]
[DisassemblyDiagnoser(maxDepth: 3)]
public class SJValueLayoutBenchmarks {
    private const int N = 100_000;

    // ──── 方案 A: ulong (align 8) ────
    private readonly struct LayoutA {
        private readonly ulong _bits;

        public LayoutA(ulong bits) => _bits = bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong GetBits() => _bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetLZC() => BitOperations.LeadingZeroCount(_bits);
    }

    // ──── 方案 B: 2×uint (align 4) ────
    private readonly struct LayoutB {
        private readonly uint _low;
        private readonly uint _high;

        public LayoutB(ulong bits) {
            _low = (uint)bits;
            _high = (uint)(bits >> 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong GetBits() => (ulong)_high << 32 | _low;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetLZC() => BitOperations.LeadingZeroCount(GetBits());
    }

    // ──── 方案 D: ulong + Pack=4 (align 4) ────
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct LayoutD {
        private readonly ulong _bits;

        public LayoutD(ulong bits) => _bits = bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong GetBits() => _bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetLZC() => BitOperations.LeadingZeroCount(_bits);
    }

    // ──── 模拟 Dictionary.Entry 结构 (TKey=int) ────
    // 测量: 不同 value layout 在 Entry 数组中遍历时的性能

    // [StructLayout(LayoutKind.Sequential)]
    private struct EntryA {
        public uint HashCode;
        public int Next;
        public int Key;
        public LayoutA Value;
    }

    // [StructLayout(LayoutKind.Sequential)]
    private struct EntryB {
        public uint HashCode;
        public int Next;
        public int Key;
        public LayoutB Value;
    }

    // [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct EntryD {
        public uint HashCode;
        public int Next;
        public int Key;
        public LayoutD Value;
    }

    // ──── 数据 ────
    private LayoutA[] _arrA = null!;
    private LayoutB[] _arrB = null!;
    private LayoutD[] _arrD = null!;

    private EntryA[] _entA = null!;
    private EntryB[] _entB = null!;
    private EntryD[] _entD = null!;

    [GlobalSetup]
    public void Setup() {
        // 打印 struct 大小，验证 alignment 预期
        Console.WriteLine($"sizeof(LayoutA) = {Unsafe.SizeOf<LayoutA>()}, sizeof(LayoutB) = {Unsafe.SizeOf<LayoutB>()}, sizeof(LayoutD) = {Unsafe.SizeOf<LayoutD>()}");
        Console.WriteLine($"sizeof(EntryA)  = {Unsafe.SizeOf<EntryA>()}, sizeof(EntryB)  = {Unsafe.SizeOf<EntryB>()}, sizeof(EntryD)  = {Unsafe.SizeOf<EntryD>()}");

        var rng = new Random(42);

        _arrA = new LayoutA[N];
        _arrB = new LayoutB[N];
        _arrD = new LayoutD[N];
        _entA = new EntryA[N];
        _entB = new EntryB[N];
        _entD = new EntryD[N];

        for (int i = 0; i < N; i++) {
            // 生成有代表性的 SJValue 位模式:
            // 50% 小整数 (LZC 1), 30% double (LZC 0), 20% slot 引用 (LZC 39+)
            ulong bits;
            int roll = rng.Next(100);
            if (roll < 50) {
                // Inline-Nonneg-Integer: 高位 01, 低 62 位是值
                bits = 0x4000_0000_0000_0000UL | (ulong)(uint)rng.Next(0, 10000);
            } else if (roll < 80) {
                // Lossy-Double: 高位 1
                bits = unchecked((ulong)rng.NextInt64()) | 0x8000_0000_0000_0000UL;
            } else {
                // Slot 引用: LZC ~39, 高位有很多零
                bits = (ulong)rng.Next(0, 1 << 24);
            }

            _arrA[i] = new LayoutA(bits);
            _arrB[i] = new LayoutB(bits);
            _arrD[i] = new LayoutD(bits);

            int key = rng.Next();
            _entA[i] = new EntryA { HashCode = (uint)key, Next = -1, Key = key, Value = new LayoutA(bits) };
            _entB[i] = new EntryB { HashCode = (uint)key, Next = -1, Key = key, Value = new LayoutB(bits) };
            _entD[i] = new EntryD { HashCode = (uint)key, Next = -1, Key = key, Value = new LayoutD(bits) };
        }
    }

    // ══════ Benchmark 1: 纯数组遍历，读取 bits 并累加 LZC ══════

    [Benchmark(Baseline = true)]
    public int ArrayScan_A_ULong() {
        int sum = 0;
        var arr = _arrA;
        for (int i = 0; i < arr.Length; i++) {
            sum += arr[i].GetLZC();
        }
        return sum;
    }

    [Benchmark]
    public int ArrayScan_B_2xUInt() {
        int sum = 0;
        var arr = _arrB;
        for (int i = 0; i < arr.Length; i++) {
            sum += arr[i].GetLZC();
        }
        return sum;
    }

    [Benchmark]
    public int ArrayScan_D_ULongPack4() {
        int sum = 0;
        var arr = _arrD;
        for (int i = 0; i < arr.Length; i++) {
            sum += arr[i].GetLZC();
        }
        return sum;
    }

    // ══════ Benchmark 2: Entry 数组遍历 (模拟 Dict<int,SJValue> 的线性扫描) ══════

    [Benchmark]
    public int EntryScan_A_ULong() {
        int sum = 0;
        var arr = _entA;
        for (int i = 0; i < arr.Length; i++) {
            sum += arr[i].Value.GetLZC();
        }
        return sum;
    }

    [Benchmark]
    public int EntryScan_B_2xUInt() {
        int sum = 0;
        var arr = _entB;
        for (int i = 0; i < arr.Length; i++) {
            sum += arr[i].Value.GetLZC();
        }
        return sum;
    }

    [Benchmark]
    public int EntryScan_D_ULongPack4() {
        int sum = 0;
        var arr = _entD;
        for (int i = 0; i < arr.Length; i++) {
            sum += arr[i].Value.GetLZC();
        }
        return sum;
    }

    // ══════ Benchmark 3: 随机访问 (模拟 Dict hash lookup) ══════

    private int[] _randomIndices = null!;

    [GlobalSetup(Target = nameof(RandomAccess_A_ULong))]
    public void SetupRandom_A() {
        Setup();
        _randomIndices = GenerateRandomIndices();
    }

    [GlobalSetup(Target = nameof(RandomAccess_B_2xUInt))]
    public void SetupRandom_B() {
        Setup();
        _randomIndices = GenerateRandomIndices();
    }

    [GlobalSetup(Target = nameof(RandomAccess_D_ULongPack4))]
    public void SetupRandom_D() {
        Setup();
        _randomIndices = GenerateRandomIndices();
    }

    private static int[] GenerateRandomIndices() {
        var rng = new Random(123);
        var indices = new int[N];
        for (int i = 0; i < N; i++) { indices[i] = rng.Next(N); }
        return indices;
    }

    [Benchmark]
    public int RandomAccess_A_ULong() {
        int sum = 0;
        var arr = _entA;
        var idx = _randomIndices;
        for (int i = 0; i < idx.Length; i++) {
            sum += arr[idx[i]].Value.GetLZC();
        }
        return sum;
    }

    [Benchmark]
    public int RandomAccess_B_2xUInt() {
        int sum = 0;
        var arr = _entB;
        var idx = _randomIndices;
        for (int i = 0; i < idx.Length; i++) {
            sum += arr[idx[i]].Value.GetLZC();
        }
        return sum;
    }

    [Benchmark]
    public int RandomAccess_D_ULongPack4() {
        int sum = 0;
        var arr = _entD;
        var idx = _randomIndices;
        for (int i = 0; i < idx.Length; i++) {
            sum += arr[idx[i]].Value.GetLZC();
        }
        return sum;
    }
}
