using System.CommandLine;
using MemoTree.Cli.Services;

namespace MemoTree.Cli.Commands;

/// <summary>
/// memotree init 命令
/// 在当前目录初始化MemoTree工作空间
/// </summary>
public static class InitCommand {
    public static Command Create() {
        var command = new Command("init", "Initialize a new MemoTree workspace in the current directory");

        command.SetHandler(
            async () => {
                try {
                    var workspaceManager = new WorkspaceManager();
                    var currentDir = Directory.GetCurrentDirectory();

                    // 检查是否已经是工作空间
                    if (workspaceManager.IsWorkspace()) {
                        Console.WriteLine($"MemoTree workspace already exists in {currentDir}");
                        return;
                    }

                    // 初始化工作空间
                    var workspaceRoot = await workspaceManager.InitializeWorkspaceAsync();

                    Console.WriteLine($"Initialized empty MemoTree workspace in {workspaceRoot}");
                    Console.WriteLine();
                    Console.WriteLine("Next steps:");
                    Console.WriteLine("  memotree create \"My First Node\"  # Create your first node");
                    Console.WriteLine("  memotree                          # View the tree structure");
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }
        );

        return command;
    }
}
