using System.Linq;
using System.Threading.Tasks;
using Atelia.Analyzers.Style;
using MemoTree.Tests.Analyzers.TestHelpers;

namespace MemoTree.Tests.Analyzers;

public class MT0006FirstMultilineArgumentNewLineTests {
    private const string Id = "MT0006";
    private static (System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> diags, Microsoft.CodeAnalysis.Compilation comp) RunAnalyzer(string src)
        => AnalyzerTestHost.RunAnalyzer(src, "Atelia.Analyzers.Style.MT0006FirstMultilineArgumentNewLineAnalyzer");
    private static Task<string> ApplyAllCodeFixesAsync(string src)
        => AnalyzerTestHost.ApplyAllCodeFixesAsync(src, "Atelia.Analyzers.Style.MT0006FirstMultilineArgumentNewLineAnalyzer", new MT0006FirstMultilineArgumentNewLineCodeFix(), Id);

    [Fact]
    public void NoMultiline_NoDiagnostic() {
        var code = "class C { void M(){ Foo(1, 2, 3); } void Foo(int a,int b,int c){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.DoesNotContain(d, x => x.Id == Id);
    }

    [Fact]
    public void FirstMultiline_Inlined_Diagnostic() {
        var code = @"class C { void M(){ Foo(1, x => {
    x++;
}, 3); } void Foo(int a,System.Action<int> b,int c){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.Single(d, x => x.Id == Id);
    }

    [Fact]
    public void FirstMultiline_AlreadyOnNewLine_NoDiagnostic() {
        var code = @"class C { void M(){ Foo(
    1, x => {
        x++;
    }, 3); } void Foo(int a,System.Action<int> b,int c){} }";
        var (d, _) = RunAnalyzer(code);
        Assert.DoesNotContain(d, x => x.Id == Id);
    }

    [Fact]
    public void MultipleMultiline_OnlyFirstChecked() {
        var code = @"class C { void M(){ Foo(1, x => {
    x++;
}, y => {
    y++;
}, 9); } void Foo(int a,System.Action<int> b,System.Action<int> c,int d){} }";
        var (d, _) = RunAnalyzer(code);
        // Only one diagnostic for the first multiline (x => ...)
        Assert.Single(d, x => x.Id == Id);
    }

    [Fact]
    public async Task CodeFix_InlinedFirstMultiline_Fixes() {
        var code = @"class C { void M(){ Foo(1, x => {
    x++;
}, 3); } void Foo(int a,System.Action<int> b,int c){} }";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        // Expect newline inserted before 'x =>' while keeping leading simple argument '1,' on original line.
        Assert.Contains("Foo(1,\n    x => {", fixedText.Replace("\r", ""));
    }

    [Fact]
    public async Task CodeFix_Idempotent() {
        var code = @"class C { void M(){ Foo(
    1, x => {
        x++;
    }, 3); } void Foo(int a,System.Action<int> b,int c){} }";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        // Already compliant -> unchanged
        Assert.Equal(code, fixedText);
    }
}
