using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

// ai:test `tests/StateJournal.Tests/Internal/ValueBoxStringTests.cs`
partial struct ValueBox {
    internal const uint TagHeapKindString = (uint)(LzcConstants.HeapSlotTag >> HeapHandleBitCount) | (uint)DurableValueKind.String;

    #region From string
    /// <summary>
    /// 将非 null 字符串编码为 ValueBox。通过 <see cref="ValuePools.Strings"/> InternPool 去重。
    /// </summary>
    /// <remarks>
    /// 同值字符串（Ordinal 相等）共享同一 SlotHandle，
    /// 保证 <see cref="ValueEquals"/> 快速路径命中（bits 相等 → 值相等）。
    /// null 不在此方法处理。如需表示 null，使用 <c>new ValueBox(0)</c>。
    /// </remarks>
    public static ValueBox FromString(string value) => EncodeHeapSlot(DurableValueKind.String, ValuePools.Strings.Store(value));
    #endregion

    #region Get as string
    /// <summary>
    /// 尝试将 ValueBox 读取为字符串。
    /// </summary>
    /// <param name="value">读取到的字符串。仅当返回 <see cref="GetIssue.None"/> 时有效。</param>
    /// <returns>
    /// <see cref="GetIssue.None"/> 成功；
    /// <see cref="GetIssue.TypeMismatch"/> 当 ValueBox 不是字符串类型（包括 Null）。
    /// </returns>
    public GetIssue Get(out string value) {
        // if (GetLZC() == LzcCode.HeapSlot && GetHeapKind() == DurableValueKind.String) {
        if ((uint)(_bits>>HeapHandleBitCount) == TagHeapKindString) {
            value = ValuePools.Strings[GetHeapHandle()];
            return GetIssue.None;
        }
        value = null!;
        return GetIssue.TypeMismatch;
    }
    #endregion

    #region Intern set
    /// <summary>
    /// 独占更新：将 ValueBox 覆写为指定的字符串值。
    /// </summary>
    /// <remarks>
    /// 旧值如果持有 <see cref="ValuePools.Bits64"/> slot（数值类型），会立即释放。
    /// 旧值如果持有 <see cref="ValuePools.Strings"/> slot（字符串类型），
    /// 因 InternPool 共享语义不支持手动 Free，旧 slot 由 Mark-Sweep GC 回收。
    /// </remarks>
    internal static void InternSetString(ref ValueBox box, string value) {
        FreeOldBits64IfNeeded(box);
        box = FromString(value);
    }
    #endregion
}
