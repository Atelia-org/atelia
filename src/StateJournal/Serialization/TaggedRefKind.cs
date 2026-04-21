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
    MixedOrderedDict = (byte)DurableObjectKind.MixedOrderedDict,
    TypedOrderedDict = (byte)DurableObjectKind.TypedOrderedDict,
    Text = (byte)DurableObjectKind.Text,
    Symbol = DurableObjectKindHelper.BitMask
}

internal static class TaggedRefKindHelper {
    internal const byte BitMask = DurableObjectKindHelper.BitMask;

    internal static bool IsValidKind(TaggedRefKind kind) => kind is
        TaggedRefKind.MixedDict
        or TaggedRefKind.TypedDict
        or TaggedRefKind.MixedDeque
        or TaggedRefKind.TypedDeque
        or TaggedRefKind.MixedOrderedDict
        or TaggedRefKind.TypedOrderedDict
        or TaggedRefKind.Text
        or TaggedRefKind.Symbol;

    internal static bool IsDurableObjectKind(TaggedRefKind kind) => kind is
        TaggedRefKind.MixedDict
        or TaggedRefKind.TypedDict
        or TaggedRefKind.MixedDeque
        or TaggedRefKind.TypedDeque
        or TaggedRefKind.MixedOrderedDict
        or TaggedRefKind.TypedOrderedDict
        or TaggedRefKind.Text;

    internal static TaggedRefKind FromDurableObjectKind(DurableObjectKind kind) => kind switch {
        DurableObjectKind.MixedDict => TaggedRefKind.MixedDict,
        DurableObjectKind.TypedDict => TaggedRefKind.TypedDict,
        DurableObjectKind.MixedDeque => TaggedRefKind.MixedDeque,
        DurableObjectKind.TypedDeque => TaggedRefKind.TypedDeque,
        DurableObjectKind.MixedOrderedDict => TaggedRefKind.MixedOrderedDict,
        DurableObjectKind.TypedOrderedDict => TaggedRefKind.TypedOrderedDict,
        DurableObjectKind.Text => TaggedRefKind.Text,
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
            case TaggedRefKind.MixedOrderedDict:
                objectKind = DurableObjectKind.MixedOrderedDict;
                return true;
            case TaggedRefKind.TypedOrderedDict:
                objectKind = DurableObjectKind.TypedOrderedDict;
                return true;
            case TaggedRefKind.Text:
                objectKind = DurableObjectKind.Text;
                return true;
            default:
                objectKind = default;
                return false;
        }
    }
}
