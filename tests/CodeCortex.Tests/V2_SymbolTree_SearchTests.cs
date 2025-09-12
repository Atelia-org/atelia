using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace CodeCortex.Tests;

public class V2_SymbolTree_SearchTests {
    private static SymbolEntry Ns(string name, string? parent = null) {
        var fqn = "global::" + name;
        var fqnBase = IndexStringUtil.NormalizeFqnBase(fqn);
        var simple = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        return new SymbolEntry(
            SymbolId: "N:" + name,
            Fqn: fqn,
            FqnNoGlobal: name,
            FqnBase: fqnBase,
            Simple: simple,
            Kind: SymbolKinds.Namespace,
            Assembly: string.Empty,
            GenericBase: string.Empty,
            ParentNamespace: parent ?? (name.Contains('.') ? name[..name.LastIndexOf('.')] : string.Empty)
        );
    }

    private static SymbolEntry Ty(string ns, string nameBase, int arity, string assembly) {
        // Build values similar to Roslyn projection
        string docId = arity > 0
            ? $"T:{ns}.{nameBase}`{arity}"
            : $"T:{ns}.{nameBase}";
        string fqn = arity > 0
            ? $"global::{ns}.{nameBase}<T{(arity > 1 ? "," + string.Join(",", Enumerable.Range(2, arity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : $"global::{ns}.{nameBase}";
        string fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
        string fqnBase = IndexStringUtil.NormalizeFqnBase(fqn);
        string simple = nameBase; // Roslyn's INamedTypeSymbol.Name returns base without `n
        string parentNs = ns;
        return new SymbolEntry(
            SymbolId: docId,
            Fqn: fqn,
            FqnNoGlobal: fqnNoGlobal,
            FqnBase: fqnBase,
            Simple: simple,
            Kind: SymbolKinds.Type,
            Assembly: assembly,
            GenericBase: IndexStringUtil.ExtractGenericBase(simple),
            ParentNamespace: parentNs
        );
    }

    private static SymbolTree BuildSample() {
        var entries = new List<SymbolEntry>
        {
            Ns("System"),
            Ns("System.Collections", "System"),
            Ns("System.Collections.Generic", "System.Collections"),
            Ty("System.Collections.Generic", "List", 1, "System.Collections")
        };
        return SymbolTree.FromEntries(entries);
    }

    [Fact]
    public void DocId_FastPath_Type() {
        var tree = BuildSample();
        var page = tree.Search("T:System.Collections.Generic.List`1", 10, 0, SymbolKinds.All);
        Assert.Equal(1, page.Total);
        var hit = Assert.Single(page.Items);
        Assert.Equal(SymbolKinds.Type, hit.Kind);
        Assert.Equal(MatchKind.Id, hit.MatchKind);
        Assert.Equal("System.Collections.Generic", hit.Namespace);
        Assert.Equal("System.Collections", hit.Assembly);
    }

    [Fact]
    public void Exact_WithGlobalPrefix_And_GenericPseudo() {
        var tree = BuildSample();
        var page = tree.Search("global::System.Collections.Generic.List<T>", 10, 0, SymbolKinds.All);
        Assert.True(page.Total >= 1);
        Assert.Contains(page.Items, h => h.Kind == SymbolKinds.Type && h.MatchKind is MatchKind.Exact or MatchKind.ExactIgnoreCase);
    }

    [Fact]
    public void Prefix_ByNamespace_Collects_Subtree() {
        var tree = BuildSample();
        var page = tree.Search("System.Collections", 100, 0, SymbolKinds.All);
        Assert.True(page.Total >= 2); // namespace + child type(s)
        Assert.Contains(page.Items, h => h.Kind == SymbolKinds.Namespace && h.Name == "System.Collections");
        Assert.Contains(page.Items, h => h.Kind == SymbolKinds.Type && h.Name.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal));
    }
}

