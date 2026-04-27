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
        root.OfSymbol.Upsert(1, "文档标题");
        root.OfSymbol.Upsert(2, "Alice");
        root.Upsert(3, 42);
        root.Upsert(4, 100L);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);

        Assert.True(loaded.TryGet(1, out Symbol title));
        Assert.Equal("文档标题", title.Value);

        Assert.True(loaded.TryGet(2, out Symbol author));
        Assert.Equal("Alice", author.Value);

        Assert.True(loaded.TryGet(3, out int count));
        Assert.Equal(42, count);

        Assert.True(loaded.TryGet(4, out long longVal));
        Assert.Equal(100L, longVal);
    }

    [Fact]
    public void MixedDict_OfSymbolView_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.OfSymbol.Upsert(1, "value1");
        root.OfSymbol.Upsert(2, "value2");
        root.OfSymbol.Upsert(3, "value3");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal("value1", loaded.OfSymbol.Get(1));
        Assert.Equal("value2", loaded.OfSymbol.Get(2));
        Assert.Equal("value3", loaded.OfSymbol.Get(3));
    }

    // ═══════════════════════ MixedDeque + string round-trip ═══════════════════════

    [Fact]
    public void MixedDeque_StringValues_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();
        root.OfSymbol.PushBack("first");
        root.PushBack(42);
        root.OfSymbol.PushBack("second");
        root.PushBack(99);
        root.OfSymbol.PushFront("zeroth");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(5, loaded.Count);

        Assert.True(loaded.TryPeekFront(out Symbol front));
        Assert.Equal("zeroth", front.Value);

        Assert.Equal(GetIssue.None, loaded.OfSymbol.GetAt(0, out Symbol at0));
        Assert.Equal("zeroth", at0.Value);

        Assert.Equal(GetIssue.None, loaded.OfSymbol.GetAt(1, out Symbol at1));
        Assert.Equal("first", at1.Value);

        Assert.Equal(GetIssue.None, loaded.OfInt32.GetAt(2, out int intVal));
        Assert.Equal(42, intVal);

        Assert.Equal(GetIssue.None, loaded.OfSymbol.GetAt(3, out Symbol at3));
        Assert.Equal("second", at3.Value);

        Assert.Equal(GetIssue.None, loaded.OfInt32.GetAt(4, out int intVal2));
        Assert.Equal(99, intVal2);
    }

    [Fact]
    public void MixedDeque_OfSymbolView_PushAndPeek_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();
        root.OfSymbol.PushBack("back");
        root.OfSymbol.PushFront("front");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(2, loaded.Count);

        Assert.Equal(GetIssue.None, loaded.OfSymbol.PeekFront(out Symbol pf));
        Assert.Equal("front", pf.Value);

        Assert.Equal(GetIssue.None, loaded.OfSymbol.PeekBack(out Symbol pb));
        Assert.Equal("back", pb.Value);
    }

    // ═══════════════════════ mixed null value round-trip ═══════════════════════
    // A1 阶段 mixed 容器没有 string 入口；C 阶段 mixed string 已重新接入 payload 通路。
    // Symbol 不表达 null（default(Symbol) == Symbol.Empty）。这里保留 ValueBox.Null 的 round-trip 覆盖，
    // null payload string 的 round-trip 覆盖见 MixedStringPayloadTests。

    [Fact]
    public void MixedDict_NullDurableObject_Commit_Open_RoundTripsAsValueBoxNull() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.OfSymbol.Upsert(1, "hello");
        root.Upsert<DurableObject>(2, null);
        root.Upsert(3, 42);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(3, loaded.Count);

        Assert.True(loaded.TryGet(1, out Symbol present));
        Assert.Equal("hello", present.Value);

        Assert.True(loaded.TryGetValueKind(2, out var nullKind));
        Assert.Equal(ValueKind.Null, nullKind);
        Assert.Equal(GetIssue.None, loaded.Get<DurableObject>(2, out var absent));
        Assert.Null(absent);

        Assert.True(loaded.TryGet(3, out int number));
        Assert.Equal(42, number);
    }

    [Fact]
    public void MixedDeque_NullDurableObject_Commit_Open_RoundTripsAsValueBoxNull() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();
        root.OfSymbol.PushBack("hello");
        root.PushBack((DurableObject?)null);
        root.OfSymbol.PushBack("world");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(3, loaded.Count);

        Assert.Equal(GetIssue.None, loaded.OfSymbol.GetAt(0, out Symbol first));
        Assert.Equal("hello", first.Value);

        Assert.Equal(GetIssue.None, loaded.GetAt<DurableObject>(1, out var absent));
        Assert.Null(absent);

        Assert.Equal(GetIssue.None, loaded.OfSymbol.GetAt(2, out Symbol last));
        Assert.Equal("world", last.Value);
    }

    // ═══════════════════════ Empty string round-trip ═══════════════════════

    [Fact]
    public void MixedDict_EmptyString_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.OfSymbol.Upsert(1, "");
        root.OfSymbol.Upsert(2, "text");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);

        Assert.True(loaded.TryGet(1, out Symbol empty));
        Assert.Equal("", empty.Value);

        Assert.True(loaded.TryGet(2, out Symbol nonempty));
        Assert.Equal("text", nonempty.Value);
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

        root.OfSymbol.Upsert(1, longStr);
        root.OfSymbol.Upsert(2, unicodeLong);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);

        Assert.True(loaded.TryGet(1, out Symbol la));
        Assert.Equal(longStr, la.Value);

        Assert.True(loaded.TryGet(2, out Symbol lu));
        Assert.Equal(unicodeLong, lu.Value);
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
            root.OfSymbol.Upsert(i, $"value_{i % 50}");
        }

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(100, loaded.Count);

        for (int i = 0; i < 100; i++) {
            Assert.True(loaded.TryGet(i, out Symbol val));
            Assert.Equal($"value_{i % 50}", val.Value);
        }
    }

    [Fact]
    public void MixedDeque_ManySymbols_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();

        for (int i = 0; i < 200; i++) {
            root.OfSymbol.PushBack($"item_{i}");
        }

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(200, loaded.Count);

        for (int i = 0; i < 200; i++) {
            Assert.Equal(GetIssue.None, loaded.OfSymbol.GetAt(i, out Symbol val));
            Assert.Equal($"item_{i}", val.Value);
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
        root.OfSymbol.Upsert(1, "draft");
        root.Upsert(2, 1);
        var outcome1 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Commit 2: 新增与更新字符串
        root.OfSymbol.Upsert(1, "final");     // 更新已有 string
        root.OfSymbol.Upsert(3, "Bob");       // 新增 string
        root.Upsert(2, 2);           // 更新 int
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Commit 3: 再次更新
        root.OfSymbol.Upsert(1, "published");
        root.OfSymbol.Upsert(4, "science");
        var outcome3 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Open 最新 commit
        var openResult = OpenRevision(outcome3.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);

        Assert.Equal("published", loaded.OfSymbol.Get(1));
        Assert.Equal("Bob", loaded.OfSymbol.Get(3));
        Assert.Equal("science", loaded.OfSymbol.Get(4));

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

        root.OfSymbol.Upsert(1, "Project A");
        root.Upsert(2, 3);

        var tags = rev.CreateDeque();
        tags.OfSymbol.PushBack("alpha");
        tags.OfSymbol.PushBack("beta");
        tags.OfSymbol.PushBack("release");
        root.Upsert(10, tags);

        var meta = rev.CreateDict<int>();
        meta.OfSymbol.Upsert(1, "2026-01-01");
        meta.OfSymbol.Upsert(2, "2026-03-25");
        root.Upsert(20, meta);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);

        Assert.Equal("Project A", loaded.OfSymbol.Get(1));
        Assert.True(loaded.TryGet(2, out int version));
        Assert.Equal(3, version);

        // 嵌套 Deque 中的 string
        var loadedTags = loaded.GetOrThrow<DurableDeque>(10);
        Assert.NotNull(loadedTags);
        Assert.Equal(3, loadedTags.Count);
        Assert.Equal(GetIssue.None, loadedTags.OfSymbol.GetAt(0, out Symbol tag0));
        Assert.Equal("alpha", tag0.Value);
        Assert.Equal(GetIssue.None, loadedTags.OfSymbol.GetAt(1, out Symbol tag1));
        Assert.Equal("beta", tag1.Value);
        Assert.Equal(GetIssue.None, loadedTags.OfSymbol.GetAt(2, out Symbol tag2));
        Assert.Equal("release", tag2.Value);

        // 嵌套 Dict 中的 string
        var loadedMeta = loaded.GetOrThrow<DurableDict<int>>(20);
        Assert.NotNull(loadedMeta);
        Assert.Equal(2, loadedMeta.Count);
        Assert.Equal("2026-01-01", loadedMeta.OfSymbol.Get(1));
        Assert.Equal("2026-03-25", loadedMeta.OfSymbol.Get(2));
    }

    // ═══════════════════════ TypedDict<int, string> ═══════════════════════

    [Fact]
    public void TypedDict_StringValue_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, Symbol>();
        root.Upsert(1, "hello");
        root.Upsert(2, "世界");
        root.Upsert(3, Symbol.Empty);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(openResult.Value!.GraphRoot);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(1, out var v1));
        Assert.Equal("hello", v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out var v2));
        Assert.Equal("世界", v2);
        Assert.Equal(GetIssue.None, loaded.Get(3, out var v3));
        Assert.Equal(Symbol.Empty, v3);
        Assert.Equal(string.Empty, v3.Value);
        Assert.Equal(GetIssue.NotFound, loaded.Get(99, out _));
    }

    [Fact]
    public void TypedDict_SymbolValue_UpsertNull_Throws() {
        var rev = CreateRevision();
        var root = rev.CreateDict<int, Symbol>();
        Assert.Throws<ArgumentNullException>(() => root.Upsert(3, null!));
    }

    [Fact]
    public void TypedDict_StringValue_Dedup_Commit_Open_RoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, Symbol>();
        // 100 entries, 10 distinct strings → symbol pool dedup
        for (int i = 0; i < 100; i++) {
            root.Upsert(i, $"val_{i % 10}");
        }

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(openResult.Value!.GraphRoot);
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
        var root = rev.CreateDict<int, Symbol>();
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

        var loaded = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(openResult.Value!.GraphRoot);
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
        var root = rev.CreateDict<int, Symbol>();
        root.Upsert(1, longAscii);
        root.Upsert(2, longUnicode);
        root.Upsert(3, "");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(openResult.Value!.GraphRoot);
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
        var child = rev.CreateDict<int, Symbol>();
        child.Upsert(10, "nested-hello");
        child.Upsert(20, "nested-world");
        root.Upsert(1, child);
        root.Upsert(2, 42);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(1, out DurableObject? obj));
        var loadedChild = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(obj);
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
        var root = rev.CreateDict<int, Symbol>();
        root.Upsert(1, "transient");
        root.Remove(1);
        root.Upsert(2, "keep");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        Assert.Equal(1, GetSymbolTableCount(rev));

        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loadedRev = openResult.Value!;
        Assert.Equal(1, GetSymbolTableCount(loadedRev));
        var loaded = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(loadedRev.GraphRoot);
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
        var root = rev.CreateDict<int, Symbol>();
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
        var loaded = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(openResult.Value!.GraphRoot);
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
        root.OfSymbol.Upsert(1, "alive");
        root.OfSymbol.Upsert(2, "doomed");
        root.Upsert(3, 42); // 非 string

        var out1 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        // 移除 string、保留 int
        root.Remove(2);

        var out2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(out2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.TryGet(1, out Symbol s));
        Assert.Equal("alive", s.Value);
        Assert.True(loaded.TryGet(3, out int n));
        Assert.Equal(42, n);
    }

    [Fact]
    public void TypedDict_StringValue_ReplaceString_OldValueReclaimedByGC() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, Symbol>();
        root.Upsert(1, "original");

        AssertCommitSucceeded(CommitToFile(rev, root, file));

        // 替换值：原来的 "original" 如果无其他引用则应被 GC
        root.Upsert(1, "replaced");

        var out2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(out2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(openResult.Value!.GraphRoot);
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
        deque.OfSymbol.PushBack("first");
        deque.OfSymbol.PushBack("second");
        deque.OfSymbol.PushBack("third");
        root.Upsert(1, deque);

        AssertCommitSucceeded(CommitToFile(rev, root, file));

        // Pop 掉 front，"first" 不再可达
        deque.OfSymbol.PopFront(out _);

        var out2 = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var openResult = OpenRevision(out2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.Get(1, out DurableObject? obj));
        var loadedDeque = Assert.IsAssignableFrom<DurableDeque>(obj);
        Assert.Equal(2, loadedDeque.Count);
        Assert.Equal(GetIssue.None, loadedDeque.OfSymbol.PeekFront(out Symbol front));
        Assert.Equal("second", front.Value);
    }

    // ═══════════════════════ Phase 5: Fragmented Symbol Pool ═══════════════════════

    [Fact]
    public void Commit_WhenSymbolPoolFragmented_RemainsCorrect() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, Symbol>();
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
            Assert.Equal(GetIssue.None, root.Get(i, out Symbol val));
            Assert.Equal($"symbol_{i}", val.Value);
        }

        var openResult = OpenRevision(c2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");
        var loaded = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(openResult.Value!.GraphRoot);
        Assert.Equal(total - toRemove, loaded.Count);
        for (int i = toRemove; i < total; i++) {
            Assert.Equal(GetIssue.None, loaded.Get(i, out Symbol val));
            Assert.Equal($"symbol_{i}", val.Value);
        }
    }

    [Fact]
    public void Commit_WithFragmentedSymbolPool_ThenFurtherWrites_StillRoundTrips() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, Symbol>();
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
        var loaded = Assert.IsAssignableFrom<DurableDict<int, Symbol>>(openResult.Value!.GraphRoot);
        Assert.Equal(total - toRemove + 1, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(999, out Symbol v));
        Assert.Equal("after_compaction", v.Value);
        Assert.Equal(GetIssue.None, loaded.Get(toRemove, out Symbol firstAlive));
        Assert.Equal($"value_{toRemove}", firstAlive.Value);
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
                root.OfSymbol.Upsert(i, $"str_{i}");
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
                Assert.True(loaded.TryGet(i, out Symbol s));
                Assert.Equal($"str_{i}", s.Value);
            }
            else {
                Assert.True(loaded.TryGet(i, out int n));
                Assert.Equal(i * 10, n);
            }
        }
    }

    // ═══════════════════════ Symbol sweep safety ═══════════════════════
    //
    // 以下测试覆盖"commit → 移除部分 string → 再次 commit → open"流程，
    // 验证 WalkAndMark 中 AcceptChildRefVisitor 正确标记了 surviving symbol，
    // sweep 不会误删仍然被引用的 SymbolId。
    //
    // 此场景的关键前置条件是 _symbolRefCount 在 commit 时必须准确：
    //   若 refcount 为 0 而容器实际持有 symbol，AcceptChildRefVisitor 会短路跳过，
    //   导致 symbol 未被标记为可达，sweep 后 symbol 丢失。

    [Fact]
    public void MixedDict_RemoveString_ThenCommit_SurvivingSymbolsPreserved() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.OfSymbol.Upsert(1, "alpha");
        root.OfSymbol.Upsert(2, "beta");
        root.OfSymbol.Upsert(3, "gamma");
        root.Upsert(4, 42);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        // 移除一个 string，保留另外两个
        root.Remove(2);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(openResult.Value!.GraphRoot);
        Assert.Equal(3, loaded.Count);
        Assert.Equal("alpha", loaded.OfSymbol.Get(1));
        Assert.Equal("gamma", loaded.OfSymbol.Get(3));
        Assert.True(loaded.TryGet(4, out int intVal));
        Assert.Equal(42, intVal);
    }

    [Fact]
    public void MixedDeque_PopString_ThenCommit_SurvivingSymbolsPreserved() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();
        root.OfSymbol.PushBack("first");
        root.OfSymbol.PushBack("second");
        root.OfSymbol.PushBack("third");
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        // Pop 掉 front，保留 "second" 和 "third"
        root.TryPopFront<Symbol>(out _);
        var outcome2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        var openResult = OpenRevision(outcome2.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.OfSymbol.GetAt(0, out Symbol v0));
        Assert.Equal("second", v0.Value);
        Assert.Equal(GetIssue.None, loaded.OfSymbol.GetAt(1, out Symbol v1));
        Assert.Equal("third", v1.Value);
    }
}
