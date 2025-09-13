// using System;
// using System.Linq;
// using System.Threading;
// using System.Threading.Tasks;
// using CodeCortexV2.Abstractions;
// using CodeCortexV2.Index;
// using Microsoft.CodeAnalysis;
// using Microsoft.CodeAnalysis.Text;
// using Xunit;

// namespace CodeCortex.Tests;

// public class V2_SymbolIndex_NamespaceSearchTests {
//     private static async Task<(Solution solution, Project project)> CreateSolutionAsync(string source) {
//         var adhoc = new AdhocWorkspace();
//         var projId = ProjectId.CreateNewId();
//         var projInfo = ProjectInfo.Create(
//             projId,
//             VersionStamp.Create(),
//             name: "TestProj",
//             assemblyName: "TestProj",
//             language: LanguageNames.CSharp
//         );
//         adhoc.AddProject(projInfo);
//         var docId = DocumentId.CreateNewId(projId);
//         adhoc.AddDocument(projId, "Test.cs", SourceText.From(source));
//         var sol = adhoc.CurrentSolution;
//         var proj = sol.GetProject(projId)!;
//         // Force compilation to materialize for synchronizer initial build
//         _ = await proj.GetCompilationAsync(CancellationToken.None).ConfigureAwait(false);
//         return (sol, proj);
//     }

//     private const string Sample = @"namespace Foo { namespace Bar { public class Baz { } } }";

//     [Fact]
//     public async Task Search_ByNamespaceDocId_ReturnsNamespace() {
//         var (solution, _) = await CreateSolutionAsync(Sample);
//         using var ws = solution.Workspace;
//         var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
//         var idx = sync.Current;

//         var page = idx.Search("N:Foo.Bar", limit: 10, offset: 0, kinds: SymbolKinds.Namespace);
//         Assert.Equal(1, page.Total);
//         var item = page.Items[0];
//         Assert.Equal(SymbolKinds.Namespace, item.Kind);
//         Assert.Equal("Foo.Bar", item.Name);
//         Assert.Equal("Foo", item.Namespace); // parent namespace captured
//         Assert.Null(item.Assembly); // namespace assembly is undefined/null
//     }

//     [Fact]
//     public async Task Search_ByNamespaceName_WithFilter_ReturnsOnlyNamespace() {
//         var (solution, _) = await CreateSolutionAsync(Sample);
//         using var ws = solution.Workspace;
//         var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
//         var idx = sync.Current;

//         var page = idx.Search("Foo.Bar", limit: 10, offset: 0, kinds: SymbolKinds.Namespace);
//         Assert.True(page.Total >= 1);
//         Assert.All(page.Items, it => Assert.Equal(SymbolKinds.Namespace, it.Kind));
//         Assert.Contains(page.Items, it => it.Name == "Foo.Bar");
//     }

//     [Fact]
//     public async Task Search_TypeSuffix_StillWorks_AndHasNamespace() {
//         var (solution, _) = await CreateSolutionAsync(Sample);
//         using var ws = solution.Workspace;
//         var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
//         var idx = sync.Current;

//         var page = idx.Search("Baz", limit: 10, offset: 0, kinds: SymbolKinds.All);
//         Assert.True(page.Total >= 1);
//         var item = page.Items.First(i => i.Name.EndsWith("Baz", StringComparison.Ordinal));
//         Assert.Equal(SymbolKinds.Type, item.Kind);
//         Assert.Equal("Foo.Bar", item.Namespace);
//         Assert.Equal("Foo.Bar.Baz", item.Name);
//     }

//     [Fact]
//     public async Task Search_TypeSuffix_WithNamespaceFilter_ReturnsEmpty() {
//         var (solution, _) = await CreateSolutionAsync(Sample);
//         using var ws = solution.Workspace;
//         var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
//         var idx = sync.Current;

//         var page = idx.Search("Baz", limit: 10, offset: 0, kinds: SymbolKinds.Namespace);
//         // 不再要求空集；允许在无其它命中时提供 Fuzzy 命名空间提示，但应无非Fuzzy命中
//         Assert.DoesNotContain(page.Items, it => it.MatchFlags != MatchFlags.Fuzzy);
//     }

// }

