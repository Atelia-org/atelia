using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

/// <summary>
/// MixedOrderedDict (DurableOrderedDict&lt;TKey&gt;) 端到端测试。
/// </summary>
public class MixedOrderedDictTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), $"mixed-odict-test-{Guid.NewGuid()}");
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
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();
        Assert.Equal(DurableObjectKind.MixedOrderedDict, dict.Kind);
    }

    // ──────────────────────────────────────────────────────────────
    // Basic CRUD — heterogeneous values
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Upsert_Get_MixedTypes() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.Upsert(1, 42);
        dict.Upsert(2, 100.0);
        dict.Upsert(3, true);

        Assert.Equal(3, dict.Count);

        Assert.True(dict.TryGet<int>(1, out var vi));
        Assert.Equal(42, vi);

        Assert.True(dict.TryGet<double>(2, out var vd));
        Assert.Equal(100.0, vd);

        Assert.True(dict.TryGet<bool>(3, out var vb));
        Assert.True(vb);
    }

    [Fact]
    public void Upsert_String_RoundTrips() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.Upsert<string>(1, "hello");
        dict.Upsert<string>(2, "world");

        Assert.True(dict.TryGet<string>(1, out var v1));
        Assert.Equal("hello", v1);
        Assert.True(dict.TryGet<string>(2, out var v2));
        Assert.Equal("world", v2);
    }

    [Fact]
    public void Upsert_DurableObject_RoundTrips() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();
        var child = rev.CreateDict<string, int>();
        child.Upsert("x", 10);

        dict.Upsert<DurableObject>(1, child);

        var issue = dict.Get<DurableObject>(1, out var loaded);
        Assert.Equal(GetIssue.None, issue);
        Assert.NotNull(loaded);
        Assert.Equal(child.LocalId, loaded!.LocalId);
    }

    [Fact]
    public void Remove_Works() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.Upsert(10, 100);
        dict.Upsert(20, 200);
        Assert.Equal(2, dict.Count);

        Assert.True(dict.Remove(20));
        Assert.Equal(1, dict.Count);
        Assert.False(dict.ContainsKey(20));

        Assert.False(dict.Remove(99));
    }

    [Fact]
    public void Get_NotFound_ReturnsNotFound() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        var issue = dict.Get<int>(42, out var val);
        Assert.Equal(GetIssue.NotFound, issue);
    }

    [Fact]
    public void Get_TypeMismatch_ReturnsTypeMismatch() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.Upsert(1, 42);
        var issue = dict.Get<bool>(1, out _);
        Assert.Equal(GetIssue.TypeMismatch, issue);
    }

    [Fact]
    public void GetOrThrow_UnsupportedType_MessageUsesDurableOrderedDict() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        var ex = Assert.Throws<NotSupportedException>(() => dict.GetOrThrow<DateTime>(1));
        Assert.Contains("DurableOrderedDict", ex.Message);
    }

    // ──────────────────────────────────────────────────────────────
    // Ordering
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetKeys_ReturnsOrderedKeys() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

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
    public void GetKeysFrom_RangeQuery() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        for (int i = 1; i <= 10; i++) { dict.Upsert(i * 10, i); }

        var keys = dict.GetKeysFrom(35, 3);
        Assert.Equal(3, keys.Count);
        Assert.Equal(40, keys[0]);
        Assert.Equal(50, keys[1]);
        Assert.Equal(60, keys[2]);
    }

    // ──────────────────────────────────────────────────────────────
    // TryGetValueKind
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryGetValueKind_ReturnsCorrectKinds() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.Upsert(1, 42);
        dict.Upsert(2, 100.0);
        dict.Upsert(3, true);
        dict.Upsert<string>(4, "hello");

        Assert.True(dict.TryGetValueKind(1, out var k1));
        Assert.Equal(ValueKind.NonnegativeInteger, k1);
        Assert.True(dict.TryGetValueKind(2, out var k2));
        Assert.Equal(ValueKind.FloatingPoint, k2);
        Assert.True(dict.TryGetValueKind(3, out var k3));
        Assert.Equal(ValueKind.Boolean, k3);
        Assert.True(dict.TryGetValueKind(4, out var k4));
        Assert.Equal(ValueKind.String, k4);
        Assert.False(dict.TryGetValueKind(99, out _));
    }

    [Fact]
    public void TryGetValueKind_DurableObjectValues() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        // TypedDict as value
        var typedDict = rev.CreateDict<string, int>();
        dict.Upsert<DurableObject>(1, typedDict);
        Assert.True(dict.TryGetValueKind(1, out var k1));
        Assert.Equal(ValueKind.TypedDict, k1);

        // MixedDict as value
        var mixedDict = rev.CreateDict<int>();
        dict.Upsert<DurableObject>(2, mixedDict);
        Assert.True(dict.TryGetValueKind(2, out var k2));
        Assert.Equal(ValueKind.MixedDict, k2);

        // TypedOrderedDict as value
        var typedODict = rev.CreateOrderedDict<int, int>();
        dict.Upsert<DurableObject>(3, typedODict);
        Assert.True(dict.TryGetValueKind(3, out var k3));
        Assert.Equal(ValueKind.TypedOrderedDict, k3);

        // MixedOrderedDict as value
        var mixedODict = rev.CreateOrderedDict<int>();
        dict.Upsert<DurableObject>(4, mixedODict);
        Assert.True(dict.TryGetValueKind(4, out var k4));
        Assert.Equal(ValueKind.MixedOrderedDict, k4);

        // Null DurableObject value
        dict.Upsert<DurableObject>(5, null);
        Assert.True(dict.TryGetValueKind(5, out var k5));
        Assert.Equal(ValueKind.Null, k5);
    }

    // ──────────────────────────────────────────────────────────────
    // Commit → Open → Load round-trip
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Commit_ThenOpen_MixedValues_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int>();
        dict.Upsert(10, 42);
        dict.Upsert(20, 100.0);
        dict.Upsert(30, true);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int>>(loadResult.Value);

        Assert.Equal(3, loadedDict.Count);
        Assert.True(loadedDict.TryGet<int>(10, out var vi));
        Assert.Equal(42, vi);
        Assert.True(loadedDict.TryGet<double>(20, out var vd));
        Assert.Equal(100.0, vd);
        Assert.True(loadedDict.TryGet<bool>(30, out var vb));
        Assert.True(vb);

        var keys = loadedDict.GetKeys();
        Assert.Equal(new[] { 10, 20, 30 }, keys);
    }

    [Fact]
    public void Commit_ThenOpen_StringValues_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int>();
        dict.Upsert<string>(1, "alpha");
        dict.Upsert<string>(2, "beta");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int>>(loadResult.Value);

        Assert.Equal(2, loadedDict.Count);
        Assert.True(loadedDict.TryGet<string>(1, out var v1));
        Assert.Equal("alpha", v1);
        Assert.True(loadedDict.TryGet<string>(2, out var v2));
        Assert.Equal("beta", v2);
    }

    [Fact]
    public void MultipleCommits_DeltaChain_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int>();
        dict.Upsert(1, 10);
        dict.Upsert(2, 20);
        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");

        dict.Upsert(2, 200);
        dict.Upsert(3, 30);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit2");

        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int>>(loadResult.Value);

        Assert.Equal(3, loadedDict.Count);
        Assert.True(loadedDict.TryGet<int>(1, out var v1));
        Assert.Equal(10, v1);
        Assert.True(loadedDict.TryGet<int>(2, out var v2));
        Assert.Equal(200, v2);
        Assert.True(loadedDict.TryGet<int>(3, out var v3));
        Assert.Equal(30, v3);
    }

    [Fact]
    public void Commit_WithRemove_ThenOpen_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int>();
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
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int>>(loadResult.Value);

        Assert.Equal(2, loadedDict.Count);
        Assert.False(loadedDict.ContainsKey(2));
    }

    // ──────────────────────────────────────────────────────────────
    // Nested DurableObject values via MixedOrderedDict
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void NestedDurObj_Commit_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var root = rev.CreateDict<string>();
        var dict = rev.CreateOrderedDict<int>();

        var child = rev.CreateDict<string, int>();
        child.Upsert("inner", 99);
        dict.Upsert<DurableObject>(1, child);
        root.Upsert("mixed-ordered", dict);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int>>(loadResult.Value);

        var issue = loadedDict.Get<DurableObject>(1, out var loadedChild);
        Assert.Equal(GetIssue.None, issue);
        Assert.NotNull(loadedChild);

        var typedChild = Assert.IsAssignableFrom<DurableDict<string, int>>(loadedChild);
        Assert.True(typedChild.TryGet("inner", out var vi));
        Assert.Equal(99, vi);
    }

    [Fact]
    public void TypedContainer_CanStoreMixedOrderedDictValue_CommitRoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var root = rev.CreateDict<string, DurableOrderedDict<int>>();
        var child = rev.CreateOrderedDict<int>();
        child.Upsert(10, 42);
        child.Upsert<string>(20, "alpha");
        root.Upsert("ordered", child);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = openResult.Value!;
        var loadResult = loaded.Load(root.LocalId);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<string, DurableOrderedDict<int>>>(loadResult.Value);

        Assert.True(loadedRoot.TryGet("ordered", out var loadedChild));
        Assert.NotNull(loadedChild);
        Assert.Equal(DurableObjectKind.MixedOrderedDict, loadedChild!.Kind);
        Assert.True(loadedChild.TryGet<int>(10, out var vi));
        Assert.Equal(42, vi);
        Assert.True(loadedChild.TryGet<string>(20, out var vs));
        Assert.Equal("alpha", vs);
    }

    // ──────────────────────────────────────────────────────────────
    // Value type update (change type of existing key)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ValueTypeChange_SameKey_Works() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.Upsert(1, 42);
        Assert.True(dict.TryGet<int>(1, out var vi));
        Assert.Equal(42, vi);

        // Now change the value type for the same key
        dict.Upsert(1, 100.0);
        Assert.True(dict.TryGet<double>(1, out var vd));
        Assert.Equal(100.0, vd);

        // Old type should return TypeMismatch
        Assert.Equal(GetIssue.TypeMismatch, dict.Get<bool>(1, out _));
    }

    // ──────────────────────────────────────────────────────────────
    // String key support
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void StringKeys_WithMixedValues() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<string>();

        dict.Upsert("age", 30);
        dict.Upsert("name", "Alice");
        dict.Upsert("active", true);

        Assert.Equal(3, dict.Count);
        Assert.True(dict.TryGet<int>("age", out var age));
        Assert.Equal(30, age);
        Assert.True(dict.TryGet<string>("name", out var name));
        Assert.Equal("Alice", name);
        Assert.True(dict.TryGet<bool>("active", out var active));
        Assert.True(active);
    }

    // ──────────────────────────────────────────────────────────────
    // UpdateOrInit 相等短路：同值 re-Upsert 不产生 dirty
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void SameValueReUpsert_DoesNotProduceDirty() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int>();
        dict.Upsert(1, 42);
        dict.Upsert(2, true);
        dict.Upsert<string>(3, "hello");

        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");

        // Re-upsert the exact same values
        dict.Upsert(1, 42);
        dict.Upsert(2, true);
        dict.Upsert<string>(3, "hello");

        // HasChanges should remain false — UpdateOrInit short-circuits on equal values
        Assert.False(dict.HasChanges, "Same-value re-Upsert should not produce dirty state");
    }

    [Fact]
    public void SameValueReUpsert_ThenRealChange_ProducesDirty() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int>();
        dict.Upsert(1, 42);

        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");

        // Same value — no dirty
        dict.Upsert(1, 42);
        Assert.False(dict.HasChanges);

        // Different value — dirty
        dict.Upsert(1, 100);
        Assert.True(dict.HasChanges);
    }

    [Fact]
    public void MultipleNoopReUpserts_ThenRealChange_NoDuplicateRelease() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int>();
        dict.Upsert(1, 42);
        dict.Upsert(2, 100);

        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");

        // 多次 no-op re-upsert，每次值都相同
        dict.Upsert(1, 42);
        dict.Upsert(1, 42);
        dict.Upsert(1, 42);
        Assert.False(dict.HasChanges, "Repeated no-ops should not affect HasChanges");

        // 真实变更
        dict.Upsert(1, 999);
        Assert.True(dict.HasChanges);

        // 验证 commit 成功（如果有重复 release 会崩溃或产生不正确值）
        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit2");

        // 验证 round-trip
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess);
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int>>(loadResult.Value);
        Assert.True(loadedDict.TryGet<int>(1, out var v1));
        Assert.Equal(999, v1);
        Assert.True(loadedDict.TryGet<int>(2, out var v2));
        Assert.Equal(100, v2);
    }

    [Fact]
    public void MultipleNoopReUpserts_ThenCommit_NoOpIsClean() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var rev = CreateRevision();

        var dict = rev.CreateOrderedDict<int>();
        dict.Upsert(1, 42);

        AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit1");

        // 多次 no-op
        for (int i = 0; i < 5; i++) {
            dict.Upsert(1, 42);
        }
        Assert.False(dict.HasChanges, "All no-ops, should have no changes");

        // Commit 后 round-trip 正确（no delta 写入）
        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file), "Commit2");
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = openResult.Value!;
        var loadResult = loaded.Load(dict.LocalId);
        Assert.True(loadResult.IsSuccess);
        var loadedDict = Assert.IsAssignableFrom<DurableOrderedDict<int>>(loadResult.Value);
        Assert.True(loadedDict.TryGet<int>(1, out var v));
        Assert.Equal(42, v);
    }
}
