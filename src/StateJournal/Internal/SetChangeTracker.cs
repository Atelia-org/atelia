using System.Diagnostics;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal struct SetChangeTracker<T> where T : notnull {
    private IEqualityComparer<T>? _comparer;
    private HashSet<T>? _committed;
    private HashSet<T> _current;
    private bool _isDirty;

    public readonly bool IsFrozen => _committed is null;
    public readonly bool HasChanges => !IsFrozen && _isDirty;
    public readonly int Count => _current.Count;
    public readonly IReadOnlyCollection<T> Items => _current.ToArray();
    internal readonly HashSet<T> Current => _current;

    private readonly HashSet<T> Committed => _committed ?? throw new InvalidOperationException("Frozen SetChangeTracker has no mutable committed set.");
    private readonly IEnumerable<T> CommittedOrCurrent => _committed ?? _current;

    public SetChangeTracker() {
        _comparer = null;
        _committed = [];
        _current = [];
        _isDirty = false;
    }

    public bool Contains<THelper>(T value)
        where THelper : unmanaged, ITypeHelper<T> {
        ArgumentNullException.ThrowIfNull(value);
        return EnsureCurrentSet<THelper>().Contains(value);
    }

    public bool Add<THelper>(T value)
        where THelper : unmanaged, ITypeHelper<T> {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(value);
        HashSet<T> current = EnsureCurrentSet<THelper>();
        if (current.Contains(value)) { return false; }

        if (TryGetCommittedCanonical<THelper>(value, out T committedValue)) {
            current.Add(committedValue);
            ReleaseCanonicalizedIncomingIfNeeded<THelper>(committedValue, value);
        }
        else {
            current.Add(value);
        }

        RecomputeDirty();
        return true;
    }

    public bool Remove<THelper>(T value)
        where THelper : unmanaged, ITypeHelper<T> {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(value);
        HashSet<T> current = EnsureCurrentSet<THelper>();
        if (!current.TryGetValue(value, out T? removed)) { return false; }

        current.Remove(removed);
        if (THelper.NeedRelease && !EnsureCommittedSet<THelper>().Contains(removed)) {
            THelper.ReleaseSlot(removed);
        }

        RecomputeDirty();
        return true;
    }

    public readonly uint EstimatedRebaseBytes<THelper>()
        where THelper : unmanaged, ITypeHelper<T> =>
        checked(CostEstimateUtil.VarIntSize(0u) + EstimateSnapshotBytes<THelper>(_current));

    public readonly uint EstimatedDeltifyBytes<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        if (IsFrozen || !_isDirty) { return checked(CostEstimateUtil.VarIntSize(0u) + CostEstimateUtil.VarIntSize(0u)); }

        uint removedBytes = 0;
        uint addedBytes = 0;
        int removedCount = 0;
        int addedCount = 0;
        foreach (T item in EnumerateRemovedItems()) {
            removedCount++;
            removedBytes = checked(removedBytes + THelper.EstimateBareSize(item, asKey: true));
        }
        foreach (T item in EnumerateAddedItems()) {
            addedCount++;
            addedBytes = checked(addedBytes + THelper.EstimateBareSize(item, asKey: true));
        }

        return checked(
            CostEstimateUtil.VarIntSize((uint)removedCount)
            + removedBytes
            + CostEstimateUtil.VarIntSize((uint)addedCount)
            + addedBytes
        );
    }

    public void Revert<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        ThrowIfFrozen();
        if (!_isDirty) { return; }

        ReleaseItemsNotInCommitted<THelper>(_current);
        _current = FreezeItems<THelper>(Committed);
        _isDirty = false;
    }

    public void Commit<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        if (IsFrozen || !_isDirty) { return; }

        ReleaseItemsNotInCurrentCommittedOnly<THelper>();

        HashSet<T> frozen = FreezeItems<THelper>(_current);
        _committed = frozen;
        _current = new HashSet<T>(frozen, EnsureComparer<THelper>());
        _isDirty = false;
    }

    public void FreezeFromClean<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        ThrowIfFrozen();
        if (_isDirty) { throw new InvalidOperationException("Cannot FreezeFromClean while hash set has changes."); }

        _current = FreezeItems<THelper>(_current);
        _committed = null;
    }

    public void FreezeFromCurrent<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        ThrowIfFrozen();
        Commit<THelper>();
        FreezeFromClean<THelper>();
    }

    public void UnfreezeToMutableClean<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        if (!IsFrozen) { throw new InvalidOperationException("SetChangeTracker is not frozen."); }
        _committed = new HashSet<T>(_current, EnsureComparer<THelper>());
        _isDirty = false;
    }

    public void MaterializeFrozenFromReconstructedCommitted<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        if (IsFrozen) { throw new InvalidOperationException("SetChangeTracker is already frozen."); }
        Debug.Assert(_current.Count == 0, "MaterializeFrozenFromReconstructedCommitted should run on an empty current set.");

        _current = FreezeItems<THelper>(Committed);
        _committed = null;
        _isDirty = false;
    }

    public SetChangeTracker<T> ForkMutableFromCommitted<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        IEqualityComparer<T> comparer = EnsureComparer<THelper>();
        var fork = new SetChangeTracker<T> {
            _comparer = comparer,
            _committed = new HashSet<T>(comparer),
            _current = new HashSet<T>(comparer),
            _isDirty = false
        };

        foreach (T item in CommittedOrCurrent) {
            T cloned = THelper.ForkFrozenForNewOwner(item)!;
            fork._committed.Add(cloned);
            fork._current.Add(cloned);
        }

        return fork;
    }

    public void WriteRebase<THelper>(BinaryDiffWriter writer, DiffWriteContext context)
        where THelper : unmanaged, ITypeHelper<T> =>
        WriteItems<THelper>(writer, null, _current);

    public void WriteDeltify<THelper>(BinaryDiffWriter writer, DiffWriteContext context)
        where THelper : unmanaged, ITypeHelper<T> {
        if (IsFrozen || !_isDirty) {
            WriteItems<THelper>(writer, null, []);
            return;
        }

        WriteItems<THelper>(writer, EnumerateRemovedItems(), EnumerateAddedItems());
    }

    public void ApplyDelta<THelper>(ref BinaryDiffReader reader)
        where THelper : unmanaged, ITypeHelper<T> {
        if (_current.Count != 0) { throw new InvalidOperationException("ApplyDelta requires _current to be empty. Apply deltas during deserialization, then call SyncCurrentFromCommitted."); }

        HashSet<T> committed = EnsureCommittedSet<THelper>();

        int removedCount = reader.ReadCount();
        for (int i = 0; i < removedCount; ++i) {
            T? read = THelper.Read(ref reader, asKey: true);
            if (read is null) { throw new InvalidDataException("DurableHashSet does not support null elements."); }

            if (!committed.TryGetValue(read, out T? removed)) { throw new InvalidDataException("DurableHashSet delta removes an element that does not exist in committed state."); }

            committed.Remove(removed);
            if (THelper.NeedRelease) {
                THelper.ReleaseSlot(removed);
            }
        }

        int addedCount = reader.ReadCount();
        for (int i = 0; i < addedCount; ++i) {
            T? read = THelper.Read(ref reader, asKey: true);
            if (read is null) { throw new InvalidDataException("DurableHashSet does not support null elements."); }

            if (!committed.Add(read)) { throw new InvalidDataException("DurableHashSet delta contains duplicate added elements."); }
        }

        _current.Clear();
        _isDirty = false;
    }

    public void SyncCurrentFromCommitted<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        Debug.Assert(_current.Count == 0, "SyncCurrentFromCommitted should run on an empty current set.");
        _current = FreezeItems<THelper>(Committed);
        _isDirty = false;
    }

    internal readonly IEnumerable<T> ReconstructedOrCurrent => _current.Count != 0 ? _current : CommittedOrCurrent;

    private readonly void ThrowIfFrozen() {
        if (IsFrozen) { throw new InvalidOperationException("Frozen SetChangeTracker cannot be modified."); }
    }

    private void RecomputeDirty() {
        _isDirty = !IsFrozen && !_current.SetEquals(Committed);
    }

    private readonly IEnumerable<T> EnumerateRemovedItems() {
        foreach (T item in Committed) {
            if (!_current.Contains(item)) {
                yield return item;
            }
        }
    }

    private readonly IEnumerable<T> EnumerateAddedItems() {
        foreach (T item in _current) {
            if (!Committed.Contains(item)) {
                yield return item;
            }
        }
    }

    private static uint EstimateSnapshotBytes<THelper>(IReadOnlyCollection<T> items)
        where THelper : ITypeHelper<T> {
        uint bytes = CostEstimateUtil.VarIntSize((uint)items.Count);
        foreach (T item in items) {
            bytes = checked(bytes + THelper.EstimateBareSize(item, asKey: true));
        }
        return bytes;
    }

    private static void WriteItems<THelper>(BinaryDiffWriter writer, IEnumerable<T>? removedItems, IEnumerable<T> addedItems)
        where THelper : ITypeHelper<T> {
        int removedCount = removedItems?.Count() ?? 0;
        writer.WriteCount(removedCount);
        if (removedItems is not null) {
            foreach (T item in removedItems) {
                THelper.Write(writer, item, asKey: true);
            }
        }

        int addedCount = addedItems.Count();
        writer.WriteCount(addedCount);
        foreach (T item in addedItems) {
            THelper.Write(writer, item, asKey: true);
        }
    }

    private void ReleaseItemsNotInCommitted<THelper>(IEnumerable<T> items)
        where THelper : ITypeHelper<T> {
        if (!THelper.NeedRelease) { return; }

        foreach (T item in items) {
            if (!Committed.Contains(item)) {
                THelper.ReleaseSlot(item);
            }
        }
    }

    private void ReleaseItemsNotInCurrentCommittedOnly<THelper>()
        where THelper : ITypeHelper<T> {
        if (!THelper.NeedRelease) { return; }

        foreach (T item in Committed) {
            if (!_current.Contains(item)) {
                THelper.ReleaseSlot(item);
            }
        }
    }

    private bool TryGetCommittedCanonical<THelper>(T value, out T committedValue)
        where THelper : unmanaged, ITypeHelper<T> {
        if (EnsureCommittedSet<THelper>().TryGetValue(value, out committedValue!)) { return true; }

        committedValue = default!;
        return false;
    }

    private static void ReleaseCanonicalizedIncomingIfNeeded<THelper>(T committedValue, T incomingValue)
        where THelper : ITypeHelper<T> {
        if (!THelper.NeedRelease) { return; }
        if (!typeof(T).IsValueType && ReferenceEquals(committedValue, incomingValue)) { return; }
        THelper.ReleaseSlot(incomingValue);
    }

    private IEqualityComparer<T> EnsureComparer<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        var comparer = TypeHelperEqualityComparer<T, THelper>.Instance;
        if (_comparer is null) {
            _comparer = comparer;
            _current = new HashSet<T>(_current, comparer);
            if (_committed is not null) {
                _committed = new HashSet<T>(_committed, comparer);
            }
            return comparer;
        }

        if (!ReferenceEquals(_comparer, comparer)) { throw new InvalidOperationException($"SetChangeTracker helper mismatch: existing comparer is '{_comparer.GetType().Name}', requested '{typeof(THelper).Name}'."); }

        return _comparer;
    }

    private HashSet<T> EnsureCurrentSet<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        _ = EnsureComparer<THelper>();
        return _current;
    }

    private HashSet<T> EnsureCommittedSet<THelper>()
        where THelper : unmanaged, ITypeHelper<T> {
        _ = EnsureComparer<THelper>();
        return Committed;
    }

    private HashSet<T> FreezeItems<THelper>(IEnumerable<T> source)
        where THelper : unmanaged, ITypeHelper<T> {
        var frozen = new HashSet<T>(EnsureComparer<THelper>());
        foreach (T item in source) {
            frozen.Add(THelper.Freeze(item)!);
        }
        return frozen;
    }
}
