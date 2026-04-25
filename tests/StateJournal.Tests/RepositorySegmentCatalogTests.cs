using Xunit;

namespace Atelia.StateJournal.Tests;

public class RepositorySegmentCatalogTests : IDisposable {
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void MultiSegmentHistoryCheckout() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            repo.SetRotationThreshold(1);
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
            root.Upsert(2, 2);
            AssertSuccess(repo.Commit(root));
            root.Upsert(3, 3);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedMain = AssertSuccess(reopened.CheckoutBranch("main"));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, int>>(reopenedMain.GraphRoot);
        Assert.Equal(3, loadedRoot.Count);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out int v1));
        Assert.Equal(1, v1);
        Assert.Equal(GetIssue.None, loadedRoot.Get(3, out int v3));
        Assert.Equal(3, v3);

        var segFiles = Directory.GetFiles(Path.Combine(dir, "recent"), "*.sj.rbf");
        Assert.True(segFiles.Length >= 2, "Expected multiple segments after rotation.");
    }

    [Fact]
    public void OlderSegmentBranchRemainsReadableAfterRotation() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
        repo.SetRotationThreshold(1);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 1);
        AssertSuccess(repo.Commit(root));

        var feature = AssertSuccess(repo.CreateBranch("feature", "main"));
        var featureRoot = Assert.IsAssignableFrom<DurableDict<int, int>>(feature.GraphRoot);
        Assert.Equal(1, featureRoot.Count);

        root.Upsert(2, 2);
        AssertSuccess(repo.Commit(root));
        repo.Dispose();

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedFeature = AssertSuccess(reopened.CheckoutBranch("feature"));
        var reopenedRoot = Assert.IsAssignableFrom<DurableDict<int, int>>(reopenedFeature.GraphRoot);
        Assert.Equal(1, reopenedRoot.Count);
        Assert.Equal(GetIssue.None, reopenedRoot.Get(1, out int v));
        Assert.Equal(1, v);
    }

    [Fact]
    public void OlderSegmentPhysicalCorruption_ReturnsFailureOnCheckoutInsteadOfThrowing() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            repo.SetRotationThreshold(1);

            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));

            _ = AssertSuccess(repo.CreateBranch("feature", "main"));

            root.Upsert(2, 2);
            AssertSuccess(repo.Commit(root));
        }

        var oldSegmentPath = Path.Combine(dir, "recent", "00000001.sj.rbf");
        using (var stream = new FileStream(oldSegmentPath, FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.Position = 0;
            stream.Write([0x00, 0x00, 0x00, 0x00]);
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        AteliaResult<Revision> result;
        try {
            result = reopened.CheckoutBranch("feature");
        }
        catch (Exception ex) {
            throw new Xunit.Sdk.XunitException($"CheckoutBranch should return failure instead of throwing, but threw: {ex}");
        }

        Assert.True(result.IsFailure);
        Assert.Contains("Failed to materialize Revision", result.Error!.Message);
    }

    [Fact]
    public void ArchivedOlderSegmentBranchRemainsReadableAfterReopen() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            repo.SetRotationThreshold(1);

            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));

            _ = AssertSuccess(repo.CreateBranch("feature", "main"));

            root.Upsert(2, 2);
            AssertSuccess(repo.Commit(root));
        }

        var archivedPath = SegmentPathTestHelper.ArchiveSegmentPath(dir, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(archivedPath)!);
        File.Move(
            SegmentPathTestHelper.RecentSegmentPath(dir, 1),
            archivedPath
        );

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedFeature = AssertSuccess(reopened.CheckoutBranch("feature"));
        var reopenedRoot = Assert.IsAssignableFrom<DurableDict<int, int>>(reopenedFeature.GraphRoot);
        Assert.Equal(1, reopenedRoot.Count);
        Assert.Equal(GetIssue.None, reopenedRoot.Get(1, out int v));
        Assert.Equal(1, v);
    }

    public void Dispose() {
        foreach (var dir in _tempDirs) {
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { }
        }
    }

    private string GetTempDir() {
        var path = Path.Combine(Path.GetTempPath(), $"repo-segcat-{Guid.NewGuid()}");
        _tempDirs.Add(path);
        return path;
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
