using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Serialization;

/// <summary>
/// Binary diff 流中用于 tagged reference payload 的 kind。
/// 前四个值与 <see cref="DurableObjectKind"/> 数值对齐，额外扩展一个 <see cref="Symbol"/>。
/// </summary>
internal enum TaggedRefKind : byte {
    Blank = 0,
    MixedDict = (byte)DurableObjectKind.MixedDict,
    TypedDict = (byte)DurableObjectKind.TypedDict,
    MixedDeque = (byte)DurableObjectKind.MixedDeque,
    TypedDeque = (byte)DurableObjectKind.TypedDeque,
    Symbol,
    Mask = (1 << ValueBox.DurRefKindBitCount) - 1
}

internal static class TaggedRefKindHelper {
    internal static bool IsValidKind(TaggedRefKind kind) => kind is
        TaggedRefKind.MixedDict
        or TaggedRefKind.TypedDict
        or TaggedRefKind.MixedDeque
        or TaggedRefKind.TypedDeque
        or TaggedRefKind.Symbol;

    internal static bool IsDurableObjectKind(TaggedRefKind kind) => kind is
        TaggedRefKind.MixedDict
        or TaggedRefKind.TypedDict
        or TaggedRefKind.MixedDeque
        or TaggedRefKind.TypedDeque;

    internal static TaggedRefKind FromDurableObjectKind(DurableObjectKind kind) => kind switch {
        DurableObjectKind.MixedDict => TaggedRefKind.MixedDict,
        DurableObjectKind.TypedDict => TaggedRefKind.TypedDict,
        DurableObjectKind.MixedDeque => TaggedRefKind.MixedDeque,
        DurableObjectKind.TypedDeque => TaggedRefKind.TypedDeque,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only real DurableObject kinds can be converted to TaggedRefKind."),
    };

    internal static bool TryToDurableObjectKind(TaggedRefKind kind, out DurableObjectKind objectKind) {
        switch (kind) {
            case TaggedRefKind.MixedDict:
                objectKind = DurableObjectKind.MixedDict;
                return true;
            case TaggedRefKind.TypedDict:
                objectKind = DurableObjectKind.TypedDict;
                return true;
            case TaggedRefKind.MixedDeque:
                objectKind = DurableObjectKind.MixedDeque;
                return true;
            case TaggedRefKind.TypedDeque:
                objectKind = DurableObjectKind.TypedDeque;
                return true;
            default:
                objectKind = default;
                return false;
        }
    }
}
