using System.Linq;
using System.Threading.Tasks;
using Atelia.Analyzers.Style;
using MemoTree.Tests.Analyzers.TestHelpers;

namespace MemoTree.Tests.Analyzers;

public class MT0007ClosingParenIndentTests {
    private const string Id = "MT0007";
    private static (System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> diags, Microsoft.CodeAnalysis.Compilation comp) RunAnalyzer(string src)
        => AnalyzerTestHost.RunAnalyzer(src, "Atelia.Analyzers.Style.MT0007ClosingParenIndentAnalyzer");
    private static Task<string> ApplyAllCodeFixesAsync(string src)
        => AnalyzerTestHost.ApplyAllCodeFixesAsync(src, "Atelia.Analyzers.Style.MT0007ClosingParenIndentAnalyzer", new MT0007ClosingParenIndentCodeFix(), Id);

    [Fact]
    public void SingleLine_NoDiagnostic() {
        var code = "class C { void M(int a, int b){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.DoesNotContain(d, x => x.Id == Id);
    }

    [Fact]
    public void Multiline_AndCloseParenSharesLineWithLastParameter_Skip() {
        var code = @"class C { void M(
    int a,
    int b){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.DoesNotContain(d, x => x.Id == Id);
    }

    [Fact]
    public void Multiline_ClosingParenMisAligned_MethodDeclaration() {
        var code = @"class C { void M(
    int a,
    int b
        ){} }"; // extra indent before )
        var (d, _) = RunAnalyzer(code);
        Assert.Single(d, x => x.Id == Id);
    }

    [Fact]
    public void Multiline_ClosingParenAligned_MethodDeclaration() {
        var code = @"class C { void M(
    int a,
    int b
){} }"; // aligned )
        var (d, _) = RunAnalyzer(code);
        Assert.DoesNotContain(d, x => x.Id == Id);
    }

    [Fact]
    public void Invocation_Misaligned() {
        var code = @"class C { void M(){ Foo(
    1,
    2
        ); } void Foo(int a,int b){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.Single(d, x => x.Id == Id);
    }

    [Fact]
    public async Task CodeFix_AlignsClosingParen_Method() {
        var code = @"class C { void M(
    int a,
    int b
        ){} }";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        var (afterDiags, _) = RunAnalyzer(fixedText);
        Assert.DoesNotContain(afterDiags, d => d.Id == Id);
    }

    [Fact]
    public async Task CodeFix_AlignsClosingParen_Invocation() {
        var code = @"class C { void M(){ Foo(
    1,
    2
        ); } void Foo(int a,int b){} }";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        var (afterDiags, _) = RunAnalyzer(fixedText);
        Assert.DoesNotContain(afterDiags, d => d.Id == Id);
    }
}
