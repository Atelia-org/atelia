using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public enum DurableObjectKind : byte {
    Blank = 0,
    MixedDict,
    TypedDict,
    MixedDeque,
    TypedDeque,
    Mask = (1 << ValueBox.DurRefKindBitCount) - 1
}
