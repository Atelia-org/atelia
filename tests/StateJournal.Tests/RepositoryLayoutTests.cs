using Xunit;

namespace Atelia.StateJournal.Tests;

public class RepositoryLayoutTests : IDisposable {
    private readonly List<string> _tempDirs = new();

    private string GetTempDir() {
        var dir = Path.Combine(Path.GetTempPath(), $"repo-layout-test-{Guid.NewGuid()}");
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose() {
        foreach (var dir in _tempDirs) {
            try {
                if (Directory.Exists(dir)) {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch {
            }
        }
    }

    [Fact]
    public void Create_TargetDirectoryMissing_Succeeds() {
        var dir = GetTempDir();
        var result = Repository.Create(dir);
        var repo = AssertSuccess(result);
        repo.Dispose();
    }

    [Fact]
    public void Create_TargetDirectoryEmpty_Succeeds() {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);
        var result = Repository.Create(dir);
        var repo = AssertSuccess(result);
        repo.Dispose();
    }

    [Fact]
    public void Create_TargetDirectoryNonEmpty_Fails() {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "payload.txt"), "keep-out");

        var result = Repository.Create(dir);
        Assert.True(result.IsFailure);
        Assert.False(File.Exists(Path.Combine(dir, "state-journal.lock")));
        Assert.True(File.Exists(Path.Combine(dir, "payload.txt")));
    }

    [Fact]
    public void Create_TargetDirectoryContainingLockFile_Fails() {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state-journal.lock"), "not-a-fresh-repo");

        var result = Repository.Create(dir);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Open_AfterCreate_Succeeds() {
        var dir = GetTempDir();
        var repo = AssertSuccess(Repository.Create(dir));
        repo.Dispose();

        var reopened = AssertSuccess(Repository.Open(dir));
        reopened.Dispose();
        Assert.True(File.Exists(Path.Combine(dir, "state-journal.lock")));
    }

    [Fact]
    public void Open_MissingLockFile_Fails() {
        var dir = GetTempDir();
        var repo = AssertSuccess(Repository.Create(dir));
        repo.Dispose();
        File.Delete(Path.Combine(dir, "state-journal.lock"));

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void Open_NonRepositoryDirectory_Fails() {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);

        var result = Repository.Open(dir);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Open_MissingBranchesDirectory_Fails() {
        var dir = GetTempDir();
        var repo = AssertSuccess(Repository.Create(dir));
        repo.Dispose();
        Directory.Delete(Path.Combine(dir, "refs", "branches"), recursive: true);

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void Open_MissingRecentDirectory_Fails() {
        var dir = GetTempDir();
        var repo = AssertSuccess(Repository.Create(dir));
        repo.Dispose();
        Directory.Delete(Path.Combine(dir, "recent"), recursive: true);

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void Open_EmptyRecentDirectory_Fails() {
        var dir = GetTempDir();
        var repo = AssertSuccess(Repository.Create(dir));
        repo.Dispose();

        var recentDir = Path.Combine(dir, "recent");
        foreach (var path in Directory.GetFiles(recentDir, "*.sj.rbf")) {
            File.Delete(path);
        }

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void Open_SegmentNumberGap_Fails() {
        var dir = GetTempDir();
        using (var repo = AssertSuccess(Repository.Create(dir))) {
        }

        var recentDir = Path.Combine(dir, "recent");
        File.Move(
            Path.Combine(recentDir, "00000001.sj.rbf"),
            Path.Combine(recentDir, "00000002.sj.rbf")
        );

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void Open_RecentWindowCanStartAfterOne_WhenOlderSegmentsAreArchived() {
        var dir = GetTempDir();
        using (var repo = AssertSuccess(Repository.Create(dir))) {
            var mainResult = repo.CreateBranch("main");
            Assert.True(mainResult.IsSuccess, $"Expected Revision success but got error: {mainResult.Error}");
            var main = mainResult.Value!;
            repo.SetRotationThreshold(1);

            var root = main.CreateDict<int, int>();
            root.Upsert(1, 10);
            var commit1 = repo.Commit(root);
            Assert.True(commit1.IsSuccess, $"Expected CommitOutcome success but got error: {commit1.Error}");
            root.Upsert(2, 20);
            var commit2 = repo.Commit(root);
            Assert.True(commit2.IsSuccess, $"Expected CommitOutcome success but got error: {commit2.Error}");
        }

        var archivedPath = SegmentPathTestHelper.ArchiveSegmentPath(dir, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(archivedPath)!);
        File.Move(
            SegmentPathTestHelper.RecentSegmentPath(dir, 1),
            archivedPath
        );

        var reopened = AssertSuccess(Repository.Open(dir));
        reopened.Dispose();
    }

    private static Repository AssertSuccess(AteliaResult<Repository> result) {
        Assert.True(result.IsSuccess, $"Expected Repository success but got error: {result.Error}");
        return result.Value!;
    }
}
