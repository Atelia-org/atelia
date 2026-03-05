namespace Atelia.StateJournal.Internal;

internal class TypedListImpl<T, VHelper> : DurableList<T>
    where T : notnull
    where VHelper : unmanaged, ITypeHelper<T> {
    public override ValueKind Kind => ValueKind.TypedList;
    public override bool HasChanges => false;

    internal TypedListImpl() {
    }

    public override void DiscardChanges() {
    }

    internal override void OnCommitSucceeded() {
    }

    internal override void WritePendingDiff(IDiffWriter writer) {
    }
}
