using System.Net;
using System.Net.Http.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaCompletionRetryHostTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdPendingRecoveryJittersOnceAndCancellationPreservesInput(bool cancelFirst) {
        var clock = new GalateaLabClock();
        var provider = CompletedClient();
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance, timeProvider: clock);
        EventAddress pending;
        using (var writer = SessionJournalEngine.Open(fixture.SessionDirectory)) {
            pending = writer.AppendObservation(GalateaUserMessageEnvelope.Wrap("cold pending"));
        }
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        int samples = 0;
        host.ColdRecoveryJitterSampleForTest = () => { samples++; return 1; };
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        using var cancellation = new CancellationTokenSource();
        Task<GalateaAutomaticTurnResult> pulse = coordinator.TryPulseAsync("alice", cancellation.Token);
        Assert.Equal(1, samples);
        Assert.False(pulse.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Equal(0, provider.Calls);
        Assert.False(pulse.IsCompleted);
        if (cancelFirst) {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulse);
            Assert.Equal(pending, session.Engine.ReadCurrentHead());
            Assert.Equal(0, provider.Calls);
            pulse = coordinator.TryPulseAsync("alice", CancellationToken.None);
        }
        else { clock.Advance(TimeSpan.FromMilliseconds(1)); }
        var resumed = Assert.IsType<GalateaAutomaticTurnResult.Started>(await pulse.WaitAsync(TimeSpan.FromSeconds(10)));
        await resumed.Turn.RunTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", resumed.Turn.Status);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, samples);
        Assert.Single(session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
        Assert.IsType<GalateaAutomaticTurnResult.Status>(await coordinator.TryPulseAsync("alice", CancellationToken.None));
        Assert.Equal(1, samples);
    }

    [Fact]
    public async Task FreshInputDoesNotUseColdRecoveryJitter() {
        var provider = CompletedClient();
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance, timeProvider: new GalateaLabClock());
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        host.ColdRecoveryJitterSampleForTest = () => throw new InvalidOperationException("Fresh input must not jitter.");
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        Assert.Null(session.ColdRecoveryJitterHead);
        await session.TurnLock.WaitAsync();
        var turn = host.StartTurn(session, "fresh input", new("test"), GalateaDelegateTestConfiguration.PlayerSender);
        var runner = fixture.Factory.Services.GetRequiredService<GalateaAcceptedTurnRunner>();
        await runner.Start(session, turn).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", turn.Status);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ExplicitRecoveryAndLaterDifferentPendingHeadDoNotConsumeColdDelay() {
        var provider = CompletedClient();
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance, timeProvider: new GalateaLabClock());
        EventAddress cold;
        using (var writer = SessionJournalEngine.Open(fixture.SessionDirectory)) {
            cold = writer.AppendObservation(GalateaUserMessageEnvelope.Wrap("old cold input"));
        }
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        host.ColdRecoveryJitterSampleForTest = () => throw new InvalidOperationException("Only the original automatic cold task may jitter.");
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        var runner = fixture.Factory.Services.GetRequiredService<GalateaAcceptedTurnRunner>();
        await session.TurnLock.WaitAsync();
        var manual = host.StartRecovery(session, new("test", GalateaTurnMode.Resume, cold));
        await runner.Start(session, manual).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", manual.Status);
        session.Engine.AppendObservation(GalateaUserMessageEnvelope.Wrap("later pending input"));
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        var next = Assert.IsType<GalateaAutomaticTurnResult.Started>(await coordinator.TryPulseAsync("alice", CancellationToken.None));
        await next.Turn.RunTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", next.Turn.Status);
        Assert.Equal(2, provider.Calls);
        Assert.Null(session.ColdRecoveryJitterHead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedActionOrTurnEndFailureBlocksLiveWriterAndColdRestartDoesNotRegenerate(bool turnEnd) {
        ScriptedClient? provider = null;
        provider = new ScriptedClient((request, _, _) => Task.FromResult(new CompletionResult(
            new ActionMessage([new ActionBlock.Text("provider result")]), CompletionDescriptor.From(provider!, request),
            termination: turnEnd ? CompletionTermination.Incomplete("max_output_tokens") : CompletionTermination.Completed())));
        await using var first = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance, deleteFilesOnDispose: false);
        var host = first.Factory.Services.GetRequiredService<GalateaHostService>();
        SessionEventKind publishedKind = turnEnd ? SessionEventKind.TurnEnded : SessionEventKind.AgentActionProduced;
        int published = 0;
        host.OpenSessionForTest = path => SessionJournalEngine.OpenForTest(path, runtime: null,
            new SessionJournalTestHooks(AfterCommitBeforeReturn: (kind, _) => {
                if (kind == publishedKind) {
                    published++;
                    throw new IOException("ref published before response failed");
                }
            }), new EventJournalOptions());
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        var runner = first.Factory.Services.GetRequiredService<GalateaAcceptedTurnRunner>();
        var coordinator = first.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        await session.TurnLock.WaitAsync();
        var live = host.StartTurn(session, "must not be repeated", new("test"), GalateaDelegateTestConfiguration.PlayerSender);
        await runner.Start(session, live).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, published);
        Assert.Equal(1, provider.Calls);
        Assert.True(session.GenerationBlocked);
        Assert.Throws<SessionJournalReopenRequiredException>(() => session.Engine.ReadCurrentHead());
        for (int i = 0; i < 3; i++) {
            Assert.IsType<GalateaAutomaticTurnResult.Blocked>(await coordinator.TryPulseAsync("alice", CancellationToken.None));
        }
        Assert.Equal(1, provider.Calls);
        await first.DisposeAsync();

        var afterRestart = CompletedClient();
        await using var restarted = first.CreateRestarted(afterRestart, DisabledGalateaUserMessageNormalizer.Instance, new NoDispatchTransport());
        var restartedCoordinator = restarted.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        Assert.IsType<GalateaAutomaticTurnResult.Status>(await restartedCoordinator.TryPulseAsync("alice", CancellationToken.None));
        var restartedHost = restarted.Factory.Services.GetRequiredService<GalateaHostService>();
        var restored = await restartedHost.GetSessionAsync("alice", CancellationToken.None);
        Assert.Equal(SessionExecutionPhase.Idle, restored.Engine.InspectExecutionBoundary().Phase);
        Assert.Equal(publishedKind, restored.Engine.InspectExecutionBoundary().HeadKind);
        SessionClosedTurnOutcome outcome = Assert.Single(restored.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns).Outcome;
        if (turnEnd) { Assert.IsType<SessionClosedTurnOutcome.Terminated>(outcome); }
        else { Assert.IsType<SessionClosedTurnOutcome.Completed>(outcome); }
        Assert.Equal(0, afterRestart.Calls);
    }

    [Fact]
    public async Task AdmissionStopRunsClientCancellationCallbacksOutsideSessionGate() {
        await using var fixture = GalateaTestHost.Create(CompletedClient(), DisabledGalateaUserMessageNormalizer.Instance);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        var operation = session.BeginAdmission(CancellationToken.None, CancellationToken.None);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = operation.Token.Register(() => {
            _ = Task.Run(session.ReadAdmissionStatus).GetAwaiter().GetResult();
            callbackCompleted.TrySetResult();
        });
        Assert.True(session.StopAdmission(operation.Id));
        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await operation.DisposeAsync();
        Assert.Equal("idle", session.ReadAdmissionStatus().State);
    }

    [Theory]
    [InlineData("response.refusal", SessionTurnEndReason.Rejected)]
    [InlineData("content_filter", SessionTurnEndReason.Rejected)]
    [InlineData("max_output_tokens", SessionTurnEndReason.Incomplete)]
    public void OnlyAuthoritativeBusinessReasonsCloseInput(string reason, SessionTurnEndReason expected) {
        Assert.Equal(expected, GalateaHostService.ClassifyBusinessTermination(CompletionTermination.Incomplete(reason)));
        Assert.Null(GalateaHostService.ClassifyBusinessTermination(CompletionTermination.Incomplete("unknown-protocol-state")));
        Assert.Null(GalateaHostService.ClassifyBusinessTermination(CompletionTermination.Failed("rate_limit")));
    }

    [Fact]
    public async Task StopCancelsSilentGenerationAndDurablyClosesAcceptedInput() {
        var provider = new ScriptedClient(async (_, _, token) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var runner = fixture.Factory.Services.GetRequiredService<GalateaAcceptedTurnRunner>();
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        var turn = host.StartTurn(session, "keep this input", new("test"), GalateaDelegateTestConfiguration.PlayerSender);
        Task running = runner.Start(session, turn);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(host.RequestStop(session, turn.TurnId));
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("terminated", turn.Status);
        Assert.Equal(SessionExecutionPhase.Idle, session.Engine.InspectExecutionBoundary().Phase);
        var closed = Assert.Single(session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
        Assert.IsType<SessionClosedTurnOutcome.Terminated>(closed.Outcome);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task EnvironmentFailureBlocksPulsesUntilExactHeadPendingStop() {
        var provider = new ScriptedClient((_, _, _) => throw new InvalidDataException("contract failure"));
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance);
        using HttpClient client = fixture.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var runner = fixture.Factory.Services.GetRequiredService<GalateaAcceptedTurnRunner>();
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        var turn = host.StartTurn(session, "pending", new("test"), GalateaDelegateTestConfiguration.PlayerSender);
        await runner.Start(session, turn).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.GenerationBlocked);
        for (int i = 0; i < 3; i++) {
            Assert.IsType<GalateaAutomaticTurnResult.Blocked>(await coordinator.TryPulseAsync("alice", CancellationToken.None));
        }
        Assert.Equal(1, provider.Calls);
        string head = EventAddressTextCodec.Format(session.Engine.ReadCurrentHead()!.Value);
        using var stop = await client.PostAsJsonAsync("/api/v1/characters/alice/chat/turns/pending/stop", new { expectedHead = head });
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        Assert.Equal(SessionExecutionPhase.Idle, session.Engine.InspectExecutionBoundary().Phase);
        Assert.False(session.GenerationBlocked);
        Assert.Equal(1, provider.Calls);
        using var stale = await client.PostAsJsonAsync("/api/v1/characters/alice/chat/turns/pending/stop", new { expectedHead = head });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task ZeroIntervalPendingObservationRecoversWithoutNewHeartbeat() {
        var provider = CompletedClient();
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        session.Engine.AppendObservation(GalateaUserMessageEnvelope.Wrap("already accepted"));
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        var started = Assert.IsType<GalateaAutomaticTurnResult.Started>(await coordinator.TryPulseAsync("alice", CancellationToken.None));
        Assert.Equal("recovery", started.Origin);
        await started.Turn.RunTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", started.Turn.Status);
        Assert.Equal(1, provider.Calls);
        Assert.Single(session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
    }

    [Fact]
    public async Task ZeroIntervalMissingSessionIsNotProvisionedByPulse() {
        var provider = CompletedClient();
        await using var fixture = GalateaTestHost.CreateMissingSession(provider, DisabledGalateaUserMessageNormalizer.Instance);
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        _ = await coordinator.TryPulseAsync("alice", CancellationToken.None);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ShutdownLeavesPreparedAndColdZeroIntervalPulseRecoversIt() {
        var blocked = new ScriptedClient(async (_, _, token) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        await using var first = GalateaTestHost.Create(blocked, DisabledGalateaUserMessageNormalizer.Instance, deleteFilesOnDispose: false);
        var host = first.Factory.Services.GetRequiredService<GalateaHostService>();
        var runner = first.Factory.Services.GetRequiredService<GalateaAcceptedTurnRunner>();
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        var live = host.StartTurn(session, "survive shutdown", new("test"), GalateaDelegateTestConfiguration.PlayerSender);
        Task running = runner.Start(session, live);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.BeginShutdown();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionExecutionPhase.AwaitingCompletion, session.Engine.InspectExecutionBoundary().Phase);
        await first.DisposeAsync();
        ScriptedClient? completed = null;
        completed = new ScriptedClient((request, _, _) => Task.FromResult(new CompletionResult(
            new ActionMessage([new ActionBlock.Text("recovered")]), CompletionDescriptor.From(completed!, request))));
        await using var restarted = first.CreateRestarted(completed, DisabledGalateaUserMessageNormalizer.Instance, new NoDispatchTransport());
        var coordinator = restarted.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        var resumed = Assert.IsType<GalateaAutomaticTurnResult.Started>(await coordinator.TryPulseAsync("alice", CancellationToken.None));
        await resumed.Turn.RunTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", resumed.Turn.Status);
        Assert.Equal(1, completed.Calls);
        var recoveredHost = restarted.Factory.Services.GetRequiredService<GalateaHostService>();
        var recovered = await recoveredHost.GetSessionAsync("alice", CancellationToken.None);
        Assert.Single(recovered.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
    }

    [Fact]
    public async Task StopDuringPostActionRateLimitPreservesCompletedActionAndReleasesWriter() {
        var featureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var silentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool useSilentFeature = false;
        bool silentDisposed = false;
        async Task<CompletionResult> SilentAsync(CancellationToken token) {
            silentEntered.TrySetResult();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }
            finally { silentDisposed = true; }
        }
        ScriptedClient? provider = null;
        provider = new ScriptedClient((request, _, token) => {
            if (request.PromptPrefix.OutputContract.Tools.Any(tool => tool.Name == OutboundMailExtractor.ToolName)) {
                if (useSilentFeature) { return SilentAsync(token); }
                featureEntered.TrySetResult();
                throw new CompletionFailureException(new(CompletionFailureKind.Http, 429), "rate limit");
            }
            return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.Text("finished")]),
                CompletionDescriptor.From(provider!, request)));
        });
        var main = new CompletionConnectionConfig("test", "openai-chat", "model-a", "openai-chat/strict", "http://localhost:8000/", ApiKey: "test-key");
        var helper = main with { Id = "helper" };
        await using var fixture = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, helper], connectionOptionIds: ["test"], outboundMailExtractorConnectionId: "helper");
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var runner = fixture.Factory.Services.GetRequiredService<GalateaAcceptedTurnRunner>();
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        var turn = host.StartTurn(session, "test postprocessing stop", new("test"), GalateaDelegateTestConfiguration.PlayerSender);
        Task running = runner.Start(session, turn);
        await featureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(host.RequestStop(session, turn.TurnId));
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", turn.Status);
        Assert.Equal(SessionExecutionPhase.Idle, session.Engine.InspectExecutionBoundary().Phase);
        var closed = Assert.Single(session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
        Assert.IsType<SessionClosedTurnOutcome.Completed>(closed.Outcome);
        Assert.True(session.TurnLock.Wait(0));
        session.TurnLock.Release();

        // Old extraction remains recoverable without inventing a new turn.
        // Its separate transient owner can also be stopped without TurnLock.
        var coordinator = fixture.Factory.Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        Task<GalateaAutomaticTurnResult> admission = coordinator.TryPulseAsync("alice", CancellationToken.None);
        GalateaAdmissionStatusDto operation = session.ReadAdmissionStatus();
        Assert.NotNull(operation.OperationId);
        Assert.Equal("running", operation.State);
        using HttpClient client = fixture.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);
        using var stopped = await client.PostAsync($"/api/v1/characters/alice/agent/admission/{operation.OperationId}/stop", null);
        Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
        await admission.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("idle", session.ReadAdmissionStatus().State);
        Assert.False(session.AutomaticAdmissionFailed);
        Assert.Single(session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
        using var stale = await client.PostAsync($"/api/v1/characters/alice/agent/admission/{operation.OperationId}/stop", null);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        useSilentFeature = true;
        Task<GalateaAutomaticTurnResult> silentAdmission = coordinator.TryPulseAsync("alice", CancellationToken.None);
        await silentEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.BeginShutdown();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => silentAdmission.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(silentDisposed);
        Assert.Equal("idle", session.ReadAdmissionStatus().State);
        Assert.True(session.TurnLock.Wait(0));
        session.TurnLock.Release();
    }

    private sealed class NoDispatchTransport : IGalateaDurableDelegateTransport {
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(GalateaEnsureDelegateBindingRequest request, CancellationToken ct) => throw new InvalidOperationException("unexpected delegate call");
        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(GalateaStartDelegateTurnRequest request, CancellationToken ct) => throw new InvalidOperationException("unexpected delegate call");
        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(GalateaInspectDelegateDispatchRequest request, CancellationToken ct) => throw new InvalidOperationException("unexpected delegate call");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedClient(Func<CompletionRequest, CompletionStreamObserver?, CancellationToken, Task<CompletionResult>> run)
        : ICompletionClient, ICompletionClientFactory {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public string Name => "retry-host-test";
        public string ApiSpecId => "openai-chat-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Calls++;
            Entered.TrySetResult();
            return run(request, observer, cancellationToken);
        }
    }

    private static ScriptedClient CompletedClient() {
        ScriptedClient? client = null;
        client = new ScriptedClient((request, _, _) => Task.FromResult(new CompletionResult(
            new ActionMessage([new ActionBlock.Text("recovered")]), CompletionDescriptor.From(client!, request))));
        return client;
    }
}
