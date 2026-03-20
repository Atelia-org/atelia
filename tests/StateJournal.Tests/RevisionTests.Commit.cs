using System.Reflection;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void Commit_Empty_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Create and commit with a minimal root
        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        CommitId commitId = outcome.HeadCommitId;
        Assert.False(commitId.IsNull); // Commit 后 Id 不为 null
        Assert.Equal(commitId, rev.HeadId); // Id 已更新
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome.Completion);
        Assert.True(outcome.IsPrimaryOnly);
        Assert.False(outcome.IsCompacted);

        // Open from CommitId
        var openResult = OpenRevision(commitId, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        Assert.Equal(commitId, loaded.HeadId);
        Assert.True(loaded.HeadParentId.IsNull); // root commit
    }

    [Fact]
    public void Commit_WithCreatedObject_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();

        // 通过 Revision 工厂创建对象（自动绑定 Revision + 分配 LocalId）
        var dict = rev.CreateDict<int, double>();
        Assert.Equal(rev, dict.Revision);
        Assert.False(dict.LocalId.IsNull);
        Assert.Equal(1u, dict.LocalId.Value);
        Assert.Equal(DurableState.TransientDirty, dict.State);

        dict.Upsert(10, 3.14);
        dict.Upsert(20, 2.718);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome.Completion);

        // Open via CommitId and verify
        var openResult = OpenRevision(outcome.HeadCommitId, file);
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

        var rev = CreateRevision();
        var dict = rev.CreateDict<int, double>();
        dict.Upsert(1, 1.0);

        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");

        dict.Upsert(1, 2.0);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit2");

        var open = OpenRevision(outcome2.HeadCommitId, file);
        Assert.True(open.IsSuccess, $"Open failed: {open.Error}");

        var loaded = open.Value!;
        var load = loaded.Load(dict.LocalId);
        Assert.True(load.IsSuccess, $"Load failed: {load.Error}");

        var loadedDict = Assert.IsAssignableFrom<DurableDict<int, double>>(load.Value);
        Assert.Equal(GetIssue.None, loadedDict.Get(1, out double latest));
        Assert.Equal(2.0, latest);
    }

    [Fact]
    public void Commit_WithDanglingReference_FailsFast() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, double>>();
        var child = rev.CreateDict<int, double>();
        root.Upsert(1, child);

        // 人工破坏：直接从 Revision 的 pool 中释放 child，使 root 内引用悬空。
        var poolField = typeof(Revision).GetField("_pool", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object poolObj = poolField.GetValue(rev)!;
        var toHandle = typeof(LocalId).GetMethod("ToSlotHandle", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object childHandle = toHandle.Invoke(child.LocalId, null)!;
        poolObj.GetType().GetMethod("Free", BindingFlags.Public | BindingFlags.Instance)!.Invoke(poolObj, [childHandle]);

        var result = CommitToFile(rev, root, file);
        Assert.True(result.IsFailure);
        Assert.IsType<SjCorruptionError>(result.Error);
    }

    [Fact]
    public void Commit_WithNullGraphRoot_ThrowsArgumentNullException() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        Assert.Throws<ArgumentNullException>("graphRoot", () => CommitToFile(rev, null!, file));
    }

    [Fact]
    public void Commit_WhenPersistFails_DoesNotDetachUnreachableObjectsEarly() {
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, double>>();
        var child = rev.CreateDict<int, double>();
        root.Upsert(1, child);

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        Assert.Equal(DurableState.Clean, child.State);

        // child 变为不可达；若 Commit 失败前提前 Sweep，会被错误标记 Detached。
        root.Remove(1);
        file.Dispose(); // 人工制造持久化失败

        var failed = CommitToFile(rev, root, file);
        Assert.True(failed.IsFailure);
        Assert.Equal(DurableState.Clean, child.State);
    }

    [Fact]
    public void Commit_WhenPersistFails_DoesNotUpdateGraphRoot() {
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root1 = rev.CreateDict<int, int>();
        root1.Upsert(1, 1);
        _ = AssertCommitSucceeded(CommitToFile(rev, root1, file), "Commit1");
        Assert.Equal(root1.LocalId, rev.GraphRoot!.LocalId);

        var root2 = rev.CreateDict<int, int>();
        root2.Upsert(2, 2);
        file.Dispose(); // 人工制造持久化失败

        var failed = CommitToFile(rev, root2, file);
        Assert.True(failed.IsFailure);
        Assert.Equal(root1.LocalId, rev.GraphRoot!.LocalId);
    }

    [Fact]
    public void Commit_SetsTransientObjectStateToClean() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 1);
        Assert.Equal(DurableState.TransientDirty, root.State);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        Assert.Equal(DurableState.Clean, root.State);
    }

    #region Compaction Integration

    [Fact]
    public void Commit_WithHeavyFragmentation_CompactsAndRemainsConsistent() {
        // 创建 80+ 对象再删除大部分，产生 >25% 碎片率，触发 Compaction
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();

        // 创建 100 个子对象挂在 root 下
        const int totalChildren = 100;
        var children = new DurableDict<int, int>[totalChildren];
        for (int i = 0; i < totalChildren; i++) {
            children[i] = rev.CreateDict<int, int>();
            children[i].Upsert(i, i * 10);
            root.Upsert(i, children[i]);
        }

        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome1.Completion);

        // 删除前 70 个子对象——产生大量空洞
        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

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

            _ = AssertCommitSucceeded(CommitToFile(rev, root, file), $"Commit round {round}");
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

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();

        const int totalChildren = 100;
        for (int i = 0; i < totalChildren; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(1, i * 100);
            root.Upsert(i, child);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        // 删除 80 个子对象，保留最后 20 个
        for (int i = 0; i < 80; i++) {
            root.Remove(i);
        }

        // 多次 commit 让 compaction 逐步执行
        CommitId lastCommitId = default;
        for (int round = 0; round < 10; round++) {
            lastCommitId = AssertHeadCommitId(CommitToFile(rev, root, file), $"Commit round {round}");
        }

        // Open 最终 commit，验证数据完整性
        var openResult = OpenRevision(lastCommitId, file);
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

        var rev = CreateRevision();
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

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        // 删除所有垫脚对象
        for (int i = 0; i < 80; i++) {
            root.Remove(i);
        }

        // 多次 commit 触发 compaction
        for (int round = 0; round < 10; round++) {
            _ = AssertCommitSucceeded(CommitToFile(rev, root, file), $"Commit round {round}");
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
    public void Commit_WithHotPathCompactionValidation_StillRoundTripsCorrectly() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var validationScope = Revision.OverrideCompactionValidationModeScope(Revision.CompactionValidationMode.HotPath);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();

        for (int i = 0; i < 80; i++) {
            var padding = rev.CreateDict<int, int>();
            padding.Upsert(i, i);
            root.Upsert(i, padding);
        }

        for (int i = 0; i < 20; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(i, i * 10);
            root.Upsert(10_000 + i, child);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < 80; i++) {
            root.Remove(i);
        }

        CommitOutcome outcome = default;
        for (int round = 0; round < 10; round++) {
            outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), $"Commit round {round}");
        }

        Assert.True(outcome.IsCompacted || outcome.IsPrimaryOnly);

        var open = OpenRevision(outcome.HeadCommitId, file);
        Assert.True(open.IsSuccess, $"Open failed: {open.Error}");

        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int>>(open.Value!.GraphRoot);
        Assert.Equal(20, loadedRoot.Count);
        for (int i = 0; i < 20; i++) {
            Assert.Equal(GetIssue.None, loadedRoot.Get(10_000 + i, out DurableObject? childObj));
            var child = Assert.IsAssignableFrom<DurableDict<int, int>>(childObj);
            Assert.Equal(GetIssue.None, child.Get(i, out int value));
            Assert.Equal(i * 10, value);
        }
    }

    [Fact]
    public void Commit_BelowMinThreshold_DoesNotCompact() {
        // 少于 CompactionMinThreshold（64）个存活对象时不触发压缩
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();

        // 创建 30 个子对象
        for (int i = 0; i < 30; i++) {
            var child = rev.CreateDict<int, int>();
            root.Upsert(i, child);
        }
        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome1.Completion);

        // 删除 20 个——碎片率高但数量少
        for (int i = 0; i < 20; i++) {
            root.Remove(i);
        }

        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome2.Completion);

        // 验证数据完整（无论是否压缩都应正确）
        for (int i = 20; i < 30; i++) {
            Assert.True(root.ContainsKey(i));
        }
    }

    [Fact]
    public void Commit_WhenCompactionTriggered_AppendsTwoObjectMapFrames() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();

        const int totalChildren = 140;
        for (int i = 0; i < totalChildren; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(i, i);
            root.Upsert(i, child);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        int before = CountObjectMapFrames(file);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(CommitCompletion.Compacted, outcome2.Completion);
        int after = CountObjectMapFrames(file);

        Assert.Equal(before + 2, after);
    }
    #endregion
}
