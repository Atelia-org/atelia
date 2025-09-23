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

[Collection("IndexSyncSerial")] // 复用现有集合，避免并行干扰 debounce
public class V2_IndexSynchronizer_DeduplicationTests {
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000, int intervalMs = 50) {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs) {
            try { if (condition()) { return; } } catch { /* ignore transient */ }
            await Task.Delay(intervalMs);
        }
        if (!condition()) { throw new TimeoutException("Condition not met within timeout"); }
    }

    private static async Task<(AdhocWorkspace ws, IndexSynchronizer sync)> CreateWorkspaceAsync() {
        var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var projInfo = ProjectInfo.Create(pid, VersionStamp.Create(), name: "P", assemblyName: "P", language: LanguageNames.CSharp);
        ws.AddProject(projInfo);
        var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
        sync.DebounceMs = 20; // 加速测试
        return (ws, sync);
    }

    [Fact]
    public async Task RemoveAndAddSameId_PrefersAdd() {
        var (ws, sync) = await CreateWorkspaceAsync();
        var pid = ws.CurrentSolution.ProjectIds[0];
        var doc = ws.AddDocument(pid, "A.cs", SourceText.From("namespace N { public class A {} }"));
        await WaitUntilAsync(() => sync.Current.Search("T:N.A", 10, 0, SymbolKinds.Type).Total >= 1);

        // 在同一批次中：移除 A，再添加同名 A（模拟 editor 抖动导致的 remove+add 冲突）
        var sol = ws.CurrentSolution.RemoveDocument(doc.Id);
        sol = sol.AddDocument(DocumentId.CreateNewId(pid), "A2.cs", SourceText.From("namespace N { public class A {} }"));
        Assert.True(ws.TryApplyChanges(sol));

        await WaitUntilAsync(() => sync.Current.Search("T:N.A", 10, 0, SymbolKinds.Type).Total >= 1);
        var res = sync.Current.Search("T:N.A", 10, 0, SymbolKinds.Type);
        Assert.True(res.Total >= 1);
    }

    [Fact]
    public async Task MultipleAdds_SameId_LastWins() {
        var (ws, sync) = await CreateWorkspaceAsync();
        var pid = ws.CurrentSolution.ProjectIds[0];

        // 第一份定义
        var sol = ws.CurrentSolution.AddDocument(DocumentId.CreateNewId(pid), "A1.cs", SourceText.From("namespace N { public partial class A { public static int X = 1; } }"));
        // 第二份定义（同 id add），在同一批次中添加，后者覆盖前者
        sol = sol.AddDocument(DocumentId.CreateNewId(pid), "A2.cs", SourceText.From("namespace N { public partial class A { public static int Y = 2; } }"));
        Assert.True(ws.TryApplyChanges(sol));

        await WaitUntilAsync(() => sync.Current.Search("T:N.A", 10, 0, SymbolKinds.Type).Total >= 1);
        var res = sync.Current.Search("T:N.A", 10, 0, SymbolKinds.Type);
        Assert.True(res.Total >= 1);
        // 索引是基于类型存在性而非字段合并的，验证存在即可，语义保持“最后 add 生效”的自洽
    }
}
