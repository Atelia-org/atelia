using System.Diagnostics;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

public readonly record struct NodeSequence(uint Value);

internal interface ISubNodesGetter<TNode> {
    static abstract int Count { get; }
    static abstract void GetSubNodes(in TNode node, Stack<NodeSequence> result);
}
internal struct DurableTreeCore<TNode, NodeHelper, TRoots, RootsHelper>
    where TNode : struct
    where NodeHelper : unmanaged, ITypeHelper<TNode>, ISubNodesGetter<TNode>
    where TRoots : struct
    where RootsHelper :  unmanaged, ITypeHelper<TRoots>, ISubNodesGetter<TRoots> {

    private TNode[] _nodes;
    private uint[] _sequences;
    private int _currentCount;
    private int _committedCount;
    private BitVector _dirtyCommittedNodes;
    private Dictionary<int, TNode>? _dirtyOriginals;

    private TRoots _committedRoots;
    public TRoots CurrentRoots;
    private int Capacity => _nodes.Length;
    internal int CurrentNodeCount => _currentCount;
    internal int CommittedNodeCount => _committedCount;
    internal int DirtyCommittedCount => _dirtyCommittedNodes.PopCount;
    private int AppendedCount => _currentCount - _committedCount;
    private bool RootsChanged => !RootsHelper.Equals(CurrentRoots, _committedRoots);

    /// <summary>依赖先执行<see cref="CollectBuilderNodes"/></summary>
    public int DeltifyCount => HasChanges
        ? DirtyCommittedCount + AppendedCount + RootsHelper.Count
        : 0;

    /// <summary>依赖先执行<see cref="CollectBuilderNodes"/></summary>
    public int RebaseCount => _currentCount != 0 || RootsChanged
        ? _currentCount + RootsHelper.Count
        : 0;

    /// <summary>依赖先执行<see cref="CollectBuilderNodes"/></summary>
    public bool HasChanges => AppendedCount != 0 || DirtyCommittedCount != 0 || RootsChanged;

    public readonly struct NodeWriteToken {
        internal readonly int Index;
        internal readonly uint Sequence;
        internal readonly bool TracksCommittedNode;

        internal NodeWriteToken(int index, uint sequence, bool tracksCommittedNode) {
            Index = index;
            Sequence = sequence;
            TracksCommittedNode = tracksCommittedNode;
        }
    }

    public DurableTreeCore() {
        _nodes = [];
        _sequences = [];
        _currentCount = 0;
        _committedCount = 0;
        _dirtyCommittedNodes = new();
        _dirtyOriginals = null;
        _committedRoots = default;
        CurrentRoots = default;
    }

    public bool TryGetNodeIndex(NodeSequence target, out int index) {
        index = Array.BinarySearch(_sequences, 0, _currentCount, target.Value);
        return index >= 0;
    }

    public int GetNodeIndex(NodeSequence target) {
        if (!TryGetNodeIndex(target, out int index)) { throw new KeyNotFoundException(); }
        return index;
    }
#region 感觉这一组冗余检查和查找有点多，执行效率偏低，我再想想办法平衡API的易用性、健壮性、性能。理想情况下在试图“逻辑编辑”一棵树时，如果需要被“逻辑改写”的目标节点尚未提交，则可以按Builder逻辑就地改写，如果目标节点已提交就按Immutable逻辑COW。
    public bool IsNodeMutable(NodeSequence target) {
        if(!TryGetNodeIndex(target, out int index)) {
            throw new KeyNotFoundException();
        }
        return index >= _committedCount;
    }

    public ref TNode GetBuilderNodeRef(NodeSequence target) {
        if(!TryGetNodeIndex(target, out int index)) {
            throw new KeyNotFoundException();
        }
        if (index < _committedCount) {
            throw new InvalidOperationException($"Committed nodes are immutable, target {target.Value} .");
        }
        return ref _nodes[index];
    }

    public ref TNode GetWritableNodeRef(NodeSequence target, out NodeWriteToken token) {
        Debug.Assert(
            !NodeHelper.NeedRelease,
            "GetWritableNodeRef/AfterWrite 当前仅适用于无额外释放语义的节点；NeedRelease 节点请走 ReplaceNode。"
        );
        if(!TryGetNodeIndex(target, out int index)) {
            throw new KeyNotFoundException();
        }

        bool tracksCommittedNode = index < _committedCount;
        if (tracksCommittedNode) {
            CaptureDirtyOriginal(index);
        }

        token = new(index, _sequences[index], tracksCommittedNode);
        return ref _nodes[index];
    }

    public void AfterWrite(NodeWriteToken token) {
        Debug.Assert(
            !NodeHelper.NeedRelease,
            "GetWritableNodeRef/AfterWrite 当前仅适用于无额外释放语义的节点；NeedRelease 节点请走 ReplaceNode。"
        );
        Debug.Assert(token.Index >= 0 && token.Index < _currentCount);
        Debug.Assert(_sequences[token.Index] == token.Sequence);

        if (!token.TracksCommittedNode) { return; }
        if (_dirtyOriginals is null || !_dirtyOriginals.TryGetValue(token.Index, out TNode original)) { return; }

        ref TNode slot = ref _nodes[token.Index];
        if (NodeHelper.Equals(slot, original)) {
            if (NodeHelper.NeedRelease) {
                NodeHelper.ReleaseSlot(slot);
            }
            slot = original;
            _dirtyCommittedNodes.ClearBit(token.Index);
            _dirtyOriginals.Remove(token.Index);
            if (_dirtyOriginals.Count == 0) {
                _dirtyOriginals = null;
            }
        }
    }

    public TNode GetNode(NodeSequence target) {
        if(!TryGetNodeIndex(target, out int index)) {
            throw new KeyNotFoundException();
        }
        return _nodes[index];
    }

    public void ReplaceNode(NodeSequence target, TNode value) {
        if(!TryGetNodeIndex(target, out int index)) {
            throw new KeyNotFoundException();
        }

        if (index >= _committedCount) {
            _nodes[index] = value;
            return;
        }

        bool wasDirty = _dirtyCommittedNodes.TestBit(index);
        TNode current = _nodes[index];
        CaptureDirtyOriginal(index);
        if (_dirtyOriginals is not null && _dirtyOriginals.TryGetValue(index, out TNode original) && NodeHelper.Equals(value, original)) {
            if (NodeHelper.NeedRelease && wasDirty) {
                NodeHelper.ReleaseSlot(current);
            }
            _nodes[index] = original;
            _dirtyCommittedNodes.ClearBit(index);
            _dirtyOriginals.Remove(index);
            if (_dirtyOriginals.Count == 0) {
                _dirtyOriginals = null;
            }
            return;
        }

        if (NodeHelper.NeedRelease && wasDirty) {
            NodeHelper.ReleaseSlot(current);
        }
        _nodes[index] = value;
    }
#endregion
    public NodeSequence AllocNode() {
        ref TNode _ = ref AllocNodeRef(out NodeSequence sequence);
        return sequence;
    }

    public ref TNode AllocNodeRef(out NodeSequence sequence) {
        int allocedIndex = _currentCount;
        EnsureCapacity(allocedIndex + 1);

        uint allocedSequence;
        if (allocedIndex == 0) {
            allocedSequence = 1;
        }
        else {
            allocedSequence = _sequences[allocedIndex - 1] + 1;
        }
        _sequences[allocedIndex] = allocedSequence;

        ++_currentCount;
        sequence = new(allocedSequence);
        return ref _nodes[allocedIndex];
    }

    public NodeSequence EnsureMutableCopy(NodeSequence target) {
        if(!TryGetNodeIndex(target, out int index)) {
            throw new KeyNotFoundException();
        }
        if (index >= _committedCount) { return target; }

        ref TNode copy = ref AllocNodeRef(out NodeSequence newSequence);
        copy = _nodes[index];
        return newSequence;
    }

    public ref TNode EnsureMutableCopyRef(NodeSequence target, out NodeSequence mutableSequence) {
        if(!TryGetNodeIndex(target, out int index)) {
            throw new KeyNotFoundException();
        }
        if (index >= _committedCount) {
            mutableSequence = target;
            return ref _nodes[index];
        }

        ref TNode copy = ref AllocNodeRef(out mutableSequence);
        copy = _nodes[index];
        return ref copy;
    }

    private void EnsureCapacity(int requiredCapacity) {
        if (requiredCapacity <= _nodes.Length) { return; }

        int newCapacity = _nodes.Length == 0 ? 4 : _nodes.Length;
        while (newCapacity < requiredCapacity) {
            newCapacity *= 2;
        }

        Array.Resize(ref _nodes, newCapacity);
        Array.Resize(ref _sequences, newCapacity);
    }

    /// <summary>用于在提交前避免写入无法访问的节点。由于限制walk的深度所以非常快。</summary>
    public void CollectBuilderNodes() => CollectCore(_committedCount, ref _currentCount, CurrentRoots);

    private void CollectCore(int startIndex, ref int count, in TRoots roots) {
        Debug.Assert((uint)startIndex <= (uint)count);

        int workWindowSize = count - startIndex;
        if (workWindowSize <= 0) { return; }
        uint minSequence = _sequences[startIndex]; // 第一个尚未提交的节点

        BitVector reachable = new();
        reachable.SetLength(workWindowSize);
        Stack<NodeSequence> walkRemain = new();

        RootsHelper.GetSubNodes(in roots, walkRemain);

        while (walkRemain.TryPop(out NodeSequence curSeq)) {
            if (curSeq.Value < minSequence) { continue; }
            int index = GetNodeIndex(curSeq);

            int workWindowIndex = index - startIndex;
            if (reachable.TestBit(workWindowIndex)) { continue; }
            reachable.SetBit(workWindowIndex);

            NodeHelper.GetSubNodes(in _nodes[index], walkRemain);
        }

        SweepAndCompactStable(startIndex, ref count, reachable);
    }

    /// <summary>
    /// 对 <c>[startIndex, endIndex)</c> 做保序 sweep-and-compact。
    /// 采用经典 sliding compaction：read 扫描原窗口，write 只写 surviving node。
    /// </summary>
    private void SweepAndCompactStable(int startIndex, ref int endIndex, BitVector reachable) {
        int writeIndex = startIndex;
        for (int readIndex = startIndex; readIndex < endIndex; ++readIndex) {
            int workWindowIndex = readIndex - startIndex;
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
        if (startIndex == 0) {
            _committedCount = writeIndex;
        }
        else {
            Debug.Assert(startIndex == _committedCount);
        }
    }

    public void Revert() {
        CurrentRoots = _committedRoots;

        RestoreDirtyCommittedNodes();

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

    public void Commit() {
        FinalizeDirtyCommittedNodes();
        _committedCount = _currentCount;
        _committedRoots = CurrentRoots;
        ResetDirtyTracking();
    }
    #region Save
    public void WriteDeltify(BinaryDiffWriter writer, DiffWriteContext context) {
        writer.WriteCount(DirtyCommittedCount);
        foreach (int dirtyIndex in _dirtyCommittedNodes.Ones()) {
            writer.BareUInt32(_sequences[dirtyIndex], asKey:true);
            NodeHelper.Write(writer, _nodes[dirtyIndex], asKey:false);
        }

        writer.WriteCount(AppendedCount);
        WriteNodesOnly(writer, _committedCount, _currentCount);
        RootsHelper.Write(writer, CurrentRoots, asKey:false);
    }
    public void WriteRebase(BinaryDiffWriter writer, DiffWriteContext context) {
        writer.WriteCount(0);
        writer.WriteCount(_currentCount);
        WriteNodesOnly(writer, 0, _currentCount);
        RootsHelper.Write(writer, CurrentRoots, asKey:false);
    }
    private void WriteNodesOnly(BinaryDiffWriter writer, int startIndex, int endIndex) {
        for (int i = startIndex; i < endIndex; ++i) {
            writer.BareUInt32(_sequences[i], asKey:true);
            NodeHelper.Write(writer, _nodes[i], asKey:false);
        }
    }
    #endregion
    #region Load
    public void ApplyDelta(ref BinaryDiffReader reader) {
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

        _committedRoots = RootsHelper.Read(ref reader, asKey: false);
        CurrentRoots = _committedRoots;
        _currentCount = _committedCount;
        ResetDirtyTracking();
    }

    /// <summary>用于控制重建阶段的内存占用，比如每4次<see cref="ApplyDelta"/>后进行一次回收。</summary>
    public void CollectCommitted() {
        CollectCore(0, ref _committedCount, _committedRoots);
        ResetDirtyTracking();
    }

    public void SyncCurrentFromCommitted() {
        CollectCommitted();
        CurrentRoots = _committedRoots;
        _currentCount = _committedCount;
        ResetDirtyTracking();
    }
    #endregion

    private void CaptureDirtyOriginal(int index) {
        Debug.Assert(index >= 0 && index < _committedCount);
        if (_dirtyCommittedNodes.TestBit(index)) { return; }

        _dirtyCommittedNodes.SetBit(index);
        (_dirtyOriginals ??= new())[index] = _nodes[index];
    }

    private void RestoreDirtyCommittedNodes() {
        if (_dirtyOriginals is null) { return; }

        foreach (int index in _dirtyCommittedNodes.Ones()) {
            if (!_dirtyOriginals.TryGetValue(index, out TNode original)) { continue; }
            if (NodeHelper.NeedRelease) {
                NodeHelper.ReleaseSlot(_nodes[index]);
            }
            _nodes[index] = original;
        }
    }

    private void FinalizeDirtyCommittedNodes() {
        if (!NodeHelper.NeedRelease || _dirtyOriginals is null) { return; }

        foreach (int index in _dirtyCommittedNodes.Ones()) {
            if (_dirtyOriginals.TryGetValue(index, out TNode original)) {
                NodeHelper.ReleaseSlot(original);
            }
        }
    }

    private void ResetDirtyTracking() {
        _dirtyCommittedNodes.Clear();
        _dirtyCommittedNodes.SetLength(_committedCount);
        _dirtyOriginals = null;
    }
}
