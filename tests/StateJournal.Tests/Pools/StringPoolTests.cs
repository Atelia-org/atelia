using Xunit;

namespace Atelia.StateJournal.Pools.Tests;

public class StringPoolTests {
    [Fact]
    // [InlineData(typeof(StringPoolDirect64x4))]
    public void StoreVariant_SameLongStringReference_ReusesHandle() {
        var pool = new StringPool();
        string value = new('p', 88);

        SlotHandle h1 = Store(pool, value);
        SlotHandle h2 = Store(pool, value);

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    // [InlineData(typeof(StringPoolDirect64x4))]
    public void StoreVariant_LongStringsWithSameContent_DeduplicatesByContent() {
        var pool = new StringPool();
        string a = new('r', 72);
        string b = string.Concat(new('r', 48), new('r', 24));

        SlotHandle h1 = Store(pool, a);
        SlotHandle h2 = Store(pool, b);

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Store_SameLongStringReference_ReusesHandle() {
        var pool = new StringPool();
        string value = new('x', 64);

        SlotHandle h1 = pool.Store(value);
        SlotHandle h2 = pool.Store(value);

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Store_SameLongStringReference_AfterSweep_ReusesCachedHash() {
        var pool = new StringPool();
        string value = new('y', 80);

        SlotHandle oldHandle = pool.Store(value);

        pool.BeginMark();
        Assert.Equal(1, pool.Sweep());
        Assert.False(pool.Validate(oldHandle));

        SlotHandle newHandle = pool.Store(value);

        Assert.True(pool.Validate(newHandle));
        Assert.Equal(value, pool[newHandle]);
    }

    [Fact]
    public void Store_SameLongStringReference_AcrossManySweepCycles_RemainsCorrect() {
        var pool = new StringPool();
        string value = new('w', 96);

        for (int i = 0; i < 300; i++) {
            SlotHandle handle = pool.Store(value);
            Assert.True(pool.Validate(handle));
            Assert.Equal(value, pool[handle]);

            pool.BeginMark();
            Assert.Equal(1, pool.Sweep());
            Assert.False(pool.Validate(handle));
        }

        SlotHandle finalHandle = pool.Store(value);
        Assert.True(pool.Validate(finalHandle));
        Assert.Equal(value, pool[finalHandle]);
    }

    [Fact]
    public void Store_LongStringsWithSameContent_DeduplicatesByContent() {
        var pool = new StringPool();
        string a = new('z', 72);
        string b = string.Concat(new('z', 36), new('z', 36));

        SlotHandle h1 = pool.Store(a);
        SlotHandle h2 = pool.Store(b);

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Store_ShortStrings_PassThroughToInnerPool() {
        var pool = new StringPool();
        string value = new('q', 16);

        SlotHandle h1 = pool.Store(value);
        SlotHandle h2 = pool.Store(string.Concat("qqqq", "qqqq", "qqqq", "qqqq"));

        Assert.Equal(h1, h2);
        Assert.Equal(1, pool.Count);
    }

    private static SlotHandle Store(StringPool pool, string value) => pool.Store(value);
}
