using Atelia.Rbf;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void FrozenTypedOrderedDict_RejectsMutations_AndAllowsReads() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int, int>();
        dict.Upsert(1, 10);

        dict.Freeze();

        Assert.True(dict.IsFrozen);
        Assert.Equal(GetIssue.None, dict.Get(1, out int value));
        Assert.Equal(10, value);
        Assert.Throws<ObjectFrozenException>(() => dict.Upsert(2, 20));
        Assert.Throws<ObjectFrozenException>(() => dict.Remove(1));
    }

    [Fact]
    public void FrozenMixedOrderedDict_RejectsAllMutationEntrypoints_AndAllowsReads() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();
        dict.Upsert(1, 10);
        dict.OfSymbol.Upsert(2, "kept");

        dict.Freeze();

        Assert.True(dict.IsFrozen);
        Assert.True(dict.TryGet(1, out int count));
        Assert.Equal(10, count);
        Assert.Equal(GetIssue.None, dict.Get(2, out Symbol label));
        Assert.Equal("kept", label.Value);
        Assert.Throws<ObjectFrozenException>(() => dict.Upsert(3, "next"));
        Assert.Throws<ObjectFrozenException>(() => dict.OfInt32.Upsert(1, 20));
        Assert.Throws<ObjectFrozenException>(() => dict.Remove(1));
    }

    [Fact]
    public void FrozenTypedOrderedDict_TransientFreeze_RoundTripsAsFrozen() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateOrderedDict<int, int>();
        root.Upsert(1, 10);
        root.Upsert(2, 20);
        root.Freeze();

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        Assert.True(root.IsFrozen);
        Assert.Equal(DurableState.Clean, root.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int value));
        Assert.Equal(10, value);
        Assert.Throws<ObjectFrozenException>(() => loaded.Upsert(3, 30));
    }

    [Fact]
    public void FrozenTypedOrderedDict_CleanFreeze_CanBeDiscardedBeforeCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateOrderedDict<int, int>();
        root.Upsert(1, 10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Freeze();
        root.DiscardChanges();

        Assert.False(root.IsFrozen);
        Assert.False(root.HasChanges);
        root.Upsert(2, 20);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(opened.GraphRoot);
        Assert.False(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(2, out int value));
        Assert.Equal(20, value);
    }

    [Fact]
    public void FrozenTypedOrderedDict_CleanFreeze_PersistsWithoutContentChange() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateOrderedDict<int, int>();
        root.Upsert(1, 10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        int framesBefore = CountUserPayloadFrames(file, DurableObjectKind.TypedOrderedDict);

        root.Freeze();
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        int framesAfter = CountUserPayloadFrames(file, DurableObjectKind.TypedOrderedDict);

        Assert.Equal(framesBefore + 1, framesAfter);
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void FrozenTypedOrderedDict_DirtyFreeze_RoundTripsCurrentContent_AndCannotDiscard() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateOrderedDict<int, int>();
        root.Upsert(1, 10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Upsert(1, 20);
        root.Freeze();

        Assert.Throws<InvalidOperationException>(() => root.DiscardChanges());
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int value));
        Assert.Equal(20, value);
    }

    [Fact]
    public void FrozenTypedOrderedDict_DirtyFreeze_CanRetryCommitAfterPersistenceFailure() {
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateOrderedDict<int, int>();
        root.Upsert(1, 10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Upsert(1, 20);
        root.Freeze();
        file.Dispose();

        var failed = CommitToFile(rev, root, file);
        Assert.True(failed.IsFailure);
        Assert.True(root.IsFrozen);

        using var retryFile = RbfFile.OpenExisting(path);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, retryFile), "Commit2Retry");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, retryFile));
        var loaded = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int value));
        Assert.Equal(20, value);
    }

    [Fact]
    public void FrozenMixedOrderedDict_KeepsChildAndSymbolReferencesReachable() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateOrderedDict<int>();
        var child = rev.CreateDict<int, int>();
        child.Upsert(1, 10);
        root.Upsert(1, child);
        root.OfSymbol.Upsert(2, "kept");
        root.Freeze();

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        Assert.NotEqual(DurableState.Detached, child.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableOrderedDict<int>>(opened.GraphRoot);
        Assert.True(loadedRoot.IsFrozen);
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out Symbol label));
        Assert.Equal("kept", label.Value);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableObject? loadedChild));
        var loadedChildDict = Assert.IsAssignableFrom<DurableDict<int, int>>(loadedChild);
        Assert.Equal(GetIssue.None, loadedChildDict.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void FrozenMixedOrderedDict_DirtyFreeze_CanRetryCommitAfterPersistenceFailure() {
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateOrderedDict<int>();
        root.Upsert(1, "alpha");
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Upsert(1, "beta");
        root.Freeze();
        file.Dispose();

        var failed = CommitToFile(rev, root, file);
        Assert.True(failed.IsFailure);
        Assert.True(root.IsFrozen);

        using var retryFile = RbfFile.OpenExisting(path);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, retryFile), "Commit2Retry");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, retryFile));
        var loaded = Assert.IsAssignableFrom<DurableOrderedDict<int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.True(loaded.TryGet<string>(1, out var value));
        Assert.Equal("beta", value);
    }

    [Fact]
    public void FrozenDurObjOrderedDict_RoundTripsAndKeepsChildrenReachable() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateOrderedDict<int, DurableDict<int, int>>();
        var child = rev.CreateDict<int, int>();
        child.Upsert(1, 10);
        root.Upsert(1, child);
        root.Freeze();

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        Assert.NotEqual(DurableState.Detached, child.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableOrderedDict<int, DurableDict<int, int>>>(opened.GraphRoot);
        Assert.True(loadedRoot.IsFrozen);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int, int>? loadedChild));
        Assert.NotNull(loadedChild);
        Assert.Equal(GetIssue.None, loadedChild!.Get(1, out int value));
        Assert.Equal(10, value);
        Assert.Throws<ObjectFrozenException>(() => loadedRoot.Upsert(2, null));
        Assert.Throws<ObjectFrozenException>(() => loadedRoot.Remove(1));
    }
}
