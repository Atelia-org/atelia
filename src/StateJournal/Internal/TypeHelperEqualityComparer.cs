namespace Atelia.StateJournal.Internal;

internal sealed class TypeHelperEqualityComparer<T, THelper> : IEqualityComparer<T>
    where T : notnull
    where THelper : unmanaged, ITypeHelper<T> {
    public static TypeHelperEqualityComparer<T, THelper> Instance { get; } = new();

    private TypeHelperEqualityComparer() { }

    public bool Equals(T? x, T? y) {
        if (x is null) { return y is null; }
        if (y is null) { return false; }
        return THelper.Equals(x, y);
    }

    public int GetHashCode(T obj) {
        ArgumentNullException.ThrowIfNull(obj);
        return THelper.GetHashCode(obj);
    }
}
