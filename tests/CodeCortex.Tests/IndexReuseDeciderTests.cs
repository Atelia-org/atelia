using System;
using System.IO;
using System.Linq;
using System.Threading;
using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.Outline;
using CodeCortex.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeCortex.Tests;

public class IndexReuseDeciderTests {
    private (Microsoft.CodeAnalysis.Project project, AdhocWorkspace ws, string filePath) CreatePhysicalProject(string code) {
        var tmpDir = Path.Combine(Path.GetTempPath(), "ccx_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var filePath = Path.Combine(tmpDir, "File1.cs");
        File.WriteAllText(filePath, code);
        var ws = new AdhocWorkspace();
        var projId = ProjectId.CreateNewId();
        var projInfo = ProjectInfo.Create(projId, VersionStamp.Create(), "P", "P", LanguageNames.CSharp);
        ws.AddProject(projInfo);
        ws.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(projId), "File1.cs", null, SourceCodeKind.Regular, TextLoader.From(TextAndVersion.Create(SourceText.From(File.ReadAllText(filePath)), VersionStamp.Create(), filePath)), filePath: filePath));
        var project = ws.CurrentSolution.GetProject(projId)!;
        return (project, ws, filePath);
    }

    private CodeCortexIndex BuildIndex(params Microsoft.CodeAnalysis.Project[] projects) {
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var b = new IndexBuilder(new RoslynTypeEnumerator(), new TypeHasher(), new OutlineExtractor());
        var req = new IndexBuildRequest("test.sln", projects, false, new HashConfig(), new CodeCortex.Core.Outline.OutlineOptions(), new FixedClock(), new NoOpOutlineWriter());
        return b.Build(req);
    }

    [Fact]
    public void Reuse_NoChange() {
        var (p, _, _) = CreatePhysicalProject("namespace A { public class X{} }");
        var idx = BuildIndex(p);
        var ok = IndexReuseDecider.IsReusable(idx, out var changed, out var total);
        Assert.True(ok);
        Assert.Equal(0, changed);
        Assert.True(total >= 1);
    }

    [Fact]
    public void Reuse_FileModified_Fails() {
        var (p, _, path) = CreatePhysicalProject("namespace A { public class X{} }");
        var idx = BuildIndex(p);
        Thread.Sleep(20); // ensure timestamp change
        File.AppendAllText(path, " // change");
        var ok = IndexReuseDecider.IsReusable(idx, out var changed, out var total);
        Assert.False(ok);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Reuse_FileDeleted_Fails() {
        var (p, _, path) = CreatePhysicalProject("namespace A { public class X{} }");
        var idx = BuildIndex(p);
        File.Delete(path);
        var ok = IndexReuseDecider.IsReusable(idx, out var changed, out var total);
        Assert.False(ok);
        Assert.Equal(1, changed);
    }

    private sealed class FixedClock : IClock { public DateTime UtcNow => new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc); }
    private sealed class NoOpOutlineWriter : IOutlineWriter { public void EnsureDirectory() { } public void Write(string typeId, string outlineMarkdown) { } }
}
