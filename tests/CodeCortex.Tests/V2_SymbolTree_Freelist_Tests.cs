using System;
using System.Collections.Generic;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace CodeCortex.Tests;

public class SymbolTreeFreelistTests {
    private const string AssemblyName = "FreelistAsm";
    private const string NamespaceName = "FreelistNs";

    [Fact]
    public void ApplyDelta_ShouldReuseFreedSlots_WhenTypeIsReadded() {
        var builder = SymbolTreeBuilder.CreateEmpty();
        var entry = CreateTypeEntry("T:FreelistNs.Sample", AssemblyName);
        var removalKey = new TypeKey(entry.DocCommentId, entry.Assembly);

        var addStats = builder.ApplyDelta(
            SymbolsDeltaContract.Normalize(
                new[] { entry },
                Array.Empty<TypeKey>()
            )
        );
        Assert.Equal(0, addStats.FreedNodeCount);
        Assert.Equal(0, addStats.ReusedNodeCount);
        var nodeCountAfterAdd = builder.Nodes.Count;

        var removeStats = builder.ApplyDelta(
            SymbolsDeltaContract.Normalize(
                Array.Empty<SymbolEntry>(),
                new[] { removalKey }
            )
        );
        Assert.True(removeStats.FreedNodeCount > 0, "Removing the type should free at least one node");
        var nodeCountAfterRemove = builder.Nodes.Count;

        var reAddStats = builder.ApplyDelta(
            SymbolsDeltaContract.Normalize(
                new[] { entry },
                Array.Empty<TypeKey>()
            )
        );
        Assert.True(reAddStats.ReusedNodeCount > 0, "Re-adding the type should reuse freed nodes");
        Assert.Equal(nodeCountAfterRemove, builder.Nodes.Count);
        Assert.Equal(nodeCountAfterAdd, builder.Nodes.Count);
    }

    private static SymbolEntry CreateTypeEntry(string docCommentId, string assembly) {
        if (!docCommentId.StartsWith("T:", StringComparison.Ordinal)) { throw new ArgumentException("DocCommentId must start with 'T:'", nameof(docCommentId)); }

        var body = docCommentId.Substring(2);
        var namespaceParts = body.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var namespaceSegments = namespaceParts.Length > 1
            ? namespaceParts[..^1]
            : Array.Empty<string>();
        var ns = namespaceSegments.Length > 0 ? string.Join('.', namespaceSegments) : string.Empty;
        var leaf = namespaceParts[^1];
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
            FullDisplayName: string.IsNullOrEmpty(ns) ? leaf : docCommentId.Substring(2).Replace('+', '.'),
            DisplayName: leaf
        );
    }
}
