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
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v1)); Assert.Equal("alpha", v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2)); Assert.Equal("beta", v2);
        Assert.Equal(GetIssue.None, loaded.Get(3, out var v3)); Assert.Equal("alpha", v3);
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
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v1)); Assert.Equal("first", v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2)); Assert.Equal("second", v2);
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
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v1)); Assert.Equal("", v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2)); Assert.Equal("non-empty", v2);
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
}
