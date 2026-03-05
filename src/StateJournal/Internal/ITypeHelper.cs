using System.Diagnostics;

namespace Atelia.StateJournal.Internal;

internal interface ITypeHelper<T> where T : notnull {
    static abstract bool Equals(T? a, T? b);
    static abstract void Write(IDiffWriter writer, T? v, bool asKey);

    /// <summary>冻结一个值用于 Commit 时共享。对于无堆资源的类型直接返回原值。</summary>
    static abstract T? Freeze(T? value);

    /// <summary>释放值持有的堆 Slot。调用者确保是最后持有者。对于无堆资源类型无操作。</summary>
    static abstract void ReleaseSlot(T? value);
}

internal readonly struct ByteHelper : ITypeHelper<byte> {
    public static bool Equals(byte a, byte b) => a == b;

    public static void Write(IDiffWriter writer, byte v, bool asKey) => writer.BareByte(v, asKey);

    public static byte Freeze(byte value) => value;
    public static void ReleaseSlot(byte value) { }
}

internal readonly struct Int32Helper : ITypeHelper<int> {
    public static bool Equals(int a, int b) => a == b;
    public static void Write(IDiffWriter writer, int v, bool asKey) => writer.BareInt32(v, asKey);
    public static int Freeze(int value) => value;
    public static void ReleaseSlot(int value) { }
}

internal readonly struct DoubleHelper : ITypeHelper<double> {
    public static bool Equals(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    public static void Write(IDiffWriter writer, double v, bool asKey) => writer.BareDouble(v, asKey);
    public static double Freeze(double value) => value;
    public static void ReleaseSlot(double value) { }
}

internal readonly struct StringHelper : ITypeHelper<string> {
    public static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    public static void Write(IDiffWriter writer, string? v, bool asKey) => writer.BareString(v, asKey);
    public static string? Freeze(string? value) => value; // strings are immutable
    public static void ReleaseSlot(string? value) { }
}

/// <summary>
/// 用于将 <see cref="DurableObject"/> 子类型（嵌套容器）作为 Dict 值类型时的 Helper。
/// 容器使用引用相等性语义（同一对象 = 相等），生命周期由 Epoch 管理。
/// </summary>
internal readonly struct DurableObjectHelper<T> : ITypeHelper<T> where T : DurableObject {
    public static bool Equals(T? a, T? b) => ReferenceEquals(a, b);

    public static void Write(IDiffWriter writer, T? v, bool asKey) {
        Debug.Assert(!asKey, "DurableObject不支持作为key使用。");
        writer.BareLocalId(v?.LocalId, asKey);
    }

    public static T? Freeze(T? value) => value; // 容器是共享引用对象，不需要冻结拷贝
    public static void ReleaseSlot(T? value) { } // 容器生命周期由 Epoch 管理
}

internal readonly struct ValueBoxHelper : ITypeHelper<ValueBox> {
    public static bool Equals(ValueBox a, ValueBox b) => ValueBox.ValueEquals(a, b);

    public static void Write(IDiffWriter writer, ValueBox v, bool asKey) {
        Debug.Assert(!asKey, "ValueBox不支持作为key使用。");
        v.Write(writer);
    }

    public static ValueBox Freeze(ValueBox value) => ValueBox.Freeze(value);
    public static void ReleaseSlot(ValueBox value) => ValueBox.ReleaseBits64Slot(value);
}
