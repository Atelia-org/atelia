using System.Linq;
using System.Threading.Tasks;
using Atelia.Analyzers.Style;
using Atelia.Analyzers.Style.Tests.TestHelpers;

namespace Atelia.Analyzers.Style.Tests;

public class MT0004ClosingParenNewLineTests {
    private const string Id = "MT0004";
    private static (System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> diags, Microsoft.CodeAnalysis.Compilation comp) RunAnalyzer(string src)
        => AnalyzerTestHost.RunAnalyzer(src, "Atelia.Analyzers.Style.MT0004ClosingParenNewLineAnalyzer");
    private static Task<string> ApplyAllCodeFixesAsync(string src)
        => AnalyzerTestHost.ApplyAllCodeFixesAsync(src, "Atelia.Analyzers.Style.MT0004ClosingParenNewLineAnalyzer", new MT0004ClosingParenNewLineCodeFix(), Id);

    [Fact]
    public void SingleLine_NoDiagnostic() {
        var code = "class C { void M(int a, int b){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.DoesNotContain(d, x => x.Id == Id);
    }

    [Fact]
    public void Multiline_AlreadySeparated_NoDiagnostic() {
        var code = @"class C { void M(
    int a,
    int b
){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.DoesNotContain(d, x => x.Id == Id);
    }

    [Fact]
    public void Multiline_MethodDeclaration_Diagnostic() {
        var code = @"class C { void M(
    int a,
    int b){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.Single(d, x => x.Id == Id);
    }

    [Fact]
    public void Multiline_Invocation_Diagnostic() {
        var code = @"class C { void M(int a,int b){ Foo(
    a,
    b); }
    void Foo(int a,int b){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.Single(d, x => x.Id == Id);
    }

    [Fact]
    public async Task CodeFix_MethodDeclaration() {
        var code = @"class C { void M(
    int a,
    int b){} }";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        // Expect pattern: last parameter line, newline, ')', optional whitespace, '{'
        Assert.Matches(@"intb\r?\n\)\s*\{\}", fixedText.Replace(" ", ""));
    }

    [Fact]
    public async Task CodeFix_Invocation() {
        var code = @"class C { void M(int a,int b){ Foo(
    a,
    b); }
    void Foo(int a,int b){} }";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        var normalized = fixedText.Replace("\r", "");
        Assert.Contains("b\n)", normalized); // ')' moved to new line
    }
}
