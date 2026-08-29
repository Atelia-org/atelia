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
        GalateaRouteBindingSnapshot route = fixture.Store.ReadSnapshot().Route;
        Assert.Equal(1, route.EnsureAttemptCount);
        Assert.Equal(1_000, route.NextEnsureAtUnixTimeMilliseconds);

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
    [InlineData("ensure-binding", "THREAD_NOT_FOUND", true)]
    [InlineData("future-stage", "FUTURE_CODE", true)]
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
                ? GalateaDurableDelegationPulseStep.Quarantined
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
        Assert.Equal("RECOVERED_STARTED", mail.ReconcileLastCode);
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
                    "turn-1"
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
                        request.ThreadId
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
    public async Task AcceptedNotFound_PreservesAcceptedThreadAndTurn() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue(NotFound());
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.InspectionNotFound,
            (await driver.PulseAsync()).Step);

        GalateaOutboundMailSnapshot mail =
            fixture.Store.ReadSnapshot().Mails.Single();
        Assert.Equal(GalateaDurableMailState.Accepted, mail.State);
        Assert.Equal("thread-1", mail.AcceptedThreadId);
        Assert.Equal("turn-1", mail.AcceptedTurnId);
        Assert.Equal(1, mail.ReconcileAttemptCount);
    }

    [Theory]
    [InlineData("inspect-dispatch", "INSPECTION_UNAVAILABLE", false)]
    [InlineData("protocol", "SIDECAR_WRITE_FAILED", false)]
    [InlineData("shutdown", "STOPPING", false)]
    [InlineData("start-turn", "THREAD_NOT_FOUND", true)]
    [InlineData("future-stage", "FUTURE_CODE", true)]
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
                ? GalateaDurableDelegationPulseStep.Quarantined
                : GalateaDurableDelegationPulseStep.Backoff,
            result.Step
        );
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

    [Fact]
    public async Task AmbiguousInspection_QuarantinesRouteAndMail() {
        using var fixture = new DriverStore();
        fixture.Bind("thread-1");
        await using var transport = new ScriptedTransport();
        transport.StartSteps.Enqueue(Accept("turn-1"));
        transport.InspectSteps.Enqueue((request, _) => Task.FromResult<
            GalateaDelegateDispatchInspection>(
                new GalateaDelegateDispatchInspection.Ambiguous(
                    request.DispatchId,
                    request.ThreadId,
                    "MULTIPLE_MATCHES"
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.Quarantined,
            (await driver.PulseAsync()).Step);

        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDelegationRouteState.Quarantined,
            snapshot.Route.State);
        Assert.Equal(GalateaDurableMailState.Quarantined,
            snapshot.Mails.Single().State);
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
    public async Task WrongEnsureStartAndInspectionIdentity_Quarantine() {
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
        await VerifyInspectionCancellationAsync();
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
                    "done"
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
                    "CODE-X"
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
                    "recovered"
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
                    final
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
                    code
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
        Assert.Equal(GalateaDurableDelegationPulseStep.Quarantined,
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
        Assert.Equal(GalateaDurableDelegationPulseStep.Quarantined,
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
                    "wrong-turn"
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.Quarantined,
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
                    "turn-1"
                )
            ));
        GalateaDurableDelegationDriver driver = fixture.Driver(transport);
        _ = await driver.PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.Quarantined,
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
            fixture.Store.ReadSnapshot().Route.EnsureLastCode);
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
        Assert.Equal(GalateaDurableDelegationPulseStep.MailOutcomeUnknown,
            (await pulse).Step);
        Assert.Equal("START_CANCELLED",
            fixture.Store.ReadSnapshot().Mails.Single().ReconcileLastCode);
    }

    private static async Task VerifyInspectionCancellationAsync() {
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
        Assert.Equal("INSPECTION_CANCELLED", mail.ReconcileLastCode);
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
                    request.ThreadId
                )
            );

    private static void AssertBackoff(
        GalateaDelegationSqliteStore store,
        int attempt,
        long nextAt
    ) {
        GalateaOutboundMailSnapshot mail =
            store.ReadSnapshot().Mails.Single();
        Assert.Equal(attempt, mail.ReconcileAttemptCount);
        Assert.Equal(nextAt, mail.NextReconcileAtUnixTimeMilliseconds);
    }

    private sealed class DriverStore : IDisposable {
        private readonly OwnedDirectory _directory = new();
        private readonly GalateaDelegationStoreOwner _owner;
        private readonly GalateaDelegationStoreLimits _limits;
        private readonly string _fingerprint;

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
            GalateaDelegateRouteConfig route = Route(_limits);
            _fingerprint = GalateaDelegationDurableContract
                .CreateRoutePolicyFingerprint(route);
            _owner = new("user", "repository-id", _fingerprint);
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
            ScriptedTransport transport,
            TimeProvider? clock = null
        ) => new(
            Store,
            transport,
            _fingerprint,
            clock,
            static () => BindingOperationId
        );

        internal GalateaRouteBindingSnapshot Bind(string threadId) {
            GalateaRouteBindingSnapshot route = Store.ReadSnapshot().Route;
            route = Store.BeginThreadBinding(
                BindingOperationId,
                route.Revision
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

        internal void Advance(TimeSpan duration) =>
            _utcNow = _utcNow.Add(duration);
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

    private static GalateaDelegateRouteConfig Route(
        GalateaDelegationStoreLimits limits
    ) => new(
        GalateaDelegateConfigReader.CanonicalRecipient,
        GalateaDelegateConfigReader.CodexAppServerKind,
        "/repos/focus/atelia",
        GalateaDelegateMode.Work,
        LocalCommandNetwork: true,
        new GalateaDelegateToolConfig(
            GalateaDelegateWebSearchMode.Live,
            ImageGeneration: true,
            ViewImage: true
        ),
        limits.MaximumQueuedMails,
        limits.MaximumTaskUtf8Bytes,
        limits.MaximumReplyUtf8Bytes,
        limits.MaximumInboxReplies,
        limits.MaximumInboxUtf8Bytes
    );

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
