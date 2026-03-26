using System.Diagnostics;
namespace Atelia.StateJournal.Internal;

partial struct ValueBox {
    #region DurableRef
    internal const int DurRefKindBitCount = 4, DurRefIdBitCount = 32;
    internal const int DurRefKindShift = DurRefIdBitCount;

    internal bool IsDurableRef => GetLzc() == BoxLzc.DurableRef;
    internal readonly DurableObjectKind GetDurRefKind() {
        Debug.Assert(IsDurableRef);
        return (DurableObjectKind)(GetBits() >> DurRefKindShift) & DurableObjectKind.Mask;
    }
    /// <summary>依赖<see cref="DurRefIdBitCount"/> == 32</summary>
    internal readonly LocalId GetDurRefId() {
        Debug.Assert(IsDurableRef);
        return new((uint)GetBits());
    }
    private readonly DurableRef DecodeDurableRef() {
        return new(GetDurRefKind(), GetDurRefId());
    }
    #endregion

    internal readonly struct DurableRefFace : ITypedFace<DurableRef> {
        /// <summary>将 DurableRef 编码为 ValueBox。</summary>
        public static ValueBox From(DurableRef value) {
            if (value.IsNull) { return Null; }
            Debug.Assert(DurableRef.IsValidObjectKind(value.Kind), $"Invalid DurableRef kind: {value.Kind}");
            return new(LzcConstants.DurableRefTag | ((ulong)value.Kind << DurRefKindShift) | value.Id.Value);
        }

        /// <summary>将 ValueBox 覆写为指定的 DurableRef 值。</summary>
        /// <remarks>
        /// 旧值如果持有 <see cref="ValuePools.OfBits64"/> slot（数值类型），会立即释放。
        /// 旧值如果持有 InternPool slot（例如字符串等），因 InternPool 共享语义不支持手动 Free，旧 slot 由 Mark-Sweep GC 回收。
        /// </remarks>
        public static bool UpdateOrInit(ref ValueBox old, DurableRef value) {
            var newBox = From(value);
            if (old.GetBits() == newBox.GetBits()) { return false; }
            FreeOldBits64IfNeeded(old);
            old = newBox;
            return true;
        }

        /// <summary>
        /// 尝试将 ValueBox 读取为DurableObject。
        /// </summary>
        /// <param name="value">读取到的DurableObject。仅当返回 <see cref="GetIssue.None"/> 时有效。</param>
        /// <returns>
        /// <see cref="GetIssue.None"/> 成功；
        /// <see cref="GetIssue.TypeMismatch"/> 当 ValueBox 不是DurableObject类型。
        /// </returns>
        /// <remarks>依赖<see cref="LocalId.Null"/> == default</remarks>
        public static GetIssue Get(ValueBox box, out DurableRef value) {
            Debug.Assert(!box.IsUninitialized);
            if (box.IsNull) {
                value = default;
                return GetIssue.None;
            }
            if (box.IsDurableRef) {
                value = box.DecodeDurableRef();
                return GetIssue.None;
            }
            value = default;
            return GetIssue.TypeMismatch;
        }
    }
}
