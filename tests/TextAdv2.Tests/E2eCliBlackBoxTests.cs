using System.Diagnostics;
using System.Text.Json;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Runtime;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public sealed class E2eCliBlackBoxTests {
    [Fact]
    public void BareInvocation_WithoutSessionTarget_FailsFast() {
        CliRunResult result = RunCli();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("缺少 session target，请传 --repo-dir <repoDir> 或 --dev-sample-world", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("TextAdv2 session repo:", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldCommand_WithoutSessionTarget_FailsFast() {
        CliRunResult result = RunCli("--world");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("缺少 session target，请传 --repo-dir <repoDir> 或 --dev-sample-world", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("TextAdv2 session repo:", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DevSampleWorld_WithWorldCommand_Succeeds() {
        CliRunResult result = RunCli("--dev-sample-world", "--world");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("TextAdv2 session repo:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("WORLD", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DevSampleWorld_WithLocationCommand_Succeeds() {
        CliRunResult result = RunCli("--dev-sample-world", "--location", "square");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("TextAdv2 session repo:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("- square | Square", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("WORLD", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DevSampleWorld_WithoutExplicitOperation_DefaultsToWorldDump() {
        CliRunResult result = RunCli("--dev-sample-world");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("TextAdv2 session repo:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("WORLD", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOnly_WithSingleJsonOperation_PrintsPureJsonDocument() {
        CliRunResult result = RunCli("--dev-sample-world", "--json-only", "--observe-actor", "scout");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.DoesNotContain("TextAdv2 session repo:", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("[1/1]", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));

        using JsonDocument json = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal("scout", json.RootElement.GetProperty("actorId").GetString());
        Assert.Equal("square", json.RootElement.GetProperty("location").GetProperty("locationId").GetString());
    }

    [Fact]
    public void JsonOnly_WithMultipleJsonOperations_FailsFast() {
        CliRunResult result = RunCli(
            "--dev-sample-world",
            "--json-only",
            "--observe-actor", "scout",
            "--observe-time"
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("--json-only 只允许一条 JSON 类 operation", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOnly_WithTextOperation_FailsFast() {
        CliRunResult result = RunCli("--dev-sample-world", "--json-only", "--world");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("--json-only 只支持 JSON 类 operation", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("world dump", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOnly_WithTextRouteTraceOperation_FailsFast() {
        CliRunResult result = RunCli("--dev-sample-world", "--json-only", "--trace-actor-route", "scout");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("--json-only 只支持 JSON 类 operation", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("trace actor route scout", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceActorRouteTextOperation_RemainsAvailableForHumanDebugging() {
        CliRunResult result = RunCli("--dev-sample-world", "--trace-actor-route", "scout");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains("TextAdv2 session repo:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ROUTE TRACE", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("start=square (Square)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("<no movement in this run>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("end=square (Square) | steps=0 | totalCost=0", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOnly_WithoutExplicitOperation_FailsFast() {
        CliRunResult result = RunCli("--dev-sample-world", "--json-only");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("--json-only 需要显式提供且只提供一条 JSON 类 operation", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoDir_SessionState_PersistsAcrossInvocations() {
        string repoDir = CreateTempRepoDir();

        try {
            CliRunResult init = RunCli("init-sample", repoDir);
            CliRunResult move = RunCli(
                "--repo-dir", repoDir,
                "--move-actor", "scout", "square-ridge-trail"
            );
            CliRunResult observe = RunCli(
                "--repo-dir", repoDir,
                "--observe-actor", "scout"
            );

            Assert.Equal(0, init.ExitCode);
            Assert.Equal(0, move.ExitCode);
            Assert.Equal(0, observe.ExitCode);
            Assert.Contains("\"toLocationId\": \"ridge\"", move.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"travelMode\": \"land\"", move.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"locationId\": \"ridge\"", observe.StandardOutput, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void RepoDir_ObserveActorContext_ReturnsStructuredContext_AndUpdatesAfterMove() {
        string repoDir = CreateTempRepoDir();

        try {
            CliRunResult init = RunCli("init-sample", repoDir);
            CliRunResult initialContextRun = RunCli("--repo-dir", repoDir, "--observe-actor-context", "scout");
            CliRunResult move = RunCli("--repo-dir", repoDir, "--move-actor", "scout", "square-ridge-trail");
            CliRunResult movedContextRun = RunCli("--repo-dir", repoDir, "--observe-actor-context", "scout");

            Assert.Equal(0, init.ExitCode);
            Assert.Equal(0, initialContextRun.ExitCode);
            Assert.Equal(0, move.ExitCode);
            Assert.Equal(0, movedContextRun.ExitCode);

            var initialContext = DeserializeCliJson<ActorContextObservation>(initialContextRun.StandardOutput);
            var movedContext = DeserializeCliJson<ActorContextObservation>(movedContextRun.StandardOutput);

            Assert.NotNull(initialContext);
            Assert.Equal("scout", initialContext.ActorId);
            Assert.Equal("Scout", initialContext.ActorName);
            Assert.Equal(0, initialContext.CurrentTick);
            Assert.Equal("square", initialContext.CurrentLocation.LocationId);
            Assert.Equal(
                ["square-ridge-trail", "square-shrine-gate", "village-square-road"],
                initialContext.AvailableMoves.Select(static edge => edge.PassageId).ToArray()
            );
            Assert.Equal(
                ["north gate", "old arch", "west"],
                initialContext.AvailableMoves.Select(static edge => edge.ExitName).ToArray()
            );
            Assert.Equal(
                ["ridge", "shrine", "village"],
                initialContext.AvailableMoves.Select(static edge => edge.TargetLocationId).ToArray()
            );
            Assert.Contains(
                initialContext.CurrentLocation.PresentActors,
                static actor => actor.ActorId == "scout"
            );

            Assert.NotNull(movedContext);
            Assert.Equal("scout", movedContext.ActorId);
            Assert.Equal("Scout", movedContext.ActorName);
            Assert.Equal(0, movedContext.CurrentTick);
            Assert.Equal("ridge", movedContext.CurrentLocation.LocationId);
            Assert.Equal(
                ["ridge-aerie-winch", "square-ridge-trail"],
                movedContext.AvailableMoves.Select(static edge => edge.PassageId).ToArray()
            );
            Assert.Equal(
                ["cliff lift", "downhill trail"],
                movedContext.AvailableMoves.Select(static edge => edge.ExitName).ToArray()
            );
            Assert.Equal(
                ["aerie", "square"],
                movedContext.AvailableMoves.Select(static edge => edge.TargetLocationId).ToArray()
            );
            Assert.Contains(
                movedContext.CurrentLocation.PresentActors,
                static actor => actor.ActorId == "scout"
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void RepoDir_ObserveActorContext_TracksLogicalTimeAndFiltersDisabledExitsFromAvailableMoves() {
        string repoDir = CreateTempRepoDir();

        try {
            CliRunResult init = RunCli("init-sample", repoDir);
            CliRunResult advanceTime = RunCli("--repo-dir", repoDir, "--advance-time", "4");
            CliRunResult moveBoatman = RunCli("--repo-dir", repoDir, "--move-actor", "boatman", "harbor-delta-current");
            CliRunResult observeContext = RunCli("--repo-dir", repoDir, "--observe-actor-context", "boatman");

            Assert.Equal(0, init.ExitCode);
            Assert.Equal(0, advanceTime.ExitCode);
            Assert.Equal(0, moveBoatman.ExitCode);
            Assert.Equal(0, observeContext.ExitCode);

            var context = DeserializeCliJson<ActorContextObservation>(observeContext.StandardOutput);

            Assert.NotNull(context);
            Assert.Equal("boatman", context.ActorId);
            Assert.Equal("Boatman", context.ActorName);
            Assert.Equal(4, context.CurrentTick);
            Assert.Equal("delta", context.CurrentLocation.LocationId);
            Assert.Empty(context.AvailableMoves);
            Assert.Equal(["harbor-delta-current"], context.CurrentLocation.Exits.Select(static edge => edge.PassageId).ToArray());
            Assert.False(context.CurrentLocation.Exits[0].IsEnabled);
            Assert.Equal("harbor", context.CurrentLocation.Exits[0].TargetLocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void RepoDir_TraceActorRouteJson_ReturnsTypedTrace_AndResetsAfterReopen() {
        string repoDir = CreateTempRepoDir();

        try {
            CliRunResult init = RunCli("init-sample", repoDir);
            CliRunResult trace = RunCli(
                "--repo-dir", repoDir,
                "--move-actor-quiet", "scout", "square-ridge-trail",
                "--trace-actor-route-json", "scout"
            );
            CliRunResult traceAfterReopen = RunCli("--repo-dir", repoDir, "--trace-actor-route-json", "scout");

            Assert.Equal(0, init.ExitCode);
            Assert.Equal(0, trace.ExitCode);
            Assert.Equal(0, traceAfterReopen.ExitCode);

            var traceJson = DeserializeCliJson<ActorRouteTrace>(trace.StandardOutput);
            var traceAfterReopenJson = DeserializeCliJson<ActorRouteTrace>(traceAfterReopen.StandardOutput);

            Assert.NotNull(traceJson);
            Assert.Equal("scout", traceJson.ActorId);
            Assert.Equal("square", traceJson.StartLocationId);
            Assert.Equal("ridge", traceJson.EndLocationId);
            Assert.Equal(1, traceJson.StepCount);
            Assert.Equal(5, traceJson.TotalTravelCost);
            Assert.Single(traceJson.Steps);
            Assert.Equal(1, traceJson.Steps[0].StepNumber);
            Assert.Equal("square-ridge-trail", traceJson.Steps[0].PassageId);
            Assert.Equal("north gate", traceJson.Steps[0].ExitName);
            Assert.Equal("square", traceJson.Steps[0].FromLocationId);
            Assert.Equal("ridge", traceJson.Steps[0].ToLocationId);
            Assert.Equal("land", traceJson.Steps[0].TravelMode);
            Assert.Equal(5, traceJson.Steps[0].TravelCost);

            Assert.NotNull(traceAfterReopenJson);
            Assert.Equal("scout", traceAfterReopenJson.ActorId);
            Assert.Equal("ridge", traceAfterReopenJson.StartLocationId);
            Assert.Equal("ridge", traceAfterReopenJson.EndLocationId);
            Assert.Equal(0, traceAfterReopenJson.StepCount);
            Assert.Equal(0, traceAfterReopenJson.TotalTravelCost);
            Assert.Empty(traceAfterReopenJson.Steps);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void JsonOnly_WithJsonRouteTraceOperation_PrintsPureJsonDocument() {
        CliRunResult result = RunCli("--dev-sample-world", "--json-only", "--trace-actor-route-json", "scout");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.DoesNotContain("TextAdv2 session repo:", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("[1/1]", result.StandardOutput, StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal("scout", json.RootElement.GetProperty("actorId").GetString());
        Assert.Equal("square", json.RootElement.GetProperty("startLocationId").GetString());
        Assert.Equal("square", json.RootElement.GetProperty("endLocationId").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("stepCount").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("totalTravelCost").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("steps").EnumerateArray());
    }

    [Fact]
    public void DevSampleWorld_JsonCommands_PreserveCanonicalEnumTokens() {
        CliRunResult observe = RunCli("--dev-sample-world", "--observe-navigation", "square");
        CliRunResult plan = RunCli("--dev-sample-world", "--plan-route", "shrine", "shrine");

        Assert.Equal(0, observe.ExitCode);
        Assert.Contains("\"travelMode\": \"land\"", observe.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"travelMode\": \"portal\"", observe.StandardOutput, StringComparison.Ordinal);

        Assert.Equal(0, plan.ExitCode);
        Assert.Contains("\"status\": \"already-there\"", plan.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_ShowsExplicitDevFlag_AndNoImplicitTemporaryWorldMessage() {
        CliRunResult result = RunCli("help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--dev-sample-world", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--json-only", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--trace-actor-route <actorId>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--trace-actor-route-json <actorId>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("This no longer creates a sample world implicitly", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("current compatibility behavior creates a persistent sample world", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime  Omit a meta command", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_PrintsCamelCaseJson() {
        CliRunResult result = RunCli("status");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        using JsonDocument json = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal("Atelia.TextAdv2", json.RootElement.GetProperty("engineAssemblyName").GetString());
        Assert.False(json.RootElement.TryGetProperty("EngineAssemblyName", out _));
    }

    [Fact]
    public void Usage_ShowsJsonOnlyFlag() {
        CliRunResult result = RunCli("--dev-sample-world", "--unknown-option");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--json-only", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoDir_AndDevSampleWorld_CannotBeCombined() {
        string repoDir = CreateTempRepoDir();

        try {
            CliRunResult result = RunCli(
                "--repo-dir", repoDir,
                "--dev-sample-world",
                "--world"
            );

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--repo-dir 不能与 --dev-sample-world 同时使用", result.StandardError, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void InitEmpty_CreatesOpenableEmptyWorldRepo() {
        string repoDir = CreateTempRepoDir();

        try {
            CliRunResult init = RunCli("init-empty", repoDir);
            CliRunResult time = RunCli("--repo-dir", repoDir, "--observe-time");
            CliRunResult acceleration = RunCli("--repo-dir", repoDir, "--observe-route-acceleration");

            Assert.Equal(0, init.ExitCode);
            Assert.Contains("Initialized empty TextAdv2 world repo:", init.StandardOutput, StringComparison.Ordinal);

            Assert.Equal(0, time.ExitCode);
            Assert.Contains("\"currentTick\": 0", time.StandardOutput, StringComparison.Ordinal);

            Assert.Equal(0, acceleration.ExitCode);
            Assert.Contains("\"locationCount\": 0", acceleration.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"passageCount\": 0", acceleration.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("\"locationCount\": 7", acceleration.StandardOutput, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void InitSample_CreatesExplicitSampleWorldRepo() {
        string repoDir = CreateTempRepoDir();

        try {
            CliRunResult init = RunCli("init-sample", repoDir);
            CliRunResult observe = RunCli("--repo-dir", repoDir, "--observe-actor", "scout");

            Assert.Equal(0, init.ExitCode);
            Assert.Contains("Initialized sample TextAdv2 world repo:", init.StandardOutput, StringComparison.Ordinal);

            Assert.Equal(0, observe.ExitCode);
            Assert.Contains("\"actorId\": \"scout\"", observe.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"locationId\": \"square\"", observe.StandardOutput, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void RepoDir_MissingRepo_FailsWithCliLevelMessage() {
        string repoDir = CreateTempRepoDir();

        CliRunResult result = RunCli("--repo-dir", repoDir, "--world");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--repo-dir 指向的仓库不存在:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("请先运行 init-empty <repoDir> 或 init-sample <repoDir> 初始化", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("StateJournal repository", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoDir_EmptyDirectory_FailsWithCliLevelMessage() {
        string repoDir = CreateTempRepoDir();
        Directory.CreateDirectory(repoDir);

        try {
            CliRunResult result = RunCli("--repo-dir", repoDir, "--world");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--repo-dir 指向的目录为空，尚未初始化为 TextAdv2 world repo:", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("请先运行 init-empty <repoDir> 或 init-sample <repoDir>", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("StateJournal repository", result.StandardError, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void RepoDir_FilePath_FailsWithPathTypeMessage() {
        string repoDir = CreateTempRepoDir();
        Directory.CreateDirectory(repoDir);
        string filePath = Path.Combine(repoDir, "not-a-directory.txt");
        File.WriteAllText(filePath, "not a repo");

        try {
            CliRunResult result = RunCli("--repo-dir", filePath, "--world");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--repo-dir 必须指向目录，但收到的是文件路径:", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("init-empty <repoDir> / init-sample <repoDir>", result.StandardError, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void Help_ShowsExplicitInitCommands() {
        CliRunResult result = RunCli("help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("init-empty <repoDir>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("init-sample <repoDir>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--create-location <locationId> <name> <description>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--create-actor <actorId> <name> <currentLocationId>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--create-passage <passageId> <locationAId> <exitNameFromA> <locationBId> <exitNameFromB>", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void InitEmpty_CanAuthorMinimalWorldAcrossCliInvocations() {
        string repoDir = CreateTempRepoDir();

        try {
            CliRunResult init = RunCli("init-empty", repoDir);
            CliRunResult createStart = RunCli("--repo-dir", repoDir, "--create-location", "start", "Start", "Starting point.");
            CliRunResult createGoal = RunCli("--repo-dir", repoDir, "--create-location", "goal", "Goal", "Goal point.");
            CliRunResult createActor = RunCli("--repo-dir", repoDir, "--create-actor", "runner", "Runner", "start");
            CliRunResult createPassage = RunCli(
                "--repo-dir", repoDir,
                "--create-passage", "start-goal", "start", "east", "goal", "west", "land", "2"
            );
            CliRunResult plan = RunCli("--repo-dir", repoDir, "--plan-actor-route", "runner", "goal");
            CliRunResult move = RunCli("--repo-dir", repoDir, "--move-actor", "runner", "start-goal");
            CliRunResult observe = RunCli("--repo-dir", repoDir, "--observe-actor", "runner");

            Assert.Equal(0, init.ExitCode);

            Assert.Equal(0, createStart.ExitCode);
            Assert.Contains("\"locationId\": \"start\"", createStart.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"locationName\": \"Start\"", createStart.StandardOutput, StringComparison.Ordinal);

            Assert.Equal(0, createGoal.ExitCode);
            Assert.Contains("\"locationId\": \"goal\"", createGoal.StandardOutput, StringComparison.Ordinal);

            Assert.Equal(0, createActor.ExitCode);
            Assert.Contains("\"actorId\": \"runner\"", createActor.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"currentLocationId\": \"start\"", createActor.StandardOutput, StringComparison.Ordinal);

            Assert.Equal(0, createPassage.ExitCode);
            Assert.Contains("\"passageId\": \"start-goal\"", createPassage.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"travelMode\": \"land\"", createPassage.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"baseTravelCost\": 2", createPassage.StandardOutput, StringComparison.Ordinal);

            Assert.Equal(0, plan.ExitCode);
            Assert.Contains("\"status\": \"found\"", plan.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"passageId\": \"start-goal\"", plan.StandardOutput, StringComparison.Ordinal);

            Assert.Equal(0, move.ExitCode);
            Assert.Contains("\"toLocationId\": \"goal\"", move.StandardOutput, StringComparison.Ordinal);

            Assert.Equal(0, observe.ExitCode);
            Assert.Contains("\"actorId\": \"runner\"", observe.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"locationId\": \"goal\"", observe.StandardOutput, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void InitEmpty_CanAuthorMoveAndObserveOtherActorAcrossCliInvocations() {
        string repoDir = CreateTempRepoDir();

        try {
            CliRunResult init = RunCli("init-empty", repoDir);
            CliRunResult createStart = RunCli("--repo-dir", repoDir, "--create-location", "start", "Start", "Shared-world start.");
            CliRunResult createGoal = RunCli("--repo-dir", repoDir, "--create-location", "goal", "Goal", "Shared-world goal.");
            CliRunResult createAlpha = RunCli("--repo-dir", repoDir, "--create-actor", "alpha", "Alpha", "start");
            CliRunResult createBeta = RunCli("--repo-dir", repoDir, "--create-actor", "beta", "Beta", "goal");
            CliRunResult createPassage = RunCli(
                "--repo-dir", repoDir,
                "--create-passage", "start-goal", "start", "advance", "goal", "return"
            );
            CliRunResult moveAlpha = RunCli("--repo-dir", repoDir, "--move-actor", "alpha", "start-goal");
            CliRunResult observeGoal = RunCli("--repo-dir", repoDir, "--observe-location", "goal");
            CliRunResult observeStart = RunCli("--repo-dir", repoDir, "--observe-location", "start");
            CliRunResult observeBeta = RunCli("--repo-dir", repoDir, "--observe-actor", "beta");

            Assert.Equal(0, init.ExitCode);
            Assert.Equal(0, createStart.ExitCode);
            Assert.Equal(0, createGoal.ExitCode);
            Assert.Equal(0, createAlpha.ExitCode);
            Assert.Equal(0, createBeta.ExitCode);
            Assert.Equal(0, createPassage.ExitCode);
            Assert.Equal(0, moveAlpha.ExitCode);
            Assert.Equal(0, observeGoal.ExitCode);
            Assert.Equal(0, observeStart.ExitCode);
            Assert.Equal(0, observeBeta.ExitCode);

            using JsonDocument goalJson = ExtractJsonBlock(observeGoal.StandardOutput);
            using JsonDocument startJson = ExtractJsonBlock(observeStart.StandardOutput);
            using JsonDocument betaJson = ExtractJsonBlock(observeBeta.StandardOutput);
            using JsonDocument moveJson = ExtractJsonBlock(moveAlpha.StandardOutput);

            Assert.Equal("goal", moveJson.RootElement.GetProperty("currentLocation").GetProperty("locationId").GetString());
            Assert.Equal(
                ["alpha", "beta"],
                ReadActorIds(moveJson.RootElement.GetProperty("currentLocation").GetProperty("presentActors"))
            );
            Assert.Equal(["alpha", "beta"], ReadActorIds(goalJson.RootElement.GetProperty("presentActors")));
            Assert.Empty(ReadActorIds(startJson.RootElement.GetProperty("presentActors")));
            Assert.Equal("goal", betaJson.RootElement.GetProperty("location").GetProperty("locationId").GetString());
            Assert.Equal(
                ["alpha", "beta"],
                ReadActorIds(betaJson.RootElement.GetProperty("location").GetProperty("presentActors"))
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void CreatePassage_WithoutOptionalArgs_UsesDefaultTravelModeAndCost() {
        string repoDir = CreateTempRepoDir();

        try {
            _ = RunCli("init-empty", repoDir);
            _ = RunCli("--repo-dir", repoDir, "--create-location", "start", "Start", "Starting point.");
            _ = RunCli("--repo-dir", repoDir, "--create-location", "goal", "Goal", "Goal point.");

            CliRunResult createPassage = RunCli(
                "--repo-dir", repoDir,
                "--create-passage", "start-goal", "start", "east", "goal", "west"
            );

            Assert.Equal(0, createPassage.ExitCode);
            Assert.Contains("\"travelMode\": \"land\"", createPassage.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"baseTravelCost\": 1", createPassage.StandardOutput, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static CliRunResult RunCli(params string[] args) {
        string repoRoot = GetRepoRoot();
        string projectPath = Path.Combine(repoRoot, "prototypes", "TextAdv2.E2eCli", "TextAdv2.E2eCli.csproj");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Unable to resolve test build configuration.");

        var startInfo = new ProcessStartInfo("dotnet") {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        foreach (string arg in args) {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start TextAdv2.E2eCli.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new CliRunResult(
            process.ExitCode,
            Normalize(standardOutput),
            Normalize(standardError)
        );
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-e2ecli-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static JsonDocument ExtractJsonBlock(string output) {
        int jsonStart = output.IndexOf('{');
        if (jsonStart < 0) {
            throw new InvalidOperationException("CLI output did not contain a JSON object.");
        }

        return JsonDocument.Parse(output[jsonStart..]);
    }

    private static T DeserializeCliJson<T>(string output)
        where T : class
    {
        int jsonStart = output.IndexOf('{');
        if (jsonStart < 0) {
            throw new InvalidOperationException("CLI output did not contain a JSON object.");
        }

        return JsonSerializer.Deserialize<T>(output[jsonStart..], CliJsonOptions)
            ?? throw new InvalidOperationException($"CLI output did not deserialize to {typeof(T).Name}.");
    }

    private static string[] ReadActorIds(JsonElement presentActors)
        => presentActors.EnumerateArray()
            .Select(static actor => actor.GetProperty("actorId").GetString() ?? string.Empty)
            .OrderBy(static actorId => actorId, StringComparer.Ordinal)
            .ToArray();

    private static JsonSerializerOptions CreateCliJsonOptions() {
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        TextAdv2Json.AddHostConverters(options);
        return options;
    }

    private static readonly JsonSerializerOptions CliJsonOptions = CreateCliJsonOptions();

    private sealed record CliRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError
    );
}
