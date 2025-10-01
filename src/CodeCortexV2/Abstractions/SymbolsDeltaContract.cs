using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CodeCortexV2.Abstractions;

/// <summary>
/// Helpers enforcing the canonical ordering and validation contract for <see cref="SymbolsDelta"/> instances.
/// </summary>
public static class SymbolsDeltaContract {
    /// <summary>
    /// Normalize the provided delta according to the public contract: TypeAdds ascending by DocCommentId length,
    /// TypeRemovals descending. DocCommentIds must start with "T:".
    /// </summary>
    /// <param name="delta">The delta to normalize.</param>
    /// <returns>A new delta instance with validated and ordered collections.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="delta"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when DocCommentIds are missing or violate the contract.</exception>
    public static SymbolsDelta Normalize(SymbolsDelta delta) {
        if (delta is null) { throw new ArgumentNullException(nameof(delta)); }
        return Normalize(delta.TypeAdds, delta.TypeRemovals);
    }

    /// <summary>
    /// Normalize individual collections.
    /// </summary>
    public static SymbolsDelta Normalize(IReadOnlyList<SymbolEntry>? typeAdds, IReadOnlyList<TypeKey>? typeRemovals) {
        var adds = CopyAndValidate(
            typeAdds,
            static entry => entry.DocCommentId,
            static entry => entry.Assembly,
            "TypeAdds"
        );
        var removals = CopyAndValidate(
            typeRemovals,
            static key => key.DocCommentId,
            static key => key.Assembly,
            "TypeRemovals"
        );

        if (adds.Count > 1) {
            adds.Sort(static (left, right) => left.DocCommentId.Length.CompareTo(right.DocCommentId.Length));
        }
        if (removals.Count > 1) {
            removals.Sort(static (left, right) => right.DocCommentId.Length.CompareTo(left.DocCommentId.Length));
        }

        return new SymbolsDelta(adds, removals);
    }

    private static List<T> CopyAndValidate<T>(
        IReadOnlyList<T>? source,
        Func<T, string?> docIdSelector,
        Func<T, string?>? assemblySelector,
        string fieldName
    ) {
        if (source is null || source.Count == 0) { return new List<T>(); }

        var list = new List<T>(source.Count);
        for (int i = 0; i < source.Count; i++) {
            var item = source[i];
            var docId = docIdSelector(item);
            if (string.IsNullOrEmpty(docId) || !docId.StartsWith("T:", StringComparison.Ordinal)) {
                var message = $"{fieldName}[{i}] must have a DocCommentId starting with 'T:'";
                Debug.Assert(false, message);
                throw new InvalidOperationException(message);
            }
            if (assemblySelector is not null) {
                var assembly = assemblySelector(item);
                if (string.IsNullOrWhiteSpace(assembly)) {
                    var message = $"{fieldName}[{i}] ('{docId}') must specify Assembly";
                    Debug.Assert(false, message);
                    throw new InvalidOperationException(message);
                }
            }
            list.Add(item);
        }
        return list;
    }
}
