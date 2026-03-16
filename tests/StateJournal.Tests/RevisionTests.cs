using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using Xunit;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Tests;

public partial class RevisionTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), $"rbf-commit-test-{Guid.NewGuid()}");
        _tempFiles.Add(path);
        return path;
    }

    private static int CountObjectMapFrames(IRbfFile file) {
        int count = 0;
        foreach (var info in file.ScanReverse()) {
            if (new FrameTag(info.Tag).UsageKind == UsageKind.ObjectMap) { count++; }
        }
        return count;
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
        var detachedCommit = rev.Commit(child);
        Assert.True(detachedCommit.IsFailure);
        Assert.IsType<SjStateError>(detachedCommit.Error);

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
    public void Commit_WithNullGraphRoot_ReturnsSjStateError() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = new Revision(file);

        var result = rev.Commit(null!);
        Assert.True(result.IsFailure);
        Assert.IsType<SjStateError>(result.Error);
    }

    [Fact]
    public void Commit_WhenPersistFails_DoesNotDetachUnreachableObjectsEarly() {
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, double>>();
        var child = rev.CreateDict<int, double>();
        root.Upsert(1, child);

        var c1 = rev.Commit(root);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");
        Assert.Equal(DurableState.Clean, child.State);

        // child 变为不可达；若 Commit 失败前提前 Sweep，会被错误标记 Detached。
        root.Remove(1);
        file.Dispose(); // 人工制造持久化失败

        var failed = rev.Commit(root);
        Assert.True(failed.IsFailure);
        Assert.Equal(DurableState.Clean, child.State);
    }

    [Fact]
    public void Commit_WhenPersistFails_DoesNotUpdateGraphRoot() {
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root1 = rev.CreateDict<int, int>();
        root1.Upsert(1, 1);
        var c1 = rev.Commit(root1);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");
        Assert.Equal(root1.LocalId, rev.GraphRoot!.LocalId);

        var root2 = rev.CreateDict<int, int>();
        root2.Upsert(2, 2);
        file.Dispose(); // 人工制造持久化失败

        var failed = rev.Commit(root2);
        Assert.True(failed.IsFailure);
        Assert.Equal(root1.LocalId, rev.GraphRoot!.LocalId);
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

    // ───────────────────── Compaction Integration ─────────────────────

    [Fact]
    public void Commit_WithHeavyFragmentation_CompactsAndRemainsConsistent() {
        // 创建 80+ 对象再删除大部分，产生 >25% 碎片率，触发 Compaction
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, int>>();

        // 创建 100 个子对象挂在 root 下
        const int totalChildren = 100;
        var children = new DurableDict<int, int>[totalChildren];
        for (int i = 0; i < totalChildren; i++) {
            children[i] = rev.CreateDict<int, int>();
            children[i].Upsert(i, i * 10);
            root.Upsert(i, children[i]);
        }

        var c1 = rev.Commit(root);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");

        // 删除前 70 个子对象——产生大量空洞
        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        var c2 = rev.Commit(root);
        Assert.True(c2.IsSuccess, $"Commit2 (after deletions) failed: {c2.Error}");

        // 验证剩余 30 个子对象仍然可达、数据完整
        for (int i = 70; i < totalChildren; i++) {
            var child = children[i];
            Assert.NotEqual(DurableState.Detached, child.State);
            Assert.Equal(GetIssue.None, child.Get(i, out int val));
            Assert.Equal(i * 10, val);
        }

        // 再 commit 几次，让 compaction 渐进收敛
        for (int round = 0; round < 5; round++) {
            // 每轮微修改以确保有脏数据触发 Persist
            var aliveChild = children[70];
            aliveChild.Upsert(9999 + round, round);

            var cr = rev.Commit(root);
            Assert.True(cr.IsSuccess, $"Commit round {round} failed: {cr.Error}");
        }

        // 最终验证：数据完整且 GraphRoot 可达
        Assert.NotNull(rev.GraphRoot);
        var finalRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(rev.GraphRoot);
        Assert.Equal(30, finalRoot.Count);
        for (int i = 70; i < totalChildren; i++) {
            Assert.True(finalRoot.ContainsKey(i), $"Key {i} missing in final root");
        }
    }

    [Fact]
    public void Commit_WithCompaction_ThenOpen_Roundtrips() {
        // 验证 compaction 后落盘数据仍可正确 Open 恢复
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, int>>();

        const int totalChildren = 100;
        for (int i = 0; i < totalChildren; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(1, i * 100);
            root.Upsert(i, child);
        }

        var c1 = rev.Commit(root);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");

        // 删除 80 个子对象，保留最后 20 个
        for (int i = 0; i < 80; i++) {
            root.Remove(i);
        }

        // 多次 commit 让 compaction 逐步执行
        CommitId lastCommitId = default;
        for (int round = 0; round < 10; round++) {
            var cr = rev.Commit(root);
            Assert.True(cr.IsSuccess, $"Commit round {round} failed: {cr.Error}");
            lastCommitId = cr.Value;
        }

        // Open 最终 commit，验证数据完整性
        var openResult = Revision.Open(lastCommitId, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        Assert.NotNull(loaded.GraphRoot);
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(loaded.GraphRoot);
        Assert.Equal(20, loadedRoot.Count);

        for (int i = 80; i < totalChildren; i++) {
            Assert.True(loadedRoot.ContainsKey(i), $"Key {i} missing after Open");
            Assert.Equal(GetIssue.None, loadedRoot.Get(i, out var loadedChild));
            Assert.Equal(GetIssue.None, loadedChild!.Get(1, out int val));
            Assert.Equal(i * 100, val);
        }
    }

    [Fact]
    public void Commit_WithMixedDictChildRefs_CompactionRewritesCorrectly() {
        // 验证 MixedDict 中 DurableRef 引用在 compaction 后被正确重写
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int>();

        // 创建 80 个垫脚对象（将被删除产生碎片）+ 20 个保留对象
        var padding = new DurableDict<int, int>[80];
        for (int i = 0; i < 80; i++) {
            padding[i] = rev.CreateDict<int, int>();
            root.Upsert(i, padding[i]);
        }

        // 20 个存活的 child 对象，通过 MixedDict 引用
        var survivors = new DurableDict<int, int>[20];
        for (int i = 0; i < 20; i++) {
            survivors[i] = rev.CreateDict<int, int>();
            survivors[i].Upsert(i, i * 100);
            root.Upsert(1000 + i, survivors[i]);
        }

        var c1 = rev.Commit(root);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");

        // 删除所有垫脚对象
        for (int i = 0; i < 80; i++) {
            root.Remove(i);
        }

        // 多次 commit 触发 compaction
        for (int round = 0; round < 10; round++) {
            var cr = rev.Commit(root);
            Assert.True(cr.IsSuccess, $"Commit round {round} failed: {cr.Error}");
        }

        // 验证 MixedDict 中引用仍然正确
        Assert.Equal(20, root.Count);
        for (int i = 0; i < 20; i++) {
            Assert.True(root.ContainsKey(1000 + i));
            Assert.Equal(GetIssue.None, root.Get(1000 + i, out DurableObject? childObj));
            var child = Assert.IsAssignableFrom<DurableDict<int, int>>(childObj);
            Assert.Equal(GetIssue.None, child.Get(i, out int val));
            Assert.Equal(i * 100, val);
        }
    }

    [Fact]
    public void Commit_BelowMinThreshold_DoesNotCompact() {
        // 少于 CompactionMinThreshold（64）个存活对象时不触发压缩
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, int>>();

        // 创建 30 个子对象
        for (int i = 0; i < 30; i++) {
            var child = rev.CreateDict<int, int>();
            root.Upsert(i, child);
        }
        var c1 = rev.Commit(root);
        Assert.True(c1.IsSuccess);

        // 删除 20 个——碎片率高但数量少
        for (int i = 0; i < 20; i++) {
            root.Remove(i);
        }

        var c2 = rev.Commit(root);
        Assert.True(c2.IsSuccess);

        // 验证数据完整（无论是否压缩都应正确）
        for (int i = 20; i < 30; i++) {
            Assert.True(root.ContainsKey(i));
        }
    }

    [Fact]
    public void Commit_WhenCompactionTriggered_AppendsTwoObjectMapFrames() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, int>>();

        const int totalChildren = 140;
        for (int i = 0; i < totalChildren; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(i, i);
            root.Upsert(i, child);
        }

        var c1 = rev.Commit(root);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");

        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        int before = CountObjectMapFrames(file);
        var c2 = rev.Commit(root);
        Assert.True(c2.IsSuccess, $"Commit2 failed: {c2.Error}");
        int after = CountObjectMapFrames(file);

        Assert.Equal(before + 2, after);
    }

    [Fact]
    public void Commit_WhenCompactionTriggered_HeadParentPointsToIntermediateCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        const int totalChildren = 140;
        for (int i = 0; i < totalChildren; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(i, i);
            root.Upsert(i, child);
        }

        var c1 = rev.Commit(root);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");

        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        var c2 = rev.Commit(root);
        Assert.True(c2.IsSuccess, $"Commit2 failed: {c2.Error}");

        Assert.Equal(c2.Value, rev.Head);
        Assert.NotEqual(c1.Value, rev.HeadParent); // HeadParent 应指向内部中间 commit

        var opened = Revision.Open(c2.Value, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");
        Assert.Equal(rev.HeadParent, opened.Value!.HeadParent);
        Assert.NotEqual(c1.Value, opened.Value!.HeadParent);
    }

    [Fact]
    public void Commit_WhenCompactionFollowupPersistFails_ReturnsFailureWithPrimaryCommitDurable() {
        var path = GetTempFilePath();
        var inner = RbfFile.CreateNew(path);
        using var file = new FailOnNthBeginAppendFile(inner);

        var rev = new Revision(file);
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        const int totalChildren = 140;
        for (int i = 0; i < totalChildren; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(i, i);
            root.Upsert(i, child);
        }

        var c1 = rev.Commit(root);
        Assert.True(c1.IsSuccess, $"Commit1 failed: {c1.Error}");

        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        int before = CountObjectMapFrames(file);
        file.Arm(failOnBeginAppendIndex: 4);
        var c2 = rev.Commit(root);
        Assert.True(c2.IsFailure);
        var err = Assert.IsType<SjCompactionFollowupPersistError>(c2.Error);
        Assert.Equal("SJ.Compaction.FollowupPersistFailed", err.ErrorCode);
        Assert.True(err.Details?.ContainsKey("PrimaryCommitTicket"));
        Assert.Equal("FollowupPersist", err.Details!["CompactionStage"]);
        Assert.True(err.Details.ContainsKey("FollowupErrorCode"));
        int after = CountObjectMapFrames(file);
        Assert.Equal(before + 1, after); // 仅 primary commit 的 ObjectMap 已写入

        Assert.Equal(rev.Head, rev.GraphRoot!.Revision.Head);
        var opened = Revision.Open(rev.Head, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.Value!.GraphRoot);
        Assert.Equal(70, loadedRoot.Count);
    }

    private sealed class FailOnNthBeginAppendFile(IRbfFile inner) : IRbfFile {
        private bool _armed;
        private int _beginAppendCount;
        private int _failOnBeginAppendIndex;

        public void Arm(int failOnBeginAppendIndex) {
            if (failOnBeginAppendIndex <= 0) { throw new ArgumentOutOfRangeException(nameof(failOnBeginAppendIndex)); }
            _armed = true;
            _beginAppendCount = 0;
            _failOnBeginAppendIndex = failOnBeginAppendIndex;
        }

        public long TailOffset => inner.TailOffset;

        public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => inner.Append(tag, payload, tailMeta);

        public RbfFrameBuilder BeginAppend() {
            if (_armed && ++_beginAppendCount == _failOnBeginAppendIndex) {
                _armed = false;
                throw new IOException("Injected failure on compaction follow-up BeginAppend.");
            }
            return inner.BeginAppend();
        }
        public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) => inner.ReadPooledFrame(ptr);
        public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) => inner.ReadFrame(ptr, buffer);
        public RbfReverseSequence ScanReverse(bool showTombstone = false) => inner.ScanReverse(showTombstone);
        public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => inner.ReadFrameInfo(ticket);
        public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
        public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
        public void DurableFlush() => inner.DurableFlush();
        public void Truncate(long newLengthBytes) => inner.Truncate(newLengthBytes);
        public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
        public void Dispose() => inner.Dispose();
    }
}
