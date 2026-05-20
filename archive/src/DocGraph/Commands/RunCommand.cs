// DocGraph v0.1 - 全流程命令
// 默认命令：validate + fix + generate

using System.CommandLine;
using Atelia.DocGraph.Core;
using Atelia.DocGraph.Core.Fix;
using Atelia.DocGraph.Visitors;

namespace Atelia.DocGraph.Commands;

/// <summary>
/// 输出路径预检结果。
/// </summary>
internal sealed class OutputPreflightResult {
    public bool Success { get; init; }
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// 输出路径预检器。
/// </summary>
internal static class OutputPreflight {
    /// <summary>
    /// 校验所有 visitor 的输出路径。
    /// </summary>
    /// <param name="visitors">Visitor 列表。</param>
    /// <param name="graph">文档图。</param>
    /// <param name="workspaceRoot">工作区根路径（已规范化）。</param>
    /// <returns>预检结果。</returns>
    public static OutputPreflightResult Validate(
        IReadOnlyList<IDocumentGraphVisitor> visitors,
        DocumentGraph graph,
        string workspaceRoot
    ) {
        var errors = new List<string>();
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var visitor in visitors) {
            var multiOutput = visitor.GenerateMultiple(graph);

            // 空 Dictionary 等价于 null，回退单输出模式（修复点 3）
            if (multiOutput != null && multiOutput.Count == 0) {
                multiOutput = null;
            }

            if (multiOutput != null) {
                foreach (var (key, _) in multiOutput) {
                    // Key 基本校验（修复点 4）
                    if (string.IsNullOrWhiteSpace(key)) {
                        errors.Add($"[{visitor.Name}] 输出路径 key 不能为空或空白");
                        continue;
                    }

                    // 路径安全校验（修复点 2）
                    var pathError = ValidateOutputPath(key, workspaceRoot, visitor.Name);
                    if (pathError != null) {
                        errors.Add(pathError);
                        continue;
                    }

                    // 路径冲突检测（修复点 1）
                    var normalizedPath = NormalizePath(key, workspaceRoot);
                    if (!allPaths.Add(normalizedPath)) {
                        errors.Add($"[{visitor.Name}] 输出路径冲突: {key}");
                    }
                }
            }
            else {
                // 单输出模式
                var outputPath = visitor.OutputPath;

                // OutputPath 基本校验（与多输出的 key 校验对齐）
                if (string.IsNullOrWhiteSpace(outputPath)) {
                    errors.Add($"[{visitor.Name}] OutputPath 不能为空或空白");
                    continue;
                }

                // 路径安全校验（修复点 2）
                var pathError = ValidateOutputPath(outputPath, workspaceRoot, visitor.Name);
                if (pathError != null) {
                    errors.Add(pathError);
                    continue;
                }

                // 路径冲突检测（修复点 1）
                var normalizedPath = NormalizePath(outputPath, workspaceRoot);
                if (!allPaths.Add(normalizedPath)) {
                    errors.Add($"[{visitor.Name}] 输出路径冲突: {outputPath}");
                }
            }
        }

        return new OutputPreflightResult {
            Success = errors.Count == 0,
            Errors = errors
        };
    }

    /// <summary>
    /// 校验单个输出路径的安全性。
    /// </summary>
    private static string? ValidateOutputPath(string relativePath, string workspaceRoot, string visitorName) {
        // 拒绝绝对路径
        if (Path.IsPathRooted(relativePath)) { return $"[{visitorName}] 输出路径不能是绝对路径: {relativePath}"; }

        // 拒绝路径穿越
        if (relativePath.Contains("..")) { return $"[{visitorName}] 输出路径不能包含 '..': {relativePath}"; }

        // 归一化后验证必须在 workspace 内
        var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        var normalizedWorkspace = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase)) { return $"[{visitorName}] 输出路径越界 workspace: {relativePath}"; }

        return null;
    }

    /// <summary>
    /// 规范化路径用于冲突检测。
    /// </summary>
    private static string NormalizePath(string relativePath, string workspaceRoot) {
        return Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
    }
}

/// <summary>
/// 全流程命令（默认行为）：validate + fix + generate。
/// 当用户直接运行 docgraph 时执行此流程。
/// </summary>
public class RunCommand : Command {
    public RunCommand() : base("run", "执行全流程：验证 + 修复 + 生成（可省略，直接 docgraph 即可）") {
        // 参数定义
        var pathArgument = new Argument<string>(
            name: "path",
            getDefaultValue: () => ".",
            description: "工作区目录路径"
        );

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "只显示会执行的操作，不实际执行"
        );

        var yesOption = new Option<bool>(
            aliases: ["--yes", "-y"],
            description: "跳过确认提示，自动执行"
        );

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "显示详细输出"
        );

        var forceOption = new Option<bool>(
            name: "--force",
            description: "即使有 Error 级别问题也继续生成（不推荐）"
        );

        AddArgument(pathArgument);
        AddOption(dryRunOption);
        AddOption(yesOption);
        AddOption(verboseOption);
        AddOption(forceOption);

        this.SetHandler(ExecuteAsync, pathArgument, dryRunOption, yesOption, verboseOption, forceOption);
    }

    /// <summary>
    /// 执行全流程（可被 Program.cs 直接调用）。
    /// </summary>
    public static Task<int> ExecuteAsync(string path, bool dryRun, bool yes, bool verbose, bool force) {
        try {
            // 解析工作区路径
            var workspaceRoot = Path.GetFullPath(path);
            if (!Directory.Exists(workspaceRoot)) {
                Console.Error.WriteLine($"❌ [FATAL] 目录不存在: {workspaceRoot}");
                return Task.FromResult(3);
            }

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine("                    DocGraph 全流程执行");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine();

            // ===== 阶段 1：构建文档图 =====
            Console.WriteLine("📂 阶段 1/3：扫描文档图");
            if (verbose) {
                Console.WriteLine($"   工作区: {workspaceRoot}");
            }

            var builder = new DocumentGraphBuilder(workspaceRoot);
            var graph = builder.Build();

            Console.WriteLine($"   ✅ 发现 {graph.RootNodes.Count} 个 Wish 文档，{graph.AllNodes.Count - graph.RootNodes.Count} 个产物文档");
            Console.WriteLine();

            // ===== 阶段 2：验证 + 修复 =====
            Console.WriteLine("🔍 阶段 2/3：验证并修复");

            var fixOptions = new FixOptions {
                Enabled = true,
                DryRun = dryRun,
                AutoConfirm = yes
            };

            var result = builder.Validate(graph, fixOptions);

            // 输出验证结果摘要
            PrintValidationSummary(result, verbose);

            // 检查是否有阻塞性错误
            var hasError = result.Issues.Any(i => i.Severity is IssueSeverity.Error or IssueSeverity.Fatal);
            if (hasError && !force) {
                Console.WriteLine();
                Console.WriteLine("❌ 存在 Error/Fatal 级别问题，无法继续生成。");
                Console.WriteLine("   请先修复这些问题，或使用 --force 跳过（不推荐）。");
                Console.WriteLine();
                Console.WriteLine("═══════════════════════════════════════════════════════════");
                return Task.FromResult(2);
            }

            if (hasError && force) {
                Console.WriteLine();
                Console.WriteLine("⚠️  使用 --force 跳过错误，生成结果可能不完整。");
            }

            Console.WriteLine();

            // ===== 阶段 3：生成汇总文档 =====
            Console.WriteLine("📝 阶段 3/3：生成汇总文档");

            // 重新构建文档图（fix 之后可能有新文件）
            if (result.FixResults?.Any(r => r.Success) == true && !dryRun) {
                if (verbose) {
                    Console.WriteLine("   重新扫描文档图（包含新创建的文件）...");
                }
                graph = builder.Build();
            }

            var visitors = GetVisitors();

            // Preflight 校验：路径冲突 + 安全性（修复点 1, 2, 4）
            var preflightResult = OutputPreflight.Validate(visitors, graph, workspaceRoot);
            if (!preflightResult.Success) {
                Console.WriteLine();
                Console.WriteLine("❌ 输出路径预检失败：");
                foreach (var error in preflightResult.Errors) {
                    Console.WriteLine($"   • {error}");
                }
                Console.WriteLine();
                Console.WriteLine("═══════════════════════════════════════════════════════════");
                return Task.FromResult(3);
            }

            var generatedFiles = new List<(string Path, bool Success, string? Error)>();

            foreach (var visitor in visitors) {
                // 检查是否使用多输出模式
                var multiOutput = visitor.GenerateMultiple(graph);

                // 空 Dictionary 等价于 null，回退到单输出逻辑（修复点 3）
                // 这使得 visitor 可以动态决定输出模式，返回空字典时不会产生"多输出但无文件"的歧义
                if (multiOutput != null && multiOutput.Count == 0) {
                    multiOutput = null;
                }

                if (multiOutput != null) {
                    // 多输出模式
                    foreach (var (relativePath, content) in multiOutput) {
                        var outputPath = Path.Combine(workspaceRoot, relativePath);

                        if (dryRun) {
                            Console.WriteLine($"   📄 将生成: {relativePath}");
                            generatedFiles.Add((relativePath, true, null));
                        }
                        else {
                            try {
                                // 确保目录存在
                                var dir = Path.GetDirectoryName(outputPath);
                                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) {
                                    Directory.CreateDirectory(dir);
                                }

                                File.WriteAllText(outputPath, content);
                                Console.WriteLine($"   ✅ 已生成: {relativePath}");
                                generatedFiles.Add((relativePath, true, null));
                            }
                            catch (Exception ex) {
                                Console.WriteLine($"   ❌ 生成失败: {relativePath}");
                                Console.WriteLine($"      错误: {ex.Message}");
                                generatedFiles.Add((relativePath, false, ex.Message));
                            }
                        }
                    }
                }
                else {
                    // 单输出模式（原有逻辑）
                    var outputPath = Path.Combine(workspaceRoot, visitor.OutputPath);

                    if (dryRun) {
                        Console.WriteLine($"   📄 将生成: {visitor.OutputPath}");
                        generatedFiles.Add((visitor.OutputPath, true, null));
                    }
                    else {
                        try {
                            var content = visitor.Generate(graph);

                            // 确保目录存在
                            var dir = Path.GetDirectoryName(outputPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) {
                                Directory.CreateDirectory(dir);
                            }

                            File.WriteAllText(outputPath, content);
                            Console.WriteLine($"   ✅ 已生成: {visitor.OutputPath}");
                            generatedFiles.Add((visitor.OutputPath, true, null));
                        }
                        catch (Exception ex) {
                            Console.WriteLine($"   ❌ 生成失败: {visitor.OutputPath}");
                            Console.WriteLine($"      错误: {ex.Message}");
                            generatedFiles.Add((visitor.OutputPath, false, ex.Message));
                        }
                    }
                }
            }

            Console.WriteLine();

            // ===== 输出最终摘要 =====
            PrintFinalSummary(result, generatedFiles, dryRun);

            // 返回退出码
            var anyGenerateFailed = generatedFiles.Any(f => !f.Success);
            if (anyGenerateFailed) { return Task.FromResult(3); }

            var hasWarning = result.Issues.Any(i => i.Severity == IssueSeverity.Warning);
            return Task.FromResult(hasWarning ? 1 : 0);
        }
        catch (Exception ex) {
            Console.Error.WriteLine($"❌ [FATAL] 执行失败: {ex.Message}");
            if (verbose) {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return Task.FromResult(3);
        }
    }

    /// <summary>
    /// 获取所有已注册的 Visitor。
    /// </summary>
    private static IReadOnlyList<IDocumentGraphVisitor> GetVisitors() {
        return
        [
            new GlossaryVisitor(),
            new IssueAggregator(),
            new GoalAggregator(),
            new ReachableDocumentsVisitor()
        ];
    }

    /// <summary>
    /// 打印验证结果摘要。
    /// </summary>
    private static void PrintValidationSummary(ValidationResult result, bool verbose) {
        if (result.Issues.Count == 0) {
            Console.WriteLine("   ✅ 验证通过，无问题");
        }
        else {
            var errorCount = result.Issues.Count(i => i.Severity is IssueSeverity.Error or IssueSeverity.Fatal);
            var warningCount = result.Issues.Count(i => i.Severity == IssueSeverity.Warning);
            var infoCount = result.Issues.Count(i => i.Severity == IssueSeverity.Info);

            Console.Write("   ⚠️  发现问题: ");
            var parts = new List<string>();
            if (errorCount > 0) { parts.Add($"{errorCount} 个错误"); }
            if (warningCount > 0) { parts.Add($"{warningCount} 个警告"); }
            if (infoCount > 0) { parts.Add($"{infoCount} 个提示"); }
            Console.WriteLine(string.Join(", ", parts));

            if (verbose) {
                Console.WriteLine();
                foreach (var issue in result.Issues) {
                    var icon = issue.Severity switch {
                        IssueSeverity.Fatal => "❌",
                        IssueSeverity.Error => "🔴",
                        IssueSeverity.Warning => "🟡",
                        IssueSeverity.Info => "🔵",
                        _ => "❓"
                    };
                    Console.WriteLine($"      {icon} [{issue.ErrorCode}] {issue.Message}");
                    Console.WriteLine($"         {issue.FilePath}");
                }
            }
        }

        // 修复结果
        if (result.FixResults != null && result.FixResults.Count > 0) {
            var successCount = result.FixResults.Count(r => r.Success);
            Console.WriteLine($"   🔧 修复: {successCount}/{result.FixResults.Count} 个操作成功");
        }
    }

    /// <summary>
    /// 打印最终摘要。
    /// </summary>
    private static void PrintFinalSummary(
        ValidationResult result,
        List<(string Path, bool Success, string? Error)> generatedFiles,
        bool dryRun
    ) {
        Console.WriteLine("═══════════════════════════════════════════════════════════");

        if (dryRun) {
            Console.WriteLine("                        预览完成（Dry-Run）");
            Console.WriteLine();
            Console.WriteLine("上述操作未实际执行。移除 --dry-run 参数以执行。");
        }
        else {
            var allSuccess = !result.Issues.Any(i => i.Severity is IssueSeverity.Error or IssueSeverity.Fatal)
                             && generatedFiles.All(f => f.Success);

            if (allSuccess) {
                Console.WriteLine("                        ✅ 全流程完成");
            }
            else {
                Console.WriteLine("                        ⚠️ 完成（有警告或错误）");
            }

            Console.WriteLine();
            Console.WriteLine("生成的文件：");
            foreach (var (path, success, _) in generatedFiles) {
                var icon = success ? "✅" : "❌";
                Console.WriteLine($"  {icon} {path}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════");
    }
}
