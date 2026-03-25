using Atelia.Rbf;
using Xunit;

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
        var dict = rev.CreateDict<int, double>();
        dict.Upsert(1, 1.0);

        // Intern 几个 symbol
        var sym1 = rev.InternSymbol("alpha");
        var sym2 = rev.InternSymbol("beta");
        var sym3 = rev.InternSymbol("alpha"); // dedup

        Assert.Equal(sym1, sym3);
        Assert.NotEqual(sym1, sym2);

        // Commit
        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        // Open from persisted data
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;

        // Verify round-tripped symbols
        var loadedSym1 = loaded.GetSymbol(sym1);
        var loadedSym2 = loaded.GetSymbol(sym2);
        Assert.Equal("alpha", loadedSym1);
        Assert.Equal("beta", loadedSym2);
    }

    [Fact]
    public void Commit_WithSymbols_MultipleCommits_SymbolsPersist() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateDict<int, double>();
        dict.Upsert(1, 1.0);

        // First commit with one symbol
        var sym1 = rev.InternSymbol("first");
        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        // Second commit with another symbol
        var sym2 = rev.InternSymbol("second");
        dict.Upsert(2, 2.0);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        // Open latest commit
        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        Assert.Equal("first", loaded.GetSymbol(sym1));
        Assert.Equal("second", loaded.GetSymbol(sym2));
    }

    [Fact]
    public void Commit_EmptyString_PersistsAndRoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateDict<int, double>();
        dict.Upsert(1, 1.0);

        var emptyId = rev.InternSymbol("");
        Assert.False(emptyId.IsNull);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        Assert.Equal("", loaded.GetSymbol(emptyId));
        Assert.Null(loaded.GetSymbol(SymbolId.Null));
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
        var dict = rev.CreateDict<int, double>();
        dict.Upsert(0, 0.0);

        const int symbolCount = 50;
        var ids = new SymbolId[symbolCount];
        for (int i = 0; i < symbolCount; i++) {
            ids[i] = rev.InternSymbol($"symbol_{i}");
        }

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        for (int i = 0; i < symbolCount; i++) {
            Assert.Equal($"symbol_{i}", loaded.GetSymbol(ids[i]));
        }
    }
}
