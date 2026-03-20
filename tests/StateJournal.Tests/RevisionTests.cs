using System.Buffers.Binary;
using System.Reflection;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

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
            if (new FrameTag(info.Tag).Usage == FrameUsage.ObjectMap) { count++; }
        }
        return count;
    }

    private static FrameSource GetLatestObjectMapFrameSource(IRbfFile file) {
        foreach (var info in file.ScanReverse()) {
            var tag = new FrameTag(info.Tag);
            if (tag.Usage == FrameUsage.ObjectMap) { return tag.Source; }
        }
        throw new Xunit.Sdk.XunitException("No ObjectMap frame found.");
    }

    private static int GetMixedDictDurableRefCount<TKey>(DurableDict<TKey> dict) where TKey : notnull {
        var field = dict.GetType().GetField("_durableRefCount", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<int>(field!.GetValue(dict));
    }

    private static CommitOutcome AssertCommitSucceeded(AteliaResult<CommitOutcome> result, string label = "Commit") {
        Assert.True(result.IsSuccess, $"{label} failed: {result.Error}");
        return result.Value;
    }

    private static CommitId AssertHeadCommitId(AteliaResult<CommitOutcome> result, string label = "Commit") {
        return AssertCommitSucceeded(result, label).HeadCommitId;
    }

    private static Revision CreateRevision(uint segmentNumber = 1) {
        return new Revision(segmentNumber);
    }

    private static AteliaResult<CommitOutcome> CommitToFile(
        Revision revision,
        DurableObject graphRoot,
        IRbfFile file,
        uint segmentNumber = 1
    ) {
        var result = revision.Commit(graphRoot, file);
        if (result.IsSuccess) { revision.AcceptPersistedSegment(segmentNumber); }
        return result;
    }

    private static AteliaResult<CommitOutcome> SaveAsToFile(
        Revision revision,
        DurableObject graphRoot,
        IRbfFile file,
        uint segmentNumber = 1
    ) {
        var result = revision.SaveAs(graphRoot, file);
        if (result.IsSuccess) { revision.AcceptPersistedSegment(segmentNumber); }
        return result;
    }

    private static AteliaResult<Revision> OpenRevision(
        CommitId commitId,
        IRbfFile file,
        uint segmentNumber = 1
    ) {
        return Revision.Open(commitId, file, segmentNumber);
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { if (File.Exists(path)) { File.Delete(path); } } catch { }
        }
    }

    [Fact]
    public void NewRevision_HasNullId_And_NullParent() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();

        Assert.True(rev.HeadId.IsNull); // 未 Commit 前 Id 为 null
        Assert.True(rev.HeadParentId.IsNull);
    }

    [Fact]
    public void Load_SameLocalIdTwice_ReturnsSameInstance_FromIdentityMap() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateDict<int, double>();
        dict.Upsert(7, 7.7);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        var opened = OpenRevision(outcome.HeadCommitId, file);
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

        var rev1 = CreateRevision();
        var ownerDict = rev1.CreateDict<int, DurableDict<int, double>>();
        var childSameRevision = rev1.CreateDict<int, double>();
        ownerDict.Upsert(1, childSameRevision); // same revision should pass

        var rev2 = CreateRevision();
        var childForeignRevision = rev2.CreateDict<int, double>();

        Assert.Throws<InvalidOperationException>(() => ownerDict.Upsert(2, childForeignRevision));
    }

    [Fact]
    public void Upsert_DurableObjectFromDifferentRevision_InMixedDict_Throws() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev1 = CreateRevision();
        var mixed = rev1.CreateDict<string>();
        var childSameRevision = rev1.CreateDict<int, double>();
        mixed.Upsert("ok", childSameRevision); // same revision should pass

        var rev2 = CreateRevision();
        var childForeignRevision = rev2.CreateDict<int, double>();

        Assert.Throws<InvalidOperationException>(() => mixed.Upsert("bad", childForeignRevision));
    }

    [Fact]
    public void GcCollectedObject_BecomesDetached_AndCannotBeReReferenced() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, double>>();
        var child = rev.CreateDict<int, double>();
        child.Upsert(7, 7.7);
        root.Upsert(1, child);

        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        Assert.NotEqual(DurableState.Detached, child.State);

        root.Remove(1);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        Assert.Equal(DurableState.Detached, child.State);
        Assert.Throws<InvalidOperationException>(() => root.Upsert(2, child));
        var detachedCommit = CommitToFile(rev, child, file);
        Assert.True(detachedCommit.IsFailure);
        Assert.IsType<SjStateError>(detachedCommit.Error);

        var reopened = OpenRevision(outcome2.HeadCommitId, file);
        Assert.True(reopened.IsSuccess, $"Open failed: {reopened.Error}");
        var reopenedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, double>>>(reopened.Value!.GraphRoot);
        Assert.False(reopenedRoot.ContainsKey(1));
    }

    [Fact]
    public void Open_WithDanglingReferenceInPersistedData_FailsFast() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, double>>();
        var child = rev.CreateDict<int, double>();
        child.Upsert(7, 7.7);
        root.Upsert(1, child);

        // 手工写入一个损坏快照：
        // 仅把 root 放入 ObjectMap，故 root->child 引用会在 Open 时成为悬空。
        var rootSave = VersionChain.Save(root, file, DiffWriteContext.UserPrimary);
        Assert.True(rootSave.IsSuccess, $"Save root failed: {rootSave.Error}");

        var mapField = typeof(Revision).GetField("_objectMap", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var objectMap = Assert.IsAssignableFrom<DurableDict<uint, ulong>>(mapField.GetValue(rev));
        objectMap.Upsert(root.LocalId.Value, rootSave.Value.Serialize()); // 故意不写 child ticket

        Span<byte> rootMeta = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta, root.LocalId.Value);
        DiffWriteContext context = new(FrameUsage.ObjectMap, FrameSource.PrimaryCommit) { ForceSave = true };
        var mapSave = VersionChain.Save(objectMap, file, context, tailMeta: rootMeta);
        Assert.True(mapSave.IsSuccess, $"Save objectMap failed: {mapSave.Error}");

        var open = OpenRevision(new CommitId(mapSave.Value), file);
        Assert.True(open.IsFailure);
        Assert.IsType<SjCorruptionError>(open.Error);
    }

    [Fact]
    public void Open_WithGraphRootLocalIdZeroInTailMeta_FailsFast() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 42);

        var rootSave = VersionChain.Save(root, file, DiffWriteContext.UserPrimary);
        Assert.True(rootSave.IsSuccess, $"Save root failed: {rootSave.Error}");

        var mapField = typeof(Revision).GetField("_objectMap", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var objectMap = Assert.IsAssignableFrom<DurableDict<uint, ulong>>(mapField.GetValue(rev));
        objectMap.Upsert(root.LocalId.Value, rootSave.Value.Serialize());

        Span<byte> badMeta = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(badMeta, 0u); // 非法 GraphRoot LocalId
        DiffWriteContext context = new(FrameUsage.ObjectMap, FrameSource.PrimaryCommit) { ForceSave = true };
        var mapSave = VersionChain.Save(objectMap, file, context, tailMeta: badMeta);
        Assert.True(mapSave.IsSuccess, $"Save objectMap failed: {mapSave.Error}");

        var open = OpenRevision(new CommitId(mapSave.Value), file);
        Assert.True(open.IsFailure);
        Assert.IsType<SjCorruptionError>(open.Error);
    }

    [Fact]
    public void ParentId_DerivedFromFrameParentTicket() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Commit 1 (root)
        var rev1 = CreateRevision();
        var root1 = rev1.CreateDict<int, int>();
        var outcome1 = AssertCommitSucceeded(CommitToFile(rev1, root1, file), "Commit1");
        CommitId id1 = outcome1.HeadCommitId;

        // Open commit 1 → ParentId should be null (root)
        var open1 = OpenRevision(id1, file);
        Assert.True(open1.IsSuccess);
        Assert.True(open1.Value!.HeadParentId.IsNull, "root commit should have null parent");

        // Commit 2
        var rev2 = CreateRevision();
        var root2 = rev2.CreateDict<int, int>();
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev2, root2, file), "Commit2");
        CommitId id2 = outcome2.HeadCommitId;

        // id1 and id2 are distinct, non-null
        Assert.False(id1.IsNull);
        Assert.False(id2.IsNull);
        Assert.NotEqual(id1, id2);

        // Open commit 2 and verify it loads successfully
        var open2 = OpenRevision(id2, file);
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
        var rev = CreateRevision();

        var result = rev.Load(LocalId.Null);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Load_NonexistentLocalId_ReturnsError() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var result = rev.Load(new LocalId(999));
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Consecutive_Commits_UpdateHeadParent() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        Assert.True(rev.HeadId.IsNull);
        Assert.True(rev.HeadParentId.IsNull);

        // Commit 1 (root)
        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        CommitId id1 = outcome1.HeadCommitId;
        Assert.False(id1.IsNull);
        Assert.Equal(id1, rev.HeadId);
        Assert.True(rev.HeadParentId.IsNull, "root commit's parent should be null");

        // Commit 2 on the same Revision instance
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        CommitId id2 = outcome2.HeadCommitId;
        Assert.NotEqual(id1, id2);
        Assert.Equal(id2, rev.HeadId);
        Assert.Equal(id1, rev.HeadParentId); // HeadParent should now point to previous Head

        // Commit 3
        var outcome3 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit3");
        CommitId id3 = outcome3.HeadCommitId;
        Assert.Equal(id3, rev.HeadId);
        Assert.Equal(id2, rev.HeadParentId); // HeadParent tracks the chain

        // Verify persistence: Open commit 3, its parent should be id2
        var open3 = OpenRevision(id3, file);
        Assert.True(open3.IsSuccess, $"Open commit3 failed: {open3.Error}");
        Assert.Equal(id2, open3.Value!.HeadParentId);
    }

    [Fact]
    public void MixedDict_DurableRefCount_TracksUpsertRemoveAndDiscardChanges() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        var child1 = rev.CreateDict<int, int>();
        var child2 = rev.CreateDict<int, int>();

        Assert.Equal(0, GetMixedDictDurableRefCount(root));

        root.Upsert(100, 123);
        Assert.Equal(0, GetMixedDictDurableRefCount(root));

        root.Upsert(1, child1);
        Assert.Equal(1, GetMixedDictDurableRefCount(root));

        root.Upsert(1, child2);
        Assert.Equal(1, GetMixedDictDurableRefCount(root));

        root.Upsert(2, child1);
        Assert.Equal(2, GetMixedDictDurableRefCount(root));

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        Assert.Equal(2, GetMixedDictDurableRefCount(root));

        root.Remove(1);
        Assert.Equal(1, GetMixedDictDurableRefCount(root));

        root.Upsert(2, 456);
        Assert.Equal(0, GetMixedDictDurableRefCount(root));

        root.Upsert(3, child2);
        Assert.Equal(1, GetMixedDictDurableRefCount(root));

        root.DiscardChanges();
        Assert.Equal(2, GetMixedDictDurableRefCount(root));
        Assert.Equal(GetIssue.None, root.Get(1, out DurableObject? restoredA));
        Assert.Same(child2, restoredA);
        Assert.Equal(GetIssue.None, root.Get(2, out DurableObject? restoredB));
        Assert.Same(child1, restoredB);
    }

    [Fact]
    public void MixedDict_DurableRefCount_RecountsAfterOpen() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        var child1 = rev.CreateDict<int, int>();
        var child2 = rev.CreateDict<int, int>();
        root.Upsert(1, child1);
        root.Upsert(2, 42);
        root.Upsert(3, child2);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        Assert.Equal(2, GetMixedDictDurableRefCount(root));

        var open = OpenRevision(outcome.HeadCommitId, file);
        Assert.True(open.IsSuccess, $"Open failed: {open.Error}");

        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int>>(open.Value!.GraphRoot);
        Assert.Equal(2, GetMixedDictDurableRefCount(loadedRoot));
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableObject? loadedA));
        Assert.NotNull(loadedA);
        Assert.Equal(GetIssue.None, loadedRoot.Get(3, out DurableObject? loadedC));
        Assert.NotNull(loadedC);
    }
}
