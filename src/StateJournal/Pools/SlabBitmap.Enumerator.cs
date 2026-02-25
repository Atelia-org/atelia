using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

// ai:facade `src/StateJournal/Pools/SlabBitmap.cs`
// ai:test `tests/StateJournal.Tests/SlabBitmapTests.Enumerator.cs`
partial class SlabBitmap {
    // ───────────────────── OnesForwardEnumerator ─────────────────────

    ref partial struct OnesForwardEnumerator {
        private readonly SlabBitmap _bmp;
        private int _current, _slabIdx, _wordIdx;
        private ulong _remaining;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal OnesForwardEnumerator(SlabBitmap bmp) {
            _bmp = bmp;
            _slabIdx = -1;
            _wordIdx = WordsPerSlab; // sentinel: forces slab advance on first MoveNextSlow
            _remaining = 0;
            _current = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public partial bool MoveNext() {
            if (_remaining != 0) {
                EmitBit();
                return true;
            }
            return MoveNextSlow();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitBit() {
            int bit = BitOperations.TrailingZeroCount(_remaining);
            _remaining ^= 1UL << bit;
            _current = (_slabIdx << SlabShift) | (_wordIdx << 6) | bit;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool MoveNextSlow() {
            while (true) {
                // L1: find next word with ones in current slab
                if (_slabIdx >= 0 && _wordIdx < WordsPerSlab - 1) {
                    ulong l1bits = _bmp._l1HasOne[_slabIdx] & (ulong.MaxValue << (_wordIdx + 1));
                    if (l1bits != 0) {
                        _wordIdx = BitOperations.TrailingZeroCount(l1bits);
                        _remaining = _bmp._data[_slabIdx][_wordIdx];
                        Debug.Assert(_remaining != 0);
                        EmitBit();
                        return true;
                    }
                }

                // L2: find next slab with ones
                int nextSlab = _bmp.FindSlabWithOneForward(_slabIdx + 1);
                if (nextSlab < 0) { return false; }

                _slabIdx = nextSlab;
                ulong l1 = _bmp._l1HasOne[nextSlab];
                Debug.Assert(l1 != 0, $"L2 indicated slab {nextSlab} has ones but L1 is zero.");
                _wordIdx = BitOperations.TrailingZeroCount(l1);
                _remaining = _bmp._data[nextSlab][_wordIdx];
                Debug.Assert(_remaining != 0);
                EmitBit();
                return true;
            }
        }
    }

    // ───────────────────── ZerosReverseEnumerator ─────────────────────

    ref partial struct ZerosReverseEnumerator {
        private readonly SlabBitmap _bmp;
        private int _current, _slabIdx, _wordIdx;
        private ulong _remaining; // ~word: bit=1 means original bit was 0

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ZerosReverseEnumerator(SlabBitmap bmp) {
            _bmp = bmp;
            _slabIdx = bmp._slabCount; // sentinel: no slab entered yet
            _wordIdx = -1;              // forces slab advance on first MoveNextSlow
            _remaining = 0;
            _current = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public partial bool MoveNext() {
            if (_remaining != 0) {
                EmitBit();
                return true;
            }
            return MoveNextSlow();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitBit() {
            int bit = 63 - BitOperations.LeadingZeroCount(_remaining);
            _remaining ^= 1UL << bit;
            _current = (_slabIdx << SlabShift) | (_wordIdx << 6) | bit;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool MoveNextSlow() {
            while (true) {
                // L1: find prev word with zeros in current slab
                if (_slabIdx < _bmp._slabCount && _wordIdx > 0) {
                    ulong candidates = _bmp._l1HasZero[_slabIdx] & (ulong.MaxValue >> (64 - _wordIdx));
                    if (candidates != 0) {
                        _wordIdx = 63 - BitOperations.LeadingZeroCount(candidates);
                        _remaining = ~_bmp._data[_slabIdx][_wordIdx];
                        Debug.Assert(_remaining != 0);
                        EmitBit();
                        return true;
                    }
                }

                // L2: find prev slab with zeros
                int from = _slabIdx < _bmp._slabCount ? _slabIdx - 1 : _bmp._slabCount - 1;
                int prevSlab = _bmp.FindSlabWithZeroReverse(from);
                if (prevSlab < 0) { return false; }

                _slabIdx = prevSlab;
                ulong l1hz = _bmp._l1HasZero[prevSlab];
                Debug.Assert(l1hz != 0, $"L2 indicated slab {prevSlab} has zeros but L1 says all-one.");
                _wordIdx = 63 - BitOperations.LeadingZeroCount(l1hz);
                _remaining = ~_bmp._data[prevSlab][_wordIdx];
                Debug.Assert(_remaining != 0);
                EmitBit();
                return true;
            }
        }
    }

    // ───────────────────── CompactionEnumerator ─────────────────────

    ref partial struct CompactionEnumerator {
        private OnesForwardEnumerator _ones;
        private ZerosReverseEnumerator _zeros;
        private (int One, int Zero) _current;
        private bool _exhausted;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal CompactionEnumerator(SlabBitmap bmp) {
            _ones = new OnesForwardEnumerator(bmp);
            _zeros = new ZerosReverseEnumerator(bmp);
            _current = default;
            _exhausted = false;
        }

        public partial bool MoveNext() {
            if (_exhausted) { return false; }
            if (!_ones.MoveNext() || !_zeros.MoveNext()) {
                _exhausted = true;
                return false;
            }
            if (_ones.Current >= _zeros.Current) {
                _exhausted = true;
                return false;
            }
            _current = (_ones.Current, _zeros.Current);
            return true;
        }
    }
}
