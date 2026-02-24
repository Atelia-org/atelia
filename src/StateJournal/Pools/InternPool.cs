using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

/// <summary>
/// 基于 <see cref="SlotPool{T}"/> 的去重池（Intern Pool）。
/// 每个 distinct value 只存储一份，返回 stable slot index 作为 handle。
/// </summary>
/// <remarks>
///
///   核心操作 <see cref="Intern"/> 实现值去重：若池中已存在等价值则返回已有 index，
///   否则分配新 slot。值一经 intern 即不可变（集合 mutable，元素 immutable）。
///
///
///   <b>释放策略</b>：不内建引用计数。调用方（如外部可达性探测 / 简易 GC）
///   通过 <see cref="Free"/> 显式释放。
///   可通过 <see cref="Capacity"/> + <see cref="IsOccupied"/> 遍历所有活跃 slot。
///
///
///   <b>内部结构</b>：<c>SlotPool&lt;Entry&gt;</c> 提供 slab 式 alloc/free/index-access，
///   独立的 <c>int[] _buckets</c> + 桶内链表提供 O(1) 均摊的哈希查找。
///   本质上是 .NET Dictionary 的 <c>buckets[] + entries[]</c> 结构，
///   只是把 <c>entries[]</c> 替换为 SlotPool，获得稳定 index 和尾部自动回收。
///
/// </remarks>
/// <typeparam name="T">Interned 值类型，必须正确实现 GetHashCode / Equals（或提供 IEqualityComparer）。</typeparam>
internal sealed class InternPool<T> where T : notnull {

    private struct Entry {
        public T Value;
        public int Next;      // 同桶内下一个 entry 的 slot index，-1 = 链尾
        public int HashCode;  // 缓存（非负，已 & 0x7FFFFFFF）
    }

    private const int InitialBucketCount = 4;

    private readonly SlotPool<Entry> _slots;
    private readonly IEqualityComparer<T> _comparer;
    private int[] _buckets;    // _buckets[hash & mask] → chain head slot index, -1 = 空桶
    private int _bucketMask;   // buckets.Length - 1（2^n sizing，用 & 代替 %）

    /// <summary>池中去重后的活跃值数量。</summary>
    public int Count => _slots.Count;

    /// <summary>
    /// 底层 SlotPool 的容量上界。
    /// 外部 GC 可迭代 <c>[0, Capacity)</c> 配合 <see cref="IsOccupied"/> 遍历全部活跃 slot。
    /// </summary>
    public int Capacity => _slots.Capacity;

    /// <summary>创建一个空的 <see cref="InternPool{T}"/>。</summary>
    /// <param name="comparer">值相等比较器，null 时使用 <see cref="EqualityComparer{T}.Default"/>。</param>
    /// <param name="slabShift">底层 SlotPool 的 slabShift，控制每页大小 (2^slabShift)。</param>
    public InternPool(IEqualityComparer<T>? comparer = null, int slabShift = SlotPool<Entry>.DefaultSlabShift) {
        _slots = new SlotPool<Entry>(slabShift);
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _buckets = new int[InitialBucketCount];
        Array.Fill(_buckets, -1);
        _bucketMask = InitialBucketCount - 1;
    }

    // ───────────────────── Core: Intern ─────────────────────

    /// <summary>
    /// 去重获取 stable slot index。
    /// 若池中已存在等价值，返回已有 index；否则分配新 slot 并存入。O(1) 均摊。
    /// </summary>
    public int Intern(T value) {
        int hashCode = _comparer.GetHashCode(value) & 0x7FFFFFFF;
        int bucket = hashCode & _bucketMask;

        // 在链表中查找已有
        for (int i = _buckets[bucket]; i >= 0;) {
            ref Entry e = ref _slots.GetValueRef(i);
            if (e.HashCode == hashCode && _comparer.Equals(e.Value, value)) { return i; /* 去重命中 */ }
            i = e.Next;
        }

        // 未命中 → 分配新 slot
        var entry = new Entry {
            Value = value,
            HashCode = hashCode,
            Next = _buckets[bucket],
        };
        int slot = _slots.Alloc(entry).Index;
        _buckets[bucket] = slot;

        // 按需扩容 bucket（load factor ≈ 1.0）
        if (_slots.Count > _buckets.Length) {
            Rehash(_buckets.Length * 2);
        }

        return slot;
    }

    // ───────────────────── Lookup ─────────────────────

    /// <summary>查找池中是否存在等价值。若存在，通过 <paramref name="index"/> 返回其 slot index。</summary>
    public bool TryGetIndex(T value, out int index) {
        int hashCode = _comparer.GetHashCode(value) & 0x7FFFFFFF;
        int bucket = hashCode & _bucketMask;

        for (int i = _buckets[bucket]; i >= 0;) {
            ref Entry e = ref _slots.GetValueRef(i);
            if (e.HashCode == hashCode && _comparer.Equals(e.Value, value)) {
                index = i;
                return true;
            }
            i = e.Next;
        }

        index = -1;
        return false;
    }

    /// <summary>检查池中是否存在等价值。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(T value) => TryGetIndex(value, out _);

    // ───────────────────── Access by index ─────────────────────

    /// <summary>按 slot index 读取 interned 值。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出范围。</exception>
    /// <exception cref="InvalidOperationException">slot 未被占用。</exception>
    public T this[int index] {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slots.GetValueRef(index).Value;
    }

    /// <summary>按 slot index 获取 interned 值的只读引用，避免值类型拷贝。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出范围。</exception>
    /// <exception cref="InvalidOperationException">slot 未被占用。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T GetRef(int index) => ref _slots.GetValueRef(index).Value;

    /// <summary>尝试读取 slot 的值。若 index 无效或 slot 未占用，返回 false。O(1)。</summary>
    public bool TryGetValue(int index, out T value) {
        if (_slots.TryGetValue(index, out Entry entry)) {
            value = entry.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>检查指定 index 的 slot 是否已被占用。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出范围。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOccupied(int index) => _slots.IsOccupied(index);

    // ───────────────────── Lifecycle (for external GC) ─────────────────────

    /// <summary>
    /// 释放一个 slot：从哈希桶链中摘除，然后释放底层 slot。
    /// 供外部可达性探测 / GC 调用。O(1) 均摊。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出范围。</exception>
    /// <exception cref="InvalidOperationException">slot 未被占用（double free）。</exception>
    public void Free(int index) {
        // 读取 entry 信息（必须在 _slots.Free 之前）
        ref Entry entry = ref _slots.GetValueRef(index); // 同时验证 occupied
        int hashCode = entry.HashCode;
        int next = entry.Next;
        int bucket = hashCode & _bucketMask;

        // 从桶链表中摘除 index
        int prev = -1;
        int current = _buckets[bucket];
        while (current != index) {
            Debug.Assert(current >= 0, "Entry not found in bucket chain — internal state corrupted.");
            prev = current;
            current = _slots.GetValueRef(current).Next;
        }

        if (prev < 0) {
            _buckets[bucket] = next;
        }
        else {
            _slots.GetValueRef(prev).Next = next;
        }

        // 释放 slot（SlotPool 会自动回收尾部空页）
        _slots.Free(index);
    }

    // ───────────────────── Rehash ─────────────────────

    /// <summary>扩容并重建 bucket 数组。遍历全部活跃 slot 重新分桶。</summary>
    private void Rehash(int newSize) {
        Debug.Assert(BitOperations.IsPow2(newSize));

        var newBuckets = new int[newSize];
        Array.Fill(newBuckets, -1);
        int newMask = newSize - 1;

        int cap = _slots.Capacity;
        for (int i = 0; i < cap; i++) {
            if (!_slots.IsOccupied(i)) { continue; }

            ref Entry e = ref _slots.GetValueRef(i);
            int b = e.HashCode & newMask;
            e.Next = newBuckets[b];
            newBuckets[b] = i;
        }

        _buckets = newBuckets;
        _bucketMask = newMask;
    }
}
