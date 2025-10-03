using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace Atelia.CodeCortex.Tests;

/// <summary>
/// Tests for Use-After-Free (UAF) scenarios in SymbolTreeBuilder.
/// These tests verify that when removing types, nodes already freed by parent
/// removals are correctly skipped to prevent accessing freed memory slots.
/// </summary>
public class V2_SymbolTree_UAF_Tests {
    private const string AssemblyName = "TestAsm";
    private const string NamespaceName = "TestNs";

    [Fact]
    public void RemoveParentAndChild_ShouldNotAccessFreedNodes() {
        // Arrange: Create a parent type with nested children
        var outerEntry = CreateSymbol($"T:{NamespaceName}.Outer", AssemblyName);
        var innerEntry = CreateSymbol($"T:{NamespaceName}.Outer+Inner", AssemblyName);
        var leafEntry = CreateSymbol($"T:{NamespaceName}.Outer+Inner+Leaf", AssemblyName);

        var addDelta = SymbolsDeltaContract.Normalize(
            new[] { outerEntry, innerEntry, leafEntry },
            Array.Empty<TypeKey>()
        );

        var tree = (SymbolTree)SymbolTree.Empty.WithDelta(addDelta);

        // Verify all three types are present
        Assert.Equal(1, tree.Search("T:TestNs.Outer", 10, 0, SymbolKinds.All).Total);
        Assert.Equal(1, tree.Search("T:TestNs.Outer+Inner", 10, 0, SymbolKinds.All).Total);
        Assert.Equal(1, tree.Search("T:TestNs.Outer+Inner+Leaf", 10, 0, SymbolKinds.All).Total);

        // Act: Remove all three types in a single delta
        // The key scenario: when Outer is removed, Inner and Leaf are freed as descendants.
        // The removal loop must skip already-freed nodes when processing Inner and Leaf.
        var removeDelta = SymbolsDeltaContract.Normalize(
            Array.Empty<SymbolEntry>(),
            new[] {
                new TypeKey($"T:{NamespaceName}.Outer+Inner+Leaf", AssemblyName),  // longest first (contract)
                new TypeKey($"T:{NamespaceName}.Outer+Inner", AssemblyName),
                new TypeKey($"T:{NamespaceName}.Outer", AssemblyName)
            }
        );

        // Assert: Should not throw, even though Inner and Leaf are freed when Outer is removed
        var updatedTree = tree.WithDelta(removeDelta);

        // Verify all types are removed
        Assert.Equal(0, updatedTree.Search("T:TestNs.Outer", 10, 0, SymbolKinds.All).Total);
        Assert.Equal(0, updatedTree.Search("T:TestNs.Outer+Inner", 10, 0, SymbolKinds.All).Total);
        Assert.Equal(0, updatedTree.Search("T:TestNs.Outer+Inner+Leaf", 10, 0, SymbolKinds.All).Total);
    }

    [Fact]
    public void RemoveSiblings_ShouldHandleFreedNodesGracefully() {
        // Arrange: Create multiple sibling types that might share alias buckets
        var type1Entry = CreateSymbol($"T:{NamespaceName}.MyType", AssemblyName);
        var nested1Entry = CreateSymbol($"T:{NamespaceName}.MyType+Nested", AssemblyName);
        var type2Entry = CreateSymbol($"T:{NamespaceName}.MyType", "OtherAsm");  // Same name, different assembly
        var nested2Entry = CreateSymbol($"T:{NamespaceName}.MyType+Nested", "OtherAsm");

        var addDelta = SymbolsDeltaContract.Normalize(
            new[] { type1Entry, nested1Entry, type2Entry, nested2Entry },
            Array.Empty<TypeKey>()
        );

        var tree = (SymbolTree)SymbolTree.Empty.WithDelta(addDelta);

        // Verify both assemblies are present
        var searchResults = tree.Search("T:TestNs.MyType", 10, 0, SymbolKinds.All).Items.ToList();
        Assert.Equal(2, searchResults.Count);

        // Act: Remove both parent types (which will cascade to nested types)
        var removeDelta = SymbolsDeltaContract.Normalize(
            Array.Empty<SymbolEntry>(),
            new[] {
                new TypeKey($"T:{NamespaceName}.MyType+Nested", AssemblyName),
                new TypeKey($"T:{NamespaceName}.MyType+Nested", "OtherAsm"),
                new TypeKey($"T:{NamespaceName}.MyType", AssemblyName),
                new TypeKey($"T:{NamespaceName}.MyType", "OtherAsm")
            }
        );

        // Assert: Should handle correctly even if alias buckets contain multiple nodes
        var updatedTree = tree.WithDelta(removeDelta);
        Assert.Equal(0, updatedTree.Search("T:TestNs.MyType", 10, 0, SymbolKinds.All).Total);
    }

    [Fact]
    public void RemoveAlreadyRemovedNode_ShouldBeIdempotent() {
        // Arrange: Create and remove a type
        var entry = CreateSymbol($"T:{NamespaceName}.TestType", AssemblyName);
        var addDelta = SymbolsDeltaContract.Normalize(
            new[] { entry },
            Array.Empty<TypeKey>()
        );

        var tree = (SymbolTree)SymbolTree.Empty.WithDelta(addDelta);
        Assert.Equal(1, tree.Search("T:TestNs.TestType", 10, 0, SymbolKinds.All).Total);

        var removeDelta = SymbolsDeltaContract.Normalize(
            Array.Empty<SymbolEntry>(),
            new[] { new TypeKey($"T:{NamespaceName}.TestType", AssemblyName) }
        );

        // Act: Remove twice (second removal should be no-op)
        var tree1 = tree.WithDelta(removeDelta);
        var tree2 = tree1.WithDelta(removeDelta);

        // Assert: Both should succeed without error
        Assert.Equal(0, tree1.Search("T:TestNs.TestType", 10, 0, SymbolKinds.All).Total);
        Assert.Equal(0, tree2.Search("T:TestNs.TestType", 10, 0, SymbolKinds.All).Total);
    }

    [Fact]
    public void DeepNestingRemoval_ShouldHandleAllLevelsCorrectly() {
        // Arrange: Create deeply nested type hierarchy (5 levels)
        var entries = new List<SymbolEntry>();
        var removals = new List<TypeKey>();

        string[] levels = { "L1", "L2", "L3", "L4", "L5" };
        for (int i = 0; i < levels.Length; i++) {
            var typeChain = string.Join("+", levels.Take(i + 1));
            var docId = $"T:{NamespaceName}.{typeChain}";
            entries.Add(CreateSymbol(docId, AssemblyName));
            removals.Add(new TypeKey(docId, AssemblyName));
        }

        var addDelta = SymbolsDeltaContract.Normalize(entries.ToArray(), Array.Empty<TypeKey>());
        var tree = (SymbolTree)SymbolTree.Empty.WithDelta(addDelta);

        // Verify all levels exist
        foreach (var entry in entries) {
            Assert.Equal(1, tree.Search(entry.DocCommentId, 10, 0, SymbolKinds.All).Total);
        }

        // Act: Remove all levels in descending order (longest first, per contract)
        removals.Reverse();
        var removeDelta = SymbolsDeltaContract.Normalize(Array.Empty<SymbolEntry>(), removals.ToArray());
        var updatedTree = tree.WithDelta(removeDelta);

        // Assert: All should be removed without UAF errors
        foreach (var entry in entries) {
            Assert.Equal(0, updatedTree.Search(entry.DocCommentId, 10, 0, SymbolKinds.All).Total);
        }
    }

    [Fact]
    public void FreelistIntegrity_AfterComplexRemovals_ShouldRemainValid() {
        // Arrange: Create multiple types, remove some, add new ones, remove again
        var phase1Entries = new[] {
            CreateSymbol($"T:{NamespaceName}.Type1", AssemblyName),
            CreateSymbol($"T:{NamespaceName}.Type1+Nested1", AssemblyName),
            CreateSymbol($"T:{NamespaceName}.Type2", AssemblyName),
            CreateSymbol($"T:{NamespaceName}.Type2+Nested2", AssemblyName),
        };

        var tree = (SymbolTree)SymbolTree.Empty.WithDelta(
            SymbolsDeltaContract.Normalize(phase1Entries, Array.Empty<TypeKey>())
        );

        // Act: Remove Type1 tree
        tree = (SymbolTree)tree.WithDelta(
            SymbolsDeltaContract.Normalize(
                Array.Empty<SymbolEntry>(),
                new[] {
                    new TypeKey($"T:{NamespaceName}.Type1+Nested1", AssemblyName),
                    new TypeKey($"T:{NamespaceName}.Type1", AssemblyName)
            }
            )
        );

        // Add Type3 (should reuse freed nodes)
        tree = (SymbolTree)tree.WithDelta(
            SymbolsDeltaContract.Normalize(
                new[] {
                    CreateSymbol($"T:{NamespaceName}.Type3", AssemblyName),
                    CreateSymbol($"T:{NamespaceName}.Type3+Nested3", AssemblyName)
            },
                Array.Empty<TypeKey>()
            )
        );

        // Remove all remaining types
        tree = (SymbolTree)tree.WithDelta(
            SymbolsDeltaContract.Normalize(
                Array.Empty<SymbolEntry>(),
                new[] {
                    new TypeKey($"T:{NamespaceName}.Type3+Nested3", AssemblyName),
                    new TypeKey($"T:{NamespaceName}.Type3", AssemblyName),
                    new TypeKey($"T:{NamespaceName}.Type2+Nested2", AssemblyName),
                    new TypeKey($"T:{NamespaceName}.Type2", AssemblyName)
            }
            )
        );

        // Assert: Tree should be effectively empty (only namespace and root remain)
        var results = tree.Search(NamespaceName, 20, 0, SymbolKinds.All);
        var typeResults = results.Items.Where(hit => hit.Kind == SymbolKinds.Type).ToList();
        Assert.Empty(typeResults);
    }

    private static SymbolEntry CreateSymbol(string docCommentId, string assembly) {
        if (!docCommentId.StartsWith("T:", StringComparison.Ordinal)) { throw new ArgumentException("DocCommentId must start with 'T:'", nameof(docCommentId)); }

        var body = docCommentId[2..];  // Strip "T:" prefix
        var parts = body.Split('.');
        var namespacePart = parts.Length > 1 ? string.Join('.', parts.Take(parts.Length - 1)) : string.Empty;
        var typePart = parts[^1];

        var nsSegments = string.IsNullOrEmpty(namespacePart)
            ? Array.Empty<string>()
            : namespacePart.Split('.', StringSplitOptions.RemoveEmptyEntries);

        var typeSegments = typePart.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var displayName = typeSegments[^1];
        var fullDisplayName = body.Replace('+', '.');

        return new SymbolEntry(
            DocCommentId: docCommentId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            NamespaceSegments: nsSegments,
            TypeSegments: typeSegments,
            FullDisplayName: fullDisplayName,
            DisplayName: displayName
        );
    }
}
