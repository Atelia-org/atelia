using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeCortexV2.Index.SymbolTreeInternal {
    /// <summary>
    /// Lightweight parser/preprocessor for SymbolTree queries.
    /// Responsibilities:
    /// - Detect DocId queries (e.g., "T:...", "N:...") and return as-is
    /// - Handle global:: root constraint
    /// - Split into namespace/type segments, expanding nested types ('+')
    /// - For each segment, if generic arity can be parsed from `n or &lt;...&gt;, normalize to DocId-like form: base`arity
    /// - Track original segments and whether each segment is already lower-cased (for IgnoreCase intent)
    /// - MVP: reject malformed generic segments (unbalanced '&lt;' '&gt;')
    /// </summary>
    public static class QueryPreprocessor {
        public enum DocIdKind { None, Type, Namespace, Member /* future */ }

        public readonly record struct QueryInfo(
            string Raw,
            string Effective,
            bool IsDocId,
            DocIdKind Kind,
            bool RootConstraint,
            string[] SegmentsNormalized,
            string[] SegmentsOriginal,
            bool[] SegmentIsLower
        ) {
            public string LastOriginal => SegmentsOriginal.Length > 0 ? SegmentsOriginal[^1] : string.Empty;
            public string LastNormalized => SegmentsNormalized.Length > 0 ? SegmentsNormalized[^1] : string.Empty;
            public bool LastIsLower => SegmentIsLower.Length > 0 && SegmentIsLower[^1];
        }

        public static QueryInfo Preprocess(string? query) {
            var raw = (query ?? string.Empty).Trim();
            if (raw.Length == 0) {
                return new QueryInfo(string.Empty, string.Empty, false, DocIdKind.None, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<bool>());
            }

            // DocId detection (kept verbatim)
            if (raw.Length > 2 && raw[1] == ':' && (raw[0] == 'T' || raw[0] == 'N' || raw[0] == 'M')) {
                var kind = raw[0] == 'T' ? DocIdKind.Type : (raw[0] == 'N' ? DocIdKind.Namespace : DocIdKind.Member);
                return new QueryInfo(raw, raw, true, kind, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<bool>());
            }

            bool root = false;
            var eff = raw;
            const string RootPrefix = "global::";
            if (eff.StartsWith(RootPrefix, StringComparison.Ordinal)) {
                root = true;
                eff = eff.Substring(RootPrefix.Length);
            }

            var segsOrig = SplitSegments(eff);
            if (segsOrig.Length == 0) {
                return new QueryInfo(raw, eff, false, DocIdKind.None, root, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<bool>());
            }

            // MVP reject malformed generic segments (unbalanced '<' '>')
            bool invalid = false;
            foreach (var s in segsOrig) {
                if (!IsBalancedAngles(s)) {
                    invalid = true;
                    break;
                }
            }
            if (invalid) {
                return new QueryInfo(raw, eff, false, DocIdKind.None, root, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<bool>());
            }

            var segsNorm = new string[segsOrig.Length];
            var isLower = new bool[segsOrig.Length];
            for (int i = 0; i < segsOrig.Length; i++) {
                var s = segsOrig[i];
                isLower[i] = s == s.ToLowerInvariant();
                var (bn, ar) = ParseTypeSegment(s);
                segsNorm[i] = ar > 0 ? bn + "`" + ar.ToString() : bn;
            }

            return new QueryInfo(raw, eff, false, DocIdKind.None, root, segsNorm, segsOrig, isLower);
        }

        // --- Helpers ---
        public static string[] SplitSegments(string q) {
            if (string.IsNullOrEmpty(q)) {
                return Array.Empty<string>();
            }

            var parts = q.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) {
                return parts;
            }

            var last = parts[^1];
            var typeSegs = last.Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (typeSegs.Length == 1) {
                return parts;
            }

            var list = new List<string>(parts.Length - 1 + typeSegs.Length);
            for (int i = 0; i < parts.Length - 1; i++) {
                list.Add(parts[i]);
            }

            list.AddRange(typeSegs);
            return list.ToArray();
        }

        public static (string baseName, int arity) ParseTypeSegment(string seg) {
            if (string.IsNullOrEmpty(seg)) {
                return (seg, 0);
            }

            var baseName = seg;
            int arity = 0;
            var back = seg.IndexOf('`');
            if (back >= 0) {
                baseName = seg.Substring(0, back);
                var numStr = new string(seg.Skip(back + 1).TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(numStr, out var n1)) {
                    arity = n1;
                }
            }
            var lt = seg.IndexOf('<');
            if (lt >= 0) {
                baseName = seg.Substring(0, lt);
                var inside = seg.Substring(lt + 1);
                var rt = inside.LastIndexOf('>');
                if (rt < 0) {
                    // unbalanced, let caller reject via IsBalancedAngles
                } else {
                    inside = inside.Substring(0, rt);
                    if (inside.Length > 0) {
                        arity = inside.Count(c => c == ',') + 1;
                    }
                }
            }
            return (baseName, arity);
        }

        private static bool IsBalancedAngles(string seg) {
            int bal = 0;
            foreach (var ch in seg) {
                if (ch == '<') {
                    bal++;
                } else if (ch == '>') {
                    bal--;
                }

                if (bal < 0) {
                    return false;
                }
            }
            return bal == 0;
        }
    }
}

