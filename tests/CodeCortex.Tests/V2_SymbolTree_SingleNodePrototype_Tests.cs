using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using CodeCortex.Tests.Util;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace CodeCortex.Tests;

public sealed class SymbolTreeSingleNodePrototypeTests {
    private const string AssemblyName = "ProtoAsm";
    private const string NamespaceName = "ProtoNs";

    private static readonly string[] PrototypeQueries =
    [
        $"T:{NamespaceName}.Outer`1",
        $"T:{NamespaceName}.Outer`1+Inner",
        $"T:{NamespaceName}.Outer`1+Inner+Leaf`1",
        $"{NamespaceName}.Outer`1",
        "Outer`1",
        "Inner",
        "Leaf`1"
    ];

    private static readonly HashSet<string> SeedDocIds = new(
        new[] {
            $"T:{NamespaceName}.Outer`1",
            $"T:{NamespaceName}.Outer`1+Inner",
            $"T:{NamespaceName}.Outer`1+Inner+Leaf`1"
        },
        StringComparer.Ordinal
    );

    [Fact]
    public void SingleNode_LegacySnapshot_ConvergesAfterDelta() {
        const string legacyAssembly = "LegacyAsm";
        var outerDocId = $"T:{NamespaceName}.LegacyOuter";
        var innerDocId = $"T:{NamespaceName}.LegacyOuter+LegacyInner";

        var builder = SymbolTreeBuilder.CreateEmpty();
        var nsSegments = SymbolTreeBuilder.SplitNamespace(NamespaceName);
        var nsParent = builder.EnsureNamespaceChain(nsSegments);

        var outerEntry = CreateSymbol(outerDocId, legacyAssembly);
        var innerEntry = CreateSymbol(innerDocId, legacyAssembly);

        var outerNodeName = BuildNodeName(outerDocId, nsSegments.Length, segmentIndex: 0);
        var innerNodeName = BuildNodeName(innerDocId, nsSegments.Length, segmentIndex: 1);

        var legacyPlaceholder = builder.NewChild(nsParent, outerNodeName, NodeKind.Type, entry: null);
        builder.AddAliasesForNode(legacyPlaceholder);

        var legacyOuterEntryNode = builder.NewChild(nsParent, outerNodeName, NodeKind.Type, outerEntry);
        builder.AddAliasesForNode(legacyOuterEntryNode);

        var legacyInnerEntryNode = builder.NewChild(legacyPlaceholder, innerNodeName, NodeKind.Type, innerEntry);
        builder.AddAliasesForNode(legacyInnerEntryNode);

        var legacyTree = InstantiateTree(builder);

        var delta = SymbolsDeltaContract.Normalize(
            new SymbolsDelta(new[] { outerEntry, innerEntry }, Array.Empty<TypeKey>())
        );

        var updated = (SymbolTreeB)legacyTree.WithDelta(delta);

        var activeTypeNodes = updated.DebugNodes
            .Select((node, index) => (node, index))
            .Where(pair => pair.node.Kind == NodeKind.Type && pair.node.Parent >= 0)
            .ToList();

        Assert.DoesNotContain(activeTypeNodes, pair => pair.node.Entry is null);

        var outerNodes = activeTypeNodes
            .Where(pair => string.Equals(pair.node.Entry?.DocCommentId, outerDocId, StringComparison.Ordinal))
            .ToList();
        Assert.Single(outerNodes);

        var innerNodes = activeTypeNodes
            .Where(pair => string.Equals(pair.node.Entry?.DocCommentId, innerDocId, StringComparison.Ordinal))
            .ToList();
        Assert.Single(innerNodes);

        var hits = updated.Search("LegacyOuter", 8, 0, SymbolKinds.All);
        Assert.Contains(outerDocId, hits.Items.Select(hit => hit.SymbolId.Value));
        Assert.Contains(innerDocId, updated.Search("LegacyInner", 8, 0, SymbolKinds.All).Items.Select(hit => hit.SymbolId.Value));
    }

    [Fact]
    public void SingleNode_BuildFromScratch_ReturnsExpectedHits() {
        var delta = SymbolsDeltaContract.Normalize(
            new SymbolsDelta(
                SeedDocIds.Select(docId => CreateSymbol(docId, AssemblyName)).ToArray(),
                Array.Empty<TypeKey>()
            )
        );

        var tree = BuildTree(delta);

        foreach (var query in PrototypeQueries) {
            var hits = CollectSymbolIds(tree, query);
            Assert.NotEmpty(hits);
            Assert.True(hits.IsSubsetOf(SeedDocIds), $"Unexpected hits for '{query}': {string.Join(", ", hits)}");
        }
    }

    [Fact]
    public void SingleNode_IncrementalAdd_MergesWithoutDuplicates() {
        var outer = CreateSymbol($"T:{NamespaceName}.Outer`1", AssemblyName);
        var firstNested = CreateSymbol($"T:{NamespaceName}.Outer`1+First", AssemblyName);
        var baseline = BuildTree(SymbolsDeltaContract.Normalize(new SymbolsDelta(new[] { outer, firstNested }, Array.Empty<TypeKey>())));

        var secondNested = CreateSymbol($"T:{NamespaceName}.Outer`1+Second", AssemblyName);
        var updated = (SymbolTreeB)baseline.WithDelta(
            SymbolsDeltaContract.Normalize(new SymbolsDelta(new[] { secondNested }, Array.Empty<TypeKey>()))
        );

        AssertAllEntriesMaterialized(updated, out var activeTypeNodes);
        Assert.Contains(activeTypeNodes, pair => pair.Entry.DocCommentId == firstNested.DocCommentId);
        Assert.Contains(activeTypeNodes, pair => pair.Entry.DocCommentId == secondNested.DocCommentId);
        Assert.DoesNotContain(activeTypeNodes, pair => pair.Entry is null);

        Assert.Contains(firstNested.DocCommentId, CollectSymbolIds(updated, firstNested.DocCommentId));
        Assert.Contains(secondNested.DocCommentId, CollectSymbolIds(updated, secondNested.DocCommentId));
    }

    [Fact]
    public void SingleNode_ReapplySameDelta_IsIdempotent() {
        var entries = SeedDocIds.Select(docId => CreateSymbol(docId, AssemblyName)).ToArray();
        var delta = SymbolsDeltaContract.Normalize(new SymbolsDelta(entries, Array.Empty<TypeKey>()));

        var tree = BuildTree(delta);
        var replay = (SymbolTreeB)tree.WithDelta(delta);

        AssertAllEntriesMaterialized(replay, out var activeTypeNodes);

        var duplicateGroups = activeTypeNodes
            .GroupBy(pair => (pair.Entry.DocCommentId, pair.Entry.Assembly ?? string.Empty))
            .Where(group => group.Count() > 1)
            .ToList();

        Assert.Empty(duplicateGroups);
    }

    [Fact]
    public void SingleNode_Removals_CleanUpEmptyShells() {
        var outer = CreateSymbol($"T:{NamespaceName}.Outer`1", AssemblyName);
        var inner = CreateSymbol($"T:{NamespaceName}.Outer`1+Inner", AssemblyName);
        var leaf = CreateSymbol($"T:{NamespaceName}.Outer`1+Inner+Leaf`1", AssemblyName);

        var tree = BuildTree(SymbolsDeltaContract.Normalize(new SymbolsDelta(new[] { outer, inner, leaf }, Array.Empty<TypeKey>())));

        var afterLeafRemoval = (SymbolTreeB)tree.WithDelta(
            SymbolsDeltaContract.Normalize(
                new SymbolsDelta(Array.Empty<SymbolEntry>(), new[] { new TypeKey(leaf.DocCommentId, AssemblyName) })
            )
        );

        Assert.Equal(0, afterLeafRemoval.Search(leaf.DocCommentId, 8, 0, SymbolKinds.All).Total);
        Assert.DoesNotContain(afterLeafRemoval.DebugNodes, node => node.Kind == NodeKind.Type && node.Parent >= 0 && node.Entry is null && node.FirstChild < 0);

        var afterInnerRemoval = (SymbolTreeB)afterLeafRemoval.WithDelta(
            SymbolsDeltaContract.Normalize(
                new SymbolsDelta(Array.Empty<SymbolEntry>(), new[] { new TypeKey(inner.DocCommentId, AssemblyName) })
            )
        );

        Assert.Equal(0, afterInnerRemoval.Search(inner.DocCommentId, 8, 0, SymbolKinds.All).Total);
        Assert.DoesNotContain(afterInnerRemoval.DebugNodes, node => node.Kind == NodeKind.Type && node.Parent >= 0 && node.Entry is null && node.FirstChild < 0);
    }

    [Fact]
    public void SingleNode_ShouldKeepAssembliesSeparatedForSameDocId() {
        const string assemblyA = "AsmA";
        const string assemblyB = "AsmB";
        var docId = $"T:{NamespaceName}.Outer`1";

        var entries = new[] { CreateSymbol(docId, assemblyA), CreateSymbol(docId, assemblyB) };
        var tree = BuildTree(SymbolsDeltaContract.Normalize(new SymbolsDelta(entries, Array.Empty<TypeKey>())));

        var typeNodes = tree.DebugNodes
            .Select((node, index) => (node, index))
            .Where(pair => pair.node.Entry?.DocCommentId == docId)
            .Select(pair => pair.node.Entry!)
            .ToList();

        Assert.Equal(2, typeNodes.Count);
        Assert.Contains(typeNodes, entry => string.Equals(entry.Assembly, assemblyA, StringComparison.Ordinal));
        Assert.Contains(typeNodes, entry => string.Equals(entry.Assembly, assemblyB, StringComparison.Ordinal));

        var hits = tree.Search("Outer`1", 8, 0, SymbolKinds.All).Items;
        var docHits = hits.Where(hit => string.Equals(hit.SymbolId.Value, docId, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, docHits.Count);

        var removalDelta = SymbolsDeltaContract.Normalize(
            new SymbolsDelta(Array.Empty<SymbolEntry>(), new[] { new TypeKey(docId, assemblyA) })
        );

        var afterRemoval = (SymbolTreeB)tree.WithDelta(removalDelta);

        var remainingNodes = afterRemoval.DebugNodes
            .Select((node, index) => (node, index))
            .Where(pair => pair.node.Entry?.DocCommentId == docId)
            .ToList();

        Assert.Single(remainingNodes);
        Assert.Equal(assemblyB, remainingNodes[0].node.Entry!.Assembly);
        Assert.DoesNotContain(afterRemoval.DebugNodes, node => node.Kind == NodeKind.Type && node.Parent >= 0 && node.Entry is null && node.FirstChild < 0);

        var remainingHits = afterRemoval.Search("Outer`1", 8, 0, SymbolKinds.All).Items;
        var filteredHits = remainingHits.Where(hit => string.Equals(hit.SymbolId.Value, docId, StringComparison.Ordinal)).ToList();
        Assert.Single(filteredHits);
        Assert.All(filteredHits, hit => Assert.Equal(assemblyB, hit.Assembly));
    }

    [Fact]
    public void SingleNode_ExtremeNestingAndAssemblies_RemainsConsistent() {
        const string assemblyA = "AsmExtremeA";
        const string assemblyB = "AsmExtremeB";

        var docIds = new[] {
            $"T:{NamespaceName}.MegaType`2",
            $"T:{NamespaceName}.MegaType`2+Inner`1",
            $"T:{NamespaceName}.MegaType`2+Inner`1+Leaf`3",
            $"T:{NamespaceName}.MegaType`2+Inner`1+Leaf`3+Final",
            $"T:{NamespaceName}.MegaType`2+Aux`2",
            $"T:{NamespaceName}.MegaType`2+Aux`2+Deep`1"
        };

        var entries = docIds
            .SelectMany(docId => new[] { CreateSymbol(docId, assemblyA), CreateSymbol(docId, assemblyB) })
            .ToArray();

        var baseline = BuildTree(SymbolsDeltaContract.Normalize(new SymbolsDelta(entries, Array.Empty<TypeKey>())));

        AssertAllEntriesMaterialized(baseline, out var baselineEntries);

        var perDocGroups = baselineEntries
            .GroupBy(pair => pair.Entry.DocCommentId)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var docId in docIds) {
            Assert.True(perDocGroups.TryGetValue(docId, out var group), $"Missing nodes for {docId}");
            Assert.Equal(2, group.Count);
            Assert.Contains(group, pair => string.Equals(pair.Entry.Assembly, assemblyA, StringComparison.Ordinal));
            Assert.Contains(group, pair => string.Equals(pair.Entry.Assembly, assemblyB, StringComparison.Ordinal));
        }

        foreach (var docId in docIds) {
            var exactHits = baseline.Search(docId, 8, 0, SymbolKinds.All).Items
                .Where(hit => string.Equals(hit.SymbolId.Value, docId, StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, exactHits.Count);
            Assert.Contains(exactHits, hit => string.Equals(hit.Assembly, assemblyA, StringComparison.Ordinal));
            Assert.Contains(exactHits, hit => string.Equals(hit.Assembly, assemblyB, StringComparison.Ordinal));
        }

        Assert.Contains($"T:{NamespaceName}.MegaType`2", CollectSymbolIds(baseline, "megatype"));
        Assert.Contains($"T:{NamespaceName}.MegaType`2", CollectSymbolIds(baseline, "MegaType`2"));
        Assert.Contains($"T:{NamespaceName}.MegaType`2+Inner`1+Leaf`3", CollectSymbolIds(baseline, "leaf`3"));
        Assert.Contains($"T:{NamespaceName}.MegaType`2+Inner`1+Leaf`3+Final", CollectSymbolIds(baseline, "Final"));
        Assert.Contains($"T:{NamespaceName}.MegaType`2+Aux`2", CollectSymbolIds(baseline, "aux`2"));
        Assert.Contains($"T:{NamespaceName}.MegaType`2+Aux`2+Deep`1", CollectSymbolIds(baseline, "deep"));

        // Remove one assembly's view; ensure the other assembly remains intact and no placeholder nodes linger.
        var removalDelta = SymbolsDeltaContract.Normalize(
            new SymbolsDelta(Array.Empty<SymbolEntry>(), docIds.Select(docId => new TypeKey(docId, assemblyA)).ToArray())
        );
        var afterRemoval = (SymbolTreeB)baseline.WithDelta(removalDelta);

        AssertAllEntriesMaterialized(afterRemoval, out var afterRemovalEntries);

        foreach (var docId in docIds) {
            var surviving = afterRemovalEntries.Where(pair => string.Equals(pair.Entry.DocCommentId, docId, StringComparison.Ordinal)).ToList();
            Assert.Single(surviving);
            Assert.Equal(assemblyB, surviving[0].Entry.Assembly);
        }

        foreach (var docId in docIds) {
            var hits = afterRemoval.Search(docId, 8, 0, SymbolKinds.All).Items
                .Where(hit => string.Equals(hit.SymbolId.Value, docId, StringComparison.Ordinal))
                .ToList();
            Assert.Single(hits);
            Assert.Equal(assemblyB, hits[0].Assembly);
        }

        Assert.DoesNotContain(afterRemoval.DebugNodes, node => node.Kind == NodeKind.Type && node.Parent >= 0 && node.Entry is null && node.FirstChild < 0);
    }

    private static SymbolTreeB BuildTree(SymbolsDelta delta)
        => (SymbolTreeB)SymbolTreeB.Empty.WithDelta(delta);

    private static HashSet<string> CollectSymbolIds(SymbolTreeB tree, string query) {
        var results = tree.Search(query, 16, 0, SymbolKinds.All);
        return results.Items.Select(hit => hit.SymbolId.Value).ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertAllEntriesMaterialized(SymbolTreeB tree, out List<(SymbolEntry Entry, int NodeId)> entries) {
        var activeTypeNodes = tree.DebugNodes
            .Select((node, index) => (node, index))
            .Where(pair => pair.node.Kind == NodeKind.Type && pair.node.Parent >= 0)
            .ToList();

        Assert.DoesNotContain(activeTypeNodes, pair => pair.node.Entry is null);

        entries = activeTypeNodes
            .Select(pair => (pair.node.Entry!, pair.index))
            .ToList();
    }

    private static SymbolEntry CreateSymbol(string docCommentId, string assembly) {
        if (!docCommentId.StartsWith("T:", StringComparison.Ordinal)) { throw new ArgumentException("DocCommentId must start with 'T:'", nameof(docCommentId)); }

        var body = docCommentId.Substring(2);
        var nestedSegments = body.Split('+');
        var outerSegments = nestedSegments[0].Split('.');
        if (outerSegments.Length == 0) { throw new ArgumentException("DocCommentId must contain a namespace and type name", nameof(docCommentId)); }

        var ns = string.Join('.', outerSegments.Take(outerSegments.Length - 1));
        var typeNames = new List<string> { outerSegments.Last() };
        typeNames.AddRange(nestedSegments.Skip(1));

        var formatted = typeNames.Select(FormatGenericName).ToArray();
        var fqnNoGlobal = string.IsNullOrEmpty(ns)
            ? string.Join('.', formatted)
            : $"{ns}.{string.Join('.', formatted)}";

        var namespaceSegments = string.IsNullOrEmpty(ns)
            ? Array.Empty<string>()
            : ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var typeSegments = SymbolNormalization
            .SplitSegmentsWithNested(body)
            .Skip(namespaceSegments.Length)
            .ToArray();

        return new SymbolEntry(
            DocCommentId: docCommentId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            NamespaceSegments: namespaceSegments,
            TypeSegments: typeSegments,
            FullDisplayName: fqnNoGlobal,
            DisplayName: StripGenericArity(typeNames[^1])
        );
    }

    private static string FormatGenericName(string value) {
        var baseName = StripGenericArity(value, out var arity);
        if (arity == 0) { return baseName; }

        var parameters = Enumerable.Range(1, arity)
            .Select(i => i == 1 ? "T" : $"T{i}");
        return $"{baseName}<{string.Join(',', parameters)}>";
    }

    private static string StripGenericArity(string value) => StripGenericArity(value, out _);

    private static string StripGenericArity(string value, out int arity) {
        var index = value.IndexOf('`');
        if (index < 0) {
            arity = 0;
            return value;
        }

        var suffix = value[(index + 1)..];
        arity = int.TryParse(suffix, out var parsed) ? parsed : 0;
        return value[..index];
    }

    private static SymbolTreeB InstantiateTree(SymbolTreeBuilder builder) {
        var ctor = typeof(SymbolTreeB).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[] {
                typeof(ImmutableArray<NodeB>),
                typeof(Dictionary<string, ImmutableArray<AliasRelation>>),
                typeof(Dictionary<string, ImmutableArray<AliasRelation>>),
                typeof(int)
            },
            modifiers: null
        );

        Assert.NotNull(ctor);

        var nodes = builder.Nodes.ToImmutableArray();
        var exact = builder.ExactAliases.ToDictionary(pair => pair.Key, pair => pair.Value);
        var nonExact = builder.NonExactAliases.ToDictionary(pair => pair.Key, pair => pair.Value);

        return (SymbolTreeB)ctor!.Invoke(new object[] { nodes, exact, nonExact, builder.FreeHead });
    }

    private static string BuildNodeName(string docCommentId, int namespaceSegmentCount, int segmentIndex) {
        var body = docCommentId.Substring(2);
        var segments = SymbolNormalization.SplitSegmentsWithNested(body);
        var typeSegment = segments[namespaceSegmentCount + segmentIndex];
        return typeSegment;
    }
}
