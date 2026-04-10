using System.Buffers;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.NodeContainers;
using Atelia.StateJournal.Serialization;
using Xunit;

using TrackedArena = Atelia.StateJournal.NodeContainers.LeafChainStore<
    int, string,
    Atelia.StateJournal.Internal.Int32Helper,
    Atelia.StateJournal.Tests.NodeContainers.TrackingStringHelper>;

namespace Atelia.StateJournal.Tests.NodeContainers;

/// <summary>
/// Test-only <see cref="ITypeHelper{T}"/> for <see cref="string"/> with <c>NeedRelease = true</c>.
/// Uses <c>BareInlineString</c> for serialization (no Revision context needed).
/// All <see cref="ReleaseSlot"/> calls are recorded in <see cref="LeafChainStoreStringTests.ReleasedValues"/>.
/// </summary>
internal readonly struct TrackingStringHelper : ITypeHelper<string> {
    public static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    public static int Compare(string? a, string? b) => string.Compare(a, b, StringComparison.Ordinal);
    public static void Write(BinaryDiffWriter writer, string? v, bool asKey) => writer.BareInlineString(v, asKey);
    public static string? Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInlineString(asKey);
    public static bool NeedRelease => true;
    public static void ReleaseSlot(string? value) => LeafChainStoreStringTests.ReleasedValues.Add(value);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref string? old) {
        if (old is not null) { ReleaseSlot(old); }
        old = Read(ref reader, asKey: false);
    }
}

/// <summary>
/// 引用类型 value 的 round-trip 及 <c>NeedRelease</c> 路径覆盖测试。
/// 使用 <see cref="TrackingStringHelper"/> 追踪 <c>ReleaseSlot</c> 调用。
/// </summary>
public class LeafChainStoreStringTests : IDisposable {
    internal static readonly List<string?> ReleasedValues = new();

    public LeafChainStoreStringTests() => ReleasedValues.Clear();
    public void Dispose() => ReleasedValues.Clear();

    #region Helpers

    private static LeafHandle BuildLinkedList(
        ref TrackedArena arena, params (int Key, string Value)[] items
    ) {
        LeafHandle head = default;
        LeafHandle prev = default;
        foreach (var (key, value) in items) {
            LeafHandle handle = arena.AllocNode(key, value);
            if (head.IsNull) { head = handle; }
            else { arena.SetNext(ref prev, handle); }
            prev = handle;
        }
        return head;
    }

    private static List<(int Key, string Value)> ReadChain(
        ref TrackedArena arena, LeafHandle head
    ) {
        var result = new List<(int, string)>();
        var current = head;
        int guard = 10000;
        while (current.IsNotNull && --guard > 0) {
            int k = arena.GetKey(ref current);
            string v = arena.GetValue(ref current);
            result.Add((k, v));
            current = arena.GetNext(ref current);
        }
        return result;
    }

    private static LeafHandle SeedCommitted(
        ref TrackedArena arena, params (int Key, string Value)[] items
    ) {
        var head = BuildLinkedList(ref arena, items);
        arena.Commit();
        return head;
    }

    private static byte[] WriteDelta(ref TrackedArena arena) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        arena.WriteDeltify(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteRebase(ref TrackedArena arena) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        arena.WriteRebase(writer, DiffWriteContext.UserPrimary);
        return buffer.WrittenSpan.ToArray();
    }

    private static void ApplyAndSync(ref TrackedArena target, byte[] data) {
        var reader = new BinaryDiffReader(data);
        target.ApplyDelta(ref reader);
        reader.EnsureFullyConsumed();
        target.SyncCurrentFromCommitted();
    }

    private static void AssertReleasedExactlyOnce(string value) {
        Assert.Equal(1, ReleasedValues.Count(x => string.Equals(x, value, StringComparison.Ordinal)));
    }

    #endregion

    #region ReleaseSlot paths

    [Fact]
    public void SetValue_OnCommittedNode_DoesNotReleaseOldValue() {
        // Old committed value is held by CapturedOriginal for potential Revert.
        // It must NOT be released until Commit or Revert decides ownership.
        TrackedArena arena = new();
        var head = SeedCommitted(ref arena, (1, "hello"));
        ReleasedValues.Clear();

        arena.SetValue(ref head, "world");

        Assert.DoesNotContain("hello", ReleasedValues);
    }

    [Fact]
    public void SetValue_Twice_OnCommittedNode_ReleasesIntermediateValue() {
        // On repeat mutation, the intermediate dirty value is orphaned and must be released.
        TrackedArena arena = new();
        var head = SeedCommitted(ref arena, (1, "hello"));
        arena.SetValue(ref head, "intermediate");
        ReleasedValues.Clear();

        arena.SetValue(ref head, "final");

        AssertReleasedExactlyOnce("intermediate");
        // Original committed value is still held by CapturedOriginal
        Assert.DoesNotContain("hello", ReleasedValues);
    }

    [Fact]
    public void Commit_AfterSetValue_ReleasesCapturedOriginal() {
        TrackedArena arena = new();
        var head = SeedCommitted(ref arena, (1, "hello"));

        arena.SetValue(ref head, "world");

        // SetValue should NOT have released "hello" (it's in CapturedOriginal)
        Assert.DoesNotContain("hello", ReleasedValues);
        ReleasedValues.Clear();

        arena.Commit();

        // Commit releases the captured original — it's the last holder of "hello"
        AssertReleasedExactlyOnce("hello");
        // "world" is now the committed value, must NOT be released
        Assert.DoesNotContain("world", ReleasedValues);
    }

    [Fact]
    public void Revert_AfterSetValue_ReleasesDirtyValue_RestoresOriginal() {
        TrackedArena arena = new();
        var head = SeedCommitted(ref arena, (1, "hello"));

        arena.SetValue(ref head, "world");
        ReleasedValues.Clear();

        arena.Revert();

        AssertReleasedExactlyOnce("world");
        Assert.DoesNotContain("hello", ReleasedValues);

        string v = arena.GetValue(ref head);
        Assert.Equal("hello", v);
    }

    [Fact]
    public void Revert_ReleasesDraftNodeValues() {
        TrackedArena arena = new();
        SeedCommitted(ref arena, (1, "committed"));

        arena.AllocNode(2, "draft");
        ReleasedValues.Clear();

        arena.Revert();

        AssertReleasedExactlyOnce("draft");
        Assert.DoesNotContain("committed", ReleasedValues);
    }

    [Fact]
    public void ApplyDelta_ValueMutation_ReleasesOldValue() {
        TrackedArena source = new();
        var head = SeedCommitted(ref source, (1, "hello"));

        source.SetValue(ref head, "world");
        byte[] delta = WriteDelta(ref source);

        TrackedArena target = new();
        SeedCommitted(ref target, (1, "hello"));
        ReleasedValues.Clear();

        ApplyAndSync(ref target, delta);

        AssertReleasedExactlyOnce("hello");

        var targetHead = new LeafHandle(head.Sequence);
        Assert.Equal("world", target.GetValue(ref targetHead));
    }

    [Fact]
    public void CollectAll_ReleasesUnreachableValues() {
        TrackedArena arena = new();
        var head = BuildLinkedList(ref arena, (1, "keep"), (2, "dead"), (3, "also-keep"));

        // Relink in draft state: 1→3, making node 2 unreachable
        var second = arena.GetNext(ref head);
        var third = arena.GetNext(ref second);
        arena.SetNext(ref head, third);
        ReleasedValues.Clear();

        arena.CollectAll(head.Sequence);

        AssertReleasedExactlyOnce("dead");
        Assert.DoesNotContain("keep", ReleasedValues);
        Assert.DoesNotContain("also-keep", ReleasedValues);
    }

    #endregion

    #region Serialization round-trips

    [Fact]
    public void WriteDeltify_ValueMutation_RoundTrips() {
        TrackedArena source = new();
        var head = SeedCommitted(ref source, (1, "alpha"), (2, "beta"), (3, "gamma"));

        var second = source.GetNext(ref head);
        source.SetValue(ref second, "BETA");

        Assert.Equal(1, source.DirtyValueCount);

        byte[] delta = WriteDelta(ref source);

        TrackedArena target = new();
        SeedCommitted(ref target, (1, "alpha"), (2, "beta"), (3, "gamma"));
        ApplyAndSync(ref target, delta);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, "alpha"), (2, "BETA"), (3, "gamma")], chain);
    }

    [Fact]
    public void WriteDeltify_AppendedStringNodes_RoundTrips() {
        TrackedArena source = new();
        var head = SeedCommitted(ref source, (1, "first"));

        LeafHandle h2 = source.AllocNode(2, "second");
        source.SetNext(ref head, h2);
        LeafHandle h3 = source.AllocNode(3, "third");
        source.SetNext(ref h2, h3);

        byte[] delta = WriteDelta(ref source);

        TrackedArena target = new();
        SeedCommitted(ref target, (1, "first"));
        ApplyAndSync(ref target, delta);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, "first"), (2, "second"), (3, "third")], chain);
    }

    [Fact]
    public void WriteDeltify_ValueMutationSection_ContainsOnlySeqAndValue() {
        // Structurally verify the delta format: Section 2 entries are {seq, value} only,
        // with NO key bytes. Manually parse the delta to prove the encoding shape.
        TrackedArena arena = new();
        var head = SeedCommitted(ref arena, (42, "original"));

        arena.SetValue(ref head, "updated");

        byte[] delta = WriteDelta(ref arena);
        var reader = new BinaryDiffReader(delta);

        // Section 1: no link mutations
        Assert.Equal(0, reader.ReadCount());

        // Section 2: one value mutation — only seq + value, no key
        Assert.Equal(1, reader.ReadCount());
        uint seq = reader.BareUInt32(asKey: true);
        Assert.Equal(head.Sequence, seq);
        string value = reader.BareInlineString(asKey: false);
        Assert.Equal("updated", value);

        // Section 3: no appends
        Assert.Equal(0, reader.ReadCount());

        // If key bytes (BareInt32) were present, they would remain as unread data
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void WriteRebase_StringValues_RoundTrips() {
        TrackedArena source = new();
        var head = SeedCommitted(ref source, (1, "alpha"), (2, "beta"));

        byte[] rebase = WriteRebase(ref source);

        TrackedArena target = new();
        ApplyAndSync(ref target, rebase);

        var targetHead = new LeafHandle(head.Sequence);
        var chain = ReadChain(ref target, targetHead);
        Assert.Equal([(1, "alpha"), (2, "beta")], chain);
    }

    #endregion
}
