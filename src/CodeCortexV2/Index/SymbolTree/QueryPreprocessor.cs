using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal {
    /// <summary>
    /// Lightweight parser/preprocessor for SymbolTree queries.
    /// Responsibilities:
    /// - Detect DocId-style prefixes (e.g., "T:", "N:") to set DocIdKind and root-constraint; no DocId fast-path is used
    /// - Handle global:: root constraint
    /// - Split into namespace/type segments, expanding nested types ('+')
    /// - For each segment, if generic arity can be parsed from `n or &lt;...&gt;, normalize to DocId-like form: base`arity
    /// - Provide lower-cased variants for inclusive case-insensitive matching (no "intent" semantics).  LowerNormalizedSegments 用于 inclusive 阶段的忽略大小写匹配
    /// - MVP: reject malformed generic segments (unbalanced '&lt;' '&gt;')
    /// </summary>
    public static class QueryPreprocessor {

        public readonly record struct QueryInfo(
            string Raw, // 用户输入的原始查询文本
            SymbolKinds DocIdKind, // Represents kinds implied by DocId prefix; merged by OR with caller filter. 用户通过DocId风格前缀制定要**额外包含**的符号类别，也就是“T:”/"N:"那些。如果用户没输入这样的前缀，就为SymbolKinds.None。后面使用时会合并入Search函数传入的SymbolKinds参数。举例来说若Query为“T:Ns.Tpy”同时Search传入的SymbolKinds为Namespace，则结果应为并集，Namespace和Type类型的Symbol都是合法结果。
            bool IsRootAnchored, // 如果Query有“global::”或“T:”/“N:”等DocId风格的“根部前缀”，则表示结果都应以根节点开始，是匹配条件。DocId风格前缀不触发任何“快速路径”，不会绕过标准化匹配流程。
            string[] NormalizedSegments, // 原始输入去掉“根部前缀”后，按命名空间层次分段，逐段处理。如果某一段是泛型，则统一标准化为“`n”形式，有类型形参“<T>” / 无类参数名<,> / 有类型实参<int>”都是合法的输入，方便用户直接粘贴文本。
            string[] LowerNormalizedSegments, // 在SegmentsNormalized的基础上逐段ToLower。
            string? RejectionReason // 非空表示这是一个被拒绝/无效的查询，并给出人类可读原因
        ) {
            public bool IsRejected => !string.IsNullOrEmpty(RejectionReason);
        }

        public static QueryInfo Preprocess(string? query) {
            var raw = (query ?? string.Empty).Trim();
            if (raw.Length == 0) {
                return new QueryInfo(string.Empty, SymbolKinds.None, false, Array.Empty<string>(), Array.Empty<string>(), null);
            }

            bool root = false;
            SymbolKinds kinds = SymbolKinds.None;
            var eff = raw;
            const string RootPrefix = "global::";
            if (eff.StartsWith(RootPrefix, StringComparison.Ordinal)) {
                root = true;
                eff = eff.Substring(RootPrefix.Length);
            }

            // DocId-style prefix: N:/T:/M:/P:/F:/E:; reject "!:" explicitly
            if (eff.Length > 2 && eff[1] == ':') {
                if (eff[0] == '!') {
                    return new QueryInfo(raw, SymbolKinds.None, true, Array.Empty<string>(), Array.Empty<string>(), "Unsupported DocId prefix '!:' (internal Roslyn-only). Use N:/T:/M:/P:/F:/E:");
                }
                if (eff[0] == 'N' || eff[0] == 'T' || eff[0] == 'M' || eff[0] == 'P' || eff[0] == 'F' || eff[0] == 'E') {
                    root = true; // DocId implies root constraint
                    kinds = eff[0] switch {
                        'N' => SymbolKinds.Namespace,
                        'T' => SymbolKinds.Type,
                        'M' => SymbolKinds.Method,
                        'P' => SymbolKinds.Property,
                        'F' => SymbolKinds.Field,
                        'E' => SymbolKinds.Event,
                        _ => kinds,
                    };
                    eff = eff.Substring(2); // strip "X:"
                }
            }

            var segsOrig = SplitSegments(eff);
            if (segsOrig.Length == 0) {
                return new QueryInfo(raw, kinds, root, Array.Empty<string>(), Array.Empty<string>(), null);
            }

            // MVP reject malformed generic segments (unbalanced '<' '>')
            foreach (var s in segsOrig) {
                if (!IsBalancedAngles(s)) {
                    return new QueryInfo(raw, kinds, root, Array.Empty<string>(), Array.Empty<string>(), "Unbalanced generic angle brackets: '<' '>'");
                }
            }

            var segsNorm = new string[segsOrig.Length];
            var segsLower = new string[segsOrig.Length];
            for (int i = 0; i < segsOrig.Length; i++) {
                var s = segsOrig[i];
                var (bn, ar) = ParseTypeSegment(s);
                var norm = ar > 0 ? bn + "`" + ar.ToString() : bn;
                segsNorm[i] = norm;
                segsLower[i] = norm.ToLowerInvariant();
            }

            return new QueryInfo(raw, kinds, root, segsNorm, segsLower, null);
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

