using System.Reflection;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void SaveAs_EmptyGraph_SwitchesToNewFile() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = new Revision(srcFile);
        var root = rev.CreateDict<int, int>();
        _ = AssertCommitSucceeded(rev.Commit(root));

        var saveAsResult = rev.SaveAs(root, dstFile);
        var outcome = AssertCommitSucceeded(saveAsResult, "SaveAs");
        Assert.Equal(CommitCompletion.PrimaryOnly, outcome.Completion);

        // HeadId 已更新
        Assert.Equal(outcome.HeadCommitId, rev.HeadId);

        // 新文件可独立打开
        var opened = Revision.Open(outcome.HeadCommitId, dstFile);
        Assert.True(opened.IsSuccess, $"Open from new file failed: {opened.Error}");
        Assert.Equal(outcome.HeadCommitId, opened.Value!.HeadId);
        Assert.Equal(FrameSource.CrossFileSnapshot, GetLatestObjectMapFrameSource(dstFile));
    }

    [Fact]
    public void SaveAs_PreservesDataAndCanContinue() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = new Revision(srcFile);
        var root = rev.CreateDict<int, double>();
        root.Upsert(1, 3.14);
        root.Upsert(2, 2.718);
        _ = AssertCommitSucceeded(rev.Commit(root));

        var saveAsResult = rev.SaveAs(root, dstFile);
        var outcome = AssertCommitSucceeded(saveAsResult, "SaveAs");

        // 从新文件打开并验证数据
        var opened = Revision.Open(outcome.HeadCommitId, dstFile);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");
        var loaded = Assert.IsAssignableFrom<DurableDict<int, double>>(opened.Value!.Load(root.LocalId).Value);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(1, out double v1));
        Assert.Equal(3.14, v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out double v2));
        Assert.Equal(2.718, v2);

        // SaveAs 后可以继续在新文件上 Commit
        root.Upsert(3, 1.414);
        var outcome2 = AssertCommitSucceeded(rev.Commit(root), "CommitAfterSaveAs");
        Assert.NotEqual(outcome.HeadCommitId, outcome2.HeadCommitId);
        Assert.Equal(FrameSource.PrimaryCommit, GetLatestObjectMapFrameSource(dstFile));

        var opened2 = Revision.Open(outcome2.HeadCommitId, dstFile);
        Assert.True(opened2.IsSuccess, $"Open after second commit failed: {opened2.Error}");
        var loaded2 = Assert.IsAssignableFrom<DurableDict<int, double>>(opened2.Value!.Load(root.LocalId).Value);
        Assert.Equal(3, loaded2.Count);
    }

    [Fact]
    public void SaveAs_WithChildObjects_ExportsEntireGraphAndSwitches() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = new Revision(srcFile);
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var child = rev.CreateDict<int, int>();
        child.Upsert(1, 10);
        root.Upsert(1, child);
        _ = AssertCommitSucceeded(rev.Commit(root));

        var outcome = AssertCommitSucceeded(rev.SaveAs(root, dstFile), "SaveAs");

        // 子对象从新文件可读
        var opened = Revision.Open(outcome.HeadCommitId, dstFile);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");
        var loadChild = opened.Value!.Load(child.LocalId);
        Assert.True(loadChild.IsSuccess, $"Load child failed: {loadChild.Error}");
        var lc = Assert.IsAssignableFrom<DurableDict<int, int>>(loadChild.Value);
        Assert.Equal(GetIssue.None, lc.Get(1, out int v));
        Assert.Equal(10, v);

        // 继续修改子对象并 Commit（应写入新文件）
        child.Upsert(2, 20);
        var outcome2 = AssertCommitSucceeded(rev.Commit(root), "CommitAfterSaveAs");

        var opened2 = Revision.Open(outcome2.HeadCommitId, dstFile);
        Assert.True(opened2.IsSuccess, $"Open after commit failed: {opened2.Error}");
        var lc2 = Assert.IsAssignableFrom<DurableDict<int, int>>(opened2.Value!.Load(child.LocalId).Value);
        Assert.Equal(2, lc2.Count);
    }

    [Fact]
    public void SaveAs_NewFile_HasSingleObjectMapFrame() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = new Revision(srcFile);
        var root = rev.CreateDict<int, int>();
        root.Upsert(1, 1);
        // 多次 Commit 产生多个 ObjectMap 帧
        _ = AssertCommitSucceeded(rev.Commit(root));
        root.Upsert(2, 2);
        _ = AssertCommitSucceeded(rev.Commit(root));
        root.Upsert(3, 3);
        _ = AssertCommitSucceeded(rev.Commit(root));
        Assert.True(CountObjectMapFrames(srcFile) >= 3);

        _ = AssertCommitSucceeded(rev.SaveAs(root, dstFile), "SaveAs");

        // 新文件应只有 1 个 ObjectMap 帧（全量 rebase）
        Assert.Equal(1, CountObjectMapFrames(dstFile));
    }
}
