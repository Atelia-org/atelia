using System.Diagnostics;

namespace Atelia.StateJournal.NodeContainers;

internal static class NodeHandleConstants {
    public const uint NullSequence = 0, MinSequence = NullSequence + 1;
    internal const int InvalidOneBasedIndex = 0;
}

internal struct LeafHandle(uint sequence) {
    public static LeafHandle Null => default;
    /// <summary>目标节点的持久身份。参与序列化。</summary>
    public uint Sequence = sequence;
    public bool IsNull => Sequence == NodeHandleConstants.NullSequence;
    public bool IsNotNull => Sequence != NodeHandleConstants.NullSequence;
    /// <summary>缓存对<see cref="Sequence"/>进行二分查找后的结果。0表示无效。不参与序列化。</summary>
    private int _oneBasedIndex;
    internal int CachedIndex {
        get {
            Debug.Assert(HasCachedIndex);
            return _oneBasedIndex - 1;
        }
        set {
            _oneBasedIndex = value + 1;
        }
    }
    internal bool HasCachedIndex => _oneBasedIndex != NodeHandleConstants.InvalidOneBasedIndex;
    internal bool MissingCachedIndex => _oneBasedIndex == NodeHandleConstants.InvalidOneBasedIndex;
    internal void ClearCachedIndex() => _oneBasedIndex = NodeHandleConstants.InvalidOneBasedIndex;
}
