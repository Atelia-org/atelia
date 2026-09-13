using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Real durable store, driver, process transport and reply consumption; no provider calls.</summary>
public sealed class GalateaBoundedRecoveryVerticalTests {
    [Fact]
    public async Task UnknownMail_ExhaustsPersistedBudget_ThenNextMailAndBothRepliesComplete() {
        using var sidecar = new GalateaSidecarProcessFixture(SidecarScript(cold: false));
        using var fixture = new Fixture(sidecar.Root, ["task A", "task B"]);
        await using var transport = sidecar.CreateClient();
        var clock = new ManualClock();
        GalateaDurableDelegationDriver driver = fixture.Driver(transport, clock);

        await PulseUntilAsync(driver, clock, () => fixture.Store.ReadSnapshot().Mails[0]
            .RecoveryFailureCount >= 3);
        GalateaOutboundMailSnapshot beforeRestart = fixture.Store.ReadSnapshot().Mails[0];
        Assert.Equal(GalateaDurableMailState.OutcomeUnknown, beforeRestart.State);
        Assert.Empty(fixture.Store.ReadSnapshot().Notices);
        fixture.Reopen();
        Assert.Equal(beforeRestart.RecoveryFailureCount,
            fixture.Store.ReadSnapshot().Mails[0].RecoveryFailureCount);
        driver = fixture.Driver(transport, clock);

        await PulseUntilAsync(driver, clock, () => fixture.Store.ReadSnapshot().Mails[0]
            .State == GalateaDurableMailState.TerminalFailed);
        GalateaDelegationStateSnapshot afterA = fixture.Store.ReadSnapshot();
        Assert.Equal(0, afterA.Mails[0].RecoveryFailureCount);
        Assert.Equal("RESULT_UNCONFIRMED", afterA.Mails[0].TerminalCode);
        Assert.Equal(GalateaDelegationRouteState.Unbound, afterA.Route.State);
        Assert.Null(afterA.Route.ActiveDispatchId);
        GalateaReplyNoticeSnapshot failed = Assert.Single(afterA.Notices);
        Assert.Equal(GalateaReplyNoticeKind.DeliveryFailure, failed.Kind);
        Assert.Equal("RESULT_UNCONFIRMED", failed.Code);
        Assert.Contains("结果", failed.Body, StringComparison.Ordinal);
        Assert.Contains("可能", failed.Body, StringComparison.Ordinal);
        // One uncertain start plus seven failed inspections exhaust the eight-failure budget.
        Assert.Equal(7, ReadFrames(sidecar.InputPath, "inspect-dispatch").Length);

        await PulseUntilAsync(driver, clock, () => fixture.Store.ReadSnapshot().Mails[1]
            .State == GalateaDurableMailState.TerminalCompleted);
        GalateaDelegationStateSnapshot completed = fixture.Store.ReadSnapshot();
        Assert.Equal("thread-2", completed.Route.ThreadId);
        Assert.Null(completed.Route.ActiveDispatchId);
        Assert.Equal(2, completed.Notices.Count);
        Assert.Equal(new long[] { 1, 2 }, completed.Notices.Select(value => value.CompletionSequence));
        Assert.Equal("B completed", completed.Notices[1].Body);
        string[] starts = ReadFrames(sidecar.InputPath, "start-turn");
        Assert.Equal(2, starts.Length);
        Assert.Single(starts, line => line.Contains(afterA.Mails[0].DispatchId, StringComparison.Ordinal));
        Assert.Equal(2, ReadFrames(sidecar.InputPath, "ensure-binding").Length);

        fixture.ConsumeBothNoticesAcrossReopen();
        Assert.Equal(GalateaDurableDelegationPulseStep.NoWork,
            (await fixture.Driver(transport, clock).PulseAsync()).Step);
    }

    [Fact]
    public async Task ColdEmptyThread_NotDispatched_RebindsSameMail_AndExecutesOnlyOnce() {
        using var sidecar = new GalateaSidecarProcessFixture(SidecarScript(cold: true));
        using var fixture = new Fixture(sidecar.Root, ["task A"]);
        var clock = new ManualClock();
        await using (var warmTransport = sidecar.CreateClient()) {
            GalateaDurableDelegationDriver warm = fixture.Driver(warmTransport, clock);
            await PulseUntilAsync(warm, clock, () => fixture.Store.ReadSnapshot().Route.State
                == GalateaDelegationRouteState.Bound);
            Assert.Empty(ReadFrames(sidecar.InputPath, "start-turn"));
        }
        fixture.Reopen();
        await using var coldTransport = sidecar.CreateClient();
        GalateaDurableDelegationDriver driver = fixture.Driver(coldTransport, clock);
        await PulseUntilAsync(driver, clock, () => fixture.Store.ReadSnapshot().Mails[0]
            .RecoveryFailureCount == 1);
        GalateaDelegationStateSnapshot retry = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDurableMailState.Queued, retry.Mails[0].State);
        Assert.Equal(GalateaDelegationRouteState.Unbound, retry.Route.State);
        Assert.Null(retry.Route.ActiveDispatchId);
        Assert.Empty(retry.Notices);

        await PulseUntilAsync(driver, clock, () => fixture.Store.ReadSnapshot().Mails[0]
            .State == GalateaDurableMailState.TerminalCompleted);
        Assert.Equal("thread-2", fixture.Store.ReadSnapshot().Route.ThreadId);
        Assert.Single(fixture.Store.ReadSnapshot().Notices);
        string[] starts = ReadFrames(sidecar.InputPath, "start-turn");
        Assert.Equal(2, starts.Length);
        Assert.All(starts, frame => Assert.Contains(retry.Mails[0].DispatchId, frame, StringComparison.Ordinal));
        // The first call failed before Codex dispatch. Only the second crosses the simulated execution boundary.
        Assert.Single(File.ReadAllLines(sidecar.EnvironmentPath));
        Assert.Equal(2, ReadFrames(sidecar.InputPath, "ensure-binding").Length);
    }

    private static async Task PulseUntilAsync(
        GalateaDurableDelegationDriver driver, ManualClock clock, Func<bool> reached
    ) {
        for (int pulse = 0; pulse < 50 && !reached(); pulse++) {
            await driver.PulseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromSeconds(61));
        }
        Assert.True(reached(), "Expected durable boundary was not reached within 50 pulses.");
    }

    private static string[] ReadFrames(string path, string type) => File.Exists(path)
        ? File.ReadAllLines(path).Where(line => {
            using JsonDocument frame = JsonDocument.Parse(line);
            return frame.RootElement.GetProperty("type").GetString() == type;
        }).ToArray()
        : [];

    private static string SidecarScript(bool cold) => $$"""
        printf '%s\n' '{"v":5,"type":"ready"}'
        bindings=0
        if [ -f {{GalateaSidecarProcessFixture.ShellQuote("COUNT")}} ]; then
          bindings=$(cat {{GalateaSidecarProcessFixture.ShellQuote("COUNT")}})
        fi
        while IFS= read -r line; do
          printf '%s\n' "$line" >> {{GalateaSidecarProcessFixture.ShellQuote("INPUT")}}
          request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
          dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
          thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
          case "$line" in
            *'"type":"ensure-binding"'*)
              bindings=$((bindings + 1))
              printf '%s' "$bindings" > {{GalateaSidecarProcessFixture.ShellQuote("COUNT")}}
              binding_id=$(printf '%s' "$line" | sed -n 's/.*"bindingOperationId":"\([^"]*\)".*/\1/p')
              printf '{"v":5,"type":"binding-established","requestId":"%s","bindingOperationId":"%s","threadId":"thread-%s"}\n' "$request_id" "$binding_id" "$bindings"
              ;;
            *'"type":"start-turn"'*)
              if [ "$thread_id" = thread-1 ]; then
                printf '{"v":5,"type":"failed","stage":"start-turn","requestId":"%s","dispatchId":"%s","threadId":"%s","code":"{{(cold ? "THREAD_NOT_FOUND" : "START_OUTCOME_UNKNOWN")}}","dispatchState":"{{(cold ? "not-dispatched" : "may-have-dispatched")}}"}\n' "$request_id" "$dispatch_id" "$thread_id"
              else
                printf '%s\n' "$dispatch_id" >> {{GalateaSidecarProcessFixture.ShellQuote("ENV")}}
                printf '{"v":5,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"%s","turnId":"turn-2"}\n' "$request_id" "$dispatch_id" "$thread_id"
              fi
              ;;
            *'"type":"inspect-dispatch"'*)
              if [ "$thread_id" = thread-1 ]; then
                printf '{"v":5,"type":"failed","stage":"inspect-dispatch","requestId":"%s","dispatchId":"%s","threadId":"%s","code":"INSPECTION_UNAVAILABLE"}\n' "$request_id" "$dispatch_id" "$thread_id"
              else
                printf '{"v":5,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"completed","turnId":"turn-2","final":"B completed","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
              fi
              ;;
          esac
        done
        """;

    private sealed class Fixture : IDisposable {
        private readonly string _storePath;
        private readonly string _home;
        private readonly GalateaDelegationStoreOwner _owner;
        private readonly GalateaDelegationStoreLimits _limits = new(32, 8_000, 8_000, 16, 128_000);

        internal Fixture(string root, IReadOnlyList<string> bodies) {
            _home = root;
            Engine = SessionJournalEngine.Create(Path.Combine(root, "session"),
                new SessionCreateOptions("model-a", "system-a", "surface-a"));
            _owner = new("user", Engine.Path);
            _storePath = Path.Combine(root, "delegation");
            Store = GalateaDelegationSqliteStore.CreateNew(_storePath, _owner,
                new(Engine.ReadView.ReadPhysicalAppendFrontier(),
                    EventAddressTextCodec.FormatNullable(Engine.ReadCurrentHead())), _limits);
            Store.CaptureActionBatch(new("ej1:00000000000010010000000100000000",
                new string('a', 64), 12, "extractor-contract-v1",
                bodies.Select(body => new SendMailIntent("Codex", null, body, null, "evidence")).ToArray()));
        }

        internal SessionJournalEngine Engine { get; }
        internal GalateaDelegationSqliteStore Store { get; private set; }
        internal GalateaDurableDelegationDriver Driver(IGalateaDurableDelegateTransport transport, TimeProvider clock)
            => new(Store, transport, _home, clock);

        internal void Reopen() {
            Store.Dispose();
            Store = GalateaDelegationSqliteStore.OpenExisting(_storePath, _owner, _limits);
        }

        internal void ConsumeBothNoticesAcrossReopen() {
            var reconciler = new GalateaDurableReplyLeaseReconciler(Store);
            GalateaDurableReplyLease lease = Assert.IsType<GalateaDurableReplyLeaseBeginResult.Created>(
                reconciler.BeginCutoff(PlayerTurnObservationEnvelope.DelegateReplyLeasePlayerTextDiscriminator)).Lease;
            Assert.Collection(lease.ReadNotices(),
                notice => Assert.IsType<PlayerTurnNotice.DeliveryFailure>(notice),
                notice => Assert.IsType<PlayerTurnNotice.Reply>(notice));
            string rendered = PlayerTurnObservationEnvelope.Wrap(PlayerTurnObservation.CreateDelegateReply(
                DateTimeOffset.UnixEpoch, lease.ReadNotices()));
            lease.BindObservationBase(Engine, Engine.ReadCurrentHead()!.Value, rendered);
            lease.RecordObservationCommitted(Engine.AppendObservation(rendered));
            Reopen();
            reconciler = new(Store);
            Assert.IsType<GalateaDurableReplyLeaseReconcileResult.Retained>(reconciler.ReconcileActiveLease(Engine));
            Engine.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text("received both notices")]),
                new CompletionDescriptor("import", "legacy-import-v1", "model-a"));
            Assert.IsType<GalateaDurableReplyLeaseReconcileResult.Consumed>(reconciler.ReconcileActiveLease(Engine));
            Assert.All(Store.ReadSnapshot().Notices, notice => Assert.Equal(GalateaReplyNoticeState.Consumed, notice.State));
            Assert.IsType<GalateaDurableReplyLeaseReconcileResult.None>(reconciler.ReconcileActiveLease(Engine));
            Assert.IsType<GalateaDurableReplyLeaseBeginResult.Empty>(
                reconciler.BeginCutoff(PlayerTurnObservationEnvelope.DelegateReplyLeasePlayerTextDiscriminator));
        }

        public void Dispose() {
            Store.Dispose();
            Engine.Dispose();
        }
    }

    private sealed class ManualClock : TimeProvider {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
