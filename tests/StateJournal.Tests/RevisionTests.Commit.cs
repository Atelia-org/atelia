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

        CommitTicket commitTicket = outcome.HeadCommitTicket;
        Assert.False(commitTicket.IsNull); // Commit 后 Id 不为 null
        Assert.Equal(commitTicket, rev.HeadId); // Id 已更新
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome.Completion);
        Assert.True(outcome.IsPrimaryOnly);

        // Open from CommitTicket
        var openResult = OpenRevision(commitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        Assert.Equal(commitTicket, loaded.HeadId);
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
        Assert.Equal(2u, dict.LocalId.Value); // slot 0=ObjectMap, slot 1=SymbolTable, slot 2=first user object
        Assert.Equal(DurableState.TransientDirty, dict.State);

        dict.Upsert(10, 3.14);
        dict.Upsert(20, 2.718);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome.Completion);

        // Open via CommitTicket and verify
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
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

        var open = OpenRevision(outcome2.HeadCommitTicket, file);
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

    #region Fragmentation Stability

    [Fact]
    public void Commit_WithHeavyFragmentation_RemainsConsistent() {
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

        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        for (int i = 70; i < totalChildren; i++) {
            var child = children[i];
            Assert.NotEqual(DurableState.Detached, child.State);
            Assert.Equal(GetIssue.None, child.Get(i, out int val));
            Assert.Equal(i * 10, val);
        }

        for (int round = 0; round < 5; round++) {
            var aliveChild = children[70];
            aliveChild.Upsert(9999 + round, round);

            _ = AssertCommitSucceeded(CommitToFile(rev, root, file), $"Commit round {round}");
        }

        Assert.NotNull(rev.GraphRoot);
        var finalRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(rev.GraphRoot);
        Assert.Equal(30, finalRoot.Count);
        for (int i = 70; i < totalChildren; i++) {
            Assert.True(finalRoot.ContainsKey(i), $"Key {i} missing in final root");
        }
    }

    [Fact]
    public void Commit_WithHeavyFragmentation_ThenOpen_RoundTrips() {
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

        for (int i = 0; i < 80; i++) {
            root.Remove(i);
        }

        CommitTicket lastCommitTicket = default;
        for (int round = 0; round < 10; round++) {
            lastCommitTicket = AssertHeadCommitTicket(CommitToFile(rev, root, file), $"Commit round {round}");
        }

        var openResult = OpenRevision(lastCommitTicket, file);
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
    public void Commit_WithMixedDictChildRefs_RemainsCorrectUnderFragmentation() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();

        var padding = new DurableDict<int, int>[80];
        for (int i = 0; i < 80; i++) {
            padding[i] = rev.CreateDict<int, int>();
            root.Upsert(i, padding[i]);
        }

        var survivors = new DurableDict<int, int>[20];
        for (int i = 0; i < 20; i++) {
            survivors[i] = rev.CreateDict<int, int>();
            survivors[i].Upsert(i, i * 100);
            root.Upsert(1000 + i, survivors[i]);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < 80; i++) {
            root.Remove(i);
        }

        for (int round = 0; round < 10; round++) {
            _ = AssertCommitSucceeded(CommitToFile(rev, root, file), $"Commit round {round}");
        }

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
    public void Commit_WithSmallerGraph_StillPersistsCorrectly() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();

        for (int i = 0; i < 30; i++) {
            var child = rev.CreateDict<int, int>();
            root.Upsert(i, child);
        }
        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome1.Completion);

        for (int i = 0; i < 20; i++) {
            root.Remove(i);
        }

        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome2.Completion);

        for (int i = 20; i < 30; i++) {
            Assert.True(root.ContainsKey(i));
        }
    }

    [Fact]
    public void Commit_AppendsSingleObjectMapFramePerCommit() {
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
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome2.Completion);
        int after = CountObjectMapFrames(file);

        Assert.Equal(before + 1, after);
    }
    #endregion
}
