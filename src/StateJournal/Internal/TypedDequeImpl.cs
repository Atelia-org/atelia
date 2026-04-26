using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal class TypedDequeImpl<T, VHelper> : DurableDeque<T>
    where T : notnull
    where VHelper : unmanaged, ITypeHelper<T> {
    private DequeChangeTracker<T> _core;

    internal TypedDequeImpl() {
        _core = new();
    }

    #region DurableObject

    public override bool HasChanges => _core.HasChanges;

    #endregion

    #region DurableDeque<T>

    public override int Count => _core.Current.Count;
    public override void PushFront(T? value) => _core.PushFront<VHelper>(value);
    public override void PushBack(T? value) => _core.PushBack<VHelper>(value);
    public override GetIssue GetAt(int index, out T? value) {
        if (!_core.TryGetAt(index, out value)) {
            value = default;
            return GetIssue.OutOfRange;
        }
        return GetIssue.None;
    }
    public override GetIssue PeekFront(out T? value) {
        if (!_core.TryPeekFront(out value)) {
            value = default;
            return GetIssue.NotFound;
        }
        return GetIssue.None;
    }
    public override GetIssue PeekBack(out T? value) {
        if (!_core.TryPeekBack(out value)) {
            value = default;
            return GetIssue.NotFound;
        }
        return GetIssue.None;
    }
    public override bool TrySetAt(int index, T? value) {
        if ((uint)index >= (uint)_core.Current.Count) { return false; }
        SetCore(index, value);
        return true;
    }
    public override bool TrySetFront(T? value) {
        if (_core.Current.Count == 0) { return false; }
        SetCore(0, value);
        return true;
    }
    public override bool TrySetBack(T? value) {
        if (_core.Current.Count == 0) { return false; }
        SetCore(_core.Current.Count - 1, value);
        return true;
    }
    public override GetIssue PopFront(out T? value) {
        if (!_core.TryPopFront<VHelper>(out value, out bool callerOwned)) {
            value = default;
            return GetIssue.NotFound;
        }
        if (VHelper.NeedRelease && callerOwned) {
            VHelper.ReleaseSlot(value);
        }
        return GetIssue.None;
    }
    public override GetIssue PopBack(out T? value) {
        if (!_core.TryPopBack<VHelper>(out value, out bool callerOwned)) {
            value = default;
            return GetIssue.NotFound;
        }
        if (VHelper.NeedRelease && callerOwned) {
            VHelper.ReleaseSlot(value);
        }
        return GetIssue.None;
    }

    #endregion

    #region DurableDequeBase

    private protected override uint EstimatedRebaseBytes => _core.EstimatedRebaseBytes<VHelper>();
    private protected override uint EstimatedDeltifyBytes => _core.EstimatedDeltifyBytes<VHelper>();

    internal override void DiscardChanges() => _core.Revert<VHelper>();

    private protected override void CommitCore() => _core.Commit<VHelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<VHelper>();
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<VHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<VHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<VHelper>(ref reader);

    #endregion

    private void SetCore(int index, T? value) => _core.SetAt<VHelper>(index, value);

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        if (!VHelper.NeedVisitChildRefs) { return; }

        _core.Current.GetSegments(out Span<T?> first, out Span<T?> second);
        VisitSegment(ref visitor, first);
        VisitSegment(ref visitor, second);
    }

    private void VisitSegment<TVisitor>(ref TVisitor visitor, Span<T?> segment)
        where TVisitor : IChildRefVisitor, allows ref struct {
        foreach (var value in segment) {
            VHelper.VisitChildRefs(value, Revision, ref visitor);
        }
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? symbolPool) {
        if (tracker is null || !VHelper.NeedValidateReconstructed) { return null; }
        _core.Current.GetSegments(out Span<T?> first, out Span<T?> second);
        return ValidateSegment(first, tracker) ?? ValidateSegment(second, tracker);
    }

    private static AteliaError? ValidateSegment(Span<T?> segment, LoadPlaceholderTracker tracker) {
        foreach (T? value in segment) {
            if (VHelper.ValidateReconstructed(value, tracker, "TypedDeque") is { } error) { return error; }
        }
        return null;
    }
}
