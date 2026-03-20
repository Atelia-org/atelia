using Atelia.Data;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

// 仅占位，尚未实现。对应 DurableObjectDictImpl 的 Deque 版本，
// 未来将内部以 LocalId 存储 DurableObject 引用，Get 时通过 Commit.Load 懒加载。
internal class DurObjDequeImpl<T> : DurableDeque<T>
    where T : DurableObject {
    public override DurableObjectKind Kind => DurableObjectKind.TypedDeque;
    public override bool HasChanges => false;

    internal DurObjDequeImpl() {
    }

    internal override void DiscardChanges() => throw new NotImplementedException();
    internal override SizedPtr HeadTicket => throw new NotImplementedException();
    internal override bool IsTracked => throw new NotImplementedException();
    internal override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) => throw new NotImplementedException();
    internal override FrameTag WritePendingDiff(BinaryDiffWriter writer, ref DiffWriteContext context) => throw new NotImplementedException();
    internal override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket) => throw new NotImplementedException();
    internal override void OnLoadCompleted(SizedPtr versionTicket) => throw new NotImplementedException();

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) { }

    internal override bool AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter) => false;
}
