using System;
using CodeCortexV2.Index.SymbolTreeInternal;
using CodeCortexV2.Abstractions;
using Xunit;

namespace CodeCortex.Tests;

public class SymbolNormalizationTests {
    [Theory]
    [InlineData("global::System.Text", "System.Text")]
    [InlineData("System.Text", "System.Text")]
    public void StripGlobalPrefix_Works(string input, string expected) {
        Assert.Equal(expected, SymbolNormalization.StripGlobalPrefix(input));
    }

    [Theory]
    [InlineData("T:Namespace.Type", SymbolKinds.Type, "Namespace.Type")]
    [InlineData("N:Ns.Inner", SymbolKinds.Namespace, "Ns.Inner")]
    [InlineData("M:System.String.Length", SymbolKinds.Method, "System.String.Length")]
    public void TryParseDocIdPrefix_Valid(string input, SymbolKinds expectKind, string remainder) {
        Assert.True(SymbolNormalization.TryParseDocIdPrefix(input, out var k, out var rem));
        Assert.Equal(expectKind, k);
        Assert.Equal(remainder, rem);
    }

    [Theory]
    [InlineData("!:ErrorType")]
    [InlineData("X:Whatever")]
    [InlineData("")]
    public void TryParseDocIdPrefix_Invalid(string input) {
        Assert.False(SymbolNormalization.TryParseDocIdPrefix(input, out var k, out var rem));
        Assert.Equal(SymbolKinds.None, k);
        Assert.Equal(input, rem);
    }

    [Theory]
    [InlineData("A.B.", "A.B", true)]
    [InlineData("A.B...", "A.B", true)]
    [InlineData("A.B", "A.B", false)]
    public void TrailingDot_Detection(string raw, string expected, bool flag) {
        var r = SymbolNormalization.HasTrailingDirectChildMarker(raw, out var trimmed);
        Assert.Equal(flag, r);
        Assert.Equal(expected, trimmed);
    }

    [Theory]
    [InlineData("Namespace.Outer+Inner", new[] { "Namespace", "Outer", "Inner" })]
    [InlineData("System..Collections", new[] { "System", "Collections" })]
    public void SplitSegmentsWithNested_Cases(string input, string[] expected) {
        Assert.Equal(expected, SymbolNormalization.SplitSegmentsWithNested(input));
    }

    [Theory]
    [InlineData("List`1", "List", 1, true)]
    [InlineData("List<int>", "List", 1, true)]
    [InlineData("Dictionary<string,int>", "Dictionary", 2, true)]
    [InlineData("Wrapper<,>", "Wrapper", 2, true)]
    [InlineData("NoGeneric", "NoGeneric", 0, false)]
    [InlineData("Pair<>", "Pair", 0, true)]
    public void ParseGenericArity_Various(string seg, string b, int a, bool exp) {
        var (bn, ar, had) = SymbolNormalization.ParseGenericArity(seg);
        Assert.Equal(b, bn);
        Assert.Equal(a, ar);
        Assert.Equal(exp, had);
    }

    [Theory]
    [InlineData("<T", false)]
    [InlineData("List<int>", true)]
    [InlineData("List<<int>", false)]
    [InlineData("NoGeneric", true)]
    public void AngleBalance(string seg, bool ok) {
        Assert.Equal(ok, SymbolNormalization.IsBalancedGenericAngles(seg));
    }

    [Theory]
    [InlineData("List", "list")]
    [InlineData("list", null)]
    [InlineData("List`1", "list`1")]
    [InlineData("list`1", null)]
    [InlineData("MyTYPE", "mytype")]
    public void ToLowerIfDifferent_Tests(string input, string? expectedNullable) {
        Assert.Equal(expectedNullable, SymbolNormalization.ToLowerIfDifferent(input));
    }

    [Theory]
    [InlineData("Foo`1", "Foo")]
    [InlineData("Foo", "Foo")]
    public void RemoveGenericAritySuffix_Works(string input, string expected) {
        Assert.Equal(expected, SymbolNormalization.RemoveGenericAritySuffix(input));
    }

    [Fact]
    public void BuildNamespaceChain_Basic() {
        var chain = SymbolNormalization.BuildNamespaceChain("A.B.C.Type");
        Assert.Equal(new[] { "A", "B", "C" }, chain);
    }

    [Fact]
    public void ExpandNestedDisplay_Works() {
        var arr = SymbolNormalization.ExpandNestedDisplay("Outer+Inner+Sub");
        Assert.Equal(new[] { "Outer", "Inner", "Sub" }, arr);
    }
    [Fact]
    public void Test_SplitSegmentsWithNested() {
        var input = "TestNs.Outer`1+Inner";
        var result = SymbolNormalization.SplitSegmentsWithNested(input);

        Console.WriteLine($"输入: '{input}'");
        Console.WriteLine($"结果长度: {result.Length}");
        for (int i = 0; i < result.Length; i++) {
            Console.WriteLine($"  [{i}] '{result[i]}'");
        }

        // 验证我们的期望
        Assert.Equal(3, result.Length);
        Assert.Equal("TestNs", result[0]);
        Assert.Equal("Outer`1", result[1]);
        Assert.Equal("Inner", result[2]);
    }
}
