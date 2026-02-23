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
            _wordIdx = bmp._wordsPerSlab - 1; // 使首次 MoveNext 直接进入 slab 搜索
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
            _current = (_slabIdx << _bmp._slabShift) | (_wordIdx << 6) | bit;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool MoveNextSlow() {
            while (true) {
                // 正序扫描当前 slab 中剩余 words
                while (_wordIdx < _bmp._wordsPerSlab - 1) {
                    _wordIdx++;
                    _remaining = _bmp._data[_slabIdx][_wordIdx];
                    if (_remaining != 0) {
                        EmitBit();
                        return true;
                    }
                }

                // 当前 slab 耗尽，通过 _slabHasOne 正序跳到下一个有 `1` 的 slab
                int from = _slabIdx >= 0 ? _slabIdx + 1 : 0;
                int nextSlab = _bmp.FindSlabWithOneForward(from);
                if (nextSlab < 0) { return false; }

                Debug.Assert(_bmp._oneCounts[nextSlab] > 0,
                    $"_slabHasOne indicated slab {nextSlab} has ones but _oneCounts is {_bmp._oneCounts[nextSlab]}."
                );

                _slabIdx = nextSlab;
                _wordIdx = -1; // 下次 while 步进到 word 0
            }
        }
    }

    // ───────────────────── ZerosReverseEnumerator ─────────────────────

    ref partial struct ZerosReverseEnumerator {
        private readonly SlabBitmap _bmp;
        private int _current, _slabIdx, _wordIdx;
        private ulong _remaining; // ~word：bit=1 表示原始 word 中对应位为 0

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ZerosReverseEnumerator(SlabBitmap bmp) {
            _bmp = bmp;
            _slabIdx = bmp._slabCount; // 哨兵：尚未进入任何 slab
            _wordIdx = 0;              // 使首次 MoveNext 直接进入 slab 搜索
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
            _current = (_slabIdx << _bmp._slabShift) | (_wordIdx << 6) | bit;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool MoveNextSlow() {
            while (true) {
                // 逆序扫描当前 slab 中剩余 words
                while (_wordIdx > 0) {
                    _wordIdx--;
                    _remaining = ~_bmp._data[_slabIdx][_wordIdx];
                    if (_remaining != 0) {
                        EmitBit();
                        return true;
                    }
                }

                // 当前 slab 耗尽，通过 _slabAllOne 逆序跳到下一个有 `0` 的 slab
                int from = _slabIdx < _bmp._slabCount ? _slabIdx - 1 : _bmp._slabCount - 1;
                int nextSlab = _bmp.FindSlabWithZeroReverse(from);
                if (nextSlab < 0) { return false; }

                Debug.Assert(_bmp._oneCounts[nextSlab] < _bmp._slabSize,
                    $"_slabAllOne indicated slab {nextSlab} has zeros but _oneCounts is {_bmp._oneCounts[nextSlab]}."
                );

                _slabIdx = nextSlab;
                _wordIdx = _bmp._wordsPerSlab; // 下次 while 步进到最后一个 word
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
