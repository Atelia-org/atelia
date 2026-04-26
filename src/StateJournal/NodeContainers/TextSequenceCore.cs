using System.Diagnostics;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.NodeContainers;

/// <summary>
/// 链式文本序列核心：每个节点存储一个内容块，通过单向 Next 链维护顺序，通过 sequence 提供稳定节点身份。
/// 内部复用 <see cref="LeafChainStore{TKey, TValue, KHelper, VHelper}"/> 的全部 delta / GC / dirty-tracking 基础设施。
/// Key 槽位为 dummy byte（始终为 0），Value 槽位为块内容 string。
/// </summary>
internal struct TextSequenceCore {
    private LeafChainStore<byte, string, ByteHelper, StringHelper> _arena;
    private LeafHandle _head;
    private LeafHandle _committedHead;
    private LeafHandle _tail;
    private int _count;
    private int _committedCount;
    private Dictionary<uint, uint> _prevByNodeId;

    public TextSequenceCore() {
        _arena = new();
        _prevByNodeId = new();
    }

    #region Properties

    public LeafHandle Head => _head;
    public uint HeadSequence => _head.Sequence;
    public int Count => _count;
    public bool HasChanges =>
        _count != _committedCount
        || _head.Sequence != _committedHead.Sequence
        || _arena.DirtyValueCount > 0
        || _arena.DirtyLinkCount > 0;

    #endregion

    #region Read

    public string GetBlock(uint nodeId) {
        var handle = RequireLiveHandle(nodeId);
        return _arena.GetValue(ref handle);
    }

    public (uint NodeId, string Content) GetEntry(ref LeafHandle handle) {
        var (_, value) = _arena.GetEntry(ref handle);
        return (handle.Sequence, value);
    }

    public LeafHandle GetNext(ref LeafHandle handle) => _arena.GetNext(ref handle);

    public List<TextBlock> GetAllBlocks() {
        var result = new List<TextBlock>(_count);
        var cursor = _head;
        while (cursor.IsNotNull) {
            var content = _arena.GetValue(ref cursor);
            result.Add(new TextBlock(cursor.Sequence, content));
            cursor = _arena.GetNext(ref cursor);
        }
        return result;
    }

    public List<TextBlock> GetBlocksFrom(uint startNodeId, int maxCount) {
        var start = RequireLiveHandle(startNodeId);
        var result = new List<TextBlock>(Math.Min(maxCount, _count));
        var cursor = start;
        int collected = 0;
        while (cursor.IsNotNull && collected < maxCount) {
            var content = _arena.GetValue(ref cursor);
            result.Add(new TextBlock(cursor.Sequence, content));
            cursor = _arena.GetNext(ref cursor);
            collected++;
        }
        return result;
    }

    #endregion

    #region Write

    /// <summary>在 head 之前插入一个块。</summary>
    public uint Prepend(string content) {
        uint oldHeadSeq = _head.Sequence;
        var newNode = _arena.AllocNode(key: 0, value: content, nextSequence: _head.Sequence);
        _head = newNode;
        if (_count == 0) { _tail = newNode; }
        else { _prevByNodeId[oldHeadSeq] = newNode.Sequence; }
        _prevByNodeId[newNode.Sequence] = 0;
        _count++;
        return newNode.Sequence;
    }

    /// <summary>在 tail 之后插入一个块。</summary>
    public uint Append(string content) {
        uint oldTailSeq = _tail.Sequence;
        var newNode = _arena.AllocNode(key: 0, value: content, nextSequence: 0);
        if (_count > 0) {
            _arena.SetNextSequence(ref _tail, newNode.Sequence);
        }
        else {
            _head = newNode;
        }
        _tail = newNode;
        _prevByNodeId[newNode.Sequence] = _count > 0 ? oldTailSeq : 0;
        _count++;
        return newNode.Sequence;
    }

    /// <summary>在指定节点之后插入一个块。</summary>
    public uint InsertAfter(uint afterNodeId, string content) {
        var afterHandle = RequireLiveHandle(afterNodeId);
        uint oldNextSeq = _arena.GetNextSequence(ref afterHandle);
        var newNode = _arena.AllocNode(key: 0, value: content, nextSequence: oldNextSeq);
        _arena.SetNextSequence(ref afterHandle, newNode.Sequence);

        // 如果 afterNode 是 tail，更新 tail
        if (afterHandle.Sequence == _tail.Sequence) {
            _tail = newNode;
        }
        _prevByNodeId[newNode.Sequence] = afterNodeId;
        if (oldNextSeq != 0) { _prevByNodeId[oldNextSeq] = newNode.Sequence; }
        _count++;
        return newNode.Sequence;
    }

    /// <summary>在指定节点之前插入一个块。</summary>
    public uint InsertBefore(uint beforeNodeId, string content) {
        if (!_prevByNodeId.TryGetValue(beforeNodeId, out uint predSeq)) {
            throw new KeyNotFoundException($"Node {beforeNodeId} not found in text chain.");
        }
        return predSeq == 0
            ? Prepend(content)
            : InsertAfter(predSeq, content);
    }

    /// <summary>替换指定节点的内容。</summary>
    public void SetContent(uint nodeId, string newContent) {
        var handle = RequireLiveHandle(nodeId);
        _arena.SetValue(ref handle, newContent);
    }

    /// <summary>删除指定节点。</summary>
    public void Delete(uint nodeId) {
        if (!_prevByNodeId.TryGetValue(nodeId, out uint predSeq)) {
            throw new KeyNotFoundException($"Node {nodeId} not found in text chain.");
        }

        var current = new LeafHandle(nodeId);
        uint deletedNextSeq = _arena.GetNextSequence(ref current);

        if (predSeq == 0) {
            _head = _arena.GetNext(ref current);
            if (_head.IsNull) {
                _tail = default;
            }
            else {
                _prevByNodeId[_head.Sequence] = 0;
            }
        }
        else {
            var pred = new LeafHandle(predSeq);
            _arena.SetNextSequence(ref pred, deletedNextSeq);
            if (deletedNextSeq == 0) {
                _tail = pred;
            }
            else {
                _prevByNodeId[deletedNextSeq] = predSeq;
            }
        }

        _prevByNodeId.Remove(nodeId);
        _count--;
    }

    /// <summary>批量加载行。仅空容器可用。</summary>
    public void LoadBlocks(ReadOnlySpan<string> lines) {
        if (_count != 0) {
            throw new InvalidOperationException("LoadBlocks can only be called on an empty text.");
        }
        if (lines.IsEmpty) { return; }

        LeafHandle prev = default;
        for (int i = 0; i < lines.Length; i++) {
            var node = _arena.AllocNode(key: 0, value: lines[i], nextSequence: 0);
            _prevByNodeId[node.Sequence] = i == 0 ? 0 : prev.Sequence;
            if (i == 0) {
                _head = node;
            }
            else {
                _arena.SetNextSequence(ref prev, node.Sequence);
            }
            prev = node;
        }
        _tail = prev;
        _count = lines.Length;
    }

    #endregion

    #region Lifecycle

    public void Commit() {
        _arena.Commit();
        _arena.CollectCommitted(_head.Sequence);
        if (_arena.CommittedNodeCount != _count) {
            throw new InvalidOperationException(
                $"Text committed count mismatch after commit canonicalization: expected {_count}, actual {_arena.CommittedNodeCount}."
            );
        }
        _arena.SyncCurrentFromCommitted();
        _head.ClearCachedIndex();
        _committedCount = _count;
        RebuildPredecessorIndexAndTail();
        _committedHead = _head;
        _committedHead.ClearCachedIndex();
    }

    public void Revert() {
        _arena.Revert();
        _head = _committedHead;
        _head.ClearCachedIndex();
        _count = _committedCount;
        RebuildPredecessorIndexAndTail();
    }

    #endregion

    #region Serialization

    public void WriteDeltify(BinaryDiffWriter writer, DiffWriteContext context) {
        writer.BareUInt32(_head.Sequence, asKey: false);
        writer.WriteCount(_count);
        _arena.WriteDeltify(writer, context);
    }

    public void WriteRebase(BinaryDiffWriter writer, DiffWriteContext context) {
        writer.BareUInt32(_head.Sequence, asKey: false);
        writer.WriteCount(_count);
        _arena.WriteRebaseLiveOnly(writer, context, _head.Sequence);
    }

    public void ApplyDelta(ref BinaryDiffReader reader) {
        _committedHead = new(reader.BareUInt32(asKey: false));
        _committedCount = reader.ReadCount();
        _arena.ApplyDelta(ref reader);
        _arena.CollectCommitted(_committedHead.Sequence);
        if (_arena.CommittedNodeCount != _committedCount) {
            throw new InvalidDataException(
                $"Text committed count mismatch after load canonicalization: expected {_committedCount}, actual {_arena.CommittedNodeCount}."
            );
        }
    }

    public void SyncCurrentFromCommitted() {
        _arena.SyncCurrentFromCommitted();
        _head = _committedHead;
        _head.ClearCachedIndex();
        _count = _committedCount;
        RebuildPredecessorIndexAndTail();
    }

    private static uint CountSize(int count) => CostEstimateUtil.VarIntSize((uint)count);
    private static uint SequenceSize(uint sequence) => CostEstimateUtil.VarIntSize(sequence);

    /// <summary>估算 rebase 帧所需的 bare 字节数，对齐真实 rebase wire shape。</summary>
    public uint EstimatedRebaseBytes() {
        uint sum = SequenceSize(_head.Sequence)
            + CountSize(_count)
            + CountSize(0)
            + CountSize(0)
            + CountSize(_count);

        var cursor = _head;
        while (cursor.IsNotNull) {
            string content = _arena.GetValue(ref cursor);
            sum += SequenceSize(cursor.Sequence);
            sum += SequenceSize(_arena.GetNextSequence(ref cursor));
            sum += ByteHelper.EstimateBareSize(0, asKey: true);
            sum += StringHelper.EstimateBareSize(content, asKey: false);
            cursor = _arena.GetNext(ref cursor);
        }

        return sum;
    }

    /// <summary>估算 deltify 帧所需的 bare 字节数，对齐真实 delta wire shape。</summary>
    public uint EstimatedDeltifyBytes() {
        int dirtyLinkCount = _arena.DirtyLinkCount;
        int dirtyValueCount = _arena.DirtyValueCount;
        int appendedCount = _arena.CurrentNodeCount - _arena.CommittedNodeCount;

        uint sum = SequenceSize(_head.Sequence)
            + CountSize(_count)
            + CountSize(dirtyLinkCount)
            + CountSize(dirtyValueCount)
            + CountSize(appendedCount);

        foreach (int dirtyIndex in _arena.EnumerateDirtyLinkIndices()) {
            sum += SequenceSize(_arena.GetSequenceByIndex(dirtyIndex));
            sum += SequenceSize(_arena.GetNextSequenceByIndex(dirtyIndex));
        }

        foreach (int dirtyIndex in _arena.EnumerateDirtyValueIndices()) {
            sum += SequenceSize(_arena.GetSequenceByIndex(dirtyIndex));
            sum += StringHelper.EstimateBareSize(_arena.GetValueByIndex(dirtyIndex), asKey: false);
        }

        for (int i = _arena.CommittedNodeCount; i < _arena.CurrentNodeCount; i++) {
            sum += SequenceSize(_arena.GetSequenceByIndex(i));
            sum += SequenceSize(_arena.GetNextSequenceByIndex(i));
            sum += ByteHelper.EstimateBareSize(_arena.GetKeyByIndex(i), asKey: true);
            sum += StringHelper.EstimateBareSize(_arena.GetValueByIndex(i), asKey: false);
        }

        return sum;
    }

    #endregion

    #region Current-Only Predecessor Index

    private LeafHandle RequireLiveHandle(uint nodeId) {
        if (!_prevByNodeId.ContainsKey(nodeId)) {
            throw new KeyNotFoundException($"Node {nodeId} is not a live node in the current text.");
        }
        return new LeafHandle(nodeId);
    }

    private void RebuildPredecessorIndexAndTail() {
        _prevByNodeId.Clear();
        uint prevSeq = 0;
        LeafHandle last = default;
        var cursor = _head;
        while (cursor.IsNotNull) {
            _prevByNodeId[cursor.Sequence] = prevSeq;
            prevSeq = cursor.Sequence;
            last = cursor;
            cursor = _arena.GetNext(ref cursor);
        }
        _tail = last;
        _tail.ClearCachedIndex();
        Debug.Assert(_prevByNodeId.Count == _count);
        Debug.Assert(_count == 0 || _prevByNodeId[_head.Sequence] == 0);
    }

    #endregion

    #region Child Ref Visitor

    public void AcceptChildRefVisitor<TVisitor>(Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        if (!StringHelper.NeedVisitChildRefs) { return; }
        var cursor = _head;
        while (cursor.IsNotNull) {
            var content = _arena.GetValue(ref cursor);
            StringHelper.VisitChildRefs(content, revision, ref visitor);
            cursor = _arena.GetNext(ref cursor);
        }
    }

    public AteliaError? ValidateReconstructed(LoadPlaceholderTracker tracker, string ownerName) {
        if (!StringHelper.NeedValidateReconstructed) { return null; }
        var cursor = _head;
        while (cursor.IsNotNull) {
            var content = _arena.GetValue(ref cursor);
            if (StringHelper.ValidateReconstructed(content, tracker, ownerName) is { } error) {
                return error;
            }
            cursor = _arena.GetNext(ref cursor);
        }
        return null;
    }

    #endregion
}
