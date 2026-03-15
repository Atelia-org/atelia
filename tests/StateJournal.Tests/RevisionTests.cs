using System.Buffers.Binary;
using System.Reflection;
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

        // Create and commit with a minimal root
        var commit = new Revision(file);
        var root = commit.CreateDict<int, int>();
        var commitResult = commit.Commit(root);
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
    public void Commit_WithCreatedObject_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);

        // 通过 Revision 工厂创建对象（自动绑定 Revision + 分配 LocalId）
        var dict = rev.CreateDict<int, double>();
        Assert.Equal(rev, dict.Revision);
        Assert.False(dict.LocalId.IsNull);
        Assert.Equal(1u, dict.LocalId.Value);
        Assert.Equal(DurableState.TransientDirty, dict.State);

        dict.Upsert(10, 3.14);
        dict.Upsert(20, 2.718);

        var commitResult = rev.Commit(dict);
        Assert.True(commitResult.IsSuccess, $"Commit failed: {commitResult.Error}");

        // Open via CommitId and verify
        var openResult = Revision.Open(commitResult.Value, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        // Load the object back and verify contents
        var loaded = openResult.Value!;
        Assert.NotNull(loaded.GraphRoot); // GraphRoot 自动从 TailMeta 恢复
        Assert.Equal(dict.LocalId, loaded.GraphRoot!.LocalId); // 应指向同一 LocalId
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableDict<int, double>>(loadResult.Value);
        Assert.Equal(2, loadedDict.Count);
        Assert.Equal(GetIssue.None, loadedDict.Get(10, out double v1));
        Assert.Equal(3.14, v1);
        Assert.Equal(GetIssue.None, loadedDict.Get(20, out double v2));
        Assert.Equal(2.718, v2);
    }

    [Fact]
    public void Commit_ModifyAfterFirstCommit_ThenSecondCommit_PersistsLatestValue() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var dict = rev.CreateDict<int, double>();
        dict.Upsert(1, 1.0);

        var c1 = rev.Commit(dict);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");

        dict.Upsert(1, 2.0);
        var c2 = rev.Commit(dict);
        Assert.True(c2.IsSuccess, $"Commit2 failed: {c2.Error}");

        var open = Revision.Open(c2.Value, file);
        Assert.True(open.IsSuccess, $"Open failed: {open.Error}");

        var loaded = open.Value!;
        var load = loaded.Load(dict.LocalId);
        Assert.True(load.IsSuccess, $"Load failed: {load.Error}");

        var loadedDict = Assert.IsAssignableFrom<DurableDict<int, double>>(load.Value);
        Assert.Equal(GetIssue.None, loadedDict.Get(1, out double latest));
        Assert.Equal(2.0, latest);
    }

    [Fact]
    public void Load_SameLocalIdTwice_ReturnsSameInstance_FromIdentityMap() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var dict = rev.CreateDict<int, double>();
        dict.Upsert(7, 7.7);
        var commit = rev.Commit(dict);
        Assert.True(commit.IsSuccess, $"Commit failed: {commit.Error}");

        var opened = Revision.Open(commit.Value, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");
        var loadedRev = opened.Value!;

        var first = loadedRev.Load(dict.LocalId);
        var second = loadedRev.Load(dict.LocalId);
        Assert.True(first.IsSuccess, $"First load failed: {first.Error}");
        Assert.True(second.IsSuccess, $"Second load failed: {second.Error}");

        Assert.Same(first.Value, second.Value);
    }

    [Fact]
    public void Upsert_DurableObjectFromDifferentRevision_InTypedDurObjDict_Throws() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev1 = new Revision(file);
        var ownerDict = rev1.CreateDict<int, DurableDict<int, double>>();
        var childSameRevision = rev1.CreateDict<int, double>();
        ownerDict.Upsert(1, childSameRevision); // same revision should pass

        var rev2 = new Revision(file);
        var childForeignRevision = rev2.CreateDict<int, double>();

        Assert.Throws<InvalidOperationException>(() => ownerDict.Upsert(2, childForeignRevision));
    }

    [Fact]
    public void Upsert_DurableObjectFromDifferentRevision_InMixedDict_Throws() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev1 = new Revision(file);
        var mixed = rev1.CreateDict<string>();
        var childSameRevision = rev1.CreateDict<int, double>();
        mixed.Upsert("ok", childSameRevision); // same revision should pass

        var rev2 = new Revision(file);
        var childForeignRevision = rev2.CreateDict<int, double>();

        Assert.Throws<InvalidOperationException>(() => mixed.Upsert("bad", childForeignRevision));
    }

    [Fact]
    public void GcCollectedObject_BecomesDetached_AndCannotBeReReferenced() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, double>>();
        var child = rev.CreateDict<int, double>();
        child.Upsert(7, 7.7);
        root.Upsert(1, child);

        var c1 = rev.Commit(root);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");
        Assert.NotEqual(DurableState.Detached, child.State);

        root.Remove(1);
        var c2 = rev.Commit(root);
        Assert.True(c2.IsSuccess, $"Commit2 failed: {c2.Error}");

        Assert.Equal(DurableState.Detached, child.State);
        Assert.Throws<InvalidOperationException>(() => root.Upsert(2, child));
        Assert.Throws<InvalidOperationException>(() => rev.Commit(child));

        var reopened = Revision.Open(c2.Value, file);
        Assert.True(reopened.IsSuccess, $"Open failed: {reopened.Error}");
        var reopenedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, double>>>(reopened.Value!.GraphRoot);
        Assert.False(reopenedRoot.ContainsKey(1));
    }

    [Fact]
    public void Commit_WithDanglingReference_FailsFast() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, double>>();
        var child = rev.CreateDict<int, double>();
        root.Upsert(1, child);

        // 人工破坏：直接从 Revision 的 pool 中释放 child，使 root 内引用悬空。
        var poolField = typeof(Revision).GetField("_pool", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object poolObj = poolField.GetValue(rev)!;
        var toHandle = typeof(LocalId).GetMethod("ToSlotHandle", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object childHandle = toHandle.Invoke(child.LocalId, null)!;
        poolObj.GetType().GetMethod("Free", BindingFlags.Public | BindingFlags.Instance)!.Invoke(poolObj, [childHandle]);

        var result = rev.Commit(root);
        Assert.True(result.IsFailure);
        Assert.IsType<SjCorruptionError>(result.Error);
    }

    [Fact]
    public void Open_WithDanglingReferenceInPersistedData_FailsFast() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, double>>();
        var child = rev.CreateDict<int, double>();
        child.Upsert(7, 7.7);
        root.Upsert(1, child);

        // 手工写入一个损坏快照：
        // 仅把 root 放入 ObjectMap，故 root->child 引用会在 Open 时成为悬空。
        var rootSave = VersionChain.Save(root, file);
        Assert.True(rootSave.IsSuccess, $"Save root failed: {rootSave.Error}");

        var mapField = typeof(Revision).GetField("_objectMap", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var objectMap = Assert.IsAssignableFrom<DurableDict<uint, ulong>>(mapField.GetValue(rev));
        objectMap.Upsert(root.LocalId.Value, rootSave.Value.Serialize()); // 故意不写 child ticket

        Span<byte> rootMeta = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta, root.LocalId.Value);
        DiffWriteContext context = new() { UsageKindOverride = UsageKind.ObjectMap, ForceSave = true };
        var mapSave = VersionChain.Save(objectMap, file, context, tailMeta: rootMeta);
        Assert.True(mapSave.IsSuccess, $"Save objectMap failed: {mapSave.Error}");

        var open = Revision.Open(new CommitId(mapSave.Value), file);
        Assert.True(open.IsFailure);
        Assert.IsType<SjCorruptionError>(open.Error);
    }

    [Fact]
    public void Open_WithGraphRootLocalIdZeroInTailMeta_FailsFast() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 42);

        var rootSave = VersionChain.Save(root, file);
        Assert.True(rootSave.IsSuccess, $"Save root failed: {rootSave.Error}");

        var mapField = typeof(Revision).GetField("_objectMap", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var objectMap = Assert.IsAssignableFrom<DurableDict<uint, ulong>>(mapField.GetValue(rev));
        objectMap.Upsert(root.LocalId.Value, rootSave.Value.Serialize());

        Span<byte> badMeta = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(badMeta, 0u); // 非法 GraphRoot LocalId
        DiffWriteContext context = new() { UsageKindOverride = UsageKind.ObjectMap, ForceSave = true };
        var mapSave = VersionChain.Save(objectMap, file, context, tailMeta: badMeta);
        Assert.True(mapSave.IsSuccess, $"Save objectMap failed: {mapSave.Error}");

        var open = Revision.Open(new CommitId(mapSave.Value), file);
        Assert.True(open.IsFailure);
        Assert.IsType<SjCorruptionError>(open.Error);
    }

    [Fact]
    public void Commit_SetsTransientObjectStateToClean() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 1);
        Assert.Equal(DurableState.TransientDirty, root.State);

        var commit = rev.Commit(root);
        Assert.True(commit.IsSuccess, $"Commit failed: {commit.Error}");
        Assert.Equal(DurableState.Clean, root.State);
    }

    [Fact]
    public void ParentId_DerivedFromFrameParentTicket() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Commit 1 (root)
        var c1 = new Revision(file);
        var root1 = c1.CreateDict<int, int>();
        var commit1 = c1.Commit(root1);
        Assert.True(commit1.IsSuccess, $"Commit1 failed: {commit1.Error}");
        CommitId id1 = commit1.Value;

        // Open commit 1 → ParentId should be null (root)
        var open1 = Revision.Open(id1, file);
        Assert.True(open1.IsSuccess);
        Assert.True(open1.Value!.HeadParent.IsNull, "root commit should have null parent");

        // Commit 2
        var c2 = new Revision(file);
        var root2 = c2.CreateDict<int, int>();
        var commit2 = c2.Commit(root2);
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
        var root = rev.CreateDict<int, int>();
        Assert.True(rev.Head.IsNull);
        Assert.True(rev.HeadParent.IsNull);

        // Commit 1 (root)
        var r1 = rev.Commit(root);
        Assert.True(r1.IsSuccess, $"Commit1 failed: {r1.Error}");
        CommitId id1 = r1.Value;
        Assert.False(id1.IsNull);
        Assert.Equal(id1, rev.Head);
        Assert.True(rev.HeadParent.IsNull, "root commit's parent should be null");

        // Commit 2 on the same Revision instance
        var r2 = rev.Commit(root);
        Assert.True(r2.IsSuccess, $"Commit2 failed: {r2.Error}");
        CommitId id2 = r2.Value;
        Assert.NotEqual(id1, id2);
        Assert.Equal(id2, rev.Head);
        Assert.Equal(id1, rev.HeadParent); // HeadParent should now point to previous Head

        // Commit 3
        var r3 = rev.Commit(root);
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
