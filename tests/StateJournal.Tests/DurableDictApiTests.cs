using Xunit;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Tests;

public class DurableDictApiTests {
    [Fact]
    public void MixedDict_SupportsGenericAndTypedViews() {
        var rev = new Revision(1);
        var dict = rev.CreateDict<string>();

        dict.Upsert("count", 42);
        dict.OfSymbol.Upsert("title", "draft");

        Assert.True(dict.TryGet("count", out int count));
        Assert.Equal(42, count);
        Assert.True(dict.TryGet("title", out Symbol title));
        Assert.Equal("draft", title.Value);

        dict.OfInt32.Upsert("count", 99);
        dict.OfSymbol.Upsert("title", "v2");

        Assert.Equal(99, dict.GetOrThrow<int>("count"));
        Assert.Equal("v2", dict.OfSymbol.Get("title"));
        Assert.Equal(GetIssue.None, dict.OfInt32.Get("count", out int exactCount));
        Assert.Equal(99, exactCount);
    }

    [Fact]
    public void MixedDict_TypedView_TypeMismatch_ReturnsIssue() {
        var rev = new Revision(1);
        var dict = rev.CreateDict<string>();
        dict.OfSymbol.Upsert("title", "memo");

        Assert.Equal(GetIssue.TypeMismatch, dict.OfInt32.Get("title", out int _));
        Assert.Throws<InvalidCastException>(() => dict.OfInt32.Get("title"));
    }

    [Fact]
    public void MixedDict_StringLiteral_GoesToPayload_NotSymbolIntern() {
        var rev = new Revision(1);
        var dict = rev.CreateDict<string>();

        int symbolBefore = rev.SymbolPoolCount;
        dict.Upsert("title", "draft");

        // string 字面量推断为 TValue=string，走 payload 路径，不进 intern 池。
        Assert.Equal(symbolBefore, rev.SymbolPoolCount);
        Assert.True(dict.TryGetValueKind("title", out var kind));
        Assert.Equal(ValueKind.String, kind);
        Assert.Equal(GetIssue.None, dict.OfString.Get("title", out string? value));
        Assert.Equal("draft", value);
        // Symbol 视图读取不到（两者不互通）。
        Assert.Equal(GetIssue.TypeMismatch, dict.OfSymbol.Get("title", out Symbol _));
    }

    [Fact]
    public void MixedDict_DurableSubtype_GenericConvenienceApis_WorkWithoutOfView() {
        var rev = new Revision(1);
        var dict = rev.CreateDict<string>();
        var child = rev.CreateDict<int, int>();
        child.Upsert(7, 70);

        dict.Upsert("child", child);

        Assert.True(dict.TryGet("child", out DurableDict<int, int>? loaded));
        Assert.Same(child, loaded);
        Assert.Same(child, dict.GetOrThrow<DurableDict<int, int>>("child"));
    }

    [Fact]
    public void MixedDict_OfDurableSubtype_RemainsUnsupported_ToKeepExactViewSemantics() {
        var dict = Durable.Dict<string>();

        Assert.Throws<NotSupportedException>(() => dict.Of<DurableDict<int, int>>());
    }

    [Fact]
    public void MixedDict_DurableSubtypeAccess_AllowsNullValue() {
        var dict = Durable.Dict<string>();
        dict.Upsert("child", (DurableObject?)null);

        Assert.True(dict.TryGet("child", out DurableDict<int, int>? loaded));
        Assert.Null(loaded);
        Assert.Null(dict.GetOrThrow<DurableDict<int, int>>("child"));
    }
}
