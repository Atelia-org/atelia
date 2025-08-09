using System.CommandLine;
using MemoTree.Cli.Commands;
using MemoTree.Cli.Services;

namespace MemoTree.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // 创建根命令
        var rootCommand = new RootCommand("MemoTree - Hierarchical context management for LLMs")
        {
            InitCommand.Create(),
            CreateCommand.Create()
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

            // TODO: 实现实际的渲染逻辑
            Console.WriteLine("# MemoTree Workspace");
            Console.WriteLine();
            Console.WriteLine($"**Workspace**: {workspaceRoot}");
            Console.WriteLine($"**Status**: Empty workspace (no nodes yet)");
            Console.WriteLine();
            Console.WriteLine("Create your first node with:");
            Console.WriteLine("```");
            Console.WriteLine("memotree create \"My First Node\"");
            Console.WriteLine("```");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
