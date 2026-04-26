using System.Diagnostics;
using Atelia.Data;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal;

/// <summary>
/// <see cref="DurableDict{TKey, TValue}"/>和<see cref="DurableDict{TKey}"/>的共享基类，
/// 持有<see cref="VersionChainStatus"/>，
/// 实现统一的持久化生命周期方法（OnCommitSucceeded、WritePendingDiff、ApplyDelta、OnLoadCompleted）。
/// 类型相关的操作通过 abstract hook 委托给具体子类。
/// <see cref="DictChangeTracker{TKey, TValue}"/> 由各实现类自持，本类通过 abstract 属性访问计数。
/// </summary>
public abstract class DurableDictBase<TKey> : DurableObject
    where TKey : notnull {
    private protected VersionChainStatus _versionStatus;

    #region Abstract Hooks

    private protected abstract uint EstimatedRebaseBytes { get; }
    private protected abstract uint EstimatedDeltifyBytes { get; }

    private protected abstract void CommitCore();
    private protected abstract void SyncCurrentFromCommittedCore();
    private protected abstract void SyncFrozenCurrentFromCommittedCore();
    private protected abstract void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context);
    private protected abstract void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context);
    private protected abstract void ApplyDeltaCore(ref BinaryDiffReader reader);

    #endregion

    #region Version Chain

    internal sealed override SizedPtr HeadTicket => _versionStatus.Head;
    internal sealed override bool IsTracked => _versionStatus.IsTracked;
    internal sealed override ObjectVersionFlags VersionObjectFlags => _versionStatus.ObjectFlags;

    #endregion

    #region Persistence Lifecycle

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

        // rebase frame 写 WriteBytes(TypeCode)，deltify frame 写 WriteBytes(null)；二者实际写出的字节都包含 VarUInt 长度前缀。
        uint rebaseSize = checked(EstimatedRebaseBytes + CostEstimateUtil.WriteBytesSize(TypeCode));
        uint deltifySize = checked(EstimatedDeltifyBytes + CostEstimateUtil.WriteBytesSize(default));
        bool doRebase = context.ForceRebase || ForceRebaseForFrozenSnapshot || _versionStatus.ShouldRebase(rebaseSize, deltifySize);
        ObjectVersionFlags objectFlags = CurrentObjectFlags;
        if (doRebase) {
            context.SetOutcome(wasRebase: true, rebaseSize, deltifySize);
            writer.WriteBytes(TypeCode); // 非空TypeCode表示rebase frame
            _versionStatus.WriteRebase(writer, rebaseSize, objectFlags);
            WriteRebaseCore(writer, context);
            return new(VersionKind.Rebase, Kind, context.FrameUsage, context.FrameSource);
        }
        else {
            context.SetOutcome(wasRebase: false, rebaseSize, deltifySize);
            writer.WriteBytes(null); // 空TypeCode表示deltify frame
            _versionStatus.WriteDeltify(writer, deltifySize, objectFlags);
            WriteDeltifyCore(writer, context);
            return new(VersionKind.Delta, Kind, context.FrameUsage, context.FrameSource);
        }
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

    #endregion
}
