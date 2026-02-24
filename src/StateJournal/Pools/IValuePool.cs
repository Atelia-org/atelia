namespace Atelia.StateJournal.Pools;

internal interface IValuePool<T> where T : notnull {
    SlotHandle Store(T value);

    void Free(SlotHandle handle);

    T this[SlotHandle handle] { get; }

    bool TryGetValue(SlotHandle handle, out T value);

    bool Validate(SlotHandle handle);

    int Count { get; }

    int Capacity { get; }
}
