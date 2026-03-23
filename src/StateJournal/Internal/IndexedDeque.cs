using System.Numerics;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// Minimal indexed deque based on a ring buffer.
/// Supports O(1) push/pop at both ends and O(1) index access.
/// </summary>
internal sealed class IndexedDeque<T> {
    private const int DefaultCapacity = 4;

    private T[] _buffer;
    private int _head;
    private int _count;

    public IndexedDeque(int capacity = DefaultCapacity) {
        if (capacity < 0) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
        _buffer = new T[RoundUpToPowerOfTwo(Math.Max(DefaultCapacity, capacity))];
    }

    public int Count => _count;

    public T this[int index] {
        get {
            ValidateIndex(index);
            return _buffer[ToBufferIndex(index)];
        }
        set {
            ValidateIndex(index);
            _buffer[ToBufferIndex(index)] = value;
        }
    }

    public ref T GetRef(int index) {
        ValidateIndex(index);
        return ref _buffer[ToBufferIndex(index)];
    }

    public bool TryGetAt(int index, out T value) {
        if ((uint)index >= (uint)_count) {
            value = default!;
            return false;
        }

        value = _buffer[ToBufferIndex(index)];
        return true;
    }

    public bool TryPeekFront(out T value) {
        if (_count == 0) {
            value = default!;
            return false;
        }

        value = _buffer[_head];
        return true;
    }

    public bool TryPeekBack(out T value) {
        if (_count == 0) {
            value = default!;
            return false;
        }

        value = _buffer[ToBufferIndex(_count - 1)];
        return true;
    }

    /// <summary>
    /// Exposes the logical deque content as up to two contiguous buffer segments.
    /// The second segment is empty when the logical range does not wrap.
    /// </summary>
    public void GetSegments(out Span<T> first, out Span<T> second) =>
        GetSegments(0, _count, out first, out second);

    /// <summary>
    /// Exposes the logical deque content as up to two contiguous buffer segments.
    /// The second segment is empty when the logical range does not wrap.
    /// </summary>
    public void GetSegments(out int firstStartIndex, out Span<T> first, out int secondStartIndex, out Span<T> second) =>
        GetSegments(0, _count, out firstStartIndex, out first, out secondStartIndex, out second);

    /// <summary>
    /// Exposes a logical sub-range as up to two contiguous buffer segments.
    /// Returned segment start indices are logical indices in the deque.
    /// </summary>
    public void GetSegments(int index, int count, out Span<T> first, out Span<T> second) =>
        GetSegments(index, count, out _, out first, out _, out second);

    /// <summary>
    /// Exposes a logical sub-range as up to two contiguous buffer segments.
    /// Returned segment start indices are logical indices in the deque.
    /// </summary>
    public void GetSegments(int index, int count, out int firstStartIndex, out Span<T> first, out int secondStartIndex, out Span<T> second) {
        ValidateRange(index, count);

        firstStartIndex = index;
        secondStartIndex = index + count;

        if (count == 0) {
            first = [];
            second = [];
            return;
        }

        int firstBufferIndex = ToBufferIndex(index);
        int firstLength = Math.Min(count, _buffer.Length - firstBufferIndex);
        first = _buffer.AsSpan(firstBufferIndex, firstLength);

        int secondLength = count - firstLength;
        if (secondLength == 0) {
            second = [];
            return;
        }

        secondStartIndex = index + firstLength;
        second = _buffer.AsSpan(0, secondLength);
    }

    /// <summary>
    /// Reserves logical slots at the front and exposes them as up to two writable segments
    /// ordered from new logical front to back. Caller must fill every returned element.
    /// </summary>
    public void ReserveFront(int count, out Span<T> first, out Span<T> second) {
        ValidateReserveCount(count);
        if (count == 0) {
            first = [];
            second = [];
            return;
        }

        EnsureCapacityFor(count);
        _head = (_head - count) & BufferMask;
        _count += count;
        GetSegments(0, count, out _, out first, out _, out second);
    }

    /// <summary>
    /// Reserves logical slots at the back and exposes them as up to two writable segments
    /// ordered from existing logical back towards the new tail. Caller must fill every returned element.
    /// </summary>
    public void ReserveBack(int count, out Span<T> first, out Span<T> second) {
        ValidateReserveCount(count);
        if (count == 0) {
            first = [];
            second = [];
            return;
        }

        EnsureCapacityFor(count);
        int startIndex = _count;
        _count += count;
        GetSegments(startIndex, count, out _, out first, out _, out second);
    }

    public void PushFront(T value) {
        EnsureCapacityFor(1);
        _head = (_head - 1) & BufferMask;
        _buffer[_head] = value;
        _count++;
    }

    public void PushBack(T value) {
        EnsureCapacityFor(1);
        _buffer[ToBufferIndex(_count)] = value;
        _count++;
    }

    public T PopFront() {
        if (TryPopFront(out T value)) { return value; }
        throw new InvalidOperationException("Deque is empty.");
    }

    public bool TryPopFront(out T value) {
        if (_count == 0) {
            value = default!;
            return false;
        }

        int idx = _head;
        value = _buffer[idx];
        _buffer[idx] = default!;
        _head = (_head + 1) & BufferMask;
        _count--;
        if (_count == 0) { _head = 0; }
        return true;
    }

    public T PopBack() {
        if (TryPopBack(out T value)) { return value; }
        throw new InvalidOperationException("Deque is empty.");
    }

    public bool TryPopBack(out T value) {
        if (_count == 0) {
            value = default!;
            return false;
        }

        int idx = ToBufferIndex(_count - 1);
        value = _buffer[idx];
        _buffer[idx] = default!;
        _count--;
        if (_count == 0) { _head = 0; }
        return true;
    }

    public void TrimFront(int count) {
        if (count == 0) { return; }
        if (count > _count) { throw new ArgumentOutOfRangeException(nameof(count)); }

        if (_head + count <= _buffer.Length) {
            Array.Clear(_buffer, _head, count);
        }
        else {
            int firstPart = _buffer.Length - _head;
            Array.Clear(_buffer, _head, firstPart);
            Array.Clear(_buffer, 0, count - firstPart);
        }

        _head = (_head + count) & BufferMask;
        _count -= count;
        if (_count == 0) { _head = 0; }
    }

    public void TrimBack(int count) {
        if (count == 0) { return; }
        if (count > _count) { throw new ArgumentOutOfRangeException(nameof(count)); }

        int startIdx = ToBufferIndex(_count - count);
        if (startIdx + count <= _buffer.Length) {
            Array.Clear(_buffer, startIdx, count);
        }
        else {
            int firstPart = _buffer.Length - startIdx;
            Array.Clear(_buffer, startIdx, firstPart);
            Array.Clear(_buffer, 0, count - firstPart);
        }

        _count -= count;
        if (_count == 0) { _head = 0; }
    }

    public void Clear() {
        if (_count == 0) { return; }

        if (_head + _count <= _buffer.Length) {
            Array.Clear(_buffer, _head, _count);
        }
        else {
            int firstPart = _buffer.Length - _head;
            Array.Clear(_buffer, _head, firstPart);
            Array.Clear(_buffer, 0, _count - firstPart);
        }

        _head = 0;
        _count = 0;
    }

    private void EnsureCapacityFor(int additionalCount) {
        if (additionalCount < 0) { throw new ArgumentOutOfRangeException(nameof(additionalCount)); }
        if (additionalCount == 0) { return; }
        if (additionalCount <= _buffer.Length - _count) { return; }

        int requiredCapacity = checked(_count + additionalCount);
        int newCapacity = _buffer.Length;
        while (newCapacity < requiredCapacity) {
            newCapacity = checked(newCapacity << 1);
        }
        T[] newBuffer = new T[newCapacity];
        CopyToContiguous(newBuffer);
        _buffer = newBuffer;
        _head = 0;
    }

    private void CopyToContiguous(T[] destination) {
        if (_count == 0) { return; }

        if (_head + _count <= _buffer.Length) {
            Array.Copy(_buffer, _head, destination, 0, _count);
        }
        else {
            int firstPart = _buffer.Length - _head;
            Array.Copy(_buffer, _head, destination, 0, firstPart);
            Array.Copy(_buffer, 0, destination, firstPart, _count - firstPart);
        }
    }

    private void ValidateIndex(int index) {
        if ((uint)index >= (uint)_count) { throw new ArgumentOutOfRangeException(nameof(index)); }
    }

    private void ValidateRange(int index, int count) {
        if ((uint)index > (uint)_count) { throw new ArgumentOutOfRangeException(nameof(index)); }
        if ((uint)count > (uint)(_count - index)) { throw new ArgumentOutOfRangeException(nameof(count)); }
    }

    private static void ValidateReserveCount(int count) {
        if (count < 0) { throw new ArgumentOutOfRangeException(nameof(count)); }
    }

    private int BufferMask => _buffer.Length - 1;

    private int ToBufferIndex(int logicalIndex) => (_head + logicalIndex) & BufferMask;

    private static int RoundUpToPowerOfTwo(int value) {
        uint rounded = BitOperations.RoundUpToPowerOf2((uint)value);
        if (rounded == 0) { throw new OverflowException("Capacity is too large to round up to a power of two."); }

        return checked((int)rounded);
    }
}
