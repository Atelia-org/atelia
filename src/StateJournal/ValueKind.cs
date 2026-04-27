namespace Atelia.StateJournal;

public enum ValueKind : byte {
    Blank = 0,

    FloatingPoint,
    NonnegativeInteger,
    NegativeInteger,

    /// <summary>Symbol facade — intern 池中的字符串身份。占用了原 <c>String</c> 的槽位。</summary>
    Symbol,

    /// <summary><see cref="DurableDict{TKey}"/> heterogeneous</summary>
    MixedDict,
    /// <summary><see cref="DurableDict{TKey,TValue}"/>  homogeneous</summary>
    TypedDict,
    /// <summary><see cref="DurableDeque"/> heterogeneous</summary>
    MixedDeque,
    /// <summary><see cref="DurableDeque{T}"/> homogeneous</summary>
    TypedDeque,
    /// <summary><see cref="DurableOrderedDict{TKey}"/> heterogeneous</summary>
    MixedOrderedDict,
    /// <summary><see cref="DurableOrderedDict{TKey, TValue}"/> homogeneous</summary>
    TypedOrderedDict,
    /// <summary><see cref="DurableText"/></summary>
    Text,

    Boolean,
    Null,

    /// <summary>Payload string — 非 intern，独立 owned 字节序列。A1 阶段暂未挂任何 <c>ValueBox</c>，预留给 B2/C 启用。</summary>
    String,

    /// <summary>Payload blob — <see cref="ByteString"/>，独立 owned 字节序列；CMS Step 3b 启用。</summary>
    Blob,
}
