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
    /// - Provide lower-cased variants for inclusive case-insensitive matching (no "intent" semantics). LowerNormalizedSegments 用于 inclusive 阶段的忽略大小写匹配（当小写与标准化段相同则记为 null）
    /// - MVP: reject malformed generic segments (unbalanced '&lt;' '&gt;')
    /// </summary>
    public static class QueryPreprocessor {

        public readonly record struct QueryInfo(
            string Raw, // 用户输入的原始查询文本
            SymbolKinds DocIdKind, // Represents kinds implied by DocId prefix; merged by OR with caller filter. 用户通过DocId风格前缀制定要**额外包含**的符号类别，也就是“T:”/"N:"那些。如果用户没输入这样的前缀，就为SymbolKinds.None。后面使用时会合并入Search函数传入的SymbolKinds参数。举例来说若Query为“T:Ns.Tpy”同时Search传入的SymbolKinds为Namespace，则结果应为并集，Namespace和Type类型的Symbol都是合法结果。
            bool IsRootAnchored, // 如果Query有“global::”或“T:”/“N:”等DocId风格的“根部前缀”，则表示结果都应以根节点开始，是匹配条件。DocId风格前缀不触发任何“快速路径”，不会绕过标准化匹配流程。
            string[] NormalizedSegments, // 原始输入去掉“根部前缀”后，按命名空间层次分段，逐段处理。如果某一段是泛型，则统一标准化为“`n”形式，有类型形参“<T>” / 无类参数名<,> / 有类型实参<int>”都是合法的输入，方便用户直接粘贴文本。
            string?[] LowerNormalizedSegments, // 在 SegmentsNormalized 的基础上逐段 ToLower；当小写结果与标准化段相同（即不存在大小写差异）时，该位置为 null，用于防止误用并避免无意义的重复匹配。
            string? RejectionReason, // 非空表示这是一个被拒绝/无效的查询，并给出人类可读原因
            bool RequireDirectChildren // 若为 true，表示查询意图为“返回最后一段匹配节点的直接子项（尾随点语义）”
        ) {
            public bool IsRejected => !string.IsNullOrEmpty(RejectionReason);
        }

        public static QueryInfo Preprocess(string? query) {
            var raw = (query ?? string.Empty).Trim();
            if (raw.Length == 0) { return new QueryInfo(string.Empty, SymbolKinds.None, false, Array.Empty<string>(), Array.Empty<string>(), null, false); }
            bool root = false;
            SymbolKinds kinds = SymbolKinds.None;
            var eff = raw;
            // global:: 前缀（区分大小写）
            var stripped = SymbolNormalization.StripGlobalPrefix(eff);
            if (!ReferenceEquals(stripped, eff)) {
                root = true;
                eff = stripped;
            }

            // 尾随点：表示请求“直接子项”。处理顺序置于 DocId 解析之前或之后均可，这里选择在 DocId 剥离之后再检查。
            // 支持如："N.", "Foo.Bar.", "T:Ns.Type." 等。多重点尾随与单点等价。
            bool requireChildren = SymbolNormalization.HasTrailingDirectChildMarker(eff, out var withoutMarker);
            if (requireChildren) {
                eff = withoutMarker;
            }

            // DocId-style prefix: N:/T:/M:/P:/F:/E:; reject "!:" explicitly
            if (SymbolNormalization.TryParseDocIdPrefix(eff, out var pkind, out var remainder)) {
                root = true; // DocId implies root constraint
                kinds = pkind;
                eff = remainder;
            }
            else if (eff.Length > 2 && eff[1] == ':' && eff[0] == '!') { return new QueryInfo(raw, SymbolKinds.None, true, Array.Empty<string>(), Array.Empty<string>(), "Unsupported DocId prefix '!:' (internal Roslyn-only). Use N:/T:/M:/P:/F:/E:", requireChildren); }

            var segsOrig = SymbolNormalization.SplitSegmentsWithNested(eff);
            if (segsOrig.Length == 0) { return new QueryInfo(raw, kinds, root, Array.Empty<string>(), Array.Empty<string>(), null, requireChildren); }
            foreach (var s in segsOrig) {
                if (!SymbolNormalization.IsBalancedGenericAngles(s)) { return new QueryInfo(raw, kinds, root, Array.Empty<string>(), Array.Empty<string>(), "Unbalanced generic angle brackets: '<' '>'", requireChildren); }
            }

            var segsNorm = new string[segsOrig.Length];
            var segsLower = new string?[segsOrig.Length];
            for (int i = 0; i < segsOrig.Length; i++) {
                var s = segsOrig[i];
                var (bn, ar, _) = SymbolNormalization.ParseGenericArity(s);
                var norm = ar > 0 ? bn + "`" + ar.ToString() : bn;
                segsNorm[i] = norm;
                segsLower[i] = SymbolNormalization.ToLowerIfDifferent(norm);
            }

            return new QueryInfo(raw, kinds, root, segsNorm, segsLower, null, requireChildren);
        }

        // --- Helpers ---
        // 旧的内部解析 Helper 已迁移至 SymbolNormalization；保留空壳以兼容现有测试引用（如果有）。
        [Obsolete("Use SymbolNormalization.SplitSegmentsWithNested")] public static string[] SplitSegments(string q) => SymbolNormalization.SplitSegmentsWithNested(q);
        [Obsolete("Use SymbolNormalization.ParseGenericArity")]
        public static (string baseName, int arity) ParseTypeSegment(string seg) {
            var (b, a, _) = SymbolNormalization.ParseGenericArity(seg);
            return (b, a);
        }
    }
}

