namespace Atelia.StateJournal.Internal;

internal interface ITypeHelper<T> where T : notnull {
    static abstract bool Equals(T? a, T? b);
    static abstract void PushKey(IDiffWriter writer, T? v);
    static abstract void PushValue(IDiffWriter writer, T? v);

    /// <summary>冻结一个值用于 Commit 时共享。对于无堆资源的类型直接返回原值。</summary>
    static abstract T? Freeze(T? value);

    /// <summary>释放值持有的堆 Slot。调用者确保是最后持有者。对于无堆资源类型无操作。</summary>
    static abstract void ReleaseSlot(T? value);
}

internal readonly struct ByteHelper : ITypeHelper<byte> {
    public static bool Equals(byte a, byte b) => a == b;

    public static void PushKey(IDiffWriter writer, byte v) => writer.BareByte(v);
    public static void PushValue(IDiffWriter writer, byte v) => writer.BareByte(v);

    public static byte Freeze(byte value) => value;
    public static void ReleaseSlot(byte value) { }
}

internal readonly struct Int32Helper : ITypeHelper<int> {
    public static bool Equals(int a, int b) => a == b;
    public static void PushKey(IDiffWriter writer, int v) => writer.BareInt32(v);
    public static void PushValue(IDiffWriter writer, int v) => writer.BareInt32(v);
    public static int Freeze(int value) => value;
    public static void ReleaseSlot(int value) { }
}

internal readonly struct DoubleHelper : ITypeHelper<double> {
    public static bool Equals(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    public static void PushKey(IDiffWriter writer, double v) => writer.BareDouble(v);
    public static void PushValue(IDiffWriter writer, double v) => writer.BareDouble(v);
    public static double Freeze(double value) => value;
    public static void ReleaseSlot(double value) { }
}

internal readonly struct StringHelper : ITypeHelper<string> {
    public static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    public static void PushKey(IDiffWriter writer, string? v) => writer.BareString(v);
    public static void PushValue(IDiffWriter writer, string? v) => writer.BareString(v);
    public static string? Freeze(string? value) => value; // strings are immutable
    public static void ReleaseSlot(string? value) { }
}

/// <summary>
/// 用于将 <see cref="DurableObject"/> 子类型（嵌套容器）作为 Dict 值类型时的 Helper。
/// 容器使用引用相等性语义（同一对象 = 相等），生命周期由 Epoch 管理。
/// </summary>
internal readonly struct ContainerHelper<T> : ITypeHelper<T> where T : DurableObject {
    public static bool Equals(T? a, T? b) => ReferenceEquals(a, b);

    public static void PushKey(IDiffWriter writer, T? v) =>
        throw new NotSupportedException("Container types cannot be used as dictionary keys.");

    public static void PushValue(IDiffWriter writer, T? v) {
        // AI TODO: 实现容器类型的序列化。使用 DurableObject.GlobalId 作为引用标识写入。
        throw new NotImplementedException();
    }

    public static T? Freeze(T? value) => value; // 容器是共享引用对象，不需要冻结拷贝
    public static void ReleaseSlot(T? value) { } // 容器生命周期由 Epoch 管理
}

internal readonly struct ValueBoxHelper : ITypeHelper<ValueBox> {
    public static bool Equals(ValueBox a, ValueBox b) => ValueBox.ValueEquals(a, b);

    /// <summary>预期永远不会被调用到。因为ValueBox不支持作为key使用。</summary>
    /// <exception cref="NotSupportedException">无条件直接抛出此异常。</exception>
    public static void PushKey(IDiffWriter writer, ValueBox v) {
        throw new NotSupportedException(); // ValueBox不支持作为key使用。
    }

    public static void PushValue(IDiffWriter writer, ValueBox v) {
        // ai:note 暂时我们还没开始让ValueBox支持存DurableObject实例的工作，后续会类似对字符串的支持，用DurableObject.GlobalId作为相等性语义，俩对象ID相等就是相等，是一种引用相等性语义。
        // AI TODO: 根据ValueBox.GetLZC()与ValueBox.GetHeapKind()的返回值模式，选择IDiffWriter上对应的PushTyped*方法，将参数v内存的值用writer按自描述方式写入下层。
        throw new NotImplementedException();
    }

    public static ValueBox Freeze(ValueBox value) => ValueBox.Freeze(value);
    public static void ReleaseSlot(ValueBox value) => ValueBox.ReleaseBits64Slot(value);
}
