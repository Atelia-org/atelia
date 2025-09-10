using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeCortex.Tests;

public class V2_SymbolIndex_NamespaceSearchTests {
    private static async Task<(Solution solution, Project project)> CreateSolutionAsync(string source) {
        var adhoc = new AdhocWorkspace();
        var projId = ProjectId.CreateNewId();
        var projInfo = ProjectInfo.Create(
            projId,
            VersionStamp.Create(),
            name: "TestProj",
            assemblyName: "TestProj",
            language: LanguageNames.CSharp
        );
        adhoc.AddProject(projInfo);
        var docId = DocumentId.CreateNewId(projId);
        adhoc.AddDocument(projId, "Test.cs", SourceText.From(source));
        var sol = adhoc.CurrentSolution;
        var proj = sol.GetProject(projId)!;
        // Force compilation to materialize for BuildAsync
        _ = await proj.GetCompilationAsync(CancellationToken.None).ConfigureAwait(false);
        return (sol, proj);
    }

    private const string Sample = @"namespace Foo { namespace Bar { public class Baz { } } }";

    [Fact]
    public async Task Search_ByNamespaceDocId_ReturnsNamespace() {
        var (solution, _) = await CreateSolutionAsync(Sample);
        var idx = await SymbolIndex.BuildAsync(solution, CancellationToken.None);

        var page = idx.SearchAsync("N:Foo.Bar", limit: 10, offset: 0, kinds: SymbolKinds.Namespace);
        Assert.Equal(1, page.Total);
        var item = page.Items[0];
        Assert.Equal(SymbolKinds.Namespace, item.Kind);
        Assert.Equal("Foo.Bar", item.Name);
        Assert.Equal("Foo", item.Namespace); // parent namespace captured
        Assert.Null(item.Assembly); // namespace assembly is undefined/null
    }

    [Fact]
    public async Task Search_ByNamespaceName_WithFilter_ReturnsOnlyNamespace() {
        var (solution, _) = await CreateSolutionAsync(Sample);
        var idx = await SymbolIndex.BuildAsync(solution, CancellationToken.None);

        var page = idx.SearchAsync("Foo.Bar", limit: 10, offset: 0, kinds: SymbolKinds.Namespace);
        Assert.True(page.Total >= 1);
        Assert.All(page.Items, it => Assert.Equal(SymbolKinds.Namespace, it.Kind));
        Assert.Contains(page.Items, it => it.Name == "Foo.Bar");
    }

    [Fact]
    public async Task Search_TypeSuffix_StillWorks_AndHasNamespace() {
        var (solution, _) = await CreateSolutionAsync(Sample);
        var idx = await SymbolIndex.BuildAsync(solution, CancellationToken.None);

        var page = idx.SearchAsync("Baz", limit: 10, offset: 0, kinds: SymbolKinds.All);
        Assert.True(page.Total >= 1);
        var item = page.Items.First(i => i.Name.EndsWith("Baz", StringComparison.Ordinal));
        Assert.Equal(SymbolKinds.Type, item.Kind);
        Assert.Equal("Foo.Bar", item.Namespace);
        Assert.Equal("Foo.Bar.Baz", item.Name);
    }

    [Fact]
    public async Task Search_TypeSuffix_WithNamespaceFilter_ReturnsEmpty() {
        var (solution, _) = await CreateSolutionAsync(Sample);
        var idx = await SymbolIndex.BuildAsync(solution, CancellationToken.None);

        var page = idx.SearchAsync("Baz", limit: 10, offset: 0, kinds: SymbolKinds.Namespace);
        Assert.Equal(0, page.Total);
    }

}

