using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeActions;

namespace MemoTree.Tests.Analyzers.TestHelpers;

/// <summary>
/// Lightweight host to run a single analyzer + optional codefix on an in-memory C# snippet.
/// Avoids heavy Microsoft.CodeAnalysis.Testing dependency conflicts.
/// </summary>
public static class AnalyzerTestHost {
    public static (IReadOnlyList<Diagnostic> Diagnostics, Compilation Compilation) RunAnalyzer(string source, string analyzerFullTypeName) {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = GetMetadataReferences();
        var compilation = CSharpCompilation.Create("AnalyzerTest", new[] { tree }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analyzerAssembly = LoadAnalyzerAssembly();
        var analyzerType = analyzerAssembly.GetType(analyzerFullTypeName, throwOnError: true)!;
        var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType)!;
        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer));
        var diags = withAnalyzers.GetAnalyzerDiagnosticsAsync().Result;
        return (diags, compilation);
    }

    public static async Task<string> ApplyAllCodeFixesAsync(string source, string analyzerFullTypeName, CodeFixProvider codeFix, string diagnosticId, int maxIterations = 20) {
        string current = source;
        var refs = GetMetadataReferences();
        var analyzerAsm = LoadAnalyzerAssembly();
        var analyzerType = analyzerAsm.GetType(analyzerFullTypeName, throwOnError: true)!;
        var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType)!;
        for (int iter = 0; iter < maxIterations; iter++) {
            var tree = CSharpSyntaxTree.ParseText(current);
            var compilation = CSharpCompilation.Create("AnalyzerTest", new[] { tree }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer));
            var diags = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
            var targets = diags.Where(d => d.Id == diagnosticId).OrderBy(d => d.Location.SourceSpan.Start).ToArray();
            if (targets.Length == 0) {
                break;
            }

            var workspace = new AdhocWorkspace();
            var projId = ProjectId.CreateNewId();
            var docId = DocumentId.CreateNewId(projId);
            var solution = workspace.CurrentSolution
                .AddProject(projId, "TestProj", "TestProj", LanguageNames.CSharp)
                .WithProjectCompilationOptions(projId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReferences(projId, refs)
                .AddDocument(docId, "Test.cs", current);

            // Apply only first diagnostic per iteration to minimize location drift.
            var first = targets[0];
            var document = solution.GetDocument(docId)!;
            var actions = new List<CodeAction>();
            var ctx = new CodeFixContext(document, first, (a, _) => actions.Add(a), CancellationToken.None);
            await codeFix.RegisterCodeFixesAsync(ctx);
            if (actions.Count > 0) {
                var ops = await actions[0].GetOperationsAsync(CancellationToken.None);
                foreach (var op in ops) {
                    op.Apply(workspace, CancellationToken.None);
                }
            }
            current = (await workspace.CurrentSolution.GetDocument(docId)!.GetTextAsync()).ToString();
        }
        return current;
    }

    public static System.Reflection.Assembly LoadAnalyzerAssembly() {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir != null; i++) {
            var candidate = Path.Combine(dir.FullName, "src", "Atelia.Analyzers.Style", "bin");
            if (Directory.Exists(candidate)) {
                var dll = Directory.GetFiles(candidate, "Atelia.Analyzers.Style.dll", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (dll != null) {
                    return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                }
            }
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("Unable to locate Atelia.Analyzers.Style.dll for tests.");
    }

    private static List<MetadataReference> GetMetadataReferences() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
        .Select(a => MetadataReference.CreateFromFile(a.Location))
        .Cast<MetadataReference>()
        .ToList();
}
