using CodeCortex.Core.Index;
using CodeCortex.Workspace.Incremental;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeCortex.Tests;

public class ImpactAnalyzerTests {
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
        var analyzer = new ImpactAnalyzer();
        var changes = new List<ClassifiedFileChange> { new ClassifiedFileChange(file, ClassifiedKind.Modify) };
        // 关键：Project 必须包含该文件
        var ws = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var proj = ws.AddProject("P", Microsoft.CodeAnalysis.LanguageNames.CSharp);
        var doc = ws.AddDocument(proj.Id, "A.cs", SourceText.From(System.IO.File.ReadAllText(file)));
        var newSolution = ws.CurrentSolution.WithDocumentFilePath(doc.Id, file);
        ws.TryApplyChanges(newSolution);
        var r = analyzer.Analyze(idx, changes, _ => ws.CurrentSolution.GetProject(proj.Id), default);
        Assert.Contains("T1", r.AffectedTypeIds);
    }
}
