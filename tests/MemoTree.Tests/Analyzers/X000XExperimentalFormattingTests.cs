using System.Linq;
using System.Threading.Tasks;
using MemoTree.Analyzers;
using MemoTree.Tests.Analyzers.TestHelpers;
using Xunit;

namespace MemoTree.Tests.Analyzers;

public class X000XExperimentalFormattingTests {
    private static (System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> diags, Microsoft.CodeAnalysis.Compilation comp) RunAnalyzer(string src, string analyzer)
        => AnalyzerTestHost.RunAnalyzer(src, analyzer);

    // MT0005 (former X0001) archived / disabled-by-default; no active tests retained.

    [Fact]
    public void X0002_Diagnostic_When_OpeningHasNewlineButCloseInline() {
        var code = "class C{void M(\n int a,\n int b){} }"; // ')' inline with last param -> should report
        var (d, _) = RunAnalyzer(code, "MemoTree.Analyzers.X0002ConditionalCloseParenAnalyzer");
        Assert.Contains(d, x => x.Id == "X0002");
    }

    [Fact]
    public async Task X0002_CodeFix_MovesCloseParen() {
        var code = "class C{void M(\n int a,\n int b){} }";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, "MemoTree.Analyzers.X0002ConditionalCloseParenAnalyzer", new X0002ConditionalCloseParenCodeFix(), "X0002");
        // Allow optional blank line or indentation before '{'
        Assert.Matches(@"int b\r?\n\)\r?\n\s*\{", fixedText);
    }
}
