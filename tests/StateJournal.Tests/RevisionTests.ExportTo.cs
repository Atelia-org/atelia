using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void ExportTo_EmptyGraph_CanBeOpenedFromTargetFile() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        _ = AssertCommitSucceeded(CommitToFile(rev, root, srcFile));

        var exportResult = rev.ExportTo(root, dstFile);
        Assert.True(exportResult.IsSuccess, $"ExportTo failed: {exportResult.Error}");

        var opened = OpenRevision(exportResult.Value, dstFile);
        Assert.True(opened.IsSuccess, $"Open exported failed: {opened.Error}");
        Assert.Equal(exportResult.Value, opened.Value!.HeadId);
        Assert.Equal(FrameSource.CrossFileSnapshot, GetLatestObjectMapFrameSource(dstFile));
    }

    [Fact]
    public void ExportTo_PreservesDataInTargetFile() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, double>();
        root.Upsert(1, 3.14);
        root.Upsert(2, 2.718);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, srcFile));

        var exportResult = rev.ExportTo(root, dstFile);
        Assert.True(exportResult.IsSuccess, $"ExportTo failed: {exportResult.Error}");

        var opened = OpenRevision(exportResult.Value, dstFile);
        Assert.True(opened.IsSuccess, $"Open exported failed: {opened.Error}");
        var loadResult = opened.Value!.Load(root.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loaded = Assert.IsAssignableFrom<DurableDict<int, double>>(loadResult.Value);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(1, out double v1));
        Assert.Equal(3.14, v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out double v2));
        Assert.Equal(2.718, v2);
    }

    [Fact]
    public void ExportTo_DoesNotModifySourceRevision() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        root.Upsert(42, 100);
        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, srcFile));
        CommitTicket headBefore = rev.HeadId;

        var exportResult = rev.ExportTo(root, dstFile);
        Assert.True(exportResult.IsSuccess, $"ExportTo failed: {exportResult.Error}");

        // HeadId 未变
        Assert.Equal(headBefore, rev.HeadId);
        // GraphRoot 未变
        Assert.Equal(root.LocalId, rev.GraphRoot!.LocalId);
        // 原文件仍可正常 Commit
        root.Upsert(43, 200);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, srcFile), "CommitAfterExport");
        Assert.NotEqual(headBefore, rev.HeadId);
    }

    [Fact]
    public void ExportTo_WithChildObjects_ExportsEntireGraph() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var child1 = rev.CreateDict<int, int>();
        var child2 = rev.CreateDict<int, int>();
        child1.Upsert(1, 10);
        child2.Upsert(2, 20);
        root.Upsert(1, child1);
        root.Upsert(2, child2);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, srcFile));

        var exportResult = rev.ExportTo(root, dstFile);
        Assert.True(exportResult.IsSuccess, $"ExportTo failed: {exportResult.Error}");

        var opened = OpenRevision(exportResult.Value, dstFile);
        Assert.True(opened.IsSuccess, $"Open exported failed: {opened.Error}");
        var loadedRev = opened.Value!;

        var loadChild1 = loadedRev.Load(child1.LocalId);
        Assert.True(loadChild1.IsSuccess, $"Load child1 failed: {loadChild1.Error}");
        var lc1 = Assert.IsAssignableFrom<DurableDict<int, int>>(loadChild1.Value);
        Assert.Equal(GetIssue.None, lc1.Get(1, out int v));
        Assert.Equal(10, v);

        var loadChild2 = loadedRev.Load(child2.LocalId);
        Assert.True(loadChild2.IsSuccess, $"Load child2 failed: {loadChild2.Error}");
        var lc2 = Assert.IsAssignableFrom<DurableDict<int, int>>(loadChild2.Value);
        Assert.Equal(GetIssue.None, lc2.Get(2, out int v2));
        Assert.Equal(20, v2);
    }

    [Fact]
    public void ExportTo_WithUncommittedChanges_ExportsLatestInMemoryState() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 100);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, srcFile));

        // 修改但不 Commit
        root.Upsert(1, 999);
        root.Upsert(2, 888);

        var exportResult = rev.ExportTo(root, dstFile);
        Assert.True(exportResult.IsSuccess, $"ExportTo failed: {exportResult.Error}");

        var opened = OpenRevision(exportResult.Value, dstFile);
        Assert.True(opened.IsSuccess, $"Open exported failed: {opened.Error}");
        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(opened.Value!.Load(root.LocalId).Value);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int v1));
        Assert.Equal(999, v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out int v2));
        Assert.Equal(888, v2);
    }
}
