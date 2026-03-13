using System.Diagnostics;
using Atelia.Data;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal;

/// <summary>
/// <see cref="DurableDict{TKey, TValue}"/>和<see cref="DurableDict{TKey}"/>的共享基类，
/// 持有<see cref="DictChangeTracker{TKey, TValue}"/>和<see cref="VersionChainStatus"/>，
/// 实现统一的持久化生命周期方法（OnCommitSucceeded、WritePendingDiff、ApplyDelta、OnLoadCompleted）。
/// 类型相关的操作通过 abstract hook 委托给具体子类。
/// </summary>
public abstract class DurableDictBase<TKey, TValue> : DurableObject
    where TKey : notnull
    where TValue : notnull {
    private protected DictChangeTracker<TKey, TValue> _core;
    private protected VersionChainStatus _versionStatus;

    #region DurableObject

    public override bool HasChanges => _core.HasChanges;

    #endregion

    #region Abstract Hooks

    private protected abstract void CommitCore();
    private protected abstract void SyncCurrentFromCommittedCore();
    private protected abstract void WriteRebaseCore(IDiffWriter writer, DiffWriteContext context);
    private protected abstract void WriteDeltifyCore(IDiffWriter writer, DiffWriteContext context);
    private protected abstract void ApplyDeltaCore(ref BinaryDiffReader reader);

    #endregion

    #region Version Chain

    internal sealed override SizedPtr LatestVersionTicket => _versionStatus.CommittedVersion;
    internal sealed override bool HasBeenSaved => _versionStatus.HasCommittedVersion;

    #endregion

    #region Persistence Lifecycle

    internal sealed override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) {
        if (context.WasRebase) {
            _versionStatus.UpdateRebased(versionTicket, context.EffectiveRebaseSize);
        }
        else {
            _versionStatus.UpdateDeltified(versionTicket, context.EffectiveDeltifySize);
        }
        CommitCore();
    }

    internal sealed override FrameTag WritePendingDiff(IDiffWriter writer, DiffWriteContext context) {
        uint rebaseSize = (uint)_core.RebaseCount + (uint)TypeCode.Length;
        uint deltifySize = (uint)_core.DeltifyCount;
        bool doRebase = context.ForceRebase || _versionStatus.ShouldRebase(rebaseSize, deltifySize);
        if (doRebase) {
            context.SetOutcome(wasRebase: true, rebaseSize, deltifySize);
            writer.WriteBytes(TypeCode); // 非空TypeCode表示rebase frame
            _versionStatus.WriteRebase(writer, rebaseSize);
            WriteRebaseCore(writer, context);
            return new(UsageKind.Blank, Kind, VersionKind.Rebase);
        }
        else {
            context.SetOutcome(wasRebase: false, rebaseSize, deltifySize);
            writer.WriteBytes(null); // 空TypeCode表示deltify frame
            _versionStatus.WriteDeltify(writer, deltifySize);
            WriteDeltifyCore(writer, context);
            return new(UsageKind.Blank, Kind, VersionKind.Delta);
        }
    }

    internal sealed override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr previousVersion) {
        Debug.Assert(_core.RebaseCount == 0);
        Debug.Assert(_core.DeltifyCount == 0);
        _versionStatus.ApplyDelta(ref reader, previousVersion);
        ApplyDeltaCore(ref reader);
    }

    internal sealed override void OnLoadCompleted(SizedPtr versionTicket) {
        _versionStatus.SetLoadedVersionTicket(versionTicket);
        SyncCurrentFromCommittedCore();
    }

    #endregion
}
