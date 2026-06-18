using Xunit;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Tests;

public class RepositoryReplayCommittedTests : IDisposable {
    private readonly List<string> _tempDirs = new();

    private string GetTempDir() {
        var path = Path.Combine(Path.GetTempPath(), $"repo-replay-test-{Guid.NewGuid()}");
        _tempDirs.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var dir in _tempDirs) {
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { }
        }
    }

    [Fact]
    public void ReplayCommitted_ForceMutable_FromFrozenTypedDict_PersistsMutableClone() {
        var dir = GetTempDir();

        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, DurableDict<int, int>>();
            var source = main.CreateDict<int, int>();
            source.Upsert(1, 10);
            source.Freeze();
            root.Upsert(1, source);
            AssertSuccess(repo.Commit(root));

            var clone = AssertSuccess(repo.ReplayCommitted(source, LoadMaterializationMode.ForceMutable));
            Assert.False(clone.IsFrozen);
            Assert.False(clone.HasChanges);
            Assert.Equal(DurableState.Clean, clone.State);

            root.Upsert(2, clone);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var mainAgain = AssertSuccess(reopened.CheckoutBranch("main"));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(mainAgain.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int, int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedClone));
        Assert.True(loadedSource!.IsFrozen);
        Assert.False(loadedClone!.IsFrozen);
        Assert.Equal(GetIssue.None, loadedClone.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void ReplayCommitted_ForceFrozen_FromMutableTypedDict_PersistsFrozenClone() {
        var dir = GetTempDir();

        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, DurableDict<int, int>>();
            var source = main.CreateDict<int, int>();
            source.Upsert(1, 10);
            root.Upsert(1, source);
            AssertSuccess(repo.Commit(root));

            var clone = AssertSuccess(repo.ReplayCommitted(source, LoadMaterializationMode.ForceFrozen));
            Assert.True(clone.IsFrozen);
            Assert.False(clone.HasChanges);
            Assert.Equal(DurableState.Clean, clone.State);

            root.Upsert(2, clone);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var mainAgain = AssertSuccess(reopened.CheckoutBranch("main"));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(mainAgain.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int, int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedClone));
        Assert.False(loadedSource!.IsFrozen);
        Assert.True(loadedClone!.IsFrozen);
        Assert.Equal(GetIssue.None, loadedClone.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void ReplayCommitted_DirtyFrozenTypedDict_CopiesCommittedStateOnly() {
        var dir = GetTempDir();

        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, DurableDict<int, int>>();
            var source = main.CreateDict<int, int>();
            source.Upsert(1, 10);
            root.Upsert(1, source);
            AssertSuccess(repo.Commit(root));

            source.Upsert(1, 20);
            source.Freeze();
            var clone = AssertSuccess(repo.ReplayCommitted(source, LoadMaterializationMode.ForceMutable));
            root.Upsert(2, clone);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var mainAgain = AssertSuccess(reopened.CheckoutBranch("main"));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(mainAgain.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int, int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedClone));
        Assert.True(loadedSource!.IsFrozen);
        Assert.False(loadedClone!.IsFrozen);
        Assert.Equal(GetIssue.None, loadedSource.Get(1, out int sourceValue));
        Assert.Equal(GetIssue.None, loadedClone.Get(1, out int cloneValue));
        Assert.Equal(20, sourceValue);
        Assert.Equal(10, cloneValue);
    }

    [Fact]
    public void ReplayCommitted_TypedOrderedDict_DirtySource_CopiesCommittedStateOnly() {
        var dir = GetTempDir();

        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, DurableOrderedDict<int, int>>();
            var source = main.CreateOrderedDict<int, int>();
            source.Upsert(1, 10);
            root.Upsert(1, source);
            AssertSuccess(repo.Commit(root));

            source.Upsert(1, 20);
            var clone = AssertSuccess(repo.ReplayCommitted(source, LoadMaterializationMode.ForceMutable));
            root.Upsert(2, clone);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var mainAgain = AssertSuccess(reopened.CheckoutBranch("main"));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableOrderedDict<int, int>>>(mainAgain.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableOrderedDict<int, int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableOrderedDict<int, int>? loadedClone));
        Assert.True(loadedSource!.TryGet(1, out var sourceValue));
        Assert.True(loadedClone!.TryGet(1, out var cloneValue));
        Assert.Equal(20, sourceValue);
        Assert.Equal(10, cloneValue);
    }

    [Fact]
    public void ReplayCommitted_MixedOrderedDict_DirtySource_CopiesCommittedStateOnly() {
        var dir = GetTempDir();

        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, DurableOrderedDict<int>>();
            var source = main.CreateOrderedDict<int>();
            source.Upsert(1, "alpha");
            root.Upsert(1, source);
            AssertSuccess(repo.Commit(root));

            source.Upsert(1, "beta");
            var clone = AssertSuccess(repo.ReplayCommitted(source, LoadMaterializationMode.ForceMutable));
            root.Upsert(2, clone);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var mainAgain = AssertSuccess(reopened.CheckoutBranch("main"));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableOrderedDict<int>>>(mainAgain.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableOrderedDict<int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableOrderedDict<int>? loadedClone));
        Assert.True(loadedSource!.TryGet<string>(1, out var sourceValue));
        Assert.True(loadedClone!.TryGet<string>(1, out var cloneValue));
        Assert.Equal("beta", sourceValue);
        Assert.Equal("alpha", cloneValue);
    }

    [Fact]
    public void ReplayCommitted_DurableText_DirtySource_CopiesCommittedBlocksOnly() {
        var dir = GetTempDir();

        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, DurableText>();
            var source = main.CreateText();
            source.Append("first");
            source.Append("second");
            root.Upsert(1, source);
            AssertSuccess(repo.Commit(root));

            source.Append("third");
            var clone = AssertSuccess(repo.ReplayCommitted(source, LoadMaterializationMode.ForceMutable));
            root.Upsert(2, clone);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var mainAgain = AssertSuccess(reopened.CheckoutBranch("main"));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableText>>(mainAgain.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableText? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableText? loadedClone));
        Assert.Equal(["first", "second", "third"], loadedSource!.GetAllBlocks().Select(static block => block.Content).ToArray());
        Assert.Equal(["first", "second"], loadedClone!.GetAllBlocks().Select(static block => block.Content).ToArray());
    }

    [Fact]
    public void ReplayCommitted_DurableText_ForceFrozen_PersistsFrozenClone() {
        var dir = GetTempDir();

        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, DurableText>();
            var source = main.CreateText();
            source.Append("line");
            root.Upsert(1, source);
            AssertSuccess(repo.Commit(root));

            var clone = AssertSuccess(repo.ReplayCommitted(source, LoadMaterializationMode.ForceFrozen));
            Assert.True(clone.IsFrozen);
            Assert.False(clone.HasChanges);
            Assert.Equal(DurableState.Clean, clone.State);
            Assert.Equal(["line"], clone.GetAllBlocks().Select(static block => block.Content).ToArray());
            Assert.Throws<ObjectFrozenException>(() => clone.Append("line2"));

            root.Upsert(2, clone);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var mainAgain = AssertSuccess(reopened.CheckoutBranch("main"));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableText>>(mainAgain.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableText? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableText? loadedClone));
        Assert.False(loadedSource!.IsFrozen);
        Assert.True(loadedClone!.IsFrozen);
        Assert.Equal(["line"], loadedClone.GetAllBlocks().Select(static block => block.Content).ToArray());
    }

    [Fact]
    public void ReplayCommitted_TypedOrderedDict_ForceFrozen_FailsClearly() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, DurableOrderedDict<int, int>>();
        var source = main.CreateOrderedDict<int, int>();
        source.Upsert(1, 10);
        root.Upsert(1, source);
        AssertSuccess(repo.Commit(root));

        var result = repo.ReplayCommitted(source, LoadMaterializationMode.ForceFrozen);
        Assert.True(result.IsFailure);
        Assert.IsType<SjStateError>(result.Error);
        Assert.Contains("Frozen OrderedDict is not supported", result.Error!.Message);
        Assert.Contains("Choose a different LoadMaterializationMode", result.Error!.RecoveryHint);
    }

    [Fact]
    public void ReplayCommitted_MixedOrderedDict_ForceFrozen_FailsClearly() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, DurableOrderedDict<int>>();
        var source = main.CreateOrderedDict<int>();
        source.Upsert(1, "alpha");
        root.Upsert(1, source);
        AssertSuccess(repo.Commit(root));

        var result = repo.ReplayCommitted(source, LoadMaterializationMode.ForceFrozen);
        Assert.True(result.IsFailure);
        Assert.IsType<SjStateError>(result.Error);
        Assert.Contains("Frozen OrderedDict is not supported", result.Error!.Message);
        Assert.Contains("Choose a different LoadMaterializationMode", result.Error!.RecoveryHint);
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error}");
        return result.Value!;
    }

    private static Repository CreateRepositoryWithBranch(string dir, string branchName, out Revision revision) {
        var repo = AssertSuccess(Repository.Create(dir));
        revision = AssertSuccess(repo.CreateBranch(branchName));
        return repo;
    }
}
