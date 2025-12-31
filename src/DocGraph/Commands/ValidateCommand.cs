// DocGraph v0.1 - 验证命令
// 参考：api.md §8.2 命令行使用

using System.CommandLine;
using Atelia.DocGraph.Core;
using Atelia.DocGraph.Core.Fix;

namespace Atelia.DocGraph.Commands;

/// <summary>
/// 验证命令：验证文档关系完整性。
/// </summary>
public class ValidateCommand : Command
{
    public ValidateCommand() : base("validate", "验证文档关系完整性")
    {
        // 参数定义
        var pathArgument = new Argument<string>(
            name: "path",
            getDefaultValue: () => ".",
            description: "要验证的工作区目录路径");

        var fixOption = new Option<bool>(
            name: "--fix",
            description: "修复可自动修复的问题");

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "只显示会执行的操作，不实际执行");

        var yesOption = new Option<bool>(
            aliases: ["--yes", "-y"],
            description: "跳过确认提示，自动执行");

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "显示详细输出");

        var outputOption = new Option<string>(
            aliases: ["--output", "-o"],
            description: "输出格式：text（默认）或 json");
        outputOption.SetDefaultValue("text");

        AddArgument(pathArgument);
        AddOption(fixOption);
        AddOption(dryRunOption);
        AddOption(yesOption);
        AddOption(verboseOption);
        AddOption(outputOption);

        this.SetHandler(ExecuteAsync, pathArgument, fixOption, dryRunOption, yesOption, verboseOption, outputOption);
    }

    private static Task<int> ExecuteAsync(string path, bool fix, bool dryRun, bool yes, bool verbose, string output)
    {
        try
        {
            // 解析工作区路径
            var workspaceRoot = Path.GetFullPath(path);
            if (!Directory.Exists(workspaceRoot))
            {
                if (output == "json")
                {
                    Console.WriteLine($"{{\"error\": \"目录不存在: {EscapeJson(workspaceRoot)}\", \"exitCode\": 3}}");
                }
                else
                {
                    Console.Error.WriteLine($"❌ [FATAL] 目录不存在: {workspaceRoot}");
                }
                return Task.FromResult(3);
            }

            // 创建构建器
            var builder = new DocumentGraphBuilder(workspaceRoot);

            // 构建文档图
            if (verbose && output != "json")
            {
                Console.WriteLine($"📂 扫描目录: {workspaceRoot}");
            }

            var graph = builder.Build();

            if (verbose && output != "json")
            {
                Console.WriteLine($"   发现 {graph.RootNodes.Count} 个 Wish 文档");
                Console.WriteLine($"   发现 {graph.AllNodes.Count - graph.RootNodes.Count} 个产物文档");
            }

            // 配置修复选项
            var fixOptions = fix
                ? new FixOptions
                {
                    Enabled = true,
                    DryRun = dryRun,
                    AutoConfirm = yes
                }
                : FixOptions.Disabled;

            // 验证
            var result = builder.Validate(graph, fixOptions);

            // 输出结果
            if (output == "json")
            {
                PrintJsonResult(result, workspaceRoot);
            }
            else
            {
                PrintResult(result, verbose);
            }

            // 返回退出码
            return Task.FromResult(GetExitCode(result, fixOptions));
        }
        catch (Exception ex)
        {
            if (output == "json")
            {
                Console.WriteLine($"{{\"error\": \"{EscapeJson(ex.Message)}\", \"exitCode\": 3}}");
            }
            else
            {
                Console.Error.WriteLine($"❌ [FATAL] 执行失败: {ex.Message}");
                if (verbose)
                {
                    Console.Error.WriteLine(ex.StackTrace);
                }
            }
            return Task.FromResult(3);
        }
    }

    private static void PrintResult(ValidationResult result, bool verbose)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("                    DocGraph 验证报告");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
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
            Console.WriteLine("✅ 验证通过，无问题发现！");
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
            // 遵循 [S-ERROR-003]：错误严重度使用视觉标记和动作标签
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

                if (verbose)
                {
                    Console.WriteLine($"   详细: {issue.DetailedSuggestion}");
                }

                Console.WriteLine();
            }
        }

        // 修复结果
        if (result.FixResults != null && result.FixResults.Count > 0)
        {
            Console.WriteLine("🔧 修复结果");
            foreach (var fixResult in result.FixResults)
            {
                var status = fixResult.Success ? "✅" : "❌";
                Console.WriteLine($"   {status} {fixResult.TargetPath}");
                if (!fixResult.Success && fixResult.ErrorMessage != null)
                {
                    Console.WriteLine($"      错误: {fixResult.ErrorMessage}");
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine("═══════════════════════════════════════════════════════════");
    }

    private static void PrintJsonResult(ValidationResult result, string workspaceRoot)
    {
        Console.WriteLine("{");
        Console.WriteLine($"  \"workspaceRoot\": \"{EscapeJson(workspaceRoot)}\",");
        Console.WriteLine($"  \"isValid\": {(result.IsValid ? "true" : "false")},");
        Console.WriteLine($"  \"statistics\": {{");
        Console.WriteLine($"    \"totalFiles\": {result.Statistics.TotalFiles},");
        Console.WriteLine($"    \"wishDocuments\": {result.Statistics.WishDocuments},");
        Console.WriteLine($"    \"productDocuments\": {result.Statistics.ProductDocuments},");
        Console.WriteLine($"    \"totalRelations\": {result.Statistics.TotalRelations},");
        Console.WriteLine($"    \"elapsedMs\": {result.Statistics.ElapsedTime.TotalMilliseconds:F0}");
        Console.WriteLine("  },");
        Console.WriteLine("  \"issues\": [");

        for (int i = 0; i < result.Issues.Count; i++)
        {
            var issue = result.Issues[i];
            var comma = i < result.Issues.Count - 1 ? "," : "";
            Console.WriteLine("    {");
            Console.WriteLine($"      \"severity\": \"{issue.Severity}\",");
            Console.WriteLine($"      \"errorCode\": \"{EscapeJson(issue.ErrorCode)}\",");
            Console.WriteLine($"      \"message\": \"{EscapeJson(issue.Message)}\",");
            Console.WriteLine($"      \"filePath\": \"{EscapeJson(issue.FilePath)}\",");
            if (issue.TargetFilePath != null)
            {
                Console.WriteLine($"      \"targetFilePath\": \"{EscapeJson(issue.TargetFilePath)}\",");
            }
            if (issue.LineNumber.HasValue)
            {
                Console.WriteLine($"      \"lineNumber\": {issue.LineNumber},");
            }
            Console.WriteLine($"      \"quickSuggestion\": \"{EscapeJson(issue.QuickSuggestion)}\",");
            Console.WriteLine($"      \"detailedSuggestion\": \"{EscapeJson(issue.DetailedSuggestion)}\"");
            Console.WriteLine($"    }}{comma}");
        }

        Console.WriteLine("  ],");
        Console.WriteLine("  \"fixResults\": [");

        if (result.FixResults != null)
        {
            for (int i = 0; i < result.FixResults.Count; i++)
            {
                var fixResult = result.FixResults[i];
                var comma = i < result.FixResults.Count - 1 ? "," : "";
                Console.WriteLine("    {");
                Console.WriteLine($"      \"success\": {(fixResult.Success ? "true" : "false")},");
                Console.WriteLine($"      \"targetPath\": \"{EscapeJson(fixResult.TargetPath ?? "")}\",");
                Console.WriteLine($"      \"actionType\": \"{fixResult.ActionType}\"");
                if (!fixResult.Success && fixResult.ErrorMessage != null)
                {
                    Console.WriteLine($"      ,\"errorMessage\": \"{EscapeJson(fixResult.ErrorMessage)}\"");
                }
                Console.WriteLine($"    }}{comma}");
            }
        }

        Console.WriteLine("  ]");
        Console.WriteLine("}");
    }

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    private static int GetExitCode(ValidationResult result, FixOptions fixOptions)
    {
        // 参考 spec.md §5.3 退出码语义
        var hasFatal = result.Issues.Any(i => i.Severity == IssueSeverity.Fatal);
        var hasError = result.Issues.Any(i => i.Severity == IssueSeverity.Error);
        var hasWarning = result.Issues.Any(i => i.Severity == IssueSeverity.Warning);

        if (hasFatal)
        {
            return 3; // Fatal
        }

        if (fixOptions.Enabled)
        {
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

        // 基础退出码
        if (hasError)
        {
            return 2;
        }

        if (hasWarning)
        {
            return 1;
        }

        return 0;
    }
}
