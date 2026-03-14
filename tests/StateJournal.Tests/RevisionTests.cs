using Xunit;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Tests;

public class RevisionTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), $"rbf-commit-test-{Guid.NewGuid()}");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { if (File.Exists(path)) { File.Delete(path); } } catch { }
        }
    }

    [Fact]
    public void NewCommit_HasNullId_And_NullParent() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var commit = new Revision(file);

        Assert.True(commit.Head.IsNull); // 未 Commit 前 Id 为 null
        Assert.True(commit.HeadParent.IsNull);
    }

    [Fact]
    public void Commit_Empty_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Create and commit empty
        var commit = new Revision(file);
        var commitResult = commit.Commit();
        Assert.True(commitResult.IsSuccess, $"Commit failed: {commitResult.Error}");

        CommitId commitId = commitResult.Value;
        Assert.False(commitId.IsNull); // Commit 后 Id 不为 null
        Assert.Equal(commitId, commit.Head); // Id 已更新

        // Open from CommitId
        var openResult = Revision.Open(commitId, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        Assert.Equal(commitId, loaded.Head);
        Assert.True(loaded.HeadParent.IsNull); // root commit
    }

    [Fact]
    public void Commit_WithAllocatedId_ThenOpen_RoundTrips() {
        // TODO: 完善为端到端测试——当前仅验证 AllocateId + Commit + Open 的 round-trip，
        //       未将对象注册到 ObjectMap，因此 Open 后的 ObjectMap 实际为空。
        //       需要实现 Register 工作流后补充 ObjectMap 内容验证。
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var commit = new Revision(file);

        // 手动向 ObjectMap 写入模拟数据
        var dict = Durable.Dict<int, double>();
        dict.Upsert(10, 3.14);
        dict.Upsert(20, 2.718);
        var saveResult = VersionChain.Save(dict, file);
        Assert.True(saveResult.IsSuccess);

        // 在 object map 中手动注册
        var localId = commit.AllocateId();
        Assert.Equal(1u, localId.Value);

        var commitResult = commit.Commit();
        Assert.True(commitResult.IsSuccess, $"Commit failed: {commitResult.Error}");

        // Open via CommitId and verify
        var openResult = Revision.Open(commitResult.Value, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");
    }

    [Fact]
    public void ParentId_DerivedFromFrameParentTicket() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Commit 1 (root)
        var c1 = new Revision(file);
        var commit1 = c1.Commit();
        Assert.True(commit1.IsSuccess, $"Commit1 failed: {commit1.Error}");
        CommitId id1 = commit1.Value;

        // Open commit 1 → ParentId should be null (root)
        var open1 = Revision.Open(id1, file);
        Assert.True(open1.IsSuccess);
        Assert.True(open1.Value!.HeadParent.IsNull, "root commit should have null parent");

        // Commit 2
        var c2 = new Revision(file);
        var commit2 = c2.Commit();
        Assert.True(commit2.IsSuccess, $"Commit2 failed: {commit2.Error}");
        CommitId id2 = commit2.Value;

        // id1 and id2 are distinct, non-null
        Assert.False(id1.IsNull);
        Assert.False(id2.IsNull);
        Assert.NotEqual(id1, id2);

        // Open commit 2 and verify it loads successfully
        var open2 = Revision.Open(id2, file);
        Assert.True(open2.IsSuccess, $"Open commit2 failed: {open2.Error}");
    }

    [Fact]
    public void FindLatestCommitId_EmptyFile_ReturnsError() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var result = Revision.FindLatestCommitId(file);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Load_NullLocalId_ReturnsError() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var commit = new Revision(file);

        var result = commit.Load(LocalId.Null);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Load_NonexistentLocalId_ReturnsError() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var commit = new Revision(file);

        var result = commit.Load(new LocalId(999));
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Consecutive_Commits_UpdateHeadParent() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        Assert.True(rev.Head.IsNull);
        Assert.True(rev.HeadParent.IsNull);

        // Commit 1 (root)
        var r1 = rev.Commit();
        Assert.True(r1.IsSuccess, $"Commit1 failed: {r1.Error}");
        CommitId id1 = r1.Value;
        Assert.False(id1.IsNull);
        Assert.Equal(id1, rev.Head);
        Assert.True(rev.HeadParent.IsNull, "root commit's parent should be null");

        // Commit 2 on the same Revision instance
        var r2 = rev.Commit();
        Assert.True(r2.IsSuccess, $"Commit2 failed: {r2.Error}");
        CommitId id2 = r2.Value;
        Assert.NotEqual(id1, id2);
        Assert.Equal(id2, rev.Head);
        Assert.Equal(id1, rev.HeadParent); // HeadParent should now point to previous Head

        // Commit 3
        var r3 = rev.Commit();
        Assert.True(r3.IsSuccess, $"Commit3 failed: {r3.Error}");
        CommitId id3 = r3.Value;
        Assert.Equal(id3, rev.Head);
        Assert.Equal(id2, rev.HeadParent); // HeadParent tracks the chain

        // Verify persistence: Open commit 3, its parent should be id2
        var open3 = Revision.Open(id3, file);
        Assert.True(open3.IsSuccess, $"Open commit3 failed: {open3.Error}");
        Assert.Equal(id2, open3.Value!.HeadParent);
    }
}
