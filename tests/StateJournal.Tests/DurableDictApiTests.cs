using Xunit;

namespace Atelia.StateJournal.Tests;

public class DurableDictApiTests {
    [Fact]
    public void MixedDict_SupportsGenericAndTypedViews() {
        var dict = Durable.Dict<string>();

        dict.Upsert("count", 42);
        dict.Upsert("title", "draft");

        Assert.True(dict.TryGet("count", out int count));
        Assert.Equal(42, count);
        Assert.True(dict.TryGet("title", out string? title));
        Assert.Equal("draft", title);

        dict.OfInt32.Upsert("count", 99);
        dict.OfString.Upsert("title", "v2");

        Assert.Equal(99, dict.GetOrThrow<int>("count"));
        Assert.Equal("v2", dict.OfString.Get("title"));
        Assert.Equal(GetIssue.None, dict.OfInt32.Get("count", out int exactCount));
        Assert.Equal(99, exactCount);
    }

    [Fact]
    public void MixedDict_TypedView_TypeMismatch_ReturnsIssue() {
        var dict = Durable.Dict<string>();
        dict.Upsert("title", "memo");

        Assert.Equal(GetIssue.TypeMismatch, dict.OfInt32.Get("title", out int _));
        Assert.Throws<InvalidCastException>(() => dict.OfInt32.Get("title"));
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
