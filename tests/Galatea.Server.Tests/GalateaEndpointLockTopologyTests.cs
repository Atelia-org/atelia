using System.Net;
using System.Net.Http.Json;
using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaEndpointLockTopologyTests {
    private static readonly TimeSpan EndpointDeadline =
        TimeSpan.FromSeconds(3);

    [Fact]
    public async Task SessionDisposalWaitsForTurnLockBeforeDisposingEngine() {
        await using var host = CreateHost();
        var hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        bool lockHeld = false;
        Task? disposal = null;
        try {
            await session.TurnLock.WaitAsync();
            lockHeld = true;
            disposal = session.DisposeAsync().AsTask();

            Assert.False(disposal.IsCompleted);
            Assert.NotNull(session.Engine.ReadCurrentHead());
        }
        finally {
            if (lockHeld) {
                session.TurnLock.Release();
            }
        }

        await disposal!.WaitAsync(EndpointDeadline);
        Assert.Throws<ObjectDisposedException>(
            () => session.Engine.ReadCurrentHead()
        );
    }

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
            liveTurn.PublishStatus(
                GalateaSseStatusCode.Generating
            );

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/chat/turns/{liveTurn.TurnId}/events"
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

            Assert.Equal("event: status", eventLine);
            Assert.Equal(
                "data: {\"code\":\"generating\"}",
                dataLine
            );
            Assert.False(session.TurnLock.Wait(0));
        }
        finally {
            if (liveTurn is not null) {
                hostService.FinishTurn(session, liveTurn);
                liveTurn.PublishError(
                    GalateaSseErrorCode.InternalFailure
                );
                liveTurn.Complete();
            }

            if (lockHeld) {
                session.TurnLock.Release();
            }

            response?.Dispose();
        }
    }

    [Fact]
    public async Task Events_ReplayThenLiveThenTerminalHaveNoGapOrDuplicate() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        GalateaHostService hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn liveTurn = hostService.StartTurn(
            session,
            "replay/live fixture",
            new GalateaTurnOptions("test")
        );
        try {
            liveTurn.PublishStatus(GalateaSseStatusCode.Generating);
            liveTurn.PublishTextDelta("replay");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/chat/turns/{liveTurn.TurnId}/events"
            );
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead
            ).WaitAsync(EndpointDeadline);
            await using Stream stream = await response.Content
                .ReadAsStreamAsync();
            Task<string> body = new StreamReader(
                stream,
                Encoding.UTF8
            ).ReadToEndAsync();

            liveTurn.PublishTextDelta("live");
            liveTurn.PublishDone(recent: null);
            liveTurn.Complete();

            Assert.Equal(
                "event: status\ndata: {\"code\":\"generating\"}\n\n"
                    + "event: text-delta\ndata: {\"delta\":\"replay\"}\n\n"
                    + "event: text-delta\ndata: {\"delta\":\"live\"}\n\n"
                    + "event: done\ndata: {\"recent\":null}\n\n",
                await body.WaitAsync(EndpointDeadline)
            );
        }
        finally {
            hostService.FinishTurn(session, liveTurn);
            if (liveTurn.Status == "running") {
                liveTurn.PublishError(
                    GalateaSseErrorCode.InternalFailure
                );
                liveTurn.Complete();
            }
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task FatalTurnFailurePreservesCauseAndEndsTransport() {
        var fatalClient = new FatalCompletionClient();
        await using var host = GalateaTestHost.Create(
            new SingleClientFactory(fatalClient),
            new PassThroughNormalizer()
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        GalateaHostService hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/api/v1/chat/turns",
            new ChatStreamRequest("fatal fixture", "test")
        );
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await fatalClient.Entered.Task.WaitAsync(EndpointDeadline);
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            session.GetCurrentTurn()
        );
        using GalateaTurnSubscription subscription = liveTurn.Subscribe();

        fatalClient.Release.TrySetResult();
        OutOfMemoryException failure = await Assert.ThrowsAsync<
            OutOfMemoryException
        >(async () => await Assert.IsAssignableFrom<Task>(liveTurn.RunTask));

        Assert.Same(fatalClient.Failure, failure);
        Assert.True(liveTurn.TransportAborted);
        Assert.Throws<InvalidOperationException>(() =>
            liveTurn.PublishTextDelta("after fatal")
        );
        Assert.Throws<InvalidOperationException>(() =>
            liveTurn.PublishDone(recent: null)
        );
        List<GalateaSseFrame> observed = [];
        await foreach (GalateaSseFrame frame
                       in subscription.Reader.ReadAllAsync()) {
            observed.Add(frame);
        }
        Assert.DoesNotContain(observed, static frame => frame.IsTerminal);
        Assert.Null(session.GetCurrentTurn());
        Assert.True(session.TurnLock.Wait(0));
        session.TurnLock.Release();
    }

    [Fact]
    public async Task Stop_ReturnsNoContentWhileTurnLockRemainsHeld() {
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
                    $"/api/v1/chat/turns/{liveTurn.TurnId}/stop",
                    content: null
                )
                .WaitAsync(EndpointDeadline);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.True(liveTurn.StopRequested);
            Assert.False(session.TurnLock.Wait(0));
        }
        finally {
            if (liveTurn is not null) {
                hostService.FinishTurn(session, liveTurn);
                liveTurn.PublishError(
                    GalateaSseErrorCode.InternalFailure
                );
                liveTurn.Complete();
            }

            if (lockHeld) {
                session.TurnLock.Release();
            }
        }
    }

    [Fact]
    public async Task ActiveTurn_CurrentUsesLiveStateAndRecentReturnsTypedBusy() {
        var completionFactory =
            new NonDispatchingCompletionClientFactory();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            new PassThroughNormalizer()
        );
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
                "/api/v1/recent-turns"
            );
        Assert.NotNull(idleRecent);
        Assert.NotNull(idleRecent!.RewindLatestToken);
        RecapGridReadinessSnapshotDto idleRecap = Assert.IsType<
            RecapGridReadinessSnapshotDto
        >(idleRecent.RecapGridReadiness);
        Assert.Equal("exact", idleRecap.Freshness);
        Assert.Equal("raw-only", idleRecap.State);
        Assert.Null(idleRecap.Authority);
        Assert.Null(idleRecap.Metrics);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.False(File.Exists(Path.Combine(
            host.SessionDirectory,
            "derived",
            "recap-grid",
            "v1",
            "grid.sqlite"
        )));

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
                    "/api/v1/chat/turns/current"
                )
                .WaitAsync(EndpointDeadline);
            Assert.NotNull(current);
            Assert.Equal("running", current!.Status);
            Assert.Equal(liveTurn.TurnId, current.TurnId);
            Assert.Equal("test", current.ConnectionId);
            Assert.False(current.RestartRequired);
            Assert.Null(current.RecoveryHead);

            using HttpResponseMessage activeRecent = await client
                .GetAsync("/api/v1/recent-turns")
                .WaitAsync(EndpointDeadline);
            Assert.Equal(
                HttpStatusCode.ServiceUnavailable,
                activeRecent.StatusCode
            );
            ApiErrorDto? recentBusy = await activeRecent.Content
                .ReadFromJsonAsync<ApiErrorDto>();
            Assert.Equal(
                "recent-view-busy",
                Assert.IsType<ApiErrorDto>(recentBusy).Code
            );

            using HttpResponseMessage busy = await client
                .PostAsJsonAsync(
                    "/api/v1/chat/turns",
                    new ChatStreamRequest(
                        "must remain busy",
                        ConnectionId: "test"
                    )
                )
                .WaitAsync(EndpointDeadline);
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            TurnBusyErrorDto? conflict = await busy.Content
                .ReadFromJsonAsync<TurnBusyErrorDto>();
            Assert.NotNull(conflict);
            Assert.Equal(liveTurn.TurnId, conflict!.TurnId);
            Assert.Equal("turn-busy", conflict.Code);
            Assert.NotEmpty(conflict.Error);

            using HttpResponseMessage stop = await client.PostAsync(
                    $"/api/v1/chat/turns/{liveTurn.TurnId}/stop",
                    content: null
                )
                .WaitAsync(EndpointDeadline);
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
            Assert.True(liveTurn.StopRequested);

            SessionJournalReadDiagnostics after =
                session.Engine.CaptureReadDiagnostics();
            Assert.Equal(before, after);
            Assert.False(session.TurnLock.Wait(0));
        }
        finally {
            if (liveTurn is not null) {
                hostService.FinishTurn(session, liveTurn);
                liveTurn.PublishError(
                    GalateaSseErrorCode.InternalFailure
                );
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
                    "/api/v1/chat/turns/current"
                )
                .WaitAsync(EndpointDeadline);

            Assert.NotNull(current);
            Assert.Equal("running", current!.Status);
            Assert.Null(current.TurnId);
            Assert.Null(current.ConnectionId);
            Assert.False(current.RestartRequired);
            Assert.Null(current.RecoveryHead);
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
    public async Task StaticClient_PollsOnlyTheLiveTurnPublicationWindow() {
        await using var host = CreateHost();
        IWebHostEnvironment environment = host.Factory.Services
            .GetRequiredService<IWebHostEnvironment>();
        await using Stream stream = environment.WebRootFileProvider
            .GetFileInfo("assets/galatea.js")
            .CreateReadStream();
        using var reader = new StreamReader(stream);

        string script = await reader.ReadToEndAsync();

        Assert.Contains(
            "async function waitForPublishedCurrentTurn(currentTurn)",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "currentTurn?.status === \"running\" && !currentTurn.turnId",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "currentTurn = await waitForPublishedCurrentTurn(currentTurn);",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "state.recapGridReadiness = recent.recapGridReadiness;",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "new Intl.NumberFormat(\"zh-CN\")",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "metrics.selectedRows",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "metrics.recipeRowSteps",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "metrics.missingAssignments",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "snapshot.orderedMissing?.length",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "当前authority尚未重新确认。",
            script,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "payload?.recapPlanning",
            script,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "snapshot.recapBuildIntervalHistoryLoad",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "await loadRecentTurns().catch(() => {});",
            script,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StaticClient_AllowsConsecutiveUndoAndProtectsEditedDraft() {
        await using var host = CreateHost();
        IWebHostEnvironment environment = host.Factory.Services
            .GetRequiredService<IWebHostEnvironment>();
        await using Stream stream = environment.WebRootFileProvider
            .GetFileInfo("assets/galatea.js")
            .CreateReadStream();
        using var reader = new StreamReader(stream);

        string script = await reader.ReadToEndAsync();

        Assert.Contains(
            "undoLastButton.disabled = maintenanceMode || state.streaming || !hasUndoableTurn();",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "function confirmPendingPoppedTurnReplacement()",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "if (!confirmPendingPoppedTurnReplacement())",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "继续撤销会覆盖输入框中尚未发送的修改",
            script,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "state.pendingPoppedDraftText = receipt.poppedUserText;",
            script,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "if (state.pendingPoppedTurn) {\n      return state.pendingPoppedTurn;",
            script,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "state.pendingPoppedTurn || !hasUndoableTurn()",
            script,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ExceptionClassifier_RejectsFatalProcessFailures() {
        Assert.True(GalateaExceptionClassifier.IsNonFatal(
            new IOException("ordinary I/O failure")
        ));
        Assert.True(GalateaExceptionClassifier.IsNonFatal(
            new InvalidDataException("ordinary invalid data")
        ));
        Assert.False(GalateaExceptionClassifier.IsNonFatal(
            new OutOfMemoryException()
        ));
        Assert.False(GalateaExceptionClassifier.IsNonFatal(
            new StackOverflowException()
        ));
        Assert.False(GalateaExceptionClassifier.IsNonFatal(
            new AccessViolationException()
        ));
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
                "/api/v1/chat/turns",
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
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            Interlocked.Increment(ref _createCallCount);
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

    private sealed class SingleClientFactory(ICompletionClient client)
        : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            return client;
        }
    }

    private sealed class FatalCompletionClient : ICompletionClient {
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal OutOfMemoryException Failure { get; } = new(
            "fatal completion fixture"
        );

        public string Name => "galatea-fatal-test";

        public string ApiSpecId => "openai-chat-v1";

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            throw Failure;
        }
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
