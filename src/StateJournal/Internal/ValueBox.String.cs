using System.Diagnostics;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

// ai:test `tests/StateJournal.Tests/Internal/ValueBoxStringTests.cs`
partial struct ValueBox {

    /// <summary>HeapSlot + Symbol kind 的组合 tag，用于快速类型判断。</summary>
    internal const uint TagHeapKindSymbol = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)HeapValueKind.Symbol;

    /// <summary>判断 ValueBox 是否为 SymbolId 引用（HeapSlot + HeapValueKind.Symbol）。O(1) 位操作。</summary>
    internal readonly bool IsSymbolRef => (uint)(GetBits() >> HeapKindShift) == TagHeapKindSymbol;

    /// <summary>将 SymbolId 编码为 HeapSlot ValueBox。纯位操作，不访问任何池。</summary>
    internal static ValueBox FromSymbolId(SymbolId id) => id.IsNull
        ? Null
        : EncodeHeapSlot(HeapValueKind.Symbol, id.ToSlotHandle());

    /// <summary>从 HeapSlot ValueBox 解码出 SymbolId。纯位操作，不访问任何池。</summary>
    internal static GetIssue GetSymbolId(ValueBox box, out SymbolId id) {
        Debug.Assert(!box.IsUninitialized);
        if (box.IsNull) {
            id = SymbolId.Null;
            return GetIssue.None;
        }
        if ((uint)(box.GetBits() >> HeapKindShift) == TagHeapKindSymbol) {
            id = new SymbolId(box.GetHeapHandle().Packed);
            return GetIssue.None;
        }
        id = default;
        return GetIssue.TypeMismatch;
    }

    /// <summary>解码 HeapSlot 中的 SymbolId。调用前须确保 LZC == HeapSlot 且 HeapKind == Symbol。</summary>
    internal SymbolId DecodeSymbolId() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        Debug.Assert(GetHeapKind() == HeapValueKind.Symbol);
        return new SymbolId(GetHeapHandle().Packed);
    }

    /// <summary>
    /// load 后校验 mixed 容器中 surviving 的 SymbolId 是否仍能在最终 <see cref="StringPool"/> 中解析。
    /// 注意：此校验发生在容器的 runtime ref-count cache 重建前，因此不能依赖 <c>_symbolRefCount</c> 一类派生缓存。
    /// </summary>
    internal static AteliaError? ValidateReconstructedMixedSymbol(ValueBox box, StringPool symbolPool, string ownerName) {
        if (!box.IsSymbolRef) { return null; }

        SymbolId symbolId = box.DecodeSymbolId();
        if (symbolPool.Validate(symbolId.ToSlotHandle())) { return null; }

        return new SjCorruptionError(
            $"{ownerName} load completed with a dangling SymbolId {symbolId.Value}.",
            RecoveryHint: "The final SymbolTable is missing a string still referenced by the reconstructed object state."
        );
    }

    /// <summary>
    /// SymbolId 的 ITypedFace 实现。纯位操作，不访问任何池。
    /// 容器层负责 SymbolId ↔ Symbol 的转换（通过 Revision.InternSymbol/TryGetSymbol）。
    /// </summary>
    internal readonly struct SymbolIdFace : ITypedFace<SymbolId> {
        public static ValueBox From(SymbolId value) => value.IsNull
            ? Null
            : FromSymbolId(value);

        public static bool UpdateOrInit(ref ValueBox old, SymbolId value, out uint oldBareBytesBeforeMutation) {
            // Symbol 路径下 estimate 与 slot 内容无关（HeapSlot+Symbol kind 走常量），
            // 但接口契约要求在任何 mutation 之前捕获 oldBareBytes。
            oldBareBytesBeforeMutation = old.IsUninitialized ? 0u : old.EstimateBareSize();
            var newBox = value.IsNull ? Null : FromSymbolId(value);
            if (old.GetBits() == newBox.GetBits()) { return false; }
            FreeOldOwnedHeapIfNeeded(old);
            old = newBox;
            return true;
        }

        public static GetIssue Get(ValueBox box, out SymbolId value) {
            return GetSymbolId(box, out value);
        }
    }

    /// <summary>判断 ValueBox 是否为独立 owned payload string（HeapSlot + HeapValueKind.StringPayload）。</summary>
    internal readonly bool IsStringPayloadRef => (uint)(GetBits() >> HeapKindShift) == TagHeapKindStringPayload;

    /// <summary>
    /// payload string 的 ITypedFace 实现。每次 <see cref="From"/> 在 <see cref="ValuePools.OfOwnedString"/>
    /// 分配新 slot（不去重），<see cref="UpdateOrInit"/> 在内容相同时直接复用旧 slot 避免抖动。
    /// </summary>
    internal readonly struct StringPayloadFace : ITypedFace<string> {
        public static ValueBox From(string? value) {
            if (value is null) { return Null; }
            SlotHandle handle = ValuePools.OfOwnedString.Store(value);
            return EncodeHeapSlot(HeapValueKind.StringPayload, handle);
        }

        public static bool UpdateOrInit(ref ValueBox old, string? value, out uint oldBareBytesBeforeMutation) {
            // 关键：必须在任何对 slot / 后备 OwnedStringPool 的修改之前捕获 oldBareBytes。
            // StringPayload estimate 会读取当前 slot 内容，因此必须严格先 snapshot、后 inplace 覆写 / 释放。
            oldBareBytesBeforeMutation = old.IsUninitialized ? 0u : old.EstimateBareSize();
            if (value is null) { return UpdateToNull(ref old); }
            // inplace 更新：旧 box 是 exclusive StringPayload → 比较内容；同则 no-op，否则覆写 slot 内容。
            if (old.GetLzc() == BoxLzc.HeapSlot
                && old.GetTagAndKind() == TagHeapKindStringPayload
                && old.IsExclusive) {
                SlotHandle h = old.GetHeapHandle();
                string current = ValuePools.OfOwnedString[h];
                if (string.Equals(current, value, StringComparison.Ordinal)) { return false; }
                ValuePools.OfOwnedString[h] = value;
                // bits 不变（同 handle、kind、exclusive bit 都不变）；返回 true 表示内容变化。
                return true;
            }
            // 其他情况：释放旧 owned heap slot（如有），分配新 StringPayload slot。
            FreeOldOwnedHeapIfNeeded(old);
            SlotHandle newHandle = ValuePools.OfOwnedString.Store(value);
            old = EncodeHeapSlot(HeapValueKind.StringPayload, newHandle);
            return true;
        }

        public static GetIssue Get(ValueBox box, out string? value) {
            Debug.Assert(!box.IsUninitialized);
            if (box.IsNull) {
                value = null;
                return GetIssue.None;
            }
            if ((uint)(box.GetBits() >> HeapKindShift) == TagHeapKindStringPayload) {
                value = ValuePools.OfOwnedString[box.GetHeapHandle()];
                return GetIssue.None;
            }
            value = null;
            return GetIssue.TypeMismatch;
        }
    }
}
