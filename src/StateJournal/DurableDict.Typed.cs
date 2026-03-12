using Atelia.Data;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public abstract class DurableDict<TKey, TValue> : DurableObject, IDict<TKey>, IDict<TKey, TValue>
where TKey : notnull where TValue : notnull {
    /// <summary>由<see cref="TypedDictFactory{TKey, TValue}"/>初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    #region Core
    private protected DictChangeTracker<TKey, TValue> _core;
    #endregion
    internal DurableDict() {
    }

    #region DurableObject
    public override ValueKind Kind => ValueKind.MixedDict;
    public override bool HasChanges => _core.HasChanges;
    #endregion

    // ── IDict<TKey> ─────────────────────────────────────────────

    public bool ContainsKey(TKey key) => _core.Current.ContainsKey(key);
    public int Count => _core.Current.Count;
    public abstract bool Remove(TKey key);

    /// <summary>所有键的枚举。</summary>
    public IEnumerable<TKey> Keys => _core.Current.Keys;

    // ── IDict<TKey, TValue> ─────────────────────────────────────

    public GetIssue Get(TKey key, out TValue? value) {
        return _core.Current.TryGetValue(key, out value)
            ? GetIssue.None
            : GetIssue.NotFound;
    }

    public abstract UpsertStatus Upsert(TKey key, TValue? value);

    #region Version Chain

    private protected VersionChainStatus _versionStatus; // TODO: 如果确定了所有DurableObject都采用delta chain模型，则上移到DurableObject中；如果某些类型使用共享成员，则保持，不过由于RBF已被设计为粗粒度读写，估计难以支持共享成员（比如红黑树）那种琐碎的节点读写。
    internal override SizedPtr LatestVersionTicket => _versionStatus.CommittedVersion;
    internal override bool HasBeenSaved => _versionStatus.HasCommittedVersion;

    #endregion
}
