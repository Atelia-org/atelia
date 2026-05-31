using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Session;
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

            AssertJsonEquivalent(cliJson, hostJson, "observe actor context");
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
    public async Task RouteTraceJson_ParityBetweenGameServerAndCliJsonOnlyAsync() {
        string cliRepoDir = CreateTempRepoDir();
        string hostRepoDir = CreateTempRepoDir();

        try {
            PrepareSampleRepo(cliRepoDir);
            PrepareSampleRepo(hostRepoDir);

            using var factory = CreateFactory(hostRepoDir);
            using var client = factory.CreateClient();

            string cliJson = RunCliAndReadSuccessfulJson("--repo-dir", cliRepoDir, "--json-only", "--trace-actor-route-json", "scout");
            using var traceResponse = await client.GetAsync("/actors/scout/route-trace/json");
            string hostJson = await ReadSuccessfulHostJsonAsync(traceResponse);

            AssertJsonEquivalent(cliJson, hostJson, "route trace json");
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

    private static void PrepareSampleRepo(string repoDir) {
        using (SampleWorldBootstrap.CreateFreshSession(repoDir)) {
        }

        WaitUntilSessionCanReopen(repoDir);
    }

    private static void PrepareEmptyRepo(string repoDir) {
        using (WorldSession.CreateEmpty(repoDir)) {
        }

        WaitUntilSessionCanReopen(repoDir);
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

    private static void AssertJsonEquivalent(string cliJson, string hostJson, string contractName) {
        using JsonDocument cli = JsonDocument.Parse(cliJson);
        using JsonDocument host = JsonDocument.Parse(hostJson);

        string? difference = FindJsonDifference(cli.RootElement, host.RootElement, "$");
        Assert.True(
            difference is null,
            $"Cross-host parity mismatch for {contractName}: {difference}{Environment.NewLine}CLI JSON:{Environment.NewLine}{cliJson}{Environment.NewLine}GameServer JSON:{Environment.NewLine}{hostJson}"
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
                using var session = WorldSession.OpenExisting(repoDir);
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
