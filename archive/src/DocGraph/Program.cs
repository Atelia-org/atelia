// DocGraph v0.1 - CLI 入口
// 参考：api.md §8.2 命令行使用

using System.CommandLine;
using Atelia.DocGraph.Commands;

namespace Atelia.DocGraph;

/// <summary>
/// DocGraph CLI 入口点。
/// 默认行为：无参数时执行全流程（validate + fix + generate）。
/// </summary>
public static class Program {
    public static async Task<int> Main(string[] args) {
        var rootCommand = new RootCommand("DocGraph - 文档关系图验证和生成工具")
        {
            new ValidateCommand(),
            new FixCommand(),
            new StatsCommand(),
            new RunCommand(),
        };

        // 添加根命令的选项（用于默认行为）
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

        rootCommand.AddArgument(pathArgument);
        rootCommand.AddOption(dryRunOption);
        rootCommand.AddOption(yesOption);
        rootCommand.AddOption(verboseOption);
        rootCommand.AddOption(forceOption);

        // 默认行为：执行全流程
        rootCommand.SetHandler(
            RunCommand.ExecuteAsync,
            pathArgument, dryRunOption, yesOption, verboseOption, forceOption
        );

        return await rootCommand.InvokeAsync(args);
    }
}
