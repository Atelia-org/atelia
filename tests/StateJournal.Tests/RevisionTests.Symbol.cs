using Atelia.Rbf;
using Xunit;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void InternSymbol_ReturnsNonNullId() {
        var rev = CreateRevision();
        var id = rev.InternSymbol("hello");
        Assert.False(id.IsNull);
    }

    [Fact]
    public void InternSymbol_DuplicateReturnsSameId() {
        var rev = CreateRevision();
        var id1 = rev.InternSymbol("foo");
        var id2 = rev.InternSymbol("foo");
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void InternSymbol_DifferentStringsReturnDifferentIds() {
        var rev = CreateRevision();
        var id1 = rev.InternSymbol("aaa");
        var id2 = rev.InternSymbol("bbb");
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GetSymbol_ReturnsInternedString() {
        var rev = CreateRevision();
        var id = rev.InternSymbol("world");
        var result = rev.GetSymbol(id);
        Assert.Equal("world", result);
    }

    [Fact]
    public void InternSymbol_EmptyString_IsDistinctFromNull() {
        var rev = CreateRevision();
        var id = rev.InternSymbol("");
        Assert.False(id.IsNull);
        Assert.NotEqual(SymbolId.Null, id);
        Assert.Equal("", rev.GetSymbol(id));
    }

    [Fact]
    public void SymbolId_Null_IsDefault() {
        Assert.True(SymbolId.Null.IsNull);
        Assert.Equal(0u, SymbolId.Null.Value);
    }

    [Fact]
    public void InternSymbol_Null_ReturnsNullId() {
        var rev = CreateRevision();
        var id = rev.InternSymbol(null);
        Assert.Equal(SymbolId.Null, id);
        Assert.Null(rev.GetSymbol(id));
    }

    [Fact]
    public void Commit_WithSymbols_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        // 使用 TypedDict<int, string> 使 symbol 在 DFS 中可达
        var dict = rev.CreateDict<int, string>();

        dict.Upsert(1, "alpha");
        dict.Upsert(2, "beta");
        dict.Upsert(3, "alpha"); // 与 key 1 dedup

        // Commit
        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        // Open from persisted data
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v1));
        Assert.Equal("alpha", v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2));
        Assert.Equal("beta", v2);
        Assert.Equal(GetIssue.None, loaded.Get(3, out var v3));
        Assert.Equal("alpha", v3);
    }

    [Fact]
    public void Commit_WithSymbols_MultipleCommits_SymbolsPersist() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateDict<int, string>();

        // First commit with one symbol
        dict.Upsert(1, "first");
        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        // Second commit with another symbol
        dict.Upsert(2, "second");
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        // Open latest commit
        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v1));
        Assert.Equal("first", v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2));
        Assert.Equal("second", v2);
    }

    [Fact]
    public void SymbolTable_TracksCommittedMirror_NotRuntimeWorkingSet() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateDict<int, string>();
        dict.Upsert(1, "first");

        _ = AssertCommitSucceeded(CommitToFile(rev, dict, file));
        Assert.Equal(1, GetSymbolTableCount(rev));

        dict.Upsert(2, "second");
        Assert.Equal(1, GetSymbolTableCount(rev));

        _ = AssertCommitSucceeded(CommitToFile(rev, dict, file));
        Assert.Equal(2, GetSymbolTableCount(rev));
    }

    [Fact]
    public void Commit_EmptyString_PersistsAndRoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        // 使用 TypedDict<int, string> 使 empty string symbol 可达
        var dict = rev.CreateDict<int, string>();
        dict.Upsert(1, "");
        dict.Upsert(2, "non-empty");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v1));
        Assert.Equal("", v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2));
        Assert.Equal("non-empty", v2);
    }

    [Fact]
    public void InternSymbol_ManyStrings_AllRetrievable() {
        var rev = CreateRevision();
        var ids = new SymbolId[100];
        for (int i = 0; i < 100; i++) {
            ids[i] = rev.InternSymbol($"sym_{i}");
        }
        for (int i = 0; i < 100; i++) {
            Assert.Equal($"sym_{i}", rev.GetSymbol(ids[i]));
        }
    }

    [Fact]
    public void Commit_WithManySymbols_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        // 使用 TypedDict<int, string> 使 symbol 可达
        var dict = rev.CreateDict<int, string>();

        const int symbolCount = 50;
        for (int i = 0; i < symbolCount; i++) {
            dict.Upsert(i, $"symbol_{i}");
        }

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        for (int i = 0; i < symbolCount; i++) {
            Assert.Equal(GetIssue.None, loaded.Get(i, out var v));
            Assert.Equal($"symbol_{i}", v);
        }
    }

    [Fact]
    public void Commit_WithTypedDequeOfString_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var deque = rev.CreateDeque<string>();
        deque.PushBack("alpha");
        deque.PushBack("beta");
        deque.PushFront("zero");
        deque.PushBack(null);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, deque, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque<string>>(openResult.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.GetAt(0, out string? v0));
        Assert.Equal("zero", v0);
        Assert.Equal(GetIssue.None, loaded.GetAt(1, out string? v1));
        Assert.Equal("alpha", v1);
        Assert.Equal(GetIssue.None, loaded.GetAt(2, out string? v2));
        Assert.Equal("beta", v2);
        Assert.Equal(GetIssue.None, loaded.GetAt(3, out string? v3));
        Assert.Null(v3);
    }

    [Fact]
    public void Commit_WithTypedDictStringKey_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateDict<string, int>();
        dict.Upsert("alpha", 1);
        dict.Upsert("beta", 2);
        dict.Upsert("你好", 3);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<string, int>>(openResult.Value!.GraphRoot);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get("alpha", out int a));
        Assert.Equal(1, a);
        Assert.Equal(GetIssue.None, loaded.Get("beta", out int b));
        Assert.Equal(2, b);
        Assert.Equal(GetIssue.None, loaded.Get("你好", out int c));
        Assert.Equal(3, c);
    }

    [Fact]
    public void Commit_UnchangedTypedStringChild_PreservesValuesAcrossLaterCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        var stringChild = rev.CreateDict<int, string>();
        stringChild.Upsert(1, "alpha");
        stringChild.Upsert(2, "beta");
        var intChild = rev.CreateDict<int, int>();
        intChild.Upsert(1, 1);

        root.Upsert(1, stringChild);
        root.Upsert(2, intChild);

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        intChild.Upsert(2, 2);

        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableObject? stringChildObj));
        var loadedStringChild = Assert.IsAssignableFrom<DurableDict<int, string>>(stringChildObj);
        Assert.Equal(GetIssue.None, loadedStringChild.Get(1, out string? v1));
        Assert.Equal("alpha", v1);
        Assert.Equal(GetIssue.None, loadedStringChild.Get(2, out string? v2));
        Assert.Equal("beta", v2);
    }

    [Fact]
    public void Commit_UnchangedTypedStringKeyChild_PreservesKeysAcrossLaterCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        var keyedChild = rev.CreateDict<string, int>();
        keyedChild.Upsert("alpha", 1);
        keyedChild.Upsert("beta", 2);
        var intChild = rev.CreateDict<int, int>();
        intChild.Upsert(1, 1);

        root.Upsert(1, keyedChild);
        root.Upsert(2, intChild);

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        intChild.Upsert(2, 2);

        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableObject? keyedChildObj));
        var loadedKeyedChild = Assert.IsAssignableFrom<DurableDict<string, int>>(keyedChildObj);
        Assert.Equal(GetIssue.None, loadedKeyedChild.Get("alpha", out int a));
        Assert.Equal(1, a);
        Assert.Equal(GetIssue.None, loadedKeyedChild.Get("beta", out int b));
        Assert.Equal(2, b);
    }

    [Fact]
    public void Commit_TypedStringValueDeltaReplay_CanLoadAfterRemovedHistoricalStringFallsOutOfHeadSymbolTable() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        for (int i = 0; i < 6; i++) {
            root.Upsert(i, $"value_{i}");
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Remove(0);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var frameInfo = file.ReadFrameInfo(root.HeadTicket);
        Assert.True(frameInfo.IsSuccess, $"ReadFrameInfo failed: {frameInfo.Error}");
        Assert.Equal(VersionKind.Delta, new FrameTag(frameInfo.Value.Tag).VersionKind);

        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(5, loaded.Count);
        Assert.Equal(GetIssue.NotFound, loaded.Get(0, out _));
        for (int i = 1; i < 6; i++) {
            Assert.Equal(GetIssue.None, loaded.Get(i, out string? value));
            Assert.Equal($"value_{i}", value);
        }
    }

    [Fact]
    public void Commit_TypedStringKeyDeltaReplay_CanLoadAfterRemovedHistoricalKeyFallsOutOfHeadSymbolTable() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<string, int>();
        for (int i = 0; i < 6; i++) {
            root.Upsert($"key_{i}", i);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Remove("key_0");
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var frameInfo = file.ReadFrameInfo(root.HeadTicket);
        Assert.True(frameInfo.IsSuccess, $"ReadFrameInfo failed: {frameInfo.Error}");
        Assert.Equal(VersionKind.Delta, new FrameTag(frameInfo.Value.Tag).VersionKind);

        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<string, int>>(openResult.Value!.GraphRoot);
        Assert.Equal(5, loaded.Count);
        Assert.Equal(GetIssue.NotFound, loaded.Get("key_0", out _));
        for (int i = 1; i < 6; i++) {
            Assert.Equal(GetIssue.None, loaded.Get($"key_{i}", out int value));
            Assert.Equal(i, value);
        }
    }

    [Fact]
    public void Commit_MixedStringValueDeltaReplay_CanLoadAfterRemovedHistoricalStringFallsOutOfHeadSymbolTable() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        for (int i = 0; i < 6; i++) {
            root.OfString.Upsert(i, $"value_{i}");
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        root.Remove(0);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var frameInfo = file.ReadFrameInfo(root.HeadTicket);
        Assert.True(frameInfo.IsSuccess, $"ReadFrameInfo failed: {frameInfo.Error}");
        Assert.Equal(VersionKind.Delta, new FrameTag(frameInfo.Value.Tag).VersionKind);

        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(5, loaded.Count);
        Assert.Equal(GetIssue.NotFound, loaded.OfString.Get(0, out _));
        for (int i = 1; i < 6; i++) {
            Assert.Equal(GetIssue.None, loaded.OfString.Get(i, out string? value));
            Assert.Equal($"value_{i}", value);
        }
    }

    [Fact]
    public void Commit_MixedDequeStringDeltaReplay_CanLoadAfterRemovedHistoricalStringFallsOutOfHeadSymbolTable() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();
        for (int i = 0; i < 6; i++) {
            root.OfString.PushBack($"value_{i}");
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        Assert.Equal(GetIssue.None, root.OfString.PopFront(out string? removed));
        Assert.Equal("value_0", removed);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var frameInfo = file.ReadFrameInfo(root.HeadTicket);
        Assert.True(frameInfo.IsSuccess, $"ReadFrameInfo failed: {frameInfo.Error}");
        Assert.Equal(VersionKind.Delta, new FrameTag(frameInfo.Value.Tag).VersionKind);

        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(5, loaded.Count);
        for (int i = 0; i < 5; i++) {
            Assert.Equal(GetIssue.None, loaded.OfString.GetAt(i, out string? value));
            Assert.Equal($"value_{i + 1}", value);
        }
    }
}
