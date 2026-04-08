using System.Text.Json;
using Xunit;

namespace Atelia.StateJournal.Tests;

public class RepositoryBranchMetadataTests : IDisposable {
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void UnbornBranchLoadsAfterOpen() {
        var dir = GetTempDir();
        using (var repo = AssertSuccess(Repository.Create(dir))) {
            var feature = AssertSuccess(repo.CreateBranch("feature"));
            Assert.Equal(default, feature.HeadId);
            Assert.Null(feature.GraphRoot);
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedFeature = AssertSuccess(reopened.CheckoutBranch("feature"));
        Assert.Equal(default, reopenedFeature.HeadId);
        Assert.Null(reopenedFeature.GraphRoot);
    }

    [Fact]
    public void CommittedBranchLoadsAfterOpen() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedMain = AssertSuccess(reopened.CheckoutBranch("main"));
        Assert.NotNull(reopenedMain.GraphRoot);
    }

    [Fact]
    public void InvalidBranchVersionFailsOpen() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
        }

        var branchPath = GetBranchPath(dir, "main");
        var (seq, ticket) = ReadBranchData(branchPath);
        WriteBranchData(branchPath, version: 42, segmentNumber: seq, ticket: ticket);

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void ZeroSequenceWithTicketFailsOpen() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
        }

        var branchPath = GetBranchPath(dir, "main");
        var (_, ticket) = ReadBranchData(branchPath);
        WriteBranchData(branchPath, version: 1, segmentNumber: 0, ticket: ticket);

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void SequencePositiveTicketZeroFailsOpen() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
        }

        var branchPath = GetBranchPath(dir, "main");
        var (seq, _) = ReadBranchData(branchPath);
        WriteBranchData(branchPath, version: 1, segmentNumber: seq, ticket: 0);

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void IllegalBranchNameOnDiskFailsOpen() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
        }

        var mainBranchPath = GetBranchPath(dir, "main");
        var illegalPath = Path.Combine(dir, "refs", "branches", "非法.json");
        File.WriteAllText(illegalPath, File.ReadAllText(mainBranchPath));

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void BranchPointingToMissingSegmentFailsOpen() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
        }

        var branchPath = GetBranchPath(dir, "main");
        var (_, ticket) = ReadBranchData(branchPath);
        WriteBranchData(branchPath, version: 1, segmentNumber: 2, ticket: ticket);

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    public void Dispose() {
        foreach (var dir in _tempDirs) {
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { }
        }
    }

    private string GetTempDir() {
        var path = Path.Combine(Path.GetTempPath(), $"repo-branch-meta-{Guid.NewGuid()}");
        _tempDirs.Add(path);
        return path;
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) {
        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error}");
        return result.Value!;
    }

    private static Repository CreateRepositoryWithBranch(string dir, string branchName, out Revision revision) {
        var repo = AssertSuccess(Repository.Create(dir));
        revision = AssertSuccess(repo.CreateBranch(branchName));
        return repo;
    }

    private static string GetBranchPath(string repoDir, string branchName) {
        var relative = branchName.Replace('/', Path.DirectorySeparatorChar) + ".json";
        return Path.Combine(repoDir, "refs", "branches", relative);
    }

    private static (uint SegmentNumber, ulong Ticket) ReadBranchData(string path) {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return (
            SegmentNumber: root.GetProperty("segmentNumber").GetUInt32(),
            Ticket: root.GetProperty("ticket").GetUInt64()
        );
    }

    private static void WriteBranchData(string path, int version, uint segmentNumber, ulong ticket) {
        var data = new {
            version,
            segmentNumber,
            ticket
        };
        var json = JsonSerializer.Serialize(data);
        File.WriteAllText(path, json);
    }
}
