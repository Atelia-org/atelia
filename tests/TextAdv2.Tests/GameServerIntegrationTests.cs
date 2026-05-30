using System.Net;
using System.Text.Json;
using Atelia.TextAdv2.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class GameServerIntegrationTests {
    [Fact]
    public async Task AdminWorld_ReturnsPlainTextWorldDumpAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/admin/world");
            string text = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("WORLD", text, StringComparison.Ordinal);
            Assert.Contains("actors=2", text, StringComparison.Ordinal);
            Assert.Contains("locations=7", text, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task MoveTraceAndReset_RoundTripsThroughRuntimeBackedEndpointsAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var initialObserve = await client.GetAsync("/actors/scout/observation");
            var initialJson = JsonDocument.Parse(await initialObserve.Content.ReadAsStringAsync());
            Assert.Equal("square", GetActorLocationId(initialJson));

            using var moveResponse = await client.PostAsync("/actors/scout/moves/square-ridge-trail", content: null);
            string moveText = await moveResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);
            Assert.Equal("application/json", moveResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains("\"ToLocationId\": \"ridge\"", moveText, StringComparison.Ordinal);

            using var traceResponse = await client.GetAsync("/actors/scout/route-trace");
            string traceText = await traceResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, traceResponse.StatusCode);
            Assert.Equal("text/plain", traceResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains("1. square --north gate/square-ridge-trail--> ridge | land | cost=5", traceText, StringComparison.Ordinal);

            using var resetResponse = await client.PostAsync("/admin/reset-sample-world", content: null);
            string resetText = await resetResponse.Content.ReadAsStringAsync();
            var resetJson = JsonDocument.Parse(resetText);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
            Assert.Equal("runtime-connected", resetJson.RootElement.GetProperty("mode").GetString());

            using var reopenedObserve = await client.GetAsync("/actors/scout/observation");
            var reopenedJson = JsonDocument.Parse(await reopenedObserve.Content.ReadAsStringAsync());
            Assert.Equal("square", GetActorLocationId(reopenedJson));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task InvalidMove_ReturnsBadRequestWithErrorPayloadAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/actors/scout/moves/harbor-delta-current", content: null);
            string jsonText = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(jsonText);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("does not connect location", json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RuntimeStatus_ReportsResolvedRepoAndConnectedModeAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/admin/runtime-status");
            string jsonText = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(jsonText);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("runtime-connected", json.RootElement.GetProperty("mode").GetString());
            Assert.Equal(repoDir, json.RootElement.GetProperty("configuration").GetProperty("resolvedRepoDir").GetString());
            Assert.True(json.RootElement.GetProperty("runtime").GetProperty("runtimeExtracted").GetBoolean());
            Assert.Contains(
                json.RootElement.GetProperty("runtime").GetProperty("notes").EnumerateArray().Select(static x => x.GetString()),
                static note => note is not null
                    && note.Contains("宿主仍自行负责 CLI/HTTP 请求到 runtime method 的分发。", StringComparison.Ordinal)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task TimeEndpoints_AdvanceAndResetLogicalClockAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var initialTime = await client.GetAsync("/admin/time");
            string initialText = await initialTime.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, initialTime.StatusCode);
            Assert.Contains("\"CurrentTick\":0", initialText.Replace(" ", string.Empty), StringComparison.Ordinal);

            using var advancedTime = await client.PostAsync("/admin/advance-time/9", content: null);
            string advancedText = await advancedTime.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, advancedTime.StatusCode);
            Assert.Contains("\"CurrentTick\":9", advancedText.Replace(" ", string.Empty), StringComparison.Ordinal);

            using var resetResponse = await client.PostAsync("/admin/reset-sample-world", content: null);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

            using var resetTime = await client.GetAsync("/admin/time");
            string resetTimeText = await resetTime.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, resetTime.StatusCode);
            Assert.Contains("\"CurrentTick\":0", resetTimeText.Replace(" ", string.Empty), StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task AdminLocationAndRouteEndpoints_AreScopedUnderAdminPrefixAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var adminLocation = await client.GetAsync("/admin/locations/square");
            string adminLocationText = await adminLocation.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, adminLocation.StatusCode);
            Assert.Contains("Square", adminLocationText, StringComparison.Ordinal);

            using var publicLocation = await client.GetAsync("/locations/square");
            Assert.Equal(HttpStatusCode.NotFound, publicLocation.StatusCode);

            using var adminRoute = await client.GetAsync("/admin/routes/village/aerie");
            string adminRouteText = await adminRoute.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, adminRoute.StatusCode);
            Assert.Contains("ROUTE PLAN from=village (Village) to=aerie (Aerie) status=found", adminRouteText, StringComparison.Ordinal);

            using var adminAcceleration = await client.GetAsync("/admin/route-acceleration");
            string adminAccelerationText = await adminAcceleration.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, adminAcceleration.StatusCode);
            Assert.Contains("\"PlannerMode\":\"zero\"", adminAccelerationText.Replace(" ", string.Empty), StringComparison.Ordinal);

            using var rebuiltAcceleration = await client.PostAsync("/admin/route-acceleration/rebuild", content: null);
            string rebuiltAccelerationText = await rebuiltAcceleration.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, rebuiltAcceleration.StatusCode);
            Assert.Contains("\"PlannerMode\":\"landmark\"", rebuiltAccelerationText.Replace(" ", string.Empty), StringComparison.Ordinal);
            Assert.Contains("\"LandmarkProfileName\":\"sample-world-default\"", rebuiltAccelerationText.Replace(" ", string.Empty), StringComparison.Ordinal);

            using var publicRoute = await client.GetAsync("/routes/village/aerie");
            Assert.Equal(HttpStatusCode.NotFound, publicRoute.StatusCode);

            using var publicAcceleration = await client.GetAsync("/route-acceleration");
            Assert.Equal(HttpStatusCode.NotFound, publicAcceleration.StatusCode);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task HostRestart_ReopensWorldTruthButResetsRuntimeOwnedStateAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            await using (var firstFactory = CreateFactory(repoDir)) {
                using var firstClient = firstFactory.CreateClient();

                using var advancedTime = await firstClient.PostAsync("/admin/advance-time/13", content: null);
                using var movedActor = await firstClient.PostAsync("/actors/scout/moves/square-ridge-trail", content: null);

                Assert.Equal(HttpStatusCode.OK, advancedTime.StatusCode);
                Assert.Equal(HttpStatusCode.OK, movedActor.StatusCode);
            }

            WaitUntilRuntimeCanReopen(repoDir);

            await using var secondFactory = CreateFactory(repoDir);
            using var secondClient = secondFactory.CreateClient();

            using var timeAfterRestart = await secondClient.GetAsync("/admin/time");
            string timeText = await timeAfterRestart.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, timeAfterRestart.StatusCode);
            Assert.Contains("\"CurrentTick\":0", timeText.Replace(" ", string.Empty), StringComparison.Ordinal);

            using var traceAfterRestart = await secondClient.GetAsync("/actors/scout/route-trace");
            string traceText = await traceAfterRestart.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, traceAfterRestart.StatusCode);
            Assert.Contains("start=ridge (Ridge)", traceText, StringComparison.Ordinal);
            Assert.Contains("<no movement in this run>", traceText, StringComparison.Ordinal);
            Assert.Contains("end=ridge (Ridge) | steps=0 | totalCost=0", traceText, StringComparison.Ordinal);

            using var observedAfterRestart = await secondClient.GetAsync("/actors/scout/observation");
            var observedJson = JsonDocument.Parse(await observedAfterRestart.Content.ReadAsStringAsync());
            Assert.Equal("ridge", GetActorLocationId(observedJson));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static TextAdv2GameServerFactory CreateFactory(string repoDir) => new(repoDir);

    private static string GetActorLocationId(JsonDocument json)
        => json.RootElement.GetProperty("Location").GetProperty("LocationId").GetString()
            ?? throw new InvalidOperationException("Actor observation did not contain Location.LocationId.");

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-gameserver-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    private static void WaitUntilRuntimeCanReopen(string repoDir) {
        InvalidOperationException? lastLockFailure = null;

        for (int attempt = 0; attempt < 40; attempt++) {
            try {
                using var runtime = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir);
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Failed to acquire lock", StringComparison.Ordinal)) {
                lastLockFailure = ex;
                Thread.Sleep(50);
            }
        }

        throw new InvalidOperationException(
            $"Timed out waiting for repo '{repoDir}' to become reopenable after host shutdown.",
            lastLockFailure
        );
    }

    private sealed class TextAdv2GameServerFactory(string repoDir) : WebApplicationFactory<Program> {
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseSetting("TextAdv2:RepoDir", repoDir);
            builder.UseSetting("TextAdv2:AutoBootstrapSampleWorld", "true");
        }
    }
}
