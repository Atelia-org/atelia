using System.Diagnostics;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

/// <remarks>
/// <para><b>COW 生命周期契约</b>（仅对 <see cref="NeedRelease"/> == true 的类型有意义）：</para>
/// <list type="number">
///   <item>新值以 <b>Exclusive</b>（独占/可变）状态进入 <c>_current</c>。</item>
///   <item><see cref="Freeze"/> 将值转为 <b>Frozen</b>（共享/不可变），使 committed 与 current 可安全引用同一份值。</item>
///   <item>当某个 frozen 值不再被任何方引用时，最后持有者调用 <see cref="ReleaseSlot"/> 归还堆资源。</item>
/// </list>
/// <para>对无堆资源的值类型（<see cref="NeedRelease"/> == false），上述三步均退化为 no-op / identity。</para>
/// </remarks>
internal interface ITypeHelper<T> where T : notnull {
    static abstract bool Equals(T? a, T? b);

    /// <summary>
    /// 将值从 Exclusive 状态转为 Frozen 状态，用于 Commit 时让 committed 与 current 共享同一份值。
    /// </summary>
    /// <remarks>
    /// <para><b>幂等性契约</b>：对已 Frozen 的值，必须返回同一实例（<c>Freeze(Freeze(x)) ≡ Freeze(x)</c>）。
    /// ChangeTracker 的 Commit 路径依赖此性质——keep 窗口中的值已经是 frozen 的，再次 Freeze 不得产生新引用。</para>
    /// <para>对无堆资源的类型直接返回原值。目前仅 <see cref="ValueBoxHelper"/> (ValueBox) 会清除 ExclusiveBit。</para>
    /// </remarks>
    static virtual T? Freeze(T? value) => value;

    /// <summary>是否持有需要手动管理的堆资源。为 true 时，调用方需遵守 Freeze/ReleaseSlot 的生命周期契约。</summary>
    static virtual bool NeedRelease => false;

    /// <summary>
    /// 无条件释放值持有的堆资源，不论 Exclusive/Frozen 状态。
    /// </summary>
    /// <remarks>
    /// <para>调用者必须确保自己是该值的最后持有者（即 committed 与 current 均不再引用此值）。</para>
    /// <para>典型调用时机：Commit 时释放已被 trim/remove 的旧 committed 值；Revert 时释放 current 中的新增值。</para>
    /// <para>对无堆资源的类型为空操作。目前仅 <see cref="ValueBoxHelper"/> (ValueBox) 会归还池化 Slot。</para>
    /// </remarks>
    static virtual void ReleaseSlot(T? value) { }
    // static abstract List<T> GetTempList(DiffWriteContext context);

    static abstract void Write(BinaryDiffWriter writer, T? v, bool asKey);
    static abstract T? Read(ref BinaryDiffReader reader, bool asKey);

    /// <summary>当前 facade 类型是否需要在提交期参与 child-ref walk（例如 typed string facade）。</summary>
    static virtual bool NeedVisitChildRefs => false;

    /// <summary>将单个 facade 值暴露给 child-ref visitor。默认 no-op。</summary>
    static virtual void VisitChildRefs<TVisitor>(T? value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct { }

    /// <summary>
    /// 加载历史版本链后，是否需要对最终 surviving 的 facade 值做 placeholder 残留校验。
    /// 目前主要用于 typed string 在 load 阶段的缺失 SymbolId 占位方案。
    /// </summary>
    static virtual bool NeedValidateReconstructed => false;

    /// <summary>校验单个 facade 值是否残留非法的 load placeholder。</summary>
    static virtual AteliaError? ValidateReconstructed(T? value, LoadPlaceholderTracker tracker, string ownerName) => null;

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

    public static void Write(BinaryDiffWriter writer, ValueBox v, bool asKey) {
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
    public static void Write(BinaryDiffWriter writer, bool v, bool asKey) => writer.BareBoolean(v, asKey);
    public static bool Read(ref BinaryDiffReader reader, bool asKey) => reader.BareBoolean(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref bool old) => old = Read(ref reader, asKey: false);
    // public static List<bool> GetTempList(DiffWriteContext context) => context.BooleanTempList;
}

/// <summary>
/// string 的 symbol-backed 序列化 Helper。
/// 运行时 facade/store 都是普通 string；写入/读取时通过 writer/reader 上下文桥接 SymbolId。
/// </summary>
internal readonly struct StringHelper : ITypeHelper<string> {
    public static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    public static void Write(BinaryDiffWriter writer, string? v, bool asKey) => writer.BareSymbol(v, asKey);
    public static string? Read(ref BinaryDiffReader reader, bool asKey) => reader.BareSymbolId(asKey);
    public static bool NeedVisitChildRefs => true;
    public static bool NeedValidateReconstructed => true;
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref string? old) => old = Read(ref reader, asKey: false);

    public static void VisitChildRefs<TVisitor>(string? value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        visitor.Visit(value);
    }

    public static AteliaError? ValidateReconstructed(string? value, LoadPlaceholderTracker tracker, string ownerName) {
        if (!tracker.IsPlaceholder(value)) { return null; }
        return new SjCorruptionError(
            $"{ownerName} load completed with an unresolved historical string placeholder: '{value}'.",
            RecoveryHint: "The final SymbolTable is missing a string still referenced by the reconstructed object state."
        );
    }
}

/// <summary>
/// 值语义字符串（<see cref="InlineString"/>）的序列化 Helper。
/// 用于 Per-Revision String Pool（<c>DurableDict&lt;uint, InlineString&gt;</c>）的 value 侧。
/// </summary>
internal readonly struct InlineStringHelper : ITypeHelper<InlineString> {
    public static bool Equals(InlineString a, InlineString b) => a == b;
    public static void Write(BinaryDiffWriter writer, InlineString v, bool asKey) => writer.BareInlineString(v.Value, asKey);
    public static InlineString Read(ref BinaryDiffReader reader, bool asKey) => new(reader.BareInlineString(asKey));
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref InlineString old) => old = Read(ref reader, asKey: false);
}

/// <summary>
/// <see cref="LocalId"/> 的序列化 Helper。
/// 用于 <see cref="DurObjDictImpl{TKey, TDurObj, KHelper}"/> 内部以 LocalId 存储 DurableObject 引用。
/// </summary>
internal readonly struct LocalIdAsRefHelper : ITypeHelper<LocalId> {
    public static bool Equals(LocalId a, LocalId b) => a == b;
    public static void Write(BinaryDiffWriter writer, LocalId v, bool asKey) => writer.BareDurableRef(v, asKey);
    public static LocalId Read(ref BinaryDiffReader reader, bool asKey) => new(reader.BareUInt32(asKey));
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref LocalId old) => old = Read(ref reader, asKey: false);
}

internal readonly struct DoubleHelper : ITypeHelper<double> {
    public static bool Equals(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    public static void Write(BinaryDiffWriter writer, double v, bool asKey) => writer.BareDouble(v, asKey);
    public static double Read(ref BinaryDiffReader reader, bool asKey) => reader.BareDouble(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref double old) => old = Read(ref reader, asKey: false);
}

internal readonly struct SingleHelper : ITypeHelper<float> {
    public static bool Equals(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    public static void Write(BinaryDiffWriter writer, float v, bool asKey) => writer.BareSingle(v, asKey);
    public static float Read(ref BinaryDiffReader reader, bool asKey) => reader.BareSingle(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref float old) => old = Read(ref reader, asKey: false);
}

internal readonly struct HalfHelper : ITypeHelper<Half> {
    public static bool Equals(Half a, Half b) =>
        BitConverter.HalfToUInt16Bits(a) == BitConverter.HalfToUInt16Bits(b);
    public static void Write(BinaryDiffWriter writer, Half v, bool asKey) => writer.BareHalf(v, asKey);
    public static Half Read(ref BinaryDiffReader reader, bool asKey) => reader.BareHalf(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref Half old) => old = Read(ref reader, asKey: false);
}

internal readonly struct UInt64Helper : ITypeHelper<ulong> {
    public static bool Equals(ulong a, ulong b) => a == b;
    public static void Write(BinaryDiffWriter writer, ulong v, bool asKey) => writer.BareUInt64(v, asKey);
    public static ulong Read(ref BinaryDiffReader reader, bool asKey) => reader.BareUInt64(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ulong old) => old = Read(ref reader, asKey: false);
}

internal readonly struct UInt32Helper : ITypeHelper<uint> {
    public static bool Equals(uint a, uint b) => a == b;
    public static void Write(BinaryDiffWriter writer, uint v, bool asKey) => writer.BareUInt32(v, asKey);
    public static uint Read(ref BinaryDiffReader reader, bool asKey) => reader.BareUInt32(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref uint old) => old = Read(ref reader, asKey: false);
}

internal readonly struct UInt16Helper : ITypeHelper<ushort> {
    public static bool Equals(ushort a, ushort b) => a == b;
    public static void Write(BinaryDiffWriter writer, ushort v, bool asKey) => writer.BareUInt16(v, asKey);
    public static ushort Read(ref BinaryDiffReader reader, bool asKey) => reader.BareUInt16(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ushort old) => old = Read(ref reader, asKey: false);
}

internal readonly struct Int64Helper : ITypeHelper<long> {
    public static bool Equals(long a, long b) => a == b;
    public static void Write(BinaryDiffWriter writer, long v, bool asKey) => writer.BareInt64(v, asKey);
    public static long Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInt64(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref long old) => old = Read(ref reader, asKey: false);
}

internal readonly struct Int32Helper : ITypeHelper<int> {
    public static bool Equals(int a, int b) => a == b;
    public static void Write(BinaryDiffWriter writer, int v, bool asKey) => writer.BareInt32(v, asKey);
    public static int Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInt32(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref int old) => old = Read(ref reader, asKey: false);
}

internal readonly struct Int16Helper : ITypeHelper<short> {
    public static bool Equals(short a, short b) => a == b;
    public static void Write(BinaryDiffWriter writer, short v, bool asKey) => writer.BareInt16(v, asKey);
    public static short Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInt16(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref short old) => old = Read(ref reader, asKey: false);
}

internal readonly struct SByteHelper : ITypeHelper<sbyte> {
    public static bool Equals(sbyte a, sbyte b) => a == b;
    public static void Write(BinaryDiffWriter writer, sbyte v, bool asKey) => writer.BareSByte(v, asKey);
    public static sbyte Read(ref BinaryDiffReader reader, bool asKey) => reader.BareSByte(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref sbyte old) => old = Read(ref reader, asKey: false);
}

internal readonly struct ByteHelper : ITypeHelper<byte> {
    public static bool Equals(byte a, byte b) => a == b;
    public static void Write(BinaryDiffWriter writer, byte v, bool asKey) => writer.BareByte(v, asKey);
    public static byte Read(ref BinaryDiffReader reader, bool asKey) => reader.BareByte(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref byte old) => old = Read(ref reader, asKey: false);
}

// internal readonly struct LocalIdHelper : ITypeHelper<LocalId> {
//     public static bool Equals(LocalId a, LocalId b) => a == b;
//     public static void Write(IDiffWriter writer, LocalId v, bool asKey) => writer.BareDurableObjectRef(v, asKey);
// }
#endregion
