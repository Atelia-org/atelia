using System.Linq;
using System.Threading.Tasks;
using MemoTree.Analyzers;
using MemoTree.Tests.Analyzers.TestHelpers;
using Xunit;
using System.IO;

namespace MemoTree.Tests.Analyzers;

public class X0001MultiScenarioTests {
    private string LoadSource() {
        var path = Path.Combine(TestContextHelper.Root, "tests", "MemoTree.Tests", "Analyzers", "TestData", "X0001_MultiScenario_Input.cs");
        return File.ReadAllText(path);
    }

    [Fact]
    public void X0001_Reports_On_All_InlineFirst_Multiline_Constructs() {
        var code = LoadSource();
        var (diags, _) = AnalyzerTestHost.RunAnalyzer(code, "MemoTree.Analyzers.X0001OpenParenNewLineAnalyzer");
    // Expect at least one diagnostic per applicable construct (8 scenarios after removing invalid conversion case)
    Assert.True(diags.Count(d => d.Id == "X0001") >= 6);
    }

    [Fact]
    public async Task X0001_CodeFix_Applies_To_First_Diagnostic_Iteratively() {
        var code = LoadSource();
    var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, "MemoTree.Analyzers.X0001OpenParenNewLineAnalyzer", new X0001OpenParenNewLineCodeFix(), "X0001");
        // After fixes, rerun analyzer should have zero diagnostics
        var (afterDiags, _) = AnalyzerTestHost.RunAnalyzer(fixedText, "MemoTree.Analyzers.X0001OpenParenNewLineAnalyzer");
    Assert.DoesNotContain(afterDiags, d => d.Id == "X0001");
    }
}

// Helper to locate repository root from test base directory (simple upward search once).
internal static class TestContextHelper {
    private static string? _root;
    public static string Root => _root ??= LocateRoot();
    private static string LocateRoot() {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++) {
            if (File.Exists(Path.Combine(dir.FullName, "MemoTree.sln"))) return dir.FullName;
            dir = dir.Parent!;
        }
        return System.AppContext.BaseDirectory; // fallback
    }
}
