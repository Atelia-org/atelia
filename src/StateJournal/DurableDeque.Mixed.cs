using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>替代<see cref="DurableDeque{ValueBox}"/></summary>
public abstract class DurableDeque : DurableObject {
    private protected override ReadOnlySpan<byte> TypeCode => HelperRegistry.MixedDeque.TypeCode;

    internal DurableDeque() {
    }
}
