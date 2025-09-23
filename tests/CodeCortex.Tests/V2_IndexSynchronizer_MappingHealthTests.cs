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
public class V2_IndexSynchronizer_MappingHealthTests {
    private static async Task<(AdhocWorkspace ws, ProjectId pid, IndexSynchronizer sync)> CreateWorkspaceAsync() {
        var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var projInfo = ProjectInfo.Create(pid, VersionStamp.Create(), name: "P", assemblyName: "P", language: LanguageNames.CSharp);
        ws.AddProject(projInfo);
        var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
        sync.DebounceMs = 10;
        return (ws, pid, sync);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000, int intervalMs = 20) {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs) {
            try { if (condition()) { return; } } catch { }
            await Task.Delay(intervalMs);
        }
        if (!condition()) { throw new TimeoutException("Timeout waiting for condition"); }
    }

    [Fact]
    public async Task Rebuild_Then_No_OutOfSolution_DocIds() {
        var (ws, pid, sync) = await CreateWorkspaceAsync();
        ws.AddDocument(pid, "A.cs", SourceText.From("namespace N { public class A {} }"));
        await WaitUntilAsync(() => sync.Current.Search("T:N.A", 1, 0, SymbolKinds.Type).Total >= 1);

        // 假设 Rebuild 后映射应与当前解一致
        await sync.RebuildDocTypeMapsAsync(ws.CurrentSolution, CancellationToken.None);
        var h = sync.GetMappingHealth(ws.CurrentSolution);
        Assert.Equal(0, h.OutDocInDocMap);
        Assert.Equal(0, h.OutDocInTypeMap);
        Assert.True(h.DocMapCount > 0);
        Assert.True(h.TypeMapCount > 0);
    }

    [Fact]
    public async Task RemoveProject_Maps_Cleaned() {
        var (ws, pid, sync) = await CreateWorkspaceAsync();
        ws.AddDocument(pid, "A.cs", SourceText.From("namespace N { public class A {} }"));
        await WaitUntilAsync(() => sync.Current.Search("T:N.A", 1, 0, SymbolKinds.Type).Total >= 1);

        // Remove project and ensure rebuild cleans maps
        var sol2 = ws.CurrentSolution.RemoveProject(pid);
        Assert.True(ws.TryApplyChanges(sol2));
        await WaitUntilAsync(() => sync.Current.Search("T:N.A", 1, 0, SymbolKinds.Type).Total == 0);

        await sync.RebuildDocTypeMapsAsync(ws.CurrentSolution, CancellationToken.None);
        var h = sync.GetMappingHealth(ws.CurrentSolution);
        Assert.Equal(0, h.OutDocInDocMap);
        Assert.Equal(0, h.OutDocInTypeMap);
        Assert.True(h.DocMapCount >= 0);
        Assert.True(h.TypeMapCount >= 0);
    }
}
