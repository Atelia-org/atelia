using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public sealed class CrossHostMachineContractParityTests {
    private const string OpenExistingOnlyBootstrapMode = "open-existing-only";

    [Fact]
    public async Task ObserveActor_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--observe-actor", "scout");

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/actors/scout/observation");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            using JsonDocument cli = JsonDocument.Parse(cliJson);
            using JsonDocument host = JsonDocument.Parse(hostJson);
            AssertActorObservationJsonUsesCanonicalShape(cli.RootElement, "cli observe actor");
            AssertActorObservationJsonUsesCanonicalShape(host.RootElement, "host observe actor");
            AssertJsonEquivalent(cliJson, hostJson, "observe actor");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ObserveActorContext_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--observe-actor-context", "scout");

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/actors/scout/context");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            using JsonDocument cli = JsonDocument.Parse(cliJson);
            using JsonDocument host = JsonDocument.Parse(hostJson);
            AssertActorContextJsonUsesCanonicalActionSurface(cli.RootElement, "cli observe actor context");
            AssertActorContextJsonUsesCanonicalActionSurface(host.RootElement, "host observe actor context");
            AssertJsonEquivalent(cliJson, hostJson, "observe actor context");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ObserveActorContextRouteFollowing_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepoWithActiveRouteFollowing(cliRepoDir, "scout", "aerie");
            PrepareSampleRepoWithActiveRouteFollowing(hostRepoDir, "scout", "aerie");

            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--observe-actor-context", "scout");

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/actors/scout/context");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            using JsonDocument cli = JsonDocument.Parse(cliJson);
            using JsonDocument host = JsonDocument.Parse(hostJson);
            AssertActorContextJsonUsesCanonicalActionSurface(cli.RootElement, "cli observe actor context route-following");
            AssertActorContextJsonUsesCanonicalActionSurface(host.RootElement, "host observe actor context route-following");
            AssertRouteFollowingActivityJson(cli.RootElement, "cli observe actor context route-following");
            AssertRouteFollowingActivityJson(host.RootElement, "host observe actor context route-following");
            AssertJsonEquivalent(cliJson, hostJson, "observe actor context route-following");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ObserveLocation_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--observe-location", "square");

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/admin/locations/square/observation");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            using JsonDocument cli = JsonDocument.Parse(cliJson);
            using JsonDocument host = JsonDocument.Parse(hostJson);
            AssertLocationObservationJsonUsesCanonicalShape(cli.RootElement, "cli observe location");
            AssertLocationObservationJsonUsesCanonicalShape(host.RootElement, "host observe location");
            AssertJsonEquivalent(cliJson, hostJson, "observe location");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ObserveNavigation_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--observe-navigation", "square");

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/admin/locations/square/navigation");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "observe navigation");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ObserveNavigationDeltaEmpty_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--observe-navigation", "delta");

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/admin/locations/delta/navigation");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "observe navigation delta empty");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ObserveActorNavigationAfterMove_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            _ = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--move-actor", "scout", "square-ridge-trail");
            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--observe-actor-navigation", "scout");

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var moveResponse = await client.PostAsync("/actors/scout/moves/square-ridge-trail", content: null);
            _ = await ReadSuccessfulHostJsonAsync(moveResponse);
            using var observeResponse = await client.GetAsync("/actors/scout/navigation");
            string hostJson = await ReadSuccessfulHostJsonAsync(observeResponse);

            AssertJsonEquivalent(cliJson, hostJson, "observe actor navigation after move");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ObserveTime_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            _ = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--advance-time", "9");

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var advanceResponse = await client.PostAsync("/admin/advance-time/9", content: null);
            _ = await ReadSuccessfulHostJsonAsync(advanceResponse);

            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--observe-time");
            using var observeResponse = await client.GetAsync("/admin/time");
            string hostJson = await ReadSuccessfulHostJsonAsync(observeResponse);

            AssertJsonEquivalent(cliJson, hostJson, "observe time");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task MoveResult_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--move-actor", "scout", "square-ridge-trail");

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.PostAsync("/actors/scout/moves/square-ridge-trail", content: null);
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "move result");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task RuntimeRouteTraceJson_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();

            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir, "--json-only", "--trace-actor-runtime-route-json", "scout"
            );
            using var traceResponse = await client.GetAsync("/actors/scout/runtime-route-trace/json");
            string hostJson = await ReadSuccessfulHostJsonAsync(traceResponse);

            using JsonDocument cli = JsonDocument.Parse(cliJson);
            using JsonDocument host = JsonDocument.Parse(hostJson);
            AssertRuntimeRouteTraceJsonUsesRuntimeBoundary(cli.RootElement, "cli runtime route trace");
            AssertRuntimeRouteTraceJsonUsesRuntimeBoundary(host.RootElement, "host runtime route trace");
            AssertJsonEquivalentIgnoringTopLevelProperty(cliJson, hostJson, "runtime route trace json", "runtimeEpochId");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ActorRoutePlanFound_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--plan-actor-route", "scout", "aerie"
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/actors/scout/plan-route/aerie");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "actor route plan found");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ActorRoutePlanAlreadyThere_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--plan-actor-route", "scout", "square"
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/actors/scout/plan-route/square");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "actor route plan already-there");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task ActorRoutePlanUnreachable_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--plan-actor-route", "boatman", "village"
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/actors/boatman/plan-route/village");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "actor route plan unreachable");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task LocationRoutePlanFound_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--plan-route", "village", "aerie"
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/admin/routes/village/aerie");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "location route plan found");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task LocationRoutePlanAlreadyThere_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--plan-route", "shrine", "shrine"
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/admin/routes/shrine/shrine");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "location route plan already-there");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task LocationRoutePlanUnreachable_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--plan-route", "delta", "harbor"
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/admin/routes/delta/harbor");
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "location route plan unreachable");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task CreateLocationSnapshot_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareEmptyRepo(cliRepoDir);
            PrepareEmptyRepo(hostRepoDir);

            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--create-location", "start", "Start", "Starting point."
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            const string createLocationRequestJson =
                """
                {
                  "id": "start",
                  "name": "Start",
                  "description": "Starting point."
                }
                """;
            using var response = await client.PostAsync(
                "/admin/locations",
                new StringContent(createLocationRequestJson, Encoding.UTF8, "application/json")
            );
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "create location snapshot");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task CreateActorSnapshot_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareEmptyRepo(cliRepoDir);
            PrepareEmptyRepo(hostRepoDir);

            _ = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--create-location", "start", "Start", "Starting point."
            );
            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--create-actor", "guide", "Guide", "start"
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            const string createLocationRequestJson =
                """
                {
                  "id": "start",
                  "name": "Start",
                  "description": "Starting point."
                }
                """;
            using var createLocationResponse = await client.PostAsync(
                "/admin/locations",
                new StringContent(createLocationRequestJson, Encoding.UTF8, "application/json")
            );
            _ = await ReadSuccessfulHostJsonAsync(createLocationResponse);

            const string createActorRequestJson =
                """
                {
                  "id": "guide",
                  "name": "Guide",
                  "currentLocationId": "start"
                }
                """;
            using var response = await client.PostAsync(
                "/admin/actors",
                new StringContent(createActorRequestJson, Encoding.UTF8, "application/json")
            );
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "create actor snapshot");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task CreatePassageDefaultSnapshot_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareEmptyRepo(cliRepoDir);
            PrepareEmptyRepo(hostRepoDir);

            CreateStartAndEndLocationsViaCli(cliRepoDir);
            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--create-passage", "start-end", "start", "north", "end", "south"
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            await CreateStartAndEndLocationsViaHostAsync(client);

            const string createPassageRequestJson =
                """
                {
                  "id": "start-end",
                  "locationAId": "start",
                  "exitNameFromA": "north",
                  "locationBId": "end",
                  "exitNameFromB": "south"
                }
                """;
            using var response = await client.PostAsync(
                "/admin/passages",
                new StringContent(createPassageRequestJson, Encoding.UTF8, "application/json")
            );
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "create passage default snapshot");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    [Fact]
    public async Task CreatePassageExplicitTravelModeAndBaseTravelCostSnapshot_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareEmptyRepo(cliRepoDir);
            PrepareEmptyRepo(hostRepoDir);

            CreateStartAndEndLocationsViaCli(cliRepoDir);
            string cliJson = RunCliAndReadSuccessfulJson(
                "--repo-dir", cliRepoDir,
                "--json-only",
                "--create-passage", "start-end-portal", "start", "gate", "end", "return", "portal", "7"
            );

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();
            await CreateStartAndEndLocationsViaHostAsync(client);

            const string createPassageRequestJson =
                """
                {
                  "id": "start-end-portal",
                  "locationAId": "start",
                  "exitNameFromA": "gate",
                  "locationBId": "end",
                  "exitNameFromB": "return",
                  "travelMode": "portal",
                  "baseTravelCost": 7
                }
                """;
            using var response = await client.PostAsync(
                "/admin/passages",
                new StringContent(createPassageRequestJson, Encoding.UTF8, "application/json")
            );
            string hostJson = await ReadSuccessfulHostJsonAsync(response);

            AssertJsonEquivalent(cliJson, hostJson, "create passage explicit travel mode and base travel cost snapshot");
        }
        finally {
            DeleteDirectoryIfExists(cliRepoDir);
            DeleteDirectoryIfExists(hostRepoDir);
        }
    }

    private static void PrepareSampleRepo(string repoDir) {
        using (SampleWorldBootstrap.CreateFreshSession(repoDir)) {
        }

        WaitUntilSessionCanReopen(repoDir);
    }

    private static void PrepareSampleRepoWithActiveRouteFollowing(
        string repoDir,
        string actorId,
        string destinationLocationId
    ) {
        using (var session = SampleWorldBootstrap.CreateFreshSession(repoDir)) {
            _ = session.StartActorRouteFollowing(actorId, destinationLocationId);
        }

        WaitUntilSessionCanReopen(repoDir);
    }

    private static void PrepareEmptyRepo(string repoDir) {
        using (SerialWorldRuntime.CreateEmpty(repoDir)) {
        }

        WaitUntilSessionCanReopen(repoDir);
    }

    private static void CreateStartAndEndLocationsViaCli(string repoDir) {
        _ = RunCliAndReadSuccessfulJson(
            "--repo-dir", repoDir,
            "--json-only",
            "--create-location", "start", "Start", "Starting point."
        );
        _ = RunCliAndReadSuccessfulJson(
            "--repo-dir", repoDir,
            "--json-only",
            "--create-location", "end", "End", "Ending point."
        );
    }

    private static async Task CreateStartAndEndLocationsViaHostAsync(HttpClient client) {
        const string createStartLocationRequestJson =
            """
            {
              "id": "start",
              "name": "Start",
              "description": "Starting point."
            }
            """;
        using var createStartResponse = await client.PostAsync(
            "/admin/locations",
            new StringContent(createStartLocationRequestJson, Encoding.UTF8, "application/json")
        );
        _ = await ReadSuccessfulHostJsonAsync(createStartResponse);

        const string createEndLocationRequestJson =
            """
            {
              "id": "end",
              "name": "End",
              "description": "Ending point."
            }
            """;
        using var createEndResponse = await client.PostAsync(
            "/admin/locations",
            new StringContent(createEndLocationRequestJson, Encoding.UTF8, "application/json")
        );
        _ = await ReadSuccessfulHostJsonAsync(createEndResponse);
    }

    private static TextAdv2GameServerFactory CreateFactory(string repoDir)
        => new(repoDir, OpenExistingOnlyBootstrapMode);

    private static async Task<string> ReadSuccessfulHostJsonAsync(HttpResponseMessage response) {
        string json = Normalize(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        return json;
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

    private static string RunCliAndReadSuccessfulJson(params string[] args) {
        CliRunResult result = RunCli(args);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);

        return result.StandardOutput;
    }

    private static void AssertActorContextJsonUsesCanonicalActionSurface(JsonElement root, string source) {
        JsonElement currentLocation = root.GetProperty("currentLocation");
        string[] currentLocationPropertyNames = currentLocation
            .EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(currentLocation.TryGetProperty("locationId", out _), $"{source}: currentLocation.locationId should be present.");
        Assert.True(currentLocation.TryGetProperty("locationName", out _), $"{source}: currentLocation.locationName should be present.");
        Assert.True(currentLocation.TryGetProperty("locationDescription", out _), $"{source}: currentLocation.locationDescription should be present.");
        Assert.True(currentLocation.TryGetProperty("presentActors", out _), $"{source}: currentLocation.presentActors should be present.");
        Assert.False(
            currentLocation.TryGetProperty("exits", out _),
            $"{source}: currentLocation.exits should be absent because availableMoves is the canonical action surface."
        );
        Assert.Equal(
            ["locationDescription", "locationId", "locationName", "presentActors"],
            currentLocationPropertyNames
        );
        Assert.True(root.TryGetProperty("availableMoves", out _), $"{source}: availableMoves should be present.");
        Assert.True(root.TryGetProperty("currentActivity", out JsonElement currentActivity), $"{source}: currentActivity should be present.");
        Assert.True(currentActivity.TryGetProperty("kind", out _), $"{source}: currentActivity.kind should be present.");
        Assert.True(root.TryGetProperty("carriedResources", out JsonElement carriedResources), $"{source}: carriedResources should be present.");
        Assert.Equal(JsonValueKind.Array, carriedResources.ValueKind);
    }

    private static void AssertRouteFollowingActivityJson(JsonElement root, string source) {
        JsonElement currentActivity = root.GetProperty("currentActivity");
        Assert.Equal("route-following", currentActivity.GetProperty("kind").GetString());
        Assert.True(currentActivity.GetProperty("isInterruptible").GetBoolean(), $"{source}: route-following should default to interruptible.");

        JsonElement routeFollowing = currentActivity.GetProperty("routeFollowing");
        Assert.Equal("aerie", routeFollowing.GetProperty("destinationLocationId").GetString());
        Assert.Equal("Aerie", routeFollowing.GetProperty("destinationLocationName").GetString());
        Assert.Equal(5, routeFollowing.GetProperty("remainingTravelTicksOnCurrentLeg").GetInt32());
        Assert.Equal(
            ["square-ridge-trail", "ridge-aerie-winch"],
            routeFollowing.GetProperty("remainingPassageIds").EnumerateArray().Select(static x => x.GetString()!).ToArray()
        );
    }

    private static void AssertActorObservationJsonUsesCanonicalShape(JsonElement root, string source) {
        string[] propertyNames = ReadSortedPropertyNames(root);

        Assert.True(root.TryGetProperty("actorId", out _), $"{source}: actorId should be present.");
        Assert.True(root.TryGetProperty("actorName", out _), $"{source}: actorName should be present.");
        Assert.True(root.TryGetProperty("location", out JsonElement location), $"{source}: location should be present.");
        Assert.Equal(
            ["actorId", "actorName", "location"],
            propertyNames
        );

        AssertLocationObservationJsonUsesCanonicalShape(location, $"{source}: location");
    }

    private static void AssertRuntimeRouteTraceJsonUsesRuntimeBoundary(JsonElement root, string source) {
        string[] propertyNames = ReadSortedPropertyNames(root);

        Assert.True(root.TryGetProperty("runtimeEpochId", out JsonElement runtimeEpochId), $"{source}: runtimeEpochId should be present.");
        Assert.Equal(JsonValueKind.String, runtimeEpochId.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(runtimeEpochId.GetString()), $"{source}: runtimeEpochId should be non-empty.");
        Assert.Equal(
            [
                "actorId",
                "actorName",
                "endLocationId",
                "endLocationName",
                "runtimeEpochId",
                "startLocationId",
                "startLocationName",
                "stepCount",
                "steps",
                "totalTravelCost",
            ],
            propertyNames
        );
    }

    private static void AssertLocationObservationJsonUsesCanonicalShape(JsonElement root, string source) {
        string[] propertyNames = ReadSortedPropertyNames(root);

        Assert.True(root.TryGetProperty("locationId", out _), $"{source}: locationId should be present.");
        Assert.True(root.TryGetProperty("locationName", out _), $"{source}: locationName should be present.");
        Assert.True(root.TryGetProperty("locationDescription", out _), $"{source}: locationDescription should be present.");
        Assert.True(root.TryGetProperty("exits", out JsonElement exits), $"{source}: exits should be present.");
        Assert.True(root.TryGetProperty("presentActors", out JsonElement presentActors), $"{source}: presentActors should be present.");
        Assert.Equal(
            ["exits", "locationDescription", "locationId", "locationName", "presentActors"],
            propertyNames
        );

        AssertExitObservationArrayUsesCanonicalShape(exits, $"{source}: exits");
        AssertActorPresenceArrayUsesCanonicalShape(presentActors, $"{source}: presentActors");
    }

    private static void AssertExitObservationArrayUsesCanonicalShape(JsonElement exits, string source) {
        Assert.Equal(JsonValueKind.Array, exits.ValueKind);

        int index = 0;
        foreach (JsonElement exit in exits.EnumerateArray()) {
            string itemSource = $"{source}[{index}]";
            string[] propertyNames = ReadSortedPropertyNames(exit);

            Assert.True(exit.TryGetProperty("passageId", out _), $"{itemSource}: passageId should be present.");
            Assert.True(exit.TryGetProperty("exitName", out _), $"{itemSource}: exitName should be present.");
            Assert.True(exit.TryGetProperty("targetLocationId", out _), $"{itemSource}: targetLocationId should be present.");
            Assert.True(exit.TryGetProperty("targetLocationName", out _), $"{itemSource}: targetLocationName should be present.");
            Assert.True(exit.TryGetProperty("travelMode", out _), $"{itemSource}: travelMode should be present.");
            Assert.True(exit.TryGetProperty("baseTravelCost", out _), $"{itemSource}: baseTravelCost should be present.");
            Assert.True(exit.TryGetProperty("travelCostModifier", out _), $"{itemSource}: travelCostModifier should be present.");
            Assert.True(exit.TryGetProperty("totalTravelCost", out _), $"{itemSource}: totalTravelCost should be present.");
            Assert.True(exit.TryGetProperty("sharedConditionNote", out _), $"{itemSource}: sharedConditionNote should be present.");
            Assert.True(exit.TryGetProperty("directionalConditionNote", out _), $"{itemSource}: directionalConditionNote should be present.");
            Assert.True(exit.TryGetProperty("localViewNote", out _), $"{itemSource}: localViewNote should be present.");
            Assert.True(exit.TryGetProperty("isEnabled", out _), $"{itemSource}: isEnabled should be present.");
            Assert.Equal(
                [
                    "baseTravelCost",
                    "directionalConditionNote",
                    "exitName",
                    "isEnabled",
                    "localViewNote",
                    "passageId",
                    "sharedConditionNote",
                    "targetLocationId",
                    "targetLocationName",
                    "totalTravelCost",
                    "travelCostModifier",
                    "travelMode",
                ],
                propertyNames
            );

            index++;
        }
    }

    private static void AssertActorPresenceArrayUsesCanonicalShape(JsonElement presentActors, string source) {
        Assert.Equal(JsonValueKind.Array, presentActors.ValueKind);

        int index = 0;
        foreach (JsonElement actor in presentActors.EnumerateArray()) {
            string itemSource = $"{source}[{index}]";
            string[] propertyNames = ReadSortedPropertyNames(actor);

            Assert.True(actor.TryGetProperty("actorId", out _), $"{itemSource}: actorId should be present.");
            Assert.True(actor.TryGetProperty("actorName", out _), $"{itemSource}: actorName should be present.");
            Assert.Equal(
                ["actorId", "actorName"],
                propertyNames
            );

            index++;
        }
    }

    private static string[] ReadSortedPropertyNames(JsonElement element) {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);

        return element.EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertJsonEquivalent(string cliJson, string hostJson, string contractName) {
        using JsonDocument cli = JsonDocument.Parse(cliJson);
        using JsonDocument host = JsonDocument.Parse(hostJson);

        string? difference = FindJsonDifference(cli.RootElement, host.RootElement, "$");
        Assert.True(
            difference is null,
            $"Cross-host parity mismatch for {contractName}: {difference}{Environment.NewLine}CLI JSON:{Environment.NewLine}{cliJson}{Environment.NewLine}GameServer JSON:{Environment.NewLine}{hostJson}"
        );
    }

    private static void AssertJsonEquivalentIgnoringTopLevelProperty(
        string cliJson,
        string hostJson,
        string contractName,
        string ignoredTopLevelPropertyName
    ) {
        using JsonDocument cli = JsonDocument.Parse(cliJson);
        using JsonDocument host = JsonDocument.Parse(hostJson);

        string? difference = FindJsonDifferenceIgnoringTopLevelProperty(
            cli.RootElement,
            host.RootElement,
            "$",
            ignoredTopLevelPropertyName
        );
        Assert.True(
            difference is null,
            $"Cross-host parity mismatch for {contractName} (ignoring top-level property '{ignoredTopLevelPropertyName}'): {difference}{Environment.NewLine}CLI JSON:{Environment.NewLine}{cliJson}{Environment.NewLine}GameServer JSON:{Environment.NewLine}{hostJson}"
        );
    }

    private static string? FindJsonDifference(JsonElement expected, JsonElement actual, string path) {
        if (expected.ValueKind != actual.ValueKind) {
            return $"{path}: expected {expected.ValueKind}, actual {actual.ValueKind}.";
        }

        switch (expected.ValueKind) {
            case JsonValueKind.Object:
                JsonProperty[] expectedProperties = expected.EnumerateObject()
                    .OrderBy(static property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                JsonProperty[] actualProperties = actual.EnumerateObject()
                    .OrderBy(static property => property.Name, StringComparer.Ordinal)
                    .ToArray();

                if (expectedProperties.Length != actualProperties.Length) {
                    return $"{path}: expected {expectedProperties.Length} properties, actual {actualProperties.Length}.";
                }

                for (int i = 0; i < expectedProperties.Length; i++) {
                    JsonProperty expectedProperty = expectedProperties[i];
                    JsonProperty actualProperty = actualProperties[i];

                    if (!string.Equals(expectedProperty.Name, actualProperty.Name, StringComparison.Ordinal)) {
                        return $"{path}: expected property '{expectedProperty.Name}', actual property '{actualProperty.Name}'.";
                    }

                    string? nestedDifference = FindJsonDifference(
                        expectedProperty.Value,
                        actualProperty.Value,
                        $"{path}.{expectedProperty.Name}"
                    );
                    if (nestedDifference is not null) {
                        return nestedDifference;
                    }
                }

                return null;
            case JsonValueKind.Array:
                JsonElement[] expectedItems = expected.EnumerateArray().ToArray();
                JsonElement[] actualItems = actual.EnumerateArray().ToArray();

                if (expectedItems.Length != actualItems.Length) {
                    return $"{path}: expected array length {expectedItems.Length}, actual {actualItems.Length}.";
                }

                for (int i = 0; i < expectedItems.Length; i++) {
                    string? nestedDifference = FindJsonDifference(expectedItems[i], actualItems[i], $"{path}[{i}]");
                    if (nestedDifference is not null) {
                        return nestedDifference;
                    }
                }

                return null;
            case JsonValueKind.String:
                return string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal)
                    ? null
                    : $"{path}: expected string '{expected.GetString()}', actual '{actual.GetString()}'.";
            case JsonValueKind.Number:
                return NumbersAreEquivalent(expected, actual)
                    ? null
                    : $"{path}: expected number {expected.GetRawText()}, actual {actual.GetRawText()}.";
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return null;
            default:
                return string.Equals(expected.GetRawText(), actual.GetRawText(), StringComparison.Ordinal)
                    ? null
                    : $"{path}: expected {expected.GetRawText()}, actual {actual.GetRawText()}.";
        }
    }

    private static string? FindJsonDifferenceIgnoringTopLevelProperty(
        JsonElement expected,
        JsonElement actual,
        string path,
        string ignoredTopLevelPropertyName
    ) {
        if (path == "$") {
            if (expected.ValueKind != JsonValueKind.Object || actual.ValueKind != JsonValueKind.Object) {
                return FindJsonDifference(expected, actual, path);
            }

            JsonProperty[] expectedProperties = expected.EnumerateObject()
                .Where(property => !string.Equals(property.Name, ignoredTopLevelPropertyName, StringComparison.Ordinal))
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .ToArray();
            JsonProperty[] actualProperties = actual.EnumerateObject()
                .Where(property => !string.Equals(property.Name, ignoredTopLevelPropertyName, StringComparison.Ordinal))
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .ToArray();

            if (expectedProperties.Length != actualProperties.Length) {
                return $"{path}: expected {expectedProperties.Length} properties, actual {actualProperties.Length}, after ignoring top-level property '{ignoredTopLevelPropertyName}'.";
            }

            for (int i = 0; i < expectedProperties.Length; i++) {
                JsonProperty expectedProperty = expectedProperties[i];
                JsonProperty actualProperty = actualProperties[i];

                if (!string.Equals(expectedProperty.Name, actualProperty.Name, StringComparison.Ordinal)) {
                    return $"{path}: expected property '{expectedProperty.Name}', actual property '{actualProperty.Name}', after ignoring top-level property '{ignoredTopLevelPropertyName}'.";
                }

                string? nestedDifference = FindJsonDifference(
                    expectedProperty.Value,
                    actualProperty.Value,
                    $"{path}.{expectedProperty.Name}"
                );
                if (nestedDifference is not null) {
                    return nestedDifference;
                }
            }

            return null;
        }

        return FindJsonDifference(expected, actual, path);
    }

    private static bool NumbersAreEquivalent(JsonElement expected, JsonElement actual) {
        string expectedRaw = expected.GetRawText();
        string actualRaw = actual.GetRawText();

        if (decimal.TryParse(expectedRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal expectedDecimal)
            && decimal.TryParse(actualRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal actualDecimal)) {
            return expectedDecimal == actualDecimal;
        }

        if (double.TryParse(expectedRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double expectedDouble)
            && double.TryParse(actualRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double actualDouble)) {
            return expectedDouble.Equals(actualDouble);
        }

        return string.Equals(expectedRaw, actualRaw, StringComparison.Ordinal);
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-cross-host-parity-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static void WaitUntilSessionCanReopen(string repoDir) {
        InvalidOperationException? lastLockFailure = null;

        for (int attempt = 0; attempt < 40; attempt++) {
            try {
                using var session = SerialWorldRuntime.OpenExisting(repoDir);
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Failed to acquire lock", StringComparison.Ordinal)) {
                lastLockFailure = ex;
                Thread.Sleep(50);
            }
        }

        throw new InvalidOperationException(
            $"Timed out waiting for repo '{repoDir}' to become reopenable after setup.",
            lastLockFailure
        );
    }

    private sealed record CliRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError
    );

    private sealed class TextAdv2GameServerFactory(string repoDir, string bootstrapMode) : WebApplicationFactory<Program> {
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseSetting("TextAdv2:RepoDir", repoDir);
            builder.UseSetting("TextAdv2:BootstrapMode", bootstrapMode);
        }
    }
}
