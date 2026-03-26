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

    #region DurableRef round-trip

    private static byte[] WriteDurableRef(DurableObjectKind kind, LocalId id) {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.TaggedDurableRef(new DurableRef(kind, id));
        return buffer.WrittenSpan.ToArray();
    }

    [Fact]
    public void DurableRef_NarrowId_RoundTrips() {
        byte[] data = WriteDurableRef(DurableObjectKind.MixedDict, new LocalId(42));
        Assert.Equal(3, data.Length); // 1 tag + 2 payload

        ValueBox box = Init(data);
        Assert.Equal(GetIssue.None, ValueBox.DurableRefFace.Get(box, out var value));
        Assert.Equal(DurableObjectKind.MixedDict, value.Kind);
        Assert.Equal(42u, value.Id.Value);
    }

    [Fact]
    public void DurableRef_WideId_RoundTrips() {
        uint wideId = (uint)ushort.MaxValue + 100;
        byte[] data = WriteDurableRef(DurableObjectKind.TypedDeque, new LocalId(wideId));
        Assert.Equal(5, data.Length); // 1 tag + 4 payload

        ValueBox box = Init(data);
        Assert.Equal(GetIssue.None, ValueBox.DurableRefFace.Get(box, out var value));
        Assert.Equal(DurableObjectKind.TypedDeque, value.Kind);
        Assert.Equal(wideId, value.Id.Value);
    }

    [Theory]
    [InlineData(DurableObjectKind.MixedDict)]
    [InlineData(DurableObjectKind.TypedDict)]
    [InlineData(DurableObjectKind.MixedDeque)]
    [InlineData(DurableObjectKind.TypedDeque)]
    public void DurableRef_AllKinds_RoundTrip(DurableObjectKind kind) {
        byte[] data = WriteDurableRef(kind, new LocalId(1));
        ValueBox box = Init(data);
        Assert.Equal(GetIssue.None, ValueBox.DurableRefFace.Get(box, out var value));
        Assert.Equal(kind, value.Kind);
        Assert.Equal(1u, value.Id.Value);
    }

    [Fact]
    public void DurableRef_BoundaryId_UInt16Max_UsesNarrow() {
        byte[] data = WriteDurableRef(DurableObjectKind.MixedDeque, new LocalId(ushort.MaxValue));
        Assert.Equal(3, data.Length); // still narrow

        ValueBox box = Init(data);
        Assert.Equal(GetIssue.None, ValueBox.DurableRefFace.Get(box, out var value));
        Assert.Equal(ushort.MaxValue, (ushort)value.Id.Value);
    }

    [Fact]
    public void DurableRef_BoundaryId_UInt16MaxPlus1_UsesWide() {
        uint id = (uint)ushort.MaxValue + 1;
        byte[] data = WriteDurableRef(DurableObjectKind.TypedDict, new LocalId(id));
        Assert.Equal(5, data.Length); // wide

        ValueBox box = Init(data);
        Assert.Equal(GetIssue.None, ValueBox.DurableRefFace.Get(box, out var value));
        Assert.Equal(id, value.Id.Value);
    }

    [Fact]
    public void DurableRef_Update_SameValue_ReturnsFalse() {
        byte[] data = WriteDurableRef(DurableObjectKind.MixedDict, new LocalId(7));
        ValueBox box = Init(data);

        var reader2 = new BinaryDiffReader(data);
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader2, ref box);
        Assert.False(changed);
    }

    [Fact]
    public void DurableRef_InvalidKind_ThrowsInvalidDataException() {
        Assert.Throws<System.IO.InvalidDataException>(() => Init([0xBE, 0x01, 0x00]));
    }

    [Fact]
    public void DurableRef_NullId_ThrowsInvalidDataException() {
        Assert.Throws<System.IO.InvalidDataException>(() => Init([0xA2, 0x00, 0x00]));
    }

    [Fact]
    public void WriteDurableRef_BlankKind_ThrowsInvalidDataException() {
        Assert.Throws<System.IO.InvalidDataException>(
            () => {
                var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                var writer = new BinaryDiffWriter(buffer);
                writer.TaggedDurableRef(new DurableRef(DurableObjectKind.Blank, new LocalId(1)));
            }
        );
    }

    [Fact]
    public void WriteDurableRef_NullId_ThrowsInvalidDataException() {
        Assert.Throws<System.IO.InvalidDataException>(
            () => {
                var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                var writer = new BinaryDiffWriter(buffer);
                writer.TaggedDurableRef(new DurableRef(DurableObjectKind.MixedDict, LocalId.Null));
            }
        );
    }

    [Fact]
    public void TaggedRefKind_FromDurableObjectKind_PreservesAlignedValues() {
        Assert.Equal((byte)DurableObjectKind.MixedDict, (byte)TaggedRefKindHelper.FromDurableObjectKind(DurableObjectKind.MixedDict));
        Assert.Equal((byte)DurableObjectKind.TypedDict, (byte)TaggedRefKindHelper.FromDurableObjectKind(DurableObjectKind.TypedDict));
        Assert.Equal((byte)DurableObjectKind.MixedDeque, (byte)TaggedRefKindHelper.FromDurableObjectKind(DurableObjectKind.MixedDeque));
        Assert.Equal((byte)DurableObjectKind.TypedDeque, (byte)TaggedRefKindHelper.FromDurableObjectKind(DurableObjectKind.TypedDeque));
    }

    [Fact]
    public void TaggedRefKind_TryToDurableObjectKind_RejectsSymbol() {
        Assert.False(TaggedRefKindHelper.TryToDurableObjectKind(TaggedRefKind.Symbol, out _));
    }

    #endregion

    #region SymbolId round-trip

    private static byte[] WriteSymbolId(SymbolId id) {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.TaggedSymbolId(id);
        return buffer.WrittenSpan.ToArray();
    }

    [Fact]
    public void SymbolId_NarrowId_RoundTrips() {
        byte[] data = WriteSymbolId(new SymbolId(42));
        Assert.Equal(3, data.Length); // 1 tag + 2 payload

        ValueBox box = Init(data);
        Assert.Equal(GetIssue.None, ValueBox.GetSymbolId(box, out var id));
        Assert.Equal(42u, id.Value);
    }

    [Fact]
    public void SymbolId_WideId_RoundTrips() {
        uint wideValue = (uint)ushort.MaxValue + 100;
        byte[] data = WriteSymbolId(new SymbolId(wideValue));
        Assert.Equal(5, data.Length); // 1 tag + 4 payload

        ValueBox box = Init(data);
        Assert.Equal(GetIssue.None, ValueBox.GetSymbolId(box, out var id));
        Assert.Equal(wideValue, id.Value);
    }

    [Fact]
    public void SymbolId_BoundaryId_UInt16Max_UsesNarrow() {
        byte[] data = WriteSymbolId(new SymbolId(ushort.MaxValue));
        Assert.Equal(3, data.Length); // still narrow

        ValueBox box = Init(data);
        Assert.Equal(GetIssue.None, ValueBox.GetSymbolId(box, out var id));
        Assert.Equal((uint)ushort.MaxValue, id.Value);
    }

    [Fact]
    public void SymbolId_BoundaryId_UInt16MaxPlus1_UsesWide() {
        uint value = (uint)ushort.MaxValue + 1;
        byte[] data = WriteSymbolId(new SymbolId(value));
        Assert.Equal(5, data.Length); // wide

        ValueBox box = Init(data);
        Assert.Equal(GetIssue.None, ValueBox.GetSymbolId(box, out var id));
        Assert.Equal(value, id.Value);
    }

    [Fact]
    public void SymbolId_Update_SameValue_ReturnsFalse() {
        byte[] data = WriteSymbolId(new SymbolId(7));
        ValueBox box = Init(data);

        var reader2 = new BinaryDiffReader(data);
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader2, ref box);
        Assert.False(changed);
    }

    [Fact]
    public void SymbolId_Update_DifferentValue_ReturnsTrue() {
        byte[] data1 = WriteSymbolId(new SymbolId(7));
        byte[] data2 = WriteSymbolId(new SymbolId(42));
        ValueBox box = Init(data1);

        var reader2 = new BinaryDiffReader(data2);
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader2, ref box);
        Assert.True(changed);

        Assert.Equal(GetIssue.None, ValueBox.GetSymbolId(box, out var id));
        Assert.Equal(42u, id.Value);
    }

    [Fact]
    public void WriteSymbolId_NullId_Throws() {
        Assert.Throws<System.IO.InvalidDataException>(
            () => {
                var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                var writer = new BinaryDiffWriter(buffer);
                writer.TaggedSymbolId(SymbolId.Null);
            }
        );
    }

    [Fact]
    public void SymbolId_DoesNotConfuseWithDurableRef() {
        // Write a SymbolId and a DurableRef with same id=42, verify they decode as different types
        byte[] symbolData = WriteSymbolId(new SymbolId(42));
        byte[] durableData = WriteDurableRef(DurableObjectKind.MixedDict, new LocalId(42));

        ValueBox symBox = Init(symbolData);
        ValueBox durBox = Init(durableData);

        // SymbolId can be extracted from symBox
        Assert.Equal(GetIssue.None, ValueBox.GetSymbolId(symBox, out var symId));
        Assert.Equal(42u, symId.Value);
        // DurableRef extraction from symBox should fail (it's a string HeapSlot, not a DurableRef)
        Assert.Equal(GetIssue.TypeMismatch, ValueBox.DurableRefFace.Get(symBox, out _));

        // DurableRef can be extracted from durBox
        Assert.Equal(GetIssue.None, ValueBox.DurableRefFace.Get(durBox, out var durRef));
        Assert.Equal(42u, durRef.Id.Value);
        // SymbolId extraction from durBox should fail (it's a DurableRef, not a HeapSlot)
        Assert.Equal(GetIssue.TypeMismatch, ValueBox.GetSymbolId(durBox, out _));
    }

    #endregion
}
