using System.Diagnostics;

namespace Atelia.StateJournal.Internal;

internal interface ITypeHelper<T> where T : notnull {
    static abstract bool Equals(T? a, T? b);
    static abstract void Write(IDiffWriter writer, T? v, bool asKey);

    /// <summary>冻结一个值用于 Commit 时共享。对于无堆资源的类型直接返回原值。
    /// 目前仅有ValueBox由于堆上值的Share/Exclusive语义需要。</summary>
    static virtual T? Freeze(T? value) => value;

    /// <summary>释放值持有的堆 Slot。调用者确保是最后持有者。对于无堆资源类型无操作。
    /// 目前仅有ValueBox由于堆上值的Share/Exclusive语义需要。</summary>
    static virtual void ReleaseSlot(T? value) { }
    // static abstract List<T> GetTempList(DiffWriteContext context);
}

#region for MixedDict value
internal readonly struct ValueBoxHelper : ITypeHelper<ValueBox> {
    public static bool Equals(ValueBox a, ValueBox b) => ValueBox.ValueEquals(a, b);
    public static void Write(IDiffWriter writer, ValueBox v, bool asKey) {
        Debug.Assert(!asKey, "ValueBox不支持作为key使用。");
        v.Write(writer);
    }
    public static ValueBox Freeze(ValueBox value) => ValueBox.Freeze(value);
    public static void ReleaseSlot(ValueBox value) => ValueBox.ReleaseBits64Slot(value);
    // public static List<ValueBox> GetTempList(DiffWriteContext context) => throw new UnreachableException();
}
#endregion
#region for key and TypedDict value
internal readonly struct BooleanHelper : ITypeHelper<bool> {
    public static bool Equals(bool a, bool b) => a == b;
    public static void Write(IDiffWriter writer, bool v, bool asKey) => writer.BareBoolean(v, asKey);
    // public static List<bool> GetTempList(DiffWriteContext context) => context.BooleanTempList;
}

internal readonly struct StringHelper : ITypeHelper<string> {
    public static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    public static void Write(IDiffWriter writer, string? v, bool asKey) => writer.BareString(v, asKey);
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
        writer.BareDurableObjectRef(v?.LocalId, asKey);
    }
    // public static List<T> GetTempList(DiffWriteContext context) => throw new UnreachableException();
}

internal readonly struct DoubleHelper : ITypeHelper<double> {
    public static bool Equals(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    public static void Write(IDiffWriter writer, double v, bool asKey) => writer.BareDouble(v, asKey);
}

internal readonly struct SingleHelper : ITypeHelper<float> {
    public static bool Equals(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    public static void Write(IDiffWriter writer, float v, bool asKey) => writer.BareSingle(v, asKey);
}

internal readonly struct HalfHelper : ITypeHelper<Half> {
    public static bool Equals(Half a, Half b) =>
        BitConverter.HalfToUInt16Bits(a) == BitConverter.HalfToUInt16Bits(b);
    public static void Write(IDiffWriter writer, Half v, bool asKey) => writer.BareHalf(v, asKey);
}

internal readonly struct UInt64Helper : ITypeHelper<ulong> {
    public static bool Equals(ulong a, ulong b) => a == b;
    public static void Write(IDiffWriter writer, ulong v, bool asKey) => writer.BareUInt64(v, asKey);
}

internal readonly struct UInt32Helper : ITypeHelper<uint> {
    public static bool Equals(uint a, uint b) => a == b;
    public static void Write(IDiffWriter writer, uint v, bool asKey) => writer.BareUInt32(v, asKey);
}

internal readonly struct UInt16Helper : ITypeHelper<ushort> {
    public static bool Equals(ushort a, ushort b) => a == b;
    public static void Write(IDiffWriter writer, ushort v, bool asKey) => writer.BareUInt16(v, asKey);
}

internal readonly struct Int64Helper : ITypeHelper<long> {
    public static bool Equals(long a, long b) => a == b;
    public static void Write(IDiffWriter writer, long v, bool asKey) => writer.BareInt64(v, asKey);
}

internal readonly struct Int32Helper : ITypeHelper<int> {
    public static bool Equals(int a, int b) => a == b;
    public static void Write(IDiffWriter writer, int v, bool asKey) => writer.BareInt32(v, asKey);
}

internal readonly struct Int16Helper : ITypeHelper<short> {
    public static bool Equals(short a, short b) => a == b;
    public static void Write(IDiffWriter writer, short v, bool asKey) => writer.BareInt16(v, asKey);
}

internal readonly struct SByteHelper : ITypeHelper<sbyte> {
    public static bool Equals(sbyte a, sbyte b) => a == b;
    public static void Write(IDiffWriter writer, sbyte v, bool asKey) => writer.BareSByte(v, asKey);
}

internal readonly struct ByteHelper : ITypeHelper<byte> {
    public static bool Equals(byte a, byte b) => a == b;
    public static void Write(IDiffWriter writer, byte v, bool asKey) => writer.BareByte(v, asKey);
}

// internal readonly struct LocalIdHelper : ITypeHelper<LocalId> {
//     public static bool Equals(LocalId a, LocalId b) => a == b;
//     public static void Write(IDiffWriter writer, LocalId v, bool asKey) => writer.BareDurableObjectRef(v, asKey);
// }
#endregion
