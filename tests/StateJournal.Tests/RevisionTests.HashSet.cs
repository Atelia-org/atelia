using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

public partial class RevisionTests {
    [Fact]
    public void TypedHashSet_IntAsRoot_RoundTripsCommitLoadAndDiscard() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateHashSet<int>();
        Assert.True(root.Add(1));
        Assert.True(root.Add(2));
        Assert.False(root.Add(2));

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        Assert.True(root.Remove(1));
        Assert.True(root.Add(3));
        Assert.True(root.HasChanges);

        root.DiscardChanges();

        Assert.False(root.HasChanges);
        Assert.Equal(2, root.Count);
        Assert.True(root.Contains(1));
        Assert.True(root.Contains(2));
        Assert.False(root.Contains(3));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableHashSet<int>>(opened.GraphRoot);

        AssertSetEquivalent(loaded, 1, 2);
    }

    [Fact]
    public void TypedHashSet_SymbolAsRoot_RoundTripsCommitLoad() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateHashSet<Symbol>();
        Assert.True(root.Add(new Symbol("alpha")));
        Assert.True(root.Add(new Symbol("beta")));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableHashSet<Symbol>>(opened.GraphRoot);

        AssertSetEquivalent(loaded, new Symbol("alpha"), new Symbol("beta"));
    }

    [Fact]
    public void FrozenTypedHashSet_CleanFreeze_CanBeDiscardedBeforeCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateHashSet<int>();
        root.Add(1);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        int framesBefore = CountUserPayloadFrames(file, DurableObjectKind.TypedHashSet);

        root.Freeze();
        root.DiscardChanges();

        Assert.False(root.IsFrozen);
        Assert.False(root.HasChanges);

        root.Add(2);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        int framesAfter = CountUserPayloadFrames(file, DurableObjectKind.TypedHashSet);

        Assert.Equal(framesBefore + 1, framesAfter);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableHashSet<int>>(opened.GraphRoot);
        Assert.False(loaded.IsFrozen);
        AssertSetEquivalent(loaded, 1, 2);
    }

    [Fact]
    public void TypedHashSet_ForkCommittedAsMutable_RoundTripsWhenNestedInTypedDict() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableHashSet<int>>();
        var source = rev.CreateHashSet<int>();
        source.Add(1);
        source.Add(2);
        source.Freeze();
        root.Upsert(1, source);

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var fork = source.ForkCommittedAsMutable();
        fork.Add(3);
        root.Upsert(2, fork);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableHashSet<int>>>(opened.GraphRoot);

        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableHashSet<int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableHashSet<int>? loadedFork));
        Assert.NotNull(loadedSource);
        Assert.NotNull(loadedFork);
        Assert.True(loadedSource!.IsFrozen);
        Assert.False(loadedFork!.IsFrozen);
        AssertSetEquivalent(loadedSource, 1, 2);
        AssertSetEquivalent(loadedFork, 1, 2, 3);
    }

    [Fact]
    public void CreateHashSet_WithDurableObjectElement_ThrowsArgumentException() {
        var rev = CreateRevision();
        var ex = Assert.Throws<ArgumentException>(() => rev.CreateHashSet<DurableText>());
        Assert.Contains("Unsupported hash set key type", ex.Message);
        Assert.Contains("DurableText", ex.Message);
    }

    [Fact]
    public void DurableHashSetFactory_WithDurableObjectElement_ThrowsArgumentException() {
        var ex = Assert.Throws<ArgumentException>(() => Durable.HashSet<DurableText>());
        Assert.Contains("Unsupported hash set key type", ex.Message);
        Assert.Contains("DurableText", ex.Message);
    }

    [Fact]
    public void CreateHashSet_WithTupleKey_ReturnsInstance() {
        var rev = CreateRevision();
        var set = rev.CreateHashSet<(int, int)>();
        Assert.NotNull(set);
        Assert.IsAssignableFrom<DurableHashSet<(int, int)>>(set);
    }

    [Fact]
    public void DurableHashSetFactory_WithTupleKey_ReturnsInstance() {
        var set = Durable.HashSet<(int, int)>();
        Assert.NotNull(set);
        Assert.IsAssignableFrom<DurableHashSet<(int, int)>>(set);
    }

    [Fact]
    public void TypedHashSet_ValueTupleKey_RoundTripsCommitLoad() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateHashSet<(int, int)>();
        Assert.True(root.Add((1, 2)));
        Assert.True(root.Add((3, 4)));
        Assert.False(root.Add((1, 2)));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableHashSet<(int, int)>>(opened.GraphRoot);

        AssertSetEquivalent(loaded, (1, 2), (3, 4));
    }

    [Fact]
    public void TypedHashSet_String_RejectsNullElements() {
        var rev = CreateRevision();
        var set = rev.CreateHashSet<string>();

        Assert.Throws<ArgumentNullException>(() => set.Add(null!));
        Assert.Throws<ArgumentNullException>(() => set.Contains(null!));
        Assert.Throws<ArgumentNullException>(() => set.Remove(null!));
    }

    [Fact]
    public void TypedHashSet_Items_IsSnapshot() {
        var rev = CreateRevision();
        var set = rev.CreateHashSet<int>();
        set.Add(1);
        set.Add(2);

        var snapshot = set.Items;
        set.Add(3);

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(3, set.Count);
    }

    [Fact]
    public void TypedHashSet_DoubleBitEquality_RoundTripsCommitLoad() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateHashSet<double>();
        double posZero = 0.0;
        double negZero = BitConverter.Int64BitsToDouble(unchecked((long)0x8000_0000_0000_0000ul));
        double nan1 = BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8_0000_0000_0001ul));
        double nan2 = BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8_0000_0000_0002ul));

        Assert.True(root.Add(posZero));
        Assert.True(root.Add(negZero));
        Assert.True(root.Add(nan1));
        Assert.True(root.Add(nan2));
        Assert.False(root.Add(posZero));
        Assert.False(root.Add(nan1));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableHashSet<double>>(opened.GraphRoot);

        Assert.Equal(4, loaded.Count);
        Assert.True(loaded.Contains(posZero));
        Assert.True(loaded.Contains(negZero));
        Assert.True(loaded.Contains(nan1));
        Assert.True(loaded.Contains(nan2));
    }

    [Fact]
    public void TypedHashSet_ApplyDeltaThenDiscard_DoesNotPolluteCommittedState() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateHashSet<int>();
        root.Add(1);
        root.Add(2);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Remove(1);
        root.Add(3);
        var commit2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(commit2.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableHashSet<int>>(opened.GraphRoot);
        AssertSetEquivalent(loaded, 2, 3);

        loaded.Remove(3);
        loaded.Add(4);
        loaded.DiscardChanges();

        Assert.False(loaded.HasChanges);
        AssertSetEquivalent(loaded, 2, 3);
    }

    [Fact]
    public void TypedHashSet_AddOnlyDelta_SecondCommitLoadAndDiscard_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateHashSet<int>();
        root.Add(1);
        root.Add(2);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Add(3);
        var commit2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(commit2.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableHashSet<int>>(opened.GraphRoot);
        AssertSetEquivalent(loaded, 1, 2, 3);

        loaded.Remove(3);
        loaded.Add(4);
        loaded.DiscardChanges();

        Assert.False(loaded.HasChanges);
        AssertSetEquivalent(loaded, 1, 2, 3);
    }

    private static void AssertSetEquivalent<T>(DurableHashSet<T> set, params T[] expected) where T : notnull {
        Assert.Equal(expected.Length, set.Count);
        foreach (T item in expected) {
            Assert.True(set.Contains(item), $"Expected set to contain '{item}'.");
        }
        Assert.Equal(expected.Length, set.Items.Count);
    }
}
