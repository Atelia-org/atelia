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
                new[] { "admissionFailure", "code", "connectionId", "lastActivationAtUnixTimeMilliseconds", "nextActivationAtUnixTimeMilliseconds", "state" },
                root.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal)
            );
            Assert.Equal(expectedState, root.GetProperty("state").GetString());
            Assert.Equal(expectedConnection, root.GetProperty("connectionId").GetString());
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
    public async Task ZeroIntervalOneShotNeverAttachesAndRejectsConnectionOverride() {
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
        Assert.Null(fixture.Factory.Services.GetRequiredService<GalateaHostService>().ReadAttachedSession("alice"));
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

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class RejectProviderFactory : ICompletionClientFactory {
        private int _createCount;
        internal int CreateCount => Volatile.Read(ref _createCount);
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Interlocked.Increment(ref _createCount);
            throw new InvalidOperationException("A read-only Agent status operation must not create a provider.");
        }
    }
}
