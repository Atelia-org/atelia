using System.CommandLine;
using System.Text.Json;

namespace MemoTree.Cli.Commands
{
    /// <summary>
    /// Connect命令：连接到另一个MemoTree工作空间
    /// </summary>
    public static class ConnectCommand
    {
        public static Command Create()
        {
            var targetPathArgument = new Argument<string>(
                name: "target-path",
                description: "目标工作空间的路径");

            var command = new Command("connect", "连接到另一个MemoTree工作空间")
            {
                targetPathArgument
            };

            command.SetHandler(async (string targetPath) =>
            {
                try
                {
                    await ExecuteAsync(targetPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, targetPathArgument);

            return command;
        }

        private static async Task ExecuteAsync(string targetPath)
        {
            // 验证目标路径
            var fullTargetPath = Path.GetFullPath(targetPath);
            if (!Directory.Exists(fullTargetPath))
            {
                throw new DirectoryNotFoundException($"目标路径不存在: {fullTargetPath}");
            }

            // 检查目标路径是否为有效的MemoTree工作空间
            var targetWorkspaceDir = Path.Combine(fullTargetPath, ".memotree");
            if (!Directory.Exists(targetWorkspaceDir))
            {
                throw new InvalidOperationException($"目标路径不是有效的MemoTree工作空间: {fullTargetPath}");
            }

            // 确保当前目录有.memotree目录
            var currentDir = Directory.GetCurrentDirectory();
            var currentWorkspaceDir = Path.Combine(currentDir, ".memotree");
            
            if (!Directory.Exists(currentWorkspaceDir))
            {
                // 如果当前目录没有.memotree，创建一个
                Directory.CreateDirectory(currentWorkspaceDir);
                Console.WriteLine("Created .memotree directory in current location");
            }

            // 创建链接配置
            var linkConfig = new
            {
                target = fullTargetPath,
                created = DateTime.UtcNow,
                description = $"Link to MemoTree workspace at {fullTargetPath}"
            };

            var linkConfigPath = Path.Combine(currentWorkspaceDir, "link.json");
            var linkConfigJson = JsonSerializer.Serialize(linkConfig, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });

            await File.WriteAllTextAsync(linkConfigPath, linkConfigJson);

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
