using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeCortexV2.Index;
using CodeCortexV2.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Atelia.CodeCortex.Tests;

[CollectionDefinition("IndexSyncSerial", DisableParallelization = true)]
public class IndexSyncSerialCollectionDefinition { }

[Collection("IndexSyncSerial")]
public class V2_IndexSynchronizer_LeafOnlyTests {
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000, int intervalMs = 50) {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs) {
            try { if (condition()) { return; } } catch { /* ignore transient */ }
            await Task.Delay(intervalMs);
        }
        // one last check
        if (!condition()) { throw new TimeoutException("Condition not met within timeout"); }
    }
    private static async Task<(AdhocWorkspace ws, IndexSynchronizer sync)> CreateWorkspaceAsync() {
        Environment.SetEnvironmentVariable("ATELIA_DEBUG_CATEGORIES", "ALL");
        var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var projInfo = ProjectInfo.Create(pid, VersionStamp.Create(), name: "P", assemblyName: "P", language: LanguageNames.CSharp);
        ws.AddProject(projInfo);
        var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
        // speed up tests
        sync.DebounceMs = 20;
        return (ws, sync);
    }

    [Fact]
    public async Task NamespaceMaterialization_OnAdd() {
        var (ws, sync) = await CreateWorkspaceAsync();
        ws.AddDocument(ws.CurrentSolution.ProjectIds[0], "C.cs", SourceText.From("namespace A.B { public class C {} }"));
        await WaitUntilAsync(() => sync.Current.Search("T:A.B.C", 10, 0, SymbolKinds.Type).Total >= 1);
        await WaitUntilAsync(() => sync.Current.Search("N:A.B", 10, 0, SymbolKinds.Namespace).Total >= 1);
    }

    [Fact]
    public async Task Rename_MoveNamespace_CascadesOldNamespace() {
        var (ws, sync) = await CreateWorkspaceAsync();
        var pid = ws.CurrentSolution.ProjectIds[0];
        var doc = ws.AddDocument(pid, "C.cs", SourceText.From("namespace A.B { public class C {} }"));
        await WaitUntilAsync(() => sync.Current.Search("T:A.B.C", 10, 0, SymbolKinds.Type).Total >= 1);

        // rename by moving to another namespace
        var sol2 = ws.CurrentSolution.WithDocumentText(doc.Id, SourceText.From("namespace X.Y { public class C {} }"));
        Assert.True(ws.TryApplyChanges(sol2));
        await WaitUntilAsync(() => sync.Current.Search("T:X.Y.C", 10, 0, SymbolKinds.Type).Total >= 1);
        await WaitUntilAsync(() => sync.Current.Search("T:A.B.C", 10, 0, SymbolKinds.Type).Total == 0);
        await WaitUntilAsync(() => sync.Current.Search("N:X.Y", 10, 0, SymbolKinds.Namespace).Total >= 1);
        await WaitUntilAsync(() => sync.Current.Search("N:A.B", 10, 0, SymbolKinds.Namespace).Total == 0);
    }

    [Fact]
    public async Task MixedAddRemove_SameBatch() {
        var (ws, sync) = await CreateWorkspaceAsync();
        var pid = ws.CurrentSolution.ProjectIds[0];
        var doc1 = ws.AddDocument(pid, "A.cs", SourceText.From("namespace OldNs { public class A {} }"));
        await WaitUntilAsync(() => sync.Current.Search("T:OldNs.A", 10, 0, SymbolKinds.Type).Total >= 1);

        // Prepare batch: remove A, add B in NewNs before debounce flush
        var sol1 = ws.CurrentSolution.RemoveDocument(doc1.Id);
        sol1 = sol1.AddDocument(DocumentId.CreateNewId(pid), "B.cs", SourceText.From("namespace NewNs { public class B {} }"));
        Assert.True(ws.TryApplyChanges(sol1));
        await WaitUntilAsync(() => sync.Current.Search("T:NewNs.B", 10, 0, SymbolKinds.Type).Total >= 1);
        await WaitUntilAsync(() => sync.Current.Search("T:OldNs.A", 10, 0, SymbolKinds.Type).Total == 0);
        await WaitUntilAsync(() => sync.Current.Search("N:NewNs", 10, 0, SymbolKinds.Namespace).Total >= 1);
        await WaitUntilAsync(() => sync.Current.Search("N:OldNs", 10, 0, SymbolKinds.Namespace).Total == 0);
    }

    [Fact]
    public async Task DuplicateTypesAcrossAssemblies_AreDistinct() {
        var ws = new AdhocWorkspace();
        var pid1 = ProjectId.CreateNewId();
        var pid2 = ProjectId.CreateNewId();
        ws.AddProject(ProjectInfo.Create(pid1, VersionStamp.Create(), name: "P1", assemblyName: "P1", language: LanguageNames.CSharp));
        ws.AddProject(ProjectInfo.Create(pid2, VersionStamp.Create(), name: "P2", assemblyName: "P2", language: LanguageNames.CSharp));
        ws.AddDocument(pid1, "D1.cs", SourceText.From("namespace N { public class D {} }"));
        ws.AddDocument(pid2, "D2.cs", SourceText.From("namespace N { public class D {} }"));
        var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
        sync.DebounceMs = 50;
        await WaitUntilAsync(() => sync.Current.Search("T:N.D", 10, 0, SymbolKinds.Type).Total >= 2);
        var res = sync.Current.Search("T:N.D", 10, 0, SymbolKinds.Type);
        Assert.Contains(res.Items, i => string.Equals(i.Assembly, "P1", StringComparison.Ordinal));
        Assert.Contains(res.Items, i => string.Equals(i.Assembly, "P2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Idempotent_NoChange_NoEffect() {
        var (ws, sync) = await CreateWorkspaceAsync();
        var pid = ws.CurrentSolution.ProjectIds[0];
        var doc = ws.AddDocument(pid, "C.cs", SourceText.From("namespace A { public class C {} }"));
        await WaitUntilAsync(() => sync.Current.Search("T:A.C", 10, 0, SymbolKinds.Type).Total >= 1);

        // Apply a no-op change (same text)
        var sol2 = ws.CurrentSolution.WithDocumentText(doc.Id, SourceText.From("namespace A { public class C {} }"));
        Assert.True(ws.TryApplyChanges(sol2));
        // Wait until stable: still present with same count
        await WaitUntilAsync(() => sync.Current.Search("T:A.C", 10, 0, SymbolKinds.Type).Total >= 1);
    }
}
