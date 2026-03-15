namespace Atelia.StateJournal.Internal;

/// <summary>
/// 从 ObjectMap 的已有 keys 派生 LocalId 分配器。
/// 加载时一次性统计 holes（单个空洞和连续空洞段），运行时分配 O(1)。
/// </summary>
internal class LocalIdAllocator {
    private readonly Queue<uint> _singleHoles = new();
    private readonly List<HoleSpan> _holeSpans = new();
    private uint _nextId;

    /// <summary>下一个将被分配的 LocalId（高水位标记）。</summary>
    internal uint NextId => _nextId;

    internal LocalIdAllocator(uint nextId) {
        _nextId = nextId;
    }

    /// <summary>从 ObjectMap 已有的 key 集合构建分配器。</summary>
    internal static LocalIdAllocator FromKeys(IEnumerable<uint> keys) {
        uint[] sorted = keys.ToArray();
        Array.Sort(sorted);

        if (sorted.Length == 0) { return new LocalIdAllocator(1); }

        uint nextId = sorted[^1] + 1;
        var allocator = new LocalIdAllocator(nextId);

        uint expected = 1;
        foreach (uint id in sorted) {
            if (id > expected) {
                uint holeLength = id - expected;
                if (holeLength == 1) {
                    allocator._singleHoles.Enqueue(expected);
                }
                else {
                    allocator._holeSpans.Add(new HoleSpan(expected, holeLength));
                }
            }
            expected = id + 1;
        }

        // 反转使最低 span 位于末尾：[^1] 优先取最低 span，与 Queue 的 low-first 策略一致。
        // 这有利于活跃对象聚集在低 ID 区间，使 compaction 后 _nextId 回退幅度最大化。
        allocator._holeSpans.Reverse();

        return allocator;
    }

    /// <summary>分配一个新的 LocalId。优先复用空洞。</summary>
    internal LocalId Allocate() {
        if (_singleHoles.TryDequeue(out uint singleId)) { return new LocalId(singleId); }

        if (_holeSpans.Count > 0) {
            ref var span = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_holeSpans)[^1];
            uint id = span.Start;
            span = new HoleSpan(span.Start + 1, span.Count - 1);
            if (span.Count == 0) {
                _holeSpans.RemoveAt(_holeSpans.Count - 1);
            }
            return new LocalId(id);
        }

        if (_nextId == 0) { throw new LocalIdExhaustedException(); }
        return new LocalId(_nextId++);
    }

    internal readonly record struct HoleSpan(uint Start, uint Count);
}
