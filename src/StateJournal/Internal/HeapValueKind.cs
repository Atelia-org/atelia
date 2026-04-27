namespace Atelia.StateJournal;

internal enum HeapValueKind : byte {
    Blank = 0,

    #region 注意这3项表示数值的，需保持连续和保序，用于性能优化
    FloatingPoint,
    // 不能设计为Unsigend+Signed划分，否则会因类型不同使得`Encode((ulong)long.MaxValue) != Encode(long.MaxValue)`
    NonnegativeInteger,
    NegativeInteger,
    #endregion

    Symbol,

    /// <summary>Payload string — 非 intern，独立 owned 堆 slot（每次 Store 分配新 slot，不去重）。</summary>
    StringPayload,

    /// <summary>Payload blob — 非去重，独立 owned 堆 slot（byte[]，每次 Store 分配新 slot）。</summary>
    BlobPayload,
}

internal static class HeapValueKindHelper {
    internal const byte BitMask = (1 << Internal.ValueBox.HeapKindBitCount) - 1;
}
