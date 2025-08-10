using System.CommandLine;
using MemoTree.Cli.Services;
using MemoTree.Core.Storage.Interfaces;
using MemoTree.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MemoTree.Cli.Commands;

public static class IntegrityCommands
{
    public static Command CreateIntegrityRoot()
    {
        var root = new Command("integrity", "Integrity checking commands")
        {
            CreateValidateCommand()
        };
        return root;
    }

    public static Command CreateValidateCommand()
    {
        var cmd = new Command("validate", "Validate storage integrity and print a report");

        cmd.SetHandler(async () =>
        {
            try
            {
                var workspaceManager = new WorkspaceManager();
                var root = workspaceManager.FindWorkspaceRoot();
                if (root == null)
                {
                    Console.Error.WriteLine("Error: Not in a MemoTree workspace. Run 'memotree init' first.");
                    Environment.Exit(1);
                    return;
                }

                var services = new ServiceCollection();
                services.AddLogging(b => b.AddConsole());
                services.AddMemoTreeServices(root);
                var provider = services.BuildServiceProvider();

                var storage = provider.GetRequiredService<ICognitiveNodeStorage>();
                var result = await storage.ValidateIntegrityAsync();

                Console.WriteLine($"Integrity validation at {result.ValidatedAt:u}");
                Console.WriteLine(result.IsValid ? "Status: OK" : "Status: ISSUES FOUND");

                if (result.Warnings.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Warnings ({result.Warnings.Count}):");
                    foreach (var w in result.Warnings)
                        Console.WriteLine($"  - {w}");
                }

                if (result.Errors.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Errors ({result.Errors.Count}):");
                    foreach (var e in result.Errors)
                        Console.WriteLine($"  - {e}");
                }

                Environment.ExitCode = result.IsValid ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        });

        return cmd;
    }
}
