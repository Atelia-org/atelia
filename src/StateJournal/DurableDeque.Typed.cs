using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public abstract class DurableDeque<T> : DurableObject where T : notnull {
    /// <summary>由<see cref="TypedDequeFactory{T}"/>初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    internal DurableDeque() {
    }
}
