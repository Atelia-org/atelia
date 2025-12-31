// DocGraph v0.1 - CLI 入口
// 参考：api.md §8.2 命令行使用

using System.CommandLine;
using Atelia.DocGraph.Commands;

namespace Atelia.DocGraph;

/// <summary>
/// DocGraph CLI 入口点。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("DocGraph - 文档关系图验证和生成工具")
        {
            new ValidateCommand(),
            new FixCommand(),
            new StatsCommand(),
        };

        // 无参数时显示欢迎信息
        rootCommand.SetHandler(() =>
        {
            HelpInfo.PrintWelcome();
        });

        return await rootCommand.InvokeAsync(args);
    }
}
