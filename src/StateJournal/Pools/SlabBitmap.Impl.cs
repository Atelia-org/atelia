using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

// ai:facade `src/StateJournal/Pools/SlabBitmap.cs`
// ai:test `tests/StateJournal.Tests/SlabBitmapTests.cs`
partial class SlabBitmap {
    // ───────────────────── Slab lifecycle ─────────────────────

    /// <summary>增长一个新 slab。</summary>
    /// <param name="allOne">false:all-zero; true:all-one.</param>
    private void GrowSlab(bool allOne) {
        int slabIdx = _slabCount;

        if (slabIdx >= _data.Length) {
            int newLen = _data.Length * 2;
            Array.Resize(ref _data, newLen);
            Array.Resize(ref _oneCounts, newLen);
        }

        int summaryIdx = slabIdx >> 6;
        if (summaryIdx >= _slabHasOne.Length) {
            Array.Resize(ref _slabHasOne, _slabHasOne.Length * 2);
            Array.Resize(ref _slabAllOne, _slabAllOne.Length * 2);
        }

        var words = new ulong[_wordsPerSlab];
        if (allOne) {
            words.AsSpan().Fill(ulong.MaxValue);
            _oneCounts[slabIdx] = _slabSize;
            SetSlabHasOne(slabIdx);
            SetSlabAllOne(slabIdx);
        }
        else {
            _oneCounts[slabIdx] = 0;
            // _slabHasOne bit stays 0
        }
        _data[slabIdx] = words;

        _slabCount++;
        _capacity += _slabSize;
    }

    public partial void ShrinkLastSlab() {
        if (_slabCount == 0) { throw new InvalidOperationException("No slabs to shrink."); }

        int last = --_slabCount;
        _capacity -= _slabSize;
        _data[last] = null!;
        _oneCounts[last] = 0;
        ClearSlabHasOne(last);
        ClearSlabAllOne(last);
    }

    // ───────────────────── Per-bit operations ─────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public partial bool Test(int index) {
        int slabIdx = index >> _slabShift;
        int local = index & _slabMask;
        return (_data[slabIdx][local >> 6] & (1UL << (local & 63))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public partial void Set(int index) {
        int slabIdx = index >> _slabShift;
        int local = index & _slabMask;
        ref ulong word = ref _data[slabIdx][local >> 6];
        ulong bit = 1UL << (local & 63);
        if ((word & bit) == 0) { // was `0` → now `1`
            word |= bit;
            int c = ++_oneCounts[slabIdx];
            if (c == 1) { SetSlabHasOne(slabIdx); }
            if (c == _slabSize) { SetSlabAllOne(slabIdx); }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public partial void Clear(int index) {
        int slabIdx = index >> _slabShift;
        int local = index & _slabMask;
        ref ulong word = ref _data[slabIdx][local >> 6];
        ulong bit = 1UL << (local & 63);
        if ((word & bit) != 0) { // was `1` → now `0`
            word &= ~bit;
            int c = --_oneCounts[slabIdx];
            if (c == 0) { ClearSlabHasOne(slabIdx); }
            if (c == _slabSize - 1) { ClearSlabAllOne(slabIdx); }
        }
    }

    // ───────────────────── Bulk set / clear ─────────────────────

    public partial void SetAll() {
        for (int s = 0; s < _slabCount; s++) {
            _data[s].AsSpan().Fill(ulong.MaxValue);
            _oneCounts[s] = _slabSize;
            SetSlabHasOne(s);
            SetSlabAllOne(s);
        }
    }

    public partial void ClearAll() {
        for (int s = 0; s < _slabCount; s++) {
            _data[s].AsSpan().Clear();
            _oneCounts[s] = 0;
        }
        _slabHasOne.AsSpan().Clear();
        _slabAllOne.AsSpan().Clear();
    }


    public partial void Not() {
        for (int s = 0; s < _slabCount; s++) {
            var words = _data[s];
            int count = 0;
            for (int w = 0; w < _wordsPerSlab; w++) {
                ulong r = ~words[w];
                words[w] = r;
                count += BitOperations.PopCount(r);
            }
            _oneCounts[s] = count;
            UpdateSlabHasOne(s, count);
            UpdateSlabAllOne(s, count);
        }
    }

    // ───────────────────── Find-First ─────────────────────

    public partial int FindFirstOne() {
        int slab = FindSlabWithOneForward(0);
        if (slab < 0) { return -1; }
        int found = ScanSlabForOne(slab);
        Debug.Assert(found >= 0, "_slabHasOne said slab has `1`s but scan found none.");
        return found;
    }

    public partial int FindLastZero() {
        int slab = FindSlabWithZeroReverse(_slabCount - 1);
        if (slab < 0) { return -1; }
        int found = ScanSlabForZeroReverse(slab);
        Debug.Assert(found >= 0, "_slabAllOne said slab has `0`s but scan found none.");
        return found;
    }

    // ───────────────────── _slabHasOne helpers ─────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetSlabHasOne(int slabIdx) => _slabHasOne[slabIdx >> 6] |= 1UL << (slabIdx & 63);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearSlabHasOne(int slabIdx) => _slabHasOne[slabIdx >> 6] &= ~(1UL << (slabIdx & 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateSlabHasOne(int slabIdx, int oneCount) {
        if (oneCount > 0) { SetSlabHasOne(slabIdx); }
        else { ClearSlabHasOne(slabIdx); }
    }

    // ───────────────────── _slabAllOne helpers ─────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetSlabAllOne(int slabIdx) => _slabAllOne[slabIdx >> 6] |= 1UL << (slabIdx & 63);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearSlabAllOne(int slabIdx) => _slabAllOne[slabIdx >> 6] &= ~(1UL << (slabIdx & 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateSlabAllOne(int slabIdx, int oneCount) {
        if (oneCount == _slabSize) { SetSlabAllOne(slabIdx); }
        else { ClearSlabAllOne(slabIdx); }
    }

    /// <summary>在 _slabHasOne 中正序查找从 <paramref name="fromSlab"/>（含）开始第一个有 `1` 的 slab。找不到返回 -1。</summary>
    private int FindSlabWithOneForward(int fromSlab) {
        if (fromSlab >= _slabCount) { return -1; }

        int summaryLen = (_slabCount + 63) >> 6;
        int si = fromSlab >> 6;
        int sb = fromSlab & 63;

        ulong w = _slabHasOne[si] & (ulong.MaxValue << sb);
        if (w != 0) {
            int slab = (si << 6) | BitOperations.TrailingZeroCount(w);
            return slab < _slabCount ? slab : -1;
        }

        for (int i = si + 1; i < summaryLen; i++) {
            w = _slabHasOne[i];
            if (w != 0) {
                int slab = (i << 6) | BitOperations.TrailingZeroCount(w);
                return slab < _slabCount ? slab : -1;
            }
        }

        return -1;
    }

    /// <summary>在 _slabAllOne 中逆序查找从 <paramref name="fromSlab"/>（含）开始第一个非全满 slab。找不到返回 -1。</summary>
    private int FindSlabWithZeroReverse(int fromSlab) {
        if (fromSlab < 0) { return -1; }

        int si = fromSlab >> 6;
        int sb = fromSlab & 63;

        ulong w = ~_slabAllOne[si] & (ulong.MaxValue >> (63 - sb));
        if (w != 0) { return (si << 6) | (63 - BitOperations.LeadingZeroCount(w)); }

        for (int i = si - 1; i >= 0; i--) {
            w = ~_slabAllOne[i];
            if (w != 0) { return (i << 6) | (63 - BitOperations.LeadingZeroCount(w)); }
        }

        return -1;
    }

    // ───────────────────── Scan helpers ─────────────────────

    /// <summary>在指定 slab 内正序扫描第一个 `1`。</summary>
    private int ScanSlabForOne(int slabIdx) {
        var data = _data[slabIdx];
        for (int w = 0; w < _wordsPerSlab; w++) {
            ulong word = data[w];
            if (word != 0) { return (slabIdx << _slabShift) | (w << 6) | BitOperations.TrailingZeroCount(word); }
        }
        return -1;
    }

    /// <summary>在指定 slab 内逆序扫描最后一个 `0`。</summary>
    private int ScanSlabForZeroReverse(int slabIdx) {
        var data = _data[slabIdx];
        for (int w = _wordsPerSlab - 1; w >= 0; w--) {
            ulong word = ~data[w];
            if (word != 0) {
                int bit = 63 - BitOperations.LeadingZeroCount(word);
                return (slabIdx << _slabShift) | (w << 6) | bit;
            }
        }
        return -1;
    }
}
