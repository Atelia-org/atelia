using System.Buffers;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class DruableTreeCoreTests {
    [Fact]
    public void CollectBuilderNodes_ThenWriteRebase_AndApplyDelta_RoundTripsReachableNodesAndRoots() {
        var source = new DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper>();

        ref TestNode child = ref source.AllocNodeRef(out NodeSequence childSeq);
        child = new() { Value = 10 };

        ref TestNode unreachable = ref source.AllocNodeRef(out _);
        unreachable = new() { Value = 999 };

        ref TestNode root = ref source.AllocNodeRef(out NodeSequence rootSeq);
        root = new() { Value = 30, Left = childSeq };

        source.CurrentRoots = new(rootSeq, Revision: 7);
        source.CollectBuilderNodes();

        Assert.Equal(2, source.CurrentNodeCount);
        Assert.True(source.TryGetNodeIndex(childSeq, out int childIndex));
        Assert.True(source.TryGetNodeIndex(rootSeq, out int rootIndex));
        Assert.True(childIndex < rootIndex);

        byte[] payload = WriteRebase(source);

        var target = new DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper>();
        ApplyPayload(ref target, payload);
        target.SyncCurrentFromCommitted();

        Assert.Equal(2, target.CurrentNodeCount);
        Assert.Equal(2, target.CommittedNodeCount);
        Assert.Equal(new TestRoots(rootSeq, 7), target.CurrentRoots);
        Assert.Equal(new TestNode { Value = 10 }, target.GetNode(childSeq));
        Assert.Equal(new TestNode { Value = 30, Left = childSeq }, target.GetNode(rootSeq));
    }

    [Fact]
    public void WriteDeltify_WithDirtyCommittedReplacementAndAppendedNode_RoundTrips() {
        var source = new DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper>();

        ref TestNode root = ref source.AllocNodeRef(out NodeSequence rootSeq);
        root = new() { Value = 10 };
        source.CurrentRoots = new(rootSeq, Revision: 1);
        source.Commit();

        byte[] rebasePayload = WriteRebase(source);

        ref TestNode child = ref source.AllocNodeRef(out NodeSequence childSeq);
        child = new() { Value = 20 };
        source.ReplaceNode(rootSeq, new TestNode { Value = 11, Left = childSeq });
        source.CurrentRoots = new(rootSeq, Revision: 2);

        Assert.True(source.HasChanges);
        Assert.Equal(1, source.DirtyCommittedCount);
        Assert.Equal(4, source.DeltifyCount);

        byte[] deltaPayload = WriteDelta(source);

        var target = new DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper>();
        ApplyPayload(ref target, rebasePayload);
        ApplyPayload(ref target, deltaPayload);
        target.SyncCurrentFromCommitted();

        Assert.Equal(new TestRoots(rootSeq, 2), target.CurrentRoots);
        Assert.Equal(2, target.CurrentNodeCount);
        Assert.Equal(new TestNode { Value = 11, Left = childSeq }, target.GetNode(rootSeq));
        Assert.Equal(new TestNode { Value = 20 }, target.GetNode(childSeq));
        Assert.False(target.HasChanges);
    }

    [Fact]
    public void Revert_RestoresDirtyCommittedReplacementRootsAndAppendedNodes() {
        var core = new DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper>();

        ref TestNode root = ref core.AllocNodeRef(out NodeSequence rootSeq);
        root = new() { Value = 10 };
        core.CurrentRoots = new(rootSeq, Revision: 1);
        core.Commit();

        ref TestNode child = ref core.AllocNodeRef(out NodeSequence childSeq);
        child = new() { Value = 20 };
        core.ReplaceNode(rootSeq, new TestNode { Value = 11, Left = childSeq });
        core.CurrentRoots = new(rootSeq, Revision: 2);

        Assert.True(core.HasChanges);
        Assert.Equal(2, core.CurrentNodeCount);
        Assert.Equal(1, core.DirtyCommittedCount);

        core.Revert();

        Assert.False(core.HasChanges);
        Assert.Equal(1, core.CurrentNodeCount);
        Assert.Equal(1, core.CommittedNodeCount);
        Assert.Equal(0, core.DirtyCommittedCount);
        Assert.Equal(new TestRoots(rootSeq, 1), core.CurrentRoots);
        Assert.Equal(new TestNode { Value = 10 }, core.GetNode(rootSeq));
        Assert.False(core.TryGetNodeIndex(childSeq, out _));
    }

    [Fact]
    public void ReplaceNode_BackToCommittedValue_ClearsDirtyTracking() {
        var core = new DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper>();

        ref TestNode root = ref core.AllocNodeRef(out NodeSequence rootSeq);
        root = new() { Value = 10 };
        core.CurrentRoots = new(rootSeq, Revision: 1);
        core.Commit();

        core.ReplaceNode(rootSeq, new TestNode { Value = 11 });
        Assert.True(core.HasChanges);
        Assert.Equal(1, core.DirtyCommittedCount);

        core.ReplaceNode(rootSeq, new TestNode { Value = 10 });

        Assert.False(core.HasChanges);
        Assert.Equal(0, core.DirtyCommittedCount);
        Assert.Equal(0, core.DeltifyCount);
        Assert.Equal(new TestNode { Value = 10 }, core.GetNode(rootSeq));
    }

    [Fact]
    public void GetWritableNodeRef_AfterWrite_BackToCommittedValue_ClearsDirtyTracking() {
        var core = new DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper>();

        ref TestNode root = ref core.AllocNodeRef(out NodeSequence rootSeq);
        root = new() { Value = 10 };
        core.CurrentRoots = new(rootSeq, Revision: 1);
        core.Commit();

        ref TestNode writable = ref core.GetWritableNodeRef(rootSeq, out var firstToken);
        writable.Value = 12;
        core.AfterWrite(firstToken);

        Assert.True(core.HasChanges);
        Assert.Equal(1, core.DirtyCommittedCount);

        ref TestNode writableAgain = ref core.GetWritableNodeRef(rootSeq, out var secondToken);
        writableAgain.Value = 10;
        core.AfterWrite(secondToken);

        Assert.False(core.HasChanges);
        Assert.Equal(0, core.DirtyCommittedCount);
        Assert.Equal(new TestNode { Value = 10 }, core.GetNode(rootSeq));
    }

    private static byte[] WriteRebase(DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper> core) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        core.WriteRebase(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteDelta(DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper> core) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        core.WriteDeltify(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static void ApplyPayload(
        ref DurableTreeCore<TestNode, TestNodeHelper, TestRoots, TestRootsHelper> core,
        ReadOnlySpan<byte> payload
    ) {
        var reader = new BinaryDiffReader(payload);
        core.ApplyDelta(ref reader);
        reader.EnsureFullyConsumed();
    }

    private struct TestNode {
        public int Value;
        public NodeSequence Left;
        public NodeSequence Right;
    }

    private readonly struct TestNodeHelper : ITypeHelper<TestNode>, ISubNodesGetter<TestNode> {
        public static int Count => 3;

        public static bool Equals(TestNode a, TestNode b) =>
            a.Value == b.Value
            && a.Left == b.Left
            && a.Right == b.Right;

        public static void Write(BinaryDiffWriter writer, TestNode v, bool asKey) {
            writer.BareInt32(v.Value, asKey);
            writer.BareUInt32(v.Left.Value, asKey);
            writer.BareUInt32(v.Right.Value, asKey);
        }

        public static TestNode Read(ref BinaryDiffReader reader, bool asKey) {
            TestNode node = default;
            UpdateOrInit(ref reader, ref node);
            return node;
        }

        public static void UpdateOrInit(ref BinaryDiffReader reader, ref TestNode old) {
            old.Value = reader.BareInt32(asKey: false);
            old.Left = new(reader.BareUInt32(asKey: false));
            old.Right = new(reader.BareUInt32(asKey: false));
        }

        public static void GetSubNodes(in TestNode node, Stack<NodeSequence> result) {
            if (!node.Left.Equals(default)) { result.Push(node.Left); }
            if (!node.Right.Equals(default)) { result.Push(node.Right); }
        }
    }

    private readonly record struct TestRoots(NodeSequence Root, int Revision);

    private readonly struct TestRootsHelper : ITypeHelper<TestRoots>, ISubNodesGetter<TestRoots> {
        public static int Count => 2;
        public static bool Equals(TestRoots a, TestRoots b) => a == b;

        public static void Write(BinaryDiffWriter writer, TestRoots v, bool asKey) {
            writer.BareUInt32(v.Root.Value, asKey);
            writer.BareInt32(v.Revision, asKey);
        }

        public static TestRoots Read(ref BinaryDiffReader reader, bool asKey) {
            TestRoots roots = default;
            UpdateOrInit(ref reader, ref roots);
            return roots;
        }

        public static void UpdateOrInit(ref BinaryDiffReader reader, ref TestRoots old) {
            old = new(
                new NodeSequence(reader.BareUInt32(asKey: false)),
                reader.BareInt32(asKey: false)
            );
        }

        public static void GetSubNodes(in TestRoots roots, Stack<NodeSequence> result) {
            if (!roots.Root.Equals(default)) { result.Push(roots.Root); }
        }
    }
}
