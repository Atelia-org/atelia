using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

// 对应 DurObjDictImpl 的 Deque 版本，内部以 LocalId 存储 DurableObject 引用。
internal class DurObjDequeImpl<T> : DurableDeque<T>
    where T : DurableObject {
    private DequeChangeTracker<LocalId> _core;

    internal DurObjDequeImpl() {
        _core = new();
    }

    public override bool HasChanges => _core.HasChanges;
    public override int Count => _core.Current.Count;
    public override void PushFront(T? value) => _core.PushFront<LocalIdAsRefHelper>(ToLocalId(value));
    public override void PushBack(T? value) => _core.PushBack<LocalIdAsRefHelper>(ToLocalId(value));
    public override GetIssue GetAt(int index, out T? value) {
        if (!_core.TryGetAt(index, out var localId)) {
            value = null;
            return GetIssue.OutOfRange;
        }
        return Load(localId, out value);
    }
    public override GetIssue PeekFront(out T? value) {
        if (!_core.TryPeekFront(out var localId)) {
            value = null;
            return GetIssue.NotFound;
        }
        return Load(localId, out value);
    }
    public override GetIssue PeekBack(out T? value) {
        if (!_core.TryPeekBack(out var localId)) {
            value = null;
            return GetIssue.NotFound;
        }
        return Load(localId, out value);
    }
    public override bool TrySetAt(int index, T? value) {
        if ((uint)index >= (uint)_core.Current.Count) { return false; }
        SetCore(index, ToLocalId(value));
        return true;
    }
    public override bool TrySetFront(T? value) {
        if (_core.Current.Count == 0) { return false; }
        SetCore(0, ToLocalId(value));
        return true;
    }
    public override bool TrySetBack(T? value) {
        if (_core.Current.Count == 0) { return false; }
        SetCore(_core.Current.Count - 1, ToLocalId(value));
        return true;
    }
    public override GetIssue PopFront(out T? value) {
        var issue = PeekFront(out value);
        if (issue != GetIssue.None) { return issue; }
        if (!_core.TryPopFront<LocalIdAsRefHelper>(out _, out _)) { throw new InvalidOperationException("Deque state changed unexpectedly between peek and pop."); }
        return GetIssue.None;
    }
    public override GetIssue PopBack(out T? value) {
        var issue = PeekBack(out value);
        if (issue != GetIssue.None) { return issue; }
        if (!_core.TryPopBack<LocalIdAsRefHelper>(out _, out _)) { throw new InvalidOperationException("Deque state changed unexpectedly between peek and pop."); }
        return GetIssue.None;
    }

    internal override void DiscardChanges() => _core.Revert<LocalIdAsRefHelper>();

    private protected override int RebaseCount => _core.RebaseCount;
    private protected override int DeltifyCount => _core.DeltifyCount;

    private protected override void CommitCore() => _core.Commit<LocalIdAsRefHelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<LocalIdAsRefHelper>();
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<LocalIdAsRefHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<LocalIdAsRefHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<LocalIdAsRefHelper>(ref reader);

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        _core.Current.GetSegments(out Span<LocalId> first, out Span<LocalId> second);
        VisitSegment(ref visitor, first);
        VisitSegment(ref visitor, second);
    }

    private bool SetCore(int index, LocalId value) => _core.SetAt<LocalIdAsRefHelper>(index, value);

    private LocalId ToLocalId(T? value) {
        if (value is not null) { Revision.EnsureCanReference(value); }
        return value?.LocalId ?? LocalId.Null;
    }

    private GetIssue Load(LocalId localId, out T? value) {
        value = null;
        if (localId.IsNull) { return GetIssue.None; }

        var loadResult = Revision.Load(localId);
        if (loadResult.IsFailure) { return GetIssue.LoadFailed; }
        if (loadResult.Value is not T typed) { return GetIssue.LoadFailed; }

        value = typed;
        return GetIssue.None;
    }

    private static void VisitSegment<TVisitor>(ref TVisitor visitor, Span<LocalId> segment)
        where TVisitor : IChildRefVisitor, allows ref struct {
        foreach (var localId in segment) {
            if (!localId.IsNull) { visitor.Visit(localId); }
        }
    }
}
