using System.Diagnostics;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

// ai:test `tests/StateJournal.Tests/Internal/ValueBoxBlobPayloadTests.cs`
partial struct ValueBox {

    /// <summary>判断 ValueBox 是否为独立 owned payload blob（HeapSlot + HeapValueKind.BlobPayload）。</summary>
    internal readonly bool IsBlobPayloadRef => (uint)(GetBits() >> HeapKindShift) == TagHeapKindBlobPayload;

    /// <summary>
    /// payload blob (<see cref="ByteString"/>) 的 ITypedFace 实现。每次 <see cref="From"/> 在
    /// <see cref="ValuePools.OfOwnedBlob"/> 分配新 slot（不去重），<see cref="UpdateOrInit"/> 在内容相同时直接复用旧 slot
    /// 避免抖动。完全镜像 <see cref="StringPayloadFace"/> 的形态。
    /// </summary>
    /// <remarks>
    /// <para>命名说明：业务 API 是 <see cref="ByteString"/>；内部链路（本 face / pool / wire / view）统称 "Blob"。
    /// 详见 <c>MixedValueCatalog.cs</c> 顶部的术语对照表。</para>
    /// <para><see cref="ByteString"/> 是值类型且 <c>default</c> 等价 <see cref="ByteString.Empty"/>，没有 null 概念，
    /// 因此不存在 "From(null) → ValueBox.Null" 路径。空 blob 同样在 pool 中分配 slot（与 <see cref="StringPayloadFace"/>
    /// 处理空 string 一致）。Wire 上的"无值"由独立的 <see cref="Serialization.ScalarRules.Null"/> tag 表达。</para>
    /// <para>byte[] 入池时由 face 一次性 defensive clone（CMS Step 3b GPT5 review 决策 B：pool 内 byte[]
    /// 真 owned，外部即便 mutate 已交给 face 的源 <see cref="ByteString"/> 底层数组也不会污染 dict 内容）。
    /// 未来若 benchmark 显示大 blob 入池 clone 成为热点，再补 <c>ByteString.FromTrustedOwned(byte[])</c> +
    /// 对应零拷贝入池路径作为高级 API。<see cref="Get"/> 返回的 <see cref="ByteString"/> 包装 pool 内副本，
    /// 但因 <see cref="ByteString"/> 公开 API 仅暴露 <see cref="ReadOnlySpan{T}"/>，外部无法 mutate。</para>
    /// </remarks>
    internal readonly struct BlobPayloadFace : ITypedFace<ByteString> {
        public static ValueBox From(ByteString value) {
            byte[] owned = CloneForPool(value);
            SlotHandle handle = ValuePools.OfOwnedBlob.Store(owned);
            return EncodeHeapSlot(HeapValueKind.BlobPayload, handle);
        }

        public static bool UpdateOrInit(ref ValueBox old, ByteString value, out uint oldBareBytesBeforeMutation) {
            // 关键：必须在任何对 slot / 后备 OwnedBlobPool 的修改之前捕获 oldBareBytes（CMS Step 1 契约）。
            oldBareBytesBeforeMutation = old.IsUninitialized ? 0u : old.EstimateBareSize();
            // inplace 更新：旧 box 是 exclusive BlobPayload → 比较内容；同则 no-op，否则覆写 slot 内容。
            if (old.GetLzc() == BoxLzc.HeapSlot
                && old.GetTagAndKind() == TagHeapKindBlobPayload
                && old.IsExclusive) {
                SlotHandle h = old.GetHeapHandle();
                byte[] current = ValuePools.OfOwnedBlob[h];
                if (current.AsSpan().SequenceEqual(value.AsSpan())) { return false; }
                ValuePools.OfOwnedBlob[h] = CloneForPool(value);
                // bits 不变（同 handle、kind、exclusive bit 都不变）；返回 true 表示内容变化。
                return true;
            }
            // 其他情况：释放旧 owned heap slot（如有），分配新 BlobPayload slot。
            FreeOldOwnedHeapIfNeeded(old);
            SlotHandle newHandle = ValuePools.OfOwnedBlob.Store(CloneForPool(value));
            old = EncodeHeapSlot(HeapValueKind.BlobPayload, newHandle);
            return true;
        }

        /// <summary>
        /// 把 <see cref="ByteString"/> 的内容复制成 face/pool 独占的 <see cref="byte"/>[]。
        /// 空 blob 直接复用 <see cref="Array.Empty{T}"/> singleton，零分配。
        /// </summary>
        private static byte[] CloneForPool(ByteString value) {
            ReadOnlySpan<byte> src = value.AsSpan();
            if (src.IsEmpty) { return Array.Empty<byte>(); }
            byte[] copy = new byte[src.Length];
            src.CopyTo(copy);
            return copy;
        }

        public static GetIssue Get(ValueBox box, out ByteString value) {
            Debug.Assert(!box.IsUninitialized);
            // ByteString 是值类型且无 null 概念（default == Empty），与 BooleanFace 一致，
            // box 为 Null 时返回 TypeMismatch；上层若需要 "null → Empty" 映射，应在调用层做。
            if ((uint)(box.GetBits() >> HeapKindShift) == TagHeapKindBlobPayload) {
                byte[] bytes = ValuePools.OfOwnedBlob[box.GetHeapHandle()];
                value = new ByteString(bytes);
                return GetIssue.None;
            }
            value = default;
            return GetIssue.TypeMismatch;
        }
    }
}
