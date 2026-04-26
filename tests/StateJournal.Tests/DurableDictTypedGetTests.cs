using Xunit;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Tests;

public class DurableDictTypedGetTests {
    [Fact]
    public void TryGet_UnsupportedType_ReturnsFalseAndDefault() {
        var model = Durable.Dict<Symbol>();
        model.Upsert("zoom", 1.5);

        bool ok = model.TryGet("zoom", out DateTime value);

        Assert.False(ok);
        Assert.Equal(default, value);
    }

    [Fact]
    public void Get_UnsupportedType_ThrowsNotSupportedException() {
        var model = Durable.Dict<Symbol>();
        model.Upsert("zoom", 1.5);

        Assert.Throws<NotSupportedException>(() => model.GetOrThrow<DateTime>("zoom"));
    }
}
