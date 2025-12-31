// DocGraph v0.1 - 修复命令
// 参考：spec.md §5.4 修复模式特定约束

using System.CommandLine;
using Atelia.DocGraph.Core;
using Atelia.DocGraph.Core.Fix;

namespace Atelia.DocGraph.Commands;

/// <summary>
/// 修复命令：独立的修复命令，等同于 validate --fix。
/// 为用户提供更直观的修复入口。
/// </summary>
public class FixCommand : Command
{
    public FixCommand() : base("fix", "修复可自动修复的问题（等同于 validate --fix）")
    {
        // 参数定义
        var pathArgument = new Argument<string>(
            name: "path",
            getDefaultValue: () => ".",
            description: "要修复的工作区目录路径");

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "只显示会执行的操作，不实际执行");

        var yesOption = new Option<bool>(
            aliases: ["--yes", "-y"],
            description: "跳过确认提示，自动执行");

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "显示详细输出");

        AddArgument(pathArgument);
        AddOption(dryRunOption);
        AddOption(yesOption);
        AddOption(verboseOption);

        this.SetHandler(ExecuteAsync, pathArgument, dryRunOption, yesOption, verboseOption);
    }

    private static Task<int> ExecuteAsync(string path, bool dryRun, bool yes, bool verbose)
    {
        try
        {
            // 解析工作区路径
            var workspaceRoot = Path.GetFullPath(path);
            if (!Directory.Exists(workspaceRoot))
            {
                Console.Error.WriteLine($"❌ [FATAL] 目录不存在: {workspaceRoot}");
                return Task.FromResult(3);
            }

            // 创建构建器
            var builder = new DocumentGraphBuilder(workspaceRoot);

            // 构建文档图
            if (verbose)
            {
                Console.WriteLine($"📂 扫描目录: {workspaceRoot}");
            }

            var graph = builder.Build();

            if (verbose)
            {
                Console.WriteLine($"   发现 {graph.RootNodes.Count} 个 Wish 文档");
                Console.WriteLine($"   发现 {graph.AllNodes.Count - graph.RootNodes.Count} 个产物文档");
            }

            // 配置修复选项
            var fixOptions = new FixOptions
            {
                Enabled = true,
                DryRun = dryRun,
                AutoConfirm = yes
            };

            // 验证并修复
            var result = builder.Validate(graph, fixOptions);

            // 输出结果
            PrintResult(result, verbose, dryRun);

            // 返回退出码
            return Task.FromResult(GetExitCode(result, fixOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ [FATAL] 执行失败: {ex.Message}");
            if (verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return Task.FromResult(3);
        }
    }

    private static void PrintResult(ValidationResult result, bool verbose, bool dryRun)
    {
        Console.WriteLine();

        if (dryRun)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine("                   DocGraph 修复预览（Dry-Run）");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
        }
        else
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine("                    DocGraph 修复报告");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
        }

        Console.WriteLine();

        // 统计信息
        Console.WriteLine("📊 扫描统计");
        Console.WriteLine($"   总文件数: {result.Statistics.TotalFiles}");
        Console.WriteLine($"   Wish 文档: {result.Statistics.WishDocuments}");
        Console.WriteLine($"   产物文档: {result.Statistics.ProductDocuments}");
        Console.WriteLine($"   关系数量: {result.Statistics.TotalRelations}");
        Console.WriteLine($"   耗时: {result.Statistics.ElapsedTime.TotalMilliseconds:F0}ms");
        Console.WriteLine();

        // 问题列表
        if (result.Issues.Count == 0)
        {
            Console.WriteLine("✅ 无问题发现，无需修复！");
        }
        else
        {
            var fatalCount = result.Issues.Count(i => i.Severity == IssueSeverity.Fatal);
            var errorCount = result.Issues.Count(i => i.Severity == IssueSeverity.Error);
            var warningCount = result.Issues.Count(i => i.Severity == IssueSeverity.Warning);
            var infoCount = result.Issues.Count(i => i.Severity == IssueSeverity.Info);

            Console.WriteLine($"⚠️  发现 {result.Issues.Count} 个问题:");
            if (fatalCount > 0) Console.WriteLine($"   ❌ Fatal: {fatalCount}");
            if (errorCount > 0) Console.WriteLine($"   🔴 Error: {errorCount}");
            if (warningCount > 0) Console.WriteLine($"   🟡 Warning: {warningCount}");
            if (infoCount > 0) Console.WriteLine($"   🔵 Info: {infoCount}");
            Console.WriteLine();

            // 详细问题列表
            if (verbose)
            {
                foreach (var issue in result.Issues)
                {
                    var (icon, actionTag) = issue.Severity switch
                    {
                        IssueSeverity.Fatal => ("❌", "[FATAL]"),
                        IssueSeverity.Error => ("🔴", "[MUST FIX]"),
                        IssueSeverity.Warning => ("🟡", "[SHOULD FIX]"),
                        IssueSeverity.Info => ("🔵", "[FYI]"),
                        _ => ("❓", "[UNKNOWN]")
                    };

                    Console.WriteLine($"{icon} {actionTag} [{issue.ErrorCode}]");
                    Console.WriteLine($"   文件: {issue.FilePath}");
                    Console.WriteLine($"   问题: {issue.Message}");
                    Console.WriteLine($"   建议: {issue.QuickSuggestion}");
                    Console.WriteLine();
                }
            }
        }

        // 修复结果
        if (result.FixResults != null && result.FixResults.Count > 0)
        {
            if (dryRun)
            {
                Console.WriteLine("🔧 计划执行的修复操作");
            }
            else
            {
                Console.WriteLine("🔧 修复执行结果");
            }

            var successCount = result.FixResults.Count(r => r.Success);
            var failCount = result.FixResults.Count(r => !r.Success);

            Console.WriteLine($"   总计: {result.FixResults.Count} 个操作");
            if (!dryRun)
            {
                Console.WriteLine($"   成功: {successCount}, 失败: {failCount}");
            }
            Console.WriteLine();

            foreach (var fixResult in result.FixResults)
            {
                if (dryRun)
                {
                    Console.WriteLine($"   📝 将创建: {fixResult.TargetPath}");
                }
                else
                {
                    var status = fixResult.Success ? "✅" : "❌";
                    Console.WriteLine($"   {status} {fixResult.TargetPath}");
                    if (!fixResult.Success && fixResult.ErrorMessage != null)
                    {
                        Console.WriteLine($"      错误: {fixResult.ErrorMessage}");
                    }
                }
            }
            Console.WriteLine();
        }
        else if (result.Issues.Any(i => i.Severity is IssueSeverity.Error or IssueSeverity.Fatal))
        {
            Console.WriteLine("⚠️  存在 Error/Fatal 级别问题，无法执行自动修复。");
            Console.WriteLine("   请先手动解决这些问题，再运行 fix 命令。");
            Console.WriteLine();
        }

        Console.WriteLine("═══════════════════════════════════════════════════════════");
    }

    private static int GetExitCode(ValidationResult result, FixOptions fixOptions)
    {
        // 参考 spec.md §5.3 和 [A-DOCGRAPH-EXITCODE-FIX]
        var hasFatal = result.Issues.Any(i => i.Severity == IssueSeverity.Fatal);
        var hasError = result.Issues.Any(i => i.Severity == IssueSeverity.Error);
        var hasWarning = result.Issues.Any(i => i.Severity == IssueSeverity.Warning);

        if (hasFatal)
        {
            return 3; // Fatal
        }

        // 修复模式退出码
        var anyFixFailed = result.FixResults?.Any(r => !r.Success) ?? false;
        if (anyFixFailed)
        {
            return 3; // 修复执行失败
        }

        if (hasError)
        {
            return 2; // 有错误，未执行修复（或部分修复）
        }

        if (hasWarning)
        {
            return 1; // 有警告
        }

        return 0; // 成功
    }
}
