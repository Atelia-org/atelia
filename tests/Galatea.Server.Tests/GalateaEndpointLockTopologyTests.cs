using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaEndpointLockTopologyTests {
    private static readonly TimeSpan EndpointDeadline =
        TimeSpan.FromSeconds(3);

    [Fact]
    public async Task Events_ReplaysWhileTurnLockRemainsHeld() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        var hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        var session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        GalateaLiveTurn? liveTurn = null;
        HttpResponseMessage? response = null;
        bool lockHeld = false;
        try {
            await session.TurnLock.WaitAsync();
            lockHeld = true;
            liveTurn = hostService.StartTurn(
                session,
                "lock topology probe",
                new GalateaTurnOptions("test")
            );
            liveTurn.Publish(
                new StreamEventDto(
                    "meta",
                    new { phase = "lock-topology-proof" }
                ),
                phase: "lock-topology-proof"
            );

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/chat/turns/{liveTurn.TurnId}/events"
            );
            response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead
                )
                .WaitAsync(EndpointDeadline);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                "text/event-stream",
                response.Content.Headers.ContentType?.MediaType
            );

            await using var stream = await response.Content
                .ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            string? eventLine = await reader
                .ReadLineAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(EndpointDeadline);
            string? dataLine = await reader
                .ReadLineAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(EndpointDeadline);

            Assert.Equal("event: meta", eventLine);
            Assert.Equal(
                "data: {\"phase\":\"lock-topology-proof\"}",
                dataLine
            );
            Assert.False(session.TurnLock.Wait(0));
        }
        finally {
            if (liveTurn is not null) {
                hostService.FinishTurn(session, liveTurn);
                liveTurn.Complete();
            }

            if (lockHeld) {
                session.TurnLock.Release();
            }

            response?.Dispose();
        }
    }

    [Fact]
    public async Task Stop_ReturnsOkWhileTurnLockRemainsHeld() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        var hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        var session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        GalateaLiveTurn? liveTurn = null;
        bool lockHeld = false;
        try {
            await session.TurnLock.WaitAsync();
            lockHeld = true;
            liveTurn = hostService.StartTurn(
                session,
                "stop topology probe",
                new GalateaTurnOptions("test")
            );

            using HttpResponseMessage response = await client.PostAsync(
                    $"/api/chat/turns/{liveTurn.TurnId}/stop",
                    content: null
                )
                .WaitAsync(EndpointDeadline);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(liveTurn.StopRequested);
            Assert.False(session.TurnLock.Wait(0));
        }
        finally {
            if (liveTurn is not null) {
                hostService.FinishTurn(session, liveTurn);
                liveTurn.Complete();
            }

            if (lockHeld) {
                session.TurnLock.Release();
            }
        }
    }

    [Fact]
    public async Task InvalidConnection_DoesNotLeakTurnLock() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        var hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        var session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        var connections = host.Factory.Services
            .GetRequiredService<CompletionConnectionRegistry>();
        FieldInfo byIdField = typeof(CompletionConnectionRegistry)
            .GetField(
                "_byId",
                BindingFlags.Instance | BindingFlags.NonPublic
            ) ?? throw new InvalidOperationException(
                "CompletionConnectionRegistry._byId was not found."
            );
        var byId = Assert.IsAssignableFrom<
            IDictionary<string, CompletionConnectionConfig>
        >(byIdField.GetValue(connections));
        Assert.True(byId.Remove("test"));

        try {
            using HttpResponseMessage response =
                await client.PostAsJsonAsync(
                    "/api/chat/turns",
                    new ChatStreamRequest(
                        "invalid connection probe",
                        ConnectionId: null
                    )
                );
            Assert.Equal(
                HttpStatusCode.InternalServerError,
                response.StatusCode
            );
        }
        catch (InvalidOperationException) {
            // TestServer may surface the unhandled endpoint exception.
        }

        Assert.True(session.TurnLock.Wait(0));
        session.TurnLock.Release();
        Assert.Null(session.GetCurrentTurn());
    }

    private static GalateaTestHost CreateHost() =>
        GalateaTestHost.Create(
            new NonDispatchingCompletionClientFactory(),
            new PassThroughNormalizer()
        );

    private static async Task LoginAsync(HttpClient client) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private sealed class NonDispatchingCompletionClientFactory
        : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            return new NonDispatchingCompletionClient();
        }
    }

    private sealed class NonDispatchingCompletionClient
        : ICompletionClient {
        public string Name => "galatea-lock-topology-test";

        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException(
            "Lock-topology tests must not dispatch a completion request."
        );
    }

    private sealed class PassThroughNormalizer
        : IGalateaUserMessageNormalizer {
        public bool ShouldNormalize(string userMessage) {
            _ = userMessage;
            return false;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(userMessage);
        }
    }
}
