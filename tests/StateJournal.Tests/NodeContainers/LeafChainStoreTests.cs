using System.Buffers;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.NodeContainers;
using Atelia.StateJournal.Serialization;
using Xunit;

using IntArena = Atelia.StateJournal.NodeContainers.LeafChainStore<int, int, Atelia.StateJournal.Internal.Int32Helper, Atelia.StateJournal.Internal.Int32Helper>;

namespace Atelia.StateJournal.Tests.NodeContainers;

public class LeafChainStoreTests {

    #region Helpers

    private static LeafHandle BuildLinkedList(
        ref IntArena arena, params (int Key, int Value)[] items
    ) {
        LeafHandle head = default;
        LeafHandle prev = default;

        foreach (var (key, value) in items) {
            LeafHandle handle = arena.AllocNode(key, value);

            if (head.IsNull) {
                head = handle;
            }
            else {
                arena.SetNext(ref prev, handle);
            }
            prev = handle;
        }

        return head;
    }

    private static List<(int Key, int Value)> ReadChain(
        ref IntArena arena, LeafHandle head
    ) {
        var result = new List<(int, int)>();
        var current = head;
        int guard = 10000;
        while (current.IsNotNull && --guard > 0) {
            var (k, v) = arena.GetEntry(ref current);
            result.Add((k, v));
            current = arena.GetNext(ref current);
        }
        return result;
    }

    #endregion

    [Fact]
    public void AllocAndRead_FormsSinglyLinkedChain() {
        IntArena arena = new();

        var head = BuildLinkedList(ref arena, (1, 10), (2, 20), (3, 30));

        var chain = ReadChain(ref arena, head);
        Assert.Equal([(1, 10), (2, 20), (3, 30)], chain);
        Assert.Equal(3, arena.CurrentNodeCount);
    }

    [Fact]
    public void CommitAndRevert_RestoresValueAndLinks() {
        IntArena arena = new();

        var head = BuildLinkedList(ref arena, (1, 10), (2, 20), (3, 30));
        arena.Commit();

        Assert.Equal(3, arena.CommittedNodeCount);

        // Modify value of second node
        var second = arena.GetNext(ref head);
        arena.SetValue(ref second, 99);

        Assert.Equal(1, arena.DirtyValueCount);
        Assert.Equal(0, arena.DirtyLinkCount);

        // Revert should restore original value
        arena.Revert();

        var chain = ReadChain(ref arena, head);
        Assert.Equal([(1, 10), (2, 20), (3, 30)], chain);
    }

    [Fact]
    public void Revert_RestoresLinkChanges() {
        IntArena arena = new();

        // Build: 1 -> 2 -> 3
        var head = BuildLinkedList(ref arena, (1, 10), (2, 20), (3, 30));
        arena.Commit();

        // Now "delete" node 2 by relinking: 1 -> 3
        var second = arena.GetNext(ref head);
        uint thirdSeq = arena.GetNextSequence(ref second);
        arena.SetNextSequence(ref head, thirdSeq);

        Assert.Equal(0, arena.DirtyValueCount);
        Assert.Equal(1, arena.DirtyLinkCount);

        var chainBeforeRevert = ReadChain(ref arena, head);
        Assert.Equal([(1, 10), (3, 30)], chainBeforeRevert);

        // Revert should restore: 1 -> 2 -> 3
        arena.Revert();

        var chain = ReadChain(ref arena, head);
        Assert.Equal([(1, 10), (2, 20), (3, 30)], chain);
    }

    [Fact]
    public void Revert_RemovesAppendedNodes() {
        IntArena arena = new();

        var head = BuildLinkedList(ref arena, (1, 10), (2, 20));
        arena.Commit();
        Assert.Equal(2, arena.CurrentNodeCount);

        // Append node 3 after node 2
        var second = arena.GetNext(ref head);
        LeafHandle newHandle = arena.AllocNode(3, 30);
        arena.SetNext(ref second, newHandle);

        Assert.Equal(3, arena.CurrentNodeCount);

        arena.Revert();

        Assert.Equal(2, arena.CurrentNodeCount);
        var chain = ReadChain(ref arena, head);
        Assert.Equal([(1, 10), (2, 20)], chain);
    }

    [Fact]
    public void Revert_ClearsTailValues_ForReferenceTypes() {
        LeafChainStore<int, string, Int32Helper, StringHelper> arena = new();

        arena.AllocNode(1, "kept");
        arena.Commit();

        arena.AllocNode(2, "discard-me");

        arena.Revert();

        var valuesField = typeof(LeafChainStore<int, string, Int32Helper, StringHelper>)
            .GetField("_values", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var values = (string[])valuesField.GetValue(arena)!;
        Assert.Null(values[1]);
    }

    [Fact]
    public void Commit_AdvancesCommittedBoundary() {
        IntArena arena = new();

        BuildLinkedList(ref arena, (1, 10), (2, 20));
        Assert.Equal(0, arena.CommittedNodeCount);
        Assert.Equal(2, arena.CurrentNodeCount);

        arena.Commit();

        Assert.Equal(2, arena.CommittedNodeCount);
        Assert.Equal(2, arena.CurrentNodeCount);
    }

    [Fact]
    public void DirtyTracking_DistinguishesValueFromLink() {
        IntArena arena = new();

        var head = BuildLinkedList(ref arena, (1, 10), (2, 20), (3, 30));
        arena.Commit();

        // Dirty value on node 1
        arena.SetValue(ref head, 99);

        // Dirty link on node 2 (skip node 3: 2 -> null)
        var second = arena.GetNext(ref head);
        arena.SetNextSequence(ref second, 0);

        Assert.Equal(1, arena.DirtyValueCount);
        Assert.Equal(1, arena.DirtyLinkCount);

        // Both should revert correctly
        arena.Revert();

        var chain = ReadChain(ref arena, head);
        Assert.Equal([(1, 10), (2, 20), (3, 30)], chain);
        Assert.Equal(0, arena.DirtyValueCount);
        Assert.Equal(0, arena.DirtyLinkCount);
    }

    [Fact]
    public void DirtyTracking_BothDirtyOnSameNode() {
        IntArena arena = new();

        var head = BuildLinkedList(ref arena, (1, 10), (2, 20));
        arena.Commit();

        // Dirty both value and link on node 1
        arena.SetValue(ref head, 99);
        arena.SetNextSequence(ref head, 0); // unlink

        Assert.Equal(1, arena.DirtyValueCount);
        Assert.Equal(1, arena.DirtyLinkCount);

        arena.Revert();

        var chain = ReadChain(ref arena, head);
        Assert.Equal([(1, 10), (2, 20)], chain);
    }

    [Fact]
    public void CollectAll_RemovesUnreachableNodes() {
        IntArena arena = new();

        // Build draft chain: 1 -> 2 -> 3
        var head = BuildLinkedList(ref arena, (1, 10), (2, 20), (3, 30));

        // Unlink node 2 while still in draft state: 1 -> 3
        var second = arena.GetNext(ref head);
        uint thirdSeq = arena.GetNextSequence(ref second);
        arena.SetNextSequence(ref head, thirdSeq);

        Assert.Equal(3, arena.CurrentNodeCount);

        arena.CollectAll(head.Sequence);

        Assert.Equal(2, arena.CurrentNodeCount);

        // Chain should still be readable (handles need re-lookup after compaction)
        head.ClearCachedIndex();
        var chain = ReadChain(ref arena, head);
        Assert.Equal([(1, 10), (3, 30)], chain);
    }

    [Fact]
    public void CollectAll_WithActiveDirtyTracking_Throws() {
        IntArena arena = new();
        var head = BuildLinkedList(ref arena, (1, 10), (2, 20), (3, 30));
        arena.Commit();

        var second = arena.GetNext(ref head);
        uint thirdSeq = arena.GetNextSequence(ref second);
        arena.SetNextSequence(ref head, thirdSeq);

        Assert.Throws<InvalidOperationException>(() => arena.CollectAll(head.Sequence));
    }

    [Fact]
    public void CollectDraft_OnlyRemovesUncommittedUnreachableNodes() {
        IntArena arena = new();

        var head = BuildLinkedList(ref arena, (1, 10), (2, 20));
        arena.Commit();

        // Add two draft nodes, but only link one
        LeafHandle h3 = arena.AllocNode(3, 30);
        arena.AllocNode(4, 40); // h4 is orphaned (not linked)

        // Link: 2 -> 3 (4 is orphaned)
        var second = arena.GetNext(ref head);
        arena.SetNext(ref second, h3);

        Assert.Equal(4, arena.CurrentNodeCount);
        Assert.Equal(2, arena.CommittedNodeCount);

        arena.CollectDraft(head.Sequence);

        // Node 4 was uncommitted and unreachable → swept
        Assert.Equal(3, arena.CurrentNodeCount);
        Assert.Equal(2, arena.CommittedNodeCount); // committed region untouched

        head.ClearCachedIndex();
        var chain = ReadChain(ref arena, head);
        Assert.Equal([(1, 10), (2, 20), (3, 30)], chain);
    }

    [Fact]
    public void CollectCommitted_RemovesUnreachableCommittedNodes() {
        IntArena arena = new();

        // Build and commit: 1 -> 2 -> 3
        var head = BuildLinkedList(ref arena, (1, 10), (2, 20), (3, 30));
        arena.Commit();

        // Simulate "after load": unlink 2 directly in committed state
        // by using SetNextSequence (which dirties) then commit
        var second = arena.GetNext(ref head);
        uint thirdSeq = arena.GetNextSequence(ref second);
        arena.SetNextSequence(ref head, thirdSeq);
        arena.Commit();

        // Now all 3 nodes are committed but 2 is unreachable
        Assert.Equal(3, arena.CommittedNodeCount);

        arena.CollectCommitted(head.Sequence);

        Assert.Equal(2, arena.CommittedNodeCount);
    }

    [Fact]
    public void CollectCommitted_WithActiveDirtyTracking_Throws() {
        IntArena arena = new();
        var head = BuildLinkedList(ref arena, (1, 10), (2, 20), (3, 30));
        arena.Commit();

        var second = arena.GetNext(ref head);
        arena.SetValue(ref second, 99);

        Assert.Throws<InvalidOperationException>(() => arena.CollectCommitted(head.Sequence));
    }

    #region Serialization Round-Trip

    /// <summary>Helper: build committed chain in target arena matching source's committed state.</summary>
    private static LeafHandle SeedCommitted(
        ref IntArena arena, params (int Key, int Value)[] items
    ) {
        var head = BuildLinkedList(ref arena, items);
        arena.Commit();
        return head;
    }

    private static byte[] WriteDelta(ref IntArena arena) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        arena.WriteDeltify(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteRebase(ref IntArena arena) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        arena.WriteRebase(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteDeltaWithAppended(params (uint Sequence, uint NextSequence, int Key, int Value)[] appended) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.WriteCount(0); // link mutations
        writer.WriteCount(0); // value mutations
        writer.WriteCount(appended.Length);
        foreach (var item in appended) {
            writer.BareUInt32(item.Sequence, asKey: true);
            writer.BareUInt32(item.NextSequence, asKey: false);
            Int32Helper.Write(writer, item.Key, asKey: true);
            Int32Helper.Write(writer, item.Value, asKey: false);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void ApplyAndSync(ref IntArena target, byte[] payload) {
        var reader = new BinaryDiffReader(payload);
        target.ApplyDelta(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted();
    }

    [Fact]
    public void ApplyDelta_AppendedSequenceNotGreaterThanCommittedTail_Throws() {
        IntArena target = new();
        SeedCommitted(ref target, (1, 10), (2, 20));

        byte[] payload = WriteDeltaWithAppended((2u, 0u, 99, 999));
        Assert.Throws<InvalidDataException>(
            () => {
                var reader = new BinaryDiffReader(payload);
                target.ApplyDelta(ref reader);
            }
        );
    }

    [Fact]
    public void ApplyDelta_AppendedSequencesMustBeStrictlyIncreasing_Throws() {
        IntArena target = new();
        SeedCommitted(ref target, (1, 10), (2, 20));

        byte[] payload = WriteDeltaWithAppended(
            (3u, 0u, 30, 300),
            (3u, 0u, 31, 310)
        );
        Assert.Throws<InvalidDataException>(
            () => {
                var reader = new BinaryDiffReader(payload);
                target.ApplyDelta(ref reader);
            }
        );
    }

    [Fact]
    public void WriteDeltify_LinkOnlyMutation_RoundTrips() {
        // Source: 1→2→3 → relink to 1→3
        IntArena source = new();
        var head = SeedCommitted(ref source, (1, 10), (2, 20), (3, 30));
        var second = source.GetNext(ref head);
        uint thirdSeq = source.GetNextSequence(ref second);
        source.SetNextSequence(ref head, thirdSeq);

        Assert.Equal(1, source.DirtyLinkCount);
        Assert.Equal(0, source.DirtyValueCount);

        byte[] payload = WriteDelta(ref source);

        // Target: same committed state
        IntArena target = new();
        SeedCommitted(ref target, (1, 10), (2, 20), (3, 30));
        ApplyAndSync(ref target, payload);

        // After apply: target should have 1→3 link (node 2 still exists but orphaned)
        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 10), (3, 30)], chain);
    }

    [Fact]
    public void WriteDeltify_ValueOnlyMutation_RoundTrips() {
        // Source: 1→2→3, update node 2's value
        IntArena source = new();
        var head = SeedCommitted(ref source, (1, 10), (2, 20), (3, 30));
        var second = source.GetNext(ref head);
        source.SetValue(ref second, 99);

        Assert.Equal(0, source.DirtyLinkCount);
        Assert.Equal(1, source.DirtyValueCount);

        byte[] payload = WriteDelta(ref source);

        IntArena target = new();
        SeedCommitted(ref target, (1, 10), (2, 20), (3, 30));
        ApplyAndSync(ref target, payload);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 10), (2, 99), (3, 30)], chain);
    }

    [Fact]
    public void WriteDeltify_AppendedNodes_RoundTrips() {
        // Source: 1→2, insert 3 after 2
        IntArena source = new();
        var head = SeedCommitted(ref source, (1, 10), (2, 20));
        var second = source.GetNext(ref head);

        LeafHandle h3 = source.AllocNode(3, 30);
        source.SetNext(ref second, h3);

        Assert.Equal(1, source.DirtyLinkCount);  // node 2's link changed
        Assert.Equal(0, source.DirtyValueCount);
        Assert.Equal(1, source.CurrentNodeCount - source.CommittedNodeCount); // 1 appended

        byte[] payload = WriteDelta(ref source);

        IntArena target = new();
        SeedCommitted(ref target, (1, 10), (2, 20));
        ApplyAndSync(ref target, payload);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 10), (2, 20), (3, 30)], chain);
        Assert.Equal(3, target.CommittedNodeCount);
    }

    [Fact]
    public void WriteDeltify_InsertInMiddle_RoundTrips() {
        // Source: 1→3, insert 2 between them
        IntArena source = new();
        var head = SeedCommitted(ref source, (1, 10), (3, 30));

        LeafHandle h2 = source.AllocNode(2, 20);
        // 1→2→3: set new node's next to 3, then head's next to 2
        uint thirdSeq = source.GetNextSequence(ref head);
        source.SetNext(ref h2, new LeafHandle(thirdSeq));
        source.SetNext(ref head, h2);

        byte[] payload = WriteDelta(ref source);

        IntArena target = new();
        SeedCommitted(ref target, (1, 10), (3, 30));
        ApplyAndSync(ref target, payload);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 10), (2, 20), (3, 30)], chain);
    }

    [Fact]
    public void WriteDeltify_BothLinkAndValueDirtyOnSameNode_RoundTrips() {
        // Source: 1→2→3, change node 1's value AND relink 1→3
        IntArena source = new();
        var head = SeedCommitted(ref source, (1, 10), (2, 20), (3, 30));

        source.SetValue(ref head, 99);
        var second = source.GetNext(ref head);
        uint thirdSeq = source.GetNextSequence(ref second);
        source.SetNextSequence(ref head, thirdSeq);

        Assert.Equal(1, source.DirtyLinkCount);
        Assert.Equal(1, source.DirtyValueCount);

        byte[] payload = WriteDelta(ref source);

        IntArena target = new();
        SeedCommitted(ref target, (1, 10), (2, 20), (3, 30));
        ApplyAndSync(ref target, payload);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 99), (3, 30)], chain);
    }

    [Fact]
    public void WriteDeltify_MixedMutations_RoundTrips() {
        // Source: 1→2→3→4, exercise all mutation types simultaneously:
        //  - value change on node 2
        //  - link-only change on node 3 (→5 instead of →4)
        //  - append node 5 after node 3
        IntArena source = new();
        var head = SeedCommitted(ref source, (1, 10), (2, 20), (3, 30), (4, 40));

        // Value mutation on node 2
        var second = source.GetNext(ref head);
        source.SetValue(ref second, 99);

        // Insert node 5 between 3 and 4
        var third = source.GetNext(ref second);
        uint fourthSeq = source.GetNextSequence(ref third);
        LeafHandle h5 = source.AllocNode(5, 50);
        source.SetNext(ref h5, new LeafHandle(fourthSeq));
        source.SetNext(ref third, h5);

        Assert.Equal(1, source.DirtyLinkCount);    // node 3's link
        Assert.Equal(1, source.DirtyValueCount);  // node 2's value

        byte[] payload = WriteDelta(ref source);

        IntArena target = new();
        SeedCommitted(ref target, (1, 10), (2, 20), (3, 30), (4, 40));
        ApplyAndSync(ref target, payload);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 10), (2, 99), (3, 30), (5, 50), (4, 40)], chain);
    }

    [Fact]
    public void WriteDeltify_EmptyDelta_RoundTrips() {
        IntArena source = new();
        SeedCommitted(ref source, (1, 10), (2, 20));

        // No mutations
        byte[] payload = WriteDelta(ref source);

        IntArena target = new();
        var head = SeedCommitted(ref target, (1, 10), (2, 20));
        ApplyAndSync(ref target, payload);

        var chain = ReadChain(ref target, head);
        Assert.Equal([(1, 10), (2, 20)], chain);
    }

    [Fact]
    public void WriteRebase_RoundTripsToEmptyArena() {
        IntArena source = new();
        var head = BuildLinkedList(ref source, (1, 10), (2, 20), (3, 30));
        source.Commit();

        byte[] payload = WriteRebase(ref source);

        IntArena target = new();
        ApplyAndSync(ref target, payload);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 10), (2, 20), (3, 30)], chain);
        Assert.Equal(3, target.CommittedNodeCount);
    }

    [Fact]
    public void WriteRebase_WithPendingMutations_RoundTripsCurrentState() {
        IntArena source = new();
        var head = SeedCommitted(ref source, (1, 10), (2, 20), (3, 30));

        // Mutate: update value and insert
        var second = source.GetNext(ref head);
        source.SetValue(ref second, 99);
        var third = source.GetNext(ref second);
        LeafHandle h4 = source.AllocNode(4, 40);
        source.SetNext(ref third, h4);

        byte[] payload = WriteRebase(ref source);

        IntArena target = new();
        ApplyAndSync(ref target, payload);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 10), (2, 99), (3, 30), (4, 40)], chain);
        Assert.Equal(4, target.CommittedNodeCount);
    }

    [Fact]
    public void WriteDeltify_ThenCommit_ThenSecondDelta_ChainedRoundTrips() {
        // Verify that after a delta round-trip + commit, further deltas still work correctly.
        IntArena source = new();
        var head = SeedCommitted(ref source, (1, 10), (2, 20));

        // First delta: insert node 3
        LeafHandle h3 = source.AllocNode(3, 30);
        var second = source.GetNext(ref head);
        source.SetNext(ref second, h3);

        byte[] delta1 = WriteDelta(ref source);
        source.Commit();
        head.ClearCachedIndex();

        // Second delta: update node 3's value
        var freshThird = new LeafHandle(h3.Sequence);
        source.SetValue(ref freshThird, 99);

        byte[] delta2 = WriteDelta(ref source);

        // Replay both deltas on a fresh target
        IntArena target = new();
        SeedCommitted(ref target, (1, 10), (2, 20));

        ApplyAndSync(ref target, delta1);
        target.Commit();

        ApplyAndSync(ref target, delta2);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 10), (2, 20), (3, 99)], chain);
    }

    [Fact]
    public void WriteDeltify_MultipleInserts_RoundTrips() {
        // Insert multiple nodes in one delta
        IntArena source = new();
        var head = SeedCommitted(ref source, (1, 10));

        // Insert 2, 3, 4 after node 1
        LeafHandle h2 = source.AllocNode(2, 20);
        source.SetNext(ref head, h2);

        LeafHandle h3 = source.AllocNode(3, 30);
        source.SetNext(ref h2, h3);

        LeafHandle h4 = source.AllocNode(4, 40);
        source.SetNext(ref h3, h4);

        byte[] payload = WriteDelta(ref source);

        IntArena target = new();
        SeedCommitted(ref target, (1, 10));
        ApplyAndSync(ref target, payload);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, 10), (2, 20), (3, 30), (4, 40)], chain);
    }

    [Fact]
    public void WriteDeltify_LinkOnlyDelta_IsSmallerThanRebase() {
        // Key design property: a link-only mutation (e.g., delete-by-relink)
        // produces a delta significantly smaller than a full rebase.
        IntArena arena = new();
        var head = SeedCommitted(ref arena,
            (1, 10), (2, 20), (3, 30), (4, 40), (5, 50),
            (6, 60), (7, 70), (8, 80), (9, 90), (10, 100)
        );

        // Delete node 5 by relinking 4→6
        var n = head;
        for (int i = 0; i < 3; i++) { n = arena.GetNext(ref n); } // n = node 4
        var node5 = arena.GetNext(ref n);
        uint node6Seq = arena.GetNextSequence(ref node5);
        arena.SetNextSequence(ref n, node6Seq);

        byte[] delta = WriteDelta(ref arena);
        byte[] rebase = WriteRebase(ref arena);

        // Delta: 3 count headers + 1 link entry (seq+newNextSeq) ≈ very small
        // Rebase: 3 count headers + 10 full node entries ≈ much larger
        Assert.True(delta.Length < rebase.Length / 2,
            $"Delta ({delta.Length}B) should be much smaller than rebase ({rebase.Length}B)"
        );
    }

    #endregion
}
