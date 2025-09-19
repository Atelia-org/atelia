using System;
using System.Linq;
using CodeCortex.Core.Index;
using CodeCortex.Core.Symbols;
using Xunit;

namespace CodeCortex.Tests;

public class SymbolResolver_GenericBaseTests {
    private CodeCortexIndex MakeIndex(params (string Id, string Fqn, string Kind)[] entries) {
        var idx = new CodeCortexIndex();
        foreach (var e in entries) {
            idx.Types.Add(new TypeEntry { Id = e.Id, Fqn = e.Fqn, Kind = e.Kind });
            idx.Maps.FqnIndex[e.Fqn] = e.Id;
            var simple = e.Fqn.Contains('.') ? e.Fqn[(e.Fqn.LastIndexOf('.') + 1)..] : e.Fqn;
            if (!idx.Maps.NameIndex.TryGetValue(simple, out var list)) {
                list = new System.Collections.Generic.List<string>();
                idx.Maps.NameIndex[simple] = list;
            }
            list.Add(e.Id);

            // populate GenericBaseNameIndex when type appears as generic metadata name or explicit arity comment
            // For tests we simulate generic types by FQN ending with "`1" or containing '<'
            var baseName = simple;
            if (baseName.Contains('`')) {
                baseName = baseName.Substring(0, baseName.IndexOf('`'));
            }

            if (baseName.Contains('<')) {
                baseName = baseName.Substring(0, baseName.IndexOf('<'));
            }

            if (simple.Contains('`') || simple.Contains('<')) {
                if (!idx.Maps.GenericBaseNameIndex.TryGetValue(baseName, out var gl)) {
                    gl = new System.Collections.Generic.List<string>();
                    idx.Maps.GenericBaseNameIndex[baseName] = gl;
                }
                gl.Add(e.Id);
            }
        }
        return idx;
    }

    [Fact]
    public void GenericBase_Beats_Fuzzy_For_List() {
        var idx = MakeIndex(
            ("G1", "System.Collections.Generic.List`1", "class"),
            ("N1", "Lib.Text.LispEngine", "class")
        );
        var r = new SymbolResolver(idx);
        var res = r.Resolve("List", limit: 10);
        Assert.Contains(res, m => m.MatchKind == MatchKind.GenericBase && m.Id == "G1");
        var first = res.First();
        Assert.Equal(MatchKind.GenericBase, first.MatchKind);
        Assert.Equal("G1", first.Id);
    }

    [Theory]
    [InlineData("List<int>")]
    [InlineData("List<")]
    [InlineData("System.Collections.Generic.List<int>")]
    [InlineData("System.Collections.Generic.List`1")]
    public void GenericBase_Normalizes_Various_Inputs(string query) {
        var idx = MakeIndex(
            ("G1", "System.Collections.Generic.List`1", "class"),
            ("G2", "MyNs.List`1", "class")
        );
        var r = new SymbolResolver(idx);
        var res = r.Resolve(query, limit: 10);
        Assert.Contains(res, m => m.MatchKind == MatchKind.GenericBase && (m.Id == "G1" || m.Id == "G2"));
    }

    [Fact]
    public void GenericBase_MultipleNamespaces_StableOrdering() {
        var idx = MakeIndex(
            ("A", "Lib.A.List`1", "class"),
            ("B", "Lib.B.List`1", "class"),
            ("C", "Lib.C.List`1", "class")
        );
        var r = new SymbolResolver(idx);
        var res = r.Resolve("List", limit: 10);
        // first by MatchKind, then RankScore (same), then FQN ordinal
        Assert.Equal(new[] { "A", "B", "C" }, res.Where(m => m.MatchKind == MatchKind.GenericBase).Select(m => m.Id).ToArray());
    }
}

