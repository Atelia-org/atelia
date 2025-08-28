using System.CommandLine;
using MemoTree.Cli.Services;

namespace MemoTree.Cli.Commands {
    /// <summary>
    /// Connect命令：连接到另一个MemoTree工作空间
    /// </summary>
    public static class ConnectCommand {
        public static Command Create() {
            var targetPathArgument = new Argument<string>(
                name: "target-path",
                description: "目标工作空间的路径"
            );

            var command = new Command("connect", "连接到另一个MemoTree工作空间")
            {
                targetPathArgument
 };

            command.SetHandler(async (string targetPath) => {
                try {
                    await ExecuteAsync(targetPath);
                } catch (Exception ex) {
                    Console.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, targetPathArgument
            );

            return command;
        }

        private static async Task ExecuteAsync(string targetPath) {
            var workspaceManager = new WorkspaceManager();
            var fullTargetPath = Path.GetFullPath(targetPath);

            // 复用 WorkspaceManager 的连接与校验逻辑
            var connectedRoot = await workspaceManager.ConnectWorkspaceAsync(fullTargetPath);

            var linkConfigPath = Path.Combine(connectedRoot, ".memotree", "link.json");
            Console.WriteLine($"✅ 成功连接到工作空间: {fullTargetPath}");
            Console.WriteLine($"📁 链接配置已保存到: {linkConfigPath}");
            Console.WriteLine();
            Console.WriteLine("现在你可以在当前目录使用MemoTree命令，它们将操作目标工作空间的数据。");
            Console.WriteLine();
            Console.WriteLine("下一步:");
            Console.WriteLine("  memotree                    # 查看目标工作空间的内容");
            Console.WriteLine("  memotree create \"新节点\"    # 在目标工作空间创建节点");
        }
    }
}
