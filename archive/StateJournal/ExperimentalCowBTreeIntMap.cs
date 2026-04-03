using System.Diagnostics;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// 用于验证 <see cref="DurableTreeCore{TNode, NodeHelper, TRoots, RootsHelper}"/> builder/COW 原语是否顺手的
/// 小型实验容器：固定 2-3-4 阶的 int->int B+Tree。
/// 这里优先追求 API 验证和节点 replace / leaf chain 体验，不追求最终对外形态。
/// </summary>
internal sealed class ExperimentalCowBTreeIntMap {
    private const int MaxKeys = 3;
    private const int MaxChildren = MaxKeys + 1;

    private DurableTreeCore<BTreeNode, BTreeNodeHelper, BTreeRoots, BTreeRootsHelper> _core;

    public int Count => _core.CurrentRoots.Count;
    internal int DebugNodeCount => _core.CurrentNodeCount;
    internal int DebugCommittedNodeCount => _core.CommittedNodeCount;

    public ExperimentalCowBTreeIntMap() {
        _core = new();
    }

    public bool TryGet(int key, out int value) {
        NodeSequence root = _core.CurrentRoots.Root;
        while (!root.Equals(default)) {
            BTreeNode node = _core.GetNode(root);
            if (node.IsLeaf) {
                int index = FindLeafKeyIndex(node, key, out bool found);
                if (found) {
                    value = GetValue(node, index);
                    return true;
                }
                break;
            }

            root = GetChild(node, FindChildIndex(node, key));
        }

        value = default;
        return false;
    }

    public void Upsert(int key, int value) {
        if (_core.CurrentRoots.Root.Equals(default)) {
            ref BTreeNode root = ref _core.AllocNodeRef(out NodeSequence rootSeq);
            root.IsLeaf = true;
            root.KeyCount = 1;
            SetKey(ref root, 0, key);
            SetValue(ref root, 0, value);
            _core.CurrentRoots = new(rootSeq, count: 1);
            return;
        }

        NodeSequence rootBefore = _core.CurrentRoots.Root;
        InsertResult result = InsertCore(rootBefore, key, value);
        if (result.Split) {
            ref BTreeNode newRoot = ref _core.AllocNodeRef(out NodeSequence newRootSeq);
            newRoot.IsLeaf = false;
            newRoot.KeyCount = 1;
            SetKey(ref newRoot, 0, result.PromotedKey);
            SetChild(ref newRoot, 0, result.Left);
            SetChild(ref newRoot, 1, result.Right);
            _core.CurrentRoots = new(newRootSeq, _core.CurrentRoots.Count + (result.Inserted ? 1 : 0));
            return;
        }

        _core.CurrentRoots = new(result.Left, _core.CurrentRoots.Count + (result.Inserted ? 1 : 0));
    }

    public void Commit() {
        _core.CollectBuilderNodes();
        _core.Commit();
    }

    public void Revert() => _core.Revert();

    public void CollectBuilderNodes() => _core.CollectBuilderNodes();

    /// <summary>
    /// 实验性的有序读取入口。这里直接走 leaf sibling chain，
    /// 用来验证“committed node replace + stable sequence”后 B+Tree 叶链维护是否顺手。
    /// </summary>
    public IReadOnlyList<KeyValuePair<int, int>> ReadAscendingFrom(int minInclusive, int maxCount) {
        if (maxCount < 0) { throw new ArgumentOutOfRangeException(nameof(maxCount)); }

        List<KeyValuePair<int, int>> result = new(Math.Min(maxCount, Count));
        if (maxCount == 0 || _core.CurrentRoots.Root.Equals(default)) { return result; }

        if (!TryFindLeafLowerBound(minInclusive, out NodeSequence leafSequence, out int startIndex)) { return result; }

        while (!leafSequence.Equals(default) && result.Count < maxCount) {
            BTreeNode leaf = _core.GetNode(leafSequence);
            for (int i = startIndex; i < leaf.KeyCount && result.Count < maxCount; ++i) {
                result.Add(new(GetKey(leaf, i), GetValue(leaf, i)));
            }
            leafSequence = leaf.NextLeaf;
            startIndex = 0;
        }
        return result;
    }

    public bool TryGetLowerBound(int minInclusive, out KeyValuePair<int, int> result) {
        if (!TryFindLeafLowerBound(minInclusive, out NodeSequence leafSequence, out int index)) {
            result = default;
            return false;
        }
        BTreeNode leaf = _core.GetNode(leafSequence);
        result = new(GetKey(leaf, index), GetValue(leaf, index));
        return true;
    }

    public bool TryGetNext(int keyExclusive, out KeyValuePair<int, int> result) {
        if (keyExclusive == int.MaxValue) {
            result = default;
            return false;
        }
        return TryGetLowerBound(keyExclusive + 1, out result);
    }

    private InsertResult InsertCore(NodeSequence nodeSequence, int key, int value) {
        BTreeNode node = _core.GetNode(nodeSequence);

        if (node.IsLeaf) {
            int leafIndex = FindLeafKeyIndex(node, key, out bool found);
            return InsertIntoLeaf(nodeSequence, key, value, leafIndex, found);
        }

        int childIndex = FindChildIndex(node, key);
        NodeSequence child = GetChild(node, childIndex);
        InsertResult childResult = InsertCore(child, key, value);
        bool needsSeparatorUpdate = childIndex > 0 && GetKey(node, childIndex - 1) != childResult.LeftFirstKey;
        if (!childResult.Split) {
            if (needsSeparatorUpdate) {
                ref BTreeNode writable = ref _core.GetWritableNodeRef(nodeSequence, out var token);
                SetKey(ref writable, childIndex - 1, childResult.LeftFirstKey);
                _core.AfterWrite(token);
            }

            return new(nodeSequence, default, Split: false, Inserted: childResult.Inserted, 0, GetSubtreeFirstKey(nodeSequence));
        }

        return InsertPromotedIntoInternal(
            nodeSequence,
            childIndex,
            childResult.Right,
            childResult.PromotedKey,
            childResult.LeftFirstKey,
            childResult.Inserted
        );
    }

    private InsertResult InsertIntoLeaf(NodeSequence leafSequence, int key, int value, int insertIndex, bool found) {
        ref BTreeNode leaf = ref _core.GetWritableNodeRef(leafSequence, out var token);
        int oldCount = leaf.KeyCount;

        if (found) {
            SetValue(ref leaf, insertIndex, value);
            _core.AfterWrite(token);
            return new(leafSequence, default, Split: false, Inserted: false, 0, GetKey(leaf, 0));
        }

        Span<int> keys = stackalloc int[MaxKeys + 1];
        Span<int> values = stackalloc int[MaxKeys + 1];
        for (int i = 0; i < insertIndex; ++i) {
            keys[i] = GetKey(leaf, i);
            values[i] = GetValue(leaf, i);
        }
        keys[insertIndex] = key;
        values[insertIndex] = value;
        for (int i = insertIndex; i < oldCount; ++i) {
            keys[i + 1] = GetKey(leaf, i);
            values[i + 1] = GetValue(leaf, i);
        }

        int totalCount = oldCount + 1;
        if (totalCount <= MaxKeys) {
            WriteLeaf(ref leaf, keys, values, totalCount, leaf.NextLeaf);
            _core.AfterWrite(token);
            return new(leafSequence, default, Split: false, Inserted: true, 0, GetKey(leaf, 0));
        }

        int leftCount = totalCount / 2;
        int rightCount = totalCount - leftCount;
        NodeSequence oldNext = leaf.NextLeaf;

        ref BTreeNode right = ref _core.AllocNodeRef(out NodeSequence rightSequence);
        right.IsLeaf = true;
        ref BTreeNode leftAfterAlloc = ref _core.GetWritableNodeRef(leafSequence, out _);
        WriteLeaf(ref leftAfterAlloc, keys[..leftCount], values[..leftCount], leftCount, rightSequence);
        WriteLeaf(ref right, keys[leftCount..(leftCount + rightCount)], values[leftCount..(leftCount + rightCount)], rightCount, oldNext);
        _core.AfterWrite(token);

        int promotedKey = GetKey(right, 0);
        return new(leafSequence, rightSequence, Split: true, Inserted: true, promotedKey, GetKey(leaf, 0));
    }

    private InsertResult InsertPromotedIntoInternal(
        NodeSequence nodeSequence,
        int insertIndex,
        NodeSequence rightChild,
        int promotedKey,
        int leftFirstKey,
        bool inserted
    ) {
        ref BTreeNode node = ref _core.GetWritableNodeRef(nodeSequence, out var token);
        int oldCount = node.KeyCount;

        Span<int> keys = stackalloc int[MaxKeys + 1];
        Span<NodeSequence> children = stackalloc NodeSequence[MaxChildren + 1];

        for (int i = 0; i < insertIndex; ++i) {
            keys[i] = GetKey(node, i);
        }
        keys[insertIndex] = promotedKey;
        for (int i = insertIndex; i < oldCount; ++i) {
            keys[i + 1] = GetKey(node, i);
        }
        if (insertIndex > 0) {
            keys[insertIndex - 1] = leftFirstKey;
        }

        for (int i = 0; i <= insertIndex; ++i) {
            children[i] = GetChild(node, i);
        }
        children[insertIndex + 1] = rightChild;
        for (int i = insertIndex + 1; i <= oldCount; ++i) {
            children[i + 1] = GetChild(node, i);
        }

        int totalCount = oldCount + 1;
        if (totalCount <= MaxKeys) {
            WriteInternal(ref node, keys[..totalCount], children[..(totalCount + 1)], totalCount);
            _core.AfterWrite(token);
            return new(nodeSequence, default, Split: false, Inserted: inserted, 0, GetSubtreeFirstKey(nodeSequence));
        }

        int promoteIndex = totalCount / 2;
        int leftCount = promoteIndex;
        int rightCount = totalCount - promoteIndex - 1;

        int parentKey = keys[promoteIndex];

        ref BTreeNode right = ref _core.AllocNodeRef(out NodeSequence rightSequence);
        right.IsLeaf = false;
        ref BTreeNode leftAfterAlloc = ref _core.GetWritableNodeRef(nodeSequence, out _);
        WriteInternal(ref leftAfterAlloc, keys[..leftCount], children[..(leftCount + 1)], leftCount);
        WriteInternal(
            ref right,
            keys[(promoteIndex + 1)..(promoteIndex + 1 + rightCount)],
            children[(promoteIndex + 1)..(promoteIndex + 2 + rightCount)],
            rightCount
        );
        _core.AfterWrite(token);

        return new(nodeSequence, rightSequence, Split: true, Inserted: inserted, parentKey, GetSubtreeFirstKey(nodeSequence));
    }

    private static int FindLeafKeyIndex(BTreeNode node, int key, out bool found) {
        int count = node.KeyCount;
        for (int i = 0; i < count; ++i) {
            int current = GetKey(node, i);
            if (key == current) {
                found = true;
                return i;
            }
            if (key < current) {
                found = false;
                return i;
            }
        }
        found = false;
        return count;
    }

    private int GetSubtreeFirstKey(NodeSequence sequence) {
        while (true) {
            BTreeNode node = _core.GetNode(sequence);
            if (node.IsLeaf) { return GetKey(node, 0); }
            sequence = node.Child0;
        }
    }

    private bool TryFindLeafLowerBound(int minInclusive, out NodeSequence leafSequence, out int startIndex) {
        leafSequence = _core.CurrentRoots.Root;
        startIndex = 0;
        if (leafSequence.Equals(default)) { return false; }

        while (true) {
            BTreeNode node = _core.GetNode(leafSequence);
            if (node.IsLeaf) {
                for (int i = 0; i < node.KeyCount; ++i) {
                    if (GetKey(node, i) < minInclusive) { continue; }
                    startIndex = i;
                    return true;
                }

                if (node.NextLeaf.Equals(default)) {
                    leafSequence = default;
                    return false;
                }

                leafSequence = node.NextLeaf;
                startIndex = 0;
                minInclusive = int.MinValue;
                continue;
            }

            leafSequence = GetChild(node, FindChildIndex(node, minInclusive));
        }
    }

    private static int FindChildIndex(BTreeNode node, int minInclusive) {
        int count = node.KeyCount;
        for (int i = 0; i < count; ++i) {
            int separator = GetKey(node, i);
            if (minInclusive < separator) { return i; }
            if (minInclusive == separator) { return i + 1; }
        }
        return count;
    }
    private static void WriteLeaf(ref BTreeNode node, ReadOnlySpan<int> keys, ReadOnlySpan<int> values, int count, NodeSequence nextLeaf) {
        Debug.Assert(count <= MaxKeys);
        node.IsLeaf = true;
        node.KeyCount = (byte)count;
        ClearNodePayload(ref node);
        for (int i = 0; i < count; ++i) {
            SetKey(ref node, i, keys[i]);
            SetValue(ref node, i, values[i]);
        }
        node.NextLeaf = nextLeaf;
    }

    private static void WriteInternal(ref BTreeNode node, ReadOnlySpan<int> keys, ReadOnlySpan<NodeSequence> children, int count) {
        Debug.Assert(count <= MaxKeys);
        Debug.Assert(children.Length == count + 1);
        node.IsLeaf = false;
        node.KeyCount = (byte)count;
        ClearNodePayload(ref node);
        for (int i = 0; i < count; ++i) {
            SetKey(ref node, i, keys[i]);
        }
        for (int i = 0; i <= count; ++i) {
            SetChild(ref node, i, children[i]);
        }
    }

    private static void ClearNodePayload(ref BTreeNode node) {
        node.Key0 = 0;
        node.Key1 = 0;
        node.Key2 = 0;
        node.Value0 = 0;
        node.Value1 = 0;
        node.Value2 = 0;
        node.Child0 = default;
        node.Child1 = default;
        node.Child2 = default;
        node.Child3 = default;
        node.NextLeaf = default;
    }

    private static int GetKey(BTreeNode node, int index) => index switch {
        0 => node.Key0,
        1 => node.Key1,
        2 => node.Key2,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private static void SetKey(ref BTreeNode node, int index, int value) {
        switch (index) {
            case 0: node.Key0 = value; break;
            case 1: node.Key1 = value; break;
            case 2: node.Key2 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private static int GetValue(BTreeNode node, int index) => index switch {
        0 => node.Value0,
        1 => node.Value1,
        2 => node.Value2,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private static void SetValue(ref BTreeNode node, int index, int value) {
        switch (index) {
            case 0: node.Value0 = value; break;
            case 1: node.Value1 = value; break;
            case 2: node.Value2 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private static NodeSequence GetChild(BTreeNode node, int index) => index switch {
        0 => node.Child0,
        1 => node.Child1,
        2 => node.Child2,
        3 => node.Child3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private static void SetChild(ref BTreeNode node, int index, NodeSequence value) {
        switch (index) {
            case 0: node.Child0 = value; break;
            case 1: node.Child1 = value; break;
            case 2: node.Child2 = value; break;
            case 3: node.Child3 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private readonly record struct InsertResult(
        NodeSequence Left,
        NodeSequence Right,
        bool Split,
        bool Inserted,
        int PromotedKey,
        int LeftFirstKey
    );

    private struct BTreeRoots {
        public NodeSequence Root;
        public int Count;

        public BTreeRoots(NodeSequence root, int count) {
            Root = root;
            Count = count;
        }
    }

    private struct BTreeNode {
        public bool IsLeaf;
        public byte KeyCount;
        public int Key0;
        public int Key1;
        public int Key2;
        public int Value0;
        public int Value1;
        public int Value2;
        public NodeSequence Child0;
        public NodeSequence Child1;
        public NodeSequence Child2;
        public NodeSequence Child3;
        public NodeSequence NextLeaf;
    }

    private readonly struct BTreeNodeHelper : ITypeHelper<BTreeNode>, ISubNodesGetter<BTreeNode> {
        public static int Count => 4;
        public static bool Equals(BTreeNode a, BTreeNode b) =>
            a.IsLeaf == b.IsLeaf
            && a.KeyCount == b.KeyCount
            && a.Key0 == b.Key0
            && a.Key1 == b.Key1
            && a.Key2 == b.Key2
            && a.Value0 == b.Value0
            && a.Value1 == b.Value1
            && a.Value2 == b.Value2
            && a.Child0 == b.Child0
            && a.Child1 == b.Child1
            && a.Child2 == b.Child2
            && a.Child3 == b.Child3
            && a.NextLeaf == b.NextLeaf;

        public static void Write(BinaryDiffWriter writer, BTreeNode v, bool asKey) {
            writer.BareBoolean(v.IsLeaf, asKey);
            writer.BareByte(v.KeyCount, asKey);
            writer.BareInt32(v.Key0, asKey);
            writer.BareInt32(v.Key1, asKey);
            writer.BareInt32(v.Key2, asKey);
            writer.BareInt32(v.Value0, asKey);
            writer.BareInt32(v.Value1, asKey);
            writer.BareInt32(v.Value2, asKey);
            writer.BareUInt32(v.Child0.Value, asKey);
            writer.BareUInt32(v.Child1.Value, asKey);
            writer.BareUInt32(v.Child2.Value, asKey);
            writer.BareUInt32(v.Child3.Value, asKey);
            writer.BareUInt32(v.NextLeaf.Value, asKey);
        }

        public static BTreeNode Read(ref BinaryDiffReader reader, bool asKey) {
            BTreeNode node = default;
            UpdateOrInit(ref reader, ref node);
            return node;
        }

        public static void UpdateOrInit(ref BinaryDiffReader reader, ref BTreeNode old) {
            old.IsLeaf = reader.BareBoolean(asKey: false);
            old.KeyCount = reader.BareByte(asKey: false);
            old.Key0 = reader.BareInt32(asKey: false);
            old.Key1 = reader.BareInt32(asKey: false);
            old.Key2 = reader.BareInt32(asKey: false);
            old.Value0 = reader.BareInt32(asKey: false);
            old.Value1 = reader.BareInt32(asKey: false);
            old.Value2 = reader.BareInt32(asKey: false);
            old.Child0 = new(reader.BareUInt32(asKey: false));
            old.Child1 = new(reader.BareUInt32(asKey: false));
            old.Child2 = new(reader.BareUInt32(asKey: false));
            old.Child3 = new(reader.BareUInt32(asKey: false));
            old.NextLeaf = new(reader.BareUInt32(asKey: false));
        }

        public static void GetSubNodes(in BTreeNode node, Stack<NodeSequence> result) {
            if (node.IsLeaf) { return; }
            if (!node.Child0.Equals(default)) { result.Push(node.Child0); }
            if (!node.Child1.Equals(default)) { result.Push(node.Child1); }
            if (!node.Child2.Equals(default)) { result.Push(node.Child2); }
            if (!node.Child3.Equals(default)) { result.Push(node.Child3); }
        }
    }

    private readonly struct BTreeRootsHelper : ITypeHelper<BTreeRoots>, ISubNodesGetter<BTreeRoots> {
        public static int Count => 2;
        public static bool Equals(BTreeRoots a, BTreeRoots b) => a.Root == b.Root && a.Count == b.Count;
        public static void Write(BinaryDiffWriter writer, BTreeRoots v, bool asKey) {
            writer.BareUInt32(v.Root.Value, asKey);
            writer.BareInt32(v.Count, asKey);
        }

        public static BTreeRoots Read(ref BinaryDiffReader reader, bool asKey) {
            BTreeRoots roots = default;
            UpdateOrInit(ref reader, ref roots);
            return roots;
        }

        public static void UpdateOrInit(ref BinaryDiffReader reader, ref BTreeRoots old) {
            old.Root = new(reader.BareUInt32(asKey: false));
            old.Count = reader.BareInt32(asKey: false);
        }

        public static void GetSubNodes(in BTreeRoots node, Stack<NodeSequence> result) {
            if (!node.Root.Equals(default)) { result.Push(node.Root); }
        }
    }
}
