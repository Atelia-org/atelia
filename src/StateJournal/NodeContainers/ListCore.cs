namespace Atelia.StateJournal.NodeContainers;

internal struct ListCore<T> {
    private T[] _items;
    private int _count;
    public int Count => _count;

    public ListCore() {
        _items = [];
        _count = 0;
    }

    private void EnsureCapacity(int requiredCapacity) {
        int currentCapacity = _items?.Length ?? 0;
        if (requiredCapacity <= currentCapacity) { return; }

        int newCapacity = currentCapacity == 0 ? 4 : currentCapacity;
        while (newCapacity < requiredCapacity) {
            newCapacity *= 2;
        }

        if (_items is null) {
            _items = new T[newCapacity];
            return;
        }

        Array.Resize(ref _items, newCapacity);
    }

    public void Add(T item) {
        EnsureCapacity(_count + 1);
        _items[_count++] = item;
    }

    public bool TryPop(out T item) {
        if (_count <= 0) {
            item = default!;
            return false;
        }
        item = _items[--_count];
        _items[_count] = default!;
        return true;
    }

    public void Clear() {
        if (_items is null) {
            _count = 0;
            return;
        }

        Array.Clear(_items, 0, _count);
        _count = 0;
    }
}
