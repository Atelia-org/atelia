using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRetryIsolationTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task HostedSchedulerRunsOtherCharacterWhileFirstCharacterRetries() {
        var clock = new GalateaLabClock();
        var provider = new RoutingFactory(retryAlice: true);
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test"), Connection("bob")], selectableConnectionIds: ["test", "bob"],
            timeProvider: clock, autonomyCharacterIds: ["alice"], enableServerAgentHostedService: true);
        JsonObject config = JsonNode.Parse(File.ReadAllText(fixture.ConfigPath))!.AsObject();
        JsonObject bob = config["characters"]![0]!.DeepClone().AsObject();
        string home = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.ConfigPath)!, "homes", "bob")).FullName;
        string sessionPath = Path.Combine(fixture.RootDirectory, "bob-session");
        bob["id"] = "bob";
        bob["name"] = "Bob";
        bob["homeDir"] = home;
        bob["sessionDir"] = sessionPath;
        bob["delegationStateDir"] = Path.Combine(fixture.RootDirectory, "bob-delegation");
        bob["characterMemoryStateDir"] = Path.Combine(fixture.RootDirectory, "bob-memory");
        bob["defaultConnectionId"] = "bob";
        using (var engine = SessionJournalEngine.Create(sessionPath, new("model-a",
            GalateaSystemPromptComposer.CreateContent(new("character", "bob", "Bob"),
                "test ${characterName} system prompt", false, false, homeDir: home), "openai-chat/strict"))) {
            GalateaTestHost.ProvisionRawOnlyRecapGrid(engine);
        }
        config["characters"]!.AsArray().Add(bob);
        File.WriteAllText(fixture.ConfigPath, config.ToJsonString());

        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        await Until(() => coordinator.ReadStatus("alice").State == "waiting"
            && coordinator.ReadStatus("bob").State == "waiting");
        clock.Advance(TimeSpan.FromMinutes(10));
        await Until(() => provider.BobCalls == 1 && host.ReadAttachedSession("bob")?.GetCurrentTurn() is null
            && host.ReadAttachedSession("alice")?.GetCurrentTurn()?.Phase == "retry-wait");
        var alice = host.ReadAttachedSession("alice")!;
        var bobSession = host.ReadAttachedSession("bob")!;
        Assert.Equal(1, provider.AliceCalls);
        Assert.Equal(SessionExecutionPhase.AwaitingCompletion, alice.Engine.InspectExecutionBoundary().Phase);
        Assert.Equal(SessionExecutionPhase.Idle, bobSession.Engine.InspectExecutionBoundary().Phase);
        Assert.Single(bobSession.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
        clock.Advance(TimeSpan.FromSeconds(6));
        await Until(() => provider.AliceCalls >= 2);
        Assert.Equal(1, provider.BobCalls);
        Assert.Equal("waiting", coordinator.ReadStatus("bob").State);
    }

    [Fact]
    public async Task ColdHostedStartupWithNoPlayersRecoversExistingZeroIntervalPendingInput() {
        var provider = new RoutingFactory(retryAlice: false);
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance,
            enableServerAgentHostedService: true);
        using (var offline = SessionJournalEngine.Open(fixture.SessionDirectory)) {
            offline.AppendObservation(GalateaUserMessageEnvelope.Wrap("accepted before process start"));
        }
        JsonObject config = JsonNode.Parse(File.ReadAllText(fixture.ConfigPath))!.AsObject();
        config["players"] = new JsonArray();
        Assert.Equal(0, config["characters"]![0]!["autonomyIntervalMinutes"]!.GetValue<int>());
        File.WriteAllText(fixture.ConfigPath, config.ToJsonString());

        // Real hosted service startup; no browser, player login, or manual pulse.
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        await Until(() => provider.AliceCalls == 1 && host.ReadAttachedSession("alice") is { } session
            && session.GetCurrentTurn() is null);
        var recovered = host.ReadAttachedSession("alice")!;
        Assert.Equal(SessionExecutionPhase.Idle, recovered.Engine.InspectExecutionBoundary().Phase);
        Assert.Single(recovered.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
        Assert.Equal(1, provider.AliceCalls);
    }

    [Theory]
    [InlineData("/chat/turns/pending/stop")]
    [InlineData("/agent/admission/0123456789abcdef0123456789abcdef/stop")]
    public async Task MaintenanceRejectsNewStopMutationEndpoints(string route) {
        var provider = new RoutingFactory(retryAlice: false);
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance,
            maintenanceMode: true);
        using HttpClient client = fixture.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/characters/alice" + route,
            new { expectedHead = "unused-by-maintenance-gate" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("maintenance-mode", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, provider.AliceCalls);
        Assert.Equal(0, provider.BobCalls);
    }

    private static CompletionConnectionConfig Connection(string id) =>
        new(id, "openai-chat", "model-a", "openai-chat/strict", "http://localhost:8000/", ApiKey: "test-key");

    private static async Task Until(Func<bool> condition) {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!condition()) { await Task.Delay(1, timeout.Token); }
    }

    private sealed class RoutingFactory(bool retryAlice) : ICompletionClientFactory {
        private readonly bool _retryAlice = retryAlice;
        private int _aliceCalls;
        private int _bobCalls;
        internal int AliceCalls => Volatile.Read(ref _aliceCalls);
        internal int BobCalls => Volatile.Read(ref _bobCalls);
        public ICompletionClient Create(CompletionConnectionConfig connection) => new Client(this, connection.Id);
        private sealed class Client(RoutingFactory owner, string id) : ICompletionClient {
            public string Name => "isolation-provider";
            public string ApiSpecId => "openai-chat-v1";
            public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
                CancellationToken cancellationToken = default) {
                cancellationToken.ThrowIfCancellationRequested();
                if (id == "bob") { Interlocked.Increment(ref owner._bobCalls); }
                else {
                    Interlocked.Increment(ref owner._aliceCalls);
                    if (owner._retryAlice) { throw new CompletionFailureException(new(CompletionFailureKind.Http, 503), "temporary"); }
                }
                return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.Text("completed")]),
                    CompletionDescriptor.From(this, request)));
            }
        }
    }
}
