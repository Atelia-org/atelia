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

    public void PushFront(T value) {
        EnsureCapacityForOneMore();
        _head = (_head - 1) & BufferMask;
        _buffer[_head] = value;
        _count++;
    }

    public void PushBack(T value) {
        EnsureCapacityForOneMore();
        _buffer[ToBufferIndex(_count)] = value;
        _count++;
    }

    public T PopFront() {
        if (_count == 0) { throw new InvalidOperationException("Deque is empty."); }

        int idx = _head;
        T value = _buffer[idx];
        _buffer[idx] = default!;
        _head = (_head + 1) & BufferMask;
        _count--;
        if (_count == 0) { _head = 0; }
        return value;
    }

    public T PopBack() {
        if (_count == 0) { throw new InvalidOperationException("Deque is empty."); }

        int idx = ToBufferIndex(_count - 1);
        T value = _buffer[idx];
        _buffer[idx] = default!;
        _count--;
        if (_count == 0) { _head = 0; }
        return value;
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

    private void EnsureCapacityForOneMore() {
        if (_count < _buffer.Length) { return; }

        int newCapacity = checked(_buffer.Length << 1);
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

    private int BufferMask => _buffer.Length - 1;

    private int ToBufferIndex(int logicalIndex) => (_head + logicalIndex) & BufferMask;

    private static int RoundUpToPowerOfTwo(int value) {
        uint rounded = BitOperations.RoundUpToPowerOf2((uint)value);
        if (rounded == 0) { throw new OverflowException("Capacity is too large to round up to a power of two."); }

        return checked((int)rounded);
    }
}
