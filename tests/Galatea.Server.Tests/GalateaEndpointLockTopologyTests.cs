using System.Net;
using System.Net.Http.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
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
    public async Task ActiveTurn_ReadSurfacesUseOnlyLiveStateAndCachedRecentTurns() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        var hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        var session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        _ = session.Engine.AppendObservation(
            GalateaUserMessageEnvelope.Wrap("completed user")
        );
        _ = session.Engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("completed assistant")
            ]),
            new CompletionDescriptor(
                "lock-topology-fixture",
                "fixture-v1",
                "model-a"
            )
        );
        RecentTurnsResponseDto? idleRecent = await client
            .GetFromJsonAsync<RecentTurnsResponseDto>(
                "/api/recent-turns"
            );
        Assert.NotNull(idleRecent);
        Assert.NotNull(idleRecent!.RewindLatestToken);

        GalateaLiveTurn? liveTurn = null;
        bool lockHeld = false;
        try {
            await session.TurnLock.WaitAsync();
            lockHeld = true;
            liveTurn = hostService.StartTurn(
                session,
                "active topology probe",
                new GalateaTurnOptions("test")
            );
            SessionJournalReadDiagnostics before =
                session.Engine.CaptureReadDiagnostics();

            CurrentTurnDto? current = await client
                .GetFromJsonAsync<CurrentTurnDto>(
                    "/api/chat/turns/current"
                )
                .WaitAsync(EndpointDeadline);
            Assert.NotNull(current);
            Assert.Equal("running", current!.Status);
            Assert.Equal(liveTurn.TurnId, current.TurnId);
            Assert.Equal(liveTurn.UserMessage, current.UserMessage);
            Assert.Equal(liveTurn.Phase, current.Phase);
            Assert.Equal("test", current.ConnectionId);
            Assert.Null(current.DurablePhase);
            Assert.Null(current.RecoveryHead);

            RecentTurnsResponseDto? activeRecent = await client
                .GetFromJsonAsync<RecentTurnsResponseDto>(
                    "/api/recent-turns"
                )
                .WaitAsync(EndpointDeadline);
            Assert.NotNull(activeRecent);
            RecentTurnDto cachedTurn = Assert.Single(
                activeRecent!.Turns
            );
            Assert.Equal("completed user", cachedTurn.UserText);
            Assert.Null(activeRecent.RewindLatestToken);

            using HttpResponseMessage busy = await client
                .PostAsJsonAsync(
                    "/api/chat/turns",
                    new ChatStreamRequest(
                        "must remain busy",
                        ConnectionId: "test"
                    )
                )
                .WaitAsync(EndpointDeadline);
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            StartTurnResponseDto? conflict = await busy.Content
                .ReadFromJsonAsync<StartTurnResponseDto>();
            Assert.NotNull(conflict);
            Assert.Equal(liveTurn.TurnId, conflict!.TurnId);
            Assert.Equal("running", conflict.Status);

            using HttpResponseMessage stop = await client.PostAsync(
                    $"/api/chat/turns/{liveTurn.TurnId}/stop",
                    content: null
                )
                .WaitAsync(EndpointDeadline);
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
            Assert.True(liveTurn.StopRequested);

            SessionJournalReadDiagnostics after =
                session.Engine.CaptureReadDiagnostics();
            Assert.Equal(before, after);
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
    public async Task Current_WhenGateIsBusyBeforeLiveTurnPublication_ReturnsGenericRunning() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        var hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        var session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        await session.TurnLock.WaitAsync();
        try {
            SessionJournalReadDiagnostics before =
                session.Engine.CaptureReadDiagnostics();

            CurrentTurnDto? current = await client
                .GetFromJsonAsync<CurrentTurnDto>(
                    "/api/chat/turns/current"
                )
                .WaitAsync(EndpointDeadline);

            Assert.NotNull(current);
            Assert.Equal("running", current!.Status);
            Assert.Null(current.TurnId);
            Assert.Null(current.DurablePhase);
            Assert.Equal(
                before,
                session.Engine.CaptureReadDiagnostics()
            );
        }
        finally {
            session.TurnLock.Release();
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
        using HttpResponseMessage response =
            await client.PostAsJsonAsync(
                "/api/chat/turns",
                new ChatStreamRequest(
                    "invalid connection probe",
                    ConnectionId: "missing"
                )
            );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

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
