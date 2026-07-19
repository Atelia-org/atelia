using Atelia.Data;
using Xunit;

namespace Atelia.StateJournal.Tests;

public class RepositoryReadonlyCommitCheckoutTests : IDisposable {
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void LoadRootAtCommit_LoadsNonHeadHistoricalRootWithoutCreatingBranchMetadata() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        var firstHead = AssertSuccess(repo.Commit(root));
        root.Upsert(2, 20);
        AssertSuccess(repo.Commit(root));
        var beforeRefs = SnapshotBranchMetadata(dir);

        var historicalRoot = AssertSuccess(repo.LoadRootAtCommit(firstHead));

        AssertBranchMetadataUnchanged(beforeRefs, dir);
        var historicalDict = Assert.IsAssignableFrom<DurableDict<int, int>>(historicalRoot);
        Assert.Equal(1, historicalDict.Count);
        Assert.Equal(GetIssue.None, historicalDict.Get(1, out int value1));
        Assert.Equal(10, value1);
        Assert.Equal(GetIssue.NotFound, historicalDict.Get(2, out _));
        Assert.Null(historicalRoot.Revision.BranchName);

        var commitResult = repo.Commit(historicalRoot);
        Assert.True(commitResult.IsFailure);
        AssertBranchMetadataUnchanged(beforeRefs, dir);
    }

    [Fact]
    public void LoadRootAtCommit_LoadsAddressEnumeratedByRepositoryHistoryReader() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        var firstHead = AssertSuccess(repo.Commit(root));
        root.Upsert(2, 20);
        AssertSuccess(repo.Commit(root));

        var addresses = RepositoryHistoryReader.EnumerateBranchCommitAddressValues(dir, "main");
        Assert.Contains(firstHead, addresses);

        var historicalRoot = AssertSuccess(repo.LoadRootAtCommit(firstHead));

        var historicalDict = Assert.IsAssignableFrom<DurableDict<int, int>>(historicalRoot);
        Assert.Equal(1, historicalDict.Count);
        Assert.Equal(GetIssue.NotFound, historicalDict.Get(2, out _));
    }

    [Fact]
    public void LoadRootAtCommit_MissingSegment_ReturnsFailureWithoutPollutingMetadata() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        var firstHead = AssertSuccess(repo.Commit(root));
        var missingSegmentAddress = CommitAddress.Create(firstHead.SegmentNumber + 100, firstHead.CommitTicket);
        var beforeRefs = SnapshotBranchMetadata(dir);

        var result = repo.LoadRootAtCommit(missingSegmentAddress);

        Assert.True(result.IsFailure);
        Assert.Contains("Failed to materialize", result.Error!.Message, StringComparison.Ordinal);
        AssertBranchMetadataUnchanged(beforeRefs, dir);
        Assert.True(repo.TryGetBranchHeadAddress("main", out var currentHead));
        Assert.Equal(firstHead, currentHead);
    }

    [Fact]
    public void LoadRootAtCommit_InvalidTicketInExistingSegment_ReturnsFailureWithoutPollutingMetadata() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        var firstHead = AssertSuccess(repo.Commit(root));
        var invalidTicket = new CommitTicket(SizedPtr.Create(firstHead.CommitTicket.Ticket.EndOffsetExclusive + 4096, 4));
        var invalidAddress = CommitAddress.Create(firstHead.SegmentNumber, invalidTicket);
        var beforeRefs = SnapshotBranchMetadata(dir);

        var result = repo.LoadRootAtCommit(invalidAddress);

        Assert.True(result.IsFailure);
        AssertBranchMetadataUnchanged(beforeRefs, dir);
        Assert.True(repo.TryGetBranchHeadAddress("main", out var currentHead));
        Assert.Equal(firstHead, currentHead);
    }

    [Fact]
    public void LoadRootAtCommit_CanReadHistoricalRootFromNonActiveSegment() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
        repo.SetRotationThreshold(1);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        var firstHead = AssertSuccess(repo.Commit(root));
        root.Upsert(2, 20);
        var secondHead = AssertSuccess(repo.Commit(root));
        Assert.NotEqual(firstHead.SegmentNumber, secondHead.SegmentNumber);
        var beforeRefs = SnapshotBranchMetadata(dir);

        var historicalRoot = AssertSuccess(repo.LoadRootAtCommit(firstHead));

        AssertBranchMetadataUnchanged(beforeRefs, dir);
        var historicalDict = Assert.IsAssignableFrom<DurableDict<int, int>>(historicalRoot);
        Assert.Equal(1, historicalDict.Count);
        Assert.Equal(GetIssue.None, historicalDict.Get(1, out int value1));
        Assert.Equal(10, value1);
        Assert.Equal(GetIssue.NotFound, historicalDict.Get(2, out _));
    }

    public void Dispose() {
        foreach (var dir in _tempDirs) {
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { }
        }
    }

    private string GetTempDir() {
        var path = Path.Combine(Path.GetTempPath(), $"repo-readonly-commit-{Guid.NewGuid()}");
        _tempDirs.Add(path);
        return path;
    }

    private static Repository CreateRepositoryWithBranch(string dir, string branchName, out Revision revision) {
        var repo = AssertSuccess(Repository.Create(dir));
        revision = AssertSuccess(repo.CreateBranch(branchName));
        return repo;
    }

    private static IReadOnlyDictionary<string, byte[]> SnapshotBranchMetadata(string repoDir) {
        var branchesDir = Path.Combine(repoDir, "refs", "branches");
        return Directory.EnumerateFiles(branchesDir, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(
            path => Path.GetRelativePath(branchesDir, path).Replace(Path.DirectorySeparatorChar, '/'),
            File.ReadAllBytes,
            StringComparer.Ordinal
        );
    }

    private static void AssertBranchMetadataUnchanged(IReadOnlyDictionary<string, byte[]> expected, string repoDir) {
        var actual = SnapshotBranchMetadata(repoDir);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var key in expected.Keys) { Assert.Equal(expected[key], actual[key]); }
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error}");
        return result.Value!;
    }
}
