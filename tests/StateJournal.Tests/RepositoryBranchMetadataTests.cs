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
        var backupPath = GetBranchBackupPath(dir, "main");
        var (seq, ticket) = ReadLegacyBranchFields(branchPath);
        WriteLegacyBranchData(branchPath, version: 42, segmentNumber: seq, ticket: ticket);
        WriteLegacyBranchData(backupPath, version: 42, segmentNumber: seq, ticket: ticket);
        File.Delete(GetBranchReflogPath(dir, "main"));

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
        var backupPath = GetBranchBackupPath(dir, "main");
        var (_, ticket) = ReadLegacyBranchFields(branchPath);
        WriteLegacyBranchData(branchPath, version: 1, segmentNumber: 0, ticket: ticket);
        WriteLegacyBranchData(backupPath, version: 1, segmentNumber: 0, ticket: ticket);
        File.Delete(GetBranchReflogPath(dir, "main"));

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
        var backupPath = GetBranchBackupPath(dir, "main");
        var (seq, _) = ReadLegacyBranchFields(branchPath);
        WriteLegacyBranchData(branchPath, version: 1, segmentNumber: seq, ticket: 0);
        WriteLegacyBranchData(backupPath, version: 1, segmentNumber: seq, ticket: 0);
        File.Delete(GetBranchReflogPath(dir, "main"));

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
        var backupPath = GetBranchBackupPath(dir, "main");
        var (_, ticket) = ReadLegacyBranchFields(branchPath);
        WriteLegacyBranchData(branchPath, version: 1, segmentNumber: 2, ticket: ticket);
        WriteLegacyBranchData(backupPath, version: 1, segmentNumber: 2, ticket: ticket);
        File.Delete(GetBranchReflogPath(dir, "main"));

        var reopened = Repository.Open(dir);
        Assert.True(reopened.IsFailure);
    }

    [Fact]
    public void Open_FallsBackToBackupRef_WhenPrimaryIsInvalid() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
            root.Upsert(2, 2);
            AssertSuccess(repo.Commit(root));
        }

        var branchPath = GetBranchPath(dir, "main");
        File.WriteAllText(branchPath, "{ not valid json");
        File.Delete(GetBranchReflogPath(dir, "main"));

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedMain = AssertSuccess(reopened.CheckoutBranch("main"));
        var root2 = Assert.IsAssignableFrom<DurableDict<int, int>>(reopenedMain.GraphRoot);
        Assert.Equal(1, root2.Count);
    }

    [Fact]
    public void Open_FallsBackToBackupRef_WhenPrimaryIsMissing() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
            root.Upsert(2, 2);
            AssertSuccess(repo.Commit(root));
        }

        File.Delete(GetBranchPath(dir, "main"));
        File.Delete(GetBranchReflogPath(dir, "main"));

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedMain = AssertSuccess(reopened.CheckoutBranch("main"));
        var root2 = Assert.IsAssignableFrom<DurableDict<int, int>>(reopenedMain.GraphRoot);
        Assert.Equal(1, root2.Count);
    }

    [Fact]
    public void Open_FallsBackToReflog_WhenPrimaryAndBackupAreInvalid() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
            root.Upsert(2, 2);
            AssertSuccess(repo.Commit(root));
        }

        File.WriteAllText(GetBranchPath(dir, "main"), "{ not valid json");
        File.WriteAllText(GetBranchBackupPath(dir, "main"), "{ not valid json");

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedMain = AssertSuccess(reopened.CheckoutBranch("main"));
        var root2 = Assert.IsAssignableFrom<DurableDict<int, int>>(reopenedMain.GraphRoot);
        Assert.Equal(2, root2.Count);
    }

    [Fact]
    public void Open_FallsBackToReflog_WhenPrimaryAndBackupAreMissing() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
            root.Upsert(2, 2);
            AssertSuccess(repo.Commit(root));
        }

        File.Delete(GetBranchPath(dir, "main"));
        File.Delete(GetBranchBackupPath(dir, "main"));

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedMain = AssertSuccess(reopened.CheckoutBranch("main"));
        var root2 = Assert.IsAssignableFrom<DurableDict<int, int>>(reopenedMain.GraphRoot);
        Assert.Equal(2, root2.Count);
    }

    [Fact]
    public void Open_V1BranchRef_StillLoadsAfterUpgrade() {
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 1);
            AssertSuccess(repo.Commit(root));
        }

        var branchPath = GetBranchPath(dir, "main");
        var (segmentNumber, ticket) = ReadLegacyBranchFields(branchPath);
        WriteLegacyBranchData(branchPath, version: 1, segmentNumber, ticket);
        File.Delete(GetBranchBackupPath(dir, "main"));

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedMain = AssertSuccess(reopened.CheckoutBranch("main"));
        Assert.NotNull(reopenedMain.GraphRoot);
    }

    [Fact]
    public void CommitNoteAndRecentHeads_ArePersistedInV2BranchRef() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 1);
        var firstHead = AssertSuccess(repo.Commit(root));
        root.Upsert(2, 2);
        var secondHead = AssertSuccess(repo.Commit(root, "before risky migration"));

        var branchData = ReadBranchJson(GetBranchPath(dir, "main"));
        Assert.Equal(2, branchData.RootElement.GetProperty("version").GetInt32());
        Assert.True(branchData.RootElement.GetProperty("generation").GetUInt64() >= 2);
        Assert.Equal(secondHead.ToString(), branchData.RootElement.GetProperty("head").GetString());
        Assert.Equal("before risky migration", branchData.RootElement.GetProperty("lastNote").GetString());

        var recentHeads = branchData.RootElement.GetProperty("recentHeads").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(secondHead.ToString(), recentHeads[0]);
        Assert.Contains(firstHead.ToString(), recentHeads);

        var reflogPath = GetBranchReflogPath(dir, "main");
        var lastLine = File.ReadLines(reflogPath).Last(line => !string.IsNullOrWhiteSpace(line));
        using var reflogJson = JsonDocument.Parse(lastLine);
        Assert.Equal("advance", reflogJson.RootElement.GetProperty("operation").GetString());
        Assert.Equal(secondHead.ToString(), reflogJson.RootElement.GetProperty("newHead").GetString());
        Assert.Equal("before risky migration", reflogJson.RootElement.GetProperty("note").GetString());
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

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
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

    private static string GetBranchBackupPath(string repoDir, string branchName) {
        return GetBranchPath(repoDir, branchName) + ".last";
    }

    private static string GetBranchReflogPath(string repoDir, string branchName) {
        var branchPath = GetBranchPath(repoDir, branchName);
        return branchPath[..^".json".Length] + ".reflog.jsonl";
    }

    private static JsonDocument ReadBranchJson(string path) {
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static (uint SegmentNumber, ulong Ticket) ReadLegacyBranchFields(string path) {
        using var document = ReadBranchJson(path);
        var root = document.RootElement;
        if (root.TryGetProperty("head", out var headProperty) && headProperty.ValueKind == JsonValueKind.String) {
            var address = CommitAddress.Parse(headProperty.GetString()!);
            return (address.SegmentNumber, address.CommitTicket.Ticket.Serialize());
        }

        return (
            SegmentNumber: root.GetProperty("segmentNumber").GetUInt32(),
            Ticket: root.GetProperty("ticket").GetUInt64()
        );
    }

    private static void WriteLegacyBranchData(string path, int version, uint segmentNumber, ulong ticket) {
        var data = new {
            version,
            segmentNumber,
            ticket
        };
        var json = JsonSerializer.Serialize(data);
        File.WriteAllText(path, json);
    }
}
