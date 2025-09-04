using System;
using System.IO;
using CodeCortex.Core.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using StreamJsonRpc;
using CodeCortex.Service;
using CodeCortex.Core.Index;
using CodeCortex.Core.Symbols; // 仅用于 SymbolResolver，不再用于 SymbolMatch/MatchKind
using CodeCortex.Core.Outline;
using CodeCortex.Workspace;
using CodeCortex.Core.Hashing;
using CodeCortex.Core.Ids;

namespace CodeCortex.Service;

public class ServiceIndexBuildObserver : IIndexBuildObserver {
    public void OnStart(IndexBuildRequest req) {
        Console.WriteLine($"[索引] 开始构建: {req.SolutionPath}");
    }

    public void OnTypeAdded(string typeId, string fqn) {
        // 可选：输出进度（但避免过多噪声）
    }

    public void OnOutlineWritten(string typeId) {
        // 可选：输出 Outline 写入进度
    }

    public void OnCompleted(CodeCortexIndex index, long durationMs) {
        Console.WriteLine($"[索引] 构建完成: {index.Stats.TypeCount} 个类型, 耗时 {durationMs}ms");
    }
}

public class RpcService {
    private readonly CodeCortexIndex _index;
    private readonly SymbolResolver _resolver;
    private readonly string _outlineDir;
    private readonly IFileSystem _fs;

    public RpcService(CodeCortexIndex index, string outlineDir, IFileSystem? fs = null) {
        _index = index;
        _resolver = new SymbolResolver(index);
        _outlineDir = outlineDir;
        _fs = fs ?? new DefaultFileSystem();
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
        var path = System.IO.Path.Combine(_outlineDir, type.Id + ".outline.md");
        if (_fs.FileExists(path)) {
            return Task.FromResult<string?>(_fs.ReadAllText(path));
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

        // 支持通过参数注入 index 路径、outline 目录、端口
        string ctxRoot = Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        string indexPath = Path.Combine(ctxRoot, "index.json");
        string outlineDir = Path.Combine(ctxRoot, "types");
        int port = 9000;

        // 初始化 TypeIdGenerator
        TypeIdGenerator.Initialize(Directory.GetCurrentDirectory());

        for (int i = 0; i < args.Length; i++) {
            if (args[i] == "--index" && i + 1 < args.Length) {
                indexPath = args[++i];
            } else if (args[i] == "--outline" && i + 1 < args.Length) {
                outlineDir = args[++i];
            } else if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) {
                port = p;
                i++;
            }
        }

        // 自动索引维护：检查索引是否存在且有效
        var index = await EnsureIndexAsync(indexPath, outlineDir);
        if (index == null) {
            await Console.Error.WriteLineAsync("[FATAL] 索引初始化失败");
            return;
        }

        // 配置默认输出到 LLM 显示器
        // await UpdateLlmDisplayAsync(index); 暂且关闭此功能尝试，因为“.github\copilot-instructions.md”的值只伴随user消息注入，而不随着LLM进行工具调用而刷新注入的内容，暂时无法把这文件当成面向LLM的实时显示器用了。

        var service = new RpcService(index, outlineDir);
        await TcpRpcHost.StartAsync(service, port);
    }

    private static async Task<CodeCortexIndex?> EnsureIndexAsync(string indexPath, string outlineDir) {
        // 尝试加载现有索引
        if (System.IO.File.Exists(indexPath)) {
            try {
                var json = System.IO.File.ReadAllText(indexPath);
                var index = System.Text.Json.JsonSerializer.Deserialize<CodeCortexIndex>(json);
                if (index != null && index.Types.Count > 0) {
                    Console.WriteLine($"[索引] 加载现有索引: {index.Types.Count} 个类型");
                    return index;
                }
            } catch (Exception ex) {
                Console.WriteLine($"[索引] 加载现有索引失败: {ex.Message}");
            }
        }

        // 索引不存在或无效，自动构建
        Console.WriteLine("[索引] 索引不存在或为空，开始自动构建...");
        return await BuildIndexAsync(indexPath, outlineDir);
    }

    private static async Task<CodeCortexIndex?> BuildIndexAsync(string indexPath, string outlineDir) {
        try {
            // 智能工作区发现：从当前目录向上查找 .sln 文件
            var workspaceRoot = FindWorkspaceRoot(Directory.GetCurrentDirectory());
            if (workspaceRoot == null) {
                Console.WriteLine("[错误] 未找到工作区（.sln 文件）");
                return null;
            }

            Console.WriteLine($"[索引] 发现工作区: {workspaceRoot}");

            // 使用完整的S1-S3实现：WorkspaceLoader + IndexBuilder
            var loader = new MsBuildWorkspaceLoader(MsBuildMode.Auto);
            var loaded = await loader.LoadAsync(workspaceRoot);

            Console.WriteLine($"[索引] 工作区加载成功: {loaded.Projects.Count} 个项目");

            // 创建索引构建器（S1-S3 完整实现）
            var enumerator = new RoslynTypeEnumerator();
            var hasher = new TypeHasher();
            var outlineExtractor = new OutlineExtractor();
            var observer = new ServiceIndexBuildObserver();
            var builder = new IndexBuilder(enumerator, hasher, outlineExtractor, observer);

            // 构建索引请求
            var hashConfig = new HashConfig();
            var outlineOptions = new OutlineOptions();
            var outlineWriter = new FileOutlineWriter(outlineDir);
            var request = new IndexBuildRequest(
                workspaceRoot,
                loaded.Projects.ToList(),
                true, // 生成 Outlines
                hashConfig,
                outlineOptions,
                SystemClock.Instance,
                outlineWriter
            );

            // 执行完整索引构建
            Console.WriteLine("[索引] 开始类型枚举和哈希计算...");
            var index = builder.Build(request);

            Console.WriteLine($"[索引] 构建完成: {index.Stats.TypeCount} 个类型, 耗时 {index.Build.DurationMs}ms");

            // 保存索引到文件
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            var json = System.Text.Json.JsonSerializer.Serialize(index,
                new System.Text.Json.JsonSerializerOptions {
                    WriteIndented = true
                }
            );
            await File.WriteAllTextAsync(indexPath, json);
            Console.WriteLine($"[索引] 已保存到: {indexPath}");

            return index;
        } catch (Exception ex) {
            Console.WriteLine($"[错误] 索引构建失败: {ex.Message}");
            Console.WriteLine($"[调试] 异常详情: {ex}");
            return null;
        }
    }
    private static string? FindWorkspaceRoot(string startPath) {
        var dir = new DirectoryInfo(startPath);
        while (dir != null) {
            // 查找 .sln 文件
            if (dir.GetFiles("*.sln").Length > 0) {
                return dir.GetFiles("*.sln").First().FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task UpdateLlmDisplayAsync(CodeCortexIndex index) {
        try {
            var displayPath = Path.Combine(Directory.GetCurrentDirectory(), ".github", "copilot-instructions.md");
            var content = $@"## CodeCortex 服务状态

**服务启动时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
**索引状态**: {index.Types.Count} 个类型已索引
**项目数量**: {index.Stats.ProjectCount}

索引自动维护已启用，服务正在运行于 TCP 模式。
";

            if (File.Exists(displayPath)) {
                await File.WriteAllTextAsync(displayPath, content);
                Console.WriteLine($"[显示器] 已更新 LLM 显示器: {displayPath}");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[显示器] 更新 LLM 显示器失败: {ex.Message}");
        }
    }
}
