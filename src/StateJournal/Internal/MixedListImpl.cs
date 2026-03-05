namespace Atelia.StateJournal.Internal;

internal class MixedListImpl : DurableList {
    public override DurableValueKind Kind => DurableValueKind.MixedList;
    public override bool HasChanges => false;

    internal MixedListImpl() {
    }

    public override void DiscardChanges() {
    }

    internal override void OnCommitSucceeded() {
    }

    internal override void WritePendingDiff(IDiffWriter writer) {
    }
}
