using System.CommandLine;
using MemoTree.Cli.Services;
using MemoTree.Core.Storage.Interfaces;
using MemoTree.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MemoTree.Cli.Commands;

public static class IntegrityCommands {
    public static Command CreateIntegrityRoot() {
        var root = new Command("integrity", "Integrity checking commands")
        {
            CreateValidateCommand(),
            CreateRepairCommand()
 };
        return root;
    }

    public static Command CreateValidateCommand() {
        var jsonOpt = new Option<bool>("--json", () => false, description: "Output result as JSON");
        var cmd = new Command("validate", "Validate storage integrity and print a report") { jsonOpt };

        cmd.SetHandler(
            async (bool json) => {
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

                    var storage = provider.GetRequiredService<ICognitiveNodeStorage>();
                    var result = await storage.ValidateIntegrityAsync();

                    if (json) {
                        var payload = new {
                            validatedAt = result.ValidatedAt,
                            isValid = result.IsValid,
                            warnings = result.Warnings,
                            errors = result.Errors
                        };
                        var text = System.Text.Json.JsonSerializer.Serialize(payload,
                            new System.Text.Json.JsonSerializerOptions {
                                WriteIndented = true
                            }
                        );
                        Console.WriteLine(text);
                    } else {
                        Console.WriteLine($"Integrity validation at {result.ValidatedAt:u}");
                        Console.WriteLine(result.IsValid ? "Status: OK" : "Status: ISSUES FOUND");

                        if (result.Warnings.Count > 0) {
                            Console.WriteLine();
                            Console.WriteLine($"Warnings ({result.Warnings.Count}):");
                            foreach (var w in result.Warnings) {
                                Console.WriteLine($"  - {w}");
                            }
                        }

                        if (result.Errors.Count > 0) {
                            Console.WriteLine();
                            Console.WriteLine($"Errors ({result.Errors.Count}):");
                            foreach (var e in result.Errors) {
                                Console.WriteLine($"  - {e}");
                            }
                        }
                    }

                    Environment.ExitCode = result.IsValid ? 0 : 2;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, jsonOpt
        );

        return cmd;
    }

    public static Command CreateRepairCommand() {
        var dryRun = new Option<bool>("--dry-run", () => true, "Show planned fixes without changing files");
        var cmd = new Command("repair", "Scan and propose repairs for common integrity issues (preview)") { dryRun };
        cmd.SetHandler(
            async (bool _dryRun) => {
                Console.WriteLine("Repair is not implemented yet. Use 'integrity validate --json' to inspect issues.");
                Environment.ExitCode = 3;
                await Task.CompletedTask;
            }, dryRun
        );
        return cmd;
    }
}
