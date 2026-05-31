using System.Text.Json;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Session;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.E2eCli;

internal static class Program {
    public static int Main(string[] args) {
        try {
            if (args.Length > 0 && IsMetaCommand(args[0])) {
                return args[0] switch {
                    "smoke" => RunSmoke(),
                    "status" => RunStatus(),
                    "init-empty" => RunInitEmpty(RequireArg(args, 1)),
                    "init-sample" => RunInitSample(RequireArg(args, 1)),
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
        return 0;
    }

    private static int RunStatus() {
        var scaffold = HostingScaffold.DescribeCurrentState();
        var json = JsonSerializer.Serialize(scaffold, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        return 0;
    }

    private static int RunInitEmpty(string repoDir) {
        using var session = WorldSession.CreateEmpty(repoDir);
        Console.WriteLine($"Initialized empty TextAdv2 world repo: {session.RepoDir}");
        return 0;
    }

    private static int RunInitSample(string repoDir) {
        using var session = SampleWorldBootstrap.CreateFreshSession(repoDir);
        Console.WriteLine($"Initialized sample TextAdv2 world repo: {session.RepoDir}");
        return 0;
    }

    private static int RunSessionCommands(string[] args) {
        var request = ParseSessionRequest(args);
        ValidateMachineOutputRequest(request);
        using var session = request.BootstrapMode switch {
            SessionBootstrapMode.RepoDir => OpenExistingRepoSession(request.RepoDir!),
            SessionBootstrapMode.DevSampleWorld => SampleWorldBootstrap.CreateTemporarySession(),
            _ => throw new InvalidOperationException("Unsupported session bootstrap mode."),
        };

        if (request.JsonOnly) {
            Console.Out.Write(request.Operations[0].Execute(session));
            return 0;
        }

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

    private static WorldSession OpenExistingRepoSession(string repoDir) {
        if (File.Exists(repoDir)) {
            throw new InvalidOperationException(
                $"--repo-dir 必须指向目录，但收到的是文件路径: '{repoDir}'。请改为已初始化的 world repo 目录，或先运行 init-empty <repoDir> / init-sample <repoDir>。"
            );
        }

        if (!Directory.Exists(repoDir)) {
            throw new InvalidOperationException(
                $"--repo-dir 指向的仓库不存在: '{repoDir}'。请先运行 init-empty <repoDir> 或 init-sample <repoDir> 初始化，或改用 --dev-sample-world。"
            );
        }

        if (!Directory.EnumerateFileSystemEntries(repoDir).Any()) {
            throw new InvalidOperationException(
                $"--repo-dir 指向的目录为空，尚未初始化为 TextAdv2 world repo: '{repoDir}'。请先运行 init-empty <repoDir> 或 init-sample <repoDir>。"
            );
        }

        try {
            return WorldSession.OpenExisting(repoDir);
        }
        catch (InvalidOperationException ex) {
            if (ex.Message.Contains("Failed to acquire lock", StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"--repo-dir 指向的 repo 当前无法打开，可能仍被其他进程占用: '{repoDir}'。底层错误: {ex.Message}",
                    ex
                );
            }

            throw new InvalidOperationException(
                $"--repo-dir 未指向可打开的 TextAdv2 world repo: '{repoDir}'。如果这是新目录，请先运行 init-empty <repoDir> 或 init-sample <repoDir>；若目录已初始化，请检查仓库状态。底层错误: {ex.Message}",
                ex
            );
        }
    }

    private static SessionRequest ParseSessionRequest(string[] args) {
        string? repoDir = null;
        bool useDevSampleWorld = false;
        bool jsonOnly = false;
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
                case "--json-only":
                    jsonOnly = true;
                    index += 1;
                    break;
                case "--world":
                    operations.Add(new SessionCommand("world dump", SessionCommandOutputKind.Text, DevTextRenderer.RenderWorld));
                    index += 1;
                    break;
                case "--location": {
                    string locationId = RequireArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"location dump {locationId}",
                            SessionCommandOutputKind.Text,
                            session => DevTextRenderer.RenderLocation(session, locationId)
                        )
                    );
                }
                index += 2;
                break;
                case "--observe-location": {
                    string locationId = RequireArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"observe location {locationId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.ObserveLocation(locationId))
                        )
                    );
                }
                index += 2;
                break;
                case "--create-location": {
                    string locationId = RequireArg(args, index + 1);
                    string locationName = RequireArg(args, index + 2);
                    string locationDescription = RequireArg(args, index + 3);
                    operations.Add(
                        new SessionCommand(
                            $"create location {locationId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.CreateLocation(locationId, locationName, locationDescription))
                        )
                    );
                }
                index += 4;
                break;
                case "--observe-actor": {
                    string actorId = RequireArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"observe actor {actorId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.ObserveActor(actorId))
                        )
                    );
                }
                index += 2;
                break;
                case "--observe-actor-context": {
                    string actorId = RequireArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"observe actor context {actorId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.ObserveActorContext(actorId))
                        )
                    );
                }
                index += 2;
                break;
                case "--create-actor": {
                    string actorId = RequireArg(args, index + 1);
                    string actorName = RequireArg(args, index + 2);
                    string currentLocationId = RequireArg(args, index + 3);
                    operations.Add(
                        new SessionCommand(
                            $"create actor {actorId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.CreateActor(actorId, actorName, currentLocationId))
                        )
                    );
                }
                index += 4;
                break;
                case "--observe-navigation": {
                    string locationId = RequireArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"observe navigation {locationId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.ObserveNavigation(locationId))
                        )
                    );
                }
                index += 2;
                break;
                case "--observe-actor-navigation": {
                    string actorId = RequireArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"observe actor navigation {actorId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.ObserveActorNavigation(actorId))
                        )
                    );
                }
                index += 2;
                break;
                case "--observe-route-acceleration":
                    operations.Add(
                        new SessionCommand(
                            "observe route acceleration",
                            SessionCommandOutputKind.Json,
                            static session => RenderJson(session.ObserveRouteAcceleration())
                        )
                    );
                    index += 1;
                    break;
                case "--observe-time":
                    operations.Add(
                        new SessionCommand(
                            "observe logical time",
                            SessionCommandOutputKind.Json,
                            static session => RenderJson(session.ObserveTime())
                        )
                    );
                    index += 1;
                    break;
                case "--advance-time": {
                    string ticksText = RequireArg(args, index + 1);
                    long ticks = ParseNonNegativeTickDelta(ticksText);
                    operations.Add(
                        new SessionCommand(
                            $"advance logical time by {ticksText}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.AdvanceTime(ticks))
                        )
                    );
                }
                index += 2;
                break;
                case "--plan-actor-route": {
                    string actorId = RequireArg(args, index + 1);
                    string toLocationId = RequireArg(args, index + 2);
                    operations.Add(
                        new SessionCommand(
                            $"plan actor route {actorId} -> {toLocationId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.PlanActorRoute(actorId, toLocationId))
                        )
                    );
                }
                index += 3;
                break;
                case "--plan-route": {
                    string fromLocationId = RequireArg(args, index + 1);
                    string toLocationId = RequireArg(args, index + 2);
                    operations.Add(
                        new SessionCommand(
                            $"plan route {fromLocationId} -> {toLocationId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.PlanRoute(fromLocationId, toLocationId))
                        )
                    );
                }
                index += 3;
                break;
                case "--create-passage": {
                    string passageId = RequireArg(args, index + 1);
                    string locationAId = RequireArg(args, index + 2);
                    string exitNameFromA = RequireArg(args, index + 3);
                    string locationBId = RequireArg(args, index + 4);
                    string exitNameFromB = RequireArg(args, index + 5);
                    string? travelModeText = TryReadOptionalArg(args, index + 6);
                    TravelMode travelMode = travelModeText is null ? TravelMode.Land : ParseTravelMode(travelModeText);
                    string? baseTravelCostText = travelModeText is null ? null : TryReadOptionalArg(args, index + 7);
                    int baseTravelCost = baseTravelCostText is null ? 1 : ParseTravelCost(baseTravelCostText);
                    operations.Add(
                        new SessionCommand(
                            $"create passage {passageId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(
                                session.CreatePassage(
                                    passageId,
                                    locationAId,
                                    exitNameFromA,
                                    locationBId,
                                    exitNameFromB,
                                    travelMode,
                                    baseTravelCost
                                )
                            )
                        )
                    );
                    index += 6 + (travelModeText is null ? 0 : 1) + (baseTravelCostText is null ? 0 : 1);
                }
                break;
                case "--rebuild-route-acceleration": {
                    string? rebuildLandmarks = TryReadOptionalArg(args, index + 1);
                    operations.Add(
                        new SessionCommand(
                            $"rebuild route acceleration {(rebuildLandmarks is null ? "default-profile" : rebuildLandmarks)}",
                            SessionCommandOutputKind.Json,
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
                            SessionCommandOutputKind.Text,
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
                            SessionCommandOutputKind.Text,
                            session => DevTextRenderer.RenderCompactMovement(session.MoveActor(actorId, passageId))
                        )
                    );
                }
                index += 3;
                break;
                case "--move-actor": {
                    string actorId = RequireArg(args, index + 1);
                    string passageId = RequireArg(args, index + 2);
                    operations.Add(
                        new SessionCommand(
                            $"move actor {actorId} via {passageId}",
                            SessionCommandOutputKind.Json,
                            session => RenderJson(session.MoveActor(actorId, passageId))
                        )
                    );
                }
                index += 3;
                break;
                default:
                    throw new InvalidOperationException(BuildUsage());
            }
        }

        if (!jsonOnly && operations.Count == 0) {
            operations.Add(new SessionCommand("world dump", SessionCommandOutputKind.Text, DevTextRenderer.RenderWorld));
        }

        return new SessionRequest(
            ResolveBootstrapMode(repoDir, useDevSampleWorld),
            repoDir,
            jsonOnly,
            operations.ToArray()
        );
    }

    private static void ValidateMachineOutputRequest(SessionRequest request) {
        if (!request.JsonOnly) {
            return;
        }

        if (request.Operations.Length == 0) {
            throw new InvalidOperationException(JsonOnlyRequiresExplicitOperationError);
        }

        if (request.Operations.Length > 1) {
            throw new InvalidOperationException(JsonOnlyRequiresSingleOperationError);
        }

        if (request.Operations[0].OutputKind is not SessionCommandOutputKind.Json) {
            throw new InvalidOperationException(
                $"--json-only 只支持 JSON 类 operation，但收到文本输出 operation: {request.Operations[0].Description}"
            );
        }
    }

    private static bool IsMetaCommand(string command)
        => command is "smoke" or "status" or "init-empty" or "init-sample" or "help" or "-h" or "--help";

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

    private static int ParseTravelCost(string value) {
        if (!int.TryParse(value, out int travelCost)) {
            throw new InvalidOperationException($"CreatePassage requires an integer base travel cost, but received '{value}'.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(travelCost, 1);
        return travelCost;
    }

    private static TravelMode ParseTravelMode(string value)
        => value.ToLowerInvariant() switch {
            "land" => TravelMode.Land,
            "water" => TravelMode.Water,
            "air" => TravelMode.Air,
            "portal" => TravelMode.Portal,
            _ => throw new InvalidOperationException(
                $"CreatePassage requires travel mode land|water|air|portal, but received '{value}'."
            ),
        };

    private static string BuildUsage()
        => """
Usage:
  dotnet run --project prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj [smoke|status|help]
  dotnet run --project prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj init-empty <repoDir>
  dotnet run --project prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj init-sample <repoDir>
  dotnet run --project prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj (--repo-dir <repoDir> | --dev-sample-world) [--json-only] [--world] [--location <locationId>] [--observe-location <locationId>] [--create-location <locationId> <name> <description>] [--observe-actor <actorId>] [--observe-actor-context <actorId>] [--create-actor <actorId> <name> <currentLocationId>] [--observe-navigation <locationId>] [--observe-actor-navigation <actorId>] [--observe-route-acceleration] [--observe-time] [--advance-time <ticks>] [--plan-actor-route <actorId> <toLocationId>] [--plan-route <fromLocationId> <toLocationId>] [--create-passage <passageId> <locationAId> <exitNameFromA> <locationBId> <exitNameFromB> [<travelMode>] [<baseTravelCost>]] [--rebuild-route-acceleration [<locationId[,locationId...]>|default]] [--trace-actor-route <actorId>] [--move-actor-quiet <actorId> <passageId>] [--move-actor <actorId> <passageId>]
""";

    private static int RunHelp() {
        Console.WriteLine(
            """
TextAdv2.E2eCli

Meta commands:
  smoke   Validate the host project starts and can reference Atelia.TextAdv2.
  status  Print the current session scaffold state as JSON.
  init-empty <repoDir>
          Create a persistent empty world repository.
  init-sample <repoDir>
          Create a persistent sample world repository.
  help    Show this message.

Session target:
  --repo-dir <repoDir>
           Open an existing persistent TextAdv2 world repo.
           This no longer creates a sample world implicitly; initialize first with init-empty or init-sample.
  --dev-sample-world
           Create a dev sample world under the system temp directory for this invocation.

           Exactly one session target must be specified.

Session options:
    --json-only
                     Session mode only. Require exactly one JSON-producing operation and print only that JSON document to stdout.
    --observe-actor-context <actorId>
                     Print the machine-consumable actor context as JSON.
    --create-location <locationId> <name> <description>
                     Create a location and print the typed authoring snapshot as JSON.
    --create-actor <actorId> <name> <currentLocationId>
                     Create an actor and print the typed authoring snapshot as JSON.
    --create-passage <passageId> <locationAId> <exitNameFromA> <locationBId> <exitNameFromB> [<travelMode>] [<baseTravelCost>]
                     Create a passage and print the typed authoring snapshot as JSON.
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
    private const string JsonOnlyRequiresExplicitOperationError = "--json-only 需要显式提供且只提供一条 JSON 类 operation。";
    private const string JsonOnlyRequiresSingleOperationError = "--json-only 只允许一条 JSON 类 operation。";

    private sealed record SessionRequest(
        SessionBootstrapMode BootstrapMode,
        string? RepoDir,
        bool JsonOnly,
        SessionCommand[] Operations
    );

    private enum SessionBootstrapMode {
        RepoDir,
        DevSampleWorld,
    }

    private sealed record SessionCommand(
        string Description,
        SessionCommandOutputKind OutputKind,
        Func<WorldSession, string> Execute
    );

    private enum SessionCommandOutputKind {
        Json,
        Text,
    }
}
