namespace Atelia.StateJournal.Pools;

internal interface IValuePool<T> where T : notnull {
    int Store(T value);

    void Free(int handle);

    T this[int handle] { get; }

    bool TryGetValue(int handle, out T value);

    bool Validate(int handle);

    int Count { get; }

    int Capacity { get; }
}
