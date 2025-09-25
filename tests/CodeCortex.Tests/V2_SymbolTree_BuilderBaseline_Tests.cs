using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortex.Tests.Util;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace CodeCortex.Tests;

public class V2_SymbolTree_BuilderBaseline_Tests {
    [Fact]
    public void EmptyTree_TypeAdds_ShouldMatch_FromEntries() {
        var entries = new[] {
            TypeEntry("Sample.One", "Foo", 0, "AsmA"),
            TypeEntry("Sample.One.Two", "Bar", 1, "AsmA"),
            NestedTypeEntry("Sample.Deep", "Outer", 1, "Inner", 0, "AsmB")
        };

        var delta = new SymbolsDelta(entries, Array.Empty<TypeKey>());
        var viaDelta = ApplyDeltas(SymbolTreeB.Empty, delta);
        var viaFull = BuildTreeFromAdds(entries);

        AssertSnapshotsEqual(viaFull, viaDelta);
    }

    [Fact]
    public void RenameSequence_ShouldMatch_FromEntries() {
        var initial = new SymbolsDelta(
            new[] { TypeEntry("Stage0", "Alpha", 0, "Asm") },
            Array.Empty<TypeKey>()
        );

        var rename = new SymbolsDelta(
            new[] { TypeEntry("Stage0", "Beta", 0, "Asm") },
            new[] { new TypeKey("T:Stage0.Alpha", "Asm") }
        );

        var finalTree = ApplyDeltas(SymbolTreeB.Empty, initial, rename);
        var expectedEntries = new[] {
            TypeEntry("Stage0", "Beta", 0, "Asm")
        };
        var viaFull = BuildTreeFromAdds(expectedEntries);

        AssertSnapshotsEqual(viaFull, finalTree);
    }

    [Fact]
    public void AliasBuckets_GenericAndCaseInsensitivity_ShouldMatch() {
        var entries = new[] {
            TypeEntry("System.Collections.Generic", "List", 1, "mscorlib"),
            NestedTypeEntry("System.Collections.Generic", "List", 1, "Enumerator", 0, "mscorlib"),
            TypeEntry("System.Collections", "ObservableCollection", 1, "SystemObjectModel")
        };

        var delta = new SymbolsDelta(entries, Array.Empty<TypeKey>());
        var viaDelta = ApplyDeltas(SymbolTreeB.Empty, delta);
        var viaFull = BuildTreeFromAdds(entries);

        AssertSnapshotsEqual(viaFull, viaDelta);

        var queryTree = (SymbolTreeB)viaDelta;
        AssertAliasSearchParity(queryTree);
    }

    private static ISymbolIndex ApplyDeltas(ISymbolIndex seed, params SymbolsDelta[] deltas) {
        var current = seed ?? throw new ArgumentNullException(nameof(seed));
        foreach (var delta in deltas) {
            current = current.WithDelta(delta ?? throw new ArgumentNullException(nameof(delta)));
        }
        return current;
    }

    private static void AssertSnapshotsEqual(SymbolTreeB expected, ISymbolIndex actualIndex) {
        var actual = Assert.IsType<SymbolTreeB>(actualIndex);
        var expectedSnap = SymbolTreeSnapshot.Capture(expected);
        var actualSnap = SymbolTreeSnapshot.Capture(actual);
        SymbolTreeSnapshotAssert.Equal(expectedSnap, actualSnap);
    }

    private static void AssertAliasSearchParity(SymbolTreeB tree) {
        var queries = new (string Query, MatchFlags Flags)[] {
            ("T:System.Collections.Generic.List`1", MatchFlags.None),
            ("System.Collections.Generic.List", MatchFlags.IgnoreGenericArity),
            ("global::system.collections.generic.list<t>", MatchFlags.IgnoreCase),
            ("System.Collections.Generic.List.Enumerator", MatchFlags.IgnoreGenericArity),
        };

        foreach (var (query, expectedFlag) in queries) {
            var results = tree.Search(query, 10, 0, SymbolKinds.All);
            Assert.True(results.Total > 0, $"Expected query '{query}' to return at least one result");
            Assert.Contains(results.Items,
                hit => (hit.MatchFlags & expectedFlag) == expectedFlag
            );
        }
    }

    private static SymbolTreeB BuildTreeFromAdds(IEnumerable<SymbolEntry> entries) {
        var typeAdds = entries?.ToArray() ?? Array.Empty<SymbolEntry>();
        var delta = new SymbolsDelta(typeAdds, Array.Empty<TypeKey>());
        return (SymbolTreeB)SymbolTreeB.Empty.WithDelta(delta);
    }

    private static SymbolEntry TypeEntry(string ns, string nameBase, int arity, string assembly) {
        string docId = arity > 0 ? $"T:{ns}.{nameBase}`{arity}" : $"T:{ns}.{nameBase}";
        string outerGenericParams = arity > 0
            ? $"<T{(arity > 1 ? "," + string.Join(",", Enumerable.Range(2, arity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : string.Empty;
        string fqn = $"global::{ns}.{nameBase}{outerGenericParams}";
        string fqnNoGlobal = CodeCortexV2.Index.IndexStringUtil.StripGlobal(fqn);
        return new SymbolEntry(
            DocCommentId: docId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            ParentNamespaceNoGlobal: ns,
            FqnNoGlobal: fqnNoGlobal,
            FqnLeaf: nameBase
        );
    }

    private static SymbolEntry NestedTypeEntry(string ns, string outerBase, int outerArity, string innerBase, int innerArity, string assembly) {
        var docId = outerArity > 0
            ? $"T:{ns}.{outerBase}`{outerArity}+{innerBase}{(innerArity > 0 ? $"`{innerArity}" : string.Empty)}"
            : $"T:{ns}.{outerBase}+{innerBase}{(innerArity > 0 ? $"`{innerArity}" : string.Empty)}";

        string outerGenerics = outerArity > 0
            ? $"<T{(outerArity > 1 ? "," + string.Join(",", Enumerable.Range(2, outerArity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : string.Empty;
        string innerGenerics = innerArity > 0
            ? $"<T{(innerArity > 1 ? "," + string.Join(",", Enumerable.Range(2, innerArity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : string.Empty;

        string fqn = $"global::{ns}.{outerBase}{outerGenerics}.{innerBase}{innerGenerics}";
        string fqnNoGlobal = CodeCortexV2.Index.IndexStringUtil.StripGlobal(fqn);

        return new SymbolEntry(
            DocCommentId: docId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            ParentNamespaceNoGlobal: ns,
            FqnNoGlobal: fqnNoGlobal,
            FqnLeaf: innerBase
        );
    }
}
