using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public enum DurableObjectKind : byte {
    Blank = 0,
    MixedDict,
    TypedDict,
    MixedDeque,
    TypedDeque,
    TypedOrderedDict
}

internal static class DurableObjectKindHelper {
    internal const byte BitMask = (1 << ValueBox.DurRefKindBitCount) - 1;
}
