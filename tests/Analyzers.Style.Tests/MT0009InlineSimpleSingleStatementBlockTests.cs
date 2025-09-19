using System.Threading.Tasks;
using Atelia.Analyzers.Style.Tests.TestHelpers;
using Xunit;

namespace Atelia.Analyzers.Style.Tests;

/// <summary>
/// Tests for MT0009 – InlineSimpleSingleStatementBlock.
///
/// Rule intent (v1 conservative): For if/else bodies that are blocks with exactly one simple, single-line statement
/// (return/break/continue/throw or ++/-- expression), compact the body to a single inline block by removing inner newlines
/// and converting any trailing EOL comments to block comments placed immediately before the closing brace.
/// Blocks containing standalone comment lines or preprocessor directives must not be compacted.
/// </summary>
public class MT0009InlineSimpleSingleStatementBlockTests {
    private const string AnalyzerType = "Atelia.Analyzers.Style.MT0009InlineSimpleSingleStatementBlockAnalyzer";

    /// <summary>
    /// Utility: Apply MT0009 code fix to the provided code until diagnostics are exhausted.
    /// Uses the in-repo lightweight test host to avoid external testing framework dependencies.
    /// </summary>
    private static Task<string> ApplyFixAsync(string code) => AnalyzerTestHost.ApplyAllCodeFixesAsync(
        code,
        AnalyzerType,
        new Atelia.Analyzers.Style.CodeFixes.MT0009InlineSimpleSingleStatementBlockCodeFix(),
        "MT0009");

    /// <summary>
    /// If-body with a single return statement should be compacted into an inline block.
    /// Also verifies: the end-of-line comment is converted to a sanitized block comment and placed before '}'.
    /// </summary>
    [Fact]
    public async Task If_Return_Block_Compacted() {
        var code = "class C{void M(bool c){ if(c)\n{\n    return; // note\n}\n} }";
        var fixedText = await ApplyFixAsync(code);
        Assert.Contains("if(c) { return; /* note */ }", fixedText);
        // Ensure no newline remains inside the block interior
        Assert.DoesNotContain("{\n", fixedText);
        Assert.DoesNotContain("\n}", fixedText);
    }

    /// <summary>
    /// Else-body with a single continue statement should be compacted into an inline block.
    /// This preserves the configured newline-before-else behavior while reducing the inner low-information lines.
    /// </summary>
    [Fact]
    public async Task Else_Continue_Block_Compacted() {
        var code = "class C{void M(bool r){ if(!r){ return; } else\n{\n    continue;\n}\n} }";
        var fixedText = await ApplyFixAsync(code);
        // Tolerate minor whitespace: ensure 'else' followed by '{', contains 'continue;' before a matching '}'
        var idxElse = fixedText.IndexOf("else");
        Assert.True(idxElse >= 0, "else should be present");
        var idxOpen = fixedText.IndexOf('{', idxElse);
        Assert.True(idxOpen > idxElse, "'{' should follow else");
        var idxCont = fixedText.IndexOf("continue;", idxOpen);
        Assert.True(idxCont > idxOpen, "continue; should be inside braces");
        var idxClose = fixedText.IndexOf('}', idxCont);
        Assert.True(idxClose > idxCont, "'}' should follow continue;");
        // No newline inside else block
        var inside = fixedText.Substring(idxOpen, idxClose - idxOpen);
        Assert.DoesNotContain("\n", inside);
    }

    /// <summary>
    /// Blocks that contain a standalone comment line must NOT be compacted.
    /// The analyzer should not report a diagnostic for such blocks, preserving author-intended vertical structure.
    /// </summary>
    [Fact]
    public void Standalone_Comment_Inside_Block_Not_Compacted() {
        var code = "class C{void M(bool c){ if(c)\n{\n    // lead\n    return;\n}\n} }";
        var (diags, _) = AnalyzerTestHost.RunAnalyzer(code, AnalyzerType);
        Assert.Empty(diags); // not eligible
    }

    /// <summary>
    /// Single break statement is eligible and should be compacted.
    /// </summary>
    [Fact]
    public async Task If_Break_Block_Compacted() {
        var code = "class C{void M(bool c){ if(c)\n{\n    break;\n}\n} }";
        var fixedText = await ApplyFixAsync(code);
        Assert.Contains("if(c) { break; }", fixedText);
    }

    /// <summary>
    /// Single-line increment/decrement expression is eligible and compacted.
    /// EOL comment is converted and placed before '}'.
    /// </summary>
    [Fact]
    public async Task If_Increment_Block_Compacted_When_SingleLine() {
        var code = "class C{int i; void M(bool c){ if(c)\n{\n    i++; // inc\n}\n} }";
        var fixedText = await ApplyFixAsync(code);
        Assert.Contains("if(c) { i++; /* inc */ }", fixedText);
    }

    /// <summary>
    /// Multi-line statements (e.g., invocation with arguments split across lines) are not eligible and must not be compacted.
    /// Analyzer should emit no diagnostic in such cases.
    /// </summary>
    [Fact]
    public void Multiline_Statement_Not_Compacted() {
        var code = "class C{void M(bool c){ if(c)\n{\n    Foo(1,\n        2);\n}\n} void Foo(int a,int b){} }";
        var (diags, _) = AnalyzerTestHost.RunAnalyzer(code, AnalyzerType);
        Assert.Empty(diags);
    }
}
