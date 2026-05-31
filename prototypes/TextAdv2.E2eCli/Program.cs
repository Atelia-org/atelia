using System.Text.Json;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Session;

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

            return RunSessionCommands(args);
        }
        catch (Exception ex) {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunSmoke() {
        var scaffold = HostingScaffold.DescribeCurrentState();
        Console.WriteLine("TextAdv2.E2eCli smoke OK.");
        Console.WriteLine($"Engine assembly: {scaffold.EngineAssemblyName}");
        Console.WriteLine($"Session extracted: {scaffold.SessionExtracted}");
        return 0;
    }

    private static int RunStatus() {
        var scaffold = HostingScaffold.DescribeCurrentState();
        var json = JsonSerializer.Serialize(scaffold, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        return 0;
    }

    private static int RunSessionCommands(string[] args) {
        var request = ParseSessionRequest(args);
        using var session = request.BootstrapMode switch {
            SessionBootstrapMode.RepoDir => SampleWorldBootstrap.OpenOrCreateSession(request.RepoDir!),
            SessionBootstrapMode.DevSampleWorld => SampleWorldBootstrap.CreateTemporarySession(),
            _ => throw new InvalidOperationException("Unsupported session bootstrap mode."),
        };

        Console.WriteLine($"TextAdv2 session repo: {session.RepoDir}");

        for (int i = 0; i < request.Operations.Length; i++) {
            var operation = request.Operations[i];
            string output = operation.Execute(session);

            Console.WriteLine();
            if (request.Operations.Length > 1) {
                Console.WriteLine($"[{i + 1}/{request.Operations.Length}] {operation.Description}");
            }

            Console.WriteLine(output);
        }

        return 0;
    }

    private static SessionRequest ParseSessionRequest(string[] args) {
        string? repoDir = null;
        bool useDevSampleWorld = false;
        var operations = new List<SessionCommand>();
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
                    operations.Add(new SessionCommand("world dump", DevTextRenderer.RenderWorld));
                    index += 1;
                    break;
                case "--location": {
                    string locationId = RequireArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"location dump {locationId}",
                            session => DevTextRenderer.RenderLocation(session, locationId)
                        )
                    );
                }
                index += 2;
                break;
                case "--observe-location": {
                    string locationId = RequireArg(args, index + 1);
                    operations.Add(new SessionCommand($"observe location {locationId}", session => RenderJson(session.ObserveLocation(locationId))));
                }
                index += 2;
                break;
                case "--observe-actor": {
                    string actorId = RequireArg(args, index + 1);
                    operations.Add(new SessionCommand($"observe actor {actorId}", session => RenderJson(session.ObserveActor(actorId))));
                }
                index += 2;
                break;
                case "--observe-navigation": {
                    string locationId = RequireArg(args, index + 1);
                    operations.Add(new SessionCommand($"observe navigation {locationId}", session => RenderJson(session.ObserveNavigation(locationId))));
                }
                index += 2;
                break;
                case "--observe-actor-navigation": {
                    string actorId = RequireArg(args, index + 1);
                    operations.Add(new SessionCommand($"observe actor navigation {actorId}", session => RenderJson(session.ObserveActorNavigation(actorId))));
                }
                index += 2;
                break;
                case "--observe-route-acceleration":
                    operations.Add(new SessionCommand("observe route acceleration", static session => RenderJson(session.ObserveRouteAcceleration())));
                    index += 1;
                    break;
                case "--observe-time":
                    operations.Add(new SessionCommand("observe logical time", static session => RenderJson(session.ObserveTime())));
                    index += 1;
                    break;
                case "--advance-time": {
                    string ticksText = RequireArg(args, index + 1);
                    long ticks = ParseNonNegativeTickDelta(ticksText);
                    operations.Add(new SessionCommand($"advance logical time by {ticksText}", session => RenderJson(session.AdvanceTime(ticks))));
                }
                index += 2;
                break;
                case "--plan-actor-route": {
                    string actorId = RequireArg(args, index + 1);
                    string toLocationId = RequireArg(args, index + 2);
                    operations.Add(new SessionCommand($"plan actor route {actorId} -> {toLocationId}", session => RenderJson(session.PlanActorRoute(actorId, toLocationId))));
                }
                index += 3;
                break;
                case "--plan-route": {
                    string fromLocationId = RequireArg(args, index + 1);
                    string toLocationId = RequireArg(args, index + 2);
                    operations.Add(new SessionCommand($"plan route {fromLocationId} -> {toLocationId}", session => RenderJson(session.PlanRoute(fromLocationId, toLocationId))));
                }
                index += 3;
                break;
                case "--rebuild-route-acceleration": {
                    string? rebuildLandmarks = TryReadOptionalArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"rebuild route acceleration {(rebuildLandmarks is null ? "default-profile" : rebuildLandmarks)}",
                            session => RenderJson(SampleWorldBootstrap.RebuildRouteAcceleration(session, rebuildLandmarks))
                        )
                    );
                    index += rebuildLandmarks is null ? 1 : 2;
                }
                break;
                case "--trace-actor-route": {
                    string actorId = RequireArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"trace actor route {actorId}",
                            session => DevTextRenderer.RenderRouteTrace(session.TraceActorRoute(actorId))
                        )
                    );
                }
                index += 2;
                break;
                case "--move-actor-quiet": {
                    string actorId = RequireArg(args, index + 1);
                    string passageId = RequireArg(args, index + 2);
                    operations.Add(
                        new SessionCommand(
                            $"move actor quietly {actorId} via {passageId}",
                            session => DevTextRenderer.RenderCompactMovement(session.MoveActor(actorId, passageId))
                        )
                    );
                }
                index += 3;
                break;
                case "--move-actor": {
                    string actorId = RequireArg(args, index + 1);
                    string passageId = RequireArg(args, index + 2);
                    operations.Add(new SessionCommand($"move actor {actorId} via {passageId}", session => RenderJson(session.MoveActor(actorId, passageId))));
                }
                index += 3;
                break;
                default:
                    throw new InvalidOperationException(BuildUsage());
            }
        }

        if (operations.Count == 0) {
            operations.Add(new SessionCommand("world dump", DevTextRenderer.RenderWorld));
        }

        return new SessionRequest(
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

    private static SessionBootstrapMode ResolveBootstrapMode(string? repoDir, bool useDevSampleWorld) {
        if (repoDir is not null && useDevSampleWorld) { throw new InvalidOperationException(RepoDirAndDevSampleWorldConflictError); }

        if (repoDir is null && !useDevSampleWorld) { throw new InvalidOperationException(MissingSessionTargetError); }

        return useDevSampleWorld ? SessionBootstrapMode.DevSampleWorld : SessionBootstrapMode.RepoDir;
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
  status  Print the current session scaffold state as JSON.
  help    Show this message.

Session target:
  --repo-dir <repoDir>
           Open or create a persistent sample world at the specified directory.
  --dev-sample-world
           Create a dev sample world under the system temp directory for this invocation.

           Exactly one session target must be specified.

Session options:
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
        TextAdv2Json.AddHostConverters(options);
        return options;
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private const string MissingSessionTargetError = "缺少 session target，请传 --repo-dir <repoDir> 或 --dev-sample-world";
    private const string RepoDirAndDevSampleWorldConflictError = "--repo-dir 不能与 --dev-sample-world 同时使用";

    private sealed record SessionRequest(
        SessionBootstrapMode BootstrapMode,
        string? RepoDir,
        SessionCommand[] Operations
    );

    private enum SessionBootstrapMode {
        RepoDir,
        DevSampleWorld,
    }

    private sealed record SessionCommand(
        string Description,
        Func<WorldSession, string> Execute
    );
}
