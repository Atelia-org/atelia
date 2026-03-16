using Atelia.Data;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

// 仅占位，尚未实现
internal class TypedListImpl<T, VHelper> : DurableList<T>
    where T : notnull
    where VHelper : unmanaged, ITypeHelper<T> {
    public override DurableObjectKind Kind => DurableObjectKind.TypedList;
    public override bool HasChanges => false;

    internal TypedListImpl() {
    }

    internal override void DiscardChanges() => throw new NotImplementedException();
    internal override SizedPtr HeadTicket => throw new NotImplementedException();
    internal override bool IsTracked => throw new NotImplementedException();
    internal override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) => throw new NotImplementedException();
    internal override FrameTag WritePendingDiff(BinaryDiffWriter writer, ref DiffWriteContext context) => throw new NotImplementedException();
    internal override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket) => throw new NotImplementedException();
    internal override void OnLoadCompleted(SizedPtr versionTicket) => throw new NotImplementedException();

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) { }

    internal override void AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter) { }
}
