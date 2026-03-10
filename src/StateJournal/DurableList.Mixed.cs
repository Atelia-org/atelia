using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>替代<see cref="DurableList{ValueBox}"/></summary>
public abstract class DurableList : DurableObject {
    private protected override ReadOnlySpan<byte> TypeCode => HelperRegistry.MixedList.TypeCode;

    internal DurableList() {
    }
}
