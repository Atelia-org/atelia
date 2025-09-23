using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CodeCortexV2.Index;
using CodeCortexV2.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Atelia.CodeCortex.Tests;

[Collection("IndexSyncSerial")] // 串行，避免干扰
public class V2_IndexSynchronizer_BurstThresholdTests {
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000, int intervalMs = 50) {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs) {
            try { if (condition()) { return; } } catch { }
            await Task.Delay(intervalMs);
        }
        if (!condition()) { throw new TimeoutException("Condition not met within timeout"); }
    }

    [Fact]
    public async Task BurstThreshold_OverridesDebounce_ForImmediateFlush() {
        var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        ws.AddProject(ProjectInfo.Create(pid, VersionStamp.Create(), name: "P", assemblyName: "P", language: LanguageNames.CSharp));
        var sync = await IndexSynchronizer.CreateAsync(ws, CancellationToken.None);
        sync.DebounceMs = 60000; // 很大的 debounce
        sync.BurstFlushThreshold = 1; // 一有事件就触发 Flush

        var sw = Stopwatch.StartNew();
        ws.AddDocument(pid, "A.cs", SourceText.From("namespace Fast { public class A {} }"));
        await WaitUntilAsync(() => sync.Current.Search("T:Fast.A", 10, 0, SymbolKinds.Type).Total >= 1, timeoutMs: 5000);
        sw.Stop();

        // 若没有 burst 机制，理论上要等 60s；现在应在 5s 内完成
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Indexing took too long: {sw.ElapsedMilliseconds}ms");
    }
}
