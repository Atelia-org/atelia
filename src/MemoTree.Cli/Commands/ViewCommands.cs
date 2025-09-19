using System.CommandLine;
using MemoTree.Cli.Services;
using MemoTree.Services;
using MemoTree.Core.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MemoTree.Cli.Commands;

public static class ViewCommands {
    public static Command CreateExpandCommand() {
        var idArg = new Argument<string>("id", "Node ID to expand");
        var cmd = new Command("expand", "Expand a node in the current view") { idArg };

        cmd.SetHandler(
            async (string id) => {
                try {
                    var workspaceManager = new WorkspaceManager();
                    var root = workspaceManager.FindWorkspaceRoot();
                    if (root == null) {
                        Console.Error.WriteLine("Error: Not in a MemoTree workspace. Run 'memotree init' first.");
                        Environment.Exit(1);
                        return;
                    }
                    var nodeId = NodeId.FromString(id);
                    var services = new ServiceCollection();
                    services.AddLogging(b => b.AddConsole());
                    services.AddMemoTreeServices(root);
                    var provider = services.BuildServiceProvider();
                    var svc = provider.GetRequiredService<IMemoTreeService>();

                    await svc.ExpandNodeAsync(nodeId, "default");
                    var output = await svc.RenderViewAsync("default");
                    Console.WriteLine(output);
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, idArg
        );

        return cmd;
    }

    public static Command CreateCollapseCommand() {
        var idArg = new Argument<string>("id", "Node ID to collapse");
        var cmd = new Command("collapse", "Collapse a node in the current view") { idArg };

        cmd.SetHandler(
            async (string id) => {
                try {
                    var workspaceManager = new WorkspaceManager();
                    var root = workspaceManager.FindWorkspaceRoot();
                    if (root == null) {
                        Console.Error.WriteLine("Error: Not in a MemoTree workspace. Run 'memotree init' first.");
                        Environment.Exit(1);
                        return;
                    }
                    var nodeId = NodeId.FromString(id);
                    var services = new ServiceCollection();
                    services.AddLogging(b => b.AddConsole());
                    services.AddMemoTreeServices(root);
                    var provider = services.BuildServiceProvider();
                    var svc = provider.GetRequiredService<IMemoTreeService>();

                    await svc.CollapseNodeAsync(nodeId, "default");
                    var output = await svc.RenderViewAsync("default");
                    Console.WriteLine(output);
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, idArg
        );

        return cmd;
    }

    public static Command CreateViewCreateCommand() {
        var nameArg = new Argument<string>("name", "View name to create");
        var descOpt = new Option<string?>(new[] { "--description", "-d" }, () => null, "Optional description for the view");
        var cmd = new Command("create", "Create a new view") { nameArg, descOpt };

        cmd.SetHandler(
            async (string name, string? description) => {
                try {
                    var workspaceManager = new WorkspaceManager();
                    var root = workspaceManager.FindWorkspaceRoot();
                    if (root == null) {
                        Console.Error.WriteLine("Error: Not in a MemoTree workspace. Run 'memotree init' first.");
                        Environment.Exit(1);
                        return;
                    }
                    var services = new ServiceCollection();
                    services.AddLogging(b => b.AddConsole());
                    services.AddMemoTreeServices(root);
                    var provider = services.BuildServiceProvider();
                    var svc = provider.GetRequiredService<IMemoTreeService>();

                    await svc.CreateViewAsync(name, description);
                    var output = await svc.RenderViewAsync(name);
                    Console.WriteLine(output);
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, nameArg, descOpt
        );

        return cmd;
    }

    public static Command CreateViewSetDescriptionCommand() {
        var nameArg = new Argument<string>("name", "View name to update");
        var descArg = new Argument<string>("description", "New description");
        var cmd = new Command("set-description", "Update description of a view") { nameArg, descArg };

        cmd.SetHandler(
            async (string name, string description) => {
                try {
                    var workspaceManager = new WorkspaceManager();
                    var root = workspaceManager.FindWorkspaceRoot();
                    if (root == null) {
                        Console.Error.WriteLine("Error: Not in a MemoTree workspace. Run 'memotree init' first.");
                        Environment.Exit(1);
                        return;
                    }
                    var services = new ServiceCollection();
                    services.AddLogging(b => b.AddConsole());
                    services.AddMemoTreeServices(root);
                    var provider = services.BuildServiceProvider();
                    var svc = provider.GetRequiredService<IMemoTreeService>();

                    await svc.UpdateViewDescriptionAsync(name, description);
                    var output = await svc.RenderViewAsync(name);
                    Console.WriteLine(output);
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, nameArg, descArg
        );

        return cmd;
    }
}
