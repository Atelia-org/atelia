using System.Diagnostics;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

// ai:test `tests/StateJournal.Tests/Internal/ValueBoxStringTests.cs`
partial struct ValueBox {

    /// <summary>HeapSlot + String kind 的组合 tag，用于快速类型判断。</summary>
    internal const uint TagHeapKindString = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)ValueKind.String;

    /// <summary>判断 ValueBox 是否为 SymbolId 引用（HeapSlot + HeapValueKind.String）。O(1) 位操作。</summary>
    internal readonly bool IsSymbolRef => (uint)(GetBits() >> HeapKindShift) == TagHeapKindString;

    /// <summary>将 SymbolId 编码为 HeapSlot ValueBox。纯位操作，不访问任何池。</summary>
    internal static ValueBox FromSymbolId(SymbolId id) => id.IsNull
        ? Null
        : EncodeHeapSlot(HeapValueKind.String, id.ToSlotHandle());

    /// <summary>从 HeapSlot ValueBox 解码出 SymbolId。纯位操作，不访问任何池。</summary>
    internal static GetIssue GetSymbolId(ValueBox box, out SymbolId id) {
        Debug.Assert(!box.IsUninitialized);
        if (box.IsNull) {
            id = SymbolId.Null;
            return GetIssue.None;
        }
        if ((uint)(box.GetBits() >> HeapKindShift) == TagHeapKindString) {
            id = new SymbolId(box.GetHeapHandle().Packed);
            return GetIssue.None;
        }
        id = default;
        return GetIssue.TypeMismatch;
    }

    /// <summary>解码 HeapSlot 中的 SymbolId。调用前须确保 LZC == HeapSlot 且 HeapKind == String。</summary>
    internal SymbolId DecodeSymbolId() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        Debug.Assert(GetHeapKind() == HeapValueKind.String);
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
    /// 容器层负责 SymbolId ↔ string 的转换（通过 Revision.InternSymbol/GetSymbol）。
    /// </summary>
    internal readonly struct SymbolIdFace : ITypedFace<SymbolId> {
        public static ValueBox From(SymbolId value) => value.IsNull
            ? Null
            : FromSymbolId(value);

        public static bool UpdateOrInit(ref ValueBox old, SymbolId value) {
            var newBox = value.IsNull ? Null : FromSymbolId(value);
            if (old.GetBits() == newBox.GetBits()) { return false; }
            FreeOldBits64IfNeeded(old);
            old = newBox;
            return true;
        }

        public static GetIssue Get(ValueBox box, out SymbolId value) {
            return GetSymbolId(box, out value);
        }
    }
}
