using System;

namespace CodeCortexV2.Index;

/// <summary>
/// Shared string utilities for symbol indexing and search.
/// Kept independent of Roslyn so it can be used by both the immutable index and synchronizer.
/// </summary>
public static class IndexStringUtil {
    /// <summary>
    /// Strip leading "global::" prefix if present.
    /// </summary>
    public static string StripGlobal(string fqn) =>
        fqn.StartsWith("global::", StringComparison.Ordinal) ? fqn.Substring(8) : fqn;

    /// <summary>
    /// For a simple identifier, return the base (no generic arity or type argument list).
    /// Examples: "List`1" -&gt; "List", "Dictionary&lt;TKey, TValue&gt;" -&gt; "Dictionary".
    /// </summary>
    public static string ExtractGenericBase(string name) {
        if (string.IsNullOrEmpty(name)) { return name; }
        var tick = name.IndexOf('`');
        if (tick >= 0) { return name.Substring(0, tick); }
        var lt = name.IndexOf('<');
        if (lt >= 0) { return name.Substring(0, lt); }
        return name;
    }
}
