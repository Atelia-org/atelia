using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;
using Xunit.Sdk;

namespace Atelia.CodeCortex.Tests.Util;

internal sealed class SymbolTreeSnapshot {
    private SymbolTreeSnapshot(
        IReadOnlyList<string> nodeFingerprints,
        IReadOnlyList<string> exactAliasFingerprints,
        IReadOnlyList<string> nonExactAliasFingerprints
    ) {
        NodeFingerprints = nodeFingerprints;
        ExactAliasFingerprints = exactAliasFingerprints;
        NonExactAliasFingerprints = nonExactAliasFingerprints;
    }

    public IReadOnlyList<string> NodeFingerprints { get; }
    public IReadOnlyList<string> ExactAliasFingerprints { get; }
    public IReadOnlyList<string> NonExactAliasFingerprints { get; }

    public static SymbolTreeSnapshot Capture(SymbolTree tree) {
        if (tree is null) { throw new ArgumentNullException(nameof(tree)); }

        var nodesField = typeof(SymbolTree).GetField("_nodes", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SymbolTree._nodes field not found");
        var exactField = typeof(SymbolTree).GetField("_exactAliasToNodes", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SymbolTree._exactAliasToNodes field not found");
        var nonExactField = typeof(SymbolTree).GetField("_nonExactAliasToNodes", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SymbolTree._nonExactAliasToNodes field not found");

        var nodes = (ImmutableArray<Node>)nodesField.GetValue(tree)!;
        var exact = (Dictionary<string, ImmutableArray<AliasRelation>>)exactField.GetValue(tree)!;
        var nonExact = (Dictionary<string, ImmutableArray<AliasRelation>>)nonExactField.GetValue(tree)!;

        var nodeFingerprints = BuildNodeFingerprints(nodes);
        var exactAliases = BuildAliasFingerprints("E", nodes, exact);
        var nonExactAliases = BuildAliasFingerprints("N", nodes, nonExact);

        return new SymbolTreeSnapshot(nodeFingerprints, exactAliases, nonExactAliases);
    }

    private static IReadOnlyList<string> BuildNodeFingerprints(ImmutableArray<Node> nodes) {
        var fingerprints = new List<string>(nodes.Length);
        for (int i = 0; i < nodes.Length; i++) {
            if (i == 0 && nodes[i].Parent < 0 && string.IsNullOrEmpty(nodes[i].Name)) { continue; /* Skip synthetic root */ }
            var node = nodes[i];
            if (node.Parent < 0) { continue; } // Skip tombstones detached from the tree
            var path = ComputePath(nodes, i);
            var entry = node.Entry;
            string? docId;
            string assembly;
            if (node.Kind == NodeKind.Namespace) {
                docId = entry?.DocCommentId;
                if (string.IsNullOrEmpty(docId) && !string.IsNullOrEmpty(path.NamespacePath)) {
                    docId = $"N:{path.NamespacePath}";
                }
                docId ??= string.Empty;
                assembly = string.Empty;
            }
            else {
                docId = entry?.DocCommentId;
                if (string.IsNullOrEmpty(docId) && !string.IsNullOrEmpty(path.FullName)) {
                    docId = $"T:{path.FullName}";
                }
                docId ??= string.Empty;
                assembly = entry?.Assembly ?? string.Empty;
            }

            var kind = node.Kind.ToString();
            fingerprints.Add($"{kind}:{path.FullName}|DocId={docId}|Asm={assembly}");
        }
        fingerprints.Sort(StringComparer.Ordinal);
        return fingerprints;
    }

    private static IReadOnlyList<string> BuildAliasFingerprints(
        string bucketPrefix,
        ImmutableArray<Node> nodes,
        Dictionary<string, ImmutableArray<AliasRelation>> aliases
    ) {
        var list = new List<string>();
        foreach (var kvp in aliases) {
            var alias = kvp.Key;
            foreach (var relation in kvp.Value) {
                var nodeId = relation.NodeId;
                if (nodeId < 0 || nodeId >= nodes.Length) { continue; }
                var node = nodes[nodeId];
                if (node.Parent < 0) { continue; } // skip tombstones
                var path = ComputePath(nodes, nodeId);
                var descriptor = node.Entry?.DocCommentId;
                if (string.IsNullOrEmpty(descriptor)) {
                    descriptor = node.Kind == NodeKind.Namespace
                        ? $"N:{path.NamespacePath}"
                        : $"T:{path.FullName}";
                }
                list.Add($"{bucketPrefix}:{alias}->[{relation.Kind}]::{descriptor}");
            }
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static NodePath ComputePath(ImmutableArray<Node> nodes, int nodeId) {
        var nsSegments = new Stack<string>();
        var typeSegments = new Stack<string>();
        int current = nodeId;
        while (current >= 0) {
            var node = nodes[current];
            if (node.Parent < 0) { break; }
            if (node.Kind == NodeKind.Namespace) {
                if (!string.IsNullOrEmpty(node.Name)) {
                    nsSegments.Push(node.Name);
                }
            }
            else {
                typeSegments.Push(node.Name);
            }
            current = node.Parent;
        }

        string nsPath = string.Join('.', nsSegments);
        string typePath = string.Join('+', typeSegments);
        string full = string.IsNullOrEmpty(typePath)
            ? nsPath
            : string.IsNullOrEmpty(nsPath) ? typePath : nsPath + "." + typePath;
        return new NodePath(nsPath, typePath, full);
    }

    private readonly record struct NodePath(string NamespacePath, string TypePath, string FullName);
}

internal static class SymbolTreeSnapshotAssert {
    public static void Equal(SymbolTreeSnapshot expected, SymbolTreeSnapshot actual) {
        if (expected is null) { throw new ArgumentNullException(nameof(expected)); }
        if (actual is null) { throw new ArgumentNullException(nameof(actual)); }

        AssertEqualSequence(expected.NodeFingerprints, actual.NodeFingerprints, "Nodes");
        AssertEqualSequence(expected.ExactAliasFingerprints, actual.ExactAliasFingerprints, "ExactAliases");
        AssertEqualSequence(expected.NonExactAliasFingerprints, actual.NonExactAliasFingerprints, "NonExactAliases");
    }

    private static void AssertEqualSequence(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string category) {
        if (expected.Count != actual.Count || !expected.SequenceEqual(actual, StringComparer.Ordinal)) {
            var diff = BuildDiff(expected, actual);
            throw new XunitException($"SymbolTreeSnapshot mismatch in {category}:{Environment.NewLine}{diff}");
        }
    }

    private static string BuildDiff(IReadOnlyList<string> expected, IReadOnlyList<string> actual) {
        var missing = expected.Except(actual, StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expected, StringComparer.Ordinal).ToArray();
        return $"Missing: [{string.Join(", ", missing)}]{Environment.NewLine}Extra: [{string.Join(", ", extra)}]";
    }
}
