using System.Diagnostics;
using System.Text;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

/// <summary>cost-model 估算共享的小工具：手算 VarUInt 编码长度，避免在 hot path 上调用更重的辅助方法。</summary>
internal static class CostEstimateUtil {
    public static uint VarIntSize(uint v) =>
        v < 128u ? 1u :
        v < 16384u ? 2u :
        v < 2097152u ? 3u :
        v < 268435456u ? 4u : 5u;

    public static uint VarIntSize(ulong v) =>
        v < 128ul ? 1u :
        v < 16384ul ? 2u :
        v < 2097152ul ? 3u :
        v < 268435456ul ? 4u :
        v < 34359738368ul ? 5u :
        v < 4398046511104ul ? 6u :
        v < 562949953421312ul ? 7u :
        v < 72057594037927936ul ? 8u :
        v < 9223372036854775808ul ? 9u : 10u;

    public static uint ZigZagInt32Size(int v) => VarIntSize(VarInt.ZigZagEncode32(v));
    public static uint ZigZagInt64Size(long v) => VarIntSize(VarInt.ZigZagEncode64(v));
    public static uint ZigZagInt16Size(short v) => VarIntSize(VarInt.ZigZagEncode16(v));

    /// <summary>
    /// 估算 <see cref="BinaryDiffWriter.WriteBytes(System.ReadOnlySpan{byte})"/> 的真实写出字节数：
    /// VarUInt 长度前缀 + payload 内容。空 span（包括 deltify frame 写出的空 TypeCode）按零长度处理，
    /// 即 <c>VarUInt(0) = 1B</c>。
    /// </summary>
    public static uint WriteBytesSize(System.ReadOnlySpan<byte> bytes) {
        uint length = (uint)bytes.Length;
        return VarIntSize(length) + length;
    }
}

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
    /// 持久化稳定的比较。用于 <see cref="SkipListCore{TKey,TValue,KHelper,VHelper}"/> 等有序容器。
    /// 默认委托 <see cref="Comparer{T}.Default"/>（对数值类型已经跨平台稳定）。
    /// 对 string / Symbol 等 culture-sensitive 类型，Helper 必须覆盖为 Ordinal 语义。
    /// </summary>
    static virtual int Compare(T? a, T? b) => Comparer<T>.Default.Compare(a!, b!);

    /// <summary>
    /// 将值从 Exclusive 状态转为 Frozen 状态，用于 Commit 时让 committed 与 current 共享同一份值。
    /// </summary>
    /// <remarks>
    /// <para><b>幂等性契约</b>：对已 Frozen 的值，必须返回同一实例（<c>Freeze(Freeze(x)) ≡ Freeze(x)</c>）。
    /// ChangeTracker 的 Commit 路径依赖此性质——keep 窗口中的值已经是 frozen 的，再次 Freeze 不得产生新引用。</para>
    /// <para>对无堆资源的类型直接返回原值。目前仅 <see cref="ValueBoxHelper"/> (ValueBox) 会清除 ExclusiveBit。</para>
    /// </remarks>
    static virtual T? Freeze(T? value) => value;

    /// <summary>
    /// 为对象级 fork 复制 committed/frozen 值到新的 owner。
    /// 对无独占堆资源的类型是 identity；持有手动生命周期资源的 helper 必须返回独立所有权。
    /// </summary>
    static virtual T? ForkFrozenForNewOwner(T? value) => value;

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

    /// <summary>当前 facade 类型是否需要在提交期参与 child-ref walk（例如 typed symbol facade）。</summary>
    static virtual bool NeedVisitChildRefs => false;

    /// <summary>将单个 facade 值暴露给 child-ref visitor。默认 no-op。</summary>
    static virtual void VisitChildRefs<TVisitor>(T? value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct { }

    /// <summary>
    /// 加载历史版本链后，是否需要对最终 surviving 的 facade 值做 placeholder 残留校验。
    /// 目前主要用于 typed Symbol 在 load 阶段的缺失 SymbolId 占位方案。
    /// </summary>
    static virtual bool NeedValidateReconstructed => false;

    /// <summary>校验单个 facade 值是否残留非法的 load placeholder。</summary>
    static virtual AteliaError? ValidateReconstructed(T? value, LoadPlaceholderTracker tracker, string ownerName) => null;

    /// <summary>未来如果需要获知是否改变了old（update or init），可以将返回值改为bool并添加`bool exists参数`，参考实现：
    /// `if (exists && Equals(old, readedNewValue)) { return false; }`</summary>
    static abstract void UpdateOrInit(ref BinaryDiffReader reader, ref T? old);

    /// <summary>
    /// 估算 <see cref="Write"/>(value, asKey) 写入的 bare 字节数，用于 cost-model 决策（rebase vs deltify）。
    /// 不要求精确，只要求与真实值同数量级；偏低会让 rebase 太频繁，偏高会让 deltify 链过长。
    /// 默认 8 字节为保守 fallback。
    /// </summary>
    static virtual uint EstimateBareSize(T? value, bool asKey) => 8;
}

#region for MixedDict value
internal readonly struct ValueBoxHelper : ITypeHelper<ValueBox> {
    public static bool Equals(ValueBox a, ValueBox b) => ValueBox.ValueEquals(a, b);
    public static ValueBox Freeze(ValueBox value) => ValueBox.Freeze(value);
    public static ValueBox ForkFrozenForNewOwner(ValueBox value) => ValueBox.CloneFrozenForNewOwner(value);
    public static bool NeedRelease => true;
    public static void ReleaseSlot(ValueBox value) => ValueBox.ReleaseOwnedHeapSlot(value);

    public static void Write(BinaryDiffWriter writer, ValueBox v, bool asKey) {
        Debug.Assert(!asKey, "ValueBox不支持作为key使用。");
        v.Write(writer);
    }
    public static ValueBox Read(ref BinaryDiffReader reader, bool asKey) => throw new UnreachableException("Mixed容器应该用ValueBox的Update系列方法。");

    /// <summary>为简化兄弟类型实现，未返回bool。</summary>
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ValueBox old) => TaggedValueDispatcher.UpdateOrInit(ref reader, ref old);

    public static uint EstimateBareSize(ValueBox value, bool asKey) => value.EstimateBareSize();
    // public static List<ValueBox> GetTempList(DiffWriteContext context) => throw new UnreachableException();
}
#endregion
#region for key and TypedDict value
internal readonly struct BooleanHelper : ITypeHelper<bool> {
    public static bool Equals(bool a, bool b) => a == b;
    public static int Compare(bool a, bool b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, bool v, bool asKey) => writer.BareBoolean(v, asKey);
    public static bool Read(ref BinaryDiffReader reader, bool asKey) => reader.BareBoolean(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref bool old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(bool value, bool asKey) => 1;
    // public static List<bool> GetTempList(DiffWriteContext context) => context.BooleanTempList;
}

/// <summary>
/// 显式 Symbol facade 的序列化 Helper。
/// 运行时 facade 是 <see cref="Symbol"/>；写入/读取时通过 writer/reader 上下文桥接 SymbolId。
/// </summary>
internal readonly struct SymbolHelper : ITypeHelper<Symbol> {
    public static bool Equals(Symbol a, Symbol b) => a == b;
    public static int Compare(Symbol a, Symbol b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, Symbol v, bool asKey) => writer.BareSymbol(v, asKey);
    public static Symbol Read(ref BinaryDiffReader reader, bool asKey) => reader.BareSymbol(asKey);
    public static bool NeedVisitChildRefs => true;
    public static bool NeedValidateReconstructed => true;
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref Symbol old) => old = Read(ref reader, asKey: false);

    public static void VisitChildRefs<TVisitor>(Symbol value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        visitor.Visit(value);
    }

    public static AteliaError? ValidateReconstructed(Symbol value, LoadPlaceholderTracker tracker, string ownerName) {
        if (!tracker.IsPlaceholder(value.Value)) { return null; }
        return new SjCorruptionError(
            $"{ownerName} load completed with an unresolved historical symbol placeholder: '{value}'.",
            RecoveryHint: "The final SymbolTable is missing a string still referenced by the reconstructed object state."
        );
    }

    /// <summary>BareSymbol 写出 VarUInt(SymbolId)。这里拿不到实际 SymbolId，先保守取 5 作为上界。</summary>
    public static uint EstimateBareSize(Symbol value, bool asKey) => 5;
}

/// <summary>
/// 值语义 string 的序列化 Helper。
/// 直接把字符串 payload 写入帧体，不经过 per-Revision symbol table。
/// </summary>
internal readonly struct StringHelper : ITypeHelper<string> {
    public static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    public static int Compare(string? a, string? b) => string.Compare(a, b, StringComparison.Ordinal);
    public static void Write(BinaryDiffWriter writer, string? v, bool asKey) => writer.BareStringPayload(v, asKey);
    public static string Read(ref BinaryDiffReader reader, bool asKey) => reader.BareStringPayload(asKey);
    public static bool NeedVisitChildRefs => false;
    public static bool NeedValidateReconstructed => false;
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref string? old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(string? value, bool asKey) {
        if (string.IsNullOrEmpty(value)) { return 1; }
        int utf8Bytes = Encoding.UTF8.GetByteCount(value);
        int utf16Bytes = value.Length * 2;
        bool useUtf8 = utf8Bytes < utf16Bytes;
        int payloadBytes = useUtf8 ? utf8Bytes : utf16Bytes;
        uint header = useUtf8 ? ((uint)utf8Bytes << 1) | 1u : (uint)utf16Bytes;
        return CostEstimateUtil.VarIntSize(header) + (uint)payloadBytes;
    }

    public static void VisitChildRefs<TVisitor>(string? value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
    }

    public static AteliaError? ValidateReconstructed(string? value, LoadPlaceholderTracker tracker, string ownerName) => null;
}

/// <summary>
/// <see cref="LocalId"/> 的序列化 Helper。
/// 用于 <see cref="DurObjDictImpl{TKey, TDurObj, KHelper}"/> 内部以 LocalId 存储 DurableObject 引用。
/// </summary>
internal readonly struct LocalIdAsRefHelper : ITypeHelper<LocalId> {
    public static bool Equals(LocalId a, LocalId b) => a == b;
    public static int Compare(LocalId a, LocalId b) => a.Value.CompareTo(b.Value);
    public static void Write(BinaryDiffWriter writer, LocalId v, bool asKey) => writer.BareDurableRef(v, asKey);
    public static LocalId Read(ref BinaryDiffReader reader, bool asKey) => new(reader.BareUInt32(asKey));
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref LocalId old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(LocalId value, bool asKey) => CostEstimateUtil.VarIntSize(value.Value);

    public static bool NeedVisitChildRefs => true;
    public static void VisitChildRefs<TVisitor>(LocalId value, Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        if (!value.IsNull) { visitor.Visit(value); }
    }
}

internal readonly struct DoubleHelper : ITypeHelper<double> {
    public static bool Equals(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    public static int Compare(double a, double b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, double v, bool asKey) => writer.BareDouble(v, asKey);
    public static double Read(ref BinaryDiffReader reader, bool asKey) => reader.BareDouble(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref double old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(double value, bool asKey) => 8;
}

internal readonly struct SingleHelper : ITypeHelper<float> {
    public static bool Equals(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    public static int Compare(float a, float b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, float v, bool asKey) => writer.BareSingle(v, asKey);
    public static float Read(ref BinaryDiffReader reader, bool asKey) => reader.BareSingle(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref float old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(float value, bool asKey) => 4;
}

internal readonly struct HalfHelper : ITypeHelper<Half> {
    public static bool Equals(Half a, Half b) =>
        BitConverter.HalfToUInt16Bits(a) == BitConverter.HalfToUInt16Bits(b);
    public static int Compare(Half a, Half b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, Half v, bool asKey) => writer.BareHalf(v, asKey);
    public static Half Read(ref BinaryDiffReader reader, bool asKey) => reader.BareHalf(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref Half old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(Half value, bool asKey) => 2;
}

internal readonly struct UInt64Helper : ITypeHelper<ulong> {
    public static bool Equals(ulong a, ulong b) => a == b;
    public static int Compare(ulong a, ulong b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, ulong v, bool asKey) => writer.BareUInt64(v, asKey);
    public static ulong Read(ref BinaryDiffReader reader, bool asKey) => reader.BareUInt64(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ulong old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(ulong value, bool asKey) => CostEstimateUtil.VarIntSize(value);
}

internal readonly struct UInt32Helper : ITypeHelper<uint> {
    public static bool Equals(uint a, uint b) => a == b;
    public static int Compare(uint a, uint b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, uint v, bool asKey) => writer.BareUInt32(v, asKey);
    public static uint Read(ref BinaryDiffReader reader, bool asKey) => reader.BareUInt32(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref uint old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(uint value, bool asKey) => CostEstimateUtil.VarIntSize(value);
}

internal readonly struct UInt16Helper : ITypeHelper<ushort> {
    public static bool Equals(ushort a, ushort b) => a == b;
    public static int Compare(ushort a, ushort b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, ushort v, bool asKey) => writer.BareUInt16(v, asKey);
    public static ushort Read(ref BinaryDiffReader reader, bool asKey) => reader.BareUInt16(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref ushort old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(ushort value, bool asKey) => CostEstimateUtil.VarIntSize((uint)value);
}

internal readonly struct Int64Helper : ITypeHelper<long> {
    public static bool Equals(long a, long b) => a == b;
    public static int Compare(long a, long b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, long v, bool asKey) => writer.BareInt64(v, asKey);
    public static long Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInt64(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref long old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(long value, bool asKey) => CostEstimateUtil.ZigZagInt64Size(value);
}

internal readonly struct Int32Helper : ITypeHelper<int> {
    public static bool Equals(int a, int b) => a == b;
    public static int Compare(int a, int b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, int v, bool asKey) => writer.BareInt32(v, asKey);
    public static int Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInt32(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref int old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(int value, bool asKey) => CostEstimateUtil.ZigZagInt32Size(value);
}

internal readonly struct Int16Helper : ITypeHelper<short> {
    public static bool Equals(short a, short b) => a == b;
    public static int Compare(short a, short b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, short v, bool asKey) => writer.BareInt16(v, asKey);
    public static short Read(ref BinaryDiffReader reader, bool asKey) => reader.BareInt16(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref short old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(short value, bool asKey) => CostEstimateUtil.ZigZagInt16Size(value);
}

internal readonly struct SByteHelper : ITypeHelper<sbyte> {
    public static bool Equals(sbyte a, sbyte b) => a == b;
    public static int Compare(sbyte a, sbyte b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, sbyte v, bool asKey) => writer.BareSByte(v, asKey);
    public static sbyte Read(ref BinaryDiffReader reader, bool asKey) => reader.BareSByte(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref sbyte old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(sbyte value, bool asKey) => 1;
}

internal readonly struct ByteHelper : ITypeHelper<byte> {
    public static bool Equals(byte a, byte b) => a == b;
    public static int Compare(byte a, byte b) => a.CompareTo(b);
    public static void Write(BinaryDiffWriter writer, byte v, bool asKey) => writer.BareByte(v, asKey);
    public static byte Read(ref BinaryDiffReader reader, bool asKey) => reader.BareByte(asKey);
    public static void UpdateOrInit(ref BinaryDiffReader reader, ref byte old) => old = Read(ref reader, asKey: false);
    public static uint EstimateBareSize(byte value, bool asKey) => 1;
}

// internal readonly struct LocalIdHelper : ITypeHelper<LocalId> {
//     public static bool Equals(LocalId a, LocalId b) => a == b;
//     public static void Write(IDiffWriter writer, LocalId v, bool asKey) => writer.BareDurableObjectRef(v, asKey);
// }
#endregion
