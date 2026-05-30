using System.Text.Json;
using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.E2eCli;

internal static class Program {
    public static int Main(string[] args) {
        try {
            if (args.Length > 0 && IsMetaCommand(args[0])) {
                return args[0] switch {
                    "smoke" => RunSmoke(),
                    "status" => RunStatus(),
                    "help" or "-h" or "--help" => RunHelp(),
                    _ => RunUnknown(args[0]),
                };
            }

            return RunRuntimeCommands(args);
        }
        catch (Exception ex) {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunSmoke() {
        var scaffold = TextAdv2RuntimeScaffold.DescribeCurrentState();
        Console.WriteLine("TextAdv2.E2eCli smoke OK.");
        Console.WriteLine($"Engine assembly: {scaffold.EngineAssemblyName}");
        Console.WriteLine($"Runtime extracted: {scaffold.RuntimeExtracted}");
        return 0;
    }

    private static int RunStatus() {
        var scaffold = TextAdv2RuntimeScaffold.DescribeCurrentState();
        var json = JsonSerializer.Serialize(scaffold, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        return 0;
    }

    private static int RunRuntimeCommands(string[] args) {
        var request = ParseRuntimeRequest(args);
        using var runtime = request.RepoDir is null
            ? TextAdv2Runtime.CreateTemporarySampleWorld()
            : TextAdv2Runtime.OpenOrCreateSampleWorld(request.RepoDir);

        Console.WriteLine($"TextAdv2 runtime repo: {runtime.RepoDir}");

        for (int i = 0; i < request.Commands.Length; i++) {
            var command = request.Commands[i];
            var result = runtime.Execute(command);

            Console.WriteLine();
            if (request.Commands.Length > 1) {
                Console.WriteLine($"[{i + 1}/{request.Commands.Length}] {DescribeCommand(command)}");
            }

            Console.WriteLine(result.Output);
        }

        return 0;
    }

    private static RuntimeRequest ParseRuntimeRequest(string[] args) {
        string? repoDir = null;
        var commands = new List<TextAdv2RuntimeCommand>();
        int index = 0;

        while (index < args.Length) {
            switch (args[index]) {
                case "--repo-dir":
                    repoDir = RequireArg(args, index + 1);
                    index += 2;
                    break;
                case "--world":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.World));
                    index += 1;
                    break;
                case "--location":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.Location, RequireArg(args, index + 1)));
                    index += 2;
                    break;
                case "--observe-location":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveLocation, RequireArg(args, index + 1)));
                    index += 2;
                    break;
                case "--observe-actor":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveActor, RequireArg(args, index + 1)));
                    index += 2;
                    break;
                case "--observe-navigation":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveNavigation, RequireArg(args, index + 1)));
                    index += 2;
                    break;
                case "--observe-actor-navigation":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveActorNavigation, RequireArg(args, index + 1)));
                    index += 2;
                    break;
                case "--observe-route-acceleration":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveRouteAcceleration));
                    index += 1;
                    break;
                case "--observe-time":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveTime));
                    index += 1;
                    break;
                case "--advance-time":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.AdvanceTime, RequireArg(args, index + 1)));
                    index += 2;
                    break;
                case "--plan-actor-route":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.PlanActorRoute, RequireArg(args, index + 1), RequireArg(args, index + 2)));
                    index += 3;
                    break;
                case "--plan-route":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.PlanRoute, RequireArg(args, index + 1), RequireArg(args, index + 2)));
                    index += 3;
                    break;
                case "--rebuild-route-acceleration":
                    string? rebuildLandmarks = TryReadOptionalArg(args, index + 1);
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.RebuildRouteAcceleration, rebuildLandmarks));
                    index += rebuildLandmarks is null ? 1 : 2;
                    break;
                case "--trace-actor-route":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.TraceActorRoute, RequireArg(args, index + 1)));
                    index += 2;
                    break;
                case "--move-actor-quiet":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.MoveActorQuiet, RequireArg(args, index + 1), RequireArg(args, index + 2)));
                    index += 3;
                    break;
                case "--move-actor":
                    commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.MoveActor, RequireArg(args, index + 1), RequireArg(args, index + 2)));
                    index += 3;
                    break;
                default:
                    throw new InvalidOperationException(BuildUsage());
            }
        }

        if (commands.Count == 0) {
            commands.Add(new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.World));
        }

        return new RuntimeRequest(repoDir, commands.ToArray());
    }

    private static bool IsMetaCommand(string command)
        => command is "smoke" or "status" or "help" or "-h" or "--help";

    private static string RequireArg(string[] args, int index)
        => index < args.Length ? args[index] : throw new InvalidOperationException(BuildUsage());

    private static string? TryReadOptionalArg(string[] args, int index)
        => index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal) ? args[index] : null;

    private static string DescribeCommand(TextAdv2RuntimeCommand command)
        => command.Mode switch {
            TextAdv2RuntimeCommandMode.World => "world dump",
            TextAdv2RuntimeCommandMode.Location => $"location dump {command.Arg1}",
            TextAdv2RuntimeCommandMode.ObserveLocation => $"observe location {command.Arg1}",
            TextAdv2RuntimeCommandMode.ObserveActor => $"observe actor {command.Arg1}",
            TextAdv2RuntimeCommandMode.ObserveNavigation => $"observe navigation {command.Arg1}",
            TextAdv2RuntimeCommandMode.ObserveActorNavigation => $"observe actor navigation {command.Arg1}",
            TextAdv2RuntimeCommandMode.ObserveRouteAcceleration => "observe route acceleration",
            TextAdv2RuntimeCommandMode.ObserveTime => "observe logical time",
            TextAdv2RuntimeCommandMode.AdvanceTime => $"advance logical time by {command.Arg1}",
            TextAdv2RuntimeCommandMode.PlanActorRoute => $"plan actor route {command.Arg1} -> {command.Arg2}",
            TextAdv2RuntimeCommandMode.PlanRoute => $"plan route {command.Arg1} -> {command.Arg2}",
            TextAdv2RuntimeCommandMode.RebuildRouteAcceleration => $"rebuild route acceleration {(command.Arg1 is null ? "default-profile" : command.Arg1)}",
            TextAdv2RuntimeCommandMode.TraceActorRoute => $"trace actor route {command.Arg1}",
            TextAdv2RuntimeCommandMode.MoveActorQuiet => $"move actor quietly {command.Arg1} via {command.Arg2}",
            TextAdv2RuntimeCommandMode.MoveActor => $"move actor {command.Arg1} via {command.Arg2}",
            _ => command.Mode.ToString(),
        };

    private static string BuildUsage()
        => "Usage: dotnet run --project prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj"
            + " [smoke|status|help]"
            + " [--repo-dir <repoDir>]"
            + " [--world]"
            + " [--location <locationId>]"
            + " [--observe-location <locationId>]"
            + " [--observe-actor <actorId>]"
            + " [--observe-navigation <locationId>]"
            + " [--observe-actor-navigation <actorId>]"
            + " [--observe-route-acceleration]"
            + " [--observe-time]"
            + " [--advance-time <ticks>]"
            + " [--plan-actor-route <actorId> <toLocationId>]"
            + " [--plan-route <fromLocationId> <toLocationId>]"
            + " [--rebuild-route-acceleration [<locationId[,locationId...]>|default]]"
            + " [--trace-actor-route <actorId>]"
            + " [--move-actor-quiet <actorId> <passageId>]"
            + " [--move-actor <actorId> <passageId>]";

    private static int RunHelp() {
        Console.WriteLine(
            """
TextAdv2.E2eCli

Commands:
  smoke   Validate the host project starts and can reference Atelia.TextAdv2.
  status  Print the current runtime scaffold state as JSON.
  runtime  Omit a meta command and use the legacy TextAdv2 option-style commands below.
  help    Show this message.

Runtime options:
  --repo-dir <repoDir>
           Open or create a persistent sample world at the specified directory.
           If omitted, a temporary sample world is created for this invocation.
    --rebuild-route-acceleration
                     Without an argument, rebuild using the world's recommended landmark profile when available.
    --rebuild-route-acceleration default
                     Equivalent to omitting the argument.
"""
        );
        Console.WriteLine(BuildUsage());
        return 0;
    }

    private static int RunUnknown(string command) {
        Console.Error.WriteLine($"Unknown command: {command}");
        _ = RunHelp();
        return 1;
    }

    private sealed record RuntimeRequest(string? RepoDir, TextAdv2RuntimeCommand[] Commands);
}
