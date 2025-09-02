using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortex.Core.Index;
using CodeCortex.Core.Symbols;
using Xunit;

namespace CodeCortex.Tests;

public class SymbolResolverTests {
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
    public void Exact_MatchSingle() {
        var idx = MakeIndex(("T1", "A.B.C", "class"));
        var r = new SymbolResolver(idx);
        var res = r.Resolve("A.B.C");
        Assert.Single(res);
        Assert.Equal(MatchKind.Exact, res[0].MatchKind);
    }

    [Fact]
    public void ExactIgnoreCase() {
        var idx = MakeIndex(("T1", "A.B.C", "class"));
        var r = new SymbolResolver(idx);
        var res = r.Resolve("a.b.c");
        Assert.Single(res);
        Assert.Equal(MatchKind.ExactIgnoreCase, res[0].MatchKind);
    }

    [Fact]
    public void Suffix_Match() {
        var idx = MakeIndex(("T1", "X.Y.Service", "class"), ("T2", "Z.Q.Service", "class"));
        var r = new SymbolResolver(idx);
        var res = r.Resolve("Service");
        Assert.True(res.Count >= 2);
        Assert.All(res, m => Assert.EndsWith("Service", m.Fqn, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(res, m => m.IsAmbiguous);
    }

    [Fact]
    public void Wildcard_Match() {
        var idx = MakeIndex(("T1", "App.Core.NodeStore", "class"), ("T2", "App.Api.NodeController", "class"));
        var r = new SymbolResolver(idx);
        var res = r.Resolve("App.*.Node*");
        Assert.Equal(2, res.Count);
        Assert.Contains(res, m => m.Fqn == "App.Core.NodeStore");
        Assert.Contains(res, m => m.Fqn == "App.Api.NodeController");
    }

    [Fact]
    public void Fuzzy_EditDistanceOne() {
        var idx = MakeIndex(("T1", "Lib.Data.NodeStore", "class"));
        var r = new SymbolResolver(idx);
        var res = r.Resolve("NodeStor"); // missing 'e'
        Assert.Contains(res, m => m.MatchKind == MatchKind.Fuzzy && m.Fqn == "Lib.Data.NodeStore");
    }

    [Fact]
    public void Fuzzy_DistanceTwo_WhenLong() {
        var idx = MakeIndex(("T1", "Lib.Data.VeryLongManagerName", "class"));
        var r = new SymbolResolver(idx);
        var target = "VeryLongManagerName";
        var query = target.Replace("er", "r"); // remove 'e' before r (distance 1) keep for safety add another? simulate single distance ok
        var res = r.Resolve(query);
        Assert.Contains(res, m => m.Fqn.EndsWith("VeryLongManagerName"));
    }

    [Fact]
    public void NotFound() {
        var idx = MakeIndex(("T1", "A.B.C", "class"));
        var r = new SymbolResolver(idx);
        var res = r.Resolve("Nope");
        Assert.Empty(res);
    }
}
