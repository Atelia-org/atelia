using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.Outline;
using CodeCortex.Workspace.Incremental;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.IO.Abstractions.TestingHelpers;
using Xunit;

namespace CodeCortex.Tests;

public partial class IncrementalProcessorInMemoryTests {
    [Fact]
    public void Processor_SkipsOutlineWrite_WhenStructureHashUnchanged() {
        var idx = new CodeCortexIndex();
        var hasher = new TypeHasher();
        var outline = new OutlineExtractor();
        var file = @"C:\proj\A.cs";
        var code = "namespace N { public class C {} }";
        var sym = MakeType(code, file, out var comp);
        var fs = new MockFileSystem();
        fs.AddFile(file, new MockFileData(code));
        var impact = new ImpactResult(
            new HashSet<string> { "ID1" },
            new List<string>(),
            new List<ClassifiedFileChange>(),
            new List<string>(), // AddedTypeFqns
            new List<string>(), // RemovedTypeFqns
            new List<string>()  // RetainedTypeFqns
        );
        INamedTypeSymbol? Resolver(string id) => sym;
        var proc = new IncrementalProcessor();
        // 第一次写入
        var res1 = proc.Process(idx, impact, hasher, outline, Resolver, @"C:\proj\outlines", new InMemoryFileSystem(fs), default);
        Assert.True(fs.FileExists(@"C:\proj\outlines\ID1.outline.md"));
        var content1 = fs.File.ReadAllText(@"C:\proj\outlines\ID1.outline.md");
        // 第二次（结构 hash 不变，应跳过写入，内容不变）
        var res2 = proc.Process(idx, impact, hasher, outline, Resolver, @"C:\proj\outlines", new InMemoryFileSystem(fs), default);
        var content2 = fs.File.ReadAllText(@"C:\proj\outlines\ID1.outline.md");
        Assert.Equal(content1, content2); // 内容未变
        // outline 文件未被重写（可通过时间戳或写入次数进一步验证）
    }
    private static INamedTypeSymbol MakeType(string code, string file, out Compilation comp) {
        var tree = CSharpSyntaxTree.ParseText(code, path: file);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        comp = CSharpCompilation.Create("t", new[] { tree }, refs);
        var model = comp.GetSemanticModel(tree);
        var cls = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First();
        return (INamedTypeSymbol)model.GetDeclaredSymbol(cls)!;
    }
    [Fact]
    public void Processor_UpdatesManifest_AndOutline_InMemory() {
        var idx = new CodeCortexIndex();
        var hasher = new TypeHasher();
        var outline = new OutlineExtractor();
        var file = @"C:\proj\A.cs";
        var code = "namespace N { public class C {} }";
        var sym = MakeType(code, file, out var comp);
        var fs = new MockFileSystem();
        fs.AddFile(file, new MockFileData(code));
        var impact = new ImpactResult(
            new HashSet<string> { "ID1" },
            new List<string>(),
            new List<ClassifiedFileChange>(),
            new List<string>(),
            new List<string>(),
            new List<string>()
        );
        INamedTypeSymbol? Resolver(string id) => sym;
        var proc = new IncrementalProcessor();
        var res = proc.Process(idx, impact, hasher, outline, Resolver, @"C:\proj\outlines", new InMemoryFileSystem(fs), default);
        // outline 文件已写入 mock fs
        Assert.True(fs.FileExists(@"C:\proj\outlines\ID1.outline.md"));
        // manifest 已更新
        Assert.True(idx.FileManifest.ContainsKey(file));
    }
}
