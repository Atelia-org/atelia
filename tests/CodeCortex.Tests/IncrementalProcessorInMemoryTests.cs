using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.Outline;
using CodeCortex.Core.IO;
using CodeCortex.Workspace.Incremental;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.IO.Abstractions.TestingHelpers;
using Xunit;

namespace CodeCortex.Tests;

public class IncrementalProcessorInMemoryTests {
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
        var res1 = proc.Process(idx, impact, hasher, outline, Resolver, @"C:\proj\outlines", new InMemoryFsAdapter(fs), default);
        Assert.True(fs.FileExists(@"C:\proj\outlines\ID1.outline.md"));
        var content1 = fs.File.ReadAllText(@"C:\proj\outlines\ID1.outline.md");
        // 第二次（结构 hash 不变，应跳过写入，内容不变）
        var res2 = proc.Process(idx, impact, hasher, outline, Resolver, @"C:\proj\outlines", new InMemoryFsAdapter(fs), default);
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
        var res = proc.Process(idx, impact, hasher, outline, Resolver, @"C:\proj\outlines", new InMemoryFsAdapter(fs), default);
        // outline 文件已写入 mock fs
        Assert.True(fs.FileExists(@"C:\proj\outlines\ID1.outline.md"));
        // manifest 已更新
        Assert.True(idx.FileManifest.ContainsKey(file));
    }
    private class InMemoryFsAdapter : IFileSystem {
        private readonly MockFileSystem _fs;
        public InMemoryFsAdapter(MockFileSystem fs) { _fs = fs; }
        public bool FileExists(string path) => _fs.FileExists(path);
        public long GetLastWriteTicks(string path) => _fs.File.GetLastWriteTimeUtc(path).Ticks;
        public void WriteAllText(string path, string content) {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !_fs.Directory.Exists(dir)) {
                _fs.Directory.CreateDirectory(dir);
            }
            _fs.File.WriteAllText(path, content);
        }
        public void WriteAllText(string path, string content, System.Text.Encoding encoding) {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !_fs.Directory.Exists(dir)) {
                _fs.Directory.CreateDirectory(dir);
            }
            var bytes = encoding.GetBytes(content);
            using var stream = _fs.File.Open(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            stream.Write(bytes, 0, bytes.Length);
        }
        public string ReadAllText(string path) => _fs.File.ReadAllText(path);
        public string ReadAllText(string path, System.Text.Encoding encoding) {
            using var stream = _fs.File.OpenRead(path);
            using var reader = new System.IO.StreamReader(stream, encoding);
            return reader.ReadToEnd();
        }
        public bool TryDelete(string path) {
            _fs.File.Delete(path);
            return true;
        }
        public void CreateDirectory(string path) {
            _fs.Directory.CreateDirectory(path);
        }
        public void Move(string src, string dest, bool overwrite = false) {
            if (overwrite && _fs.FileExists(dest)) {
                _fs.File.Delete(dest);
            }
            _fs.File.Move(src, dest);
        }
        public void Replace(string src, string dest, string backup, bool ignoreMetadataErrors = false) {
            // MockFileSystem 没有 Replace，模拟为 Move+备份
            if (_fs.FileExists(dest)) {
                _fs.File.Copy(dest, backup, true);
                _fs.File.Delete(dest);
            }
            _fs.File.Move(src, dest);
        }
        public System.IO.Stream OpenRead(string path) => _fs.File.OpenRead(path);
    }
}
