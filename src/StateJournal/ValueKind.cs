namespace Atelia.StateJournal;

public enum ValueKind : byte {
    Blank = 0,

    FloatingPoint,
    NonnegativeInteger,
    NegativeInteger,

    String,

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

    Boolean,
    Null,
}
