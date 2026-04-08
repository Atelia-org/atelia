using System.Diagnostics;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.NodeContainers;

internal interface INodeHandleVisitor {
    void VisitNodeHandle<TNode>(ref NodeHandle<TNode> handle) where TNode : struct;
}

internal interface INodeAccessor<TNode>
    where TNode : struct {
    static abstract void AcceptNodeVisitor<TVisitor>(in TVisitor visitor, ref TNode node) where TVisitor : struct, INodeVisitor<TNode>, allows ref struct;
}

internal interface INodeVisitor<TNode> where TNode : struct {
    void VisitNode(ref NodeHandle<TNode> handle);
}

readonly ref struct ArenaMarkContext<TNode>(ref NodeArena<TNode> arena, BitVector reachableBitmap, int reachableBase)
    where TNode : struct {
    private readonly ref NodeArena<TNode> _arena = ref arena;
    internal readonly BitVector _reachable = reachableBitmap;
    private readonly int _reachableBase = reachableBase;
    private static TNode s_empty;

    public ref TNode MarkAndReset(ref NodeHandle<TNode> handle, out bool isNewMarked) {
        ref TNode node = ref _arena.GetNodeRef(ref handle, out int index);
        handle.ClearCachedIndex();

        int reachableWindowIndex = index - _reachableBase;
        if (reachableWindowIndex < 0 || _reachable.TestBit(reachableWindowIndex)) {
            isNewMarked = false;
            return ref s_empty;
        }
        _reachable.SetBit(reachableWindowIndex);
        isNewMarked = true;
        return ref node;
    }
}

internal readonly ref struct NodeVisitor<TNode, NodeHelper> : INodeVisitor<TNode>
    where TNode : struct
    where NodeHelper : unmanaged, INodeAccessor<TNode> {

    private readonly ArenaMarkContext<TNode> _context;
    public NodeVisitor(in ArenaMarkContext<TNode> context) {
        _context = context;
    }

    public void VisitNode(ref NodeHandle<TNode> handle) {
        ref TNode node = ref _context.MarkAndReset(ref handle, out bool isNewMarked);
        if (!isNewMarked) { return; }

        NodeHelper.AcceptNodeVisitor(in this, ref node);
    }
}

internal interface IBranchAccessor<LeafNode, BranchNode> : INodeAccessor<LeafNode>
    where LeafNode : struct
    where BranchNode : struct {
    static abstract void AcceptLeafVisitor<TVisitor>(in TVisitor visitor, ref BranchNode node) where TVisitor : struct, INodeVisitor<LeafNode>, allows ref struct;
    static abstract void AcceptBranchVisitor<TVisitor>(in TVisitor visitor, ref BranchNode node) where TVisitor : struct, IBranchVisitor<BranchNode>, allows ref struct;
}

internal interface IBranchVisitor<BranchNode>
    where BranchNode : struct {
    void VisitBranch(ref NodeHandle<BranchNode> handle);
}

internal readonly ref struct BranchVisitor<LeafNode, LeafAccessor, BranchNode, BranchAccessor> : INodeVisitor<LeafNode>, IBranchVisitor<BranchNode>
    where LeafNode : struct
    where LeafAccessor : unmanaged, ITypeHelper<LeafNode>, INodeAccessor<LeafNode>
    where BranchNode : struct
    where BranchAccessor : unmanaged, ITypeHelper<BranchNode>, IBranchAccessor<LeafNode, BranchNode> {

    private readonly ArenaMarkContext<LeafNode> _leafContext;
    private readonly ArenaMarkContext<BranchNode> _branchContext;

    public BranchVisitor(in ArenaMarkContext<LeafNode> leafContext, in ArenaMarkContext<BranchNode> branchContext) {
        _leafContext = leafContext;
        _branchContext = branchContext;
    }

    public void VisitNode(ref NodeHandle<LeafNode> handle) {
        ref LeafNode node = ref _leafContext.MarkAndReset(ref handle, out bool isNewMarked);
        if (!isNewMarked) { return; }

        LeafAccessor.AcceptNodeVisitor(in this, ref node);
    }

    public void VisitBranch(ref NodeHandle<BranchNode> handle) {
        ref BranchNode node = ref _branchContext.MarkAndReset(ref handle, out bool isNewMarked);
        if (!isNewMarked) { return; }

        BranchAccessor.AcceptLeafVisitor(in this, ref node);
        BranchAccessor.AcceptBranchVisitor(in this, ref node);
    }

    /// <summary>用于一系列<see cref="NodeArena{TNode}.ApplyDelta{NodeHelper}(ref BinaryDiffReader)"/>后，<see cref="NodeArena{TNode}.SyncCurrentFromCommitted()"/>之前，
    /// 清理掉无法从<paramref name="root"/>访问到的节点。</summary>
    public static void CollectCommitted<LeafHelper, BranchHelper>(
        ref NodeArena<LeafNode> leafArena, ref NodeArena<BranchNode> branchArena,
        ref NodeHandle<BranchNode> root)
        where LeafHelper : unmanaged, ITypeHelper<LeafNode>
        where BranchHelper : unmanaged, ITypeHelper<BranchNode> {
        ArenaMarkContext<LeafNode> leafContext = NodeArena<LeafNode>.CreateCommttedMarkContext(ref leafArena);
        ArenaMarkContext<BranchNode> branchContext = NodeArena<BranchNode>.CreateCommttedMarkContext(ref branchArena);
        BranchVisitor<LeafNode, LeafAccessor, BranchNode, BranchAccessor> visitor = new(in leafContext, in branchContext);
        visitor.VisitBranch( ref root);
        leafArena.SweepAndCompactCommitted<LeafHelper>(in leafContext._reachable);
        branchArena.SweepAndCompactCommitted<BranchHelper>(in branchContext._reachable);
    }

    /// <summary>
    /// 遍历 current 图中的全部可达节点，但只清理未提交窗口中无法从 <paramref name="root"/> 访问到的节点。
    /// 适用于 builder/draft 阶段的 GC，不会破坏 committed 快照的 revert 语义。
    /// </summary>
    public static void CollectDraft<LeafHelper, BranchHelper>(
        ref NodeArena<LeafNode> leafArena, ref NodeArena<BranchNode> branchArena,
        ref NodeHandle<BranchNode> root)
        where LeafHelper : unmanaged, ITypeHelper<LeafNode>
        where BranchHelper : unmanaged, ITypeHelper<BranchNode> {
        ArenaMarkContext<LeafNode> leafContext = NodeArena<LeafNode>.CreateAllMarkContext(ref leafArena);
        ArenaMarkContext<BranchNode> branchContext = NodeArena<BranchNode>.CreateAllMarkContext(ref branchArena);
        BranchVisitor<LeafNode, LeafAccessor, BranchNode, BranchAccessor> visitor = new(in leafContext, in branchContext);
        visitor.VisitBranch(ref root);
        leafArena.SweepAndCompactUncommitted<LeafHelper>(in leafContext._reachable);
        branchArena.SweepAndCompactUncommitted<BranchHelper>(in branchContext._reachable);
    }

    /// <summary>
    /// 遍历 current 图中的全部可达节点，并直接清理当前 arena 中所有不可达节点。
    /// 该操作会破坏 revert 所需的 committed 物理布局，因此只适合在真正提交当前状态前使用。
    /// </summary>
    public static void CollectAll<LeafHelper, BranchHelper>(
        ref NodeArena<LeafNode> leafArena, ref NodeArena<BranchNode> branchArena,
        ref NodeHandle<BranchNode> root)
        where LeafHelper : unmanaged, ITypeHelper<LeafNode>
        where BranchHelper : unmanaged, ITypeHelper<BranchNode> {
        ArenaMarkContext<LeafNode> leafContext = NodeArena<LeafNode>.CreateAllMarkContext(ref leafArena);
        ArenaMarkContext<BranchNode> branchContext = NodeArena<BranchNode>.CreateAllMarkContext(ref branchArena);
        BranchVisitor<LeafNode, LeafAccessor, BranchNode, BranchAccessor> visitor = new(in leafContext, in branchContext);
        visitor.VisitBranch( ref root);
        leafArena.SweepAndCompactAll<LeafHelper>(in leafContext._reachable);
        branchArena.SweepAndCompactAll<BranchHelper>(in branchContext._reachable);
    }

    /// <summary>用于一系列<see cref="NodeArena{TNode}.ApplyDelta{NodeHelper}(ref BinaryDiffReader)"/>后，<see cref="NodeArena{TNode}.SyncCurrentFromCommitted()"/>之前，
    /// 清理掉无法从<paramref name="root0"/>、<paramref name="root1"/>访问到的节点。</summary>
    public static void CollectCommitted<LeafHelper, BranchHelper>(
        ref NodeArena<LeafNode> leafArena, ref NodeArena<BranchNode> branchArena,
        ref NodeHandle<BranchNode> root0, ref NodeHandle<BranchNode> root1)
        where LeafHelper : unmanaged, ITypeHelper<LeafNode>
        where BranchHelper : unmanaged, ITypeHelper<BranchNode> {
        ArenaMarkContext<LeafNode> leafContext = NodeArena<LeafNode>.CreateCommttedMarkContext(ref leafArena);
        ArenaMarkContext<BranchNode> branchContext = NodeArena<BranchNode>.CreateCommttedMarkContext(ref branchArena);
        BranchVisitor<LeafNode, LeafAccessor, BranchNode, BranchAccessor> visitor = new(in leafContext, in branchContext);
        visitor.VisitBranch( ref root0);
        visitor.VisitBranch( ref root1);
        leafArena.SweepAndCompactCommitted<LeafHelper>(in leafContext._reachable);
        branchArena.SweepAndCompactCommitted<BranchHelper>(in branchContext._reachable);
    }

    /// <summary>
    /// 遍历 current 图中的全部可达节点，但只清理未提交窗口中无法从 <paramref name="root0"/>、<paramref name="root1"/> 访问到的节点。
    /// 适用于 builder/draft 阶段的 GC，不会破坏 committed 快照的 revert 语义。
    /// </summary>
    public static void CollectDraft<LeafHelper, BranchHelper>(
        ref NodeArena<LeafNode> leafArena, ref NodeArena<BranchNode> branchArena,
        ref NodeHandle<BranchNode> root0, ref NodeHandle<BranchNode> root1)
        where LeafHelper : unmanaged, ITypeHelper<LeafNode>
        where BranchHelper : unmanaged, ITypeHelper<BranchNode> {
        ArenaMarkContext<LeafNode> leafContext = NodeArena<LeafNode>.CreateAllMarkContext(ref leafArena);
        ArenaMarkContext<BranchNode> branchContext = NodeArena<BranchNode>.CreateAllMarkContext(ref branchArena);
        BranchVisitor<LeafNode, LeafAccessor, BranchNode, BranchAccessor> visitor = new(in leafContext, in branchContext);
        visitor.VisitBranch(ref root0);
        visitor.VisitBranch(ref root1);
        leafArena.SweepAndCompactUncommitted<LeafHelper>(in leafContext._reachable);
        branchArena.SweepAndCompactUncommitted<BranchHelper>(in branchContext._reachable);
    }

    /// <summary>
    /// 遍历 current 图中的全部可达节点，并直接清理当前 arena 中所有不可达节点。
    /// 该操作会破坏 revert 所需的 committed 物理布局，因此只适合在真正提交当前状态前使用。
    /// </summary>
    public static void CollectAll<LeafHelper, BranchHelper>(
        ref NodeArena<LeafNode> leafArena, ref NodeArena<BranchNode> branchArena,
        ref NodeHandle<BranchNode> root0, ref NodeHandle<BranchNode> root1)
        where LeafHelper : unmanaged, ITypeHelper<LeafNode>
        where BranchHelper : unmanaged, ITypeHelper<BranchNode> {
        ArenaMarkContext<LeafNode> leafContext = NodeArena<LeafNode>.CreateAllMarkContext(ref leafArena);
        ArenaMarkContext<BranchNode> branchContext = NodeArena<BranchNode>.CreateAllMarkContext(ref branchArena);
        BranchVisitor<LeafNode, LeafAccessor, BranchNode, BranchAccessor> visitor = new(in leafContext, in branchContext);
        visitor.VisitBranch( ref root0);
        visitor.VisitBranch( ref root1);
        leafArena.SweepAndCompactAll<LeafHelper>(in leafContext._reachable);
        branchArena.SweepAndCompactAll<BranchHelper>(in branchContext._reachable);
    }
}

internal struct NodeArena<TNode>
    where TNode : struct {
    private TNode[] _nodes;
    private uint[] _sequences;
    private int _currentCount;
    private int _committedCount;

    #region Dirty Committed
    private BitVector _dirtyCommittedNodes;
    private ListCore<(int Index, TNode Committed)> _dirtyOriginals;
    private void CaptureDirty(int index) {
        Debug.Assert(index >= 0 && index < _committedCount);
        Debug.Assert(!_dirtyCommittedNodes.TestBit(index));

        _dirtyCommittedNodes.SetBit(index);
        _dirtyOriginals.Add((index, _nodes[index]));
    }

    private void RevertDirtyCommitted<NodeHelper>() where NodeHelper: unmanaged, ITypeHelper<TNode> {
        while(_dirtyOriginals.TryPop(out var orignalCommitted)) {
            int index = orignalCommitted.Index;
            if (NodeHelper.NeedRelease) {
                NodeHelper.ReleaseSlot(_nodes[index]);
            }
            _nodes[index] = orignalCommitted.Committed;
        }
        _dirtyCommittedNodes.Clear();
    }

    private void CommitDirtyCommitted<NodeHelper>() where NodeHelper: unmanaged, ITypeHelper<TNode> {
        if (NodeHelper.NeedRelease){
            while(_dirtyOriginals.TryPop(out var orignalCommitted)) {
                NodeHelper.ReleaseSlot(orignalCommitted.Committed);
            }
        }
        else {
            _dirtyOriginals.Clear();
        }
        _dirtyCommittedNodes.Clear();
    }
    #endregion

    private int Capacity => _nodes.Length;
    internal int CurrentNodeCount => _currentCount;
    internal int CommittedNodeCount => _committedCount;
    internal int DirtyCommittedCount => _dirtyCommittedNodes.PopCount;
    private int AppendedCount => _currentCount - _committedCount;

    public NodeArena() {
        _nodes = [];
        _sequences = [];
        _currentCount = 0;
        _committedCount = 0;
        _dirtyCommittedNodes = new();
        _dirtyOriginals = new();
        ResetDirtyTracking();
    }

    internal ref TNode GetNodeRef(ref NodeHandle<TNode> handle, out int index) {
        if (handle.MissingCachedIndex) {
            index = Array.BinarySearch(_sequences, 0, _currentCount, handle.Sequence);
            if (index < 0) { throw new KeyNotFoundException(); }
            handle.CachedIndex = index;
        }
        else {
            index = handle.CachedIndex;
            if ((uint)index >= (uint)_currentCount || _sequences[index] != handle.Sequence) {
                index = Array.BinarySearch(_sequences, 0, _currentCount, handle.Sequence);
                if (index < 0) { throw new KeyNotFoundException(); }
                handle.CachedIndex = index;
            }
        }
        return ref _nodes[index];
    }

    public TNode GetNode(ref NodeHandle<TNode> handle) {
        return GetNodeRef(ref handle, out _);
    }

    private bool IsCommttedIndex(int index) {
        Debug.Assert(index >= 0);
        return index < _committedCount;
    }

    public ref TNode GetWritableNodeRef<NodeHelper>(ref NodeHandle<TNode> handle) where NodeHelper: unmanaged, ITypeHelper<TNode> {
        Debug.Assert(
            !NodeHelper.NeedRelease,
            "GetWritableNodeRef/AfterWrite 当前仅适用于无额外释放语义的节点；NeedRelease 节点请走 ReplaceNode。"
        );
        ref TNode ret = ref GetNodeRef(ref handle, out int index);

        if (IsCommttedIndex(index) && !_dirtyCommittedNodes.TestBit(index)) {
            CaptureDirty(index);
        }

        return ref ret;
    }

    private void EnsureCapacity(int requiredCapacity) {
        int currentCapacity = _nodes?.Length ?? 0;
        if (requiredCapacity <= currentCapacity) { return; }

        int newCapacity = currentCapacity == 0 ? 4 : currentCapacity;
        while (newCapacity < requiredCapacity) {
            newCapacity *= 2;
        }

        if (_nodes is null) {
            _nodes = new TNode[newCapacity];
        }
        else {
            Array.Resize(ref _nodes, newCapacity);
        }

        if (_sequences is null) {
            _sequences = new uint[newCapacity];
        }
        else {
            Array.Resize(ref _sequences, newCapacity);
        }
    }

    public ref TNode AllocNodeRef(out NodeHandle<TNode> handle) {
        int allocedIndex = _currentCount;
        EnsureCapacity(allocedIndex + 1);

        uint allocedSequence;
        if (allocedIndex == 0) {
            allocedSequence = NodeHandleConstants.MinSequence;
        }
        else {
            allocedSequence = _sequences[allocedIndex - 1] + 1;
        }
        _sequences[allocedIndex] = allocedSequence;

        ++_currentCount;
        handle = new(allocedSequence);
        return ref _nodes[allocedIndex];
    }

    internal static ArenaMarkContext<TNode> CreateMarkContext(ref NodeArena<TNode> arena, int reachableBase, int reachableCount) {
        BitVector reachable = new BitVector();
        reachable.SetLength(reachableCount);
        return new(ref arena, reachable, reachableBase);
    }

    internal static ArenaMarkContext<TNode> CreateCommttedMarkContext(ref NodeArena<TNode> arena) {
        return CreateMarkContext(ref arena, 0, arena._committedCount);
    }

    internal static ArenaMarkContext<TNode> CreateAllMarkContext(ref NodeArena<TNode> arena) {
        return CreateMarkContext(ref arena, 0, arena._currentCount);
    }

    internal void SweepAndCompactCommitted<NodeHelper>(in BitVector reachable) where NodeHelper: unmanaged, ITypeHelper<TNode> {
        SweepAndCompactStable<NodeHelper>(0, ref _committedCount, in reachable, reachableBase: 0);
    }

    internal void SweepAndCompactUncommitted<NodeHelper>(in BitVector reachable) where NodeHelper: unmanaged, ITypeHelper<TNode> {
        SweepAndCompactStable<NodeHelper>(_committedCount, ref _currentCount, in reachable, reachableBase: 0);
    }

    internal void SweepAndCompactAll<NodeHelper>(in BitVector reachable) where NodeHelper: unmanaged, ITypeHelper<TNode> {
        SweepAndCompactStable<NodeHelper>(0, ref _currentCount, in reachable, reachableBase: 0);
    }

    /// <summary>
    /// 对 <c>[startIndex, endIndex)</c> 做保序 sweep-and-compact。
    /// 采用经典 sliding compaction：read 扫描原窗口，write 只写 surviving node。
    /// </summary>
    private void SweepAndCompactStable<NodeHelper>(int startIndex, ref int endIndex, in BitVector reachable, int reachableBase) where NodeHelper: unmanaged, ITypeHelper<TNode> {
        int writeIndex = startIndex;
        for (int readIndex = startIndex; readIndex < endIndex; ++readIndex) {
            int workWindowIndex = readIndex - reachableBase;
            if (!reachable.TestBit(workWindowIndex)) {
                if (NodeHelper.NeedRelease) {
                    NodeHelper.ReleaseSlot(_nodes[readIndex]);
                }

                _nodes[readIndex] = default;
                _sequences[readIndex] = 0;
                continue;
            }

            if (writeIndex != readIndex) {
                _nodes[writeIndex] = _nodes[readIndex];
                _sequences[writeIndex] = _sequences[readIndex];
                _nodes[readIndex] = default;
                _sequences[readIndex] = 0;
            }

            ++writeIndex;
        }

        endIndex = writeIndex;
    }

    public void Revert<NodeHelper>() where NodeHelper: unmanaged, ITypeHelper<TNode> {
        RevertDirtyCommitted<NodeHelper>();

        if (!NodeHelper.NeedRelease){
            _currentCount = _committedCount;
            ResetDirtyTracking();
            return;
        }
        while (_currentCount > _committedCount) {
            NodeHelper.ReleaseSlot(_nodes[--_currentCount]);
        }
        ResetDirtyTracking();
    }

    public void Commit<NodeHelper>() where NodeHelper: unmanaged, ITypeHelper<TNode> {
        CommitDirtyCommitted<NodeHelper>();
        _committedCount = _currentCount;
        ResetDirtyTracking();
    }
    #region Save
    public void WriteDeltify<NodeHelper>(BinaryDiffWriter writer, DiffWriteContext context) where NodeHelper: unmanaged, ITypeHelper<TNode> {
        writer.WriteCount(DirtyCommittedCount);
        foreach (int dirtyIndex in _dirtyCommittedNodes.Ones()) {
            writer.BareUInt32(_sequences[dirtyIndex], asKey:true);
            NodeHelper.Write(writer, _nodes[dirtyIndex], asKey:false);
        }

        writer.WriteCount(AppendedCount);
        WriteNodesOnly<NodeHelper>(writer, _committedCount, _currentCount);
    }
    public void WriteRebase<NodeHelper>(BinaryDiffWriter writer, DiffWriteContext context) where NodeHelper: unmanaged, ITypeHelper<TNode> {
        writer.WriteCount(0);

        writer.WriteCount(_currentCount);
        WriteNodesOnly<NodeHelper>(writer, 0, _currentCount);
    }
    private void WriteNodesOnly<NodeHelper>(BinaryDiffWriter writer, int startIndex, int endIndex) where NodeHelper: unmanaged, ITypeHelper<TNode> {
        for (int i = startIndex; i < endIndex; ++i) {
            writer.BareUInt32(_sequences[i], asKey:true);
            NodeHelper.Write(writer, _nodes[i], asKey:false);
        }
    }
    #endregion
    #region Load
    public void ApplyDelta<NodeHelper>(ref BinaryDiffReader reader) where NodeHelper: unmanaged, ITypeHelper<TNode> {
        Debug.Assert(_currentCount == _committedCount);
        int dirtyCount = reader.ReadCount();
        while (--dirtyCount >= 0) {
            uint sequence = reader.BareUInt32(asKey:true);
            int index = Array.BinarySearch(_sequences, 0, _committedCount, sequence);
            if (index < 0) { throw new InvalidDataException($"Tree delta references unknown committed node sequence {sequence}."); }

            TNode replaced = NodeHelper.Read(ref reader, asKey: false);
            if (NodeHelper.NeedRelease) {
                NodeHelper.ReleaseSlot(_nodes[index]);
            }
            _nodes[index] = replaced;
        }

        int appendedCount = reader.ReadCount();
        EnsureCapacity(_committedCount + appendedCount);
        while (--appendedCount >= 0) {
            _sequences[_committedCount] = reader.BareUInt32(asKey:true);
            _nodes[_committedCount] = NodeHelper.Read(ref reader, asKey: false);
            ++_committedCount;
        }

        _currentCount = _committedCount;
        ResetDirtyTracking();
    }

    public void SyncCurrentFromCommitted() {
        _currentCount = _committedCount;
        ResetDirtyTracking();
    }
    #endregion

    private void ResetDirtyTracking() {
        _dirtyCommittedNodes.Clear();
        _dirtyCommittedNodes.SetLength(_committedCount);
        _dirtyOriginals.Clear();
    }
}
