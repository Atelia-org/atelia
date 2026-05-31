using System.Text.Json;
using System.Text.Json.Serialization;
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
        using var runtime = request.BootstrapMode switch {
            RuntimeBootstrapMode.RepoDir => TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(request.RepoDir!),
            RuntimeBootstrapMode.DevSampleWorld => TextAdv2SampleWorldDevBootstrap.CreateTemporaryRuntime(),
            _ => throw new InvalidOperationException("Unsupported runtime bootstrap mode."),
        };

        Console.WriteLine($"TextAdv2 runtime repo: {runtime.RepoDir}");

        for (int i = 0; i < request.Operations.Length; i++) {
            var operation = request.Operations[i];
            string output = operation.Execute(runtime);

            Console.WriteLine();
            if (request.Operations.Length > 1) {
                Console.WriteLine($"[{i + 1}/{request.Operations.Length}] {operation.Description}");
            }

            Console.WriteLine(output);
        }

        return 0;
    }

    private static RuntimeRequest ParseRuntimeRequest(string[] args) {
        string? repoDir = null;
        bool useDevSampleWorld = false;
        var operations = new List<RuntimeOperation>();
        int index = 0;

        while (index < args.Length) {
            switch (args[index]) {
                case "--repo-dir":
                    repoDir = RequireArg(args, index + 1);
                    index += 2;
                    break;
                case "--dev-sample-world":
                    useDevSampleWorld = true;
                    index += 1;
                    break;
                case "--world":
                    operations.Add(new RuntimeOperation("world dump", TextAdv2RuntimeDevTextRenderer.RenderWorld));
                    index += 1;
                    break;
                case "--location":
                    {
                        string locationId = RequireArg(args, index + 1);
                        operations.Add(
                            new RuntimeOperation(
                                $"location dump {locationId}",
                                runtime => TextAdv2RuntimeDevTextRenderer.RenderLocation(runtime, locationId)
                            )
                        );
                    }
                    index += 2;
                    break;
                case "--observe-location":
                    {
                        string locationId = RequireArg(args, index + 1);
                        operations.Add(new RuntimeOperation($"observe location {locationId}", runtime => RenderJson(runtime.ObserveLocation(locationId))));
                    }
                    index += 2;
                    break;
                case "--observe-actor":
                    {
                        string actorId = RequireArg(args, index + 1);
                        operations.Add(new RuntimeOperation($"observe actor {actorId}", runtime => RenderJson(runtime.ObserveActor(actorId))));
                    }
                    index += 2;
                    break;
                case "--observe-navigation":
                    {
                        string locationId = RequireArg(args, index + 1);
                        operations.Add(new RuntimeOperation($"observe navigation {locationId}", runtime => RenderJson(runtime.ObserveNavigation(locationId))));
                    }
                    index += 2;
                    break;
                case "--observe-actor-navigation":
                    {
                        string actorId = RequireArg(args, index + 1);
                        operations.Add(new RuntimeOperation($"observe actor navigation {actorId}", runtime => RenderJson(runtime.ObserveActorNavigation(actorId))));
                    }
                    index += 2;
                    break;
                case "--observe-route-acceleration":
                    operations.Add(new RuntimeOperation("observe route acceleration", static runtime => RenderJson(runtime.ObserveRouteAcceleration())));
                    index += 1;
                    break;
                case "--observe-time":
                    operations.Add(new RuntimeOperation("observe logical time", static runtime => RenderJson(runtime.ObserveTime())));
                    index += 1;
                    break;
                case "--advance-time":
                    {
                        string ticksText = RequireArg(args, index + 1);
                        long ticks = ParseNonNegativeTickDelta(ticksText);
                        operations.Add(new RuntimeOperation($"advance logical time by {ticksText}", runtime => RenderJson(runtime.AdvanceTime(ticks))));
                    }
                    index += 2;
                    break;
                case "--plan-actor-route":
                    {
                        string actorId = RequireArg(args, index + 1);
                        string toLocationId = RequireArg(args, index + 2);
                        operations.Add(new RuntimeOperation($"plan actor route {actorId} -> {toLocationId}", runtime => RenderJson(runtime.PlanActorRoute(actorId, toLocationId))));
                    }
                    index += 3;
                    break;
                case "--plan-route":
                    {
                        string fromLocationId = RequireArg(args, index + 1);
                        string toLocationId = RequireArg(args, index + 2);
                        operations.Add(new RuntimeOperation($"plan route {fromLocationId} -> {toLocationId}", runtime => RenderJson(runtime.PlanRoute(fromLocationId, toLocationId))));
                    }
                    index += 3;
                    break;
                case "--rebuild-route-acceleration":
                    {
                        string? rebuildLandmarks = TryReadOptionalArg(args, index + 1);
                        operations.Add(
                            new RuntimeOperation(
                                $"rebuild route acceleration {(rebuildLandmarks is null ? "default-profile" : rebuildLandmarks)}",
                                runtime => RenderJson(TextAdv2SampleWorldDevBootstrap.RebuildRouteAcceleration(runtime, rebuildLandmarks))
                            )
                        );
                        index += rebuildLandmarks is null ? 1 : 2;
                    }
                    break;
                case "--trace-actor-route":
                    {
                        string actorId = RequireArg(args, index + 1);
                        operations.Add(
                            new RuntimeOperation(
                                $"trace actor route {actorId}",
                                runtime => TextAdv2RuntimeDevTextRenderer.RenderRouteTrace(runtime.TraceActorRoute(actorId))
                            )
                        );
                    }
                    index += 2;
                    break;
                case "--move-actor-quiet":
                    {
                        string actorId = RequireArg(args, index + 1);
                        string passageId = RequireArg(args, index + 2);
                        operations.Add(
                            new RuntimeOperation(
                                $"move actor quietly {actorId} via {passageId}",
                                runtime => TextAdv2RuntimeDevTextRenderer.RenderCompactMovement(runtime.MoveActor(actorId, passageId))
                            )
                        );
                    }
                    index += 3;
                    break;
                case "--move-actor":
                    {
                        string actorId = RequireArg(args, index + 1);
                        string passageId = RequireArg(args, index + 2);
                        operations.Add(new RuntimeOperation($"move actor {actorId} via {passageId}", runtime => RenderJson(runtime.MoveActor(actorId, passageId))));
                    }
                    index += 3;
                    break;
                default:
                    throw new InvalidOperationException(BuildUsage());
            }
        }

        if (operations.Count == 0) {
            operations.Add(new RuntimeOperation("world dump", TextAdv2RuntimeDevTextRenderer.RenderWorld));
        }

        return new RuntimeRequest(
            ResolveBootstrapMode(repoDir, useDevSampleWorld),
            repoDir,
            operations.ToArray()
        );
    }

    private static bool IsMetaCommand(string command)
        => command is "smoke" or "status" or "help" or "-h" or "--help";

    private static string RequireArg(string[] args, int index)
        => index < args.Length ? args[index] : throw new InvalidOperationException(BuildUsage());

    private static string? TryReadOptionalArg(string[] args, int index)
        => index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal) ? args[index] : null;

    private static RuntimeBootstrapMode ResolveBootstrapMode(string? repoDir, bool useDevSampleWorld) {
        if (repoDir is not null && useDevSampleWorld) {
            throw new InvalidOperationException(RepoDirAndDevSampleWorldConflictError);
        }

        if (repoDir is null && !useDevSampleWorld) {
            throw new InvalidOperationException(MissingRuntimeTargetError);
        }

        return useDevSampleWorld ? RuntimeBootstrapMode.DevSampleWorld : RuntimeBootstrapMode.RepoDir;
    }

    private static string RenderJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static long ParseNonNegativeTickDelta(string value) {
        if (!long.TryParse(value, out long ticks)) { throw new InvalidOperationException($"AdvanceTime requires an integer tick delta, but received '{value}'."); }

        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        return ticks;
    }

    private static string BuildUsage()
        => """
Usage:
  dotnet run --project prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj [smoke|status|help]
  dotnet run --project prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj (--repo-dir <repoDir> | --dev-sample-world) [--world] [--location <locationId>] [--observe-location <locationId>] [--observe-actor <actorId>] [--observe-navigation <locationId>] [--observe-actor-navigation <actorId>] [--observe-route-acceleration] [--observe-time] [--advance-time <ticks>] [--plan-actor-route <actorId> <toLocationId>] [--plan-route <fromLocationId> <toLocationId>] [--rebuild-route-acceleration [<locationId[,locationId...]>|default]] [--trace-actor-route <actorId>] [--move-actor-quiet <actorId> <passageId>] [--move-actor <actorId> <passageId>]
""";

    private static int RunHelp() {
        Console.WriteLine(
            """
TextAdv2.E2eCli

Meta commands:
  smoke   Validate the host project starts and can reference Atelia.TextAdv2.
  status  Print the current runtime scaffold state as JSON.
  help    Show this message.

Runtime target:
  --repo-dir <repoDir>
           Open or create a persistent sample world at the specified directory.
  --dev-sample-world
           Create a dev sample world under the system temp directory for this invocation.

           Exactly one runtime target must be specified.

Runtime options:
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

    private static JsonSerializerOptions CreateJsonOptions() {
        var options = new JsonSerializerOptions {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private const string MissingRuntimeTargetError = "缺少 runtime target，请传 --repo-dir <repoDir> 或 --dev-sample-world";
    private const string RepoDirAndDevSampleWorldConflictError = "--repo-dir 不能与 --dev-sample-world 同时使用";

    private sealed record RuntimeRequest(
        RuntimeBootstrapMode BootstrapMode,
        string? RepoDir,
        RuntimeOperation[] Operations
    );

    private enum RuntimeBootstrapMode {
        RepoDir,
        DevSampleWorld,
    }

    private sealed record RuntimeOperation(
        string Description,
        Func<TextAdv2Runtime, string> Execute
    );
}
