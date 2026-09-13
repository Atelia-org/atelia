using Atelia.EventJournal;
using Atelia.Galatea.Server;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.Testing;
using System.Collections.Concurrent;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDurableDelegationDriverTests {
    private const string BindingOperationId =
        "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task UnboundMail_ClaimsBindingBeforeExternalEnsure() {
        using var fixture = new DriverStore();
        await using var transport = new ScriptedTransport();
        transport.EnsureSteps.Enqueue((request, _) => Task.FromResult(
            new GalateaDelegateBindingEstablished(
                request.BindingOperationId,
                "thread-1"
            )
        ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);

        GalateaDurableDelegationPulseResult claimed =
            await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.BindingClaimed,
            claimed.Step);
        Assert.Equal(0, transport.ExternalCallCount);
        GalateaDelegationStateSnapshot binding = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDelegationRouteState.Binding,
            binding.Route.State);
        Assert.Equal(BindingOperationId,
            binding.Route.BindingOperationId);

        GalateaDurableDelegationPulseResult established =
            await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.BindingEstablished,
            established.Step);
        Assert.Equal(1, transport.EnsureCallCount);
        Assert.Equal(GalateaDelegationRouteState.Bound,
            fixture.Store.ReadSnapshot().Route.State);
        Assert.Equal(GalateaDurableMailState.Queued,
            fixture.Store.ReadSnapshot().Mails.Single().State);
    }

    [Fact]
    public async Task BindingRetry_ReusesOperationIdAndHonorsDurableBackoff() {
        using var fixture = new DriverStore();
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.EnsureSteps.Enqueue(static (_, _) => Task.FromException<
            GalateaDelegateBindingEstablished>(
                new GalateaDurableDelegateTransportException(
                    "ensure-binding",
                    "BINDING_OUTCOME_UNKNOWN"
                )
            ));
        transport.EnsureSteps.Enqueue((request, _) => Task.FromResult(
            new GalateaDelegateBindingEstablished(
                request.BindingOperationId,
                "thread-1"
            )
        ));
        GalateaDurableDelegationDriver driver = fixture.Driver(
            transport,
            clock
        );
        _ = await driver.PulseAsync();

        GalateaDurableDelegationPulseResult deferred =
            await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.BindingDeferred,
            deferred.Step);
        GalateaOutboundMailSnapshot mail = fixture.Store.ReadSnapshot().Mails.Single();
        Assert.Equal(1, mail.RecoveryFailureCount);
        Assert.Equal(1_000, mail.NextRetryAtUnixTimeMilliseconds);

        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff,
            (await driver.PulseAsync()).Step);
        Assert.Equal(1, transport.EnsureCallCount);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(GalateaDurableDelegationPulseStep.BindingEstablished,
            (await driver.PulseAsync()).Step);
        Assert.Equal(
            [BindingOperationId, BindingOperationId],
            transport.EnsureRequests
                .Select(static request => request.BindingOperationId)
                .ToArray()
        );
    }

    [Theory]
    [InlineData("protocol", "SIDECAR_WRITE_FAILED", false)]
    [InlineData("ensure-binding", "SIDECAR_STOPPING", false)]
    [InlineData("ensure-binding", "THREAD_NOT_FOUND", false)]
    [InlineData("future-stage", "FUTURE_CODE", false)]
    public async Task BindingFailurePolicy_IsClosed(
        string stage,
        string code,
        bool quarantine
    ) {
        using var fixture = new DriverStore();
        await using var transport = new ScriptedTransport();
        transport.EnsureSteps.Enqueue((_, _) => Task.FromException<
            GalateaDelegateBindingEstablished>(
                new GalateaDurableDelegateTransportException(stage, code)
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();

        GalateaDurableDelegationPulseResult result =
            await driver.PulseAsync();

        Assert.Equal(
            quarantine
                ? GalateaDurableDelegationPulseStep.TerminalFailed
                : GalateaDurableDelegationPulseStep.BindingDeferred,
            result.Step
        );
    }

    [Fact]
    public async Task BoundQueuedMail_CommitsStartedBeforeSingleStartCall() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue((request, _) => Task.FromResult(
            new GalateaDelegateTurnAccepted(
                request.DispatchId,
                request.ThreadId,
                "turn-1"
            )
        ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);

        GalateaDurableDelegationPulseResult result =
            await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.MailAccepted,
            result.Step);
        Assert.Equal(1, transport.StartCallCount);
        GalateaOutboundMailSnapshot mail =
            fixture.Store.ReadSnapshot().Mails.Single();
        Assert.Equal(GalateaDurableMailState.Accepted, mail.State);
        Assert.Equal("thread-1", mail.AcceptedThreadId);
        Assert.Equal("turn-1", mail.AcceptedTurnId);
    }

    [Fact]
    public async Task ReopenedStartedMail_NeverCallsStartAgain() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        _ = fixture.StartHead();
        fixture.Reopen();
        await using var transport = new ScriptedTransport();
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);

        GalateaDurableDelegationPulseResult result =
            await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.RecoveredStarted,
            result.Step);
        Assert.Equal(0, transport.ExternalCallCount);
        GalateaOutboundMailSnapshot mail =
            fixture.Store.ReadSnapshot().Mails.Single();
        Assert.Equal(GalateaDurableMailState.OutcomeUnknown, mail.State);
        Assert.Equal("RECOVERED_STARTED", mail.RecoveryLastCode);
    }

    [Fact]
    public async Task LostStartResponse_RecoversByInspectWithoutSecondStart() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(static (_, _) => Task.FromException<
            GalateaDelegateTurnAccepted>(
                new GalateaDurableDelegateTransportException(
                    "start-turn",
                    "START_OUTCOME_UNKNOWN"
                )
            ));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Running(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    GalateaDelegateInspectionSource.Live
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(
            transport,
            clock
        );

        Assert.Equal(GalateaDurableDelegationPulseStep.MailOutcomeUnknown,
            (await driver.PulseAsync()).Step);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GalateaDurableDelegationPulseStep.MailAccepted,
            (await driver.PulseAsync()).Step);

        Assert.Equal(1, transport.StartCallCount);
        Assert.Equal(1, transport.InspectCallCount);
        Assert.Null(transport.InspectRequests.Single().ExpectedTurnId);
        Assert.Equal(GalateaDurableMailState.Accepted,
            fixture.Store.ReadSnapshot().Mails.Single().State);
    }

    [Theory]
    [InlineData("start-turn", "START_OUTCOME_UNKNOWN")]
    [InlineData("protocol", "SIDECAR_WRITE_FAILED")]
    [InlineData("start-turn", "THREAD_NOT_FOUND")]
    [InlineData("future-stage", "FUTURE_CODE")]
    public async Task EveryStartTransportFailure_BecomesOutcomeUnknown(
        string stage,
        string code
    ) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue((_, _) => Task.FromException<
            GalateaDelegateTurnAccepted>(
                new GalateaDurableDelegateTransportException(stage, code)
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);

        Assert.Equal(GalateaDurableDelegationPulseStep.MailOutcomeUnknown,
            (await driver.PulseAsync()).Step);
        Assert.Equal(GalateaDurableMailState.OutcomeUnknown,
            fixture.Store.ReadSnapshot().Mails.Single().State);
    }

    [Fact]
    public async Task OutcomeUnknown_NotFoundBackoffIsOneTwoFourSeconds() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(static (_, _) => Task.FromException<
            GalateaDelegateTurnAccepted>(new IOException("lost")));
        for (int index = 0; index < 2; index++) {
            transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
                GalateaDelegateDispatchInspection>(
                    new GalateaDelegateDispatchInspection.NotFound(
                        request.DispatchId,
                        request.ThreadId,
                        GalateaDelegateInspectionSource.Persistent
                    )
                ));
        }
        GalateaDurableDelegationDriver driver = fixture.Driver(
            transport,
            clock
        );

        _ = await driver.PulseAsync();
        AssertBackoff(fixture.Store, attempt: 1, nextAt: 1_000);
        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff,
            (await driver.PulseAsync()).Step);
        Assert.Equal(0, transport.InspectCallCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GalateaDurableDelegationPulseStep.InspectionNotFound,
            (await driver.PulseAsync()).Step);
        AssertBackoff(fixture.Store, attempt: 2, nextAt: 3_000);

        clock.Advance(TimeSpan.FromMilliseconds(1_999));
        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff,
            (await driver.PulseAsync()).Step);
        Assert.Equal(1, transport.InspectCallCount);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        _ = await driver.PulseAsync();
        AssertBackoff(fixture.Store, attempt: 3, nextAt: 7_000);
    }

    [Fact]
    public async Task AcceptedInvisibleThenRunningAndTerminal_SettlesOnce() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.AcceptedTurnNotVisible(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    GalateaDelegateInspectionSource.Persistent
                )
            ));
        transport.InspectSteps.Enqueue(Running(
            "turn-1",
            GalateaDelegateInspectionSource.Live
        ));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Completed(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    "done",
                    GalateaDelegateInspectionSource.Live
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(
            transport,
            clock
        );
        _ = await driver.PulseAsync();
        GalateaDelegationStateSnapshot acceptedSnapshot =
            fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot acceptedBefore =
            acceptedSnapshot.Mails.Single();

        Assert.Equal(
            GalateaDurableDelegationPulseStep.AcceptedTurnNotVisible,
            (await driver.PulseAsync()).Step);
        GalateaOutboundMailSnapshot mail =
            fixture.Store.ReadSnapshot().Mails.Single();
        Assert.Equal(GalateaDurableMailState.Accepted, mail.State);
        Assert.Equal("thread-1", mail.AcceptedThreadId);
        Assert.Equal("turn-1", mail.AcceptedTurnId);
        Assert.Equal(1, mail.RecoveryFailureCount);
        Assert.Equal("ACCEPTED_TURN_NOT_VISIBLE", mail.RecoveryLastCode);
        Assert.Equal(1_000, mail.NextRetryAtUnixTimeMilliseconds);
        string diagnostic = GalateaDurableDelegationDriver
            .FormatInspectionDeferredDiagnostic(
                acceptedSnapshot,
                acceptedBefore,
                mail,
                "inspect-dispatch",
                "ACCEPTED_TURN_NOT_VISIBLE",
                GalateaDelegateInspectionSource.Persistent
            );
        Assert.Contains("selectorMode=accepted-turn", diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("knownTurnId=turn-1", diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("source=persistent", diagnostic,
            StringComparison.Ordinal);
        Assert.Contains(
            "stage=inspect-dispatch, code=ACCEPTED_TURN_NOT_VISIBLE",
            diagnostic,
            StringComparison.Ordinal
        );
        Assert.Contains("recovered=false, attempt=1, nextAt=1000",
            diagnostic, StringComparison.Ordinal);
        Assert.True(GalateaDurableDelegationDriver
            .ShouldWarnInspectionDeferred(
                acceptedBefore,
                "ACCEPTED_TURN_NOT_VISIBLE"
            ));
        Assert.Empty(fixture.Store.ReadSnapshot().Notices);
        Assert.Equal(GalateaDelegationRouteState.Bound,
            fixture.Store.ReadSnapshot().Route.State);

        fixture.Reopen();
        driver = fixture.Driver(transport, clock);
        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff,
            (await driver.PulseAsync()).Step);
        Assert.Equal(1, transport.InspectCallCount);
        Assert.Equal(1, transport.StartCallCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GalateaDurableDelegationPulseStep.AcceptedRunning,
            (await driver.PulseAsync()).Step);
        mail = fixture.Store.ReadSnapshot().Mails.Single();
        Assert.Equal(0, mail.RecoveryFailureCount);
        Assert.Null(mail.RecoveryLastCode);
        Assert.Null(mail.NextRetryAtUnixTimeMilliseconds);

        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalCompleted,
            (await driver.PulseAsync()).Step);
        Assert.Equal(GalateaDurableDelegationPulseStep.NoWork,
            (await driver.PulseAsync()).Step);
        Assert.Equal(GalateaDurableMailState.TerminalCompleted,
            fixture.Store.ReadSnapshot().Mails.Single().State);
        Assert.Single(fixture.Store.ReadSnapshot().Notices);
        Assert.Null(fixture.Store.ReadSnapshot().Route.ActiveDispatchId);
        Assert.Equal(1, transport.StartCallCount);
        Assert.Equal(3, transport.InspectCallCount);
        Assert.All(
            transport.InspectRequests,
            request => Assert.Equal("turn-1", request.ExpectedTurnId)
        );
    }

    [Fact]
    public async Task IllegalSelectorResultCombinations_FailClosed() {
        await VerifyAcceptedNotFoundFinishesLocallyAsync();
        await VerifyOutcomeUnknownUnavailableFinishesLocallyAsync();
    }

    [Fact]
    public async Task OutcomeUnknownGenericAcceptedInvisibleCode_FailsClosed() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(static (_, _) => Task.FromException<
            GalateaDelegateTurnAccepted>(new IOException("lost")));
        transport.InspectSteps.Enqueue(static (_, _) => Task.FromException<
            GalateaDelegateDispatchInspection>(
                new GalateaDurableDelegateTransportException(
                    "inspect-dispatch",
                    "ACCEPTED_TURN_NOT_VISIBLE"
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(
            transport,
            clock
        );
        _ = await driver.PulseAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff,
            (await driver.PulseAsync()).Step);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDurableMailState.OutcomeUnknown,
            snapshot.Mails.Single().State);
        Assert.Empty(snapshot.Notices);
        Assert.Equal(1, transport.StartCallCount);
        Assert.Null(transport.InspectRequests.Single().ExpectedTurnId);
    }

    [Fact]
    public async Task SidecarAcceptedNotFound_FinishesLocallyWithoutSecondStart() {
        using var store = new DriverStore();
        store.Bind("thread-1");
        using var sidecar = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":5,"type":"ready"}'
            count=0
            while IFS= read -r line; do
              count=$((count + 1))
              printf '%s\n' "$line" >> {{GalateaSidecarProcessFixture.ShellQuote("INPUT")}}
              request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
              dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
              thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
              if [ "$count" -eq 1 ]; then
                printf '{"v":5,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"%s","turnId":"turn-1"}\n' "$request_id" "$dispatch_id" "$thread_id"
              else
                printf '{"v":5,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"not-found","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
              fi
            done
            """
        );
        await using GalateaCodexDurableSidecarClient transport =
            sidecar.CreateClient();
        GalateaDurableDelegationDriver driver = store.Driver(transport);

        Assert.Equal(GalateaDurableDelegationPulseStep.MailAccepted,
            (await driver.PulseAsync()).Step);
        GalateaDurableDelegationPulseResult result =
            await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            result.Step);
        Assert.Equal("RESULT_UNCONFIRMED", result.Code);
        GalateaDelegationStateSnapshot snapshot = store.Store.ReadSnapshot();
        Assert.Equal(GalateaDurableMailState.TerminalFailed,
            snapshot.Mails.Single().State);
        Assert.Single(snapshot.Notices);
        string[] lines = File.ReadAllLines(sidecar.InputPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"type\":\"start-turn\"", lines[0],
            StringComparison.Ordinal);
        Assert.Contains("\"type\":\"inspect-dispatch\"", lines[1],
            StringComparison.Ordinal);
        Assert.Contains("\"expectedTurnId\":\"turn-1\"", lines[1],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningLivenessGate_LogsFirstRecoveryAndEveryMinute() {
        using var fixture = new DriverStore();
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        GalateaDurableDelegationDriver driver = fixture.Driver(
            transport,
            clock
        );

        Assert.True(driver.ShouldLogDebugRunningLiveness(
            "dispatch-1",
            recovered: false
        ));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(driver.ShouldLogDebugRunningLiveness(
            "dispatch-1",
            recovered: false
        ));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(driver.ShouldLogDebugRunningLiveness(
            "dispatch-1",
            recovered: false
        ));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(driver.ShouldLogDebugRunningLiveness(
            "dispatch-1",
            recovered: true
        ));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(driver.ShouldLogDebugRunningLiveness(
            "dispatch-1",
            recovered: false
        ));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(driver.ShouldLogDebugRunningLiveness(
            "dispatch-1",
            recovered: false
        ));
        Assert.True(driver.ShouldLogDebugRunningLiveness(
            "dispatch-2",
            recovered: false
        ));
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            GalateaDurableDelegationDriver.DebugRunningLivenessInterval
        );
    }

    [Theory]
    [InlineData("inspect-dispatch", "INSPECTION_UNAVAILABLE", false)]
    [InlineData("inspect-dispatch", "ACCEPTED_TURN_NOT_VISIBLE", false)]
    [InlineData("protocol", "SIDECAR_WRITE_FAILED", false)]
    [InlineData("shutdown", "STOPPING", false)]
    [InlineData("start-turn", "THREAD_NOT_FOUND", false)]
    [InlineData("future-stage", "FUTURE_CODE", false)]
    public async Task InspectionFailurePolicy_IsClosed(
        string stage,
        string code,
        bool quarantine
    ) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((_, _) => Task.FromException<
            GalateaDelegateDispatchInspection>(
                new GalateaDurableDelegateTransportException(stage, code)
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();

        GalateaDurableDelegationPulseResult result =
            await driver.PulseAsync();

        Assert.Equal(
            quarantine
                ? GalateaDurableDelegationPulseStep.TerminalFailed
                : code == "ACCEPTED_TURN_NOT_VISIBLE"
                    ? GalateaDurableDelegationPulseStep.AcceptedTurnNotVisible
                    : GalateaDurableDelegationPulseStep.Backoff,
            result.Step
        );
    }

    [Fact]
    public async Task DeferredDiagnosticPreservesTransportStage() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue(static (_, _) => Task.FromException<
            GalateaDelegateDispatchInspection>(
                new GalateaDurableDelegateTransportException(
                    "shutdown",
                    "STOPPING"
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        GalateaDelegationStateSnapshot before = fixture.Store.ReadSnapshot();

        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff,
            (await driver.PulseAsync()).Step);
        GalateaOutboundMailSnapshot after =
            fixture.Store.ReadSnapshot().Mails.Single();
        string diagnostic = GalateaDurableDelegationDriver
            .FormatInspectionDeferredDiagnostic(
                before,
                before.Mails.Single(),
                after,
                "shutdown",
                "STOPPING",
                source: null
            );

        Assert.Contains("source=none", diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("stage=shutdown, code=STOPPING", diagnostic,
            StringComparison.Ordinal);
        Assert.False(GalateaDurableDelegationDriver
            .ShouldWarnInspectionDeferred(
                before.Mails.Single(),
                "STOPPING"
            ));
    }

    [Fact]
    public async Task BackoffStartsWhenSlowExternalFailureReturns() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue((_, _) => {
            clock.Advance(TimeSpan.FromSeconds(10));
            return Task.FromException<GalateaDelegateTurnAccepted>(
                new IOException("late lost response")
            );
        });
        GalateaDurableDelegationDriver driver = fixture.Driver(
            transport,
            clock
        );

        _ = await driver.PulseAsync();

        AssertBackoff(fixture.Store, attempt: 1, nextAt: 11_000);
        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff,
            (await driver.PulseAsync()).Step);
        Assert.Equal(0, transport.InspectCallCount);
    }

    [Theory]
    [InlineData("DISPATCH_TURN_MISMATCH")]
    [InlineData("LIVE_OBSERVATION_CONFLICT")]
    [InlineData("PAGE_SHAPE_INVALID")]
    [InlineData("PAGINATION_CURSOR_INVALID")]
    [InlineData("PAGINATION_CURSOR_LOOP")]
    public async Task AmbiguousInspection_FinishesLocallyRouteAndMail(
        string code
    ) {
        GalateaDelegateInspectionSource source =
            code == "LIVE_OBSERVATION_CONFLICT"
                ? GalateaDelegateInspectionSource.Live
                : GalateaDelegateInspectionSource.Persistent;
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Ambiguous(
                    request.DispatchId,
                    request.ThreadId,
                    code,
                    source
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            (await driver.PulseAsync()).Step);

        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDelegationRouteState.Unbound,
            snapshot.Route.State);
        Assert.Equal(GalateaDurableMailState.TerminalFailed,
            snapshot.Mails.Single().State);
        Assert.Equal("RESULT_UNCONFIRMED", snapshot.Mails.Single().TerminalCode);
        Assert.Single(snapshot.Notices);
    }

    [Fact]
    public async Task CompletedFailedAndRecoveredTerminalResults_AreDurable() {
        await VerifyAcceptedCompletedAsync();
        await VerifyAcceptedFailedAsync();
        await VerifyOutcomeUnknownCompletedAsync();
    }

    [Fact]
    public async Task InvalidFinalAndFailureCode_UseBoundedFailureNotice() {
        await VerifyInvalidFinalAsync(" ", "FINAL_BLANK");
        await VerifyInvalidFinalAsync(new string('x', 1_025),
            "FINAL_TOO_LARGE");
        await VerifyInvalidFinalAsync(
            string.Concat('x', new string(['\ud800'])),
            "FINAL_INVALID_UNICODE"
        );
        await VerifyMalformedFailureCodeAsync("bad code with spaces");
        await VerifyMalformedFailureCodeAsync(new string('X', 65));
    }

    [Fact]
    public async Task InboxCapacityBackpressure_DoesNotCallTransport() {
        using var fixture = new DriverStore(
            bodies: ["first", "second"],
            maximumInboxReplies: 1
        );
        fixture.Bind("thread-1");
        GalateaOutboundMailSnapshot first = fixture.StartHead();
        _ = fixture.Store.RecordCompletedMail(
            first.DispatchId,
            first.Revision,
            "thread-1",
            "turn-1",
            "reply"
        );
        await using var transport = new ScriptedTransport();
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);

        Assert.Equal(GalateaDurableDelegationPulseStep.InboxBackpressure,
            (await driver.PulseAsync()).Step);
        Assert.Equal(0, transport.ExternalCallCount);
        Assert.Equal(GalateaDurableMailState.Queued,
            fixture.Store.ReadSnapshot().Mails[1].State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizeFifoHead_FailsPreflightWithoutTransport(
        bool bindFirst
    ) {
        using var fixture = new DriverStore(
            bodies: bindFirst ? ["ok"] : ["four", "ok"],
            maximumTaskUtf8Bytes: 3
        );
        if (bindFirst) {
            fixture.Bind("thread-1");
            GalateaOutboundMailSnapshot valid = fixture.StartHead();
            _ = fixture.Store.RecordCompletedMail(
                valid.DispatchId,
                valid.Revision,
                "thread-1",
                "turn-before-oversize",
                "ok"
            );
            fixture.CaptureBodies(101, ["four", "ok"]);
        }
        await using var transport = new ScriptedTransport();
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);

        GalateaDurableDelegationPulseResult result =
            await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.QueuedPreflightFailed,
            result.Step);
        Assert.Equal(0, transport.ExternalCallCount);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDurableMailState.TerminalFailed,
            snapshot.Mails.Single(mail => string.Equals(
                mail.DispatchId,
                result.DispatchId,
                StringComparison.Ordinal
            )).State);
        Assert.Equal(GalateaDelegationDurableContract.TaskTooLargeCode,
            snapshot.Notices.Single(notice => string.Equals(
                notice.DispatchId,
                result.DispatchId,
                StringComparison.Ordinal
            )).Code);
        Assert.Equal(GalateaDurableMailState.Queued,
            snapshot.Mails.Single(mail => mail.Body == "ok"
                && mail.DispatchId != result.DispatchId).State);
    }

    [Fact]
    public async Task WrongEnsureStartAndInspectionIdentity_FinishLocally() {
        await VerifyWrongEnsureIdentityAsync();
        await VerifyWrongStartIdentityAsync();
        await VerifyWrongInspectionBaseIdentityAsync();
        await VerifyWrongInspectionIdentityAsync();
    }

    [Fact]
    public async Task ConcurrentPulses_NeverOverlapExternalCalls() {
        using var fixture = new DriverStore();
        await using var transport = new ScriptedTransport();
        var ensureEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnsure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.EnsureSteps.Enqueue(async (request, _) => {
            ensureEntered.SetResult();
            await releaseEnsure.Task;
            return new(request.BindingOperationId, "thread-1");
        });
        transport.StartSteps.Enqueue(Accept("turn-1"));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();

        Task<GalateaDurableDelegationPulseResult> first = driver.PulseAsync();
        await ensureEntered.Task;
        Task<GalateaDurableDelegationPulseResult> second = driver.PulseAsync();
        await Task.Yield();
        Assert.Equal(1, transport.MaximumConcurrentCalls);
        releaseEnsure.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, transport.MaximumConcurrentCalls);
        Assert.Equal(1, transport.EnsureCallCount);
        Assert.Equal(1, transport.StartCallCount);
    }

    [Fact]
    public async Task CancellationAfterExternalEntry_IsDurablyClassified() {
        await VerifyBindingCancellationAsync();
        await VerifyStartCancellationAsync();
        await VerifyTransportSelfCancellationAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectOuterCancellation_PropagatesWithoutPollMiss(
        bool transportMapsCancellation
    ) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        transport.InspectSteps.Enqueue(async (_, ct) => {
            entered.TrySetResult();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (
                transportMapsCancellation) {
                throw new GalateaDurableDelegateTransportException(
                    "inspect-dispatch",
                    "INSPECTION_UNAVAILABLE"
                );
            }
            throw new InvalidOperationException("unreachable");
        });
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        GalateaDelegationStateSnapshot before = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot beforeMail = before.Mails.Single();
        using var cancellation = new CancellationTokenSource();

        Task<GalateaDurableDelegationPulseResult> pulse =
            driver.PulseAsync(cancellation.Token);
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pulse);
        GalateaDelegationStateSnapshot after = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot afterMail = after.Mails.Single();
        Assert.Equal(before.StoreRevision, after.StoreRevision);
        Assert.Equal(beforeMail.Revision, afterMail.Revision);
        Assert.Equal(GalateaDurableMailState.Accepted, afterMail.State);
        Assert.Equal(0, afterMail.RecoveryFailureCount);
        Assert.Null(afterMail.RecoveryLastCode);
        Assert.Null(afterMail.NextRetryAtUnixTimeMilliseconds);
        Assert.Equal(1, transport.StartCallCount);
        Assert.Equal(1, transport.InspectCallCount);
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("running")]
    [InlineData("unavailable")]
    public async Task InspectOuterCancellation_WinsOverLateNonterminal(
        string kind
    ) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        transport.InspectSteps.Enqueue(async (request, _) => {
            entered.TrySetResult();
            await release.Task;
            return kind switch {
                "running" => new GalateaDelegateDispatchInspection.Running(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    GalateaDelegateInspectionSource.Persistent
                ),
                "unavailable" => new GalateaDelegateDispatchInspection
                    .AcceptedTurnNotVisible(
                        request.DispatchId,
                        request.ThreadId,
                        "turn-1",
                        GalateaDelegateInspectionSource.Persistent
                    ),
                _ => new GalateaDelegateDispatchInspection.NotFound(
                    request.DispatchId,
                    request.ThreadId,
                    GalateaDelegateInspectionSource.Persistent
                )
            };
        });
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        GalateaDelegationStateSnapshot before = fixture.Store.ReadSnapshot();
        using var cancellation = new CancellationTokenSource();
        Task<GalateaDurableDelegationPulseResult> pulse =
            driver.PulseAsync(cancellation.Token);
        await entered.Task;

        cancellation.Cancel();
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pulse);
        GalateaDelegationStateSnapshot after = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = after.Mails.Single();
        Assert.Equal(before.StoreRevision, after.StoreRevision);
        Assert.Equal(GalateaDurableMailState.Accepted, mail.State);
        Assert.Equal(0, mail.RecoveryFailureCount);
        Assert.Null(mail.RecoveryLastCode);
        Assert.Null(mail.NextRetryAtUnixTimeMilliseconds);
        Assert.Equal(1, transport.StartCallCount);
        Assert.Equal(1, transport.InspectCallCount);
    }

    [Theory]
    [InlineData("start-turn", "THREAD_NOT_FOUND")]
    [InlineData("future-stage", "FUTURE_CODE")]
    public async Task InspectOuterCancellation_DoesNotConsumeRecoveryForTransportFailure(
        string stage,
        string code
    ) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        transport.InspectSteps.Enqueue(async (_, _) => {
            entered.TrySetResult();
            await release.Task;
            throw new GalateaDurableDelegateTransportException(stage, code);
        });
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        using var cancellation = new CancellationTokenSource();
        Task<GalateaDurableDelegationPulseResult> pulse =
            driver.PulseAsync(cancellation.Token);
        await entered.Task;

        cancellation.Cancel();
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulse);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDelegationRouteState.Bound, snapshot.Route.State);
        Assert.Equal(GalateaDurableMailState.Accepted, snapshot.Mails.Single().State);
        Assert.Equal(0, snapshot.Mails.Single().RecoveryFailureCount);
        Assert.Empty(snapshot.Notices);
        Assert.Equal(1, transport.StartCallCount);
        Assert.Equal(1, transport.InspectCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InspectOuterCancellation_DoesNotMaskTerminal(
        bool completed
    ) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        transport.InspectSteps.Enqueue(async (request, _) => {
            entered.TrySetResult();
            await release.Task;
            return completed
                ? new GalateaDelegateDispatchInspection.Completed(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    "done",
                    GalateaDelegateInspectionSource.Persistent
                )
                : new GalateaDelegateDispatchInspection.Failed(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    "TURN_FAILED",
                    GalateaDelegateInspectionSource.Persistent
                );
        });
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        using var cancellation = new CancellationTokenSource();
        Task<GalateaDurableDelegationPulseResult> pulse =
            driver.PulseAsync(cancellation.Token);
        await entered.Task;

        cancellation.Cancel();
        release.TrySetResult();

        GalateaDurableDelegationPulseResult result = await pulse;
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(
            completed
                ? GalateaDurableDelegationPulseStep.TerminalCompleted
                : GalateaDurableDelegationPulseStep.TerminalFailed,
            result.Step
        );
        Assert.Equal(
            completed
                ? GalateaDurableMailState.TerminalCompleted
                : GalateaDurableMailState.TerminalFailed,
            snapshot.Mails.Single().State
        );
        Assert.Equal(
            completed
                ? GalateaReplyNoticeKind.Reply
                : GalateaReplyNoticeKind.DeliveryFailure,
            snapshot.Notices.Single().Kind
        );
        Assert.Null(snapshot.Route.ActiveDispatchId);
        Assert.Equal(1, transport.StartCallCount);
        Assert.Equal(1, transport.InspectCallCount);
    }

    [Theory]
    [InlineData("base-identity", "INSPECTION_RESULT_IDENTITY_MISMATCH")]
    [InlineData("turn-identity", "INSPECTION_TURN_IDENTITY_MISMATCH")]
    [InlineData("ambiguous", "DISPATCH_BODY_MISMATCH")]
    public async Task InspectOuterCancellation_DoesNotMaskReturnedConflict(
        string kind,
        string expectedCode
    ) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        transport.InspectSteps.Enqueue(async (request, _) => {
            entered.TrySetResult();
            await release.Task;
            return kind switch {
                "base-identity" =>
                    new GalateaDelegateDispatchInspection.Running(
                        request.DispatchId + "-wrong",
                        request.ThreadId,
                        "turn-1",
                        GalateaDelegateInspectionSource.Persistent
                    ),
                "turn-identity" =>
                    new GalateaDelegateDispatchInspection.Running(
                        request.DispatchId,
                        request.ThreadId,
                        "turn-wrong",
                        GalateaDelegateInspectionSource.Persistent
                    ),
                _ => new GalateaDelegateDispatchInspection.Ambiguous(
                    request.DispatchId,
                    request.ThreadId,
                    expectedCode,
                    GalateaDelegateInspectionSource.Persistent
                )
            };
        });
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        using var cancellation = new CancellationTokenSource();
        Task<GalateaDurableDelegationPulseResult> pulse =
            driver.PulseAsync(cancellation.Token);
        await entered.Task;

        cancellation.Cancel();
        release.TrySetResult();

        GalateaDurableDelegationPulseResult result = await pulse;
        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            result.Step);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDelegationRouteState.Unbound,
            snapshot.Route.State);
        Assert.Equal(GalateaDurableMailState.TerminalFailed,
            snapshot.Mails.Single().State);
        Assert.Equal("RESULT_UNCONFIRMED", snapshot.Mails.Single().TerminalCode);
        Assert.Equal(1, transport.StartCallCount);
        Assert.Equal(1, transport.InspectCallCount);
    }

    [Fact]
    public async Task BindingConfigurationFailure_InboxFullPersistsSettlementWithoutAnotherEnsure() {
        using var fixture = new DriverStore(bodies: ["first", "second"], maximumInboxReplies: 1);
        GalateaDelegationStateSnapshot initial = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot first = initial.Mails[0];
        _ = fixture.Store.FinishMailLocally(first.DispatchId, first.Revision,
            initial.Route.Revision, "INVALID_CONFIG", resetBinding: true);
        await using var transport = new ScriptedTransport();
        transport.EnsureSteps.Enqueue((_, _) => Task.FromException<GalateaDelegateBindingEstablished>(
            new GalateaDurableDelegateTransportException("ensure-binding", "INVALID_CONFIG")));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        Assert.Equal(GalateaDurableDelegationPulseStep.BindingClaimed, (await driver.PulseAsync()).Step);
        Assert.Equal(GalateaDurableDelegationPulseStep.InboxBackpressure, (await driver.PulseAsync()).Step);
        GalateaOutboundMailSnapshot second = fixture.Store.ReadSnapshot().Mails[1];
        Assert.Equal(8, second.RecoveryFailureCount);
        Assert.Equal("INVALID_CONFIG", second.RecoveryLastCode);
        fixture.Reopen();
        driver = fixture.Driver(transport);
        Assert.Equal(GalateaDurableDelegationPulseStep.InboxBackpressure, (await driver.PulseAsync()).Step);
        Assert.Equal(1, transport.EnsureCallCount);
        Assert.Equal(0, transport.StartCallCount);
        Assert.Single(fixture.Store.ReadSnapshot().Notices);
    }

    [Fact]
    public async Task RecoveryBudget_SurvivesBindingRequeueRebindingAndAcceptance() {
        using var fixture = new DriverStore();
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.EnsureSteps.Enqueue((_, _) => Task.FromException<GalateaDelegateBindingEstablished>(new IOException("offline")));
        transport.EnsureSteps.Enqueue((request, _) => Task.FromResult(new GalateaDelegateBindingEstablished(request.BindingOperationId, "thread-1")));
        transport.EnsureSteps.Enqueue((request, _) => Task.FromResult(new GalateaDelegateBindingEstablished(request.BindingOperationId, "thread-2")));
        transport.StartSteps.Enqueue((_, _) => Task.FromException<GalateaDelegateTurnAccepted>(
            new GalateaDurableDelegateTransportException("start-turn", "THREAD_NOT_FOUND", GalateaDelegateDispatchState.NotDispatched)));
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((_, _) => Task.FromException<GalateaDelegateDispatchInspection>(new IOException("offline")));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport, clock);
        _ = await driver.PulseAsync(); // claim
        _ = await driver.PulseAsync(); // binding miss
        Assert.Equal(1, fixture.Store.ReadSnapshot().Mails.Single().RecoveryFailureCount);
        clock.Advance(TimeSpan.FromMinutes(1));
        _ = await driver.PulseAsync(); // binding succeeds
        Assert.Equal(1, fixture.Store.ReadSnapshot().Mails.Single().RecoveryFailureCount);
        Assert.Equal(GalateaDurableDelegationPulseStep.MailRequeued, (await driver.PulseAsync()).Step);
        Assert.Equal(2, fixture.Store.ReadSnapshot().Mails.Single().RecoveryFailureCount);
        clock.Advance(TimeSpan.FromMinutes(1));
        _ = await driver.PulseAsync(); // rebind claim
        _ = await driver.PulseAsync(); // rebind succeeds
        Assert.Equal(GalateaDurableDelegationPulseStep.MailAccepted, (await driver.PulseAsync()).Step);
        Assert.Equal(2, fixture.Store.ReadSnapshot().Mails.Single().RecoveryFailureCount);
        _ = await driver.PulseAsync(); // inspect miss
        Assert.Equal(3, fixture.Store.ReadSnapshot().Mails.Single().RecoveryFailureCount);
        Assert.Equal(2, transport.StartCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistentFailures_ExhaustBudgetWithoutResending(bool persistentRunning) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        for (int i = 0; i < 8; i++) {
            transport.InspectSteps.Enqueue(persistentRunning
                ? Running("turn-1", GalateaDelegateInspectionSource.Persistent)
                : (_, _) => Task.FromException<GalateaDelegateDispatchInspection>(
                    new IOException("app-server unavailable")));
        }
        GalateaDurableDelegationDriver driver = fixture.Driver(transport, clock);
        _ = await driver.PulseAsync();
        for (int i = 1; i <= 8; i++) {
            GalateaDurableDelegationPulseResult result = await driver.PulseAsync();
            Assert.Equal(i, fixture.Store.ReadSnapshot().Mails.Single().RecoveryFailureCount);
            if (i < 8) {
                Assert.Equal(GalateaDurableDelegationPulseStep.Backoff, result.Step);
            }
            else {
                Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed, result.Step);
            }
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal("RESULT_UNCONFIRMED", snapshot.Notices.Single().Code);
        Assert.Equal(GalateaDelegationRouteState.Unbound, snapshot.Route.State);
        Assert.Equal(1, transport.StartCallCount);
    }

    [Theory]
    [InlineData("THREAD_NOT_FOUND", true)]
    [InlineData("SIDECAR_READY_TIMEOUT", false)]
    public async Task ProvenNotDispatched_RequeuesWithBudgetAndSelectiveBindingReset(
        string code, bool resetBinding
    ) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue((_, _) => Task.FromException<GalateaDelegateTurnAccepted>(
            new GalateaDurableDelegateTransportException("start-turn", code,
                GalateaDelegateDispatchState.NotDispatched)));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport, clock);
        Assert.Equal(GalateaDurableDelegationPulseStep.MailRequeued, (await driver.PulseAsync()).Step);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(resetBinding ? GalateaDelegationRouteState.Unbound : GalateaDelegationRouteState.Bound, snapshot.Route.State);
        Assert.Null(snapshot.Route.ActiveDispatchId);
        Assert.Equal(GalateaDurableMailState.Queued, snapshot.Mails.Single().State);
        Assert.Equal(1, snapshot.Mails.Single().RecoveryFailureCount);
        Assert.Empty(snapshot.Notices);
        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff, (await driver.PulseAsync()).Step);
        Assert.Equal(1, transport.StartCallCount);
    }

    [Fact]
    public async Task BindingFailures_ExhaustSameMailBudgetAndReleaseQueue() {
        using var fixture = new DriverStore();
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        for (int i = 0; i < 8; i++) {
            transport.EnsureSteps.Enqueue((_, _) => Task.FromException<GalateaDelegateBindingEstablished>(
                new IOException("app-server unavailable")));
        }
        GalateaDurableDelegationDriver driver = fixture.Driver(transport, clock);
        _ = await driver.PulseAsync();
        for (int i = 1; i <= 8; i++) {
            _ = await driver.PulseAsync();
            Assert.Equal(i, fixture.Store.ReadSnapshot().Mails.Single().RecoveryFailureCount);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal("NOT_DISPATCHED_RETRIES_EXHAUSTED", fixture.Store.ReadSnapshot().Notices.Single().Code);
        Assert.Equal(GalateaDelegationRouteState.Unbound, fixture.Store.ReadSnapshot().Route.State);
        Assert.Equal(0, transport.StartCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WallClockRollback_RetryDueDoesNotSlideAndReopenRetainsBudget(bool reopen) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        clock.Advance(TimeSpan.FromDays(2));
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue((_, _) => Task.FromException<GalateaDelegateTurnAccepted>(new IOException("lost")));
        transport.InspectSteps.Enqueue(NotFound());
        GalateaDurableDelegationDriver driver = fixture.Driver(transport, clock);
        _ = await driver.PulseAsync();
        clock.SetUtcNow(DateTimeOffset.UnixEpoch);
        if (reopen) {
            fixture.Reopen();
            driver = fixture.Driver(transport, clock);
        }
        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff, (await driver.PulseAsync()).Step);
        for (int i = 0; i < 60; i++) {
            clock.Advance(TimeSpan.FromSeconds(1));
            clock.SetUtcNow(DateTimeOffset.UnixEpoch);
            GalateaDurableDelegationPulseResult result = await driver.PulseAsync();
            if (result.Step == GalateaDurableDelegationPulseStep.InspectionNotFound) {
                Assert.Equal(reopen ? 60 : 1, i + 1);
                Assert.Equal(2, fixture.Store.ReadSnapshot().Mails.Single().RecoveryFailureCount);
                Assert.Equal(1, transport.StartCallCount);
                return;
            }
        }
        Assert.Fail("Wall clock rollback prevented bounded monotonic retry.");
    }

    [Theory]
    [InlineData("THREAD_NOT_FOUND", true)]
    [InlineData("INSPECTION_TURN_IDENTITY_MISMATCH", false)]
    public async Task LegacyQuarantine_OnlyRemoteLifecycleAllowlistCanFinish(string code, bool recoverable) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        GalateaOutboundMailSnapshot started = fixture.StartHead();
        fixture.Store.QuarantineActiveMail(started.DispatchId, started.Revision, code);
        await using var transport = new ScriptedTransport();
        GalateaDurableDelegationPulseResult result = await fixture.Driver(transport).PulseAsync();
        Assert.Equal(recoverable ? GalateaDurableDelegationPulseStep.TerminalFailed
            : GalateaDurableDelegationPulseStep.Quarantined, result.Step);
        Assert.Equal(recoverable ? 1 : 0, fixture.Store.ReadSnapshot().Notices.Count);
        Assert.Equal(0, transport.ExternalCallCount);
    }

    private static async Task VerifyAcceptedCompletedAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Completed(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    "done",
                    GalateaDelegateInspectionSource.Persistent
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalCompleted,
            (await driver.PulseAsync()).Step);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDurableMailState.TerminalCompleted,
            snapshot.Mails.Single().State);
        Assert.Equal("done", snapshot.Notices.Single().Body);
    }

    private static async Task VerifyAcceptedNotFoundFinishesLocallyAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue(NotFound());
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            (await driver.PulseAsync()).Step);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDurableMailState.TerminalFailed,
            snapshot.Mails.Single().State);
        Assert.Single(snapshot.Notices);
        Assert.Equal(1, transport.StartCallCount);
        Assert.Equal("turn-1",
            transport.InspectRequests.Single().ExpectedTurnId);
    }

    private static async Task VerifyOutcomeUnknownUnavailableFinishesLocallyAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(static (_, _) => Task.FromException<
            GalateaDelegateTurnAccepted>(new IOException("lost")));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.AcceptedTurnNotVisible(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    GalateaDelegateInspectionSource.Persistent
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(
            transport,
            clock
        );
        _ = await driver.PulseAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            (await driver.PulseAsync()).Step);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDurableMailState.TerminalFailed,
            snapshot.Mails.Single().State);
        Assert.Single(snapshot.Notices);
        Assert.Equal(1, transport.StartCallCount);
        Assert.Null(transport.InspectRequests.Single().ExpectedTurnId);
    }

    private static async Task VerifyAcceptedFailedAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Failed(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    "CODE-X",
                    GalateaDelegateInspectionSource.Persistent
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            (await driver.PulseAsync()).Step);
        GalateaReplyNoticeSnapshot notice =
            fixture.Store.ReadSnapshot().Notices.Single();
        Assert.Equal(GalateaReplyNoticeKind.DeliveryFailure, notice.Kind);
        Assert.Equal(
            "外界代行者 Codex 未能处理这封信（阶段：inspect-dispatch；错误代码：CODE-X）。",
            notice.Body
        );
    }

    private static async Task VerifyOutcomeUnknownCompletedAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        var clock = new ManualTimeProvider();
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(static (_, _) => Task.FromException<
            GalateaDelegateTurnAccepted>(new IOException("lost")));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Completed(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    "recovered",
                    GalateaDelegateInspectionSource.Persistent
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(
            transport,
            clock
        );
        _ = await driver.PulseAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalCompleted,
            (await driver.PulseAsync()).Step);
    }

    private static async Task VerifyInvalidFinalAsync(
        string final,
        string expectedCode
    ) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Completed(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    final,
                    GalateaDelegateInspectionSource.Persistent
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            (await driver.PulseAsync()).Step);
        GalateaReplyNoticeSnapshot notice =
            fixture.Store.ReadSnapshot().Notices.Single();
        Assert.Equal(expectedCode, notice.Code);
        Assert.Equal(
            GalateaDelegationDurableContract.CreateDeliveryFailureNotice(
                "inspect-dispatch",
                expectedCode
            ),
            notice.Body
        );
    }

    private static async Task VerifyMalformedFailureCodeAsync(string code) {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Failed(
                    request.DispatchId,
                    request.ThreadId,
                    "turn-1",
                    code,
                    GalateaDelegateInspectionSource.Persistent
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        _ = await driver.PulseAsync();

        GalateaReplyNoticeSnapshot notice =
            fixture.Store.ReadSnapshot().Notices.Single();
        Assert.Equal("DELEGATE_FAILURE", notice.Code);
        Assert.Equal(
            GalateaDelegationDurableContract.CreateDeliveryFailureNotice(
                "inspect-dispatch",
                "DELEGATE_FAILURE"
            ),
            notice.Body
        );
        Assert.True(notice.Body.Length < 256);
    }

    private static async Task VerifyWrongEnsureIdentityAsync() {
        using var fixture = new DriverStore();
        await using var transport = new ScriptedTransport();
        transport.EnsureSteps.Enqueue(static (_, _) => Task.FromResult(
            new GalateaDelegateBindingEstablished(
                "ffffffffffffffffffffffffffffffff",
                "thread-1"
            )
        ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            (await driver.PulseAsync()).Step);
    }

    private static async Task VerifyWrongStartIdentityAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue((request, _) => Task.FromResult(
            new GalateaDelegateTurnAccepted(
                request.DispatchId + "-wrong",
                request.ThreadId,
                "turn-1"
            )
        ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            (await driver.PulseAsync()).Step);
    }

    private static async Task VerifyWrongInspectionIdentityAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Running(
                    request.DispatchId,
                    request.ThreadId,
                    "wrong-turn",
                    GalateaDelegateInspectionSource.Persistent
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            (await driver.PulseAsync()).Step);
    }

    private static async Task VerifyWrongInspectionBaseIdentityAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Running(
                    request.DispatchId + "-wrong",
                    request.ThreadId,
                    "turn-1",
                    GalateaDelegateInspectionSource.Persistent
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.TerminalFailed,
            (await driver.PulseAsync()).Step);
    }

    private static async Task VerifyBindingCancellationAsync() {
        using var fixture = new DriverStore();
        await using var transport = new ScriptedTransport();
        transport.EnsureSteps.Enqueue(static (_, _) => Task.FromCanceled<
            GalateaDelegateBindingEstablished>(new(true)));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.BindingDeferred,
            (await driver.PulseAsync()).Step);
        Assert.Equal("BINDING_CANCELLED",
            fixture.Store.ReadSnapshot().Mails.Single().RecoveryLastCode);
    }

    private static async Task VerifyStartCancellationAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.StartSteps.Enqueue(async (_, ct) => {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        using var cancellation = new CancellationTokenSource();
        Task<GalateaDurableDelegationPulseResult> pulse =
            driver.PulseAsync(cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulse);
        GalateaOutboundMailSnapshot mail = fixture.Store.ReadSnapshot().Mails.Single();
        Assert.Equal(GalateaDurableMailState.Started, mail.State);
        Assert.Equal(0, mail.RecoveryFailureCount);
        Assert.Null(mail.RecoveryLastCode);
    }

    private static async Task VerifyTransportSelfCancellationAsync() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue(static (_, _) => Task.FromCanceled<
            GalateaDelegateDispatchInspection>(new(true)));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.Backoff,
            (await driver.PulseAsync()).Step);
        GalateaOutboundMailSnapshot mail =
            fixture.Store.ReadSnapshot().Mails.Single();
        Assert.Equal(GalateaDurableMailState.Accepted, mail.State);
        Assert.Equal("INSPECTION_CANCELLED", mail.RecoveryLastCode);
    }

    private static Func<GalateaStartDelegateTurnRequest,
        CancellationToken, Task<GalateaDelegateTurnAccepted>> Accept(
        string turnId
    ) => (request, _) => Task.FromResult(
        new GalateaDelegateTurnAccepted(
            request.DispatchId,
            request.ThreadId,
            turnId
        )
    );

    private static Func<GalateaInspectDelegateDispatchRequest,
        CancellationToken, Task<GalateaDelegateDispatchInspection>>
        NotFound() => (request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.NotFound(
                    request.DispatchId,
                    request.ThreadId,
                    GalateaDelegateInspectionSource.Persistent
                )
            );

    private static Func<GalateaInspectDelegateDispatchRequest,
        CancellationToken, Task<GalateaDelegateDispatchInspection>>
        Running(
            string turnId,
            GalateaDelegateInspectionSource source =
                GalateaDelegateInspectionSource.Persistent
        ) => (request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Running(
                    request.DispatchId,
                    request.ThreadId,
                    turnId,
                    source
                )
            );

    private static void AssertBackoff(
        GalateaDelegationSqliteStore store,
        int attempt,
        long nextAt
    ) {
        GalateaOutboundMailSnapshot mail =
            store.ReadSnapshot().Mails.Single();
        Assert.Equal(attempt, mail.RecoveryFailureCount);
        Assert.Equal(nextAt, mail.NextRetryAtUnixTimeMilliseconds);
    }

    private sealed class DriverStore : IDisposable {
        private readonly OwnedDirectory _directory = new();
        private readonly GalateaDelegationStoreOwner _owner;
        private readonly GalateaDelegationStoreLimits _limits;

        internal DriverStore(
            IReadOnlyList<string>? bodies = null,
            int maximumTaskUtf8Bytes = 100_000,
            int maximumInboxReplies = 16,
            int maximumInboxUtf8Bytes = 16 * 1024,
            int maximumReplyUtf8Bytes = 1024
        ) {
            bodies ??= ["mail body"];
            _limits = new(
                MaximumQueuedMails: 32,
                maximumTaskUtf8Bytes,
                maximumReplyUtf8Bytes,
                maximumInboxReplies,
                maximumInboxUtf8Bytes
            );
            _owner = new("user", "repository-id");
            Store = GalateaDelegationSqliteStore.CreateNew(
                _directory.Path,
                _owner,
                Baseline(),
                _limits
            );
            CaptureBodies(100, bodies);
        }

        internal void CaptureBodies(
            int actionNumber,
            IReadOnlyList<string> bodies
        ) {
            _ = Store.CaptureActionBatch(new(
                Address(actionNumber),
                new string('a', 64),
                VisibleActionUtf8Bytes: 12,
                "extractor-contract-v1",
                bodies.Select(static body => new SendMailIntent(
                    GalateaDelegateConfigReader.CanonicalRecipient,
                    Subject: null,
                    body,
                    InReplyToMessageId: null,
                    EvidenceQuote: "sent it"
                )).ToArray()
            ));
        }

        internal GalateaDelegationSqliteStore Store { get; private set; }

        internal GalateaDurableDelegationDriver Driver(
            IGalateaDurableDelegateTransport transport,
            TimeProvider? clock = null
        ) => new(
            Store,
            transport,
            _directory.Path,
            clock,
            static () => BindingOperationId
        );

        internal GalateaRouteBindingSnapshot Bind(string threadId) {
            GalateaRouteBindingSnapshot route = Store.ReadSnapshot().Route;
            route = Store.BeginThreadBinding(
                BindingOperationId,
                route.Revision,
                Store.ReadSnapshot().Mails.First(mail => mail.State == GalateaDurableMailState.Queued).DispatchId,
                Store.ReadSnapshot().Mails.First(mail => mail.State == GalateaDurableMailState.Queued).Revision
            );
            return Store.CompleteThreadBinding(
                BindingOperationId,
                threadId,
                route.Revision
            );
        }

        internal GalateaOutboundMailSnapshot StartHead() {
            GalateaDelegationStateSnapshot snapshot = Store.ReadSnapshot();
            GalateaOutboundMailSnapshot mail = snapshot.Mails
                .First(candidate =>
                    candidate.State == GalateaDurableMailState.Queued);
            return Store.StartQueuedMail(
                mail.DispatchId,
                mail.Revision,
                snapshot.Route.Revision
            );
        }

        internal void Reopen() {
            Store.Dispose();
            Store = GalateaDelegationSqliteStore.OpenExisting(
                _directory.Path,
                _owner,
                _limits
            );
        }

        public void Dispose() {
            Store.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class ScriptedTransport :
        IGalateaDurableDelegateTransport {
        private int _externalCallCount;
        private int _activeCalls;
        private int _maximumConcurrentCalls;

        internal ConcurrentQueue<Func<GalateaEnsureDelegateBindingRequest,
            CancellationToken, Task<GalateaDelegateBindingEstablished>>>
            EnsureSteps { get; } = new();
        internal ConcurrentQueue<Func<GalateaStartDelegateTurnRequest,
            CancellationToken, Task<GalateaDelegateTurnAccepted>>>
            StartSteps { get; } = new();
        internal ConcurrentQueue<Func<GalateaInspectDelegateDispatchRequest,
            CancellationToken, Task<GalateaDelegateDispatchInspection>>>
            InspectSteps { get; } = new();
        internal ConcurrentQueue<GalateaEnsureDelegateBindingRequest>
            EnsureRequests { get; } = new();
        internal ConcurrentQueue<GalateaInspectDelegateDispatchRequest>
            InspectRequests { get; } = new();

        internal int ExternalCallCount => Volatile.Read(
            ref _externalCallCount);
        internal int EnsureCallCount { get; private set; }
        internal int StartCallCount { get; private set; }
        internal int InspectCallCount { get; private set; }
        internal int MaximumConcurrentCalls => Volatile.Read(
            ref _maximumConcurrentCalls);

        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request,
            CancellationToken ct
        ) {
            EnsureCallCount++;
            EnsureRequests.Enqueue(request);
            return Invoke(
                EnsureSteps,
                request,
                ct,
                "ensure-binding"
            );
        }

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request,
            CancellationToken ct
        ) {
            StartCallCount++;
            return Invoke(StartSteps, request, ct, "start-turn");
        }

        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request,
            CancellationToken ct
        ) {
            InspectCallCount++;
            InspectRequests.Enqueue(request);
            return Invoke(InspectSteps, request, ct, "inspect-dispatch");
        }

        private async Task<TResult> Invoke<TRequest, TResult>(
            ConcurrentQueue<Func<TRequest, CancellationToken, Task<TResult>>>
                steps,
            TRequest request,
            CancellationToken ct,
            string stage
        ) {
            if (!steps.TryDequeue(out Func<TRequest, CancellationToken,
                    Task<TResult>>? step)) {
                throw new InvalidOperationException(
                    $"Unexpected scripted transport call: {stage}."
                );
            }
            Interlocked.Increment(ref _externalCallCount);
            int active = Interlocked.Increment(ref _activeCalls);
            SetMaximum(active);
            try {
                return await step(request, ct);
            }
            finally {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private void SetMaximum(int candidate) {
            int current;
            do {
                current = Volatile.Read(ref _maximumConcurrentCalls);
                if (candidate <= current) { return; }
            } while (Interlocked.CompareExchange(
                ref _maximumConcurrentCalls,
                candidate,
                current
            ) != current);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider : TimeProvider {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration) {
            _utcNow = _utcNow.Add(duration);
            _timestamp += duration.Ticks;
        }

        internal void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }

    private sealed class OwnedDirectory : IDisposable {
        internal OwnedDirectory() {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-galatea-driver-" + Guid.NewGuid().ToString("N")
            );
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(Path);
        }

        internal string Path { get; }

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(Path);
    }

    private static GalateaDelegationStoreBaseline Baseline() {
        string selectedHead = Address(2);
        EventAddress address = EventAddressTextCodec.Parse(selectedHead);
        return new(
            new EventJournalPhysicalAppendFrontier(
                address.SegmentNumber,
                address.Ticket.EndOffsetExclusive
            ),
            selectedHead
        );
    }

    private static string Address(int value) =>
        $"ej1:{value:x16}0000000100000000";
}
