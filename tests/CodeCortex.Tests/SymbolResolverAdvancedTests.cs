using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortex.Core.Index;
using CodeCortex.Core.Symbols;
using Xunit;

namespace CodeCortex.Tests;

public class SymbolResolverAdvancedTests {
    private CodeCortexIndex MakeIndex(params (string Id, string Fqn, string Kind)[] entries) {
        var idx = new CodeCortexIndex();
        foreach (var e in entries) {
            idx.Types.Add(new TypeEntry { Id = e.Id, Fqn = e.Fqn, Kind = e.Kind });
            idx.Maps.FqnIndex[e.Fqn] = e.Id;
            var simple = e.Fqn.Contains('.') ? e.Fqn[(e.Fqn.LastIndexOf('.') + 1)..] : e.Fqn;
            if (!idx.Maps.NameIndex.TryGetValue(simple, out var list)) {
                list = new List<string>();
                idx.Maps.NameIndex[simple] = list;
            }
            list.Add(e.Id);
        }
        return idx;
    }

    [Fact]
    public void Limit_Truncates_And_OrderStable() {
        // Create many suffix matches of varying namespace length so RankScore differs
        var entries = new List<(string, string, string)>();
        for (int i = 0; i < 30; i++) {
            string ns = new string('N', i % 5 + 1); // varying length
            entries.Add(($"T{i}", $"{ns}.P.Target", "Class"));
        }
        var idx = MakeIndex(entries.ToArray());
        var r = new SymbolResolver(idx);
        var res = r.Resolve("Target", limit: 5);
        Assert.Equal(5, res.Count);
        // Ensure all are suffix and sorted by RankScore then FQN
        Assert.All(res, m => Assert.Equal(MatchKind.Suffix, m.MatchKind));
        var sorted = res.OrderBy(m => m.RankScore).ThenBy(m => m.Fqn, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted.Select(x => x.Id), res.Select(x => x.Id));
    }

    [Fact]
    public void Wildcard_Disables_Fuzzy() {
        var idx = MakeIndex(("T1", "A.MyService", "Class"), ("T2", "B.MyServicx", "Class"));
        var r = new SymbolResolver(idx);
        // Need wildcard to span namespace: include leading *
        var res = r.Resolve("*MyServ*", limit: 10);
        Assert.All(res, m => Assert.NotEqual(MatchKind.Fuzzy, m.MatchKind));
        Assert.Contains(res, m => m.MatchKind == MatchKind.Wildcard);
    }

    [Fact]
    public void Fuzzy_Threshold_Boundary() {
        // name length 13 triggers threshold=2; length 12 threshold=1
        var idx = MakeIndex(("T1", "Ns.ABCDEFGHIJKLM", "Class")); // 13 chars simple name
        var r = new SymbolResolver(idx);
        // Query with distance 2 (remove two chars)
        string baseName = "ABCDEFGHIJKLM"; // 13
                                           // create distance=2 via substitutions keeping length (positions 3,7)
        char[] chars = baseName.ToCharArray();
        chars[3] = 'X';
        chars[7] = 'Y';
        string dist2 = new string(chars);
        var res13 = r.Resolve(dist2, limit: 5); // should fuzzy match (threshold 2 because query length >12)
        Assert.Contains(res13, m => m.MatchKind == MatchKind.Fuzzy && m.Distance == 2);

        // For length 12 version threshold=1; craft by removing last char from type then distance 2 variant should not match
        var idx2 = MakeIndex(("T2", "Ns.ABCDEFGHIJLM", "Class")); // removed 'K' -> length 12 simple
        var r2 = new SymbolResolver(idx2);
        string simple12 = "ABCDEFGHIJLM";
        // two substitutions (distance 2) but query length 12 -> threshold 1, so reject
        char[] c2 = simple12.ToCharArray();
        c2[2] = 'X';
        c2[5] = 'Z';
        string dist2v = new string(c2);
        var res12 = r2.Resolve(dist2v, limit: 5);
        Assert.DoesNotContain(res12, m => m.MatchKind == MatchKind.Fuzzy && m.Distance == 2);
    }

    [Fact]
    public void OnlyWildcard_Limited() {
        var entries = Enumerable.Range(0, 40).Select(i => ($"T{i}", $"Ns.Type{i}", "Class")).ToArray();
        var idx = MakeIndex(entries);
        var r = new SymbolResolver(idx);
        var res = r.Resolve("*", limit: 10);
        Assert.Equal(10, res.Count); // truncated by limit
        Assert.All(res, m => Assert.Equal(MatchKind.Wildcard, m.MatchKind));
    }

    [Fact]
    public void Ordering_TieBreak_ByFqn() {
        var idx = MakeIndex(("T1", "NsA.Type", "Class"), ("T2", "NsB.Type", "Class"));
        var r = new SymbolResolver(idx);
        var res = r.Resolve("Type", limit: 10);
        Assert.Equal(2, res.Count);
        // Same rankscore => ordered by FQN
        var expected = res.OrderBy(m => m.Fqn, StringComparer.Ordinal).Select(m => m.Id).ToList();
        Assert.Equal(expected, res.Select(m => m.Id).ToList());
    }

    [Fact]
    public void Levenshtein_EarlyExit_NoMatchWhenLengthGap() {
        var idx = MakeIndex(("T1", "Ns.Short", "Class"));
        var r = new SymbolResolver(idx);
        var res = r.Resolve("ThisIsAVeryLongQuery", limit: 5);
        Assert.DoesNotContain(res, m => m.MatchKind == MatchKind.Fuzzy);
    }

    [Fact]
    public void Ambiguous_Truncated_StillFlagged() {
        var entries = new List<(string, string, string)>();
        for (int i = 0; i < 12; i++) {
            entries.Add(($"T{i}", $"Ns{i}.Service", "Class"));
        }

        var idx = MakeIndex(entries.ToArray());
        var r = new SymbolResolver(idx);
        var res = r.Resolve("Service", limit: 5);
        Assert.Equal(5, res.Count);
        Assert.All(res, m => Assert.True(m.IsAmbiguous));
    }
}
