using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void FrozenTypedDict_RejectsMutations_AndAllowsReads() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int, int>();
        dict.Upsert(1, 10);

        dict.Freeze();

        Assert.True(dict.IsFrozen);
        Assert.Equal(GetIssue.None, dict.Get(1, out int value));
        Assert.Equal(10, value);
        Assert.Throws<ObjectFrozenException>(() => dict.Upsert(2, 20));
        Assert.Throws<ObjectFrozenException>(() => dict.Remove(1));
    }

    [Fact]
    public void FrozenMixedDict_RejectsAllMutationEntrypoints_AndAllowsReads() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<string>();
        dict.Upsert("count", 1);
        dict.UpsertExactDouble("pi", 3.14);

        dict.Freeze();

        Assert.True(dict.IsFrozen);
        Assert.True(dict.TryGet("count", out int count));
        Assert.Equal(1, count);
        Assert.Throws<ObjectFrozenException>(() => dict.Upsert("next", 2));
        Assert.Throws<ObjectFrozenException>(() => dict.UpsertExactDouble("tau", 6.28));
        Assert.Throws<ObjectFrozenException>(() => dict.OfInt32.Upsert("count", 3));
        Assert.Throws<ObjectFrozenException>(() => dict.Remove("count"));
    }

    [Fact]
    public void FrozenTypedDict_TransientFreeze_RoundTripsAsFrozen() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 10);
        root.Freeze();

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        Assert.True(root.IsFrozen);
        Assert.Equal(DurableState.Clean, root.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int value));
        Assert.Equal(10, value);
        Assert.Throws<ObjectFrozenException>(() => loaded.Upsert(2, 20));
    }

    [Fact]
    public void FrozenTypedDict_CleanFreeze_PersistsWithoutContentChange() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        int framesBefore = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);

        root.Freeze();
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        int framesAfter = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);

        Assert.Equal(framesBefore + 1, framesAfter);
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void FrozenTypedDict_CleanFreeze_CanBeDiscardedBeforeCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Freeze();
        root.DiscardChanges();

        Assert.False(root.IsFrozen);
        Assert.False(root.HasChanges);
        root.Upsert(2, 20);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(opened.GraphRoot);
        Assert.False(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(2, out int value));
        Assert.Equal(20, value);
    }

    [Fact]
    public void FrozenTypedDict_DirtyFreeze_RoundTripsCurrentContent_AndCannotDiscard() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 10);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Upsert(1, 20);
        root.Freeze();

        Assert.Throws<InvalidOperationException>(() => root.DiscardChanges());
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int value));
        Assert.Equal(20, value);
    }

    [Fact]
    public void FrozenTypedDict_DirtyFreeze_CanRetryCommitAfterPersistenceFailure() {
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
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
        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int value));
        Assert.Equal(20, value);
    }

    [Fact]
    public void FrozenMixedDict_KeepsChildAndSymbolReferencesReachable() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<string>();
        var child = rev.CreateDict<int, int>();
        child.Upsert(1, 10);
        root.Upsert("child", child);
        root.Upsert("label", "kept");
        root.Freeze();

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        Assert.NotEqual(DurableState.Detached, child.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<string>>(opened.GraphRoot);
        Assert.True(loadedRoot.IsFrozen);
        Assert.Equal(GetIssue.None, loadedRoot.Get("label", out string? label));
        Assert.Equal("kept", label);
        Assert.Equal(GetIssue.None, loadedRoot.Get("child", out DurableObject? loadedChild));
        var loadedChildDict = Assert.IsAssignableFrom<DurableDict<int, int>>(loadedChild);
        Assert.Equal(GetIssue.None, loadedChildDict.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void ForkCommittedAsMutable_FromFrozenSource_WritesMutableFlagsEvenWithoutContentChanges() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        source.Freeze();
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        int typedPayloadFramesBefore = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);

        var fork = source.ForkCommittedAsMutable();
        root.Upsert(2, fork);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        int typedPayloadFramesAfter = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);

        Assert.Equal(2, typedPayloadFramesAfter - typedPayloadFramesBefore);
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int, int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedFork));
        Assert.True(loadedSource!.IsFrozen);
        Assert.False(loadedFork!.IsFrozen);
        Assert.Equal(GetIssue.None, loadedFork.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void ForkCommittedAsMutable_FreezeBackToInheritedFrozenFlags_RegistersWithoutPayloadFrame() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        source.Freeze();
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        int typedPayloadFramesBefore = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);

        var fork = source.ForkCommittedAsMutable();
        fork.Freeze();
        root.Upsert(2, fork);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        int typedPayloadFramesAfter = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);

        Assert.Equal(1, typedPayloadFramesAfter - typedPayloadFramesBefore);
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedFork));
        Assert.True(loadedFork!.IsFrozen);
        Assert.Equal(GetIssue.None, loadedFork.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void ForkCommittedAsMutable_FromUncommittedCleanFrozenSource_UsesCommittedContent() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        source.Freeze();
        var fork = source.ForkCommittedAsMutable();
        root.Upsert(2, fork);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int, int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedFork));
        Assert.True(loadedSource!.IsFrozen);
        Assert.False(loadedFork!.IsFrozen);
        Assert.Equal(GetIssue.None, loadedFork.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void ForkCommittedAsMutable_FromUncommittedDirtyFrozenSource_Throws() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        _ = AssertCommitSucceeded(CommitToFile(rev, source, file), "Commit1");

        source.Upsert(1, 20);
        source.Freeze();

        Assert.Throws<InvalidOperationException>(() => source.ForkCommittedAsMutable());
    }

    [Fact]
    public void FrozenMixedDict_ForkMutable_ClonesHeapValueOwnership() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int>>();
        var source = rev.CreateDict<int>();
        source.Upsert(1, ulong.MaxValue);
        source.Freeze();
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var fork = source.ForkCommittedAsMutable();
        fork.Upsert(1, 42UL);
        root.Upsert(2, fork);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int>>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int>? loadedFork));
        Assert.True(loadedSource!.IsFrozen);
        Assert.False(loadedFork!.IsFrozen);
        Assert.Equal(GetIssue.None, loadedSource.Get<ulong>(1, out ulong sourceValue));
        Assert.Equal(GetIssue.None, loadedFork.Get<ulong>(1, out ulong forkValue));
        Assert.Equal(ulong.MaxValue, sourceValue);
        Assert.Equal(42UL, forkValue);
    }

    [Fact]
    public void Freeze_OnStillUnsupportedDurableContainer_ThrowsNotSupported() {
        var rev = CreateRevision();
        var ordered = rev.CreateOrderedDict<int, int>();
        var text = rev.CreateText();

        Assert.Throws<NotSupportedException>(() => ordered.Freeze());
        Assert.Throws<NotSupportedException>(() => text.Freeze());
    }
}
