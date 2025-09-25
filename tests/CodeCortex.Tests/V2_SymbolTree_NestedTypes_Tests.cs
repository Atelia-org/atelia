using System;
using System.Collections.Generic;
using System.Linq;
using Atelia.Diagnostics;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace CodeCortex.Tests;

public class SymbolTreeNestedTypesTests {
    private const string AssemblyName = "TestAsm";
    private const string NamespaceName = "TestNs";
    private const string DebugCategory = "SymbolTreeNested";

    private static readonly string[] SingleNestedQueries =
    [
        "T:TestNs.Outer`1+Inner",
        "T:TestNs.Outer`1",
        "TestNs.Outer`1",
        "Outer`1",
        "Inner",
    ];

    private static readonly string[] AliasQueries = SingleNestedQueries
        .Concat(new[] { "TestNs" })
        .ToArray();

    [Fact]
    public void WithDelta_ShouldExposeExpectedSymbols_ForSingleNestedType() {
        var nestedEntry = CreateSymbol("T:TestNs.Outer`1+Inner", AssemblyName);
        var tree = BuildTreeWithDelta(nestedEntry);

        var expectations = new Dictionary<string, string[]>(StringComparer.Ordinal) {
            ["T:TestNs.Outer`1+Inner"] = new[] { "T:TestNs.Outer`1+Inner" },
            ["T:TestNs.Outer`1"] = new[] { "T:TestNs.Outer`1" },
            ["TestNs.Outer`1"] = new[] { "T:TestNs.Outer`1" },
            ["Outer`1"] = new[] { "T:TestNs.Outer`1" },
            ["Inner"] = new[] { "T:TestNs.Outer`1+Inner" }
        };

        foreach (var (query, expectedIds) in expectations) {
            var results = tree.Search(query, 10, 0, SymbolKinds.All);
            DebugUtil.Print(DebugCategory, $"Query '{query}' returned {results.Total} results");

            Assert.True(results.Total > 0, $"Expected query '{query}' to return at least one result");

            var actualIds = new HashSet<string>(results.Items.Select(i => i.SymbolId.Value), StringComparer.Ordinal);
            foreach (var expectedId in expectedIds) {
                Assert.Contains(expectedId, actualIds);
            }
        }
    }

    [Fact]
    public void WithDelta_ShouldCreateOuterTypeSymbolEntry() {
        var nestedEntry = CreateSymbol("T:TestNs.Outer`1+Inner", AssemblyName);

        var tree = BuildTreeWithDelta(nestedEntry);

        var results = tree.Search("T:TestNs.Outer`1", 10, 0, SymbolKinds.All);
        DebugUtil.Print(DebugCategory, $"Outer type query returned {results.Total} results");

        Assert.True(results.Total > 0, "Expected WithDelta to surface the outer type when a nested type is added");

        var hit = results.Items[0];
        Assert.Equal(SymbolKinds.Type, hit.Kind);
        Assert.Equal("T:TestNs.Outer`1", hit.SymbolId.Value);
        Assert.Equal(AssemblyName, hit.Assembly);
        Assert.Equal(NamespaceName, hit.Namespace);
    }

    [Fact]
    public void WithDelta_ShouldExposeOuterTypeDetails() {
        var nestedEntry = CreateSymbol("T:TestNs.Outer`1+Inner", AssemblyName);
        var tree = BuildTreeWithDelta(nestedEntry);

        var results = tree.Search("T:TestNs.Outer`1", 10, 0, SymbolKinds.All);
        Assert.True(results.Total > 0, "WithDelta should expose the outer type");

        var hit = results.Items[0];
        var expectedName = $"{NamespaceName}.{FormatGenericName("Outer`1")}";

        Assert.Equal(SymbolKinds.Type, hit.Kind);
        Assert.Equal(expectedName, hit.Name);
        Assert.Equal(NamespaceName, hit.Namespace);
        Assert.Equal(AssemblyName, hit.Assembly);
        Assert.Equal("T:TestNs.Outer`1", hit.SymbolId.Value);
    }

    [Fact]
    public void WithDelta_ShouldIndexAllNestedLevels() {
        var tripleNestedEntry = CreateSymbol("T:TestNs.Outer`1+Middle+Inner", AssemblyName);

        var tree = BuildTreeWithDelta(tripleNestedEntry);

        AssertSearchSucceeds(tree, "T:TestNs.Outer`1", "Outer level should be queryable");
        AssertSearchSucceeds(tree, "T:TestNs.Outer`1+Middle", "Middle level should be queryable");
        AssertSearchSucceeds(tree, "T:TestNs.Outer`1+Middle+Inner", "Innermost level should be queryable");
    }

    [Theory]
    [MemberData(nameof(GetAliasQueries))]
    public void WithDelta_ShouldSupportCommonSearchAliases(string query) {
        var nestedEntry = CreateSymbol("T:TestNs.Outer`1+Inner", AssemblyName);
        var tree = BuildTreeWithDelta(nestedEntry);

        var results = tree.Search(query, 10, 0, SymbolKinds.All);
        DebugUtil.Print(DebugCategory, $"Alias query '{query}' returned {results.Total} results");

        Assert.True(results.Total > 0, $"Expected alias '{query}' to resolve to at least one symbol");
    }

    public static IEnumerable<object[]> GetAliasQueries() {
        foreach (var query in AliasQueries) {
            yield return new object[] { query };
        }
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

        return new SymbolEntry(
            DocCommentId: docCommentId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            ParentNamespaceNoGlobal: ns,
            FqnNoGlobal: fqnNoGlobal,
            FqnLeaf: StripGenericArity(typeNames[^1])
        );
    }

    private static ISymbolIndex BuildTreeWithDelta(params SymbolEntry[] entries) {
        return SymbolTreeB.Empty.WithDelta(
            new SymbolsDelta(
                TypeAdds: entries,
                TypeRemovals: Array.Empty<TypeKey>()
            )
        );
    }

    private static void AssertSearchSucceeds(ISymbolIndex tree, string query, string failureMessage) {
        var results = tree.Search(query, 10, 0, SymbolKinds.All);
        DebugUtil.Print(DebugCategory, $"Query '{query}' returned {results.Total} results");
        Assert.True(results.Total > 0, failureMessage);
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
}
