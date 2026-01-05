// DocGraph v0.1 - 统计命令
// 参考：api.md §8.2 命令行使用

using System.CommandLine;
using Atelia.DocGraph.Core;

namespace Atelia.DocGraph.Commands;

/// <summary>
/// 统计命令：显示文档图统计信息。
/// </summary>
public class StatsCommand : Command {
    public StatsCommand() : base("stats", "显示文档图统计信息") {
        // 参数定义
        var pathArgument = new Argument<string>(
            name: "path",
            getDefaultValue: () => ".",
            description: "要分析的工作区目录路径"
        );

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "显示详细统计"
        );

        var jsonOption = new Option<bool>(
            name: "--json",
            description: "输出JSON格式"
        );

        AddArgument(pathArgument);
        AddOption(verboseOption);
        AddOption(jsonOption);

        this.SetHandler(ExecuteAsync, pathArgument, verboseOption, jsonOption);
    }

    private static Task<int> ExecuteAsync(string path, bool verbose, bool json) {
        try {
            // 解析工作区路径
            var workspaceRoot = Path.GetFullPath(path);
            if (!Directory.Exists(workspaceRoot)) {
                Console.Error.WriteLine($"❌ [FATAL] 目录不存在: {workspaceRoot}");
                return Task.FromResult(3);
            }

            // 创建构建器
            var builder = new DocumentGraphBuilder(workspaceRoot);

            // 构建文档图
            var graph = builder.Build();

            // 输出统计
            if (json) {
                PrintJsonStats(graph, workspaceRoot);
            }
            else {
                PrintStats(graph, workspaceRoot, verbose);
            }

            return Task.FromResult(0);
        }
        catch (Exception ex) {
            Console.Error.WriteLine($"❌ [FATAL] 执行失败: {ex.Message}");
            return Task.FromResult(3);
        }
    }

    private static void PrintStats(DocumentGraph graph, string workspaceRoot, bool verbose) {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("                    DocGraph 统计报告");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine();

        // 基础统计
        Console.WriteLine("📊 文档统计");
        Console.WriteLine($"   总文档数: {graph.AllNodes.Count}");
        Console.WriteLine($"   Wish 文档: {graph.RootNodes.Count}");
        Console.WriteLine($"   产物文档: {graph.AllNodes.Count - graph.RootNodes.Count}");
        Console.WriteLine();

        // 关系统计
        var totalProduces = graph.AllNodes.Sum(n => n.Produces.Count);
        var totalProducedBy = graph.AllNodes.Sum(n => n.ProducedBy.Count);
        Console.WriteLine("🔗 关系统计");
        Console.WriteLine($"   produce 关系: {totalProduces}");
        Console.WriteLine($"   produce_by 关系: {totalProducedBy}");
        Console.WriteLine();

        // 状态统计（仅限 Wish 文档）
        var statusGroups = graph.RootNodes
            .GroupBy(n => n.Status ?? "unknown")
            .OrderBy(g => g.Key);

        Console.WriteLine("📁 Wish 状态分布");
        foreach (var group in statusGroups) {
            Console.WriteLine($"   {group.Key}: {group.Count()}");
        }
        Console.WriteLine();

        // 详细统计（verbose模式）
        if (verbose) {
            Console.WriteLine("📝 文档详情");
            Console.WriteLine();

            // Wish 文档列表
            Console.WriteLine("   === Wish 文档 ===");
            foreach (var node in graph.RootNodes.OrderBy(n => n.FilePath)) {
                var produceCount = node.Produces.Count;
                Console.WriteLine($"   • {node.FilePath}");
                Console.WriteLine($"     docId: {node.DocId}, status: {node.Status}");
                Console.WriteLine($"     产出文档: {produceCount} 个");
            }
            Console.WriteLine();

            // 产物文档列表
            var productNodes = graph.AllNodes.Where(n => !graph.RootNodes.Contains(n)).ToList();
            if (productNodes.Count > 0) {
                Console.WriteLine("   === 产物文档 ===");
                foreach (var node in productNodes.OrderBy(n => n.FilePath)) {
                    var producedByCount = node.ProducedBy.Count;
                    Console.WriteLine($"   • {node.FilePath}");
                    Console.WriteLine($"     docId: {node.DocId}");
                    Console.WriteLine($"     来源文档: {producedByCount} 个");
                }
                Console.WriteLine();
            }

            // 孤立节点检测
            var orphanNodes = graph.AllNodes
                .Where(n => !graph.RootNodes.Contains(n) && n.ProducedBy.Count == 0)
                .ToList();

            if (orphanNodes.Count > 0) {
                Console.WriteLine("   ⚠️ 孤立文档（无 produce_by 引用）");
                foreach (var node in orphanNodes) {
                    Console.WriteLine($"   • {node.FilePath}");
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine($"📂 工作区: {workspaceRoot}");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
    }

    private static void PrintJsonStats(DocumentGraph graph, string workspaceRoot) {
        // 简单的 JSON 输出（不依赖 System.Text.Json 的高级特性）
        var totalProduces = graph.AllNodes.Sum(n => n.Produces.Count);
        var totalProducedBy = graph.AllNodes.Sum(n => n.ProducedBy.Count);

        var statusCounts = graph.RootNodes
            .GroupBy(n => n.Status ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine("{");
        Console.WriteLine($"  \"workspaceRoot\": \"{EscapeJson(workspaceRoot)}\",");
        Console.WriteLine($"  \"totalDocuments\": {graph.AllNodes.Count},");
        Console.WriteLine($"  \"wishDocuments\": {graph.RootNodes.Count},");
        Console.WriteLine($"  \"productDocuments\": {graph.AllNodes.Count - graph.RootNodes.Count},");
        Console.WriteLine($"  \"produceRelations\": {totalProduces},");
        Console.WriteLine($"  \"producedByRelations\": {totalProducedBy},");
        Console.WriteLine("  \"statusDistribution\": {");

        var statusPairs = statusCounts.ToList();
        for (int i = 0; i < statusPairs.Count; i++) {
            var comma = i < statusPairs.Count - 1 ? "," : "";
            Console.WriteLine($"    \"{EscapeJson(statusPairs[i].Key)}\": {statusPairs[i].Value}{comma}");
        }

        Console.WriteLine("  }");
        Console.WriteLine("}");
    }

    private static string EscapeJson(string value) {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
