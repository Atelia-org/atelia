using BenchmarkDotNet.Attributes;
using Atelia.StateJournal.Pools;

namespace Atelia.StringPool.Bench;

[MemoryDiagnoser]
public class StringPoolStoreBenchmarks {
    [Params(256, 4096)]
    public int BatchSize { get; set; }

    private string[] _shortDistinct = null!;
    private string[] _longDistinct = null!;
    private string[] _longSameReference = null!;
    private string[] _longSameContentDifferentReference = null!;

    [GlobalSetup]
    public void Setup() {
        _shortDistinct = new string[BatchSize];
        _longDistinct = new string[BatchSize];
        _longSameReference = new string[BatchSize];
        _longSameContentDifferentReference = new string[BatchSize];

        string sharedLong = CreateLongString("shared", 0);
        string repeatedContent = CreateLongString("repeat", 1);

        for (int i = 0; i < BatchSize; i++) {
            _shortDistinct[i] = $"s:{i:X8}";
            _longDistinct[i] = CreateLongString("distinct", i);
            _longSameReference[i] = sharedLong;
            _longSameContentDifferentReference[i] = new string(repeatedContent.AsSpan());
        }
    }

#region Direct512x1
    [Benchmark(Baseline = true)] public int ShortDistinct_Direct512x1() => StoreBatchDirect512x1(_shortDistinct);
    [Benchmark] public int LongDistinct_Direct512x1() => StoreBatchDirect512x1(_longDistinct);
    [Benchmark] public int LongSameReference_Direct512x1() => StoreBatchDirect512x1(_longSameReference);
    [Benchmark] public int LongSameContentDifferentReference_Direct512x1() => StoreBatchDirect512x1(_longSameContentDifferentReference);
    [Benchmark] public int LongSameReference_WithSweepBetweenStores_Direct512x1() => StoreWithSweepBetweenStoresDirect512x1(_longSameReference);
    private static int StoreBatchDirect512x1(string[] values) {
        var pool = new StringPoolDirect512x1();
        uint acc = 0;
        foreach (string value in values) {
            acc ^= pool.Store(value).Packed;
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }

    private static int StoreWithSweepBetweenStoresDirect512x1(string[] values) {
        var pool = new StringPoolDirect512x1();
        uint acc = 0;
        foreach (string value in values) {
            SlotHandle handle = pool.Store(value);
            acc ^= handle.Packed;
            pool.BeginMark();
            pool.MarkReachable(handle);
            pool.Sweep();
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }
#endregion
#region Direct64x4
    [Benchmark] public int ShortDistinct_Direct64x4() => StoreBatchDirect64x4(_shortDistinct);
    [Benchmark] public int LongDistinct_Direct64x4() => StoreBatchDirect64x4(_longDistinct);
    [Benchmark] public int LongSameReference_Direct64x4() => StoreBatchDirect64x4(_longSameReference);
    [Benchmark] public int LongSameContentDifferentReference_Direct64x4() => StoreBatchDirect64x4(_longSameContentDifferentReference);
    [Benchmark] public int LongSameReference_WithSweepBetweenStores_Direct64x4() => StoreWithSweepBetweenStoresDirect64x4(_longSameReference);
    private static int StoreBatchDirect64x4(string[] values) {
        var pool = new StringPoolDirect64x4();
        uint acc = 0;
        foreach (string value in values) {
            acc ^= pool.Store(value).Packed;
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }

    private static int StoreWithSweepBetweenStoresDirect64x4(string[] values) {
        var pool = new StringPoolDirect64x4();
        uint acc = 0;
        foreach (string value in values) {
            SlotHandle handle = pool.Store(value);
            acc ^= handle.Packed;
            pool.BeginMark();
            pool.MarkReachable(handle);
            pool.Sweep();
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }
#endregion
#region Direct128x4
    [Benchmark] public int ShortDistinct_Direct128x4() => StoreBatchDirect128x4(_shortDistinct);
    [Benchmark] public int LongDistinct_Direct128x4() => StoreBatchDirect128x4(_longDistinct);
    [Benchmark] public int LongSameReference_Direct128x4() => StoreBatchDirect128x4(_longSameReference);
    [Benchmark] public int LongSameContentDifferentReference_Direct128x4() => StoreBatchDirect128x4(_longSameContentDifferentReference);
    [Benchmark] public int LongSameReference_WithSweepBetweenStores_Direct128x4() => StoreWithSweepBetweenStoresDirect128x4(_longSameReference);
    private static int StoreBatchDirect128x4(string[] values) {
        var pool = new StringPoolDirect128x4();
        uint acc = 0;
        foreach (string value in values) {
            acc ^= pool.Store(value).Packed;
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }

    private static int StoreWithSweepBetweenStoresDirect128x4(string[] values) {
        var pool = new StringPoolDirect128x4();
        uint acc = 0;
        foreach (string value in values) {
            SlotHandle handle = pool.Store(value);
            acc ^= handle.Packed;
            pool.BeginMark();
            pool.MarkReachable(handle);
            pool.Sweep();
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }
#endregion
#region Direct64x8
    [Benchmark] public int ShortDistinct_Direct64x8() => StoreBatchDirect64x8(_shortDistinct);
    [Benchmark] public int LongDistinct_Direct64x8() => StoreBatchDirect64x8(_longDistinct);
    [Benchmark] public int LongSameReference_Direct64x8() => StoreBatchDirect64x8(_longSameReference);
    [Benchmark] public int LongSameContentDifferentReference_Direct64x8() => StoreBatchDirect64x8(_longSameContentDifferentReference);
    [Benchmark] public int LongSameReference_WithSweepBetweenStores_Direct64x8() => StoreWithSweepBetweenStoresDirect64x8(_longSameReference);
    private static int StoreBatchDirect64x8(string[] values) {
        var pool = new StringPoolDirect64x8();
        uint acc = 0;
        foreach (string value in values) {
            acc ^= pool.Store(value).Packed;
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }

    private static int StoreWithSweepBetweenStoresDirect64x8(string[] values) {
        var pool = new StringPoolDirect64x8();
        uint acc = 0;
        foreach (string value in values) {
            SlotHandle handle = pool.Store(value);
            acc ^= handle.Packed;
            pool.BeginMark();
            pool.MarkReachable(handle);
            pool.Sweep();
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }
#endregion
#region Scalar64x4
    [Benchmark] public int ShortDistinct_Scalar64x4() => StoreBatchScalar64x4(_shortDistinct);
    [Benchmark] public int LongDistinct_Scalar64x4() => StoreBatchScalar64x4(_longDistinct);
    [Benchmark] public int LongSameReference_Scalar64x4() => StoreBatchScalar64x4(_longSameReference);
    [Benchmark] public int LongSameContentDifferentReference_Scalar64x4() => StoreBatchScalar64x4(_longSameContentDifferentReference);
    [Benchmark] public int LongSameReference_WithSweepBetweenStores_Scalar64x4() => StoreWithSweepBetweenStoresScalar64x4(_longSameReference);
    private static int StoreBatchScalar64x4(string[] values) {
        var pool = new StringPoolScalar64x4();
        uint acc = 0;
        foreach (string value in values) {
            acc ^= pool.Store(value).Packed;
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }

    private static int StoreWithSweepBetweenStoresScalar64x4(string[] values) {
        var pool = new StringPoolScalar64x4();
        uint acc = 0;
        foreach (string value in values) {
            SlotHandle handle = pool.Store(value);
            acc ^= handle.Packed;
            pool.BeginMark();
            pool.MarkReachable(handle);
            pool.Sweep();
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }
#endregion
#region Scalar64x8
    [Benchmark] public int ShortDistinct_Scalar64x8() => StoreBatchScalar64x8(_shortDistinct);
    [Benchmark] public int LongDistinct_Scalar64x8() => StoreBatchScalar64x8(_longDistinct);
    [Benchmark] public int LongSameReference_Scalar64x8() => StoreBatchScalar64x8(_longSameReference);
    [Benchmark] public int LongSameContentDifferentReference_Scalar64x8() => StoreBatchScalar64x8(_longSameContentDifferentReference);
    [Benchmark] public int LongSameReference_WithSweepBetweenStores_Scalar64x8() => StoreWithSweepBetweenStoresScalar64x8(_longSameReference);
    private static int StoreBatchScalar64x8(string[] values) {
        var pool = new StringPoolScalar64x8();
        uint acc = 0;
        foreach (string value in values) {
            acc ^= pool.Store(value).Packed;
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }

    private static int StoreWithSweepBetweenStoresScalar64x8(string[] values) {
        var pool = new StringPoolScalar64x8();
        uint acc = 0;
        foreach (string value in values) {
            SlotHandle handle = pool.Store(value);
            acc ^= handle.Packed;
            pool.BeginMark();
            pool.MarkReachable(handle);
            pool.Sweep();
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }
#endregion
#region Simd64x8
    [Benchmark] public int ShortDistinct_Simd64x8() => StoreBatchSimd64x8(_shortDistinct);
    [Benchmark] public int LongDistinct_Simd64x8() => StoreBatchSimd64x8(_longDistinct);
    [Benchmark] public int LongSameReference_Simd64x8() => StoreBatchSimd64x8(_longSameReference);
    [Benchmark] public int LongSameContentDifferentReference_Simd64x8() => StoreBatchSimd64x8(_longSameContentDifferentReference);
    [Benchmark] public int LongSameReference_WithSweepBetweenStores_Simd64x8() => StoreWithSweepBetweenStoresSimd64x8(_longSameReference);
    private static int StoreBatchSimd64x8(string[] values) {
        var pool = new StringPoolSimd64x8();
        uint acc = 0;
        foreach (string value in values) {
            acc ^= pool.Store(value).Packed;
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }

    private static int StoreWithSweepBetweenStoresSimd64x8(string[] values) {
        var pool = new StringPoolSimd64x8();
        uint acc = 0;
        foreach (string value in values) {
            SlotHandle handle = pool.Store(value);
            acc ^= handle.Packed;
            pool.BeginMark();
            pool.MarkReachable(handle);
            pool.Sweep();
        }
        return unchecked((int)(acc ^ (uint)pool.Count));
    }
#endregion
    private static string CreateLongString(string prefix, int index)
        => $"{prefix}:{index:X8}:{new string((char)('a' + (index % 26)), 96)}";
}
