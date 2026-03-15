using Atelia.Data;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

// 仅占位，尚未实现。对应 DurableObjectDictImpl 的 List 版本，
// 未来将内部以 LocalId 存储 DurableObject 引用，Get 时通过 Commit.Load 懒加载。
internal class DurObjListImpl<T> : DurableList<T>
    where T : DurableObject {
    public override DurableObjectKind Kind => DurableObjectKind.TypedList;
    public override bool HasChanges => false;

    internal DurObjListImpl() {
    }

    public override void DiscardChanges() => throw new NotImplementedException();
    internal override SizedPtr HeadTicket => throw new NotImplementedException();
    internal override bool IsTracked => throw new NotImplementedException();
    internal override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) => throw new NotImplementedException();
    internal override FrameTag WritePendingDiff(BinaryDiffWriter writer, DiffWriteContext context) => throw new NotImplementedException();
    internal override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket) => throw new NotImplementedException();
    internal override void OnLoadCompleted(SizedPtr versionTicket) => throw new NotImplementedException();

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) { }
}
