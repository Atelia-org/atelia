using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeCortex.Workspace.SymbolQuery {
    public enum MatchCategory {
        Exact,
        SimpleExact,
        Prefix,
        Suffix,
        Contains,
        WildcardFqn,
        WildcardFqnNoPrefix,
        WildcardSimple
    }

    public sealed record SymbolMatch(string Fqn, MatchCategory Category);

    public static class SimpleSymbolMatcher {
        public static IReadOnlyList<SymbolMatch> Match(IReadOnlyList<TypeEntry> entries, string pattern) {
            if (entries == null || entries.Count == 0) return Array.Empty<SymbolMatch>();
            pattern ??= string.Empty;
            var hasWildcard = pattern.IndexOfAny(new[] { '*', '?' }) >= 0;
            var results = new List<SymbolMatch>(entries.Count);

            Regex? rx = null;
            if (hasWildcard) {
                rx = BuildWildcardRegex(pattern);
            }

            foreach (var e in entries) {
                var fqn = e.Fqn;
                var simple = e.SimpleName ?? string.Empty;
                var fqnNoPrefix = fqn.StartsWith("global::", StringComparison.Ordinal) ? fqn[8..] : fqn;

                if (hasWildcard) {
                    if (rx!.IsMatch(fqn)) {
                        results.Add(new SymbolMatch(fqn, MatchCategory.WildcardFqn));
                    } else if (rx.IsMatch(fqnNoPrefix)) {
                        results.Add(new SymbolMatch(fqn, MatchCategory.WildcardFqnNoPrefix));
                    } else if (rx.IsMatch(simple)) {
                        results.Add(new SymbolMatch(fqn, MatchCategory.WildcardSimple));
                    }
                } else {
                    if (string.Equals(fqn, pattern, StringComparison.Ordinal) || string.Equals(fqnNoPrefix, pattern, StringComparison.Ordinal)) {
                        results.Add(new SymbolMatch(fqn, MatchCategory.Exact));
                    } else if (string.Equals(simple, pattern, StringComparison.Ordinal)) {
                        results.Add(new SymbolMatch(fqn, MatchCategory.SimpleExact));
                    } else if (fqn.StartsWith(pattern, StringComparison.Ordinal)) {
                        results.Add(new SymbolMatch(fqn, MatchCategory.Prefix));
                    } else if (fqn.EndsWith(pattern, StringComparison.Ordinal)) {
                        results.Add(new SymbolMatch(fqn, MatchCategory.Suffix));
                    } else if (fqn.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) {
                        results.Add(new SymbolMatch(fqn, MatchCategory.Contains));
                    }
                }
            }

            return results;
        }

        private static Regex BuildWildcardRegex(string pattern) {
            // Convert simple wildcard (* ?) into regex, escape others
            var escaped = Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".");
            return new Regex("^" + escaped + "$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }
    }
}

