using System;
using System.Collections.Generic;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal;

/// <summary>
/// 统一别名生成：供 WithDelta 与 FromEntries 共用。
/// 输入为节点的“标准化名称”（命名空间段或类型段：类型段可能带反引号 arity）。
/// </summary>
internal static class AliasGeneration {
    internal readonly record struct AliasSpec(bool IsExact, string Key, MatchFlags Flags);

    public static IEnumerable<AliasSpec> GetNamespaceAliases(string segment) {
        if (string.IsNullOrEmpty(segment)) { yield break; }
        yield return new AliasSpec(true, segment, MatchFlags.None);
        var lower = segment.ToLowerInvariant();
        if (!string.Equals(lower, segment, StringComparison.Ordinal)) {
            yield return new AliasSpec(false, lower, MatchFlags.IgnoreCase);
        }
    }

    public static IEnumerable<AliasSpec> GetTypeAliases(string normalizedTypeSegment) {
        if (string.IsNullOrEmpty(normalizedTypeSegment)) { yield break; }
        var (bn, ar, _) = SymbolNormalization.ParseGenericArity(normalizedTypeSegment);
        if (ar > 0) {
            var docIdLike = bn + "`" + ar;
            yield return new AliasSpec(true, docIdLike, MatchFlags.None);
            yield return new AliasSpec(false, bn, MatchFlags.IgnoreGenericArity);
            var lowerBn = bn.ToLowerInvariant();
            if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                yield return new AliasSpec(false, lowerBn, MatchFlags.IgnoreGenericArity | MatchFlags.IgnoreCase);
            }
            var lowerDoc = docIdLike.ToLowerInvariant();
            if (!string.Equals(lowerDoc, docIdLike, StringComparison.Ordinal)) {
                yield return new AliasSpec(false, lowerDoc, MatchFlags.IgnoreCase);
            }
        }
        else {
            yield return new AliasSpec(true, bn, MatchFlags.None);
            var lowerBn = bn.ToLowerInvariant();
            if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                yield return new AliasSpec(false, lowerBn, MatchFlags.IgnoreCase);
            }
        }
    }
}
