using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

/// <summary>
/// DurableOrderedDict 端到端集成测试：工厂 → Revision 绑定 → Commit → Open → Load 全链路。
/// </summary>
public class DurableOrderedDictTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), $"ordered-dict-test-{Guid.NewGuid()}");
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

    private static CommitOutcome AssertCommitSucceeded(AteliaResult<CommitOutcome> result, string label = "Commit") {
        Assert.True(result.IsSuccess, $"{label} failed: {result.Error}");
        return result.Value;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { if (File.Exists(path)) { File.Delete(path); } } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Factory / Creation
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_CreatesInstance_WithCorrectKind() {
        var dict = Durable.OrderedDict<int, double>();
        Assert.Equal(DurableObjectKind.TypedOrderedDict, dict.Kind);
    }

    [Fact]
    public void Revision_CreateOrderedDict_BindsCorrectly() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int, double>();
        Assert.Equal(rev, dict.Revision);
        Assert.False(dict.LocalId.IsNull);
        Assert.Equal(DurableState.TransientDirty, dict.State);
    }

    // ──────────────────────────────────────────────────────────────
    // Basic CRUD through Public API
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Upsert_TryGet_Remove_BasicFlow() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int, int>();

        dict.Upsert(10, 100);
        dict.Upsert(20, 200);
        dict.Upsert(30, 300);
        Assert.Equal(3, dict.Count);

        Assert.True(dict.TryGet(20, out var v));
        Assert.Equal(200, v);

        Assert.True(dict.Remove(20));
        Assert.Equal(2, dict.Count);
        Assert.False(dict.TryGet(20, out _));
    }

    [Fact]
    public void Keys_ReturnsOrderedKeys() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int, int>();

        dict.Upsert(30, 3);
        dict.Upsert(10, 1);
        dict.Upsert(20, 2);

        var keys = dict.GetKeys();
        Assert.Equal(3, keys.Count);
        Assert.Equal(10, keys[0]);
        Assert.Equal(20, keys[1]);
        Assert.Equal(30, keys[2]);
    }

    [Fact]
    public void ReadAscendingFrom_RangeQuery() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int, int>();

        for (int i = 1; i <= 10; i++) { dict.Upsert(i * 10, i * 100); }

        var result = dict.ReadAscendingFrom(35, 3);
        Assert.Equal(3, result.Count);
        Assert.Equal(40, result[0].Key);
        Assert.Equal(50, result[1].Key);
        Assert.Equal(60, result[2].Key);
    }

    [Fact]
    public void ReadAscendingFrom_NegativeMaxCount_ThrowsArgumentOutOfRange() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int, int>();
        dict.Upsert(10, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => dict.ReadAscendingFrom(10, -1));
    }

    // ──────────────────────────────────────────────────────────────
    // Commit → Open → Load round-trip
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Commit_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int, double>();
        dict.Upsert(10, 3.14);
        dict.Upsert(20, 2.718);
        dict.Upsert(5, 1.0);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int, double>>(loadResult.Value);

        Assert.Equal(3, loadedDict.Count);
        Assert.True(loadedDict.TryGet(5, out var v1));
        Assert.Equal(1.0, v1);
        Assert.True(loadedDict.TryGet(10, out var v2));
        Assert.Equal(3.14, v2);
        Assert.True(loadedDict.TryGet(20, out var v3));
        Assert.Equal(2.718, v3);

        // Keys should be ordered after reload
        var keys = loadedDict.GetKeys();
        Assert.Equal(new[] { 5, 10, 20 }, keys);
    }

    [Fact]
    public void MultipleCommits_DeltaChain_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int, int>();
        dict.Upsert(1, 10);
        dict.Upsert(2, 20);
        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");

        // 第二次提交：更新 + 新增
        dict.Upsert(2, 200);
        dict.Upsert(3, 30);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit2");

        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(loadResult.Value);

        Assert.Equal(3, loadedDict.Count);
        Assert.True(loadedDict.TryGet(1, out var v1));
        Assert.Equal(10, v1);
        Assert.True(loadedDict.TryGet(2, out var v2));
        Assert.Equal(200, v2);
        Assert.True(loadedDict.TryGet(3, out var v3));
        Assert.Equal(30, v3);
    }

    [Fact]
    public void Commit_WithRemove_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int, int>();
        dict.Upsert(1, 10);
        dict.Upsert(2, 20);
        dict.Upsert(3, 30);
        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");

        dict.Remove(2);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit2");

        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(loadResult.Value);

        Assert.Equal(2, loadedDict.Count);
        Assert.False(loadedDict.TryGet(2, out _));
        Assert.True(loadedDict.TryGet(1, out var v1));
        Assert.Equal(10, v1);
        Assert.True(loadedDict.TryGet(3, out var v3));
        Assert.Equal(30, v3);
    }

    [Fact]
    public void LargeDataSet_Commit_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int, int>();
        for (int i = 0; i < 200; i++) {
            dict.Upsert(i * 3, i * 100); // non-sequential keys
        }

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(loadResult.Value);

        Assert.Equal(200, loadedDict.Count);

        // Verify ordering
        var keys = loadedDict.GetKeys();
        for (int i = 1; i < keys.Count; i++) {
            Assert.True(keys[i] > keys[i - 1], $"Keys not ordered: {keys[i - 1]} >= {keys[i]}");
        }

        // Verify all values
        for (int i = 0; i < 200; i++) {
            Assert.True(loadedDict.TryGet(i * 3, out var v), $"Missing key {i * 3}");
            Assert.Equal(i * 100, v);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // OrderedDict as nested value in DurableDict
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void NestedAsValue_InMixedDict_Commit_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var root = rev.CreateDict<string>();
        var orderedDict = rev.CreateOrderedDict<int, int>();
        orderedDict.Upsert(1, 100);
        orderedDict.Upsert(2, 200);
        root.Upsert("scores", orderedDict);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(orderedDict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedOrdered = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(loadResult.Value);

        Assert.Equal(2, loadedOrdered.Count);
        Assert.True(loadedOrdered.TryGet(1, out var v1));
        Assert.Equal(100, v1);
    }

    // ──────────────────────────────────────────────────────────────
    // Regression: delete-commit-insert-reopen (sequence reuse bug)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteCommitInsert_Reopen_NoSequenceReuse() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int, int>();
        dict.Upsert(1, 10);
        dict.Upsert(2, 20);
        dict.Upsert(3, 30);
        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1"); // {1,2,3}

        dict.Remove(3);
        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit2"); // {1,2}

        dict.Upsert(4, 40);
        var outcome3 = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit3"); // {1,2,4}

        // Reopen and verify
        var openResult = OpenRevision(outcome3.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(loadResult.Value);

        Assert.Equal(3, loadedDict.Count);
        Assert.True(loadedDict.TryGet(1, out var v1));
        Assert.Equal(10, v1);
        Assert.True(loadedDict.TryGet(2, out var v2));
        Assert.Equal(20, v2);
        Assert.True(loadedDict.TryGet(4, out var v4));
        Assert.Equal(40, v4);
        Assert.False(loadedDict.TryGet(3, out _), "Key 3 was deleted and should not reappear");
    }

    [Fact]
    public void DeleteMiddle_CommitInsertNew_Reopen_CorrectOrder() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int, int>();
        for (int i = 1; i <= 5; i++) { dict.Upsert(i, i * 10); }
        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1"); // {1..5}

        dict.Remove(3);
        dict.Remove(4);
        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit2"); // {1,2,5}

        dict.Upsert(6, 60);
        dict.Upsert(7, 70);
        var outcome3 = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit3"); // {1,2,5,6,7}

        var openResult = OpenRevision(outcome3.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int, int>>(loadResult.Value);

        Assert.Equal(5, loadedDict.Count);
        var keys = loadedDict.GetKeys();
        Assert.Equal(new[] { 1, 2, 5, 6, 7 }, keys);
    }

    // ──────────────────────────────────────────────────────────────
    // Regression: string keys/values survive unrelated commit
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void StringKeysValues_SurviveUnrelatedCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        // Root dict holds two children: an ordered dict with strings, and a regular dict
        var root = rev.CreateDict<string>();
        var orderedDict = rev.CreateOrderedDict<string, string>();
        orderedDict.Upsert("alpha", "A");
        orderedDict.Upsert("beta", "B");

        var changingDict = rev.CreateDict<int, int>();
        changingDict.Upsert(1, 100);

        root.Upsert("ordered", orderedDict);
        root.Upsert("changing", changingDict);

        AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        // Second commit: only modify changingDict, leave orderedDict untouched
        changingDict.Upsert(2, 200);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        // Reopen and verify orderedDict strings survived
        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(orderedDict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedOrdered = Assert.IsAssignableFrom<DurableOrderedDict<string, string>>(loadResult.Value);

        Assert.Equal(2, loadedOrdered.Count);
        Assert.True(loadedOrdered.TryGet("alpha", out var va), "Key 'alpha' should exist after reload");
        Assert.Equal("A", va);
        Assert.True(loadedOrdered.TryGet("beta", out var vb), "Key 'beta' should exist after reload");
        Assert.Equal("B", vb);
    }
}
