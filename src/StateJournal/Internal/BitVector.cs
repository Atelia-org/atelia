using System.Diagnostics;
using System.Numerics;

namespace Atelia.StateJournal.Internal;

/// <summary>逻辑上的连续bit序列。使用 <c>ulong[]</c> 实现：SetBit/ClearBit/TestBit 均 O(1)。</summary>
internal struct BitVector {
    private ulong[]? _words;
    private int _popCount;
    private int _length;

    /// <summary>脏位置总数。</summary>
    public int PopCount => _popCount;

    /// <summary>当前逻辑长度（bit 数）。</summary>
    public int Length => _length;

    /// <summary>当前底层容量（bit 数）。仅用于测试观察容量伸缩。</summary>
    internal int Capacity => (_words?.Length ?? 0) << 6;

    public bool TestBit(int index) {
        if (_words is null || (uint)index >= (uint)_length) { return false; }
        int word = index >> 6;
        int bit = index & 63;
        return (_words[word] & (1UL << bit)) != 0;
    }

    public bool SetBit(int index) {
        Debug.Assert(_words is not null, "EnsureCapacity must be called before Add.");
        Debug.Assert((uint)index < (uint)_length, "index exceeds logical length.");
        if (_words is null || (uint)index >= (uint)_length) { return false; }

        int wordIndex = index >> 6;
        ulong mask = 1UL << (index & 63);
        ulong word = _words[wordIndex];
        if ((word & mask) != 0) { return false; }

        _words[wordIndex] = word | mask;
        _popCount++;
        return true;
    }

    public bool ClearBit(int index) {
        Debug.Assert(_words is not null, "EnsureCapacity must be called before Remove.");
        Debug.Assert((uint)index < (uint)_length, "index exceeds logical length.");
        if (_words is null || (uint)index >= (uint)_length) { return false; }

        int wordIndex = index >> 6;
        ulong mask = 1UL << (index & 63);
        ulong word = _words[wordIndex];
        if ((word & mask) == 0) { return false; }

        _words[wordIndex] = word & ~mask;
        _popCount--;
        return true;
    }

    public void Clear() {
        if (_words is not null) { Array.Clear(_words); }
        _popCount = 0;
    }

    /// <summary>
    /// 确保位图能容纳 [0, <paramref name="bitCount"/>) 范围的索引。
    /// 容量不足时扩展；利用率低于 1/3 时缩小到一半（使用乘法比较，避免除法）。
    /// 逻辑长度就是外界可见的 bit 序列长度；收缩时会裁掉逻辑范围外的 bit，
    /// 保证 Length 与可观察内容保持一致。
    /// 如果逻辑长度收缩到 0，会在没有剩余置位 bit 时释放底层数组。
    /// 应在 ResetTrackedWindow 时调用（Commit / Revert / SyncCurrentFromCommitted）。
    /// </summary>
    public void SetLength(int bitCount) {
        if (bitCount < 0) { throw new ArgumentOutOfRangeException(nameof(bitCount)); }
        int requiredWordCount = (bitCount + 63) >> 6;

        if (requiredWordCount == 0) {
            _popCount = 0;
            _words = null;
        }
        else if (_words is null) {
            _words = new ulong[requiredWordCount];
        }
        else if (_words.Length < requiredWordCount) {
            ResizeWords(requiredWordCount);
        }
        else {
            if (bitCount < _length) {
                ClearBitsForTruncate(bitCount, (_length + 63) >> 6);
            }

            // 允许在`_length == bitCount`时再次检查收缩
            if (requiredWordCount * 3 < _words.Length) {
                ResizeWords(Math.Max(_words.Length >> 1, requiredWordCount));
            }
        }

        _length = bitCount;
    }

    private void ClearBitsForTruncate(int startBitOffset, int endWordIndex) {
        Debug.Assert(_words is not null);
        if (_words is null) { return; }

        int wordIndex = startBitOffset >> 6;
        {
            int startShift = startBitOffset & 63;
            ulong startWord = _words[wordIndex];
            _popCount -= BitOperations.PopCount(startWord >> startShift); // 包含在擦除区间的高位部分
            _words[wordIndex] = startWord & ((1UL << startShift) - 1); // 保留低位，擦掉 [startShift, 64) 区间
        }
        while (++wordIndex < endWordIndex) {
            _popCount -= BitOperations.PopCount(_words[wordIndex]);
            _words[wordIndex] = 0;
        }
    }

    private void ResizeWords(int targetWordCount) {
        Debug.Assert(targetWordCount > 0);
        Debug.Assert(_words is not null);
        if (_words is null || _words.Length == targetWordCount) { return; }

        var resized = new ulong[targetWordCount];
        int copyWordCount = Math.Min(_words.Length, targetWordCount);
        Array.Copy(_words, resized, copyWordCount);
        _words = resized;
    }

    /// <summary>按升序枚举所有置位的索引。O(word_count + dirty_count)。</summary>
    public OnesEnumerator Ones() => new(_words, _length);

    /// <summary>按升序枚举所有置位的索引。</summary>
    public ref struct OnesEnumerator {
        private readonly ulong[]? _bits;
        private readonly int _wordCount;
        private int _wordIndex;
        private ulong _remaining;

        public int Current { get; private set; }

        internal OnesEnumerator(ulong[]? bits, int bitLength) {
            _bits = bits;
            _wordCount = (bitLength + 63) >> 6;
            _wordIndex = 0;
            _remaining = bits is not null && _wordCount > 0 ? bits[0] : 0;
            Current = -1;
        }

        public bool MoveNext() {
            while (_remaining == 0) {
                _wordIndex++;
                if (_bits is null || _wordIndex >= _wordCount) { return false; }
                _remaining = _bits[_wordIndex];
            }
            int bit = BitOperations.TrailingZeroCount(_remaining);
            _remaining &= _remaining - 1;
            Current = (_wordIndex << 6) + bit;
            return true;
        }

        public OnesEnumerator GetEnumerator() => this;
    }
}
