namespace Atelia.StateJournal;

public enum DurableObjectKind : byte {
    Blank = 0,
    MixedDict,
    TypedDict,
    MixedList,
    TypedList,
    Mask = (1 << 4) - 1
}
