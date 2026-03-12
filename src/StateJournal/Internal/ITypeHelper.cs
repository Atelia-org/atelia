using System.Diagnostics;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal interface ITypeHelper<T> where T : notnull {
    static abstract bool Equals(T? a, T? b);

    /// <summary>冻结一个值用于 Commit 时共享。对于无堆资源的类型直接返回原值。
    /// 目前仅有ValueBox由于堆上值的Share/Exclusive语义需要。</summary>
    static virtual T? Freeze(T? value) => value;

    static virtual bool NeedRelease => false;
    /// <summary>释放值持有的堆 Slot。调用者确保是最后持有者。对于无堆资源类型无操作。
    /// 目前仅有ValueBox由于堆上值的Share/Exclusive语义需要。</summary>
    static virtual void ReleaseSlot(T? value) { }
    // static abstract List<T> GetTempList(DiffWriteContext context);

    static abstract void Write(IDiffWriter writer, T? v, bool asKey);
    static abstract T? Read(ref BinaryDiffReader reader, bool asKey);

    /// <summary>未来如果需要获知是否改变了old（update or init），可以将返回值改为bool并添加`bool exists参数`，参考实现：
    /// `if (exists && Equals(old, readedNewValue)) { return false; }`</summary>
    static abstract void UpdateOrInit(ref BinaryDiffReader reader, ref T? old);
}

#region for MixedDict value
internal readonly struct ValueBoxHelper : ITypeHelper<ValueBox> {
    public static bool Equals(ValueBox a, ValueBox b) => ValueBox.ValueEquals(a, b);
    public static ValueBox Freeze(ValueBox value) => ValueBox.Freeze(value);
    public static bool NeedRelease => true;
    public static void ReleaseSlot(ValueBox value) => ValueBox.ReleaseBits64Slot(value);

    public static void Write(IDiffWriter writer, ValueBox v, bool asKey) {
        Debug.Assert(!asKey, "ValueBox不支持作为key使用。");
        v.Write(writer);
    }
    public static ValueBox Read(ref BinaryDiffReader reader, bool asKey) => throw new UnreachableException("Mixed容器应该用ValueBox的Update系列方法。");

    /// <summary>为简化兄弟类型实现，未返回bool。</summary>
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ValueBox old) => TaggedValueDispatcher.UpdateOrInit(ref reader, ref old);
    // public static List<ValueBox> GetTempList(DiffWriteContext context) => throw new UnreachableException();
}
#endregion
#region for key and TypedDict value
internal readonly struct BooleanHelper : ITypeHelper<bool> {
    public static bool Equals(bool a, bool b) => a == b;
    public static void Write(IDiffWriter writer, bool v, bool asKey) => writer.BareBoolean(v, asKey);
    public static bool Read(ref BinaryDiffReader reader, bool asKey) => reader.BareBoolean(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref bool old) => old = Read(ref reader, asKey: false);
    // public static List<bool> GetTempList(DiffWriteContext context) => context.BooleanTempList;
}

internal readonly struct StringHelper : ITypeHelper<string> {
    public static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    public static void Write(IDiffWriter writer, string? v, bool asKey) => writer.BareString(v, asKey);
    public static string? Read(ref BinaryDiffReader reader, bool asKey) => throw new NotImplementedException();
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref string? old) => old = Read(ref reader, asKey: false);
    // public static List<string> GetTempList(DiffWriteContext context) => context.StringTempList;
}

/// <summary>
/// 用于将 <see cref="DurableObject"/> 子类型（嵌套容器）作为 Dict 值类型时的 Helper。
/// 容器使用引用相等性语义（同一对象 = 相等），生命周期由 Epoch 管理。
/// </summary>
internal readonly struct DurableObjectHelper<T> : ITypeHelper<T> where T : DurableObject {
    public static bool Equals(T? a, T? b) => ReferenceEquals(a, b);

    public static void Write(IDiffWriter writer, T? v, bool asKey) {
        Debug.Assert(!asKey, "DurableObject不支持作为key使用。");
        writer.BareDurableObjectRef(v?.LocalId ?? LocalId.Null, asKey);
    }
    public static T? Read(ref BinaryDiffReader reader, bool asKey) => throw new NotImplementedException();
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref T? old) => old = Read(ref reader, asKey: false);
    // public static List<T> GetTempList(DiffWriteContext context) => throw new UnreachableException();
}

internal readonly struct DoubleHelper : ITypeHelper<double> {
    public static bool Equals(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    public static void Write(IDiffWriter writer, double v, bool asKey) => writer.BareDouble(v, asKey);
    public static double Read(ref BinaryDiffReader reader, bool asKey) => reader.BareDouble(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref double old) => old = Read(ref reader, asKey: false);
}

internal readonly struct SingleHelper : ITypeHelper<float> {
    public static bool Equals(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    public static void Write(IDiffWriter writer, float v, bool asKey) => writer.BareSingle(v, asKey);
    public static float Read(ref BinaryDiffReader reader, bool asKey) => reader.BareSingle(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref float old) => old = Read(ref reader, asKey: false);
}

internal readonly struct HalfHelper : ITypeHelper<Half> {
    public static bool Equals(Half a, Half b) =>
        BitConverter.HalfToUInt16Bits(a) == BitConverter.HalfToUInt16Bits(b);
    public static void Write(IDiffWriter writer, Half v, bool asKey) => writer.BareHalf(v, asKey);
    public static Half Read(ref BinaryDiffReader reader, bool asKey) => reader.BareHalf(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref Half old) => old = Read(ref reader, asKey: false);
}

internal readonly struct UInt64Helper : ITypeHelper<ulong> {
    public static bool Equals(ulong a, ulong b) => a == b;
    public static void Write(IDiffWriter writer, ulong v, bool asKey) => writer.BareUInt64(v, asKey);
    public static ulong Read(ref BinaryDiffReader reader, bool asKey) => reader.BareUInt64(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ulong old) => old = Read(ref reader, asKey: false);
}

internal readonly struct UInt32Helper : ITypeHelper<uint> {
    public static bool Equals(uint a, uint b) => a == b;
    public static void Write(IDiffWriter writer, uint v, bool asKey) => writer.BareUInt32(v, asKey);
    public static uint Read(ref BinaryDiffReader reader, bool asKey) => reader.BareUInt32(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref uint old) => old = Read(ref reader, asKey: false);
}

internal readonly struct UInt16Helper : ITypeHelper<ushort> {
    public static bool Equals(ushort a, ushort b) => a == b;
    public static void Write(IDiffWriter writer, ushort v, bool asKey) => writer.BareUInt16(v, asKey);
    public static ushort Read(ref BinaryDiffReader reader, bool asKey) => reader.BareUInt16(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ushort old) => old = Read(ref reader, asKey: false);
}

internal readonly struct Int64Helper : ITypeHelper<long> {
    public static bool Equals(long a, long b) => a == b;
    public static void Write(IDiffWriter writer, long v, bool asKey) => writer.BareInt64(v, asKey);
    public static long Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInt64(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref long old) => old = Read(ref reader, asKey: false);
}

internal readonly struct Int32Helper : ITypeHelper<int> {
    public static bool Equals(int a, int b) => a == b;
    public static void Write(IDiffWriter writer, int v, bool asKey) => writer.BareInt32(v, asKey);
    public static int Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInt32(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref int old) => old = Read(ref reader, asKey: false);
}

internal readonly struct Int16Helper : ITypeHelper<short> {
    public static bool Equals(short a, short b) => a == b;
    public static void Write(IDiffWriter writer, short v, bool asKey) => writer.BareInt16(v, asKey);
    public static short Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInt16(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref short old) => old = Read(ref reader, asKey: false);
}

internal readonly struct SByteHelper : ITypeHelper<sbyte> {
    public static bool Equals(sbyte a, sbyte b) => a == b;
    public static void Write(IDiffWriter writer, sbyte v, bool asKey) => writer.BareSByte(v, asKey);
    public static sbyte Read(ref BinaryDiffReader reader, bool asKey) => reader.BareSByte(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref sbyte old) => old = Read(ref reader, asKey: false);
}

internal readonly struct ByteHelper : ITypeHelper<byte> {
    public static bool Equals(byte a, byte b) => a == b;
    public static void Write(IDiffWriter writer, byte v, bool asKey) => writer.BareByte(v, asKey);
    public static byte Read(ref BinaryDiffReader reader, bool asKey) => reader.BareByte(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref byte old) => old = Read(ref reader, asKey: false);
}

// internal readonly struct LocalIdHelper : ITypeHelper<LocalId> {
//     public static bool Equals(LocalId a, LocalId b) => a == b;
//     public static void Write(IDiffWriter writer, LocalId v, bool asKey) => writer.BareDurableObjectRef(v, asKey);
// }
#endregion
