using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Session;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class GameServerIntegrationTests {
    private static readonly JsonSerializerOptions HostJsonOptions = CreateHostJsonOptions();
    private const string SampleWorldDevBootstrapMode = "sample-world-dev";
    private const string OpenExistingOnlyBootstrapMode = "open-existing-only";

    [Fact]
    public async Task AdminWorld_ReturnsPlainTextWorldDumpAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var expectedSession = SampleWorldBootstrap.CreateTemporarySession();
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/admin/world");
            string text = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(
                Normalize(DevTextRenderer.RenderWorld(expectedSession)),
                Normalize(text)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task MoveTraceAndReset_RoundTripsThroughSessionBackedEndpointsAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var initialObserve = await client.GetAsync("/actors/scout/observation");
            var initialObservation = await ReadJsonAsync<ActorLocationObservation>(initialObserve);
            Assert.NotNull(initialObservation);
            Assert.Equal("square", initialObservation.Location.LocationId);

            using var moveResponse = await client.PostAsync("/actors/scout/moves/square-ridge-trail", content: null);
            string moveText = await moveResponse.Content.ReadAsStringAsync();
            var moveJson = JsonSerializer.Deserialize<ActorMoveResult>(moveText, HostJsonOptions);
            Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);
            Assert.Equal("application/json", moveResponse.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(moveJson);
            Assert.Contains("\"actorId\"", moveText, StringComparison.Ordinal);
            Assert.DoesNotContain("\"ActorId\"", moveText, StringComparison.Ordinal);
            Assert.Contains("\"travelMode\": \"land\"", moveText, StringComparison.Ordinal);
            Assert.Equal("scout", moveJson.ActorId);
            Assert.Equal("ridge", moveJson.ToLocationId);
            Assert.Equal(TravelMode.Land, moveJson.TravelMode);
            Assert.Equal("ridge", moveJson.CurrentLocation.LocationId);

            using var traceResponse = await client.GetAsync("/actors/scout/route-trace");
            string traceText = await traceResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, traceResponse.StatusCode);
            Assert.Equal("text/plain", traceResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains("1. square --north gate/square-ridge-trail--> ridge | land | cost=5", traceText, StringComparison.Ordinal);

            using var resetResponse = await client.PostAsync("/admin/reset-sample-world", content: null);
            string resetText = await resetResponse.Content.ReadAsStringAsync();
            var resetJson = JsonDocument.Parse(resetText);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
            Assert.Equal("host-running", resetJson.RootElement.GetProperty("mode").GetString());

            using var reopenedObserve = await client.GetAsync("/actors/scout/observation");
            var reopenedObservation = await ReadJsonAsync<ActorLocationObservation>(reopenedObserve);
            Assert.NotNull(reopenedObservation);
            Assert.Equal("square", reopenedObservation.Location.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task ActorContextEndpoint_ReturnsStructuredContext_AndUpdatesAfterMoveAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var initialResponse = await client.GetAsync("/actors/scout/context");
            var initialContext = await ReadJsonAsync<ActorContextObservation>(initialResponse);

            Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
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

            using var moveResponse = await client.PostAsync("/actors/scout/moves/square-ridge-trail", content: null);
            Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);

            using var movedResponse = await client.GetAsync("/actors/scout/context");
            var movedContext = await ReadJsonAsync<ActorContextObservation>(movedResponse);

            Assert.Equal(HttpStatusCode.OK, movedResponse.StatusCode);
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
    public async Task ActorContextEndpoint_TracksLogicalTimeAndSharedPresenceAcrossRestartAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using (WorldSession.CreateEmpty(repoDir)) {
            }
            WaitUntilSessionCanReopen(repoDir);

            using (var factory = CreateFactory(repoDir, OpenExistingOnlyBootstrapMode))
            using (var client = factory.CreateClient()) {
                _ = await client.PostAsJsonAsync(
                    "/admin/locations",
                    new {
                        id = "start",
                        name = "Start",
                        description = "Shared-world start.",
                    }
                );
                _ = await client.PostAsJsonAsync(
                    "/admin/locations",
                    new {
                        id = "goal",
                        name = "Goal",
                        description = "Shared-world goal.",
                    }
                );
                _ = await client.PostAsJsonAsync(
                    "/admin/actors",
                    new {
                        id = "alpha",
                        name = "Alpha",
                        currentLocationId = "start",
                    }
                );
                _ = await client.PostAsJsonAsync(
                    "/admin/actors",
                    new {
                        id = "beta",
                        name = "Beta",
                        currentLocationId = "goal",
                    }
                );
                _ = await client.PostAsJsonAsync(
                    "/admin/passages",
                    new {
                        id = "start-goal",
                        locationAId = "start",
                        exitNameFromA = "advance",
                        locationBId = "goal",
                        exitNameFromB = "return",
                    }
                );

                using var advanceTime = await client.PostAsync("/admin/advance-time/7", content: null);
                Assert.Equal(HttpStatusCode.OK, advanceTime.StatusCode);

                using var moveResponse = await client.PostAsync("/actors/alpha/moves/start-goal", content: null);
                Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);

                using var contextResponse = await client.GetAsync("/actors/beta/context");
                var context = await ReadJsonAsync<ActorContextObservation>(contextResponse);

                Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
                Assert.NotNull(context);
                Assert.Equal("beta", context.ActorId);
                Assert.Equal("Beta", context.ActorName);
                Assert.Equal(7, context.CurrentTick);
                Assert.Equal("goal", context.CurrentLocation.LocationId);
                Assert.Equal(["alpha", "beta"], ReadActorIds(context.CurrentLocation.PresentActors));
                Assert.Equal(["start-goal"], context.AvailableMoves.Select(static edge => edge.PassageId).ToArray());
                Assert.Equal(["start-goal"], context.CurrentLocation.Exits.Select(static edge => edge.PassageId).ToArray());
            }

            WaitUntilSessionCanReopen(repoDir);

            using var reopenedFactory = CreateFactory(repoDir, OpenExistingOnlyBootstrapMode);
            using var reopenedClient = reopenedFactory.CreateClient();

            using var reopenedContextResponse = await reopenedClient.GetAsync("/actors/beta/context");
            var reopenedContext = await ReadJsonAsync<ActorContextObservation>(reopenedContextResponse);

            Assert.Equal(HttpStatusCode.OK, reopenedContextResponse.StatusCode);
            Assert.NotNull(reopenedContext);
            Assert.Equal(7, reopenedContext.CurrentTick);
            Assert.Equal("goal", reopenedContext.CurrentLocation.LocationId);
            Assert.Equal(["alpha", "beta"], ReadActorIds(reopenedContext.CurrentLocation.PresentActors));
            Assert.Equal(["start-goal"], reopenedContext.AvailableMoves.Select(static edge => edge.PassageId).ToArray());
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
            Assert.Contains("not connected by passage", json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task SessionStatus_ReportsResolvedRepoAndDevHostPolicyAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/admin/session-status");
            string jsonText = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(jsonText);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("host-running", json.RootElement.GetProperty("mode").GetString());
            Assert.Equal(repoDir, json.RootElement.GetProperty("configuration").GetProperty("resolvedRepoDir").GetString());
            Assert.Equal("sample-world-dev", json.RootElement.GetProperty("configuration").GetProperty("bootstrapMode").GetString());
            Assert.Equal("sample-world-dev", json.RootElement.GetProperty("hostPolicy").GetProperty("bootstrapMode").GetString());
            Assert.Equal("open-or-create-sample-world", json.RootElement.GetProperty("hostPolicy").GetProperty("sessionOpenMode").GetString());
            Assert.True(json.RootElement.GetProperty("hostPolicy").GetProperty("sampleWorldResetEnabled").GetBoolean());
            Assert.Contains(
                json.RootElement.GetProperty("plannedEndpoints").EnumerateArray().Select(static x => x.GetString()),
                static endpoint => string.Equals(endpoint, "POST /admin/reset-sample-world", StringComparison.Ordinal)
            );
            Assert.Contains(
                json.RootElement.GetProperty("plannedEndpoints").EnumerateArray().Select(static x => x.GetString()),
                static endpoint => string.Equals(endpoint, "GET /actors/{actorId}/context", StringComparison.Ordinal)
            );
            Assert.Equal("Atelia.TextAdv2", json.RootElement.GetProperty("session").GetProperty("engineAssemblyName").GetString());
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task OpenExistingOnlyMode_OpensExistingRepoAndHidesResetEndpointAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using (SampleWorldBootstrap.CreateFreshSession(repoDir)) {
            }
            WaitUntilSessionCanReopen(repoDir);

            using var factory = CreateFactory(repoDir, OpenExistingOnlyBootstrapMode);
            using var client = factory.CreateClient();

            using var worldResponse = await client.GetAsync("/admin/world");
            string worldText = await worldResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, worldResponse.StatusCode);
            Assert.Contains("WORLD", worldText, StringComparison.Ordinal);

            using var resetResponse = await client.PostAsync("/admin/reset-sample-world", content: null);
            Assert.Equal(HttpStatusCode.NotFound, resetResponse.StatusCode);

            using var statusResponse = await client.GetAsync("/admin/session-status");
            string statusText = await statusResponse.Content.ReadAsStringAsync();
            var statusJson = JsonDocument.Parse(statusText);

            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            Assert.Equal("open-existing-only", statusJson.RootElement.GetProperty("configuration").GetProperty("bootstrapMode").GetString());
            Assert.Equal("open-existing-only", statusJson.RootElement.GetProperty("hostPolicy").GetProperty("bootstrapMode").GetString());
            Assert.Equal("open-existing-only", statusJson.RootElement.GetProperty("hostPolicy").GetProperty("sessionOpenMode").GetString());
            Assert.False(statusJson.RootElement.GetProperty("hostPolicy").GetProperty("sampleWorldResetEnabled").GetBoolean());
            Assert.DoesNotContain(
                statusJson.RootElement.GetProperty("plannedEndpoints").EnumerateArray().Select(static x => x.GetString()),
                static endpoint => string.Equals(endpoint, "POST /admin/reset-sample-world", StringComparison.Ordinal)
            );
            Assert.Contains(
                statusJson.RootElement.GetProperty("plannedEndpoints").EnumerateArray().Select(static x => x.GetString()),
                static endpoint => string.Equals(endpoint, "POST /admin/locations", StringComparison.Ordinal)
            );
            Assert.Contains(
                statusJson.RootElement.GetProperty("plannedEndpoints").EnumerateArray().Select(static x => x.GetString()),
                static endpoint => string.Equals(endpoint, "POST /admin/actors", StringComparison.Ordinal)
            );
            Assert.Contains(
                statusJson.RootElement.GetProperty("plannedEndpoints").EnumerateArray().Select(static x => x.GetString()),
                static endpoint => string.Equals(endpoint, "POST /admin/passages", StringComparison.Ordinal)
            );
            Assert.Contains(
                statusJson.RootElement.GetProperty("plannedEndpoints").EnumerateArray().Select(static x => x.GetString()),
                static endpoint => string.Equals(endpoint, "GET /actors/{actorId}/context", StringComparison.Ordinal)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task OpenExistingOnlyMode_AdminAuthoringEndpoints_SupportEmptyRepoWorkflowAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using (WorldSession.CreateEmpty(repoDir)) {
            }
            WaitUntilSessionCanReopen(repoDir);

            using var factory = CreateFactory(repoDir, OpenExistingOnlyBootstrapMode);
            using var client = factory.CreateClient();

            using var createStartResponse = await client.PostAsJsonAsync(
                "/admin/locations",
                new {
                    id = "start",
                    name = "Start",
                    description = "Start of the authored route.",
                }
            );
            var createdStart = await ReadJsonAsync<LocationAuthoringSnapshot>(createStartResponse);
            Assert.Equal(HttpStatusCode.OK, createStartResponse.StatusCode);
            Assert.NotNull(createdStart);
            Assert.Equal("start", createdStart.LocationId);
            Assert.Equal("Start", createdStart.LocationName);

            using var createGoalResponse = await client.PostAsJsonAsync(
                "/admin/locations",
                new {
                    id = "goal",
                    name = "Goal",
                    description = "Goal of the authored route.",
                }
            );
            var createdGoal = await ReadJsonAsync<LocationAuthoringSnapshot>(createGoalResponse);
            Assert.Equal(HttpStatusCode.OK, createGoalResponse.StatusCode);
            Assert.NotNull(createdGoal);
            Assert.Equal("goal", createdGoal.LocationId);

            using var createActorResponse = await client.PostAsJsonAsync(
                "/admin/actors",
                new {
                    id = "runner",
                    name = "Runner",
                    currentLocationId = "start",
                }
            );
            var createdActor = await ReadJsonAsync<ActorAuthoringSnapshot>(createActorResponse);
            Assert.Equal(HttpStatusCode.OK, createActorResponse.StatusCode);
            Assert.NotNull(createdActor);
            Assert.Equal("runner", createdActor.ActorId);
            Assert.Equal("start", createdActor.CurrentLocationId);

            using var createPassageResponse = await client.PostAsJsonAsync(
                "/admin/passages",
                new {
                    id = "start-goal",
                    locationAId = "start",
                    exitNameFromA = "go",
                    locationBId = "goal",
                    exitNameFromB = "back",
                }
            );
            string createPassageText = await createPassageResponse.Content.ReadAsStringAsync();
            var createdPassage = JsonSerializer.Deserialize<PassageAuthoringSnapshot>(createPassageText, HostJsonOptions);
            Assert.Equal(HttpStatusCode.OK, createPassageResponse.StatusCode);
            Assert.Equal("application/json", createPassageResponse.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(createdPassage);
            Assert.Contains("\"passageId\"", createPassageText, StringComparison.Ordinal);
            Assert.DoesNotContain("\"PassageId\"", createPassageText, StringComparison.Ordinal);
            Assert.Contains("\"travelMode\": \"land\"", createPassageText, StringComparison.Ordinal);
            Assert.Equal("start-goal", createdPassage.PassageId);
            Assert.Equal(TravelMode.Land, createdPassage.TravelMode);
            Assert.Equal(1, createdPassage.BaseTravelCost);

            using var planResponse = await client.GetAsync("/actors/runner/plan-route/goal");
            var plan = await ReadJsonAsync<LocationRoutePlanObservation>(planResponse);
            Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
            Assert.NotNull(plan);
            Assert.Equal(RoutePlanStatus.Found, plan.Status);
            Assert.Equal(1, plan.StepCount);
            Assert.Equal("start-goal", plan.Steps[0].PassageId);
            Assert.Equal(1, plan.TotalTravelCost);

            using var moveResponse = await client.PostAsync("/actors/runner/moves/start-goal", content: null);
            var move = await ReadJsonAsync<ActorMoveResult>(moveResponse);
            Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);
            Assert.NotNull(move);
            Assert.Equal("goal", move.ToLocationId);
            Assert.Equal(TravelMode.Land, move.TravelMode);

            using var observeResponse = await client.GetAsync("/actors/runner/observation");
            var observation = await ReadJsonAsync<ActorLocationObservation>(observeResponse);
            Assert.Equal(HttpStatusCode.OK, observeResponse.StatusCode);
            Assert.NotNull(observation);
            Assert.Equal("runner", observation.ActorId);
            Assert.Equal("goal", observation.Location.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task OpenExistingOnlyMode_AuthorMoveAndObserveOtherActor_ProjectsSharedWorldStateAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using (WorldSession.CreateEmpty(repoDir)) {
            }
            WaitUntilSessionCanReopen(repoDir);

            using var factory = CreateFactory(repoDir, OpenExistingOnlyBootstrapMode);
            using var client = factory.CreateClient();

            _ = await client.PostAsJsonAsync(
                "/admin/locations",
                new {
                    id = "start",
                    name = "Start",
                    description = "Shared-world start.",
                }
            );
            _ = await client.PostAsJsonAsync(
                "/admin/locations",
                new {
                    id = "goal",
                    name = "Goal",
                    description = "Shared-world goal.",
                }
            );
            _ = await client.PostAsJsonAsync(
                "/admin/actors",
                new {
                    id = "alpha",
                    name = "Alpha",
                    currentLocationId = "start",
                }
            );
            _ = await client.PostAsJsonAsync(
                "/admin/actors",
                new {
                    id = "beta",
                    name = "Beta",
                    currentLocationId = "goal",
                }
            );
            _ = await client.PostAsJsonAsync(
                "/admin/passages",
                new {
                    id = "start-goal",
                    locationAId = "start",
                    exitNameFromA = "advance",
                    locationBId = "goal",
                    exitNameFromB = "return",
                }
            );

            using var moveResponse = await client.PostAsync("/actors/alpha/moves/start-goal", content: null);
            var move = await ReadJsonAsync<ActorMoveResult>(moveResponse);
            Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);
            Assert.NotNull(move);
            Assert.Equal("goal", move.ToLocationId);
            Assert.Equal("goal", move.CurrentLocation.LocationId);
            Assert.Equal(["alpha", "beta"], ReadActorIds(move.CurrentLocation.PresentActors));

            using var sourceResponse = await client.GetAsync("/admin/locations/start/observation");
            var sourceObservation = await ReadJsonAsync<LocationObservation>(sourceResponse);
            Assert.Equal(HttpStatusCode.OK, sourceResponse.StatusCode);
            Assert.NotNull(sourceObservation);
            Assert.Empty(sourceObservation.PresentActors);

            using var goalResponse = await client.GetAsync("/admin/locations/goal/observation");
            var goalObservation = await ReadJsonAsync<LocationObservation>(goalResponse);
            Assert.Equal(HttpStatusCode.OK, goalResponse.StatusCode);
            Assert.NotNull(goalObservation);
            Assert.Equal(["alpha", "beta"], ReadActorIds(goalObservation.PresentActors));

            using var betaResponse = await client.GetAsync("/actors/beta/observation");
            var betaObservation = await ReadJsonAsync<ActorLocationObservation>(betaResponse);
            Assert.Equal(HttpStatusCode.OK, betaResponse.StatusCode);
            Assert.NotNull(betaObservation);
            Assert.Equal("goal", betaObservation.Location.LocationId);
            Assert.Equal(["alpha", "beta"], ReadActorIds(betaObservation.Location.PresentActors));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task AdminCreatePassage_InvalidTravelMode_ReturnsBadRequestWithErrorPayloadAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using (WorldSession.CreateEmpty(repoDir)) {
            }
            WaitUntilSessionCanReopen(repoDir);

            using var factory = CreateFactory(repoDir, OpenExistingOnlyBootstrapMode);
            using var client = factory.CreateClient();

            _ = await client.PostAsJsonAsync(
                "/admin/locations",
                new {
                    id = "start",
                    name = "Start",
                    description = "Start.",
                }
            );
            _ = await client.PostAsJsonAsync(
                "/admin/locations",
                new {
                    id = "goal",
                    name = "Goal",
                    description = "Goal.",
                }
            );

            using var response = await client.PostAsJsonAsync(
                "/admin/passages",
                new {
                    id = "start-goal",
                    locationAId = "start",
                    exitNameFromA = "go",
                    locationBId = "goal",
                    exitNameFromB = "back",
                    travelMode = "teleport",
                }
            );
            string jsonText = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(jsonText);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Unsupported travelMode", json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task AdminCreatePassage_ExplicitTravelModeAndCost_RoundTripThroughHttpBindingAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using (WorldSession.CreateEmpty(repoDir)) {
            }
            WaitUntilSessionCanReopen(repoDir);

            using var factory = CreateFactory(repoDir, OpenExistingOnlyBootstrapMode);
            using var client = factory.CreateClient();

            _ = await client.PostAsJsonAsync(
                "/admin/locations",
                new {
                    id = "start",
                    name = "Start",
                    description = "Start.",
                }
            );
            _ = await client.PostAsJsonAsync(
                "/admin/locations",
                new {
                    id = "goal",
                    name = "Goal",
                    description = "Goal.",
                }
            );

            using var createPassageResponse = await client.PostAsJsonAsync(
                "/admin/passages",
                new {
                    id = "start-goal",
                    locationAId = "start",
                    exitNameFromA = "skiff",
                    locationBId = "goal",
                    exitNameFromB = "dock",
                    travelMode = "  WATER ",
                    baseTravelCost = 3,
                }
            );
            string createPassageText = await createPassageResponse.Content.ReadAsStringAsync();
            var createdPassage = JsonSerializer.Deserialize<PassageAuthoringSnapshot>(createPassageText, HostJsonOptions);

            Assert.Equal(HttpStatusCode.OK, createPassageResponse.StatusCode);
            Assert.Equal("application/json", createPassageResponse.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(createdPassage);
            Assert.Contains("\"travelMode\": \"water\"", createPassageText, StringComparison.Ordinal);
            Assert.Equal(TravelMode.Water, createdPassage.TravelMode);
            Assert.Equal(3, createdPassage.BaseTravelCost);

            using var planResponse = await client.GetAsync("/admin/routes/start/goal");
            var plan = await ReadJsonAsync<LocationRoutePlanObservation>(planResponse);

            Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
            Assert.NotNull(plan);
            Assert.Equal(RoutePlanStatus.Found, plan.Status);
            Assert.Equal(3, plan.TotalTravelCost);
            Assert.Equal("start-goal", plan.Steps[0].PassageId);
            Assert.Equal(TravelMode.Water, plan.Steps[0].TravelMode);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task OpenExistingOnlyMode_SessionEndpointsFailWhenRepoDoesNotExistAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir, OpenExistingOnlyBootstrapMode);
            using var client = factory.CreateClient();

            using var statusResponse = await client.GetAsync("/admin/session-status");
            string statusText = await statusResponse.Content.ReadAsStringAsync();
            var statusJson = JsonDocument.Parse(statusText);
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            Assert.Equal("host-running", statusJson.RootElement.GetProperty("mode").GetString());

            using var healthzResponse = await client.GetAsync("/healthz");
            string healthzText = await healthzResponse.Content.ReadAsStringAsync();
            var healthzJson = JsonDocument.Parse(healthzText);
            Assert.Equal(HttpStatusCode.OK, healthzResponse.StatusCode);
            Assert.Equal("host-running", healthzJson.RootElement.GetProperty("mode").GetString());

            using var worldResponse = await client.GetAsync("/admin/world");
            Assert.Equal(HttpStatusCode.InternalServerError, worldResponse.StatusCode);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task OpenExistingOnlyMode_RebuildRouteAccelerationWithoutLandmarks_UsesWorldDerivedSampleWorldProfileAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using (SampleWorldBootstrap.CreateFreshSession(repoDir)) {
            }
            WaitUntilSessionCanReopen(repoDir);

            using var factory = CreateFactory(repoDir, OpenExistingOnlyBootstrapMode);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/admin/route-acceleration/rebuild", content: null);
            var json = await ReadJsonAsync<RouteAccelerationSnapshot>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(json);
            Assert.Equal("landmark", json.PlannerMode);
            Assert.Equal("sample-world-default", json.LandmarkProfileName);
            Assert.Equal(
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Harbor, TestWorldBuilder.LocationIds.Shrine],
                json.LandmarkLocationIds
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
            var initialTimeJson = JsonSerializer.Deserialize<LogicalTimeSnapshot>(initialText, HostJsonOptions);
            Assert.Equal(HttpStatusCode.OK, initialTime.StatusCode);
            Assert.Equal("application/json", initialTime.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(initialTimeJson);
            Assert.Equal(0, initialTimeJson.CurrentTick);

            using var advancedTime = await client.PostAsync("/admin/advance-time/9", content: null);
            string advancedText = await advancedTime.Content.ReadAsStringAsync();
            var advancedTimeJson = JsonSerializer.Deserialize<LogicalTimeSnapshot>(advancedText, HostJsonOptions);
            Assert.Equal(HttpStatusCode.OK, advancedTime.StatusCode);
            Assert.Equal("application/json", advancedTime.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(advancedTimeJson);
            Assert.Equal(9, advancedTimeJson.CurrentTick);

            using var resetResponse = await client.PostAsync("/admin/reset-sample-world", content: null);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

            using var resetTime = await client.GetAsync("/admin/time");
            string resetTimeText = await resetTime.Content.ReadAsStringAsync();
            var resetTimeJson = JsonSerializer.Deserialize<LogicalTimeSnapshot>(resetTimeText, HostJsonOptions);
            Assert.Equal(HttpStatusCode.OK, resetTime.StatusCode);
            Assert.Equal("application/json", resetTime.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(resetTimeJson);
            Assert.Equal(0, resetTimeJson.CurrentTick);
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
            Assert.Equal("text/plain", adminLocation.Content.Headers.ContentType?.MediaType);
            using (var expectedSession = SampleWorldBootstrap.CreateTemporarySession()) {
                Assert.Equal(
                    Normalize(DevTextRenderer.RenderLocation(expectedSession, TestWorldBuilder.LocationIds.Square)),
                    Normalize(adminLocationText)
                );
            }

            using var adminObservation = await client.GetAsync("/admin/locations/square/observation");
            var adminLocationObservation = await ReadJsonAsync<LocationObservation>(adminObservation);
            Assert.Equal(HttpStatusCode.OK, adminObservation.StatusCode);
            Assert.Equal("application/json", adminObservation.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(adminLocationObservation);
            Assert.Contains(
                adminLocationObservation.Exits,
                static exit => exit.PassageId == TestWorldBuilder.PassageIds.SquareShrineGate && exit.TravelMode == TravelMode.Portal
            );

            using var adminNavigation = await client.GetAsync("/admin/locations/square/navigation");
            var adminNavigationObservation = await ReadJsonAsync<LocationNavigationObservation>(adminNavigation);
            Assert.Equal(HttpStatusCode.OK, adminNavigation.StatusCode);
            Assert.Equal("application/json", adminNavigation.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(adminNavigationObservation);
            Assert.Collection(
                adminNavigationObservation.Edges,
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, edge.PassageId);
                    Assert.Equal(TravelMode.Land, edge.TravelMode);
                    Assert.Equal(5, edge.TravelCost);
                },
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareShrineGate, edge.PassageId);
                    Assert.Equal(TravelMode.Portal, edge.TravelMode);
                    Assert.Equal(1, edge.TravelCost);
                },
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.VillageSquareRoad, edge.PassageId);
                    Assert.Equal(TravelMode.Land, edge.TravelMode);
                    Assert.Equal(1, edge.TravelCost);
                }
            );

            using var publicLocation = await client.GetAsync("/locations/square");
            Assert.Equal(HttpStatusCode.NotFound, publicLocation.StatusCode);

            using var adminRoute = await client.GetAsync("/admin/routes/village/aerie");
            string adminRouteText = await adminRoute.Content.ReadAsStringAsync();
            var adminRouteJson = JsonSerializer.Deserialize<LocationRoutePlanObservation>(adminRouteText, HostJsonOptions);
            Assert.Equal(HttpStatusCode.OK, adminRoute.StatusCode);
            Assert.Equal("application/json", adminRoute.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(adminRouteJson);
            Assert.Contains("\"fromLocationId\"", adminRouteText, StringComparison.Ordinal);
            Assert.DoesNotContain("\"FromLocationId\"", adminRouteText, StringComparison.Ordinal);
            Assert.Equal(RoutePlanStatus.Found, adminRouteJson.Status);
            Assert.Equal(11, adminRouteJson.TotalTravelCost);
            Assert.Equal(TestWorldBuilder.PassageIds.RidgeAerieWinch, adminRouteJson.Steps[^1].PassageId);

            using var adminAcceleration = await client.GetAsync("/admin/route-acceleration");
            string adminAccelerationText = await adminAcceleration.Content.ReadAsStringAsync();
            var adminAccelerationJson = JsonSerializer.Deserialize<RouteAccelerationSnapshot>(adminAccelerationText, HostJsonOptions);
            Assert.Equal(HttpStatusCode.OK, adminAcceleration.StatusCode);
            Assert.Equal("application/json", adminAcceleration.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(adminAccelerationJson);
            Assert.Equal("zero", adminAccelerationJson.PlannerMode);
            Assert.Equal("inactive", adminAccelerationJson.SnapshotStatus);
            Assert.Equal("none", adminAccelerationJson.SnapshotKind);
            Assert.Empty(adminAccelerationJson.LandmarkLocationIds);

            using var rebuiltAcceleration = await client.PostAsync("/admin/route-acceleration/rebuild", content: null);
            string rebuiltAccelerationText = await rebuiltAcceleration.Content.ReadAsStringAsync();
            var rebuiltAccelerationJson = JsonSerializer.Deserialize<RouteAccelerationSnapshot>(rebuiltAccelerationText, HostJsonOptions);
            Assert.Equal(HttpStatusCode.OK, rebuiltAcceleration.StatusCode);
            Assert.Equal("application/json", rebuiltAcceleration.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(rebuiltAccelerationJson);
            Assert.Equal("landmark", rebuiltAccelerationJson.PlannerMode);
            Assert.Equal("active", rebuiltAccelerationJson.SnapshotStatus);
            Assert.Equal("landmark", rebuiltAccelerationJson.SnapshotKind);
            Assert.Equal("sample-world-default", rebuiltAccelerationJson.LandmarkProfileName);
            Assert.Equal(
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Harbor, TestWorldBuilder.LocationIds.Shrine],
                rebuiltAccelerationJson.LandmarkLocationIds
            );

            using var publicRoute = await client.GetAsync("/routes/village/aerie");
            Assert.Equal(HttpStatusCode.NotFound, publicRoute.StatusCode);

            using var publicAcceleration = await client.GetAsync("/route-acceleration");
            Assert.Equal(HttpStatusCode.NotFound, publicAcceleration.StatusCode);

            using var actorNavigation = await client.GetAsync("/actors/scout/navigation");
            var actorNavigationObservation = await ReadJsonAsync<ActorNavigationObservation>(actorNavigation);
            Assert.Equal(HttpStatusCode.OK, actorNavigation.StatusCode);
            Assert.Equal("application/json", actorNavigation.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(actorNavigationObservation);
            Assert.Equal(TestWorldBuilder.ActorIds.Scout, actorNavigationObservation.ActorId);
            Assert.Equal(TestWorldBuilder.LocationIds.Square, actorNavigationObservation.Navigation.LocationId);
            Assert.Contains(
                actorNavigationObservation.Navigation.Edges,
                static edge => edge.PassageId == TestWorldBuilder.PassageIds.SquareShrineGate
                    && edge.TravelMode == TravelMode.Portal
                    && edge.TravelCost == 1
            );

            using var actorRoutePlan = await client.GetAsync("/actors/scout/plan-route/aerie");
            string actorRoutePlanText = await actorRoutePlan.Content.ReadAsStringAsync();
            var actorRoutePlanJson = JsonSerializer.Deserialize<LocationRoutePlanObservation>(actorRoutePlanText, HostJsonOptions);
            Assert.Equal(HttpStatusCode.OK, actorRoutePlan.StatusCode);
            Assert.Equal("application/json", actorRoutePlan.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(actorRoutePlanJson);
            Assert.Equal(TestWorldBuilder.LocationIds.Square, actorRoutePlanJson.FromLocationId);
            Assert.Equal(TestWorldBuilder.LocationIds.Aerie, actorRoutePlanJson.ToLocationId);
            Assert.Equal(RoutePlanStatus.Found, actorRoutePlanJson.Status);
            Assert.Equal(10, actorRoutePlanJson.TotalTravelCost);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RouteAccelerationRebuild_BindsCustomLandmarksFromQueryAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/admin/route-acceleration/rebuild?landmarks=shrine,aerie",
                content: null
            );
            string jsonText = await response.Content.ReadAsStringAsync();
            var json = JsonSerializer.Deserialize<RouteAccelerationSnapshot>(jsonText, HostJsonOptions);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(json);
            Assert.Equal("landmark", json.PlannerMode);
            Assert.Equal("active", json.SnapshotStatus);
            Assert.Equal("custom", json.LandmarkProfileName);
            Assert.Equal(
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Shrine],
                json.LandmarkLocationIds
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RoutePlanEndpoints_ProjectAlreadyThereAndUnreachableStatesAsJsonAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var alreadyThereResponse = await client.GetAsync("/admin/routes/shrine/shrine");
            string alreadyThereText = await alreadyThereResponse.Content.ReadAsStringAsync();
            var alreadyThereJson = JsonSerializer.Deserialize<LocationRoutePlanObservation>(alreadyThereText, HostJsonOptions);

            Assert.Equal(HttpStatusCode.OK, alreadyThereResponse.StatusCode);
            Assert.Equal("application/json", alreadyThereResponse.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(alreadyThereJson);
            Assert.Contains("\"status\": \"already-there\"", alreadyThereText, StringComparison.Ordinal);
            Assert.Equal(RoutePlanStatus.AlreadyThere, alreadyThereJson.Status);
            Assert.Equal(0, alreadyThereJson.StepCount);
            Assert.Equal(0, alreadyThereJson.TotalTravelCost);
            Assert.Empty(alreadyThereJson.Steps);

            using var unreachableResponse = await client.GetAsync("/admin/routes/delta/harbor");
            string unreachableText = await unreachableResponse.Content.ReadAsStringAsync();
            var unreachableJson = JsonSerializer.Deserialize<LocationRoutePlanObservation>(unreachableText, HostJsonOptions);

            Assert.Equal(HttpStatusCode.OK, unreachableResponse.StatusCode);
            Assert.Equal("application/json", unreachableResponse.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(unreachableJson);
            Assert.Contains("\"status\": \"unreachable\"", unreachableText, StringComparison.Ordinal);
            Assert.Equal(RoutePlanStatus.Unreachable, unreachableJson.Status);
            Assert.Equal(0, unreachableJson.StepCount);
            Assert.Null(unreachableJson.TotalTravelCost);
            Assert.Empty(unreachableJson.Steps);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task ActorRoutePlanEndpoint_ProjectsAlreadyThereAndUnreachableStatesAsJsonAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir);
            using var client = factory.CreateClient();

            using var alreadyThereResponse = await client.GetAsync("/actors/scout/plan-route/square");
            string alreadyThereText = await alreadyThereResponse.Content.ReadAsStringAsync();
            var alreadyThereJson = JsonSerializer.Deserialize<LocationRoutePlanObservation>(alreadyThereText, HostJsonOptions);

            Assert.Equal(HttpStatusCode.OK, alreadyThereResponse.StatusCode);
            Assert.Equal("application/json", alreadyThereResponse.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(alreadyThereJson);
            Assert.Contains("\"status\": \"already-there\"", alreadyThereText, StringComparison.Ordinal);
            Assert.Equal(RoutePlanStatus.AlreadyThere, alreadyThereJson.Status);
            Assert.Equal(0, alreadyThereJson.StepCount);
            Assert.Equal(0, alreadyThereJson.TotalTravelCost);
            Assert.Empty(alreadyThereJson.Steps);

            using var unreachableResponse = await client.GetAsync("/actors/boatman/plan-route/village");
            string unreachableText = await unreachableResponse.Content.ReadAsStringAsync();
            var unreachableJson = JsonSerializer.Deserialize<LocationRoutePlanObservation>(unreachableText, HostJsonOptions);

            Assert.Equal(HttpStatusCode.OK, unreachableResponse.StatusCode);
            Assert.Equal("application/json", unreachableResponse.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(unreachableJson);
            Assert.Contains("\"status\": \"unreachable\"", unreachableText, StringComparison.Ordinal);
            Assert.Equal(RoutePlanStatus.Unreachable, unreachableJson.Status);
            Assert.Equal(0, unreachableJson.StepCount);
            Assert.Null(unreachableJson.TotalTravelCost);
            Assert.Empty(unreachableJson.Steps);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task HostRestart_ReopensWorldTruthAndDurableTimeButResetsOtherSessionOwnedStateAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            await using (var firstFactory = CreateFactory(repoDir)) {
                using var firstClient = firstFactory.CreateClient();

                using var advancedTime = await firstClient.PostAsync("/admin/advance-time/13", content: null);
                using var movedActor = await firstClient.PostAsync("/actors/scout/moves/square-ridge-trail", content: null);

                Assert.Equal(HttpStatusCode.OK, advancedTime.StatusCode);
                Assert.Equal(HttpStatusCode.OK, movedActor.StatusCode);
            }

            WaitUntilSessionCanReopen(repoDir);

            await using var secondFactory = CreateFactory(repoDir);
            using var secondClient = secondFactory.CreateClient();

            using var timeAfterRestart = await secondClient.GetAsync("/admin/time");
            string timeText = await timeAfterRestart.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, timeAfterRestart.StatusCode);
            Assert.Contains("\"currentTick\":13", timeText.Replace(" ", string.Empty), StringComparison.Ordinal);

            using var traceAfterRestart = await secondClient.GetAsync("/actors/scout/route-trace");
            string traceText = await traceAfterRestart.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, traceAfterRestart.StatusCode);
            Assert.Contains("start=ridge (Ridge)", traceText, StringComparison.Ordinal);
            Assert.Contains("<no movement in this run>", traceText, StringComparison.Ordinal);
            Assert.Contains("end=ridge (Ridge) | steps=0 | totalCost=0", traceText, StringComparison.Ordinal);

            using var observedAfterRestart = await secondClient.GetAsync("/actors/scout/observation");
            var observedAfterRestartJson = await ReadJsonAsync<ActorLocationObservation>(observedAfterRestart);
            Assert.NotNull(observedAfterRestartJson);
            Assert.Equal("ridge", observedAfterRestartJson.Location.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void InvalidBootstrapMode_FailsFastWithAllowedValuesInError() {
        string repoDir = CreateTempRepoDir();

        try {
            using var factory = CreateFactory(repoDir, bootstrapMode: "invalid-mode");

            var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
            Assert.Contains("unsupported", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bootstrap mode", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sample-world-dev", ex.Message, StringComparison.Ordinal);
            Assert.Contains("open-existing-only", ex.Message, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static TextAdv2GameServerFactory CreateFactory(
        string repoDir,
        string bootstrapMode = SampleWorldDevBootstrapMode
    )
        => new(repoDir, bootstrapMode);

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), HostJsonOptions);

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-gameserver-tests-{Guid.NewGuid():N}");

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
                using var session = SampleWorldBootstrap.OpenOrCreateSession(repoDir);
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

    private static string[] ReadActorIds(IEnumerable<ActorPresenceObservation> presentActors)
        => presentActors
            .Select(static actor => actor.ActorId)
            .OrderBy(static actorId => actorId, StringComparer.Ordinal)
            .ToArray();

    private sealed class TextAdv2GameServerFactory(string repoDir, string bootstrapMode) : WebApplicationFactory<Program> {
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseSetting("TextAdv2:RepoDir", repoDir);
            builder.UseSetting("TextAdv2:BootstrapMode", bootstrapMode);
        }
    }

    private static JsonSerializerOptions CreateHostJsonOptions() {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        TextAdv2Json.AddHostConverters(options);
        return options;
    }
}
