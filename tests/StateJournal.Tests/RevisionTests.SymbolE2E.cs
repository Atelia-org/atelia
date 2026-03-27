using Atelia.Rbf;
using Xunit;

namespace Atelia.StateJournal.Tests;

// Phase 3d: 端到端测试
// Mixed / Typed 容器中的 string 最终都经由 symbol table 落盘并在 Open 时还原。
partial class RevisionTests {

    // ═══════════════════════ MixedDict + string value round-trip ═══════════════════════

    [Fact]
    public void MixedDict_StringValues_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.Upsert(1, "文档标题");
        root.Upsert(2, "Alice");
        root.Upsert(3, 42);
        root.Upsert(4, 100L);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);

        Assert.True(loaded.TryGet(1, out string? title));
        Assert.Equal("文档标题", title);

        Assert.True(loaded.TryGet(2, out string? author));
        Assert.Equal("Alice", author);

        Assert.True(loaded.TryGet(3, out int count));
        Assert.Equal(42, count);

        Assert.True(loaded.TryGet(4, out long longVal));
        Assert.Equal(100L, longVal);
    }

    [Fact]
    public void MixedDict_OfStringView_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.OfString.Upsert(1, "value1");
        root.OfString.Upsert(2, "value2");
        root.OfString.Upsert(3, "value3");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal("value1", loaded.OfString.Get(1));
        Assert.Equal("value2", loaded.OfString.Get(2));
        Assert.Equal("value3", loaded.OfString.Get(3));
    }

    // ═══════════════════════ MixedDeque + string round-trip ═══════════════════════

    [Fact]
    public void MixedDeque_StringValues_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();
        root.PushBack("first");
        root.PushBack(42);
        root.PushBack("second");
        root.PushBack(99);
        root.PushFront("zeroth");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(5, loaded.Count);

        Assert.True(loaded.TryPeekFront(out string? front));
        Assert.Equal("zeroth", front);

        Assert.Equal(GetIssue.None, loaded.OfString.GetAt(0, out string? at0));
        Assert.Equal("zeroth", at0);

        Assert.Equal(GetIssue.None, loaded.OfString.GetAt(1, out string? at1));
        Assert.Equal("first", at1);

        Assert.Equal(GetIssue.None, loaded.OfInt32.GetAt(2, out int intVal));
        Assert.Equal(42, intVal);

        Assert.Equal(GetIssue.None, loaded.OfString.GetAt(3, out string? at3));
        Assert.Equal("second", at3);

        Assert.Equal(GetIssue.None, loaded.OfInt32.GetAt(4, out int intVal2));
        Assert.Equal(99, intVal2);
    }

    [Fact]
    public void MixedDeque_OfStringView_PushAndPeek_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();
        root.OfString.PushBack("back");
        root.OfString.PushFront("front");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(2, loaded.Count);

        Assert.Equal(GetIssue.None, loaded.OfString.PeekFront(out string? pf));
        Assert.Equal("front", pf);

        Assert.Equal(GetIssue.None, loaded.OfString.PeekBack(out string? pb));
        Assert.Equal("back", pb);
    }

    // ═══════════════════════ null string round-trip ═══════════════════════

    [Fact]
    public void MixedDict_NullString_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.Upsert(1, "hello");
        root.Upsert(2, (string?)null);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);

        Assert.True(loaded.TryGet(1, out string? present));
        Assert.Equal("hello", present);

        Assert.True(loaded.TryGet(2, out string? absent));
        Assert.Null(absent);
    }

    [Fact]
    public void MixedDeque_NullString_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();
        root.PushBack("hello");
        root.PushBack((string?)null);
        root.PushBack("world");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(3, loaded.Count);

        Assert.True(loaded.TryPeekFront(out string? s0));
        Assert.Equal("hello", s0);

        Assert.True(loaded.TryPopFront(out string? _)); // pop "hello"
        Assert.True(loaded.TryPeekFront(out string? s1));
        Assert.Null(s1);

        Assert.True(loaded.TryPopFront(out string? _2)); // pop null
        Assert.True(loaded.TryPeekFront(out string? s2));
        Assert.Equal("world", s2);
    }

    // ═══════════════════════ Empty string round-trip ═══════════════════════

    [Fact]
    public void MixedDict_EmptyString_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.Upsert(1, "");
        root.Upsert(2, "text");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);

        Assert.True(loaded.TryGet(1, out string? empty));
        Assert.Equal("", empty);

        Assert.True(loaded.TryGet(2, out string? nonempty));
        Assert.Equal("text", nonempty);
    }

    // ═══════════════════════ Long string round-trip ═══════════════════════

    [Fact]
    public void MixedDict_LongString_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();

        string longStr = new('x', 10_000);
        string unicodeLong = string.Concat(Enumerable.Repeat("你好🎉", 2_000));

        root.Upsert(1, longStr);
        root.Upsert(2, unicodeLong);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);

        Assert.True(loaded.TryGet(1, out string? la));
        Assert.Equal(longStr, la);

        Assert.True(loaded.TryGet(2, out string? lu));
        Assert.Equal(unicodeLong, lu);
    }

    // ═══════════════════════ 大量 symbol 去重 ═══════════════════════

    [Fact]
    public void MixedDict_ManySymbols_Dedup_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();

        // 100 个 key 使用 50 个不同的 string value（每个 value 重复 2 次）
        for (int i = 0; i < 100; i++) {
            root.Upsert(i, $"value_{i % 50}");
        }

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(100, loaded.Count);

        for (int i = 0; i < 100; i++) {
            Assert.True(loaded.TryGet(i, out string? val));
            Assert.Equal($"value_{i % 50}", val);
        }
    }

    [Fact]
    public void MixedDeque_ManySymbols_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();

        for (int i = 0; i < 200; i++) {
            root.PushBack($"item_{i}");
        }

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(200, loaded.Count);

        for (int i = 0; i < 200; i++) {
            Assert.Equal(GetIssue.None, loaded.OfString.GetAt(i, out string? val));
            Assert.Equal($"item_{i}", val);
        }
    }

    // ═══════════════════════ 多次 Commit 渐进添加 string ═══════════════════════

    [Fact]
    public void MixedDict_MultipleCommits_ProgressiveStringAdditions_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();

        // Commit 1: 初始数据
        root.Upsert(1, "draft");
        root.Upsert(2, 1);
        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Commit 2: 新增与更新字符串
        root.Upsert(1, "final");     // 更新已有 string
        root.Upsert(3, "Bob");       // 新增 string
        root.Upsert(2, 2);           // 更新 int
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Commit 3: 再次更新
        root.OfString.Upsert(1, "published");
        root.Upsert(4, "science");
        var outcome3 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Open 最新 commit
        var openResult = OpenRevision(outcome3.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);

        Assert.Equal("published", loaded.OfString.Get(1));
        Assert.Equal("Bob", loaded.OfString.Get(3));
        Assert.Equal("science", loaded.OfString.Get(4));

        Assert.True(loaded.TryGet(2, out int intCount));
        Assert.Equal(2, intCount);
    }

    // ═══════════════════════ Mixed content: string + nested containers ═══════════════════════

    [Fact]
    public void MixedDict_StringsAndNestedContainers_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();

        root.Upsert(1, "Project A");
        root.Upsert(2, 3);

        var tags = rev.CreateDeque();
        tags.PushBack("alpha");
        tags.PushBack("beta");
        tags.PushBack("release");
        root.Upsert(10, tags);

        var meta = rev.CreateDict<int>();
        meta.Upsert(1, "2026-01-01");
        meta.Upsert(2, "2026-03-25");
        root.Upsert(20, meta);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);

        Assert.Equal("Project A", loaded.OfString.Get(1));
        Assert.True(loaded.TryGet(2, out int version));
        Assert.Equal(3, version);

        // 嵌套 Deque 中的 string
        var loadedTags = loaded.GetOrThrow<DurableDeque>(10);
        Assert.NotNull(loadedTags);
        Assert.Equal(3, loadedTags.Count);
        Assert.Equal(GetIssue.None, loadedTags.OfString.GetAt(0, out string? tag0));
        Assert.Equal("alpha", tag0);
        Assert.Equal(GetIssue.None, loadedTags.OfString.GetAt(1, out string? tag1));
        Assert.Equal("beta", tag1);
        Assert.Equal(GetIssue.None, loadedTags.OfString.GetAt(2, out string? tag2));
        Assert.Equal("release", tag2);

        // 嵌套 Dict 中的 string
        var loadedMeta = loaded.GetOrThrow<DurableDict<int>>(20);
        Assert.NotNull(loadedMeta);
        Assert.Equal(2, loadedMeta.Count);
        Assert.Equal("2026-01-01", loadedMeta.OfString.Get(1));
        Assert.Equal("2026-03-25", loadedMeta.OfString.Get(2));
    }

    // ═══════════════════════ TypedDict<int, string> ═══════════════════════

    [Fact]
    public void TypedDict_StringValue_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        root.Upsert(1, "hello");
        root.Upsert(2, "世界");
        root.Upsert(3, null);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v1));
        Assert.Equal("hello", v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2));
        Assert.Equal("世界", v2);
        Assert.Equal(GetIssue.None, loaded.Get(3, out var v3));
        Assert.Null(v3);
        Assert.Equal(GetIssue.NotFound, loaded.Get(99, out _));
    }

    [Fact]
    public void TypedDict_StringValue_Dedup_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        // 100 entries, 10 distinct strings → symbol pool dedup
        for (int i = 0; i < 100; i++) {
            root.Upsert(i, $"val_{i % 10}");
        }

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(100, loaded.Count);
        for (int i = 0; i < 100; i++) {
            Assert.Equal(GetIssue.None, loaded.Get(i, out var s));
            Assert.Equal($"val_{i % 10}", s);
        }
    }

    [Fact]
    public void TypedDict_StringValue_MultipleCommits_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        root.Upsert(1, "alpha");

        var out1 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        root.Upsert(2, "beta");
        root.Upsert(1, "alpha-updated");

        var out2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        root.Remove(1);
        root.Upsert(3, "gamma");

        var out3 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Open from final commit
        var openResult = OpenRevision(out3.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(GetIssue.NotFound, loaded.Get(1, out _));
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2));
        Assert.Equal("beta", v2);
        Assert.Equal(GetIssue.None, loaded.Get(3, out var v3));
        Assert.Equal("gamma", v3);
    }

    [Fact]
    public void TypedDict_StringValue_LongStrings_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var longAscii = new string('A', 10_000);
        var longUnicode = string.Concat(Enumerable.Repeat("你好🎉", 2000));

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        root.Upsert(1, longAscii);
        root.Upsert(2, longUnicode);
        root.Upsert(3, "");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v1));
        Assert.Equal(longAscii, v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2));
        Assert.Equal(longUnicode, v2);
        Assert.Equal(GetIssue.None, loaded.Get(3, out var v3));
        Assert.Equal("", v3);
    }

    [Fact]
    public void TypedDict_StringValue_AsNestedChild_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        var child = rev.CreateDict<int, string>();
        child.Upsert(10, "nested-hello");
        child.Upsert(20, "nested-world");
        root.Upsert(1, child);
        root.Upsert(2, 42);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(1, out DurableObject? obj));
        var loadedChild = Assert.IsAssignableFrom<DurableDict<int, string>>(obj);
        Assert.Equal(2, loadedChild.Count);
        Assert.Equal(GetIssue.None, loadedChild.Get(10, out var v1));
        Assert.Equal("nested-hello", v1);
        Assert.Equal(GetIssue.None, loadedChild.Get(20, out var v2));
        Assert.Equal("nested-world", v2);
    }

    [Fact]
    public void TypedDict_StringValue_SameCommitTransientSymbol_IsNotPersisted() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        root.Upsert(1, "transient");
        root.Remove(1);
        root.Upsert(2, "keep");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        Assert.Equal(1, GetSymbolTableCount(rev));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loadedRev = openResult.Value!;
        Assert.Equal(1, GetSymbolTableCount(loadedRev));
        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(loadedRev.GraphRoot);
        Assert.Equal(1, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var keep));
        Assert.Equal("keep", keep);
        Assert.Equal(GetIssue.NotFound, loaded.Get(1, out _));
    }

    // ═══════════════════════ Symbol GC：不可达 symbol 被回收 ═══════════════════════

    [Fact]
    public void TypedDict_StringValue_RemovedStrings_NotPersistedAfterCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        root.Upsert(1, "keep-me");
        root.Upsert(2, "remove-me");
        root.Upsert(3, "also-remove");

        // 第一次 Commit：3 个 string 都可达
        var out1 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // 删除 2 个 string 引用
        root.Remove(2);
        root.Remove(3);

        // 第二次 Commit：触发 GC，"remove-me"/"also-remove" 变为不可达
        var out2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Open 后验证：只有 "keep-me" 存活
        var openResult = OpenRevision(out2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(1, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v));
        Assert.Equal("keep-me", v);
        Assert.Equal(GetIssue.NotFound, loaded.Get(2, out _));
    }

    [Fact]
    public void MixedDict_StringValue_RemovedStrings_ReclaimedByGC() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.Upsert(1, "alive");
        root.Upsert(2, "doomed");
        root.Upsert(3, 42); // 非 string

        var out1 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // 移除 string、保留 int
        root.Remove(2);

        var out2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(out2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.TryGet(1, out string? s));
        Assert.Equal("alive", s);
        Assert.True(loaded.TryGet(3, out int n));
        Assert.Equal(42, n);
    }

    [Fact]
    public void TypedDict_StringValue_ReplaceString_OldValueReclaimedByGC() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        root.Upsert(1, "original");

        AssertCommitSucceeded(CommitToFile(rev, root, file));

        // 替换值：原来的 "original" 如果无其他引用则应被 GC
        root.Upsert(1, "replaced");

        var out2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(out2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(1, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v));
        Assert.Equal("replaced", v);
    }

    [Fact]
    public void MixedDeque_StringValue_PopRemovesReachability() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        var deque = rev.CreateDeque();
        deque.PushBack("first");
        deque.PushBack("second");
        deque.PushBack("third");
        root.Upsert(1, deque);

        AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Pop 掉 front，"first" 不再可达
        deque.OfString.PopFront(out _);

        var out2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(out2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(1, out DurableObject? obj));
        var loadedDeque = Assert.IsAssignableFrom<DurableDeque>(obj);
        Assert.Equal(2, loadedDeque.Count);
        Assert.Equal(GetIssue.None, loadedDeque.OfString.PeekFront(out string? front));
        Assert.Equal("second", front);
    }

    // ═══════════════════════ Phase 5: Fragmented Symbol Pool ═══════════════════════

    [Fact]
    public void Commit_WhenSymbolPoolFragmented_RemainsCorrect() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        const int total = 200;
        const int toRemove = 100;

        for (int i = 0; i < total; i++) {
            root.Upsert(i, $"symbol_{i}");
        }

        var c1 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1-AllSymbols");

        // 移除 100 个 entry，使对应 symbol 变为 unreachable
        for (int i = 0; i < toRemove; i++) {
            root.Remove(i);
        }

        var c2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2-AfterRemoval");
        Assert.Equal(CommitCompletion.PrimaryOnly, c2.Completion);

        for (int i = toRemove; i < total; i++) {
            Assert.Equal(GetIssue.None, root.Get(i, out string? val));
            Assert.Equal($"symbol_{i}", val);
        }

        var openResult = OpenRevision(c2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");
        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(total - toRemove, loaded.Count);
        for (int i = toRemove; i < total; i++) {
            Assert.Equal(GetIssue.None, loaded.Get(i, out string? val));
            Assert.Equal($"symbol_{i}", val);
        }
    }

    [Fact]
    public void Commit_WithFragmentedSymbolPool_ThenFurtherWrites_StillRoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        const int total = 200;
        const int toRemove = 100;

        for (int i = 0; i < total; i++) {
            root.Upsert(i, $"value_{i}");
        }
        AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < toRemove; i++) {
            root.Remove(i);
        }

        var c2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(CommitCompletion.PrimaryOnly, c2.Completion);

        root.Upsert(999, "after_compaction");
        var c3 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit3");

        var openResult = OpenRevision(c3.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");
        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(openResult.Value!.GraphRoot);
        Assert.Equal(total - toRemove + 1, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(999, out string? v));
        Assert.Equal("after_compaction", v);
        Assert.Equal(GetIssue.None, loaded.Get(toRemove, out string? first_alive));
        Assert.Equal($"value_{toRemove}", first_alive);
    }

    [Fact]
    public void Commit_WithFragmentedSymbolPool_MixedDictValuesRemainCorrect() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        const int total = 300;
        const int toRemove = 100;

        for (int i = 0; i < total; i++) {
            if (i % 2 == 0) {
                root.Upsert(i, $"str_{i}");
            }
            else {
                root.Upsert(i, i * 10);
            }
        }
        AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < toRemove; i++) {
            root.Remove(i);
        }

        var c2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(CommitCompletion.PrimaryOnly, c2.Completion);

        var openResult = OpenRevision(c2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");
        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(total - toRemove, loaded.Count);

        for (int i = toRemove; i < total; i++) {
            if (i % 2 == 0) {
                Assert.True(loaded.TryGet(i, out string? s));
                Assert.Equal($"str_{i}", s);
            }
            else {
                Assert.True(loaded.TryGet(i, out int n));
                Assert.Equal(i * 10, n);
            }
        }
    }
}
