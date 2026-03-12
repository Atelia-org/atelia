using System.Diagnostics;
using Atelia.Data;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

// AI TODO: 将DurableDict<TKey>改为abstract。用本类型中的_core字段具体实现DurableDict<TKey>。
internal class MixedDictImpl<TKey, KHelper> : DurableDict<TKey>
    where TKey : notnull
    where KHelper : unmanaged, ITypeHelper<TKey> {
    public override ValueKind Kind => ValueKind.MixedDict;
    internal MixedDictImpl() {
        _core = new();
    }

    public override void DiscardChanges() {
        _core.Revert<ValueBoxHelper>();
    }

    internal override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) {
        if (context.WasRebase) {
            _versionStatus.UpdateRebased(versionTicket, context.EffectiveRebaseSize);
        }
        else {
            _versionStatus.UpdateDeltified(versionTicket, context.EffectiveDeltifySize);
        }
        _core.Commit<ValueBoxHelper>();
    }

    internal override FrameTag WritePendingDiff(IDiffWriter writer, DiffWriteContext context) {
        uint rebaseSize = (uint)_core.RebaseCount + (uint)TypeCode.Length;
        uint deltifySize = (uint)_core.DeltifyCount;
        bool doRebase = context.ForceRebase || _versionStatus.ShouldRebase(rebaseSize, deltifySize);
        if (doRebase) {
            context.SetOutcome(wasRebase: true, rebaseSize, deltifySize);
            writer.WriteBytes(TypeCode); // 非空TypeCode表示rebase frame
            _versionStatus.WriteRebase(writer, rebaseSize);
            _core.WriteRebase<KHelper, ValueBoxHelper>(writer, context);
            return new(UsageKind.Blank, ObjectKind.MixedDict, VersionKind.Rebase);
        }
        else {
            context.SetOutcome(wasRebase: false, rebaseSize, deltifySize);
            writer.WriteBytes(null); // 空TypeCode表示deltify frame
            _versionStatus.WriteDeltify(writer, deltifySize);
            _core.WriteDeltify<KHelper, ValueBoxHelper>(writer, context);
            return new(UsageKind.Blank, ObjectKind.MixedDict, VersionKind.Delta);
        }
    }

    internal override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr previousVersion) {
        Debug.Assert(_core.RebaseCount == 0);
        Debug.Assert(_core.DeltifyCount == 0);
        _versionStatus.ApplyDelta(ref reader, previousVersion);
        _core.ApplyDelta<KHelper, ValueBoxHelper>(ref reader);
    }

    internal override void OnLoadCompleted(SizedPtr versionTicket) {
        _versionStatus.SetLoadedVersionTicket(versionTicket);
        _core.SyncCurrentFromCommitted<ValueBoxHelper>();
    }
}
