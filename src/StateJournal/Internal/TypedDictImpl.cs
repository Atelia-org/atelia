using System.Diagnostics;
using System.Runtime.InteropServices;
using Atelia.Data;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

// AI TODO: 将DurableDict<TKey, TValue>改为abstract。用本类型中的_core字段具体实现DurableDict<TKey, TValue>。
internal class TypedDictImpl<TKey, TValue, KHelper, VHelper> : DurableDict<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {
    internal TypedDictImpl() {
        _core = new();
    }

    public override UpsertStatus Upsert(TKey key, TValue? value) {
        ref TValue? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_core.Current, key, out bool exists);
        slot = value;
        _core.AfterUpsert<VHelper>(key, value);
        return exists ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    public override bool Remove(TKey key) {
        if (!_core.Current.Remove(key, out TValue? removedValue)) { return false; }
        _core.AfterRemove<VHelper>(key, removedValue);
        return true;
    }

    public override void DiscardChanges() {
        _core.Revert<VHelper>();
    }

    internal override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) {
        if (context.WasRebase) {
            _versionStatus.UpdateRebased(versionTicket, context.EffectiveRebaseSize);
        }
        else {
            _versionStatus.UpdateDeltified(versionTicket, context.EffectiveDeltifySize);
        }
        _core.Commit<VHelper>();
    }

    internal override FrameTag WritePendingDiff(IDiffWriter writer, DiffWriteContext context) {
        uint rebaseSize = (uint)_core.RebaseCount + (uint)TypeCode.Length;
        uint deltifySize = (uint)_core.DeltifyCount;
        bool doRebase = context.ForceRebase || _versionStatus.ShouldRebase(rebaseSize, deltifySize);
        if (doRebase) {
            context.SetOutcome(wasRebase: true, rebaseSize, deltifySize);
            writer.WriteBytes(TypeCode); // 非空TypeCode表示rebase frame
            _versionStatus.WriteRebase(writer, rebaseSize);
            _core.WriteRebase<KHelper, VHelper>(writer, context);
            return new(UsageKind.Blank, ObjectKind.TypedDict, VersionKind.Rebase);
        }
        else {
            context.SetOutcome(wasRebase: false, rebaseSize, deltifySize);
            writer.WriteBytes(null); // 空TypeCode表示deltify frame
            _versionStatus.WriteDeltify(writer, deltifySize);
            _core.WriteDeltify<KHelper, VHelper>(writer, context);
            return new(UsageKind.Blank, ObjectKind.TypedDict, VersionKind.Delta);
        }
    }

    internal override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr previousVersion) {
        Debug.Assert(_core.RebaseCount == 0);
        Debug.Assert(_core.DeltifyCount == 0);
        _versionStatus.ApplyDelta(ref reader, previousVersion);
        _core.ApplyDelta<KHelper, VHelper>(ref reader);
    }

    internal override void OnLoadCompleted(SizedPtr versionTicket) {
        _versionStatus.SetLoadedVersionTicket(versionTicket);
        _core.SyncCurrentFromCommitted<VHelper>();
    }
}
