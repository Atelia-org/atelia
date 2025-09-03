using CodeCortex.Core.Index;
using CodeCortex.Workspace.Incremental;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CodeCortex.Tests;

public class ImpactAnalyzerTests {
    private sealed class FakeCompProvider : ICompilationProvider {
        private readonly Compilation _comp; public FakeCompProvider(Compilation comp) { _comp = comp; }
        public Compilation? GetCompilation(Project project, CancellationToken ct) => _comp;
    }
    private static (Compilation comp, string tmpFile) MakeCompilationWithFile(string code) {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(tmp, code);
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(tmp), path: tmp);
        var comp = CSharpCompilation.Create("t", new[] { tree }, new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        return (comp, tmp);
    }
    [Fact]
    public void Analyze_Modify_AffectsDeclaredType() {
        var idx = new CodeCortexIndex();
        var (comp, file) = MakeCompilationWithFile("namespace N { public class A {} }");
        var fqn = "N.A";
        idx.Types.Add(new TypeEntry { Id = "T1", Fqn = fqn, Kind = "Class", Files = new List<string> { file } });
        idx.Maps.FqnIndex[fqn] = "T1";
        var analyzer = new ImpactAnalyzer(new FakeCompProvider(comp));
        var changes = new List<ClassifiedFileChange> { new ClassifiedFileChange(file, ClassifiedKind.Modify) };
        // 提供一个最小的 Adhoc Project（Compilation 将由 FakeCompProvider 覆盖返回）
        var ws = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var proj = ws.AddProject("P", Microsoft.CodeAnalysis.LanguageNames.CSharp);
        var r = analyzer.Analyze(idx, changes, _ => proj, default);
        Assert.Contains("T1", r.AffectedTypeIds);
    }
}
