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

        // Resize L0 per-slab arrays
        if (slabIdx >= _data.Length) {
            int newLen = _data.Length * 2;
            Array.Resize(ref _data, newLen);
            Array.Resize(ref _oneCounts, newLen);
        }

        // Resize L1 arrays (grows-only)
        if (slabIdx >= _l1HasOne.Length) {
            int newLen = _l1HasOne.Length * 2;
            Array.Resize(ref _l1HasOne, newLen);
            Array.Resize(ref _l1HasZero, newLen);
        }

        // Resize L2 arrays (grows-only)
        int l2Idx = slabIdx >> 6;
        if (l2Idx >= _l2HasOne.Length) {
            int newLen = _l2HasOne.Length * 2;
            Array.Resize(ref _l2HasOne, newLen);
            Array.Resize(ref _l2HasZero, newLen);
        }

        var words = new ulong[WordsPerSlab];
        // L2 bits for this slot are guaranteed clean (ShrinkLastSlab clears both;
        // Array.Resize zeroes new elements), so only Set is needed — no defensive Clear.
        if (allOne) {
            words.AsSpan().Fill(ulong.MaxValue);
            _oneCounts[slabIdx] = SlabSize;
            _l1HasOne[slabIdx] = ulong.MaxValue;
            _l1HasZero[slabIdx] = 0;
            SetL2HasOne(slabIdx);
        }
        else {
            _oneCounts[slabIdx] = 0;
            _l1HasOne[slabIdx] = 0;
            _l1HasZero[slabIdx] = ulong.MaxValue;
            SetL2HasZero(slabIdx);
        }
        _data[slabIdx] = words;

        _slabCount++;
        _capacity += SlabSize;
    }

    public partial void ShrinkLastSlab() {
        if (_slabCount == 0) { throw new InvalidOperationException("No slabs to shrink."); }

        int last = --_slabCount;
        _capacity -= SlabSize;
        _data[last] = null!;
        _oneCounts[last] = 0;
        _l1HasOne[last] = 0;     // clear L1 (array not resized)
        _l1HasZero[last] = 0;    // clear L1 (array not resized)
        ClearL2HasOne(last);      // clear L2 bit
        ClearL2HasZero(last);     // clear L2 bit
    }

    // ───────────────────── Per-bit operations ─────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public partial bool Test(int index) {
        Debug.Assert((uint)index < (uint)_capacity, $"Index {index} out of range [0, {_capacity}).");
        int slabIdx = index >> SlabShift;
        int wordIdx = (index >> 6) & 63;
        int bitIdx = index & 63;
        return (_data[slabIdx][wordIdx] & (1UL << bitIdx)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public partial void Set(int index) {
        Debug.Assert((uint)index < (uint)_capacity, $"Index {index} out of range [0, {_capacity}).");
        int slabIdx = index >> SlabShift;
        int wordIdx = (index >> 6) & 63;
        int bitIdx = index & 63;

        ref ulong word = ref _data[slabIdx][wordIdx];
        ulong bit = 1UL << bitIdx;
        ulong old = word;
        if ((old & bit) != 0) { return; /* already 1, idempotent */ }

        word = old | bit;
        ++_oneCounts[slabIdx];

        // L0→L1 hasOne: word went from 0 to non-zero
        if (old == 0) {
            ulong oldL1H = _l1HasOne[slabIdx];
            _l1HasOne[slabIdx] = oldL1H | (1UL << wordIdx);
            // L1→L2: slab went from no-ones to has-ones
            if (oldL1H == 0) { _l2HasOne[slabIdx >> 6] |= 1UL << (slabIdx & 63); }
        }

        // L0→L1 hasZero: word became all-one → no longer has zeros
        if (word == ulong.MaxValue) {
            ulong newL1Z = _l1HasZero[slabIdx] & ~(1UL << wordIdx);
            _l1HasZero[slabIdx] = newL1Z;
            // L1→L2: slab became all-one (no word has zeros)
            if (newL1Z == 0) { _l2HasZero[slabIdx >> 6] &= ~(1UL << (slabIdx & 63)); }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public partial void Clear(int index) {
        Debug.Assert((uint)index < (uint)_capacity, $"Index {index} out of range [0, {_capacity}).");
        int slabIdx = index >> SlabShift;
        int wordIdx = (index >> 6) & 63;
        int bitIdx = index & 63;

        ref ulong word = ref _data[slabIdx][wordIdx];
        ulong bit = 1UL << bitIdx;
        ulong old = word;
        if ((old & bit) == 0) { return; /* already 0, idempotent */ }

        word = old & ~bit;
        --_oneCounts[slabIdx];

        // L0→L1 hasOne: word became zero
        if (word == 0) {
            ulong newL1H = _l1HasOne[slabIdx] & ~(1UL << wordIdx);
            _l1HasOne[slabIdx] = newL1H;
            // L1→L2: slab became all-zero
            if (newL1H == 0) { _l2HasOne[slabIdx >> 6] &= ~(1UL << (slabIdx & 63)); }
        }

        // L0→L1 hasZero: word was all-one, now has zeros
        if (old == ulong.MaxValue) {
            ulong oldL1Z = _l1HasZero[slabIdx];
            _l1HasZero[slabIdx] = oldL1Z | (1UL << wordIdx);
            // L1→L2: slab was all-one (no zeros), now has zeros
            if (oldL1Z == 0) { _l2HasZero[slabIdx >> 6] |= 1UL << (slabIdx & 63); }
        }
    }

    // ───────────────────── Bulk set / clear ─────────────────────

    public partial void SetAll() {
        for (int s = 0; s < _slabCount; s++) {
            _data[s].AsSpan().Fill(ulong.MaxValue);
            _oneCounts[s] = SlabSize;
            _l1HasOne[s] = ulong.MaxValue;
            _l1HasZero[s] = 0;
            SetL2HasOne(s);
        }
        _l2HasZero.AsSpan().Clear();
    }

    public partial void ClearAll() {
        for (int s = 0; s < _slabCount; s++) {
            _data[s].AsSpan().Clear();
            _oneCounts[s] = 0;
            _l1HasOne[s] = 0;
            _l1HasZero[s] = ulong.MaxValue;
            SetL2HasZero(s);
        }
        _l2HasOne.AsSpan().Clear();
    }

    public partial void Not() {
        for (int s = 0; s < _slabCount; s++) {
            var words = _data[s];
            int count = 0;
            ulong hasOneAcc = 0;
            ulong hasZeroAcc = 0;
            for (int w = 0; w < WordsPerSlab; w++) {
                ulong r = ~words[w];
                words[w] = r;
                count += BitOperations.PopCount(r);
                if (r != 0) { hasOneAcc |= 1UL << w; }
                if (r != ulong.MaxValue) { hasZeroAcc |= 1UL << w; }
            }
            _oneCounts[s] = count;
            _l1HasOne[s] = hasOneAcc;
            _l1HasZero[s] = hasZeroAcc;
            UpdateL2HasOne(s, count);
            UpdateL2HasZero(s, count);
        }
    }

    // ───────────────────── Find ─────────────────────

    public partial int FindFirstOne() {
        // L2: find first slab with ones
        int slab = FindSlabWithOneForward(0);
        if (slab < 0) { return -1; }

        // L1: find first non-zero word in slab
        ulong l1 = _l1HasOne[slab];
        Debug.Assert(l1 != 0, "L2 said slab has ones but L1 is zero.");
        int wordIdx = BitOperations.TrailingZeroCount(l1);

        // L0: find first set bit in word
        ulong word = _data[slab][wordIdx];
        Debug.Assert(word != 0, "L1 said word has ones but word is zero.");
        int bitIdx = BitOperations.TrailingZeroCount(word);

        return (slab << SlabShift) | (wordIdx << 6) | bitIdx;
    }

    public partial int FindLastZero() {
        // L2: find last slab that is not all-one
        int slab = FindSlabWithZeroReverse(_slabCount - 1);
        if (slab < 0) { return -1; }

        // L1: find last word in slab that has zeros
        ulong hasZero = _l1HasZero[slab];
        Debug.Assert(hasZero != 0, "L2 said slab has zeros but L1 says all-one.");
        int wordIdx = 63 - BitOperations.LeadingZeroCount(hasZero);

        // L0: find last zero bit in word
        ulong wordNot = ~_data[slab][wordIdx];
        Debug.Assert(wordNot != 0, "L1 said word has zeros but word is all-one.");
        int bitIdx = 63 - BitOperations.LeadingZeroCount(wordNot);

        return (slab << SlabShift) | (wordIdx << 6) | bitIdx;
    }

    // ───────────────────── L2 helpers ─────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetL2HasOne(int slabIdx) => _l2HasOne[slabIdx >> 6] |= 1UL << (slabIdx & 63);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearL2HasOne(int slabIdx) => _l2HasOne[slabIdx >> 6] &= ~(1UL << (slabIdx & 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateL2HasOne(int slabIdx, int oneCount) {
        if (oneCount > 0) { SetL2HasOne(slabIdx); }
        else { ClearL2HasOne(slabIdx); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetL2HasZero(int slabIdx) => _l2HasZero[slabIdx >> 6] |= 1UL << (slabIdx & 63);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearL2HasZero(int slabIdx) => _l2HasZero[slabIdx >> 6] &= ~(1UL << (slabIdx & 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateL2HasZero(int slabIdx, int oneCount) {
        if (oneCount < SlabSize) { SetL2HasZero(slabIdx); }
        else { ClearL2HasZero(slabIdx); }
    }

    /// <summary>在 L2 中正序查找从 <paramref name="fromSlab"/>（含）开始第一个有 `1` 的 slab。找不到返回 -1。</summary>
    internal int FindSlabWithOneForward(int fromSlab) {
        if (fromSlab >= _slabCount) { return -1; }

        int summaryLen = (_slabCount + 63) >> 6;
        int si = fromSlab >> 6;
        int sb = fromSlab & 63;

        ulong w = _l2HasOne[si] & (ulong.MaxValue << sb);
        if (w != 0) {
            int slab = (si << 6) | BitOperations.TrailingZeroCount(w);
            return slab < _slabCount ? slab : -1;
        }

        for (int i = si + 1; i < summaryLen; i++) {
            w = _l2HasOne[i];
            if (w != 0) {
                int slab = (i << 6) | BitOperations.TrailingZeroCount(w);
                return slab < _slabCount ? slab : -1;
            }
        }

        return -1;
    }

    /// <summary>在 L2 中逆序查找从 <paramref name="fromSlab"/>（含）开始第一个非全满 slab。找不到返回 -1。</summary>
    internal int FindSlabWithZeroReverse(int fromSlab) {
        if (fromSlab < 0) { return -1; }

        int si = fromSlab >> 6;
        int sb = fromSlab & 63;

        ulong w = _l2HasZero[si] & (ulong.MaxValue >> (63 - sb));
        if (w != 0) { return (si << 6) | (63 - BitOperations.LeadingZeroCount(w)); }

        for (int i = si - 1; i >= 0; i--) {
            w = _l2HasZero[i];
            if (w != 0) { return (i << 6) | (63 - BitOperations.LeadingZeroCount(w)); }
        }

        return -1;
    }
}
