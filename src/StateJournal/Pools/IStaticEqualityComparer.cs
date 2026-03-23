namespace Atelia.StateJournal.Pools;

internal interface IStaticEqualityComparer<in T> where T : notnull {
    static abstract bool Equals(T? a, T? b);
    static abstract int GetHashCode(T obj);
}

internal readonly struct OrdinalStaticEqualityComparer : IStaticEqualityComparer<string> {
    public static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    public static int GetHashCode(string obj) => obj.GetHashCode(StringComparison.Ordinal);
}

internal readonly struct IgnoreCaseStaticEqualityComparer : IStaticEqualityComparer<string> {
    public static bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static int GetHashCode(string obj) => obj.GetHashCode(StringComparison.OrdinalIgnoreCase);
}
