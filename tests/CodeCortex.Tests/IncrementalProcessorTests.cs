using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.Outline;
using CodeCortex.Core.IO;
using CodeCortex.Workspace.Incremental;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Atelia.CodeCortex.Tests;

public class IncrementalProcessorTests {
    private static INamedTypeSymbol MakeType(string code, out Compilation comp) {
        var tree = CSharpSyntaxTree.ParseText(code);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        comp = CSharpCompilation.Create("t", new[] { tree }, refs);
        var model = comp.GetSemanticModel(tree);
        var cls = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First();
        return (INamedTypeSymbol)model.GetDeclaredSymbol(cls)!;
    }

    [Fact]
    public void Processor_WritesOutline_AndUpdatesManifest() {
        var idx = new CodeCortexIndex();
        var hasher = new TypeHasher();
        var outline = new OutlineExtractor();
        var code = "namespace N { public class C {} }";
        var sym = MakeType(code, out var comp);
        INamedTypeSymbol? Resolver(string id) => sym; // 短路解析
        var impact = new ImpactResult(
            new HashSet<string> { "ID1" },
            new List<string>(),
            new List<ClassifiedFileChange>(),
            new List<string>(), // AddedTypeFqns
            new List<string>(), // RemovedTypeFqns
            new List<string>()  // RetainedTypeFqns
        );
        var proc = new IncrementalProcessor();
        var res = proc.Process(idx, impact, hasher, outline, Resolver, Path.GetTempPath(), new DefaultFileSystem(), default);
        Assert.Equal(1, res.ChangedTypeCount);
        Assert.True(idx.Maps.FqnIndex.ContainsKey(sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "")));
    }
}
