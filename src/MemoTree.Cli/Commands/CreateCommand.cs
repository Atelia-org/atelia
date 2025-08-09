using System.CommandLine;
using MemoTree.Cli.Services;
using MemoTree.Core.Types;
using MemoTree.Services;

namespace MemoTree.Cli.Commands;

/// <summary>
/// memotree create 命令
/// 创建新的认知节点
/// </summary>
public static class CreateCommand
{
    public static Command Create()
    {
        var titleArgument = new Argument<string>("title", "The title of the new node");
        var parentOption = new Option<string?>("--parent", "Parent node ID (optional)") { IsRequired = false };
        var typeOption = new Option<string>("--type", () => "concept", "Node type (concept, entity, process, attribute)");
        var contentOption = new Option<bool>("--content", () => false, "Open editor for content input");

        var command = new Command("create", "Create a new cognitive node")
        {
            titleArgument,
            parentOption,
            typeOption,
            contentOption
        };

        command.SetHandler(async (string title, string? parentIdStr, string typeStr, bool openEditor) =>
        {
            try
            {
                var workspaceManager = new WorkspaceManager();
                var workspaceRoot = workspaceManager.FindWorkspaceRoot();
                
                if (workspaceRoot == null)
                {
                    Console.Error.WriteLine("Error: Not in a MemoTree workspace. Run 'memotree init' first.");
                    Environment.Exit(1);
                    return;
                }

                // 解析节点类型
                if (!Enum.TryParse<NodeType>(typeStr, true, out var nodeType))
                {
                    Console.Error.WriteLine($"Error: Invalid node type '{typeStr}'. Valid types: concept, entity, process, attribute");
                    Environment.Exit(1);
                    return;
                }

                // 解析父节点ID
                NodeId? parentId = null;
                if (!string.IsNullOrEmpty(parentIdStr))
                {
                    try
                    {
                        parentId = NodeId.FromString(parentIdStr);
                    }
                    catch
                    {
                        Console.Error.WriteLine($"Error: Invalid parent node ID '{parentIdStr}'");
                        Environment.Exit(1);
                        return;
                    }
                }

                // 获取内容
                string content = "";
                if (openEditor)
                {
                    content = await GetContentFromEditorAsync();
                }
                else
                {
                    // 检查是否有管道输入
                    if (!Console.IsInputRedirected)
                    {
                        Console.WriteLine("Enter content (press Ctrl+D or Ctrl+Z to finish):");
                    }
                    
                    var lines = new List<string>();
                    string? line;
                    while ((line = Console.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                    content = string.Join(Environment.NewLine, lines);
                }

                // TODO: 这里需要实际的服务实现
                // 现在先创建一个模拟的节点
                var newNode = CognitiveNode.Create(nodeType, title);
                
                Console.WriteLine($"Created node: {newNode.Metadata.Id}");
                Console.WriteLine($"Title: {newNode.Metadata.Title}");
                // TODO: 添加父节点和内容设置逻辑
                Console.WriteLine($"Type: {newNode.Metadata.Type}");
                if (!string.IsNullOrEmpty(content))
                {
                    Console.WriteLine($"Content: {content.Length} characters");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, titleArgument, parentOption, typeOption, contentOption);

        return command;
    }

    private static async Task<string> GetContentFromEditorAsync()
    {
        // 创建临时文件
        var tempFile = Path.GetTempFileName();
        try
        {
            // 获取默认编辑器
            var editor = Environment.GetEnvironmentVariable("EDITOR") ?? 
                        Environment.GetEnvironmentVariable("VISUAL") ?? 
                        (OperatingSystem.IsWindows() ? "notepad" : "nano");

            // 启动编辑器
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editor,
                Arguments = tempFile,
                UseShellExecute = true
            });

            if (process != null)
            {
                await process.WaitForExitAsync();
                
                if (File.Exists(tempFile))
                {
                    return await File.ReadAllTextAsync(tempFile);
                }
            }

            return "";
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
