using Atelia.Rbf;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void FrozenTypedDeque_RejectsMutations_AndAllowsReads() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque<int>();
        deque.PushBack(10);
        deque.PushBack(20);

        deque.Freeze();

        Assert.True(deque.IsFrozen);
        Assert.Equal(GetIssue.None, deque.PeekFront(out int front));
        Assert.Equal(10, front);
        Assert.Equal(GetIssue.None, deque.GetAt(1, out int back));
        Assert.Equal(20, back);
        Assert.Throws<ObjectFrozenException>(() => deque.PushFront(5));
        Assert.Throws<ObjectFrozenException>(() => deque.TrySetAt(0, 11));
        Assert.Throws<ObjectFrozenException>(() => deque.PopBack(out _));
    }

    [Fact]
    public void FrozenMixedDeque_RejectsAllMutationEntrypoints_AndAllowsReads() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();
        deque.OfSymbol.PushBack("tail");
        deque.PushFront(1);

        deque.Freeze();

        Assert.True(deque.IsFrozen);
        Assert.Equal(GetIssue.None, deque.PeekFront(out int front));
        Assert.Equal(1, front);
        Assert.Equal(GetIssue.None, deque.PeekBack(out Symbol back));
        Assert.Equal("tail", back.Value);
        Assert.Throws<ObjectFrozenException>(() => deque.PushBack(2));
        Assert.Throws<ObjectFrozenException>(() => deque.OfSymbol.TrySetBack("next"));
        Assert.Throws<ObjectFrozenException>(() => deque.TrySetAt<int>(0, 3));
        Assert.Throws<ObjectFrozenException>(() => deque.PopFront<int>(out _));
    }

    [Fact]
    public void FrozenTypedDeque_TransientFreeze_RoundTripsAsFrozen() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque<int>();
        root.PushBack(10);
        root.PushBack(20);
        root.Freeze();

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        Assert.True(root.IsFrozen);
        Assert.Equal(DurableState.Clean, root.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableDeque<int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.PeekFront(out int front));
        Assert.Equal(10, front);
        Assert.Equal(GetIssue.None, loaded.PeekBack(out int back));
        Assert.Equal(20, back);
        Assert.Throws<ObjectFrozenException>(() => loaded.PushBack(30));
    }

    [Fact]
    public void FrozenTypedDeque_CleanFreeze_PersistsWithoutContentChange() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque<int>();
        root.PushBack(10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        int framesBefore = CountUserPayloadFrames(file, DurableObjectKind.TypedDeque);

        root.Freeze();
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        int framesAfter = CountUserPayloadFrames(file, DurableObjectKind.TypedDeque);

        Assert.Equal(framesBefore + 1, framesAfter);
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableDeque<int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.PeekFront(out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void FrozenTypedDeque_CleanFreeze_CanBeDiscardedBeforeCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque<int>();
        root.PushBack(10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Freeze();
        root.DiscardChanges();

        Assert.False(root.IsFrozen);
        Assert.False(root.HasChanges);
        root.PushBack(20);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableDeque<int>>(opened.GraphRoot);
        Assert.False(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.PeekBack(out int value));
        Assert.Equal(20, value);
    }

    [Fact]
    public void FrozenTypedDeque_DirtyFreeze_RoundTripsCurrentContent_AndCannotDiscard() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque<int>();
        root.PushBack(10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        Assert.Equal(GetIssue.None, root.PopFront(out int oldValue));
        Assert.Equal(10, oldValue);
        root.PushBack(20);
        root.Freeze();

        Assert.Throws<InvalidOperationException>(() => root.DiscardChanges());
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableDeque<int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.PeekFront(out int value));
        Assert.Equal(20, value);
    }

    [Fact]
    public void FrozenTypedDeque_DirtyFreeze_CanRetryCommitAfterPersistenceFailure() {
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque<int>();
        root.PushBack(10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        Assert.Equal(GetIssue.None, root.PopFront(out int oldValue));
        Assert.Equal(10, oldValue);
        root.PushBack(20);
        root.Freeze();
        file.Dispose();

        var failed = CommitToFile(rev, root, file);
        Assert.True(failed.IsFailure);
        Assert.True(root.IsFrozen);

        using var retryFile = RbfFile.OpenExisting(path);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, retryFile), "Commit2Retry");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, retryFile));
        var loaded = Assert.IsAssignableFrom<DurableDeque<int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.PeekFront(out int value));
        Assert.Equal(20, value);
    }

    [Fact]
    public void FrozenMixedDeque_KeepsChildAndSymbolReferencesReachable() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();
        var child = rev.CreateDict<int, int>();
        child.Upsert(1, 10);
        root.PushBack(child);
        root.OfSymbol.PushBack("kept");
        root.Freeze();

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        Assert.NotEqual(DurableState.Detached, child.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDeque>(opened.GraphRoot);
        Assert.True(loadedRoot.IsFrozen);
        Assert.Equal(GetIssue.None, loadedRoot.GetAt<DurableDict<int, int>>(0, out var loadedChild));
        Assert.Equal(GetIssue.None, loadedRoot.GetAt<Symbol>(1, out var label));
        Assert.Equal("kept", label.Value);
        Assert.NotNull(loadedChild);
        Assert.Equal(GetIssue.None, loadedChild!.Get(1, out int value));
        Assert.Equal(10, value);
    }
}
