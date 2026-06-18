using System.Diagnostics;
using Atelia.Data;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal;

/// <summary>类型化可持久化哈希集合。key 支持矩阵与 dict key 保持一致。</summary>
public abstract class DurableHashSet<T> : DurableObject, ICommittedMutableForkable<DurableHashSet<T>>
    where T : notnull {
    private protected VersionChainStatus _versionStatus;

    /// <summary>由 <see cref="TypedHashSetFactory{TKey}"/> 初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    internal DurableHashSet() { }

    private protected abstract uint EstimatedRebaseBytes { get; }
    private protected abstract uint EstimatedDeltifyBytes { get; }

    private protected abstract void CommitCore();
    private protected abstract void SyncCurrentFromCommittedCore();
    private protected abstract void SyncFrozenCurrentFromCommittedCore();
    private protected abstract void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context);
    private protected abstract void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context);
    private protected abstract void ApplyDeltaCore(ref BinaryDiffReader reader);

    public override DurableObjectKind Kind => DurableObjectKind.TypedHashSet;

    public abstract int Count { get; }
    public abstract IReadOnlyCollection<T> Items { get; }
    public abstract bool Contains(T value);
    public abstract bool Add(T value);
    public abstract bool Remove(T value);

    internal sealed override SizedPtr HeadTicket => _versionStatus.Head;
    internal sealed override bool IsTracked => _versionStatus.IsTracked;
    internal sealed override ObjectVersionFlags VersionObjectFlags => _versionStatus.ObjectFlags;

    internal sealed override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) {
        ObjectVersionFlags objectFlags = CurrentObjectFlags;
        if (context.WasRebase) {
            _versionStatus.UpdateRebased(versionTicket, context.EffectiveRebaseSize, objectFlags);
        }
        else {
            _versionStatus.UpdateDeltified(versionTicket, context.EffectiveDeltifySize, objectFlags);
        }

        CommitCore();
        ClearCommittedPersistenceFlags();
        SetState(DurableState.Clean);
    }

    internal sealed override FrameTag WritePendingDiff(BinaryDiffWriter writer, ref DiffWriteContext context) {
        Debug.Assert(context.FrameSource != FrameSource.Blank, "FrameSource must be explicitly set");

        uint rebaseSize = checked(EstimatedRebaseBytes + CostEstimateUtil.WriteBytesSize(TypeCode));
        uint deltifySize = checked(EstimatedDeltifyBytes + CostEstimateUtil.WriteBytesSize(default));
        bool doRebase = context.ForceRebase || ForceRebaseForFrozenSnapshot || _versionStatus.ShouldRebase(rebaseSize, deltifySize);
        ObjectVersionFlags objectFlags = CurrentObjectFlags;

        if (doRebase) {
            context.SetOutcome(wasRebase: true, rebaseSize, deltifySize);
            writer.WriteBytes(TypeCode);
            _versionStatus.WriteRebase(writer, rebaseSize, objectFlags);
            WriteRebaseCore(writer, context);
            return new(VersionKind.Rebase, Kind, context.FrameUsage, context.FrameSource);
        }

        context.SetOutcome(wasRebase: false, rebaseSize, deltifySize);
        writer.WriteBytes(null);
        _versionStatus.WriteDeltify(writer, deltifySize, objectFlags);
        WriteDeltifyCore(writer, context);
        return new(VersionKind.Delta, Kind, context.FrameUsage, context.FrameSource);
    }

    internal sealed override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket) {
        AssertReconstructionOnlyState();
        _versionStatus.ApplyDelta(ref reader, parentTicket);
        ApplyDeltaCore(ref reader);
    }

    internal sealed override void OnLoadCompleted(SizedPtr versionTicket) {
        _versionStatus.SetHead(versionTicket);
        ApplyLoadedObjectFlags(_versionStatus.ObjectFlags);
        if (IsFrozen) {
            SyncFrozenCurrentFromCommittedCore();
        }
        else {
            SyncCurrentFromCommittedCore();
        }
        SetState(DurableState.Clean);
    }

    /// <summary>
    /// Fork this object's committed state into a new mutable object with a fresh LocalId.
    /// Pending working changes on the source are ignored.
    /// </summary>
    public DurableHashSet<T> ForkCommittedAsMutable() =>
        Revision.ForkCommittedAsMutable(this);
}
