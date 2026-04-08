using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

// using SkipListLeafNode = (int Key, Atelia.StateJournal.NodeContainers.NodeHandle<SkipListLeafNode> Next, string? Value);

namespace Atelia.StateJournal.NodeContainers;

/// <summary>
/// 用两个 <see cref="NodeArena{TNode}"/> 组装的最小跳表示例：
/// branch arena 管索引塔，leaf arena 管有序数据链。
/// 目标是验证 committed/current、handle 稳定性和有序查询的手感，
/// 不是最终 public API 形态。
/// </summary>
internal sealed class ExperimentalNodeArenaSkipListMap {
    private const int MaxTowerHeight = 12;

    private NodeArena<SkipListLeafNode> _leafArena;
    private NodeArena<SkipListBranchNode> _branchArena;
    private NodeHandle<SkipListBranchNode> _root;
    private NodeHandle<SkipListBranchNode> _committedRoot;
    private int _count;
    private int _committedCount;

    public int Count => _count;
    internal int DebugLeafNodeCount => _leafArena.CurrentNodeCount;
    internal int DebugBranchNodeCount => _branchArena.CurrentNodeCount;
    internal int DebugCommittedLeafNodeCount => _leafArena.CommittedNodeCount;
    internal int DebugCommittedBranchNodeCount => _branchArena.CommittedNodeCount;

    public bool TryGet(int key, out string? value) {
        if (!TryFindBottomPredecessor(key, out NodeHandle<SkipListBranchNode> predecessor)) {
            value = null;
            return false;
        }

        SkipListBranchNode predecessorNode = _branchArena.GetNode(ref predecessor);
        if (predecessorNode.Next.Sequence == 0) {
            value = null;
            return false;
        }

        NodeHandle<SkipListBranchNode> candidateHandle = predecessorNode.Next;
        SkipListBranchNode candidate = _branchArena.GetNode(ref candidateHandle);
        if (candidate.Key != key) {
            value = null;
            return false;
        }

        NodeHandle<SkipListLeafNode> leafHandle = candidate.Leaf;
        SkipListLeafNode leaf = _leafArena.GetNode(ref leafHandle);
        value = leaf.Value;
        return true;
    }

    public void Upsert(int key, string value) {
        int towerHeight = GetTowerHeight(key);
        EnsureHeight(towerHeight);

        Span<NodeHandle<SkipListBranchNode>> predecessors = stackalloc NodeHandle<SkipListBranchNode>[MaxTowerHeight];
        int levelCount = CollectPredecessors(key, predecessors);
        if (levelCount == 0) {
            throw new InvalidOperationException("Skip list must have a root after EnsureHeight.");
        }

        NodeHandle<SkipListBranchNode> bottomPredecessorHandle = predecessors[levelCount - 1];
        SkipListBranchNode bottomPredecessor = _branchArena.GetNode(ref bottomPredecessorHandle);
        if (bottomPredecessor.Next.IsNotNull) {
            NodeHandle<SkipListBranchNode> candidateHandle = bottomPredecessor.Next;
            SkipListBranchNode candidate = _branchArena.GetNode(ref candidateHandle);
            if (candidate.Key == key) {
                NodeHandle<SkipListLeafNode> leafHandle = candidate.Leaf;
                ref SkipListLeafNode leaf = ref _leafArena.GetWritableNodeRef<SkipListLeafNodeHelper>(ref leafHandle);
                leaf.Value = value;
                return;
            }
        }

        ref SkipListLeafNode newLeaf = ref _leafArena.AllocNodeRef(out NodeHandle<SkipListLeafNode> newLeafHandle);
        newLeaf.Key = key;
        newLeaf.Value = value;

        ref SkipListBranchNode writableBottomPredecessor = ref _branchArena.GetWritableNodeRef<SkipListBranchNodeHelper>(ref bottomPredecessorHandle);
        if (writableBottomPredecessor.IsSentinel) {
            newLeaf.Next = writableBottomPredecessor.Leaf;
            writableBottomPredecessor.Leaf = newLeafHandle;
        }
        else {
            NodeHandle<SkipListLeafNode> predecessorLeafHandle = writableBottomPredecessor.Leaf;
            ref SkipListLeafNode predecessorLeaf = ref _leafArena.GetWritableNodeRef<SkipListLeafNodeHelper>(ref predecessorLeafHandle);
            newLeaf.Next = predecessorLeaf.Next;
            predecessorLeaf.Next = newLeafHandle;
        }

        NodeHandle<SkipListBranchNode> down = default;
        for (int levelOffset = 0; levelOffset < towerHeight; ++levelOffset) {
            int predecessorIndex = levelCount - 1 - levelOffset;
            NodeHandle<SkipListBranchNode> predecessorHandle = predecessors[predecessorIndex];
            ref SkipListBranchNode predecessorNode = ref _branchArena.GetWritableNodeRef<SkipListBranchNodeHelper>(ref predecessorHandle);
            ref SkipListBranchNode branchNode = ref _branchArena.AllocNodeRef(out NodeHandle<SkipListBranchNode> branchHandle);
            branchNode.Key = key;
            branchNode.Next = predecessorNode.Next;
            branchNode.Down = down;
            branchNode.Leaf = levelOffset == 0 ? newLeafHandle : default;
            predecessorNode.Next = branchHandle;
            down = branchHandle;
        }

        _count++;
    }

    public IReadOnlyList<KeyValuePair<int, string?>> ReadAscendingFrom(int minInclusive, int maxCount) {
        if (maxCount < 0) { throw new ArgumentOutOfRangeException(nameof(maxCount)); }

        List<KeyValuePair<int, string?>> result = new(Math.Min(maxCount, _count));
        if (maxCount == 0 || !TryFindLowerBoundLeaf(minInclusive, out NodeHandle<SkipListLeafNode> leafHandle)) {
            return result;
        }

        while (leafHandle.IsNotNull && result.Count < maxCount) {
            SkipListLeafNode leaf = _leafArena.GetNode(ref leafHandle);
            result.Add(new(leaf.Key, leaf.Value));
            leafHandle = leaf.Next;
        }

        return result;
    }

    public bool TryGetLowerBound(int minInclusive, out KeyValuePair<int, string?> result) {
        if (!TryFindLowerBoundLeaf(minInclusive, out NodeHandle<SkipListLeafNode> leafHandle)) {
            result = default;
            return false;
        }

        SkipListLeafNode leaf = _leafArena.GetNode(ref leafHandle);
        result = new(leaf.Key, leaf.Value);
        return true;
    }

    public bool TryGetNext(int keyExclusive, out KeyValuePair<int, string?> result) {
        if (keyExclusive == int.MaxValue) {
            result = default;
            return false;
        }

        return TryGetLowerBound(keyExclusive + 1, out result);
    }

    public void CollectBuilderNodes() {
        if (_root.IsNotNull) {
            BranchVisitor<SkipListLeafNode, SkipListLeafNodeHelper, SkipListBranchNode, SkipListBranchNodeHelper>.CollectDraft<
                SkipListLeafNodeHelper,
                SkipListBranchNodeHelper
            >(ref _leafArena, ref _branchArena, ref _root);
        }
        _root.ClearCachedIndex();
    }

    public void Commit() {
        if (_root.IsNotNull) {
            BranchVisitor<SkipListLeafNode, SkipListLeafNodeHelper, SkipListBranchNode, SkipListBranchNodeHelper>.CollectAll<
                SkipListLeafNodeHelper,
                SkipListBranchNodeHelper
            >(ref _leafArena, ref _branchArena, ref _root);
        }
        _leafArena.Commit<SkipListLeafNodeHelper>();
        _branchArena.Commit<SkipListBranchNodeHelper>();
        _committedRoot = _root;
        _committedRoot.ClearCachedIndex();
        _root.ClearCachedIndex();
        _committedCount = _count;
    }

    public void Revert() {
        _leafArena.Revert<SkipListLeafNodeHelper>();
        _branchArena.Revert<SkipListBranchNodeHelper>();
        _root = _committedRoot;
        _root.ClearCachedIndex();
        _count = _committedCount;
    }

    private void EnsureHeight(int desiredHeight) {
        if (desiredHeight <= 0 || desiredHeight > MaxTowerHeight) {
            throw new ArgumentOutOfRangeException(nameof(desiredHeight));
        }

        int currentHeight = GetHeight();
        if (currentHeight == 0) {
            ref SkipListBranchNode bottomHead = ref _branchArena.AllocNodeRef(out _root);
            bottomHead.IsSentinel = true;
            currentHeight = 1;
        }

        while (currentHeight < desiredHeight) {
            ref SkipListBranchNode newHead = ref _branchArena.AllocNodeRef(out NodeHandle<SkipListBranchNode> newRoot);
            newHead.IsSentinel = true;
            newHead.Down = _root;
            _root = newRoot;
            currentHeight++;
        }
    }

    private int GetHeight() {
        int height = 0;
        NodeHandle<SkipListBranchNode> current = _root;
        while (current.IsNotNull) {
            height++;
            SkipListBranchNode node = _branchArena.GetNode(ref current);
            current = node.Down;
        }

        return height;
    }

    private int CollectPredecessors(int key, Span<NodeHandle<SkipListBranchNode>> predecessors) {
        if (_root.Sequence == 0) {
            return 0;
        }

        int levelCount = 0;
        NodeHandle<SkipListBranchNode> current = _root;
        while (true) {
            ref SkipListBranchNode currentNode = ref _branchArena.GetNodeRef(ref current, out _);
            while (currentNode.Next.IsNotNull) {
                NodeHandle<SkipListBranchNode> nextHandle = currentNode.Next;
                SkipListBranchNode nextNode = _branchArena.GetNode(ref nextHandle);
                if (nextNode.Key < key) {
                    current = currentNode.Next;
                    currentNode = ref _branchArena.GetNodeRef(ref current, out _);
                    continue;
                }

                break;
            }

            predecessors[levelCount++] = current;
            if (currentNode.Down.Sequence == 0) {
                return levelCount;
            }

            current = currentNode.Down;
        }
    }

    private bool TryFindBottomPredecessor(int key, out NodeHandle<SkipListBranchNode> predecessor) {
        predecessor = default;
        if (_root.Sequence == 0) {
            return false;
        }

        NodeHandle<SkipListBranchNode> current = _root;
        while (true) {
            ref SkipListBranchNode currentNode = ref _branchArena.GetNodeRef(ref current, out _);
            while (currentNode.Next.IsNotNull) {
                NodeHandle<SkipListBranchNode> nextHandle = currentNode.Next;
                SkipListBranchNode nextNode = _branchArena.GetNode(ref nextHandle);
                if (nextNode.Key < key) {
                    current = currentNode.Next;
                    currentNode = ref _branchArena.GetNodeRef(ref current, out _);
                    continue;
                }

                break;
            }

            if (currentNode.Down.Sequence == 0) {
                predecessor = current;
                return true;
            }

            current = currentNode.Down;
        }
    }

    private bool TryFindLowerBoundLeaf(int minInclusive, out NodeHandle<SkipListLeafNode> leafHandle) {
        leafHandle = default;
        if (!TryFindBottomPredecessor(minInclusive, out NodeHandle<SkipListBranchNode> predecessorHandle)) {
            return false;
        }

        SkipListBranchNode predecessor = _branchArena.GetNode(ref predecessorHandle);
        if (predecessor.Next.Sequence == 0) {
            return false;
        }

        NodeHandle<SkipListBranchNode> candidateHandle = predecessor.Next;
        SkipListBranchNode candidate = _branchArena.GetNode(ref candidateHandle);
        leafHandle = candidate.Leaf;
        return leafHandle.IsNotNull;
    }

    private static int GetTowerHeight(int key) {
        uint hash = (uint)key * 2654435761u;
        hash ^= hash >> 16;

        int height = 1;
        while (height < MaxTowerHeight && ((hash >> (height - 1)) & 1u) != 0) {
            height++;
        }

        return height;
    }

    private struct SkipListLeafNode {
        public int Key;
        public NodeHandle<SkipListLeafNode> Next;
        public string? Value;
    }

    private struct SkipListBranchNode {
        public bool IsSentinel;
        public int Key;
        public NodeHandle<SkipListBranchNode> Next;
        public NodeHandle<SkipListBranchNode> Down;
        public NodeHandle<SkipListLeafNode> Leaf;
    }

    private readonly struct SkipListLeafNodeHelper : ITypeHelper<SkipListLeafNode>, INodeAccessor<SkipListLeafNode> {
        public static bool Equals(SkipListLeafNode a, SkipListLeafNode b) =>
            a.Key == b.Key
            && a.Next.Sequence == b.Next.Sequence
            && StringHelper.Equals(a.Value, b.Value);

        public static void Write(BinaryDiffWriter writer, SkipListLeafNode v, bool asKey) {
            writer.BareInt32(v.Key, asKey);
            writer.BareUInt32(v.Next.Sequence, asKey);
            StringHelper.Write(writer, v.Value, asKey: false);
        }

        public static SkipListLeafNode Read(ref BinaryDiffReader reader, bool asKey) {
            SkipListLeafNode node = default;
            UpdateOrInit(ref reader, ref node);
            return node;
        }

        public static void UpdateOrInit(ref BinaryDiffReader reader, ref SkipListLeafNode old) {
            old.Key = reader.BareInt32(asKey: false);
            old.Next = new(reader.BareUInt32(asKey: false));
            old.Value = StringHelper.Read(ref reader, asKey: false);
        }

        public static void AcceptNodeVisitor<TVisitor>(in TVisitor visitor, ref SkipListLeafNode node)
            where TVisitor : struct, INodeVisitor<SkipListLeafNode>, allows ref struct {
            if (node.Next.IsNotNull) {
                visitor.VisitNode(ref node.Next);
            }
        }
    }

    private readonly struct SkipListBranchNodeHelper : ITypeHelper<SkipListBranchNode>, IBranchAccessor<SkipListLeafNode, SkipListBranchNode> {
        public static bool Equals(SkipListBranchNode a, SkipListBranchNode b) =>
            a.IsSentinel == b.IsSentinel
            && a.Key == b.Key
            && a.Next.Sequence == b.Next.Sequence
            && a.Down.Sequence == b.Down.Sequence
            && a.Leaf.Sequence == b.Leaf.Sequence;

        public static void Write(BinaryDiffWriter writer, SkipListBranchNode v, bool asKey) {
            writer.BareBoolean(v.IsSentinel, asKey);
            writer.BareInt32(v.Key, asKey);
            writer.BareUInt32(v.Next.Sequence, asKey);
            writer.BareUInt32(v.Down.Sequence, asKey);
            writer.BareUInt32(v.Leaf.Sequence, asKey);
        }

        public static SkipListBranchNode Read(ref BinaryDiffReader reader, bool asKey) {
            SkipListBranchNode node = default;
            UpdateOrInit(ref reader, ref node);
            return node;
        }

        public static void UpdateOrInit(ref BinaryDiffReader reader, ref SkipListBranchNode old) {
            old.IsSentinel = reader.BareBoolean(asKey: false);
            old.Key = reader.BareInt32(asKey: false);
            old.Next = new(reader.BareUInt32(asKey: false));
            old.Down = new(reader.BareUInt32(asKey: false));
            old.Leaf = new(reader.BareUInt32(asKey: false));
        }

        public static void AcceptNodeVisitor<TVisitor>(in TVisitor visitor, ref SkipListLeafNode node)
            where TVisitor : struct, INodeVisitor<SkipListLeafNode>, allows ref struct {
            SkipListLeafNodeHelper.AcceptNodeVisitor(in visitor, ref node);
        }

        public static void AcceptLeafVisitor<TVisitor>(in TVisitor visitor, ref SkipListBranchNode node)
            where TVisitor : struct, INodeVisitor<SkipListLeafNode>, allows ref struct {
            if (!node.Leaf.IsNull) {
                visitor.VisitNode(ref node.Leaf);
            }
        }

        public static void AcceptBranchVisitor<TVisitor>(in TVisitor visitor, ref SkipListBranchNode node)
            where TVisitor : struct, IBranchVisitor<SkipListBranchNode>, allows ref struct {
            if (!node.Next.IsNull) {
                visitor.VisitBranch(ref node.Next);
            }
            if (!node.Down.IsNull) {
                visitor.VisitBranch(ref node.Down);
            }
        }
    }
}
