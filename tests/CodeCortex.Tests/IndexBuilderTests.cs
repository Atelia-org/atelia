using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.Outline;
using CodeCortex.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;
using System.Linq;
using System.Collections.Generic;
using System.IO;
namespace CodeCortex.Tests; public class IndexBuilderTests {
    private (Project project, AdhocWorkspace ws) CreateProject(string code) {
        var ws = new AdhocWorkspace();
        var projId = ProjectId.CreateNewId();
        var projInfo = ProjectInfo.Create(projId, VersionStamp.Create(), "TestProj", "TestProj", LanguageNames.CSharp);
        ws.AddProject(projInfo);
        ws.AddDocument(projId, "File1.cs", SourceText.From(code));
        var project = ws.CurrentSolution.GetProject(projId)!;
        return (project, ws);
    }
    private IndexBuildRequest MakeRequest(params Project[] projects) => new("test.sln", projects, true, new HashConfig(), new CodeCortex.Core.Outline.OutlineOptions(), new FakeClock(), new MemoryOutlineWriter()); [Fact]
    public void Build_SingleType() {
        var code = "namespace A { public class Foo { public void M(){} } }";
        var (p, _) = CreateProject(code);
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var builder = new IndexBuilder(new RoslynTypeEnumerator(), new TypeHasher(), new OutlineExtractor());
        var req = MakeRequest(p);
        var index = builder.Build(req);
        Assert.Equal(1, index.Stats.TypeCount);
        Assert.Single(index.Types);
        Assert.Contains("A.Foo", index.Types[0].Fqn);
    }
    [Fact]
    public void DuplicateSimpleNames() {
        var code1 = "namespace N1 { public class X{} }";
        var code2 = "namespace N2 { public class X{} }";
        var (p1, _) = CreateProject(code1);
        var (p2, _) = CreateProject(code2);
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var builder = new IndexBuilder(new RoslynTypeEnumerator(), new TypeHasher(), new OutlineExtractor());
        var req = MakeRequest(p1, p2);
        var index = builder.Build(req);
        Assert.True(index.Maps.NameIndex.TryGetValue("X", out var list));
        Assert.Equal(2, list.Count);
    }
    [Fact]
    public void DeterministicOrdering() {
        var code = "namespace O { public class B{} public class A{} }";
        var (p, _) = CreateProject(code);
        CodeCortex.Core.Ids.TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());
        var b = new IndexBuilder(new RoslynTypeEnumerator(), new TypeHasher(), new OutlineExtractor());
        var req = MakeRequest(p);
        var i1 = b.Build(req);
        var i2 = b.Build(req);
        var o1 = string.Join(',', i1.Types.Select(t => t.Fqn));
        var o2 = string.Join(',', i2.Types.Select(t => t.Fqn));
        Assert.Equal(o1, o2);
    }
    private sealed class FakeClock : IClock { public DateTime UtcNow => new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc); }
    private sealed class MemoryOutlineWriter : IOutlineWriter { public Dictionary<string, string> Data { get; } = new(); public void EnsureDirectory() { } public void Write(string typeId, string outlineMarkdown) { Data[typeId] = outlineMarkdown; } }
}
