using Atelia.Rbf;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void Commit_ValueTuple2Dict_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, ValueTuple<int, int>>();
        root.Upsert(10, new ValueTuple<int, int>(3, 7));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, ValueTuple<int, int>>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(10, out ValueTuple<int, int> value));
        Assert.Equal(3, value.Item1);
        Assert.Equal(7, value.Item2);
    }

    [Fact]
    public void Commit_ValueTuple3Deque_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque<ValueTuple<int, int, int>>();
        root.PushBack(new ValueTuple<int, int, int>(1, 2, 3));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque<ValueTuple<int, int, int>>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.GetAt(0, out ValueTuple<int, int, int> value));
        Assert.Equal(1, value.Item1);
        Assert.Equal(2, value.Item2);
        Assert.Equal(3, value.Item3);
    }

    [Fact]
    public void Commit_ValueTupleWithString_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, ValueTuple<int, string>>();
        root.Upsert(5, new ValueTuple<int, string>(9, "tuple-symbol"));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, ValueTuple<int, string>>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(5, out ValueTuple<int, string> value));
        Assert.Equal(9, value.Item1);
        Assert.Equal("tuple-symbol", value.Item2);
    }

    [Fact]
    public void Commit_ValueTuple7Dict_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, ValueTuple<int, int, int, int, int, int, int>>();
        root.Upsert(42, new ValueTuple<int, int, int, int, int, int, int>(1, 2, 3, 4, 5, 6, 7));

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, ValueTuple<int, int, int, int, int, int, int>>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(42, out ValueTuple<int, int, int, int, int, int, int> value));
        Assert.Equal(1, value.Item1);
        Assert.Equal(2, value.Item2);
        Assert.Equal(3, value.Item3);
        Assert.Equal(4, value.Item4);
        Assert.Equal(5, value.Item5);
        Assert.Equal(6, value.Item6);
        Assert.Equal(7, value.Item7);
    }
}
