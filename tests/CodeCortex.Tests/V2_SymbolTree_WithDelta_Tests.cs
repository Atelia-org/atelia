using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace Atelia.CodeCortex.Tests;

public class V2_SymbolTree_WithDelta_Tests {
    private static string[] BuildNamespaceSegments(string ns)
        => string.IsNullOrEmpty(ns)
            ? Array.Empty<string>()
            : ns.Split('.', StringSplitOptions.RemoveEmptyEntries);

    private static string[] BuildTypeSegments(string docId, int namespaceSegmentCount) {
        var body = docId.Substring(2);
        var all = SymbolNormalization.SplitSegmentsWithNested(body);
        if (namespaceSegmentCount == 0) { return all; }
        return all.Skip(namespaceSegmentCount).ToArray();
    }

    private static SymbolEntry Ty(string ns, string nameBase, int arity, string assembly) {
        string docId = arity > 0 ? $"T:{ns}.{nameBase}`{arity}" : $"T:{ns}.{nameBase}";
        string fqn = arity > 0
            ? $"global::{ns}.{nameBase}<T{(arity > 1 ? "," + string.Join(",", Enumerable.Range(2, arity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : $"global::{ns}.{nameBase}";
        string fqnNoGlobal = CodeCortexV2.Index.IndexStringUtil.StripGlobal(fqn);
        string simple = nameBase;
        var namespaceSegments = BuildNamespaceSegments(ns);
        return new SymbolEntry(
            DocCommentId: docId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            NamespaceSegments: namespaceSegments,
            TypeSegments: BuildTypeSegments(docId, namespaceSegments.Length),
            FullDisplayName: fqnNoGlobal,
            DisplayName: simple
        );
    }

    private static SymbolEntry TyNested(string ns, string outerBase, int outerArity, string innerBase, int innerArity, string assembly) {
        // DocId uses '+': T:Ns.Outer`n+Inner`m
        string docId = outerArity > 0
            ? $"T:{ns}.{outerBase}`{outerArity}+{innerBase}{(innerArity > 0 ? $"`{innerArity}" : string.Empty)}"
            : $"T:{ns}.{outerBase}+{innerBase}{(innerArity > 0 ? $"`{innerArity}" : string.Empty)}";

        string outerGenericParams = outerArity > 0
            ? $"<T{(outerArity > 1 ? "," + string.Join(",", Enumerable.Range(2, outerArity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : string.Empty;
        string innerGenericParams = innerArity > 0
            ? $"<T{(innerArity > 1 ? "," + string.Join(",", Enumerable.Range(2, innerArity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : string.Empty;
        string fqn = $"global::{ns}.{outerBase}{outerGenericParams}.{innerBase}{innerGenericParams}";
        string fqnNoGlobal = CodeCortexV2.Index.IndexStringUtil.StripGlobal(fqn);
        string simple = innerBase;
        var namespaceSegments = BuildNamespaceSegments(ns);
        return new SymbolEntry(
            DocCommentId: docId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            NamespaceSegments: namespaceSegments,
            TypeSegments: BuildTypeSegments(docId, namespaceSegments.Length),
            FullDisplayName: fqnNoGlobal,
            DisplayName: simple
        );
    }

    [Fact]
    public void Removal_NestedType_RemovesSubtreeAliases() {
        ISymbolIndex tree = SymbolTreeB.Empty;
        var add = SymbolsDeltaContract.Normalize(
            new[] { Ty("Ns", "Outer", 1, "Asm"), TyNested("Ns", "Outer", 1, "Inner", 0, "Asm") },
            Array.Empty<TypeKey>()
        );
        tree = tree.WithDelta(add);

        var before = tree.Search("T:Ns.Outer`1+Inner", 10, 0, SymbolKinds.All);
        Assert.Equal(1, before.Total);

        var remove = SymbolsDeltaContract.Normalize(
            Array.Empty<SymbolEntry>(),
            new TypeKey[] { new TypeKey("T:Ns.Outer`1", "Asm") }
        );
        tree = tree.WithDelta(remove);

        var afterDoc = tree.Search("T:Ns.Outer`1+Inner", 10, 0, SymbolKinds.All);
        Assert.Equal(0, afterDoc.Total);

        var afterPath = tree.Search("global::Ns.Outer<T>.Inner", 10, 0, SymbolKinds.All);
        Assert.Equal(0, afterPath.Total);
    }

    [Fact]
    public void Cascade_Namespace_Removal_WhenEmpty() {
        ISymbolIndex tree = SymbolTreeB.Empty;
        var add = SymbolsDeltaContract.Normalize(
            new[] { Ty("A.B", "C", 0, "Asm") },
            Array.Empty<TypeKey>()
        );
        tree = tree.WithDelta(add);
        Assert.Equal(1, tree.Search("A.B", 10, 0, SymbolKinds.Namespace).Total);

        var remove = SymbolsDeltaContract.Normalize(
            Array.Empty<SymbolEntry>(),
            new TypeKey[] { new TypeKey("T:A.B.C", "Asm") }
        );
        tree = tree.WithDelta(remove);

        Assert.Equal(0, tree.Search("A.B", 10, 0, SymbolKinds.Namespace).Total);
        Assert.Equal(0, tree.Search("A", 10, 0, SymbolKinds.Namespace).Total);
    }

    [Fact]
    public void CrossAssembly_DocId_Removal_Precise() {
        ISymbolIndex tree = SymbolTreeB.Empty;
        var add = SymbolsDeltaContract.Normalize(
            new[] { Ty("A", "C", 0, "Asm1"), Ty("A", "C", 0, "Asm2") },
            Array.Empty<TypeKey>()
        );
        tree = tree.WithDelta(add);
        var before = tree.Search("T:A.C", 10, 0, SymbolKinds.All);
        Assert.True(before.Total >= 2);
        Assert.True(before.Items.Select(h => h.Assembly).Where(a => a != null).Distinct().Count() >= 2);

        var remove = SymbolsDeltaContract.Normalize(
            Array.Empty<SymbolEntry>(),
            new TypeKey[] { new TypeKey("T:A.C", "Asm1") }
        );
        tree = tree.WithDelta(remove);

        var after = tree.Search("T:A.C", 10, 0, SymbolKinds.All);
        Assert.Equal(1, after.Total);
    }

    [Fact]
    public void Idempotent_Removal_RepeatDelta() {
        ISymbolIndex tree = SymbolTreeB.Empty;
        var add = SymbolsDeltaContract.Normalize(
            new[] { Ty("X", "Y", 0, "Asm") },
            Array.Empty<TypeKey>()
        );
        tree = tree.WithDelta(add);
        Assert.Equal(1, tree.Search("T:X.Y", 10, 0, SymbolKinds.All).Total);

        var remove = SymbolsDeltaContract.Normalize(
            Array.Empty<SymbolEntry>(),
            new TypeKey[] { new TypeKey("T:X.Y", "Asm") }
        );
        tree = tree.WithDelta(remove);
        Assert.Equal(0, tree.Search("T:X.Y", 10, 0, SymbolKinds.All).Total);

        // apply again
        tree = tree.WithDelta(remove);
        Assert.Equal(0, tree.Search("T:X.Y", 10, 0, SymbolKinds.All).Total);
    }
}
