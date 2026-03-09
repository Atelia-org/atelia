using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Serialization.Tests;

public class TaggedValueDispatcherTests {
    private static ValueBox Init(byte[] data) {
        var reader = new BinaryDiffReader(data);
        ValueBox box = default;
        TaggedValueDispatcher.UpdateOrInit(ref reader, ref box);
        return box;
    }

    [Fact]
    public void Init_NonnegativeInteger_DecodesToValueBox() {
        ValueBox box = Init([0x19, 0x34, 0x12]);

        Assert.Equal(GetIssue.None, ValueBox.UInt64Face.Get(box, out ulong value));
        Assert.Equal(0x1234UL, value);
    }

    [Fact]
    public void Init_NegativeInteger_UsesCborStyleMapping() {
        ValueBox box = Init([0x38, 0x18]);

        Assert.Equal(GetIssue.None, ValueBox.Int64Face.Get(box, out long value));
        Assert.Equal(-25L, value);
    }

    [Fact]
    public void Init_HalfFloat_PreservesValue() {
        ValueBox box = Init([0xF9, 0x00, 0x3E]);

        Assert.Equal(GetIssue.None, ValueBox.HalfFace.Get(box, out Half value));
        Assert.Equal(BitConverter.HalfToUInt16Bits((Half)1.5), BitConverter.HalfToUInt16Bits(value));
    }

    [Fact]
    public void Init_SimpleValues_DecodesBooleanAndNull() {
        ValueBox falseBox = Init([0xF4]);
        ValueBox trueBox = Init([0xF5]);
        ValueBox nullBox = Init([0xF6]);

        Assert.Equal(GetIssue.None, ValueBox.BooleanFace.Get(falseBox, out bool falseValue));
        Assert.False(falseValue);
        Assert.Equal(GetIssue.None, ValueBox.BooleanFace.Get(trueBox, out bool trueValue));
        Assert.True(trueValue);
        Assert.True(nullBox.IsNull);
    }

    [Fact]
    public void Init_UnsupportedHead_ThrowsInvalidDataException() {
        Assert.Throws<System.IO.InvalidDataException>(() => Init([0x60]));
    }

    [Fact]
    public void Update_SameValue_ReturnsFalse() {
        ValueBox box = Init([0x19, 0x34, 0x12]);

        var reader2 = new BinaryDiffReader([0x19, 0x34, 0x12]);
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader2, ref box);

        Assert.False(changed);
    }

    [Fact]
    public void Update_DifferentValue_ReturnsTrueAndUpdates() {
        ValueBox box = Init([0x05]);

        var reader2 = new BinaryDiffReader([0x0A]);
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader2, ref box);

        Assert.True(changed);
        Assert.Equal(GetIssue.None, ValueBox.UInt64Face.Get(box, out ulong value));
        Assert.Equal(10UL, value);
    }

    [Fact]
    public void Update_CrossKind_FromIntToNull_ReturnsTrueAndUpdates() {
        ValueBox box = Init([0x05]);

        var reader2 = new BinaryDiffReader([0xF6]);
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader2, ref box);

        Assert.True(changed);
        Assert.True(box.IsNull);
    }
}
