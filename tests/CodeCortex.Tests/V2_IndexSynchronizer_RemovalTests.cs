using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeCortexV2.Index;
using CodeCortexV2;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Atelia.CodeCortex.Tests;

public class V2_IndexSynchronizer_RemovalTests {
    private static async Task<(AdhocWorkspace ws, DocumentId doc1, DocumentId doc2, IndexSynchronizer sync)> CreateWorkspaceWithPartialAsync() {
        var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var projInfo = ProjectInfo.Create(pid, VersionStamp.Create(), "P", "P", LanguageNames.CSharp);
        ws.AddProject(projInfo);

        var d1Doc = ws.AddDocument(pid, "A.cs", SourceText.From("namespace N { public partial class C {} }"));
        var d2Doc = ws.AddDocument(pid, "B.cs", SourceText.From("namespace N { public partial class C {} }"));
        var d1 = d1Doc.Id;
        var d2 = d2Doc.Id;

        var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
        return (ws, d1, d2, sync);
    }

    [Fact]
    public async Task RemovingOnePartialPart_DoesNotRemoveType() {
        var (ws, d1, d2, sync) = await CreateWorkspaceWithPartialAsync();

        // Ensure initial build finished
        var page0 = sync.Current.Search("N.", 10, 0, CodeCortexV2.Abstractions.SymbolKinds.All);
        Assert.True(page0.Total >= 1);

        // Remove one document
        // Apply removal via Solution API to trigger WorkspaceChanged
        var sol1 = ws.CurrentSolution.RemoveDocument(d1);
        Assert.True(ws.TryApplyChanges(sol1));
        await Task.Delay(sync.DebounceMs + 50);

        var res = sync.Current.Search("C", 10, 0, CodeCortexV2.Abstractions.SymbolKinds.Type);
        Assert.True(res.Total >= 1);
        Assert.Contains(res.Items, i => i.Name.EndsWith("N.C", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovingAllParts_RemovesType_AndCascadeNamespace() {
        var (ws, d1, d2, sync) = await CreateWorkspaceWithPartialAsync();

        var sol2 = ws.CurrentSolution.RemoveDocument(d1);
        Assert.True(ws.TryApplyChanges(sol2));
        var sol3 = ws.CurrentSolution.RemoveDocument(d2);
        Assert.True(ws.TryApplyChanges(sol3));
        await Task.Delay(sync.DebounceMs + 50);

        var resTypeExact = sync.Current.Search("N.C", 10, 0, CodeCortexV2.Abstractions.SymbolKinds.Type);
        Assert.Equal(0, resTypeExact.Total);

        // Namespace cascade: lookup by exact doc-id
        var resNsId = sync.Current.Search("N:N", 10, 0, CodeCortexV2.Abstractions.SymbolKinds.All);
        Assert.Equal(0, resNsId.Total);
    }
}
