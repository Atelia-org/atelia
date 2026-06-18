using Atelia.Rbf;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void FrozenText_RejectsAllMutations_AndAllowsReads() {
        var rev = CreateRevision();
        var text = rev.CreateText();
        var first = text.Append("alpha");
        var second = text.Append("beta");

        text.Freeze();

        Assert.True(text.IsFrozen);
        Assert.Equal(new TextBlock(first, "alpha"), text.GetBlock(first));
        Assert.Equal(["alpha", "beta"], text.GetAllBlocks().Select(static block => block.Content).ToArray());
        Assert.Equal(["alpha", "beta"], text.GetBlocksFrom(first, 2).Select(static block => block.Content).ToArray());
        Assert.Throws<ObjectFrozenException>(() => text.Prepend("start"));
        Assert.Throws<ObjectFrozenException>(() => text.Append("end"));
        Assert.Throws<ObjectFrozenException>(() => text.InsertAfter(first, "middle"));
        Assert.Throws<ObjectFrozenException>(() => text.InsertBefore(first, "before"));
        Assert.Throws<ObjectFrozenException>(() => text.SetContent(first, "changed"));
        Assert.Throws<ObjectFrozenException>(() => text.SplitBlock(first, 2));
        Assert.Throws<ObjectFrozenException>(() => text.MergeWithNext(first));
        Assert.Throws<ObjectFrozenException>(() => text.Delete(second));
        Assert.Throws<ObjectFrozenException>(() => text.LoadBlocks(["reset"]));
        Assert.Throws<ObjectFrozenException>(() => text.LoadText("reset"));
    }

    [Fact]
    public void FrozenText_TransientFreeze_RoundTripsAsFrozen() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateText();
        root.LoadBlocks(["line1", "line2"]);
        root.Freeze();

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit");
        Assert.True(root.IsFrozen);
        Assert.Equal(DurableState.Clean, root.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableText>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(["line1", "line2"], loaded.GetAllBlocks().Select(static block => block.Content).ToArray());
        Assert.Throws<ObjectFrozenException>(() => loaded.Append("line3"));
    }

    [Fact]
    public void FrozenText_CleanFreeze_PersistsWithoutContentChange() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateText();
        root.Append("line1");
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        int framesBefore = CountUserPayloadFrames(file, DurableObjectKind.Text);

        root.Freeze();
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        int framesAfter = CountUserPayloadFrames(file, DurableObjectKind.Text);

        Assert.Equal(framesBefore + 1, framesAfter);
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableText>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(["line1"], loaded.GetAllBlocks().Select(static block => block.Content).ToArray());
    }

    [Fact]
    public void FrozenText_CleanFreeze_CanBeDiscardedBeforeCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateText();
        root.Append("line1");
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Freeze();
        root.DiscardChanges();

        Assert.False(root.IsFrozen);
        Assert.False(root.HasChanges);
        root.Append("line2");
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableText>(opened.GraphRoot);
        Assert.False(loaded.IsFrozen);
        Assert.Equal(["line1", "line2"], loaded.GetAllBlocks().Select(static block => block.Content).ToArray());
    }

    [Fact]
    public void FrozenText_DirtyFreeze_RoundTripsCurrentContent_AndCannotDiscard() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateText();
        var first = root.Append("line1");
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.SetContent(first, "line1-updated");
        root.Append("line2");
        root.Freeze();

        Assert.Throws<InvalidOperationException>(() => root.DiscardChanges());
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loaded = Assert.IsAssignableFrom<DurableText>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(["line1-updated", "line2"], loaded.GetAllBlocks().Select(static block => block.Content).ToArray());
    }

    [Fact]
    public void FrozenText_DirtyFreeze_CanRetryCommitAfterPersistenceFailure() {
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateText();
        root.Append("line1");
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Append("line2");
        root.Freeze();
        file.Dispose();

        var failed = CommitToFile(rev, root, file);
        Assert.True(failed.IsFailure);
        Assert.True(root.IsFrozen);

        using var retryFile = RbfFile.OpenExisting(path);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, retryFile), "Commit2Retry");

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, retryFile));
        var loaded = Assert.IsAssignableFrom<DurableText>(opened.GraphRoot);
        Assert.True(loaded.IsFrozen);
        Assert.Equal(["line1", "line2"], loaded.GetAllBlocks().Select(static block => block.Content).ToArray());
    }
}
