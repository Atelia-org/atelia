using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public abstract class DurableDeque<T> : DurableDequeBase, IDeque<T> where T : notnull {
    /// <summary>由<see cref="TypedDequeFactory{T}"/>初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    internal DurableDeque() {
    }

    #region DurableObject
    public override DurableObjectKind Kind => DurableObjectKind.TypedDeque;
    #endregion

    public abstract int Count { get; }
    public abstract void PushFront(T? value);
    public abstract void PushBack(T? value);
    public abstract GetIssue GetAt(int index, out T? value);
    public abstract GetIssue PeekFront(out T? value);
    public abstract GetIssue PeekBack(out T? value);
    public abstract bool TrySetAt(int index, T? value);
    public abstract bool TrySetFront(T? value);
    public abstract bool TrySetBack(T? value);
    public abstract GetIssue PopFront(out T? value);
    public abstract GetIssue PopBack(out T? value);
}
