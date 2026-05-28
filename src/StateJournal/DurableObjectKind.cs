using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public enum DurableObjectKind : byte {
    Blank = 0,
    MixedDict,
    TypedDict,
    MixedDeque,
    TypedDeque,
    MixedOrderedDict,
    TypedOrderedDict,
    Text,
    TypedHashSet,
}

internal static class DurableObjectKindHelper {
    internal const byte BitMask = (1 << ValueBox.DurRefKindBitCount) - 1;

    /// <summary>
    /// 统一判定“真实 DurableObject kind”。
    /// 该集合同时用于 frame tag、DurableRef/ValueBox 和 tagged ref 序列化，
    /// 不再拆成“可引用”和“可落盘”两套白名单。
    /// </summary>
    internal static bool IsValidObjectKind(DurableObjectKind kind) => kind is
        DurableObjectKind.MixedDict
        or DurableObjectKind.TypedDict
        or DurableObjectKind.MixedDeque
        or DurableObjectKind.TypedDeque
        or DurableObjectKind.MixedOrderedDict
        or DurableObjectKind.TypedOrderedDict
        or DurableObjectKind.Text
        or DurableObjectKind.TypedHashSet;
}
