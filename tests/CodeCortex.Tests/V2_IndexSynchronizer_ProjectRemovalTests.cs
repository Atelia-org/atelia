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

[Collection("IndexSyncSerial")] // 与其它 IndexSynchronizer 测试串行，避免干扰
public class V2_IndexSynchronizer_ProjectRemovalTests {
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 20000, int intervalMs = 50) {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs) {
            try { if (condition()) { return; } } catch { }
            await Task.Delay(intervalMs);
        }
        if (!condition()) { throw new TimeoutException("Condition not met within timeout"); }
    }

    private static async Task<(AdhocWorkspace ws, ProjectId pid, IndexSynchronizer sync)> CreateWorkspaceAsync() {
        var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var projInfo = ProjectInfo.Create(pid, VersionStamp.Create(), name: "P", assemblyName: "P", language: LanguageNames.CSharp);
        ws.AddProject(projInfo);
        var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
        sync.DebounceMs = 20;
        return (ws, pid, sync);
    }

    [Fact]
    public async Task RemoveProject_RemovesAllTypesAndNamespaces() {
        var (ws, pid, sync) = await CreateWorkspaceAsync();
        ws.AddDocument(pid, "A.cs", SourceText.From("namespace N1.N2 { public class A {} }"));
        await WaitUntilAsync(() => sync.Current.Search("T:N1.N2.A", 10, 0, SymbolKinds.Type).Total >= 1);
        await WaitUntilAsync(() => sync.Current.Search("N:N1.N2", 10, 0, SymbolKinds.Namespace).Total >= 1);

        // 移除整个项目
        var sol2 = ws.CurrentSolution.RemoveProject(pid);
        Assert.True(ws.TryApplyChanges(sol2));
        await WaitUntilAsync(() => sync.Current.Search("T:N1.N2.A", 10, 0, SymbolKinds.Type).Total == 0);
        await WaitUntilAsync(() => sync.Current.Search("N:N1.N2", 10, 0, SymbolKinds.Namespace).Total == 0);
    }

    // [Fact]
    // public async Task MultiProject_RemoveOne_KeepsOther() {
    //     var ws = new AdhocWorkspace();
    //     var pid1 = ProjectId.CreateNewId();
    //     var pid2 = ProjectId.CreateNewId();
    //     ws.AddProject(ProjectInfo.Create(pid1, VersionStamp.Create(), name: "P1", assemblyName: "P1", language: LanguageNames.CSharp));
    //     ws.AddProject(ProjectInfo.Create(pid2, VersionStamp.Create(), name: "P2", assemblyName: "P2", language: LanguageNames.CSharp));
    //     ws.AddDocument(pid1, "D1.cs", SourceText.From("namespace N { public class D {} }"));
    //     ws.AddDocument(pid2, "D2.cs", SourceText.From("namespace N { public class D {} }"));
    //     var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
    //     sync.DebounceMs = 20;
    //     await WaitUntilAsync(() => sync.Current.Search("T:N.D", 10, 0, SymbolKinds.Type).Total >= 2);

    //     // 移除 P1
    //     var sol2 = ws.CurrentSolution.RemoveProject(pid1);
    //     Assert.True(ws.TryApplyChanges(sol2));
    //     await WaitUntilAsync(() => sync.Current.Search("T:N.D", 10, 0, SymbolKinds.Type).Total == 1);
    //     var res = sync.Current.Search("T:N.D", 10, 0, SymbolKinds.Type);
    //     Assert.Single(res.Items);
    //     Assert.Equal("P2", res.Items[0].Assembly);
    // }
}
