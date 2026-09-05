using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationRuntimeVerticalTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReadyReplyTurn_IsConditionalAtomicAndBypassesNormalizer() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        const string Reply = "reply delivered by the durable delegate";
        using var automaticCompletionStarted = new ManualResetEventSlim();
        using var releaseAutomaticCompletion = new ManualResetEventSlim();
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent one letter."),
            _ => {
                automaticCompletionStarted.Set();
                Assert.True(releaseAutomaticCompletion.Wait(Deadline));
                return Completed(main, "received the automatic reply");
            }
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-ready-turn", "automatic task")
            ),
            _ => Completed(extractor, "no mail")
        );
        var normalizer = new CountingNormalizer();
        var recallProvider = new BoundedRecallProvider(maximumCalls: 1);
        var clock = new CountingTimeProvider(new DateTimeOffset(
            2026,
            9,
            5,
            1,
            2,
            3,
            987,
            TimeSpan.Zero
        ));
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractor.Id] = extractorClient,
        });
        var backend = new DurableBackend();
        await using GalateaTestHost host = GalateaTestHost.Create(
            factory,
            normalizer,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(backend),
            playerTurnRecallProviderFactory: (_, _) => recallProvider,
            timeProvider: clock
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Atelia.EventJournal.EventAddress? initialHead = session.Engine
            .ReadCurrentHead();

        using (HttpResponseMessage waiting = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
            LoopPulseStatusDto? status = await waiting.Content
                .ReadFromJsonAsync<LoopPulseStatusDto>();
            Assert.NotNull(status);
            Assert.Equal(GalateaBrowserSponsoredAutonomy.WaitingState,
                status!.State);
            Assert.NotNull(status.NextActivationAtUnixTimeMilliseconds);
            Assert.Null(status.LastActivationAtUnixTimeMilliseconds);
            Assert.Null(status.Code);
        }
        Assert.Equal(initialHead, session.Engine.ReadCurrentHead());
        Assert.Null(session.GetCurrentTurn());
        Assert.Null(session.DelegationHandle!.Store.ReadSnapshot().ActiveLease);
        Assert.Equal(0, mainClient.CallCount);
        Assert.Equal(0, extractorClient.CallCount);
        Assert.Equal(0, normalizer.ShouldNormalizeCallCount);
        Assert.Equal(0, normalizer.NormalizeCallCount);

        GalateaLiveTurn sent = await StartAndWaitAsync(
            http,
            service,
            session,
            "send one"
        );
        Assert.Equal("completed", sent.Status);
        Assert.Equal(1, recallProvider.CallCount);
        await WaitUntilAsync(() => backend.StartCallCount == 1);
        backend.Complete(0, Reply);
        _ = session.DelegationHandle.Signal();
        await WaitUntilAsync(() => session.DelegationHandle.Store
            .ReadSnapshot().Notices.SingleOrDefault()?.State
                == GalateaReplyNoticeState.Ready);
        GalateaBrowserSponsoredAutonomyPulseResult due =
            AdvanceCadenceToDue(session, clock);
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult
                .AutonomousActivationDue,
            due
        );
        int shouldNormalizeBeforeReady = normalizer.ShouldNormalizeCallCount;
        int normalizeBeforeReady = normalizer.NormalizeCallCount;
        int timeCallsBeforeReady = clock.GetUtcNowCallCount;

        using (HttpResponseMessage unknown = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest("unknown"))) {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }
        GalateaDelegationStateSnapshot afterUnknown = session.DelegationHandle
            .Store.ReadSnapshot();
        Assert.Null(afterUnknown.ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Ready,
            Assert.Single(afterUnknown.Notices).State
        );

        LoopPulseAcceptedTurnDto accepted;
        using (HttpResponseMessage response = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            accepted = Assert.IsType<LoopPulseAcceptedTurnDto>(
                await response.Content.ReadFromJsonAsync<
                    LoopPulseAcceptedTurnDto>()
            );
            Assert.Equal("delegate-reply", accepted.Origin);
        }
        GalateaLiveTurn received = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, accepted.TurnId)
        );
        Assert.True(automaticCompletionStarted.Wait(Deadline));
        GalateaDelegationStateSnapshot leased = session.DelegationHandle
            .Store.ReadSnapshot();
        Assert.NotNull(leased.ActiveLease);
        Assert.Equal(
            PlayerTurnObservationEnvelope
                .DelegateReplyLeasePlayerTextDiscriminator,
            leased.ActiveLease.PlayerText
        );
        Assert.IsType<GalateaFreshInput.DelegateReply>(received.FreshInput);
        Assert.Equal(
            GalateaReplyNoticeState.Leased,
            Assert.Single(leased.Notices).State
        );
        try {
            using HttpResponseMessage busy = await http.PostAsJsonAsync(
                "/api/v1/mailbox/ready-turn",
                new ReadyReplyTurnRequest(main.Id)
            );
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            using JsonDocument body = JsonDocument.Parse(
                await busy.Content.ReadAsStringAsync()
            );
            Assert.Equal(
                "turn-busy",
                body.RootElement.GetProperty("code").GetString()
            );
        }
        finally {
            releaseAutomaticCompletion.Set();
        }
        await Assert.IsAssignableFrom<Task>(received.RunTask)
            .WaitAsync(Deadline);
        Assert.Equal("completed", received.Status);
        Assert.Equal(2, mainClient.CallCount);
        Assert.Equal(
            shouldNormalizeBeforeReady,
            normalizer.ShouldNormalizeCallCount
        );
        Assert.Equal(normalizeBeforeReady, normalizer.NormalizeCallCount);
        Assert.Equal(1, recallProvider.CallCount);
        Assert.Equal(
            timeCallsBeforeReady + 1,
            clock.GetUtcNowCallCount
        );

        SessionCompletedTurnProjection completed = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            completed.ObservationContent,
            out PlayerTurnObservation observation
        ));
        Assert.Equal(
            PlayerTurnObservationTriggerKind.DelegateReply,
            observation.TriggerKind
        );
        Assert.DoesNotContain(
            "player-action",
            completed.ObservationContent,
            StringComparison.Ordinal
        );
        Assert.NotNull(observation.ExternalLocalTimestamp);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                9,
                5,
                1,
                2,
                3,
                TimeSpan.Zero
            ),
            observation.ExternalLocalTimestamp
        );
        Assert.Equal(
            [Reply],
            observation.Notices.Select(static notice => notice.Body)
                .ToArray()
        );
        GalateaDelegationStateSnapshot consumed = session.DelegationHandle
            .Store.ReadSnapshot();
        Assert.Null(consumed.ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Consumed,
            Assert.Single(consumed.Notices).State
        );
        Atelia.EventJournal.EventAddress? completedHead = session.Engine
            .ReadCurrentHead();
        int extractorCallsAfterCompleted = extractorClient.CallCount;

        using (HttpResponseMessage waiting = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
            LoopPulseStatusDto? status = await waiting.Content
                .ReadFromJsonAsync<LoopPulseStatusDto>();
            Assert.NotNull(status);
            Assert.Equal(GalateaBrowserSponsoredAutonomy.WaitingState,
                status!.State);
            Assert.NotNull(status.NextActivationAtUnixTimeMilliseconds);
        }
        Assert.Equal(completedHead, session.Engine.ReadCurrentHead());
        Assert.Equal(2, mainClient.CallCount);
        Assert.Equal(extractorCallsAfterCompleted, extractorClient.CallCount);
        Assert.Null(session.DelegationHandle.Store.ReadSnapshot().ActiveLease);
    }

    [Fact]
    public async Task HeartbeatActivation_SamplesOnceAndBypassesRecall() {
        CompletionConnectionConfig main = Connection("test");
        var mainClient = new QueueClient(
            _ => Completed(main, "character chose to rest")
        );
        var recallProvider = new BoundedRecallProvider(maximumCalls: 0);
        var clock = new CountingTimeProvider(new DateTimeOffset(
            2026,
            9,
            5,
            4,
            5,
            6,
            987,
            TimeSpan.Zero
        ));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = mainClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main],
            selectableConnectionIds: [main.Id],
            playerTurnRecallProviderFactory: (_, _) => recallProvider,
            timeProvider: clock
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        int callsBefore = clock.GetUtcNowCallCount;
        GalateaLiveTurn turn = session.StartTurn(
            new GalateaFreshInput.HeartbeatActivation(
                new GalateaCharacterName("Alice")
            ),
            new GalateaTurnOptions(main.Id)
        );
        try {
            await service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
        }
        finally {
            // This typed-input vertical deliberately bypasses heartbeat
            // admission and therefore owns no cadence claim to settle.
            session.FinishTurn(turn);
        }

        Assert.Equal("completed", turn.Status);
        Assert.Equal(1, mainClient.CallCount);
        Assert.Equal(0, recallProvider.CallCount);
        Assert.Equal(callsBefore + 1, clock.GetUtcNowCallCount);
        SessionCompletedTurnProjection completed = Assert.Single(
            session.Engine.ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns
        );
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            completed.ObservationContent,
            out PlayerTurnObservation observation
        ));
        Assert.Equal(
            PlayerTurnObservationTriggerKind.HeartbeatActivation,
            observation.TriggerKind
        );
        Assert.Equal("Alice", observation.HeartbeatCharacterName.Value);
        Assert.Contains(
            "此刻，Alice拥有一段由自己支配的时间",
            completed.ObservationContent,
            StringComparison.Ordinal
        );
        Assert.Equal(
            new DateTimeOffset(
                2026,
                9,
                5,
                4,
                5,
                6,
                TimeSpan.Zero
            ),
            observation.ExternalLocalTimestamp
        );
    }

    [Fact]
    public async Task HeartbeatAdmissionRollbackRestoresExactClaimState() {
        CompletionConnectionConfig main = Connection("test");
        var mainClient = new QueueClient(
            _ => Completed(main, "must not run")
        );
        var clock = new CountingTimeProvider(new DateTimeOffset(
            2026,
            9,
            5,
            4,
            30,
            0,
            TimeSpan.Zero
        ));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                [main.Id] = mainClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main],
            selectableConnectionIds: [main.Id],
            timeProvider: clock
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        session.TurnLock.Wait();
        try {
            Assert.Equal(
                GalateaBrowserSponsoredAutonomyPulseResult.Rearmed,
                session.BrowserSponsoredAutonomy.ObserveSponsorPulse()
            );
        }
        finally {
            session.TurnLock.Release();
        }
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult
                .AutonomousActivationDue,
            AdvanceCadenceToDue(session, clock)
        );

        session.TurnLock.Wait();
        try {
            GalateaLiveTurn first = service.StartHeartbeatActivationTurn(
                session,
                new GalateaTurnOptions(main.Id)
            );
            Assert.Same(first, session.GetCurrentTurn());
            GalateaBrowserSponsoredAutonomyStatus claimed = session
                .BrowserSponsoredAutonomy.ProjectStatus();
            Assert.Null(claimed.NextActivationAtUnixTimeMilliseconds);
            Assert.NotNull(claimed.LastActivationAtUnixTimeMilliseconds);

            service.RollbackHeartbeatActivationAdmission(session, first);
            service.FinishTurn(session, first);

            GalateaBrowserSponsoredAutonomyStatus restored = session
                .BrowserSponsoredAutonomy.ProjectStatus();
            Assert.Equal(GalateaBrowserSponsoredAutonomy.WaitingState,
                restored.State);
            Assert.Equal(
                clock.GetUtcNow().ToUnixTimeMilliseconds(),
                restored.NextActivationAtUnixTimeMilliseconds
            );
            Assert.Null(restored.LastActivationAtUnixTimeMilliseconds);
            Assert.Null(restored.Code);
            Assert.Null(session.GetCurrentTurn());
            Assert.Equal(
                GalateaBrowserSponsoredAutonomyPulseResult
                    .AutonomousActivationDue,
                session.BrowserSponsoredAutonomy.ObserveSponsorPulse()
            );

            GalateaLiveTurn second = service.StartHeartbeatActivationTurn(
                session,
                new GalateaTurnOptions(main.Id)
            );
            service.RollbackHeartbeatActivationAdmission(session, second);
            service.FinishTurn(session, second);
            Assert.Equal(GalateaBrowserSponsoredAutonomy.WaitingState,
                session.BrowserSponsoredAutonomy.ProjectStatus().State);
            Assert.Null(session.GetCurrentTurn());
        }
        finally {
            session.TurnLock.Release();
        }
        Assert.Equal(0, mainClient.CallCount);
    }

    [Fact]
    public async Task AbortedHeartbeatTurnPausesAndReleasesTurnLock() {
        CompletionConnectionConfig main = Connection("test");
        var clock = new CountingTimeProvider(new DateTimeOffset(
            2026,
            9,
            5,
            4,
            45,
            0,
            TimeSpan.Zero
        ));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                [main.Id] = new QueueClient(
                    _ => Completed(main, "must not run")
                ),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main],
            selectableConnectionIds: [main.Id],
            timeProvider: clock
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        session.TurnLock.Wait();
        try {
            _ = session.BrowserSponsoredAutonomy.ObserveSponsorPulse();
        }
        finally {
            session.TurnLock.Release();
        }
        _ = AdvanceCadenceToDue(session, clock);

        session.TurnLock.Wait();
        try {
            GalateaLiveTurn turn = service.StartHeartbeatActivationTurn(
                session,
                new GalateaTurnOptions(main.Id)
            );
            turn.AbortTransportWithoutTerminal();
            service.FinishTurn(session, turn);
        }
        finally {
            session.TurnLock.Release();
        }

        Assert.True(session.TurnLock.Wait(0));
        try {
            GalateaBrowserSponsoredAutonomyStatus paused = session
                .BrowserSponsoredAutonomy.ProjectStatus();
            Assert.Equal(GalateaBrowserSponsoredAutonomy.PausedState,
                paused.State);
            Assert.Equal(GalateaBrowserSponsoredAutonomy.PausedCode,
                paused.Code);
            Assert.Null(paused.NextActivationAtUnixTimeMilliseconds);
            Assert.Null(session.GetCurrentTurn());
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task HeartbeatPulse_ClaimsOnceBusyThenCompletedTurnResets() {
        CompletionConnectionConfig main = Connection("test");
        using var completionStarted = new ManualResetEventSlim();
        using var releaseCompletion = new ManualResetEventSlim();
        var mainClient = new QueueClient(_ => {
            completionStarted.Set();
            Assert.True(releaseCompletion.Wait(Deadline));
            return Completed(main, "autonomous activity completed");
        });
        var clock = new CountingTimeProvider(new DateTimeOffset(
            2026,
            9,
            5,
            5,
            0,
            0,
            TimeSpan.Zero
        ));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                [main.Id] = mainClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main],
            selectableConnectionIds: [main.Id],
            timeProvider: clock
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        LoopPulseStatusDto first = await PostWaitingPulseAsync(http);
        long expectedInitialDue = (clock.GetUtcNow()
            + GalateaBrowserSponsoredAutonomy.IdleInterval)
            .ToUnixTimeMilliseconds();
        Assert.Equal(expectedInitialDue,
            first.NextActivationAtUnixTimeMilliseconds);
        for (int pulse = 1; pulse < 60; pulse++) {
            clock.Advance(TimeSpan.FromSeconds(10));
            LoopPulseStatusDto waiting = await PostWaitingPulseAsync(http);
            Assert.Equal(expectedInitialDue,
                waiting.NextActivationAtUnixTimeMilliseconds);
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        LoopPulseAcceptedTurnDto accepted;
        using (HttpResponseMessage response = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            accepted = Assert.IsType<LoopPulseAcceptedTurnDto>(
                await response.Content
                    .ReadFromJsonAsync<LoopPulseAcceptedTurnDto>()
            );
        }
        Assert.Equal("heartbeat-activation", accepted.Origin);
        GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, accepted.TurnId)
        );
        Assert.True(completionStarted.Wait(Deadline));
        Assert.IsType<GalateaFreshInput.HeartbeatActivation>(turn.FreshInput);
        using (HttpResponseMessage busy = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        }

        releaseCompletion.Set();
        await Assert.IsAssignableFrom<Task>(turn.RunTask).WaitAsync(Deadline);
        Assert.Equal("completed", turn.Status);
        Assert.Equal(1, mainClient.CallCount);

        LoopPulseStatusDto reset = await PostWaitingPulseAsync(http);
        Assert.Equal(
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            reset.LastActivationAtUnixTimeMilliseconds
        );
        Assert.Equal(
            (clock.GetUtcNow()
                + GalateaBrowserSponsoredAutonomy.IdleInterval)
                .ToUnixTimeMilliseconds(),
            reset.NextActivationAtUnixTimeMilliseconds
        );
    }

    [Fact]
    public async Task HeartbeatPulse_RearmsOnlyAfterSponsorGap() {
        CompletionConnectionConfig main = Connection("test");
        var mainClient = new QueueClient(
            _ => Completed(main, "must not run")
        );
        var clock = new CountingTimeProvider(new DateTimeOffset(
            2026,
            9,
            5,
            5,
            30,
            0,
            TimeSpan.Zero
        ));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                [main.Id] = mainClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main],
            selectableConnectionIds: [main.Id],
            timeProvider: clock
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        LoopPulseStatusDto first = await PostWaitingPulseAsync(http);
        long firstDue = first.NextActivationAtUnixTimeMilliseconds!.Value;

        clock.Advance(
            GalateaBrowserSponsoredAutonomy.SponsorContinuityGap
        );
        LoopPulseStatusDto exactGap = await PostWaitingPulseAsync(http);

        Assert.Equal(
            firstDue,
            exactGap.NextActivationAtUnixTimeMilliseconds
        );

        clock.Advance(
            GalateaBrowserSponsoredAutonomy.SponsorContinuityGap
                + TimeSpan.FromTicks(1)
        );
        LoopPulseStatusDto rearmed = await PostWaitingPulseAsync(http);

        Assert.True(
            rearmed.NextActivationAtUnixTimeMilliseconds > firstDue
        );
        Assert.Equal(
            (clock.GetUtcNow()
                + GalateaBrowserSponsoredAutonomy.IdleInterval)
                .ToUnixTimeMilliseconds(),
            rearmed.NextActivationAtUnixTimeMilliseconds
        );
        Assert.Equal(0, mainClient.CallCount);
    }

    [Fact]
    public async Task FailedAutonomyPausesButReadyReplyStillWinsAndClearsPause() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent one letter."),
            _ => Failed(main, "simulated autonomous completion failure"),
            _ => Completed(main, "received reply after autonomy pause")
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-paused-reply", "reply after pause")
            ),
            _ => Completed(extractor, "no mail")
        );
        var clock = new CountingTimeProvider(new DateTimeOffset(
            2026,
            9,
            5,
            6,
            0,
            0,
            TimeSpan.Zero
        ));
        var backend = new DurableBackend();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                [main.Id] = mainClient,
                [extractor.Id] = extractorClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(backend),
            timeProvider: clock
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        _ = await PostWaitingPulseAsync(http);
        GalateaLiveTurn manual = await StartAndWaitAsync(
            http,
            service,
            session,
            "send before autonomy failure"
        );
        Assert.Equal("completed", manual.Status);
        await WaitUntilAsync(() => backend.StartCallCount == 1);

        LoopPulseAcceptedTurnDto autonomy =
            await AdvanceHttpCadenceToAcceptedAsync(http, clock);
        Assert.Equal("heartbeat-activation", autonomy.Origin);
        GalateaLiveTurn failed = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, autonomy.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(failed.RunTask)
            .WaitAsync(Deadline);
        Assert.Equal("failed", failed.Status);

        LoopPulseStatusDto paused = await PostPulseStatusAsync(http);
        Assert.Equal(GalateaBrowserSponsoredAutonomy.PausedState,
            paused.State);
        Assert.Null(paused.NextActivationAtUnixTimeMilliseconds);
        Assert.Equal(GalateaBrowserSponsoredAutonomy.PausedCode,
            paused.Code);
        Assert.Equal(2, mainClient.CallCount);

        backend.Complete(0, "durable reply after pause");
        _ = session.DelegationHandle!.Signal();
        await WaitUntilAsync(() => session.DelegationHandle.Store
            .ReadSnapshot().Notices.SingleOrDefault()?.State
                == GalateaReplyNoticeState.Ready);
        LoopPulseAcceptedTurnDto reply;
        using (HttpResponseMessage response = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            reply = Assert.IsType<LoopPulseAcceptedTurnDto>(
                await response.Content
                    .ReadFromJsonAsync<LoopPulseAcceptedTurnDto>()
            );
        }
        Assert.Equal("delegate-reply", reply.Origin);
        GalateaLiveTurn received = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, reply.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(received.RunTask)
            .WaitAsync(Deadline);
        Assert.Equal("completed", received.Status);

        LoopPulseStatusDto resumed = await PostWaitingPulseAsync(http);
        Assert.Null(resumed.Code);
        Assert.NotNull(resumed.NextActivationAtUnixTimeMilliseconds);
        Assert.Equal(3, mainClient.CallCount);
    }

    [Fact]
    public async Task RestartedSessionFirstPulseLateRearms() {
        CompletionConnectionConfig main = Connection("test");
        var clock = new CountingTimeProvider(new DateTimeOffset(
            2026,
            9,
            5,
            7,
            0,
            0,
            TimeSpan.Zero
        ));
        var backend = new DurableBackend();
        var mainClient = new QueueClient(
            _ => Completed(main, "must not run")
        );
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        > {
            [main.Id] = mainClient,
        });
        GalateaTestHost first = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: false,
            connections: [main],
            delegateTransport: new DurableTransport(backend),
            timeProvider: clock
        );
        GalateaTestHost? restarted = null;
        try {
            using (HttpClient firstHttp = first.CreateClient()) {
                await LoginAsync(firstHttp);
                _ = await PostWaitingPulseAsync(firstHttp);
            }
            clock.Advance(TimeSpan.FromHours(1));
            await first.DisposeAsync();
            restarted = first.CreateRestarted(
                factory,
                DisabledGalateaUserMessageNormalizer.Instance,
                new DurableTransport(backend)
            );
            using HttpClient restartedHttp = restarted.CreateClient();
            await LoginAsync(restartedHttp);

            LoopPulseStatusDto status = await PostWaitingPulseAsync(
                restartedHttp
            );

            Assert.Equal(
                (clock.GetUtcNow()
                    + GalateaBrowserSponsoredAutonomy.IdleInterval)
                    .ToUnixTimeMilliseconds(),
                status.NextActivationAtUnixTimeMilliseconds
            );
            Assert.Null(status.LastActivationAtUnixTimeMilliseconds);
            Assert.Equal(0, mainClient.CallCount);
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
    public async Task DurableRoundTrip_UsesOneFixedThreadAndUndoDoesNotRearm() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        const string ReplyOne = "first reply ```nested```";
        const string ReplyTwo = "second reply <tag>值</tag>";
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent two letters."),
            _ => Completed(main, "received both replies"),
            _ => Completed(main, "after undo")
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-1", "first task"),
                MailTool("mail-2", "second task")
            ),
            _ => Completed(extractor, "no mail"),
            _ => Completed(extractor, "no mail")
        );
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractor.Id] = extractorClient,
        });
        var backend = new DurableBackend();
        await using GalateaTestHost host = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(backend)
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        GalateaLiveTurn sent = await StartAndWaitAsync(
            http,
            service,
            session,
            "send them"
        );
        Assert.Equal("completed", sent.Status);
        await WaitUntilAsync(() => backend.StartCallCount == 1);
        backend.Complete(0, ReplyOne);
        _ = session.DelegationHandle!.Signal();
        await WaitUntilAsync(() => backend.StartCallCount == 2);
        backend.Complete(1, ReplyTwo);
        _ = session.DelegationHandle.Signal();
        await WaitUntilAsync(() => session.DelegationHandle.Store
            .ReadSnapshot().Notices.Count == 2);

        Assert.Equal(1, backend.EnsureCallCount);
        Assert.Equal(
            ["thread-fixed", "thread-fixed"],
            backend.StartRequests.Select(static request => request.ThreadId)
                .ToArray()
        );

        GalateaLiveTurn received = await StartAndWaitAsync(
            http,
            service,
            session,
            "player text"
        );
        Assert.Equal("completed", received.Status);
        SessionCompletedTurnProjection receiving = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            receiving.ObservationContent,
            out PlayerTurnObservation composite
        ));
        Assert.Equal("player text", composite.PlayerText);
        Assert.NotNull(composite.ExternalLocalTimestamp);
        Assert.Equal(
            [ReplyOne, ReplyTwo],
            composite.Notices.Select(static notice => notice.Body)
                .ToArray()
        );
        GalateaDelegationStateSnapshot consumed = session.DelegationHandle
            .Store.ReadSnapshot();
        Assert.All(consumed.Notices, static notice => {
            Assert.Equal(GalateaReplyNoticeState.Consumed, notice.State);
            Assert.NotNull(notice.ConsumedActionAddress);
        });

        RecentTurnsResponseDto recent = (await http.GetFromJsonAsync<
            RecentTurnsResponseDto>("/api/v1/recent-turns"))!;
        using HttpResponseMessage undo = await http.PostAsJsonAsync(
            "/api/v1/chat/turns/pop-latest",
            new { rewindLatestToken = recent.RewindLatestToken }
        );
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.All(
            session.DelegationHandle.Store.ReadSnapshot().Notices,
            static notice => Assert.Equal(
                GalateaReplyNoticeState.Consumed,
                notice.State
            )
        );

        _ = await StartAndWaitAsync(
            http,
            service,
            session,
            "after undo"
        );
        SessionCompletedTurnProjection afterUndo = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            afterUndo.ObservationContent,
            out PlayerTurnObservation afterUndoComposite
        ));
        Assert.Empty(afterUndoComposite.Notices);
        Assert.NotNull(afterUndoComposite.ExternalLocalTimestamp);
        Assert.Equal(2, backend.StartCallCount);
    }

    [Fact]
    public async Task ManualDiscriminatorCollisionLeavesReplyReadyForTypedPulse() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent one letter."),
            _ => Completed(main, "manual marker accepted"),
            _ => Completed(main, "typed reply accepted")
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-marker-collision", "automatic task")
            ),
            _ => Completed(extractor, "no mail"),
            _ => Completed(extractor, "no mail")
        );
        var backend = new DurableBackend();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = mainClient,
                [extractor.Id] = extractorClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(backend)
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        _ = await StartAndWaitAsync(
            http,
            service,
            session,
            "send one"
        );
        await WaitUntilAsync(() => backend.StartCallCount == 1);
        backend.Complete(0, "reply");
        _ = session.DelegationHandle!.Signal();
        await WaitUntilAsync(() => session.DelegationHandle.Store
            .ReadSnapshot().Notices.SingleOrDefault()?.State
                == GalateaReplyNoticeState.Ready);

        string marker = PlayerTurnObservationEnvelope
            .DelegateReplyLeasePlayerTextDiscriminator;
        GalateaLiveTurn collision = await StartAndWaitAsync(
            http,
            service,
            session,
            marker
        );
        Assert.Equal(
            marker,
            Assert.IsType<GalateaFreshInput.PlayerAction>(
                collision.FreshInput
            ).Text
        );
        Assert.Null(collision.DurableReplyLease);
        Assert.Equal("completed", collision.Status);
        Assert.Equal(2, mainClient.CallCount);

        SessionCompletedTurnProjection manual = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            manual.ObservationContent,
            out PlayerTurnObservation manualObservation
        ));
        Assert.Equal(
            PlayerTurnObservationTriggerKind.PlayerAction,
            manualObservation.TriggerKind
        );
        Assert.Equal(marker, manualObservation.PlayerText);
        Assert.Empty(manualObservation.Notices);
        RecentTurnsResponseDto manualRecent = await service
            .GetRecentTurnsAsync(session, CancellationToken.None);
        Assert.NotNull(manualRecent.RewindLatestToken);
        GalateaDelegationStateSnapshot stillReady = session.DelegationHandle
            .Store.ReadSnapshot();
        Assert.Null(stillReady.ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Ready,
            Assert.Single(stillReady.Notices).State
        );

        LoopPulseAcceptedTurnDto accepted;
        using (HttpResponseMessage response = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            accepted = Assert.IsType<LoopPulseAcceptedTurnDto>(
                await response.Content
                    .ReadFromJsonAsync<LoopPulseAcceptedTurnDto>()
            );
        }
        Assert.Equal("delegate-reply", accepted.Origin);
        GalateaLiveTurn replyTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, accepted.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(replyTurn.RunTask)
            .WaitAsync(Deadline);

        SessionCompletedTurnProjection automatic = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            automatic.ObservationContent,
            out PlayerTurnObservation automaticObservation
        ));
        Assert.Equal(
            PlayerTurnObservationTriggerKind.DelegateReply,
            automaticObservation.TriggerKind
        );
        Assert.DoesNotContain("player-action", automatic.ObservationContent,
            StringComparison.Ordinal);
        Assert.Equal(
            GalateaReplyNoticeState.Consumed,
            Assert.Single(session.DelegationHandle.Store.ReadSnapshot()
                .Notices).State
        );
        Assert.Equal(3, mainClient.CallCount);
    }

    [Fact]
    public async Task AcceptedMail_RestartInspectsWithoutSecondStart() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent one letter."),
            _ => Completed(main, "received restart reply")
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-restart", "restart task")
            ),
            _ => Completed(extractor, "no mail")
        );
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractor.Id] = extractorClient,
        });
        var backend = new DurableBackend();
        var first = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: false,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(backend)
        );
        GalateaTestHost? restarted = null;
        try {
            using (HttpClient http = first.CreateClient()) {
                await LoginAsync(http);
                GalateaHostService service = first.Factory.Services
                    .GetRequiredService<GalateaHostService>();
                UserSessionHost session = await service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                );
                _ = await StartAndWaitAsync(
                    http,
                    service,
                    session,
                    "send before restart"
                );
                await WaitUntilAsync(() => session.DelegationHandle!.Store
                    .ReadSnapshot().Mails.Single().State
                    == GalateaDurableMailState.Accepted);
            }
            Assert.Equal(1, backend.StartCallCount);
            await first.DisposeAsync();
            backend.Complete(0, "reply recovered after restart");

            restarted = first.CreateRestarted(
                factory,
                DisabledGalateaUserMessageNormalizer.Instance,
                new DurableTransport(backend)
            );
            using HttpClient restartedHttp = restarted.CreateClient();
            await LoginAsync(restartedHttp);
            GalateaHostService restartedService = restarted.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost restartedSession =
                await restartedService.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            await WaitUntilAsync(() => restartedSession.DelegationHandle!.Store
                .ReadSnapshot().Notices.SingleOrDefault()?.State
                == GalateaReplyNoticeState.Ready);

            _ = await StartAndWaitAsync(
                restartedHttp,
                restartedService,
                restartedSession,
                "receive after restart"
            );
            Assert.Equal(1, backend.StartCallCount);
            Assert.True(backend.InspectCallCount > 0);
            SessionCompletedTurnProjection receiving = restartedSession.Engine
                .ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns.Single();
            Assert.Contains(
                "reply recovered after restart",
                receiving.ObservationContent,
                StringComparison.Ordinal
            );
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
    public async Task AcceptedMail_GracefulRestartConsumesInterruptedFailure() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent one letter."),
            _ => Completed(main, "received interruption notice")
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-interrupted", "interrupted task")
            ),
            _ => Completed(extractor, "no mail")
        );
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractor.Id] = extractorClient,
        });
        var backend = new DurableBackend();
        var first = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: false,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(
                backend,
                interruptRunningOnDispose: true
            )
        );
        GalateaTestHost? restarted = null;
        try {
            using (HttpClient http = first.CreateClient()) {
                await LoginAsync(http);
                GalateaHostService service = first.Factory.Services
                    .GetRequiredService<GalateaHostService>();
                UserSessionHost session = await service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                );
                _ = await StartAndWaitAsync(
                    http,
                    service,
                    session,
                    "send before interruption"
                );
                await WaitUntilAsync(() => session.DelegationHandle!.Store
                    .ReadSnapshot().Mails.Single().State
                    == GalateaDurableMailState.Accepted);
            }
            Assert.Equal(1, backend.StartCallCount);
            await first.DisposeAsync();

            restarted = first.CreateRestarted(
                factory,
                DisabledGalateaUserMessageNormalizer.Instance,
                new DurableTransport(backend)
            );
            using HttpClient restartedHttp = restarted.CreateClient();
            await LoginAsync(restartedHttp);
            GalateaHostService restartedService = restarted.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost restartedSession =
                await restartedService.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                );
            await WaitUntilAsync(() => restartedSession.DelegationHandle!.Store
                .ReadSnapshot().Notices.SingleOrDefault()?.State
                == GalateaReplyNoticeState.Ready);

            GalateaReplyNoticeSnapshot ready = restartedSession
                .DelegationHandle!.Store.ReadSnapshot().Notices.Single();
            Assert.Equal(GalateaReplyNoticeKind.DeliveryFailure, ready.Kind);
            Assert.Equal("inspect-dispatch", ready.Stage);
            Assert.Equal("TURN_INTERRUPTED", ready.Code);
            Assert.Equal(1, backend.StartCallCount);

            _ = await StartAndWaitAsync(
                restartedHttp,
                restartedService,
                restartedSession,
                "receive interruption notice"
            );
            SessionCompletedTurnProjection receiving = restartedSession.Engine
                .ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns.Single();
            Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
                receiving.ObservationContent,
                out PlayerTurnObservation observation
            ));
            Assert.Contains(
                ready.Body,
                observation.Notices.Select(static notice => notice.Body)
            );
            Assert.Equal(
                GalateaReplyNoticeState.Consumed,
                restartedSession.DelegationHandle.Store.ReadSnapshot()
                    .Notices.Single().State
            );
            Assert.Equal(1, backend.StartCallCount);
            Assert.True(backend.InspectCallCount > 0);
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
    public async Task MaintenanceHost_PerformsNoDurableTransportCall() {
        CompletionConnectionConfig main = Connection("test");
        var backend = new DurableBackend();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                [main.Id] = new QueueClient(
                    _ => Completed(main, "unused")
                )
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            maintenanceMode: true,
            connections: [main],
            delegateTransport: new DurableTransport(backend)
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);

        using HttpResponseMessage response = await http.GetAsync(
            "/api/v1/recent-turns"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, backend.TotalCallCount);
    }

    private static GalateaBrowserSponsoredAutonomyPulseResult
        AdvanceCadenceToDue(
        UserSessionHost session,
        CountingTimeProvider clock
    ) {
        GalateaBrowserSponsoredAutonomyPulseResult result = default;
        for (int pulse = 0; pulse < 60; pulse++) {
            clock.AdvanceMonotonic(TimeSpan.FromSeconds(10));
            session.TurnLock.Wait();
            try {
                result = session.BrowserSponsoredAutonomy
                    .ObserveSponsorPulse();
            }
            finally {
                session.TurnLock.Release();
            }
            if (pulse < 59) {
                Assert.Equal(
                    GalateaBrowserSponsoredAutonomyPulseResult.Waiting,
                    result
                );
            }
        }
        return result;
    }

    private static async Task<LoopPulseAcceptedTurnDto>
        AdvanceHttpCadenceToAcceptedAsync(
        HttpClient http,
        CountingTimeProvider clock
    ) {
        for (int pulse = 1; pulse < 60; pulse++) {
            clock.Advance(TimeSpan.FromSeconds(10));
            _ = await PostWaitingPulseAsync(http);
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        using HttpResponseMessage accepted = await http.PostAsJsonAsync(
            "/api/v1/mailbox/ready-turn",
            new ReadyReplyTurnRequest("test")
        );
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        return Assert.IsType<LoopPulseAcceptedTurnDto>(
            await accepted.Content
                .ReadFromJsonAsync<LoopPulseAcceptedTurnDto>()
        );
    }

    private static async Task<LoopPulseStatusDto> PostWaitingPulseAsync(
        HttpClient http
    ) {
        LoopPulseStatusDto status = await PostPulseStatusAsync(http);
        Assert.Equal(GalateaBrowserSponsoredAutonomy.WaitingState,
            status.State);
        Assert.NotNull(status.NextActivationAtUnixTimeMilliseconds);
        Assert.Null(status.Code);
        return status;
    }

    private static async Task<LoopPulseStatusDto> PostPulseStatusAsync(
        HttpClient http
    ) {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/mailbox/ready-turn",
            new ReadyReplyTurnRequest("test")
        );
        if (response.StatusCode != HttpStatusCode.OK) {
            Assert.Fail(
                $"Expected pulse status, got {(int)response.StatusCode}: "
                    + await response.Content.ReadAsStringAsync()
            );
        }
        return Assert.IsType<LoopPulseStatusDto>(
            await response.Content.ReadFromJsonAsync<LoopPulseStatusDto>()
        );
    }

    private static async Task<GalateaLiveTurn> StartAndWaitAsync(
        HttpClient http,
        GalateaHostService service,
        UserSessionHost session,
        string message
    ) {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/chat/turns",
            new ChatStreamRequest(message, "test")
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto started = Assert.IsType<StartTurnResponseDto>(
            await response.Content.ReadFromJsonAsync<StartTurnResponseDto>()
        );
        GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(turn.RunTask)
            .WaitAsync(Deadline);
        return turn;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate) {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;
        while (!predicate()) {
            if (DateTimeOffset.UtcNow >= deadline) {
                throw new TimeoutException("Durable vertical condition timed out.");
            }
            await Task.Delay(25);
        }
    }

    private static async Task LoginAsync(HttpClient http) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id,
        "openai-chat",
        id + "-model",
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static CompletionResult Completed(
        CompletionConnectionConfig connection,
        string text
    ) => new(
        new ActionMessage([new ActionBlock.Text(text)]),
        new CompletionDescriptor(
            "delegation-runtime-test",
            "test-v1",
            connection.ModelId
        )
    );

    private static CompletionResult Failed(
        CompletionConnectionConfig connection,
        string reason
    ) => new(
        new ActionMessage([new ActionBlock.Text("known failed output")]),
        new CompletionDescriptor(
            "delegation-runtime-test",
            "test-v1",
            connection.ModelId
        ),
        termination: CompletionTermination.Failed(reason)
    );

    private static CompletionResult CompletedWithTools(
        CompletionConnectionConfig connection,
        params ActionBlock.ToolCall[] tools
    ) => new(
        new ActionMessage(tools),
        new CompletionDescriptor(
            "delegation-runtime-test",
            "test-v1",
            connection.ModelId
        )
    );

    private static ActionBlock.ToolCall MailTool(string id, string body) =>
        new(new RawToolCall(
            OutboundMailExtractor.ToolName,
            id,
            JsonSerializer.Serialize(new {
                recipient = "Codex",
                subject = (string?)null,
                body,
                inReplyToMessageId = (string?)null,
                evidenceQuote = "sent",
            }, new JsonSerializerOptions {
                DefaultIgnoreCondition = System.Text.Json.Serialization
                    .JsonIgnoreCondition.WhenWritingNull
            })
        ));

    private sealed class RoutingFactory(
        IReadOnlyDictionary<string, ICompletionClient> clients
    ) : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => clients[connection.Id];
    }

    private sealed class QueueClient(
        params Func<CompletionRequest, CompletionResult>[] scripts
    ) : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>>
            _scripts = new(scripts);
        private int _callCount;

        public string Name => "delegation-runtime-test";
        public string ApiSpecId => "test-v1";
        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            CompletionResult result = _scripts.Dequeue()(request);
            foreach (ActionBlock.Text text in result.Message.Blocks
                         .OfType<ActionBlock.Text>()) {
                observer?.OnTextDelta(text.Content);
            }
            return Task.FromResult(result);
        }
    }

    private sealed class CountingNormalizer : IGalateaUserMessageNormalizer {
        private int _shouldNormalizeCallCount;
        private int _normalizeCallCount;

        internal int ShouldNormalizeCallCount => Volatile.Read(
            ref _shouldNormalizeCallCount
        );
        internal int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            Interlocked.Increment(ref _shouldNormalizeCallCount);
            return true;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _normalizeCallCount);
            return ValueTask.FromResult("normalized: " + userMessage);
        }
    }

    private sealed class BoundedRecallProvider(int maximumCalls)
        : IGalateaPlayerTurnRecallProvider {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<IReadOnlyList<PlayerTurnRecall>> SelectRecallsAsync(
            GalateaPlayerTurnRecallRequest request,
            CancellationToken cancellationToken
        ) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            if (call > maximumCalls) {
                throw new InvalidOperationException(
                    "Automatic triggers must bypass player recall."
                );
            }
            return ValueTask.FromResult<IReadOnlyList<PlayerTurnRecall>>([]);
        }
    }

    private sealed class CountingTimeProvider(DateTimeOffset utcNow)
        : TimeProvider {
        private int _getUtcNowCallCount;
        private long _timestamp;
        private DateTimeOffset _utcNow = utcNow;
        internal int GetUtcNowCallCount => Volatile.Read(
            ref _getUtcNowCallCount
        );
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() {
            Interlocked.Increment(ref _getUtcNowCallCount);
            return _utcNow;
        }

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan value) {
            AdvanceMonotonic(value);
            _utcNow += value;
        }

        internal void AdvanceMonotonic(TimeSpan value) {
            _timestamp = checked(_timestamp + value.Ticks);
        }
    }

    private sealed class DurableBackend {
        private readonly ConcurrentDictionary<string, DurableCall> _calls =
            new(StringComparer.Ordinal);
        private readonly List<GalateaStartDelegateTurnRequest> _starts = [];
        private int _ensureCallCount;
        private int _inspectCallCount;

        internal int EnsureCallCount => Volatile.Read(ref _ensureCallCount);
        internal int InspectCallCount => Volatile.Read(ref _inspectCallCount);
        internal int StartCallCount {
            get { lock (_starts) { return _starts.Count; } }
        }
        internal int TotalCallCount =>
            EnsureCallCount + StartCallCount + InspectCallCount;
        internal GalateaStartDelegateTurnRequest[] StartRequests {
            get { lock (_starts) { return [.. _starts]; } }
        }

        internal GalateaDelegateBindingEstablished Ensure(
            GalateaEnsureDelegateBindingRequest request
        ) {
            Interlocked.Increment(ref _ensureCallCount);
            return new(request.BindingOperationId, "thread-fixed");
        }

        internal GalateaDelegateTurnAccepted Start(
            GalateaStartDelegateTurnRequest request
        ) {
            int ordinal;
            lock (_starts) {
                ordinal = _starts.Count;
                _starts.Add(request);
            }
            string turnId = "turn-" + ordinal;
            if (!_calls.TryAdd(
                    request.DispatchId,
                    new DurableCall(turnId))) {
                throw new InvalidOperationException("duplicate start");
            }
            return new(request.DispatchId, request.ThreadId, turnId);
        }

        internal GalateaDelegateDispatchInspection Inspect(
            GalateaInspectDelegateDispatchRequest request
        ) {
            Interlocked.Increment(ref _inspectCallCount);
            DurableCall call = _calls[request.DispatchId];
            if (!string.Equals(
                    request.ExpectedTurnId,
                    call.TurnId,
                    StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    "Accepted vertical inspection did not select its exact turn."
                );
            }
            string? failureCode = Volatile.Read(ref call.FailureCode);
            if (failureCode is not null) {
                return new GalateaDelegateDispatchInspection.Failed(
                    request.DispatchId,
                    request.ThreadId,
                    call.TurnId,
                    failureCode,
                    GalateaDelegateInspectionSource.Persistent
                );
            }
            string? final = Volatile.Read(ref call.Final);
            return final is null
                ? new GalateaDelegateDispatchInspection.Running(
                    request.DispatchId,
                    request.ThreadId,
                    call.TurnId,
                    GalateaDelegateInspectionSource.Persistent
                )
                : new GalateaDelegateDispatchInspection.Completed(
                    request.DispatchId,
                    request.ThreadId,
                    call.TurnId,
                    final,
                    GalateaDelegateInspectionSource.Persistent
                );
        }

        internal void Complete(int ordinal, string final) {
            GalateaStartDelegateTurnRequest request;
            lock (_starts) { request = _starts[ordinal]; }
            Volatile.Write(ref _calls[request.DispatchId].Final, final);
        }

        internal void InterruptRunning() {
            foreach (DurableCall call in _calls.Values) {
                if (Volatile.Read(ref call.Final) is null) {
                    Volatile.Write(ref call.FailureCode, "TURN_INTERRUPTED");
                }
            }
        }
    }

    private sealed class DurableCall(string turnId) {
        internal string TurnId { get; } = turnId;
        internal string? Final;
        internal string? FailureCode;
    }

    private sealed class DurableTransport(
        DurableBackend backend,
        bool interruptRunningOnDispose = false
    )
        : IGalateaDurableDelegateTransport {
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(backend.Ensure(request));
        }

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(backend.Start(request));
        }

        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(backend.Inspect(request));
        }

        public ValueTask DisposeAsync() {
            if (interruptRunningOnDispose) {
                backend.InterruptRunning();
            }
            return ValueTask.CompletedTask;
        }
    }
}
