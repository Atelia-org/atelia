using Xunit;

namespace Atelia.StateJournal.Tests;

public class RepositoryHistoryReaderTests : IDisposable {
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void EnumerateBranchCommitAddresses_ReturnsReflogHeadsInBranchEvolutionOrder() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        var first = AssertSuccess(repo.Commit(root));
        root.Upsert(2, 20);
        var second = AssertSuccess(repo.Commit(root));
        root.Upsert(3, 30);
        var third = AssertSuccess(repo.Commit(root));

        var result = RepositoryHistoryReader.EnumerateBranchCommitAddresses(dir, "main");

        Assert.Empty(result.Warnings);
        Assert.Equal([first, second, third], result.Addresses.Select(x => x.Address).ToArray());
        Assert.All(result.Addresses, x => Assert.Equal(BranchHistoryAddressSource.ReflogNewHead, x.Source));
        Assert.All(result.Addresses, x => Assert.NotNull(x.Generation));
        Assert.All(result.Addresses, x => Assert.NotNull(x.LineNumber));
    }

    [Fact]
    public void EnumerateBranchCommitAddresses_UsesRecentHeadsWhenReflogIsMissing() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        var first = AssertSuccess(repo.Commit(root));
        root.Upsert(2, 20);
        var second = AssertSuccess(repo.Commit(root));
        File.Delete(GetBranchReflogPath(dir, "main"));

        var result = RepositoryHistoryReader.EnumerateBranchCommitAddresses(dir, "main");

        Assert.Empty(result.Warnings);
        Assert.Equal([second, first], result.Addresses.Select(x => x.Address).ToArray());
        Assert.Equal(BranchHistoryAddressSource.BranchHead, result.Addresses[0].Source);
        Assert.Equal(BranchHistoryAddressSource.BranchRecentHead, result.Addresses[1].Source);
    }

    [Fact]
    public void EnumerateBranchCommitAddresses_SkipsMalformedReflogLinesWithWarning() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        var first = AssertSuccess(repo.Commit(root));
        root.Upsert(2, 20);
        var second = AssertSuccess(repo.Commit(root));
        File.AppendAllText(GetBranchReflogPath(dir, "main"), "{ not valid json\n");

        var result = RepositoryHistoryReader.EnumerateBranchCommitAddresses(dir, "main");

        Assert.Contains(first, result.Addresses.Select(x => x.Address));
        Assert.Contains(second, result.Addresses.Select(x => x.Address));
        Assert.Contains(result.Warnings, x => x.Contains("malformed reflog line", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumeratedAddress_CanLoadHistoricalRootAfterSegmentRotation() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            repo.SetRotationThreshold(1);
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 10);
            AssertSuccess(repo.Commit(root));
            root.Upsert(2, 20);
            AssertSuccess(repo.Commit(root));
        }

        var addresses = RepositoryHistoryReader.EnumerateBranchCommitAddressValues(dir, "main");
        Assert.True(addresses.Count >= 2);
        Assert.NotEqual(addresses[0].SegmentNumber, addresses[^1].SegmentNumber);

        using var reopened = AssertSuccess(Repository.Open(dir));
        var replay = AssertSuccess(reopened.CreateBranch("replay", addresses[0]));
        var replayRoot = Assert.IsAssignableFrom<DurableDict<int, int>>(replay.GraphRoot);
        Assert.Equal(GetIssue.None, replayRoot.Get(1, out int value1));
        Assert.Equal(10, value1);
        Assert.Equal(GetIssue.NotFound, replayRoot.Get(2, out _));
    }

    public void Dispose() {
        foreach (var dir in _tempDirs) {
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { }
        }
    }

    private string GetTempDir() {
        var path = Path.Combine(Path.GetTempPath(), $"repo-history-reader-{Guid.NewGuid()}");
        _tempDirs.Add(path);
        return path;
    }

    private static Repository CreateRepositoryWithBranch(string dir, string branchName, out Revision revision) {
        var repo = AssertSuccess(Repository.Create(dir));
        revision = AssertSuccess(repo.CreateBranch(branchName));
        return repo;
    }

    private static string GetBranchReflogPath(string repoDir, string branchName) {
        var branchPath = Path.Combine(repoDir, "refs", "branches", branchName + ".json");
        return branchPath[..^".json".Length] + ".reflog.jsonl";
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error}");
        return result.Value!;
    }
}
