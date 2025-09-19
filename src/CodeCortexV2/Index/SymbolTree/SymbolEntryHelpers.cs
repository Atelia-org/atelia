using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;

namespace CodeCortexV2.Index.SymbolTreeInternal {
    internal static class SymbolEntryHelpers {
        public readonly record struct ReferenceKey(string DocId, string? AssemblyName);
        public readonly record struct NameAlias(string Name, string NameType, bool IsExact);

        /// <summary>
        /// Enumerate reference keys for an entry: its own identity (when type) and its namespace chain.
        /// This is Roslyn-free: relies solely on SymbolEntry-projected fields.
        /// </summary>
        public static IEnumerable<ReferenceKey> EnumerateReferenceChain(SymbolEntry entry) {
            if (entry.Kind.HasFlag(SymbolKinds.Type)) {
                yield return new ReferenceKey(entry.SymbolId, string.IsNullOrWhiteSpace(entry.Assembly) ? null : entry.Assembly);
            }
            else if (entry.Kind.HasFlag(SymbolKinds.Namespace)) {
                // Include the namespace itself as a reference (no assembly)
                yield return new ReferenceKey(entry.SymbolId, null);
            }

            // Walk parent namespaces from the entry's ParentNamespace
            var ns = entry.ParentNamespace;
            if (!string.IsNullOrEmpty(ns)) {
                foreach (var nsId in EnumerateNamespaceIds(ns)) {
                    yield return new ReferenceKey(nsId, null);
                }
            }
        }

        /// <summary>
        /// For a type entry, enumerate name aliases used for name-buckets.
        /// - Exact: DocId simple (List`1), pseudo-generic simple (List&lt;T..&gt;)
        /// - Non-exact: GenericBase (List)
        /// - IgnoreCase: lower variants when they differ
        /// </summary>
        public static IEnumerable<NameAlias> EnumerateTypeAliases(SymbolEntry entry) {
            if (!entry.Kind.HasFlag(SymbolKinds.Type)) {
                yield break;
            }

            var docIdSimple = ExtractDocIdSimple(entry.SymbolId); // e.g., List`1
            var genCount = ExtractArity(docIdSimple);
            var pseudoGeneric = genCount > 0 ? BuildPseudoGeneric(entry.GenericBase, genCount) : entry.GenericBase; // List<T>, List<T1,T2>

            // Exact
            if (!string.IsNullOrEmpty(docIdSimple)) {
                yield return new NameAlias(docIdSimple, "DocId", true);
            }
            if (!string.IsNullOrEmpty(pseudoGeneric) && genCount > 0) {
                yield return new NameAlias(pseudoGeneric, "FQN", true);
            }

            // Non-exact (generic base)
            if (!string.IsNullOrEmpty(entry.GenericBase)) {
                yield return new NameAlias(entry.GenericBase, "GenericBase", false);
            }

            // Ignore-case variants

            if (!string.IsNullOrEmpty(docIdSimple)) {
                foreach (var a in AddLowerIter(docIdSimple, "DocId", false)) {
                    yield return a;
                }
            }
            if (genCount > 0 && !string.IsNullOrEmpty(pseudoGeneric)) {
                foreach (var a in AddLowerIter(pseudoGeneric, "FQN", false)) {
                    yield return a;
                }
            }
            if (!string.IsNullOrEmpty(entry.GenericBase)) {
                foreach (var a in AddLowerIter(entry.GenericBase, "GenericBase", false)) {
                    yield return a;
                }
            }
        }

        /// <summary>
        /// For a single namespace segment name, enumerate its aliases (exact + ignore-case when different).
        /// </summary>
        public static IEnumerable<NameAlias> EnumerateNamespaceAliases(string segmentName) {
            if (string.IsNullOrEmpty(segmentName)) {
                yield break;
            }

            yield return new NameAlias(segmentName, "Exact", true);
            var lower = segmentName.ToLowerInvariant();
            if (!string.Equals(lower, segmentName, StringComparison.Ordinal)) {
                yield return new NameAlias(lower, "IgnoreCase", false);
            }
        }

        private static IEnumerable<NameAlias> AddLowerIter(string s, string type, bool exact) {
            var lower = s.ToLowerInvariant();
            if (!string.Equals(lower, s, StringComparison.Ordinal)) {
                yield return new NameAlias(lower, type + "+IgnoreCase", exact);
            }
        }

        private static string ExtractDocIdSimple(string symbolId) {
            // From T:Ns1.Ns2.Name`1 or T:Ns.Name+Nested`2 to Name`n
            if (string.IsNullOrEmpty(symbolId)) { return string.Empty; }
            int colon = symbolId.IndexOf(':');
            if (colon < 0) { return string.Empty; }
            var tail = symbolId[(colon + 1)..];
            // last segment after '.' or '+'
            int dot = tail.LastIndexOf('.');
            int plus = tail.LastIndexOf('+');
            int sep = Math.Max(dot, plus);
            var last = sep >= 0 ? tail[(sep + 1)..] : tail;
            return last;
        }

        private static int ExtractArity(string name) {
            // Name`n -> n
            if (string.IsNullOrEmpty(name)) { return 0; }
            var idx = name.LastIndexOf('`');
            if (idx < 0 || idx + 1 >= name.Length) { return 0; }
            if (int.TryParse(name[(idx + 1)..], out int n) && n > 0) { return n; }
            return 0;
        }

        private static string BuildPseudoGeneric(string baseName, int arity) {
            if (arity <= 0) { return baseName; }
            var args = Enumerable.Range(1, arity).Select(i => i == 1 ? "T" : ($"T{i}"));
            return baseName + "<" + string.Join(",", args) + ">";
        }

        private static IEnumerable<string> EnumerateNamespaceIds(string nsPath) {
            // nsPath like "System.Collections.Generic"
            if (string.IsNullOrEmpty(nsPath)) {
                yield break;
            }

            var parts = nsPath.Split('.');
            for (int i = parts.Length; i >= 1; i--) {
                var prefix = string.Join('.', parts.Take(i));
                yield return "N:" + prefix;
            }
        }
    }
}

