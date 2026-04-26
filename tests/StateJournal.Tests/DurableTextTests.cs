using Atelia.Rbf;
using Xunit;

namespace Atelia.StateJournal.Tests;

public class DurableTextTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), $"text-test-{Guid.NewGuid()}");
        _tempFiles.Add(path);
        return path;
    }

    private static Revision CreateRevision(uint segmentNumber = 1) => new(segmentNumber);

    private static AteliaResult<CommitOutcome> CommitToFile(
        Revision revision, DurableObject graphRoot, IRbfFile file, uint segmentNumber = 1
    ) {
        var result = revision.Commit(graphRoot, file);
        if (result.IsSuccess) { revision.AcceptPersistedSegment(segmentNumber); }
        return result;
    }

    private static AteliaResult<Revision> OpenRevision(
        CommitTicket commitTicket, IRbfFile file, uint segmentNumber = 1
    ) => Revision.Open(commitTicket, file, segmentNumber);

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { if (File.Exists(path)) { File.Delete(path); } } catch { }
        }
    }

    #region Basic API

    [Fact]
    public void CreateText_IsEmpty() {
        var rev = CreateRevision();
        var text = rev.CreateText();
        Assert.Equal(0, text.BlockCount);
        Assert.Empty(text.GetAllBlocks());
    }

    [Fact]
    public void AppendLine_AddsToEnd() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("first");
        var id2 = text.Append("second");
        var id3 = text.Append("third");

        Assert.Equal(3, text.BlockCount);
        var lines = text.GetAllBlocks();
        Assert.Equal(3, lines.Count);
        Assert.Equal(new TextBlock(id1, "first"), lines[0]);
        Assert.Equal(new TextBlock(id2, "second"), lines[1]);
        Assert.Equal(new TextBlock(id3, "third"), lines[2]);
    }

    [Fact]
    public void PrependLine_AddsToFront() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("original");
        var id2 = text.Prepend("prepended");

        var lines = text.GetAllBlocks();
        Assert.Equal(2, lines.Count);
        Assert.Equal("prepended", lines[0].Content);
        Assert.Equal("original", lines[1].Content);
        Assert.Equal(id2, lines[0].Id);
        Assert.Equal(id1, lines[1].Id);
    }

    [Fact]
    public void InsertAfter_InsertsInMiddle() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("first");
        var id3 = text.Append("third");
        var id2 = text.InsertAfter(id1, "second");

        var lines = text.GetAllBlocks();
        Assert.Equal(3, lines.Count);
        Assert.Equal("first", lines[0].Content);
        Assert.Equal("second", lines[1].Content);
        Assert.Equal("third", lines[2].Content);
        Assert.Equal(id2, lines[1].Id);
    }

    [Fact]
    public void InsertAfter_Tail_AppendsCorrectly() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("first");
        var id2 = text.InsertAfter(id1, "second");
        // After InsertAfter tail, further AppendLine should still work
        var id3 = text.Append("third");

        var lines = text.GetAllBlocks();
        Assert.Equal(3, lines.Count);
        Assert.Equal("first", lines[0].Content);
        Assert.Equal("second", lines[1].Content);
        Assert.Equal("third", lines[2].Content);
    }

    [Fact]
    public void InsertBefore_Head_PrependsCorrectly() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id2 = text.Append("second");
        var id1 = text.InsertBefore(id2, "first");

        var lines = text.GetAllBlocks();
        Assert.Equal(2, lines.Count);
        Assert.Equal(new TextBlock(id1, "first"), lines[0]);
        Assert.Equal(new TextBlock(id2, "second"), lines[1]);
    }

    [Fact]
    public void InsertBefore_Middle_InsertsCorrectly() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("first");
        var id3 = text.Append("third");
        var id2 = text.InsertBefore(id3, "second");

        var lines = text.GetAllBlocks();
        Assert.Equal(3, lines.Count);
        Assert.Equal(new TextBlock(id1, "first"), lines[0]);
        Assert.Equal(new TextBlock(id2, "second"), lines[1]);
        Assert.Equal(new TextBlock(id3, "third"), lines[2]);
    }

    [Fact]
    public void SetLine_ReplacesContent() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("old content");
        text.SetContent(id1, "new content");

        var line = text.GetBlock(id1);
        Assert.Equal(id1, line.Id);
        Assert.Equal("new content", line.Content);
    }

    [Fact]
    public void DeleteNode_Head() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("first");
        var id2 = text.Append("second");
        text.Delete(id1);

        Assert.Equal(1, text.BlockCount);
        var lines = text.GetAllBlocks();
        Assert.Single(lines);
        Assert.Equal("second", lines[0].Content);
    }

    [Fact]
    public void DeleteNode_Middle() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("first");
        var id2 = text.Append("second");
        var id3 = text.Append("third");
        text.Delete(id2);

        Assert.Equal(2, text.BlockCount);
        var lines = text.GetAllBlocks();
        Assert.Equal("first", lines[0].Content);
        Assert.Equal("third", lines[1].Content);
    }

    [Fact]
    public void DeleteNode_Tail() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("first");
        var id2 = text.Append("second");
        text.Delete(id2);

        Assert.Equal(1, text.BlockCount);
        // After deleting tail, AppendLine should still work
        var id3 = text.Append("third");
        var lines = text.GetAllBlocks();
        Assert.Equal(2, lines.Count);
        Assert.Equal("first", lines[0].Content);
        Assert.Equal("third", lines[1].Content);
    }

    [Fact]
    public void DeletedNodeId_GetLine_Throws() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("first");
        var id2 = text.Append("second");
        text.Delete(id2);

        Assert.Throws<KeyNotFoundException>(() => text.GetBlock(id2));
        Assert.Equal("first", text.GetBlock(id1).Content);
    }

    [Fact]
    public void DeletedNodeId_SetLine_Throws() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        text.Append("first");
        var deletedId = text.Append("second");
        text.Delete(deletedId);

        Assert.Throws<KeyNotFoundException>(() => text.SetContent(deletedId, "updated"));
    }

    [Fact]
    public void DeletedNodeId_InsertAfter_Throws() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        text.Append("first");
        var deletedId = text.Append("second");
        text.Append("third");
        text.Delete(deletedId);

        Assert.Throws<KeyNotFoundException>(() => text.InsertAfter(deletedId, "should fail"));
    }

    [Fact]
    public void DeletedNodeId_InsertBefore_Throws() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        text.Append("first");
        var deletedId = text.Append("second");
        text.Append("third");
        text.Delete(deletedId);

        Assert.Throws<KeyNotFoundException>(() => text.InsertBefore(deletedId, "should fail"));
    }

    [Fact]
    public void DeletedNodeId_GetLinesFrom_Throws() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        text.Append("first");
        var deletedId = text.Append("second");
        text.Append("third");
        text.Delete(deletedId);

        Assert.Throws<KeyNotFoundException>(() => text.GetBlocksFrom(deletedId, 2));
    }

    [Fact]
    public void DeleteNode_Middle_ThenDeleteOriginalSuccessor_Works() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        text.Append("a");
        var b = text.Append("b");
        var c = text.Append("c");
        text.Append("d");

        text.Delete(b);
        text.Delete(c);

        Assert.Equal(["a", "d"], text.GetAllBlocks().Select(line => line.Content));
    }

    [Fact]
    public void LoadLines_BulkLoad() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        text.LoadBlocks(["line1", "line2", "line3"]);

        Assert.Equal(3, text.BlockCount);
        var lines = text.GetAllBlocks();
        Assert.Equal("line1", lines[0].Content);
        Assert.Equal("line2", lines[1].Content);
        Assert.Equal("line3", lines[2].Content);
    }

    [Fact]
    public void LoadText_SplitsByNewline() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        text.LoadText("hello\nworld");

        Assert.Equal(2, text.BlockCount);
        var lines = text.GetAllBlocks();
        Assert.Equal("hello", lines[0].Content);
        Assert.Equal("world", lines[1].Content);
    }

    [Fact]
    public void GetLinesFrom_ReturnsSubset() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("first");
        var id2 = text.Append("second");
        var id3 = text.Append("third");
        var id4 = text.Append("fourth");

        var subset = text.GetBlocksFrom(id2, 2);
        Assert.Equal(2, subset.Count);
        Assert.Equal("second", subset[0].Content);
        Assert.Equal("third", subset[1].Content);
    }

    [Fact]
    public void NodeIds_AreStable_AcrossInsertions() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("alpha");
        var id2 = text.Append("beta");

        // Insert between
        text.InsertAfter(id1, "gamma");

        // Original IDs still work
        Assert.Equal("alpha", text.GetBlock(id1).Content);
        Assert.Equal("beta", text.GetBlock(id2).Content);
    }

    [Fact]
    public void NodeIds_AreStable_AcrossDeletions() {
        var rev = CreateRevision();
        var text = rev.CreateText();

        var id1 = text.Append("alpha");
        var id2 = text.Append("beta");
        var id3 = text.Append("gamma");

        text.Delete(id2);

        // Remaining IDs still work
        Assert.Equal("alpha", text.GetBlock(id1).Content);
        Assert.Equal("gamma", text.GetBlock(id3).Content);
    }

    #endregion

    #region Persistence Round-Trip

    [Fact]
    public void CommitAndOpen_RoundTrips() {
        var filePath = GetTempFilePath();
        using var file = RbfFile.CreateNew(filePath);

        // Create and commit
        var rev = CreateRevision();
        var text = rev.CreateText();
        text.LoadBlocks(["line1", "line2", "line3"]);

        var commitResult = CommitToFile(rev, text, file);
        Assert.True(commitResult.IsSuccess, $"Commit failed: {commitResult.Error}");
        var outcome = commitResult.Value;

        // Open and verify
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableText>(openResult.Value!.GraphRoot);
        Assert.Equal(3, loaded.BlockCount);

        var lines = loaded.GetAllBlocks();
        Assert.Equal("line1", lines[0].Content);
        Assert.Equal("line2", lines[1].Content);
        Assert.Equal("line3", lines[2].Content);
    }

    [Fact]
    public void CommitAndOpen_PreservesNodeIds() {
        var filePath = GetTempFilePath();
        using var file = RbfFile.CreateNew(filePath);

        var rev = CreateRevision();
        var text = rev.CreateText();
        var id1 = text.Append("alpha");
        var id2 = text.Append("beta");

        var commitResult = CommitToFile(rev, text, file);
        Assert.True(commitResult.IsSuccess);
        var outcome = commitResult.Value;

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableText>(openResult.Value!.GraphRoot);
        var lines = loaded.GetAllBlocks();
        Assert.Equal(id1, lines[0].Id);
        Assert.Equal(id2, lines[1].Id);
    }

    [Fact]
    public void DeltaCommit_AfterEdit_RoundTrips() {
        var filePath = GetTempFilePath();
        using var file = RbfFile.CreateNew(filePath);

        // Initial commit
        var rev = CreateRevision();
        var text = rev.CreateText();
        text.LoadBlocks(["line1", "line2", "line3"]);
        var result1 = CommitToFile(rev, text, file);
        Assert.True(result1.IsSuccess);

        // Edit and delta commit
        var id2 = text.GetAllBlocks()[1].Id;
        text.SetContent(id2, "LINE2-MODIFIED");
        text.Append("line4");
        var result2 = CommitToFile(rev, text, file);
        Assert.True(result2.IsSuccess);

        // Reopen and verify
        var openResult = OpenRevision(result2.Value.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableText>(openResult.Value!.GraphRoot);
        Assert.Equal(4, loaded.BlockCount);
        var lines = loaded.GetAllBlocks();
        Assert.Equal("line1", lines[0].Content);
        Assert.Equal("LINE2-MODIFIED", lines[1].Content);
        Assert.Equal("line3", lines[2].Content);
        Assert.Equal("line4", lines[3].Content);
    }

    [Fact]
    public void DeltaCommit_WithDelete_RoundTrips() {
        var filePath = GetTempFilePath();
        using var file = RbfFile.CreateNew(filePath);

        // Initial commit
        var rev = CreateRevision();
        var text = rev.CreateText();
        text.LoadBlocks(["a", "b", "c", "d"]);
        var result1 = CommitToFile(rev, text, file);
        Assert.True(result1.IsSuccess);

        // Delete middle lines and commit
        var lines = text.GetAllBlocks();
        text.Delete(lines[1].Id); // "b"
        text.Delete(lines[2].Id); // "c"
        var result2 = CommitToFile(rev, text, file);
        Assert.True(result2.IsSuccess);

        // Reopen
        var openResult = OpenRevision(result2.Value.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableText>(openResult.Value!.GraphRoot);
        Assert.Equal(2, loaded.BlockCount);
        var loadedLines = loaded.GetAllBlocks();
        Assert.Equal("a", loadedLines[0].Content);
        Assert.Equal("d", loadedLines[1].Content);
    }

    [Fact]
    public void DeltaCommit_AfterDeleteAndValidAppend_Succeeds() {
        var filePath = GetTempFilePath();
        using var file = RbfFile.CreateNew(filePath);

        var rev = CreateRevision();
        var text = rev.CreateText();
        text.LoadBlocks(["a", "b", "c"]);
        var result1 = CommitToFile(rev, text, file);
        Assert.True(result1.IsSuccess);

        var deletedId = text.GetAllBlocks()[1].Id;
        text.Delete(deletedId);
        text.Append("d");

        var result2 = CommitToFile(rev, text, file);
        Assert.True(result2.IsSuccess, $"Commit failed: {result2.Error}");

        var openResult = OpenRevision(result2.Value.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableText>(openResult.Value!.GraphRoot);
        Assert.Equal(["a", "c", "d"], loaded.GetAllBlocks().Select(line => line.Content));
    }

    [Fact]
    public void DeltaCommit_WithInsert_RoundTrips() {
        var filePath = GetTempFilePath();
        using var file = RbfFile.CreateNew(filePath);

        // Initial commit
        var rev = CreateRevision();
        var text = rev.CreateText();
        text.LoadBlocks(["first", "last"]);
        var result1 = CommitToFile(rev, text, file);
        Assert.True(result1.IsSuccess);

        // Insert in middle and commit
        var firstId = text.GetAllBlocks()[0].Id;
        text.InsertAfter(firstId, "middle");
        var result2 = CommitToFile(rev, text, file);
        Assert.True(result2.IsSuccess);

        // Reopen
        var openResult = OpenRevision(result2.Value.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableText>(openResult.Value!.GraphRoot);
        Assert.Equal(3, loaded.BlockCount);
        var loadedLines = loaded.GetAllBlocks();
        Assert.Equal("first", loadedLines[0].Content);
        Assert.Equal("middle", loadedLines[1].Content);
        Assert.Equal("last", loadedLines[2].Content);
    }

    [Fact]
    public void DeltaCommit_WithInsertBefore_RoundTrips() {
        var filePath = GetTempFilePath();
        using var file = RbfFile.CreateNew(filePath);

        var rev = CreateRevision();
        var text = rev.CreateText();
        text.LoadBlocks(["first", "third"]);
        var result1 = CommitToFile(rev, text, file);
        Assert.True(result1.IsSuccess);

        var thirdId = text.GetAllBlocks()[1].Id;
        text.InsertBefore(thirdId, "second");
        var result2 = CommitToFile(rev, text, file);
        Assert.True(result2.IsSuccess);

        var openResult = OpenRevision(result2.Value.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableText>(openResult.Value!.GraphRoot);
        Assert.Equal(["first", "second", "third"], loaded.GetAllBlocks().Select(line => line.Content));
    }

    #endregion

    #region Nesting

    [Fact]
    public void DurableText_AsNestedValue_InDict() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<string, DurableText>();
        var text = rev.CreateText();
        text.Append("hello from nested text");

        dict.Upsert("doc1", text);

        var got = dict.Get("doc1");
        Assert.Same(text, got);
        Assert.NotNull(got);
        Assert.Equal(1, got.BlockCount);
        Assert.Equal("hello from nested text", got.GetAllBlocks()[0].Content);
    }

    [Fact]
    public void DurableText_AsMixedDictValue_HasTextValueKind() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<string>();
        var text = rev.CreateText();
        text.Append("hello");

        dict.Upsert("doc1", text);

        Assert.True(dict.TryGetValueKind("doc1", out var kind));
        Assert.Equal(ValueKind.Text, kind);
        Assert.True(dict.TryGet("doc1", out DurableText? got));
        Assert.Same(text, got);
    }

    [Fact]
    public void DurableText_AsMixedDequeValue_HasTextValueKind() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();
        var text = rev.CreateText();
        text.Append("hello");

        deque.PushBack(text);

        Assert.True(deque.TryPeekFrontValueKind(out var frontKind));
        Assert.Equal(ValueKind.Text, frontKind);
        Assert.True(deque.TryPeekBackValueKind(out var backKind));
        Assert.Equal(ValueKind.Text, backKind);
        Assert.True(deque.TryPeekFront<DurableText>(out var got));
        Assert.Same(text, got);
    }

    #endregion

    #region HasChanges / Revert

    [Fact]
    public void HasChanges_TrueAfterEdit() {
        var rev = CreateRevision();
        var text = rev.CreateText();
        Assert.False(text.HasChanges);

        text.Append("something");
        Assert.True(text.HasChanges);
    }

    #endregion
}
