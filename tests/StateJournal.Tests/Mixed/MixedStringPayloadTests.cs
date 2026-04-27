using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;
using Xunit;

namespace Atelia.StateJournal.Tests;

// ai:impl `src/StateJournal/MixedValueCatalog.cs` (string entry)
// ai:impl `src/StateJournal/Internal/ValueBox.String.cs` (StringPayloadFace)
//
// Mixed 容器中 string payload 的端到端契约测试（Step C）。
// 与 mixed Symbol 严格区隔：
//   - Symbol 走 intern（_symbolPool），同内容去重，跨容器共享 SymbolId；
//   - string 走 OwnedStringPool payload，**不**去重，每次 Upsert 独立 slot；
//   - 两者 ValueKind 分别为 Symbol / String，不互通（互读 → TypeMismatch）。
//
// 这些测试与 `RevisionTests.SymbolE2E` 共享 partial class RevisionTests 的 helpers
// （CreateRevision / CommitToFile / OpenRevision / AssertCommitSucceeded / GetTempFilePath）。
partial class RevisionTests {

    // ═══════════════════════ MixedDict<string-key> ═══════════════════════

    [Fact]
    public void MixedDict_String_Upsert_Get_RoundTrip() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        dict.Upsert(1, "hello");
        dict.OfString.Upsert(2, "world");

        Assert.True(dict.TryGetValueKind(1, out var k1));
        Assert.Equal(ValueKind.String, k1);
        Assert.Equal(GetIssue.None, dict.OfString.Get(1, out string? v1));
        Assert.Equal("hello", v1);
        Assert.True(dict.TryGet<string>(2, out var v2));
        Assert.Equal("world", v2);
    }

    [Fact]
    public void MixedDict_String_Null_RoundTrip() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        // 显式泛型覆盖 generator generic dispatch 的 reference-type null 通路。
        dict.Upsert<string>(1, null);

        Assert.True(dict.ContainsKey(1));
        Assert.True(dict.TryGetValueKind(1, out var kind));
        Assert.Equal(ValueKind.Null, kind);
        Assert.Equal(GetIssue.None, dict.OfString.Get(1, out string? v));
        Assert.Null(v);
        Assert.True(dict.TryGet<string>(1, out var viaGeneric));
        Assert.Null(viaGeneric);
    }

    [Fact]
    public void MixedDict_String_Empty_NotIntern() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int symbolBefore = rev.SymbolPoolCount;
        dict.Upsert(1, "");

        Assert.Equal(symbolBefore, rev.SymbolPoolCount);
        Assert.True(dict.TryGetValueKind(1, out var kind));
        Assert.Equal(ValueKind.String, kind);
        Assert.Equal(GetIssue.None, dict.OfString.Get(1, out string? v));
        Assert.Equal("", v);
    }

    [Fact]
    public void MixedDict_String_SameContent_NotDeduplicated() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int ownedBefore = ValuePools.OfOwnedString.Count;
        int symbolBefore = rev.SymbolPoolCount;
        dict.Upsert(1, "hello");
        dict.Upsert(2, "hello");

        // 不去重：两次 Upsert 各占一个 owned slot。
        Assert.Equal(ownedBefore + 2, ValuePools.OfOwnedString.Count);
        // 不进 intern 池。
        Assert.Equal(symbolBefore, rev.SymbolPoolCount);

        Assert.Equal(GetIssue.None, dict.OfString.Get(1, out string? a));
        Assert.Equal(GetIssue.None, dict.OfString.Get(2, out string? b));
        Assert.Equal("hello", a);
        Assert.Equal("hello", b);
    }

    [Fact]
    public void MixedDict_String_VsSymbol_TypeIsolation() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        // Symbol → 读 string == TypeMismatch
        dict.OfSymbol.Upsert(1, "shared");
        Assert.Equal(GetIssue.TypeMismatch, dict.OfString.Get(1, out string? _));
        Assert.Equal(GetIssue.None, dict.OfSymbol.Get(1, out Symbol s1));
        Assert.Equal("shared", s1.Value);

        // string → 读 Symbol == TypeMismatch
        dict.OfString.Upsert(2, "shared");
        Assert.Equal(GetIssue.TypeMismatch, dict.OfSymbol.Get(2, out Symbol _));
        Assert.Equal(GetIssue.None, dict.OfString.Get(2, out string? sv));
        Assert.Equal("shared", sv);
    }

    [Fact]
    public void MixedDict_String_Symbol_StringSwap_RoundTrip() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        dict.OfString.Upsert(1, "abc");
        dict.OfSymbol.Upsert(1, "abc");
        Assert.Equal(GetIssue.None, dict.OfSymbol.Get(1, out Symbol s));
        Assert.Equal("abc", s.Value);
        Assert.Equal(GetIssue.TypeMismatch, dict.OfString.Get(1, out string? _));

        // Symbol → string
        dict.OfString.Upsert(1, "xyz");
        Assert.Equal(GetIssue.None, dict.OfString.Get(1, out string? sv));
        Assert.Equal("xyz", sv);
        Assert.Equal(GetIssue.TypeMismatch, dict.OfSymbol.Get(1, out Symbol _));
    }

    [Fact]
    public void MixedDict_String_HeapInteger_StringSwap_RoundTrip() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int bits64Before = ValuePools.OfBits64.Count;
        dict.Upsert(1, ulong.MaxValue);
        Assert.True(dict.TryGetValueKind(1, out var ki));
        Assert.Equal(ValueKind.NonnegativeInteger, ki);
        Assert.Equal(bits64Before + 1, ValuePools.OfBits64.Count);

        // heap integer → string（旧 Bits64 slot 应被释放）
        int ownedBefore = ValuePools.OfOwnedString.Count;
        dict.Upsert(1, "now string");
        Assert.True(dict.TryGetValueKind(1, out var ks));
        Assert.Equal(ValueKind.String, ks);
        Assert.Equal(bits64Before, ValuePools.OfBits64.Count);
        Assert.Equal(ownedBefore + 1, ValuePools.OfOwnedString.Count);
        Assert.Equal(GetIssue.None, dict.OfString.Get(1, out string? sv));
        Assert.Equal("now string", sv);

        // string → heap integer（反向：OwnedString slot 应被释放）
        dict.Upsert(1, ulong.MaxValue - 1);
        Assert.True(dict.TryGetValueKind(1, out var ki2));
        Assert.Equal(ValueKind.NonnegativeInteger, ki2);
        Assert.Equal(bits64Before + 1, ValuePools.OfBits64.Count);
        Assert.Equal(ownedBefore, ValuePools.OfOwnedString.Count);

        Assert.True(dict.Remove(1));
        Assert.Equal(bits64Before, ValuePools.OfBits64.Count);
    }

    [Fact]
    public void MixedDict_String_DirtyMutation_PoolStaysBalanced() {
        var rev = CreateRevision();
        var dict = rev.CreateDict<int>();

        int ownedBefore = ValuePools.OfOwnedString.Count;
        dict.Upsert(1, "short");

        // 多次改不同长度，inplace 复用同一 slot（exclusive 路径）。
        dict.Upsert(1, "much longer string ......");
        dict.Upsert(1, "x");
        dict.Upsert(1, "");
        dict.Upsert(1, "final");

        Assert.Equal(ownedBefore + 1, ValuePools.OfOwnedString.Count);

        // Remove 释放 slot
        Assert.True(dict.Remove(1));
        Assert.Equal(ownedBefore, ValuePools.OfOwnedString.Count);
    }

    [Fact]
    public void MixedDict_String_DiscardChanges_RestoresCommittedStringAndReleasesDirtySlot() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.Upsert(1, "base");
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        int ownedAfterCommit = ValuePools.OfOwnedString.Count;
        root.Upsert(1, "much longer dirty value");
        Assert.Equal(ownedAfterCommit + 1, ValuePools.OfOwnedString.Count);

        root.DiscardChanges();

        Assert.Equal(ownedAfterCommit, ValuePools.OfOwnedString.Count);
        Assert.Equal(GetIssue.None, root.OfString.Get(1, out string? value));
        Assert.Equal("base", value);
    }

    [Fact]
    public void MixedDict_String_ForkCommittedAsMutable_RoundTripsIndependentStrings() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int>>();
        var source = rev.CreateDict<int>();
        source.Upsert(1, "source");
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        int ownedBeforeFork = ValuePools.OfOwnedString.Count;
        var fork = source.ForkCommittedAsMutable();
        Assert.Equal(ownedBeforeFork + 1, ValuePools.OfOwnedString.Count); // frozen StringPayload deep clone

        fork.Upsert(1, "fork");
        root.Upsert(2, fork);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int>>>(opened.GraphRoot);

        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int>? loadedFork));
        Assert.Equal(GetIssue.None, loadedSource!.OfString.Get(1, out string? sourceValue));
        Assert.Equal(GetIssue.None, loadedFork!.OfString.Get(1, out string? forkValue));
        Assert.Equal("source", sourceValue);
        Assert.Equal("fork", forkValue);
    }

    [Fact]
    public void MixedDict_String_Commit_Open_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int>();
        root.Upsert(1, "");
        root.Upsert(2, "中文 + 🌟 emoji");
        root.Upsert(3, "ascii only");
        root.OfSymbol.Upsert(4, "interned");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));

        var opened = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(opened.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);

        Assert.Equal(GetIssue.None, loaded.OfString.Get(1, out string? s1));
        Assert.Equal("", s1);
        Assert.Equal(GetIssue.None, loaded.OfString.Get(2, out string? s2));
        Assert.Equal("中文 + 🌟 emoji", s2);
        Assert.Equal(GetIssue.None, loaded.OfString.Get(3, out string? s3));
        Assert.Equal("ascii only", s3);

        // ValueKind 应严格区分
        Assert.True(loaded.TryGetValueKind(1, out var k1));
        Assert.Equal(ValueKind.String, k1);
        Assert.True(loaded.TryGetValueKind(4, out var k4));
        Assert.Equal(ValueKind.Symbol, k4);

        // string ↔ Symbol 互读 TypeMismatch 在 reopen 后仍成立
        Assert.Equal(GetIssue.TypeMismatch, loaded.OfSymbol.Get(2, out Symbol _));
        Assert.Equal(GetIssue.TypeMismatch, loaded.OfString.Get(4, out string? _));
    }

    // ═══════════════════════ MixedDeque ═══════════════════════

    [Fact]
    public void MixedDeque_String_PushPop_RoundTrip() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();

        deque.PushBack("alpha");
        deque.OfString.PushFront("zero");
        deque.OfString.PushBack("omega");

        Assert.Equal(3, deque.Count);
        Assert.Equal(GetIssue.None, deque.OfString.GetAt(0, out string? a0));
        Assert.Equal("zero", a0);
        Assert.Equal(GetIssue.None, deque.OfString.GetAt(1, out string? a1));
        Assert.Equal("alpha", a1);
        Assert.Equal(GetIssue.None, deque.OfString.GetAt(2, out string? a2));
        Assert.Equal("omega", a2);

        // Pop 释放 owned slot
        int ownedBefore = ValuePools.OfOwnedString.Count;
        Assert.Equal(GetIssue.None, deque.OfString.PopFront(out string? popped));
        Assert.Equal("zero", popped);
        Assert.Equal(ownedBefore - 1, ValuePools.OfOwnedString.Count);
    }

    [Fact]
    public void MixedDeque_String_VsSymbol_TypeIsolation() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();

        deque.OfSymbol.PushBack("sym");
        deque.OfString.PushBack("str");

        Assert.Equal(GetIssue.None, deque.OfSymbol.GetAt(0, out Symbol sv));
        Assert.Equal("sym", sv.Value);
        Assert.Equal(GetIssue.TypeMismatch, deque.OfString.GetAt(0, out string? _));

        Assert.Equal(GetIssue.None, deque.OfString.GetAt(1, out string? str));
        Assert.Equal("str", str);
        Assert.Equal(GetIssue.TypeMismatch, deque.OfSymbol.GetAt(1, out Symbol _));
    }

    [Fact]
    public void MixedDeque_String_Commit_Open_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var deque = rev.CreateDeque();
        deque.OfString.PushBack("first");
        deque.OfString.PushBack("");
        deque.OfString.PushBack("中文");
        deque.OfSymbol.PushBack("sym-tail");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, deque, file));

        var opened = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDeque>(opened.Value!.GraphRoot);
        Assert.Equal(4, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.OfString.GetAt(0, out string? s0));
        Assert.Equal("first", s0);
        Assert.Equal(GetIssue.None, loaded.OfString.GetAt(1, out string? s1));
        Assert.Equal("", s1);
        Assert.Equal(GetIssue.None, loaded.OfString.GetAt(2, out string? s2));
        Assert.Equal("中文", s2);
        Assert.Equal(GetIssue.None, loaded.OfSymbol.GetAt(3, out Symbol s3));
        Assert.Equal("sym-tail", s3.Value);
    }

    // ═══════════════════════ MixedOrderedDict ═══════════════════════

    [Fact]
    public void MixedOrderedDict_String_Upsert_Get_RoundTrip() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.Upsert(2, "two");
        dict.OfString.Upsert(1, "one");
        dict.OfString.Upsert(3, "three");

        Assert.Equal(GetIssue.None, dict.OfString.Get(1, out string? v1));
        Assert.Equal("one", v1);
        Assert.Equal(GetIssue.None, dict.OfString.Get(2, out string? v2));
        Assert.Equal("two", v2);
        Assert.Equal(GetIssue.None, dict.OfString.Get(3, out string? v3));
        Assert.Equal("three", v3);

        Assert.True(dict.TryGetValueKind(2, out var k));
        Assert.Equal(ValueKind.String, k);
    }

    [Fact]
    public void MixedOrderedDict_String_VsSymbol_TypeIsolation() {
        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();

        dict.OfSymbol.Upsert(1, "shared");
        dict.OfString.Upsert(2, "shared");

        Assert.Equal(GetIssue.TypeMismatch, dict.OfString.Get(1, out string? _));
        Assert.Equal(GetIssue.TypeMismatch, dict.OfSymbol.Get(2, out Symbol _));
    }

    [Fact]
    public void MixedOrderedDict_String_Commit_Open_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var dict = rev.CreateOrderedDict<int>();
        dict.OfString.Upsert(1, "alpha");
        dict.OfString.Upsert(2, "");
        dict.OfString.Upsert(3, "中文 🌟");

        var outcome = AssertCommitSucceeded(CommitToFile(rev, dict, file));

        var opened = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");

        var loaded = Assert.IsAssignableFrom<DurableOrderedDict<int>>(opened.Value!.GraphRoot);
        Assert.Equal(GetIssue.None, loaded.OfString.Get(1, out string? v1));
        Assert.Equal("alpha", v1);
        Assert.Equal(GetIssue.None, loaded.OfString.Get(2, out string? v2));
        Assert.Equal("", v2);
        Assert.Equal(GetIssue.None, loaded.OfString.Get(3, out string? v3));
        Assert.Equal("中文 🌟", v3);
    }
}
