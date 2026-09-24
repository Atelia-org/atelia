using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaAgentStatusContractTests {
    [Theory]
    [InlineData(false, false, "waiting", "test")]
    [InlineData(true, false, "starting", "test")]
    [InlineData(true, true, "maintenance", "test")]
    public async Task StatusIsAuthenticatedClosedAndNeverAttaches(
        bool enrolled,
        bool maintenance,
        string expectedState,
        string? expectedConnection
    ) {
        var factory = new RejectProviderFactory();
        await using var fixture = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            maintenanceMode: maintenance,
            autonomyCharacterIds: enrolled ? ["alice"] : [],
            enableServerAgentHostedService: maintenance
        );
        using HttpClient client = fixture.CreateClient();
        using HttpResponseMessage anonymous = await client.GetAsync("/api/v1/characters/alice/agent/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(client);
        GalateaHostService host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();

        for (int index = 0; index < 2; index++) {
            using HttpResponseMessage response = await client.GetAsync("/api/v1/characters/alice/agent/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement root = document.RootElement;
            Assert.Equal(
                new[] { "admissionFailure", "code", "defaultConnectionId", "effectiveConnectionId", "lastActivationAtUnixTimeMilliseconds", "nextActivationAtUnixTimeMilliseconds", "runtimeConnectionOverrideId", "state" },
                root.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal)
            );
            Assert.Equal(expectedState, root.GetProperty("state").GetString());
            Assert.Equal(expectedConnection, root.GetProperty("defaultConnectionId").GetString());
            Assert.Equal(expectedConnection, root.GetProperty("effectiveConnectionId").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("runtimeConnectionOverrideId").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("code").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("nextActivationAtUnixTimeMilliseconds").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("lastActivationAtUnixTimeMilliseconds").ValueKind);
            Assert.Null(host.ReadAttachedSession("alice"));
        }
        Assert.False(Directory.Exists(fixture.DelegationStateDirectory));
        Assert.False(Directory.Exists(fixture.CharacterMemoryStateDirectory));
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task FreshConnectionPriorityIsCharacterScopedValidatedAndVisibleWithoutSessionAttach() {
        var factory = new RejectProviderFactory();
        CompletionConnectionConfig defaultConnection = new(
            "test", "openai-chat", "model-a", "openai-chat/strict",
            "http://localhost:8000/", ApiKey: "test-key");
        CompletionConnectionConfig runtimeConnection = defaultConnection with {
            Id = "runtime"
        };
        CompletionConnectionConfig diagnosticConnection = defaultConnection with {
            Id = "diagnostic"
        };
        await using var fixture = GalateaTestHost.Create(
            factory, DisabledGalateaUserMessageNormalizer.Instance,
            connections: [defaultConnection, runtimeConnection, diagnosticConnection],
            connectionOptionIds: ["test", "runtime", "diagnostic"]
        );
        using HttpClient client = fixture.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(client);
        GalateaHostService host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        Assert.True(host.TryGetCharacter("alice", out GalateaCharacterConfig? character));
        Assert.True(host.TryGetFreshConnection(character!, null, out var selected));
        Assert.Equal("test", selected.Id);

        host.SetRuntimeConnectionOverride("alice", "runtime");
        Assert.True(host.TryGetFreshConnection(character!, null, out selected));
        Assert.Equal("runtime", selected.Id);
        Assert.True(host.TryGetFreshConnection(character!, "diagnostic", out selected));
        Assert.Equal("diagnostic", selected.Id);
        Assert.False(host.TryGetFreshConnection(character!, "missing", out _));
        Assert.Throws<ArgumentException>(() => host.SetRuntimeConnectionOverride("alice", "missing"));
        Assert.Throws<ArgumentException>(() => host.SetRuntimeConnectionOverride("missing", "runtime"));
        Assert.Equal("runtime", host.ReadRuntimeConnectionOverride("alice"));

        using (HttpResponseMessage legacy = await client.PostAsync(
            "/api/v1/characters/alice/chat/turns",
            Json("{\"message\":\"legacy\",\"connectionId\":\"diagnostic\"}"))) {
            Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
        }

        using (HttpResponseMessage response = await client.GetAsync(
            "/api/v1/characters/alice/agent/status")) {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement status = document.RootElement;
            Assert.Equal("test", status.GetProperty("defaultConnectionId").GetString());
            Assert.Equal("runtime", status.GetProperty("runtimeConnectionOverrideId").GetString());
            Assert.Equal("runtime", status.GetProperty("effectiveConnectionId").GetString());
        }
        Assert.Null(host.ReadAttachedSession("alice"));
        Assert.Equal(0, factory.CreateCount);

        host.SetRuntimeConnectionOverride("alice", null);
        Assert.Null(host.ReadRuntimeConnectionOverride("alice"));
        Assert.True(host.TryGetFreshConnection(character!, null, out selected));
        Assert.Equal("test", selected.Id);
    }

    [Fact]
    public async Task ZeroIntervalOneShotInspectsExistingSessionWithoutGenerationAndRejectsConnectionOverride() {
        var factory = new RejectProviderFactory();
        await using var fixture = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        using HttpClient client = fixture.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(client);
        using HttpResponseMessage waiting = await client.PostAsync(
            "/api/v1/characters/alice/mailbox/ready-turn", Json("{}")
        );
        Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await waiting.Content.ReadAsStringAsync());
        Assert.Equal("waiting", document.RootElement.GetProperty("state").GetString());

        using HttpResponseMessage retry = await client.PostAsync(
            "/api/v1/characters/alice/agent/retry-admission", Json("{}")
        );
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        GalateaAutomaticTurnCoordinator coordinator = fixture.Factory.Services
            .GetRequiredService<GalateaAutomaticTurnCoordinator>();
        GalateaAutomaticTurnResult.Status direct = Assert.IsType<
            GalateaAutomaticTurnResult.Status>(
                await coordinator.TryPulseAsync("alice", CancellationToken.None)
            );
        Assert.Equal("waiting", direct.Value.State);

        foreach (string body in new[] { "{\"connectionId\":\"test\"}", "{\"connectionId\":null}", "{\"unexpected\":1}" }) {
            using HttpResponseMessage rejected = await client.PostAsync("/api/v1/characters/alice/mailbox/ready-turn", Json(body));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        Assert.NotNull(fixture.Factory.Services.GetRequiredService<GalateaHostService>().ReadAttachedSession("alice"));
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task StatusReadsCachedSnapshotWhileWriterLockIsHeld() {
        var factory = new RejectProviderFactory();
        await using var fixture = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            autonomyCharacterIds: ["alice"]
        );
        using HttpClient client = fixture.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(client);
        GalateaHostService host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try {
            var head = session.Engine.ReadCurrentHead();
            GalateaAutonomyCadenceStatus before = session.AutonomyCadence!.ProjectStatus();
            using HttpResponseMessage response = await client.GetAsync("/api/v1/characters/alice/agent/status")
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(head, session.Engine.ReadCurrentHead());
            Assert.Equal(before.State, session.AutonomyCadence!.ProjectStatus().State);
            Assert.Null(session.GetCurrentTurn());
            Assert.Equal(0, factory.CreateCount);
        }
        finally { session.TurnLock.Release(); }
    }

    [Fact]
    public async Task ResumeAutonomyRequiresPausedIdleSessionAndRearmsFromNow() {
        var clock = new ManualTimeProvider();
        var factory = new RejectProviderFactory();
        await using var fixture = GalateaTestHost.Create(factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            timeProvider: clock, autonomyCharacterIds: ["alice"]);
        using HttpClient client = fixture.CreateClient();
        using HttpResponseMessage anonymous = await client.PostAsync(
            "/api/v1/characters/alice/agent/resume-autonomy", Json("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(client);
        GalateaHostService host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await host.GetSessionAsync("alice", CancellationToken.None);
        GalateaAutonomyCadence cadence = session.AutonomyCadence!;
        await session.TurnLock.WaitAsync();
        try {
            cadence.Arm();
            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(GalateaAutonomyCadencePulseResult.AutonomousActivationDue,
                cadence.ObservePulse());
            Assert.True(cadence.TryClaimAutonomousActivationStarted(out var claim));
            Assert.True(cadence.SettleMainTurn(new GalateaAutonomyCadenceTurnSettlement(),
                isAutonomousActivation: true, completed: false, autonomousClaim: claim));
            session.PublishAutonomyStatus();
        }
        finally { session.TurnLock.Release(); }

        await session.TurnLock.WaitAsync();
        try {
            using HttpResponseMessage busy = await client.PostAsync(
                "/api/v1/characters/alice/agent/resume-autonomy", Json("{}"));
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            Assert.Equal(GalateaAutonomyCadence.PausedState, cadence.ProjectStatus().State);
        }
        finally { session.TurnLock.Release(); }

        using HttpResponseMessage resumed = await client.PostAsync(
            "/api/v1/characters/alice/agent/resume-autonomy", Json("{}"));
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        using JsonDocument response = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
        Assert.Equal("waiting", response.RootElement.GetProperty("state").GetString());
        Assert.Equal((clock.GetUtcNow() + TimeSpan.FromMinutes(10)).ToUnixTimeMilliseconds(),
            response.RootElement.GetProperty("nextActivationAtUnixTimeMilliseconds").GetInt64());
        Assert.Null(session.GetCurrentTurn());
        Assert.Equal(0, factory.CreateCount);
        using HttpResponseMessage again = await client.PostAsync(
            "/api/v1/characters/alice/agent/resume-autonomy", Json("{}"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class ManualTimeProvider : TimeProvider {
        private long _timestamp;
        private DateTimeOffset _now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan value) {
            _timestamp += value.Ticks;
            _now += value;
        }
    }

    private sealed class RejectProviderFactory : ICompletionClientFactory {
        private int _createCount;
        internal int CreateCount => Volatile.Read(ref _createCount);
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Interlocked.Increment(ref _createCount);
            throw new InvalidOperationException("A read-only Agent status operation must not create a provider.");
        }
    }
}
