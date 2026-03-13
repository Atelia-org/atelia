using Atelia.Data;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

// 仅占位，尚未实现
internal class MixedListImpl : DurableList {
    public override DurableObjectKind Kind => DurableObjectKind.MixedList;
    public override bool HasChanges => false;

    internal MixedListImpl() {
    }

    public override void DiscardChanges() => throw new NotImplementedException();
    internal override SizedPtr LatestVersionTicket => throw new NotImplementedException();
    internal override bool HasBeenSaved => throw new NotImplementedException();
    internal override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) => throw new NotImplementedException();
    internal override FrameTag WritePendingDiff(IDiffWriter writer, DiffWriteContext context) => throw new NotImplementedException();
    internal override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr previousVersion) => throw new NotImplementedException();
    internal override void OnLoadCompleted(SizedPtr versionTicket) => throw new NotImplementedException();
}
