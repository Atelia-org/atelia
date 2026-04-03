using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class TypeCodecTests {

    // ═══════════════════════ 辅助 ═══════════════════════

    private static bool Decode(byte[] bytes, out Type? result) =>
        TypeCodec.TryDecode(bytes, out result);

    // ═══════════════════════ 空 / 无效输入 ═══════════════════════

    [Fact]
    public void Empty_ReturnsFalse() {
        Assert.False(Decode([], out _));
    }

    [Fact]
    public void InvalidOpCode_ReturnsFalse() {
        Assert.False(Decode([(byte)TypeOpCode.Invalid], out _));
    }

    [Fact]
    public void UnknownOpCode_ReturnsFalse() {
        // 选一个不在枚举定义中的值
        Assert.False(Decode([255], out _));
    }

    // ═══════════════════════ Push 叶子类型 ═══════════════════════

    [Theory]
    [InlineData((byte)TypeOpCode.PushByte, typeof(byte))]
    [InlineData((byte)TypeOpCode.PushUInt16, typeof(ushort))]
    [InlineData((byte)TypeOpCode.PushUInt32, typeof(uint))]
    [InlineData((byte)TypeOpCode.PushUInt64, typeof(ulong))]
    [InlineData((byte)TypeOpCode.PushSByte, typeof(sbyte))]
    [InlineData((byte)TypeOpCode.PushInt16, typeof(short))]
    [InlineData((byte)TypeOpCode.PushInt32, typeof(int))]
    [InlineData((byte)TypeOpCode.PushInt64, typeof(long))]
    [InlineData((byte)TypeOpCode.PushBoolean, typeof(bool))]
    [InlineData((byte)TypeOpCode.PushHalf, typeof(Half))]
    [InlineData((byte)TypeOpCode.PushSingle, typeof(float))]
    [InlineData((byte)TypeOpCode.PushDouble, typeof(double))]
    [InlineData((byte)TypeOpCode.PushString, typeof(string))]
    public void PushLeaf_DecodesCorrectType(byte op, Type expected) {
        Assert.True(Decode([op], out var result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PushMixedDeque_DecodesDurableDeque() {
        Assert.True(Decode([(byte)TypeOpCode.PushMixedDeque], out var result));
        Assert.Equal(typeof(DurableDeque), result);
    }

    [Fact]
    public void MakeValueTuple2_Int32String() {
        byte[] bytes = [
            (byte)TypeOpCode.PushString,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.MakeValueTuple2
        ];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(ValueTuple<int, string>), result);
    }

    [Fact]
    public void MakeValueTuple3_Int32UInt32Boolean() {
        byte[] bytes = [
            (byte)TypeOpCode.PushBoolean,
            (byte)TypeOpCode.PushUInt32,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.MakeValueTuple3
        ];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(ValueTuple<int, uint, bool>), result);
    }

    [Fact]
    public void MakeNestedValueTuple2_DecodesCorrectly() {
        byte[] bytes = [
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.MakeValueTuple2,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.MakeValueTuple2
        ];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(ValueTuple<int, ValueTuple<int, int>>), result);
    }

    [Fact]
    public void MakeValueTuple2_InsufficientOperands_ReturnsFalse() {
        byte[] bytes = [(byte)TypeOpCode.PushInt32, (byte)TypeOpCode.MakeValueTuple2];
        Assert.False(Decode(bytes, out _));
    }

    [Fact]
    public void MakeValueTuple3_InsufficientOperands_ReturnsFalse() {
        byte[] bytes = [(byte)TypeOpCode.PushInt32, (byte)TypeOpCode.PushInt32, (byte)TypeOpCode.MakeValueTuple3];
        Assert.False(Decode(bytes, out _));
    }

    [Fact]
    public void MakeValueTuple7_AllInt32_DecodesCorrectly() {
        byte[] bytes = [
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.MakeValueTuple7
        ];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(ValueTuple<int, int, int, int, int, int, int>), result);
    }

    // ═══════════════════════ MakeTypedDeque ═══════════════════════

    [Fact]
    public void MakeTypedDeque_Int32() {
        byte[] bytes = [(byte)TypeOpCode.PushInt32, (byte)TypeOpCode.MakeTypedDeque];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(DurableDeque<int>), result);
    }

    [Fact]
    public void MakeTypedDeque_String() {
        byte[] bytes = [(byte)TypeOpCode.PushString, (byte)TypeOpCode.MakeTypedDeque];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(DurableDeque<string>), result);
    }

    [Fact]
    public void MakeTypedDeque_InsufficientOperands_ReturnsFalse() {
        byte[] bytes = [(byte)TypeOpCode.MakeTypedDeque];
        Assert.False(Decode(bytes, out _));
    }

    // ═══════════════════════ MakeMixedDict (→ DurableDict<TKey>) ═══════════════════════

    [Fact]
    public void MakeMixedDict_StringKey() {
        byte[] bytes = [(byte)TypeOpCode.PushString, (byte)TypeOpCode.MakeMixedDict];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(DurableDict<string>), result);
    }

    [Fact]
    public void MakeMixedDict_InsufficientOperands_ReturnsFalse() {
        byte[] bytes = [(byte)TypeOpCode.MakeMixedDict];
        Assert.False(Decode(bytes, out _));
    }

    // ═══════════════════════ MakeTypedDict (→ DurableDict<TKey,TValue>) ═══════════════════════

    [Fact]
    public void MakeTypedDict_StringKey_Int32Value() {
        // 编码顺序：右向左压入泛型参数 → 先 TValue 再 TKey
        byte[] bytes = [
            (byte)TypeOpCode.PushInt32,   // TValue (先压)
            (byte)TypeOpCode.PushString,  // TKey   (后压 → 栈顶)
            (byte)TypeOpCode.MakeTypedDict
        ];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(DurableDict<string, int>), result);
    }

    [Fact]
    public void MakeTypedDict_InsufficientOperands_One_ReturnsFalse() {
        byte[] bytes = [(byte)TypeOpCode.PushString, (byte)TypeOpCode.MakeTypedDict];
        Assert.False(Decode(bytes, out _));
    }

    [Fact]
    public void MakeTypedDict_InsufficientOperands_Zero_ReturnsFalse() {
        byte[] bytes = [(byte)TypeOpCode.MakeTypedDict];
        Assert.False(Decode(bytes, out _));
    }

    // ═══════════════════════ 嵌套复合类型 ═══════════════════════

    [Fact]
    public void NestedTypedDeque_DequeOfDequeOfInt() {
        // DurableDeque<DurableDeque<int>>
        byte[] bytes = [
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.MakeTypedDeque,   // DurableDeque<int>
            (byte)TypeOpCode.MakeTypedDeque    // DurableDeque<DurableDeque<int>>
        ];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(DurableDeque<DurableDeque<int>>), result);
    }

    [Fact]
    public void NestedDict_DictOfStringToTypedDeque() {
        // DurableDict<string, DurableDeque<double>>
        byte[] bytes = [
            (byte)TypeOpCode.PushDouble,
            (byte)TypeOpCode.MakeTypedDeque,    // DurableDeque<double> → TValue
            (byte)TypeOpCode.PushString,       // TKey
            (byte)TypeOpCode.MakeTypedDict
        ];
        Assert.True(Decode(bytes, out var result));
        Assert.Equal(typeof(DurableDict<string, DurableDeque<double>>), result);
    }

    // ═══════════════════════ 栈残余检查 ═══════════════════════

    [Fact]
    public void MultipleLeaves_NoMake_ReturnsFalse() {
        // 栈上留下两个类型 → operands.Count != 1
        byte[] bytes = [(byte)TypeOpCode.PushInt32, (byte)TypeOpCode.PushString];
        Assert.False(Decode(bytes, out _));
    }

    [Fact]
    public void ExtraOperands_ReturnsFalse() {
        // 构造完还剩余操作数
        byte[] bytes = [
            (byte)TypeOpCode.PushInt32,
            (byte)TypeOpCode.PushString,
            (byte)TypeOpCode.MakeMixedDict  // 只消耗一个，栈上还剩 int
        ];
        Assert.False(Decode(bytes, out _));
    }
}
