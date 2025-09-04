using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using StreamJsonRpc;
using CodeCortex.Service;
using CodeCortex.Core.Index;
using CodeCortex.Core.Symbols; // 仅用于 SymbolResolver，不再用于 SymbolMatch/MatchKind
using CodeCortex.Core.Outline;

namespace CodeCortex.Service;

public class RpcService {
    private readonly CodeCortexIndex _index;
    private readonly SymbolResolver _resolver;
    private readonly string _outlineDir;

    public RpcService(CodeCortexIndex index, string outlineDir) {
        _index = index;
        _resolver = new SymbolResolver(index);
        _outlineDir = outlineDir;
    }

    [JsonRpcMethod(RpcMethods.ResolveSymbol)]

    public Task<List<SymbolMatch>> ResolveSymbolAsync(ResolveRequest req) {
        var matches = _resolver.Resolve(req.Query, 20);
        var mapped = new List<SymbolMatch>();
        foreach (var m in matches) {
            mapped.Add(
                new SymbolMatch(
                    m.Id,
                    m.Fqn,
                    m.Kind,
                    (MatchKind)(int)m.MatchKind,
                    m.RankScore,
                    m.Distance,
                    m.IsAmbiguous
                )
            );
        }
        return Task.FromResult(mapped);
    }

    [JsonRpcMethod(RpcMethods.GetOutline)]
    public Task<string?> GetOutlineAsync(OutlineRequest req) {
        var idOrFqn = req.QueryOrId;
        var type = _index.Types.Find(t => t.Id == idOrFqn || t.Fqn == idOrFqn);
        if (type == null) {
            return Task.FromResult<string?>(null);
        }

        var path = Path.Combine(_outlineDir, type.Id + ".outline.md");
        if (File.Exists(path)) {
            return Task.FromResult<string?>(File.ReadAllText(path));
        }

        return Task.FromResult<string?>(null);
    }

    [JsonRpcMethod(RpcMethods.SearchSymbols)]

    public Task<List<SymbolMatch>> SearchSymbolsAsync(SearchRequest req) {
        var matches = _resolver.Search(req.Query, req.Limit);
        var mapped = new List<SymbolMatch>();
        foreach (var m in matches) {
            mapped.Add(
                new SymbolMatch(
                    m.Id,
                    m.Fqn,
                    m.Kind,
                    (MatchKind)(int)m.MatchKind,
                    m.RankScore,
                    m.Distance,
                    m.IsAmbiguous
                )
            );
        }
        return Task.FromResult(mapped);
    }

    [JsonRpcMethod(RpcMethods.Status)]
    public Task<StatusResponse> StatusAsync() {
        var s = _index.Stats;
        // 这里只做最基础实现，后续可采集更多指标
        return Task.FromResult(
            new StatusResponse(
                s.ProjectCount,
                s.TypeCount,
                _index.Build.DurationMs,
                _index.Incremental.LastIncrementalMs,
                0, // watcherQueueDepth
                1.0, // outlineCacheHitRatio (stub)
                GC.GetTotalMemory(false) / (1024 * 1024)
            )
        );
    }
}

public static class Program {
    public static async Task Main(string[] args) {
        await MainAsync(args);
    }

    private static async Task MainAsync(string[] args) {
        Console.WriteLine($"[CodeCortex.Service] 启动于 {DateTime.Now:O}");
        var ctxRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        var indexPath = Path.Combine(ctxRoot, "index.json");
        var outlineDir = Path.Combine(ctxRoot, "types");
        if (!File.Exists(indexPath)) {
            await Console.Error.WriteLineAsync($"[FATAL] 未找到 index.json: {indexPath}");
            return;
        }
        var json = File.ReadAllText(indexPath);
        var index = System.Text.Json.JsonSerializer.Deserialize<CodeCortexIndex>(json);
        if (index == null) {
            await Console.Error.WriteLineAsync("[FATAL] index.json 解析失败");
            return;
        }
        var service = new RpcService(index, outlineDir);
        int port = 9000;
        if (args.Length > 0 && int.TryParse(args[0], out var argPort))
            port = argPort;
        await TcpRpcHost.StartAsync(service, port);
    }
}
