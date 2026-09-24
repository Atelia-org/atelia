using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDurableRecoveryVerticalTests {
    private static readonly TimeSpan CompletionDeadline =
        TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ZeroIntervalPulse_WithReadyReply_StartsOnlyDelegateReply() {
        var completionFactory = new TrackingCompletionClientFactory(
            "durable reply accepted"
        );
        await using var fixture = GalateaTestHost.Create(
            completionFactory,
            DisabledGalateaUserMessageNormalizer.Instance,
            delegateTransport: NoDispatchTransport.Instance
        );
        SeedReadyReplyOffline(fixture, "durable reply");
        GalateaHostService service = fixture.Factory.Services
            .GetRequiredService<GalateaHostService>();
        GalateaAutomaticTurnCoordinator coordinator = fixture.Factory.Services
            .GetRequiredService<GalateaAutomaticTurnCoordinator>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice", CancellationToken.None
        );
        Assert.Null(session.AutonomyCadence);

        GalateaAutomaticTurnResult.Started started = Assert.IsType<
            GalateaAutomaticTurnResult.Started>(
                await coordinator.TryPulseAsync("alice", CancellationToken.None)
            );

        Assert.Equal("delegate-reply", started.Origin);
        Assert.IsType<GalateaFreshInput.DelegateReply>(started.Turn.FreshInput);
        await Assert.IsAssignableFrom<Task>(started.Turn.RunTask)
            .WaitAsync(CompletionDeadline);
        Assert.Equal("completed", started.Turn.Status);
        Assert.Equal(1, completionFactory.Client.DispatchCallCount);
        Assert.Equal("waiting", coordinator.ReadStatus("alice").State);
        Assert.Null(coordinator.ReadStatus("alice").NextActivationAtUnixTimeMilliseconds);
    }

    [Fact]
    public async Task ZeroIntervalActiveLease_ColdRestartRecoversOriginalInputAndConsumesLeaseOnce() {
        var firstFactory = new TrackingCompletionClientFactory();
        GalateaTestHost first = GalateaTestHost.Create(
            firstFactory,
            DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: false,
            delegateTransport: NoDispatchTransport.Instance
        );
        GalateaTestHost? restarted = null;
        try {
            GalateaReplyNoticeSnapshot uncertain = SeedReadyReplyOffline(
                first, "unconfirmed reply", resultUnconfirmed: true);
            GalateaHostService firstService = first.Factory.Services
                .GetRequiredService<GalateaHostService>();
            CharacterSessionHost firstSession = await firstService.GetSessionAsync(
                "alice", CancellationToken.None
            );
            GalateaReplyLeaseSnapshot membership = firstSession.RequireDelegationHandle().Store.BeginReplyLeaseMembership(
                "cold-active-lease",
                PlayerTurnObservationEnvelope
                    .DelegateReplyLeasePlayerTextDiscriminator,
                [new(uncertain.NoticeId, uncertain.Revision)]
            );
            await firstSession.TurnLock.WaitAsync();
            EventAddress observation;
            try {
                var lease = new GalateaDurableReplyLease(firstSession.RequireDelegationHandle().Store,
                    membership.LeaseId, membership.Revision);
                SessionInputContent input = GalateaObservationContent.Create(
                    new GalateaFreshInput.DelegateReply(lease.ReadNotices()), DateTimeOffset.UnixEpoch,
                    GalateaDelegationTestInputs.Sender(firstSession.RequireDelegationHandle().Store, "Galatea")
                );
                lease.BindObservationBase(firstSession.Engine, firstSession.Engine.ReadCurrentHead()!.Value, input);
                observation = firstSession.Engine.AppendObservation(input);
            }
            finally { firstSession.TurnLock.Release(); }
            Assert.Equal(
                GalateaAutomaticWakeReason.ActiveReplyLease,
                firstService.DelegationSupervisor.ReadAutomaticWakeReason(
                    "alice"
                )
            );
            await first.DisposeAsync();

            var restartFactory = new TrackingCompletionClientFactory();
            restarted = first.CreateRestarted(
                restartFactory,
                DisabledGalateaUserMessageNormalizer.Instance,
                NoDispatchTransport.Instance
            );
            GalateaHostService restartedService = restarted.Factory.Services
                .GetRequiredService<GalateaHostService>();
            GalateaAutomaticTurnCoordinator coordinator = restarted.Factory
                .Services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
            Assert.Null(restartedService.ReadAttachedSession("alice"));
            Assert.Equal(
                GalateaAutomaticWakeReason.ActiveReplyLease,
                restartedService.DelegationSupervisor.ReadAutomaticWakeReason(
                    "alice"
                )
            );

            GalateaAutomaticTurnResult.Started recovery = Assert.IsType<
                GalateaAutomaticTurnResult.Started>(
                    await coordinator.TryPulseAsync("alice", CancellationToken.None)
                );

            Assert.Equal("recovery", recovery.Origin);
            await recovery.Turn.RunTask!.WaitAsync(CompletionDeadline);
            Assert.Equal("completed", recovery.Turn.Status);
            CharacterSessionHost restartedSession = Assert.IsType<
                CharacterSessionHost>(restartedService.ReadAttachedSession("alice"));
            GalateaDelegationStateSnapshot durable = restartedSession
                .DelegationHandle!.Store.ReadSnapshot();
            Assert.Null(durable.ActiveLease);
            GalateaReplyNoticeSnapshot retained = Assert.Single(durable.Notices);
            Assert.Equal(GalateaReplyNoticeState.Consumed, retained.State);
            Assert.Equal("RESULT_UNCONFIRMED", retained.Code);
            Assert.Equal(1, restartFactory.Client.DispatchCallCount);
            var completed = Assert.Single(restartedSession.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
            Assert.Equal(observation, completed.ObservationAddress);
            Assert.Equal(EventAddressTextCodec.Format(completed.Outcome.Address), retained.ConsumedTurnEndAddress);
            Assert.IsType<GalateaAutomaticTurnResult.Status>(
                await coordinator.TryPulseAsync("alice", CancellationToken.None));
            Assert.Equal(1, restartFactory.Client.DispatchCallCount);
            Assert.Single(restartedSession.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
            Assert.Null(restartedSession.GetCurrentTurn());
        }
        finally {
            if (restarted is not null) {
                await restarted.DisposeAsync();
            }
            else {
                await first.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ZeroIntervalPulse_WhenReadyIsConsumedAfterProbe_DoesNotStartHeartbeat() {
        var completionFactory = new TrackingCompletionClientFactory();
        await using var fixture = GalateaTestHost.Create(
            completionFactory,
            DisabledGalateaUserMessageNormalizer.Instance,
            delegateTransport: NoDispatchTransport.Instance
        );
        SeedReadyReplyOffline(fixture, "raced reply");
        GalateaHostService service = fixture.Factory.Services
            .GetRequiredService<GalateaHostService>();
        GalateaAutomaticTurnCoordinator coordinator = fixture.Factory.Services
            .GetRequiredService<GalateaAutomaticTurnCoordinator>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice", CancellationToken.None
        );
        Assert.Equal(
            GalateaAutomaticWakeReason.ReadyNotice,
            service.DelegationSupervisor.ReadAutomaticWakeReason("alice")
        );
        coordinator.BeforeReadyReplyCutoffForTest = _ =>
            ConsumeReadyReplyAsOtherWinner(
                session.RequireDelegationHandle().Store,
                session.Engine
            );

        try {
            GalateaAutomaticTurnResult.Status result = Assert.IsType<
                GalateaAutomaticTurnResult.Status>(
                    await coordinator.TryPulseAsync("alice", CancellationToken.None)
                );
            Assert.Equal("waiting", result.Value.State);
        }
        finally {
            coordinator.BeforeReadyReplyCutoffForTest = null;
        }

        Assert.Null(session.GetCurrentTurn());
        Assert.Null(session.RequireDelegationHandle().Store.ReadSnapshot().ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Consumed,
            Assert.Single(session.RequireDelegationHandle().Store.ReadSnapshot().Notices)
                .State
        );
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);
        Assert.Null(session.AutonomyCadence);
    }

    [Fact]
    public async Task ReadyReplyTurn_WhenTurnFailed_DoesNotClaimOrAbandon() {
        var completionFactory = new TrackingCompletionClientFactory();
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer,
            delegateTransport: NoDispatchTransport.Instance,
            autonomyCharacterIds: ["alice"]
        );
        CompletionConnectionConfig connection = GalateaConfigLoader.Load(host.ConfigPath)
            .Connections.Single(value => value.Id == "test");
        EventAddress failedHead = await CreateFailedBoundaryAsync(
            host.SessionDirectory,
            connection
        );
        SeedReadyReplyOffline(host, "reply must remain ready");
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/mailbox/ready-turn",
            new ReadyReplyTurnRequest()
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument body = await ReadJsonAsync(response);
        Assert.Equal(
            "legacy-completion-failed",
            body.RootElement.GetProperty("code").GetString()
        );
        SessionRuntimeRecoveryRequirements.LegacyFailedTurnBlocked after =
            Assert.IsType<SessionRuntimeRecoveryRequirements
                .LegacyFailedTurnBlocked>(
                    session.Engine.InspectRuntimeRecoveryRequirements()
                );
        Assert.Equal(failedHead, after.FailedHead);
        GalateaDelegationStateSnapshot delegation = session
            .RequireDelegationHandle().Store.ReadSnapshot();
        Assert.Null(delegation.ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Ready,
            Assert.Single(delegation.Notices).State
        );
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);
    }

    [Fact]
    public async Task NewMessage_WhenObservationAwaitsAction_ReturnsRecoveryConflictWithoutCalls() {
        var completionFactory = new TrackingCompletionClientFactory();
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        EventAddress pendingHead = AppendPendingObservation(sessionPath);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns",
            new ChatStreamRequest(
                "must not be accepted",
                DiagnosticConnectionId: "test"
            )
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument body = await ReadJsonAsync(response);
        Assert.Equal(
            "recovery-required",
            body.RootElement.GetProperty("code").GetString()
        );
        Assert.Equal(
            ["code", "error"],
            body.RootElement.EnumerateObject()
                .Select(static property => property.Name)
                .Order()
                .ToArray()
        );
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(pendingHead, session.Engine.ReadCurrentHead());
        Assert.Equal(
            SessionExecutionPhase.AwaitingAgentAction,
            session.Engine.InspectExecutionBoundary().Phase
        );
    }

    [Fact]
    public async Task NewMessage_WhenLegacyTurnFailed_RequiresExplicitEndBeforeSend() {
        var completionFactory = new TrackingCompletionClientFactory(
            "answer after abandon"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        CompletionConnectionConfig connection = GetConnection(host);
        EventAddress failedHead = await CreateFailedBoundaryAsync(
            sessionPath,
            connection
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        CurrentTurnDto? before = await client.GetFromJsonAsync<
            CurrentTurnDto
        >("/api/v1/characters/alice/chat/turns/current");
        Assert.NotNull(before);
        Assert.Equal("recovery-required", before!.Status);
        Assert.Null(before.TurnId);
        Assert.Null(before.ConnectionId);
        Assert.Equal(EventAddressTextCodec.Format(failedHead), before.RecoveryHead);
        using (HttpResponseMessage blocked = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns", new ChatStreamRequest("must not replace failed input", "test"))) {
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        }
        using (HttpResponseMessage stopped = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/pending/stop",
            new StopPendingTurnRequest(EventAddressTextCodec.Format(failedHead)))) {
            Assert.True(stopped.IsSuccessStatusCode);
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns",
            new ChatStreamRequest(
                "continue after failure",
                DiagnosticConnectionId: "test"
            )
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        Assert.NotNull(started);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started!.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.Equal(1, normalizer.NormalizeCallCount);
        Assert.Equal(1, completionFactory.CreateCallCount);
        Assert.Equal(1, completionFactory.Client.DispatchCallCount);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
        Assert.NotEqual(failedHead, session.Engine.ReadCurrentHead());
        SessionCompletedTurnProjection completed =
            session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns[0];
        Assert.True(completed.ObservationContent.IsStructured);
        PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(completed.ObservationContent);
        Assert.Equal("continue after failure", observation.PlayerText);
        Assert.NotNull(observation.ExternalLocalTimestamp);
    }

    [Fact]
    public async Task DirectFreshHiddenConnection_DoesNotAbandonPreviousFailedTurn() {
        CompletionConnectionConfig visible = Connection(
            "test",
            "visible-model"
        );
        CompletionConnectionConfig hidden = Connection(
            "hidden-helper",
            "hidden-model"
        );
        var completionFactory = new TrackingCompletionClientFactory(
            "must not dispatch"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer,
            connections: [visible, hidden],
            connectionOptionIds: [visible.Id]
        );
        EventAddress failedHead = await CreateFailedBoundaryAsync(
            host.SessionDirectory,
            visible
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        SessionExecutionBoundaryInspection before = session.Engine
            .InspectExecutionBoundary();
        Assert.IsType<SessionRuntimeRecoveryRequirements
            .LegacyFailedTurnBlocked>(
                session.Engine.InspectRuntimeRecoveryRequirements()
            );
        Assert.Throws<ArgumentException>(() => service.StartTurn(
            session,
            "must not abandon failed turn",
            new GalateaTurnOptions(hidden.Id),
            GalateaDelegateTestConfiguration.PlayerSender
        ));
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);
        Assert.Equal(failedHead, session.Engine.ReadCurrentHead());
        Assert.Equal(before, session.Engine.InspectExecutionBoundary());
        SessionRuntimeRecoveryRequirements.LegacyFailedTurnBlocked
            after = Assert.IsType<SessionRuntimeRecoveryRequirements
                .LegacyFailedTurnBlocked>(
                    session.Engine.InspectRuntimeRecoveryRequirements()
                );
        Assert.Equal(failedHead, after.FailedHead);
        Assert.Null(session.GetCurrentTurn());
    }

    [Fact]
    public async Task FreshTypedNoDispatchRejection_RetainsPreparedUntilExplicitStop() {
        var completion = new SequencedCompletionClient();
        completion.Enqueue(_ => throw new CompletionRequestRejectedException(
            CompletionTermination.Failed(
                "openai.responses.invalid-function-name",
                "The adapter rejected an invalid function name before dispatch."
            ),
            ["adapter-validation=function-name"]
        ));
        completion.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("answer after rejection")]),
            new CompletionDescriptor(
                completion.Name,
                completion.ApiSpecId,
                request.ModelId
            )
        ));
        var completionFactory = new SingleCompletionClientFactory(completion);
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        StartTurnResponseDto first = await StartFreshAsync(
            client,
            "first rejected turn"
        );
        GalateaLiveTurn firstTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, first.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(firstTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("failed", firstTurn.Status);
        Assert.Equal(
            SessionExecutionPhase.AwaitingCompletion,
            session.Engine.InspectExecutionBoundary().Phase
        );
        CurrentTurnDto? settled = await client.GetFromJsonAsync<
            CurrentTurnDto
        >("/api/v1/characters/alice/chat/turns/current");
        Assert.NotNull(settled);
        Assert.Equal("recovery-required", settled!.Status);
        Assert.NotNull(settled.RecoveryHead);
        using (HttpResponseMessage stopped = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/pending/stop",
            new StopPendingTurnRequest(settled.RecoveryHead!))) {
            Assert.True(stopped.IsSuccessStatusCode);
        }

        StartTurnResponseDto second = await StartFreshAsync(
            client,
            "second accepted turn"
        );
        GalateaLiveTurn secondTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, second.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(secondTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", secondTurn.Status);
        Assert.Equal(2, completion.DispatchCallCount);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
        var turns = session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns;
        Assert.Equal(2, turns.Count);
        Assert.IsType<SessionClosedTurnOutcome.Terminated>(turns[1].Outcome);
        SessionCompletedTurnProjection completed = turns[0];
        Assert.Equal(
            "answer after rejection",
            completed.RequireTerminalAction().Message.GetFlattenedText()
        );
    }

    [Fact]
    public async Task FreshUnclassifiedHttpException_RemainsRecoveryRequired() {
        var completion = new SequencedCompletionClient();
        completion.Enqueue(_ => throw new HttpRequestException(
            "simulated backend failure",
            inner: null,
            HttpStatusCode.InternalServerError
        ));
        var completionFactory = new SingleCompletionClientFactory(completion);
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        StartTurnResponseDto started = await StartFreshAsync(
            client,
            "uncertain backend turn"
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("failed", liveTurn.Status);
        Assert.Equal(
            SessionExecutionPhase.AwaitingCompletion,
            session.Engine.InspectExecutionBoundary().Phase
        );
        CurrentTurnDto? current = await client.GetFromJsonAsync<
            CurrentTurnDto
        >("/api/v1/characters/alice/chat/turns/current");
        Assert.NotNull(current);
        Assert.Equal("recovery-required", current!.Status);
        Assert.NotNull(current.RecoveryHead);
        Assert.Equal(1, completion.DispatchCallCount);
    }

    [Fact]
    public async Task FreshStructuredHttp5xx_RetriesSamePreparedWithoutAnotherObservation() {
        var completion = new SequencedCompletionClient();
        completion.Enqueue(_ => throw new CompletionFailureException(
            new CompletionFailureInfo(CompletionFailureKind.Http, 503), "temporary"));
        completion.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("retried answer")]),
            new CompletionDescriptor(completion.Name, completion.ApiSpecId, request.ModelId)));
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(new SingleCompletionClientFactory(completion), normalizer,
            deleteFilesOnDispose: false);
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        StartTurnResponseDto started = await StartFreshAsync(http, "one logical input");
        GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(service.FindTurn(session, started.TurnId));
        await Assert.IsAssignableFrom<Task>(turn.RunTask).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("completed", turn.Status);
        Assert.Equal(2, completion.DispatchCallCount);
        Assert.Equal(1, normalizer.NormalizeCallCount);
        await host.DisposeAsync();
        var events = new List<SessionJournalAuditEvent>();
        using (var cold = SessionJournalEngine.OpenReadOnly(host.SessionDirectory)) {
            cold.ScanCheckedAuditEvents(events.Add);
        }
        Assert.Single(events, item => item.Kind == SessionEventKind.ObservationAccepted);
        Assert.Single(events, item => item.Kind == SessionEventKind.CompletionRequestPrepared);
        Assert.Single(events, item => item.Kind == SessionEventKind.AgentActionProduced);
        Assert.DoesNotContain(events, item => item.Kind is SessionEventKind.CompletionAttemptStarted or SessionEventKind.CompletionAttemptFailed);
        TestDirectorySafety.DeleteOwnedTreeNoFollow(host.RootDirectory);
    }

    [Fact]
    public async Task Resume_WhenTurnFailed_RejectsBeforeRuntimeWork() {
        var completionFactory = new TrackingCompletionClientFactory(
            "must not dispatch"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        EventAddress failedHead = await CreateFailedBoundaryAsync(
            sessionPath,
            GetConnection(host)
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(failedHead),
                DiagnosticConnectionId: null
            )
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument body = await ReadJsonAsync(response);
        Assert.Equal(
            "failed-turn-must-be-abandoned",
            body.RootElement.GetProperty("code").GetString()
        );
        Assert.Equal(
            ["code", "error"],
            body.RootElement.EnumerateObject()
                .Select(static property => property.Name)
                .Order()
                .ToArray()
        );
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(failedHead, session.Engine.ReadCurrentHead());
    }

    [Fact]
    public async Task ResumeMatchingObservation_CompletesWithoutNormalizingAgain() {
        var completionFactory = new TrackingCompletionClientFactory(
            "resumed answer"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        EventAddress pendingHead = AppendPendingObservation(sessionPath);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        StartTurnResponseDto started = await ResumeAsync(
            client,
            pendingHead,
            connectionId: "test"
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(1, completionFactory.CreateCallCount);
        Assert.Equal(1, completionFactory.Client.DispatchCallCount);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
        SessionCompletedTurnProjection completed = Assert.Single(
            session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns
        );
        Assert.Equal(
            GalateaUserMessageEnvelope.Wrap("already normalized"),
            completed.ObservationContent.TextValue
        );
        Assert.Equal(
            "resumed answer",
            completed.RequireTerminalAction().Message.GetFlattenedText()
        );
    }

    [Fact]
    public async Task ResumeMatchingObservation_RejectsHiddenCurrentConnection() {
        CompletionConnectionConfig visible = Connection(
            "test",
            "visible-model"
        );
        CompletionConnectionConfig hidden = Connection(
            "hidden-helper",
            "hidden-model"
        );
        var completionFactory = new TrackingCompletionClientFactory(
            "must not dispatch"
        );
        await using var host = GalateaTestHost.Create(
            completionFactory,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [visible, hidden],
            connectionOptionIds: [visible.Id]
        );
        EventAddress pendingHead = AppendPendingObservation(
            host.SessionDirectory
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(pendingHead),
                hidden.Id
            )
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, completionFactory.CreateCallCount);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(pendingHead, session.Engine.ReadCurrentHead());
        Assert.Null(session.GetCurrentTurn());
        Assert.True(session.TurnLock.Wait(0));
        session.TurnLock.Release();
    }

    [Fact]
    public async Task DirectNewRequestRecovery_HiddenConnectionFailsBeforeClientOrMutation() {
        CompletionConnectionConfig visible = Connection(
            "test",
            "visible-model"
        );
        CompletionConnectionConfig hidden = Connection(
            "hidden-helper",
            "hidden-model"
        );
        var completionFactory = new TrackingCompletionClientFactory(
            "must not dispatch"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer,
            connections: [visible, hidden],
            connectionOptionIds: [visible.Id]
        );
        EventAddress pendingHead = AppendPendingObservation(
            host.SessionDirectory
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn turn = service.StartRecovery(
            session,
            new GalateaTurnOptions(
                hidden.Id,
                GalateaTurnMode.Resume,
                ExpectedHead: pendingHead
            )
        );
        try {
            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException>(() => service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                ));

            Assert.Equal(
                "recap-grid-connection-absent",
                failure.FailureReason
            );
            Assert.Equal(0, completionFactory.CreateCallCount);
            Assert.Equal(0, completionFactory.Client.DispatchCallCount);
            Assert.Equal(0, normalizer.NormalizeCallCount);
            Assert.Equal(pendingHead, session.Engine.ReadCurrentHead());
        }
        finally {
            service.FinishTurn(session, turn);
        }
    }

    [Fact]
    public async Task ResumePrepared_ExactBindsWithoutOpeningRecapGridRoutes() {
        var completionFactory = new TrackingCompletionClientFactory(
            "prepared recovery answer"
        );
        var normalizer = new TrackingNormalizer();
        using var callLogs = new TemporaryCallLogDirectory();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer,
            callLogDirectory: callLogs.Path
        );
        string sessionPath = GetSessionPath(host);
        CompletionConnectionConfig connection = GetConnection(host);
        EventAddress preparedHead = await CreateRecoveryBoundaryAsync(
            sessionPath,
            connection,
            completionFactory.Client,
            "AfterRequestPreparedCommitted",
            SessionExecutionPhase.AwaitingCompletion
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        StartTurnResponseDto started = await ResumeAsync(
            client,
            preparedHead,
            connectionId: null
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(1, completionFactory.CreateCallCount);
        Assert.Equal(1, completionFactory.Client.DispatchCallCount);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(callLogs.Path, "completion"),
            "*.json"
        ));
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
    }

    [Fact]
    public async Task ResumePrepared_ExactBindsHistoricalNonSelectableConnection() {
        CompletionConnectionConfig visible = Connection(
            "test",
            "visible-model"
        );
        CompletionConnectionConfig historical = Connection(
            "historical",
            "historical-model"
        );
        var completionFactory = new TrackingCompletionClientFactory(
            "historical recovery answer"
        );
        await using var host = GalateaTestHost.Create(
            completionFactory,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [visible, historical],
            connectionOptionIds: [visible.Id]
        );
        EventAddress preparedHead = await CreateRecoveryBoundaryAsync(
            host.SessionDirectory,
            historical,
            completionFactory.Client,
            "AfterRequestPreparedCommitted",
            SessionExecutionPhase.AwaitingCompletion
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        StartTurnResponseDto started = await ResumeAsync(
            client,
            preparedHead,
            connectionId: null
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.False(service.TryGetRecoveryConnection(
            session.Character,
            historical.Id,
            out _
        ));
        Assert.Equal(1, completionFactory.CreateCallCount);
        Assert.Equal(historical.Id, completionFactory.LastConnectionId);
        Assert.Equal(historical.Id, liveTurn.Options.ConnectionId);
    }

    [Fact]
    public async Task ResumeLegacyStarted_StaleHeadRefusesBeforeClientCreation() {
        var completionFactory = new TrackingCompletionClientFactory(
            "must not dispatch"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        CompletionConnectionConfig connection = GetConnection(host);
        EventAddress startedHead = await CreateRecoveryBoundaryAsync(
            sessionPath,
            connection,
            completionFactory.Client,
            "AfterCompletionAttemptStartedCommitted",
            SessionExecutionPhase.AwaitingCompletion
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/resume",
            new ResumeTurnRequest(
                "ej1:00000000000000010000000100000000",
                DiagnosticConnectionId: null
            )
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument body = await ReadJsonAsync(response);
        Assert.Equal(
            "stale-session-head",
            body.RootElement.GetProperty("code").GetString()
        );
        Assert.Equal(
            ["code", "error"],
            body.RootElement.EnumerateObject()
                .Select(static property => property.Name)
                .Order()
                .ToArray()
        );
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(startedHead, session.Engine.ReadCurrentHead());
        CurrentTurnDto? current = await client
            .GetFromJsonAsync<CurrentTurnDto>(
                "/api/v1/characters/alice/chat/turns/current"
            );
        Assert.NotNull(current);
        Assert.Equal("recovery-required", current!.Status);
        Assert.Equal(
            EventAddressTextCodec.Format(startedHead),
            current.RecoveryHead
        );
    }

    [Fact]
    public async Task ResumeStarted_ExplicitExactHeadRestartCompletes() {
        var completionFactory = new TrackingCompletionClientFactory(
            "restarted answer"
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer
        );
        string sessionPath = GetSessionPath(host);
        CompletionConnectionConfig connection = GetConnection(host);
        EventAddress startedHead = await CreateRecoveryBoundaryAsync(
            sessionPath,
            connection,
            completionFactory.Client,
            "AfterCompletionAttemptStartedCommitted",
            SessionExecutionPhase.AwaitingCompletion
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        using (HttpResponseMessage rejected = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(startedHead),
                DiagnosticConnectionId: connection.Id))) {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using JsonDocument body = await ReadJsonAsync(rejected);
            Assert.Equal("diagnostic-connection-not-applicable",
                body.RootElement.GetProperty("code").GetString());
            Assert.Equal(0, completionFactory.CreateCallCount);
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(startedHead),
                DiagnosticConnectionId: null
            )
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        Assert.NotNull(started);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started!.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(1, completionFactory.CreateCallCount);
        Assert.Equal(1, completionFactory.Client.DispatchCallCount);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
        Assert.Equal(
            "restarted answer",
            session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns[^1]
                .RequireTerminalAction().Message.GetFlattenedText()
        );
    }

    private static string GetSessionPath(GalateaTestHost host) =>
        host.Factory.Services
            .GetRequiredService<GalateaConfig>()
            .Characters.Single().SessionDir;

    private static CompletionConnectionConfig GetConnection(
        GalateaTestHost host
    ) => host.Factory.Services
        .GetRequiredService<GalateaConfig>()
        .Connections.Single(static connection => connection.Id == "test");

    private static CompletionConnectionConfig Connection(
        string id,
        string modelId
    ) => new(
        id,
        "openai-chat",
        modelId,
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static GalateaReplyNoticeSnapshot SeedReadyReplyOffline(
        GalateaTestHost fixture,
        string reply,
        bool resultUnconfirmed = false
    ) {
        // Seed every CAS transition before Factory.Services starts the active
        // supervisor. A live driver must never race a synthetic intermediate mail.
        GalateaConfig config = GalateaConfigLoader.Load(fixture.ConfigPath);
        GalateaCharacterConfig character = config.Characters.Single();
        using var engine = SessionJournalEngine.OpenReadOnly(fixture.SessionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(character.DelegationStateDir)!);
        using var store = GalateaDelegationSqliteStore.CreateNew(
            character.DelegationStateDir,
            new GalateaDelegationStoreOwner(character.CharacterId,
                GalateaDelegationSupervisor.CreateSessionRepositoryId(character.SessionDir)),
            new GalateaDelegationStoreBaseline(engine.ReadView.ReadPhysicalAppendFrontier(),
                EventAddressTextCodec.FormatNullable(engine.ReadCurrentHead())),
            GalateaDelegationSupervisor.CreateLimits(config.Delegates.CodexRoute));
        return SeedReadyReply(store, reply, resultUnconfirmed);
    }

    private static GalateaReplyNoticeSnapshot SeedReadyReply(
        GalateaDelegationSqliteStore store,
        string reply,
        bool resultUnconfirmed = false
    ) {
        GalateaDelegationCaptureResult captured = store.CaptureActionBatch(
            new GalateaDelegationCaptureRequest(
                "ej1:00000000000010010000000100000000",
                new string('a', 64),
                VisibleActionUtf8Bytes: 6,
                "extractor-contract-v1",
                [new SendMailIntent(
                    GalateaDelegateConfigReader.CanonicalRecipient,
                    Subject: null,
                    Body: "seed task",
                    InReplyToMessageId: null,
                    EvidenceQuote: "seeded"
                )]
            , GalateaDelegationTestInputs.Sender(store, "Galatea"))
        );
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        GalateaRouteBindingSnapshot binding = store.BeginThreadBinding(
            "seed-binding",
            snapshot.Route.Revision,
            Assert.Single(captured.DispatchIds),
            snapshot.Mails.Single(value => value.DispatchId == captured.DispatchIds[0]).Revision
        );
        _ = store.CompleteThreadBinding(
            binding.BindingOperationId!,
            "seed-thread",
            binding.Revision
        );
        snapshot = store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = snapshot.Mails.Single(value =>
            string.Equals(
                value.DispatchId,
                Assert.Single(captured.DispatchIds),
                StringComparison.Ordinal
            )
        );
        GalateaOutboundMailSnapshot started = store.StartQueuedMail(
            mail.DispatchId,
            mail.Revision,
            snapshot.Route.Revision
        , GalateaDelegationTestInputs.Commitment(store, mail.DispatchId));
        return resultUnconfirmed
            ? store.FinishMailLocally(
                started.DispatchId,
                started.Revision,
                store.ReadSnapshot().Route.Revision,
                "RESULT_UNCONFIRMED",
                resetBinding: true
            )
            : store.RecordCompletedMail(
                started.DispatchId,
                started.Revision,
                "seed-thread",
                "seed-turn",
                reply
            );
    }

    private static void ConsumeReadyReplyAsOtherWinner(
        GalateaDelegationSqliteStore store,
        SessionJournalEngine engine
    ) {
        GalateaReplyNoticeSnapshot notice = Assert.Single(
            store.ReadSnapshot().Notices,
            static value => value.State == GalateaReplyNoticeState.Ready
        );
        const string playerText = PlayerTurnObservationEnvelope
            .DelegateReplyLeasePlayerTextDiscriminator;
        GalateaReplyLeaseSnapshot lease = store.BeginReplyLeaseMembership(
            "racing-winner",
            playerText,
            [new(notice.NoticeId, notice.Revision)]
        );
        GalateaDurableReplyLease durable = new(
            store,
            lease.LeaseId,
            lease.Revision
        );
        SessionInputContent input = GalateaObservationContent.Create(
            new GalateaFreshInput.DelegateReply(durable.ReadNotices()),
            DateTimeOffset.UnixEpoch,
            GalateaDelegationTestInputs.Sender(store, "Galatea")
        );
        string head = EventAddressTextCodec.Format(Assert.IsType<EventAddress>(
            engine.ReadCurrentHead()
        ));
        lease = store.BindReplyLeaseObservationBase(
            lease.LeaseId,
            lease.Revision,
            head,
            input
        );
        lease = store.RecordLeaseObservationCommitted(
            lease.LeaseId,
            lease.Revision,
            head
        );
        store.ConsumeReplyLease(lease.LeaseId, lease.Revision, head);
    }

    private static EventAddress AppendPendingObservation(
        string sessionPath
    ) {
        using var engine = SessionJournalEngine.Open(sessionPath);
        return engine.AppendObservation(
            GalateaUserMessageEnvelope.Wrap("already normalized")
        );
    }

    internal static async Task<EventAddress> CreateRecoveryBoundaryAsync(
        string sessionPath,
        CompletionConnectionConfig connection,
        ICompletionClient client,
        string failpointName,
        SessionExecutionPhase expectedPhase,
        Func<SessionJournalEngine, SessionRuntime, ValueTask<IAsyncDisposable>>?
            bindRuntime = null
    ) {
        SessionRuntime runtime = CreateFixtureRuntime(
            connection,
            client
        );
        Assembly assembly = typeof(SessionJournalEngine).Assembly;
        Type failpointType = assembly.GetType(
            "Atelia.SessionJournal.SessionJournalFailpoint",
            throwOnError: true
        )!;
        Type hooksType = assembly.GetType(
            "Atelia.SessionJournal.SessionJournalTestHooks",
            throwOnError: true
        )!;
        bool legacyStarted = failpointName == "AfterCompletionAttemptStartedCommitted";
        object failpoint = Enum.Parse(failpointType,
            legacyStarted ? "AfterRequestPreparedCommitted" : failpointName);
        ConstructorInfo hooksConstructor = Assert.Single(
            hooksType.GetConstructors(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
            ),
            constructor => constructor.GetParameters() is { Length: > 0 }
                parameters
                && parameters[0].ParameterType == failpointType
        );
        ParameterInfo[] hookParameters = hooksConstructor.GetParameters();
        object?[] hookArguments = new object?[hookParameters.Length];
        hookArguments[0] = failpoint;
        object hooks = hooksConstructor.Invoke(hookArguments);
        MethodInfo openForTest = Assert.Single(
            typeof(SessionJournalEngine).GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic
            ),
            method => {
                if (method.Name != "OpenForTest") { return false; }
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 3
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType
                        == typeof(SessionRuntime)
                    && parameters[2].ParameterType == hooksType;
            }
        );
        EventAddress head;
        {
            using var engine = Assert.IsType<SessionJournalEngine>(
                openForTest.Invoke(null, [sessionPath, runtime, hooks])
            );
            await using IAsyncDisposable? runtimeBinding = bindRuntime is null
                ? null
                : await bindRuntime(engine, runtime);

            Exception exception = await Assert.ThrowsAnyAsync<Exception>(
                () => engine.SendAsync(
                    GalateaUserMessageEnvelope.Wrap("fixture observation"),
                    CancellationToken.None
                )
            );
            Assert.Equal(
                "Atelia.SessionJournal.SessionJournalFailpointException",
                exception.GetType().FullName
            );
            SessionExecutionBoundaryInspection boundary =
                engine.InspectExecutionBoundary();
            Assert.Equal(expectedPhase, boundary.Phase);
            Assert.NotNull(boundary.Head);
            head = boundary.Head!.Value;
        }
        return legacyStarted ? LegacyPreparedV7Fixture.AppendStarted(sessionPath, head) : head;
    }

    internal static async Task<EventAddress> CreateFailedBoundaryAsync(
        string sessionPath,
        CompletionConnectionConfig connection
    ) {
        var client = new KnownFailureClient();
        EventAddress prepared = await CreateRecoveryBoundaryAsync(sessionPath, connection, client,
            "AfterRequestPreparedCommitted", SessionExecutionPhase.AwaitingCompletion);
        Assert.Equal(0, client.DispatchCallCount);
        return LegacyPreparedV7Fixture.AppendFailed(sessionPath, prepared);
    }

    internal static SessionRuntime CreateFixtureRuntime(
        CompletionConnectionConfig connection,
        ICompletionClient client
    ) {
        CompletionDispatchIdentity dispatch =
            CompletionDispatchIdentityFactory.Create(
                connection,
                client
            );
        return new SessionRuntime(
            client,
            CompletionTarget: new SessionCompletionTargetIdentity(
                dispatch.ConnectionId,
                dispatch.Kind,
                dispatch.ConnectionFingerprint
            ),
            ContextCandidateSource: new EmptyLineageCandidateSource(),
            InputProjector: GalateaInputProjector.Instance
        );
    }

    private static async Task<StartTurnResponseDto> ResumeAsync(
        HttpClient client,
        EventAddress expectedHead,
        string? connectionId
    ) {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(expectedHead),
                connectionId
            )
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        return Assert.IsType<StartTurnResponseDto>(started);
    }

    private static async Task<StartTurnResponseDto> StartFreshAsync(
        HttpClient client,
        string message
    ) {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/characters/alice/chat/turns",
            new ChatStreamRequest(message, DiagnosticConnectionId: "test")
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        return Assert.IsType<StartTurnResponseDto>(started);
    }

    private static async Task LoginAsync(HttpClient client) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response
    ) => JsonDocument.Parse(
        await response.Content.ReadAsStringAsync()
    );

    private sealed class EmptyLineageCandidateSource
        : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.EmptyLineage,
                    Candidate: null
                )
            );
        }

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "An EmptyLineage fixture must not materialize a candidate."
        );
    }

    private sealed class TrackingCompletionClientFactory(
        string responseText = "unused"
    ) : ICompletionClientFactory {
        private int _createCallCount;

        internal TrackingCompletionClient Client { get; } = new(
            responseText
        );

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        internal string? LastConnectionId { get; private set; }

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            LastConnectionId = connection.Id;
            Interlocked.Increment(ref _createCallCount);
            return Client;
        }
    }

    private sealed class NoDispatchTransport
        : IGalateaDurableDelegateTransport {
        internal static NoDispatchTransport Instance { get; } = new();

        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request,
            CancellationToken ct
        ) => throw new Xunit.Sdk.XunitException(
            "The recovery test must not create a durable delegate binding."
        );

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request,
            CancellationToken ct
        ) => throw new Xunit.Sdk.XunitException(
            "The recovery test must not dispatch a durable delegate turn."
        );

        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request,
            CancellationToken ct
        ) => throw new Xunit.Sdk.XunitException(
            "The recovery test must not inspect a durable delegate turn."
        );

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingCompletionClient(string responseText)
        : ICompletionClient {
        private int _dispatchCallCount;

        public string Name => "galatea-recovery-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            observer?.OnTextDelta(responseText);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text(responseText)
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }

    private sealed class KnownFailureClient : ICompletionClient {
        private int _dispatchCallCount;

        public string Name => "galatea-known-failure-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(request);
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text("known failed output")
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                ),
                termination: CompletionTermination.Failed(
                    "known-test-failure"
                )
            ));
        }
    }

    private sealed class SingleCompletionClientFactory(
        ICompletionClient client
    ) : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            return client;
        }
    }

    private sealed class SequencedCompletionClient : ICompletionClient {
        private readonly Queue<
            Func<CompletionRequest, CompletionResult>
        > _responses = [];
        private int _dispatchCallCount;

        public string Name => "galatea-rejection-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        internal void Enqueue(
            Func<CompletionRequest, CompletionResult> response
        ) => _responses.Enqueue(response);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(request);
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            if (_responses.Count == 0) {
                throw new InvalidOperationException(
                    "No scripted response remaining."
                );
            }
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class TrackingNormalizer
        : IGalateaUserMessageNormalizer {
        private int _normalizeCallCount;

        internal int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            _ = userMessage;
            return true;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _normalizeCallCount);
            return ValueTask.FromResult(userMessage);
        }
    }

    private sealed class TemporaryCallLogDirectory : IDisposable {
        internal TemporaryCallLogDirectory() {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-galatea-prepared-call-log-tests",
                Guid.NewGuid().ToString("N")
            );
        }

        internal string Path { get; }

        public void Dispose() {
            if (Directory.Exists(Path)) {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
