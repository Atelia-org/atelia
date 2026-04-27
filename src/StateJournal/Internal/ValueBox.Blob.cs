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
    /// CMS Step B 起额外提供 <see cref="FromTrusted"/> / <see cref="UpdateOrInitTrusted"/> 高级零拷贝路径，
    /// 配合 <see cref="ByteString.FromTrustedOwned(byte[])"/> 让性能敏感场景 opt-in 跳过 clone（契约见各方法 doc）。
    /// <see cref="Get"/> 返回的 <see cref="ByteString"/> 包装 pool 内副本，
    /// 但因 <see cref="ByteString"/> 公开 API 仅暴露 <see cref="ReadOnlySpan{T}"/>，外部无法 mutate。</para>
    /// </remarks>
    internal readonly struct BlobPayloadFace : ITypedFace<ByteString> {
        public static ValueBox From(ByteString value) => StoreNewSlot(CloneForPool(value));

        /// <summary>
        /// 高级零拷贝入池路径：直接复用 <paramref name="value"/> 底层 <see cref="byte"/>[] 引用，跳过 defensive clone。
        /// 调用方必须保证该 <see cref="ByteString"/> 通过 <see cref="ByteString.FromTrustedOwned(byte[])"/> 构造
        /// （或对底层数组的"独占 + immutable"有等价保证）。违反契约会导致 pool 内字节被外部静默篡改。
        /// </summary>
        public static ValueBox FromTrusted(ByteString value) => StoreNewSlot(value.DangerousGetUnderlyingArray());

        public static bool UpdateOrInit(ref ValueBox old, ByteString value, out uint oldBareBytesBeforeMutation)
            => UpdateOrInitCore(ref old, value, trusted: false, out oldBareBytesBeforeMutation);

        /// <summary>
        /// 高级零拷贝 in-place 入池路径：与 <see cref="UpdateOrInit"/> 完全相同的 5 路径决策（new / no-op /
        /// inplace overwrite / cross-kind / freeze-fork），但所有写入 pool 的 <see cref="byte"/>[] 都直接复用
        /// <paramref name="value"/> 底层数组而非 <c>CloneForPool</c>。CMS Step 1 snapshot 契约
        /// （<paramref name="oldBareBytesBeforeMutation"/> 在任何 mutation 前 capture）保持不变。
        /// 若内容相同命中 no-op 路径，本方法不会写入 pool，也不会接管本次传入数组；调用方若来自
        /// <see cref="System.Buffers.ArrayPool{T}"/> 等可回收来源，仍需自行处理该数组的生命周期。
        /// 契约同 <see cref="FromTrusted"/>。
        /// </summary>
        public static bool UpdateOrInitTrusted(ref ValueBox old, ByteString value, out uint oldBareBytesBeforeMutation)
            => UpdateOrInitCore(ref old, value, trusted: true, out oldBareBytesBeforeMutation);

        private static ValueBox StoreNewSlot(byte[] owned) {
            SlotHandle handle = ValuePools.OfOwnedBlob.Store(owned);
            return EncodeHeapSlot(HeapValueKind.BlobPayload, handle);
        }

        private static bool UpdateOrInitCore(ref ValueBox old, ByteString value, bool trusted, out uint oldBareBytesBeforeMutation) {
            // 关键：必须在任何对 slot / 后备 OwnedBlobPool 的修改之前捕获 oldBareBytes（CMS Step 1 契约）。
            // trusted 与默认路径在此处完全一致：snapshot 仅依赖 old box 状态。
            oldBareBytesBeforeMutation = old.IsUninitialized ? 0u : old.EstimateBareSize();
            // inplace 更新：旧 box 是 exclusive BlobPayload → 比较内容；同则 no-op，否则覆写 slot 内容。
            if (old.GetLzc() == BoxLzc.HeapSlot
                && old.GetTagAndKind() == TagHeapKindBlobPayload
                && old.IsExclusive) {
                SlotHandle h = old.GetHeapHandle();
                byte[] current = ValuePools.OfOwnedBlob[h];
                if (current.AsSpan().SequenceEqual(value.AsSpan())) { return false; }
                ValuePools.OfOwnedBlob[h] = trusted ? value.DangerousGetUnderlyingArray() : CloneForPool(value);
                // bits 不变（同 handle、kind、exclusive bit 都不变）；返回 true 表示内容变化。
                return true;
            }
            // 其他情况：释放旧 owned heap slot（如有），分配新 BlobPayload slot。
            FreeOldOwnedHeapIfNeeded(old);
            byte[] owned = trusted ? value.DangerousGetUnderlyingArray() : CloneForPool(value);
            SlotHandle newHandle = ValuePools.OfOwnedBlob.Store(owned);
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
                // CMS Step D: 走 FromTrustedOwned 跳过 ctor 的 defensive clone。Get 返回的 ByteString 包装的是 pool 内
                // byte[]，public ByteString API 仅暴露 ReadOnlySpan / IsEmpty / Length / Equals，外部用户无法 mutate
                // 底层数组（DangerousGetUnderlyingArray 是 internal），因此零拷贝安全。
                value = ByteString.FromTrustedOwned(bytes);
                return GetIssue.None;
            }
            value = default;
            return GetIssue.TypeMismatch;
        }
    }
}
