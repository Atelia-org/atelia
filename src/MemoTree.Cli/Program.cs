using System.CommandLine;
using MemoTree.Cli.Commands;
using MemoTree.Cli.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MemoTree.Services;

namespace MemoTree.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // 创建根命令
        var rootCommand = new RootCommand("MemoTree - Hierarchical context management for LLMs")
        {
            InitCommand.Create(),
            CreateCommand.Create(),
            ViewCommands.CreateExpandCommand(),
            ViewCommands.CreateCollapseCommand()
        };

        // 如果没有参数，默认执行渲染命令
        if (args.Length == 0)
        {
            return await HandleDefaultRenderAsync();
        }

        // 执行命令
        return await rootCommand.InvokeAsync(args);
    }

    /// <summary>
    /// 处理默认的渲染命令 (memotree 不带参数)
    /// </summary>
    private static async Task<int> HandleDefaultRenderAsync()
    {
        try
        {
            var workspaceManager = new WorkspaceManager();
            var workspaceRoot = workspaceManager.FindWorkspaceRoot();
            
            if (workspaceRoot == null)
            {
                Console.Error.WriteLine("Error: Not in a MemoTree workspace.");
                Console.Error.WriteLine("Run 'memotree init' to initialize a workspace, or");
                Console.Error.WriteLine("Run 'memotree --help' to see available commands.");
                return 1;
            }

            // 使用服务层渲染视图
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLogging(b => b.AddConsole());
            services.AddMemoTreeServices(workspaceRoot);
            var provider = services.BuildServiceProvider();

            var svc = provider.GetRequiredService<IMemoTreeService>();
            var output = await svc.RenderViewAsync("default");
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
