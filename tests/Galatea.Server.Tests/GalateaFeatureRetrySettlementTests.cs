using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaFeatureRetrySettlementTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string NoteText = "Remember the blue door.";
    private const string VisibleAction = "[Galatea] I submitted a Note save request:\nRemember the blue door.\nI sent Codex the letter:\ninspect the blue door\nSending is complete.";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TemporaryFeatureFailure_RetriesThenSettlesCommittedActionOnceAcrossColdReopen(bool note) {
        var clock = new RetryClock();
        var provider = new FeatureClient(note);
        var transport = new CountingTransport();
        var main = new CompletionConnectionConfig("test", "openai-chat", "model-a", "openai-chat/strict",
            "http://localhost:8000/", ApiKey: "synthetic-key");
        var helper = main with { Id = "helper", ModelId = "helper-model" };
        await using var first = GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: false, connections: [main, helper], connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: note ? null : helper.Id,
            characterNoteExtractorConnectionId: note ? helper.Id : null,
            delegateTransport: transport, timeProvider: clock);
        var service = first.Factory.Services.GetRequiredService<GalateaHostService>();
        var runner = first.Factory.Services.GetRequiredService<GalateaAcceptedTurnRunner>();
        var session = await service.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        var turn = service.StartTurn(session, "save and send", new("test"), GalateaDelegateTestConfiguration.PlayerSender);
        Task running = runner.Start(session, turn);

        // Wait for the actual production decorator's backoff timer, not merely
        // the first exception. The main Action must already be durable here.
        await clock.BackoffScheduled.Task.WaitAsync(Deadline);
        EventAddress action = session.Engine.ReadCurrentHead()!.Value;
        Assert.Equal(SessionExecutionPhase.Idle, session.Engine.InspectExecutionBoundary().Phase);
        Assert.Equal(VisibleAction, Assert.Single(session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns)
            .TerminalAction!.Message.GetFlattenedText());
        Assert.False(running.IsCompleted);
        Assert.Equal(1, provider.MainCalls);
        Assert.Equal(1, provider.FeatureCalls);
        if (note) {
            Assert.Null(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
        }
        else {
            Assert.Empty(session.DelegationHandle!.Store.ReadSnapshot().Captures);
            Assert.Equal(0, transport.Starts);
        }

        clock.Advance(TimeSpan.FromSeconds(7));
        await running.WaitAsync(Deadline);
        Assert.Equal("completed", turn.Status);
        Assert.Equal(action, session.Engine.ReadCurrentHead());
        Assert.Equal(1, provider.MainCalls);
        Assert.Equal(3, provider.FeatureCalls);
        Assert.Same(provider.FirstFeatureRequest, provider.RetriedFeatureRequest);

        string? receiptSource = null;
        long? receiptRevision = null;
        string? dispatchId = null;
        if (note) {
            var receipt = Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
                session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            receiptSource = receipt.SourceActionAddress;
            receiptRevision = receipt.StateRevision;
            Assert.Equal(EventAddressTextCodec.Format(action), receiptSource);
            AssertSingleNote(session);
        }
        else {
            await WaitUntilAsync(() => {
                _ = session.DelegationHandle!.Signal();
                return session.RequireDelegationHandle().Store.ReadSnapshot().Notices.Count == 1;
            });
            var snapshot = session.DelegationHandle!.Store.ReadSnapshot();
            Assert.Equal(EventAddressTextCodec.Format(action), Assert.Single(snapshot.Captures).SourceActionAddress);
            dispatchId = Assert.Single(snapshot.Mails).DispatchId;
            Assert.Equal(1, transport.Starts);
        }

        await ReconcileAsync(service, session);
        Assert.Equal(3, provider.FeatureCalls);
        await first.DisposeAsync();
        AssertMainJournal(first.SessionDirectory);

        // A fresh host/client owns the same on-disk Journal and SQLite files.
        // DerivedInfo is a separate pump: its optional calls are not Note extraction.
        var reopenedProvider = new FeatureClient(note, forbidMainAndFeature: true);
        await using var reopened = first.CreateRestarted(reopenedProvider,
            DisabledGalateaUserMessageNormalizer.Instance, transport, deleteFilesOnDispose: false);
        var reopenedService = reopened.Factory.Services.GetRequiredService<GalateaHostService>();
        var restored = await reopenedService.GetSessionAsync("alice", CancellationToken.None);
        await ReconcileAsync(reopenedService, restored);
        await ReconcileAsync(reopenedService, restored);
        Assert.Equal(action, restored.Engine.ReadCurrentHead());
        Assert.Equal(0, reopenedProvider.MainCalls);
        Assert.Equal(0, reopenedProvider.FeatureCalls);
        if (note) {
            AssertSingleNote(restored);
            var receipt = Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
                restored.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            Assert.Equal(receiptSource, receipt.SourceActionAddress);
            Assert.Equal(receiptRevision, receipt.StateRevision);
            Assert.Equal(0, transport.Starts);
        }
        else {
            var snapshot = restored.DelegationHandle!.Store.ReadSnapshot();
            Assert.Single(snapshot.Captures);
            Assert.Equal(dispatchId, Assert.Single(snapshot.Mails).DispatchId);
            Assert.Single(snapshot.Notices);
            Assert.Equal(1, transport.Starts);
        }
        await reopened.DisposeAsync();
        AssertMainJournal(reopened.SessionDirectory);
        TestDirectorySafety.DeleteOwnedTreeNoFollow(reopened.RootDirectory);
    }

    private static async Task ReconcileAsync(GalateaHostService service, CharacterSessionHost session) {
        await session.TurnLock.WaitAsync();
        try { await service.ReconcileDurableAdmissionAsync(session, CancellationToken.None); }
        finally { session.TurnLock.Release(); }
    }

    private static void AssertSingleNote(CharacterSessionHost session) {
        var pod = global::Atelia.MemoPod.MemoPod.Open(session.Character.CharacterMemoryStateDir, CharacterNoteDefaultPodV1.PodId);
        Assert.Equal(NoteText, Assert.Single(pod.List()).ExactText);
    }

    private static void AssertMainJournal(string repository) {
        using var engine = SessionJournalEngine.OpenReadOnly(repository);
        var events = new List<SessionJournalAuditEvent>();
        engine.ScanCheckedAuditEvents(events.Add);
        Assert.Single(events, item => item.Kind == SessionEventKind.ObservationAccepted);
        Assert.Single(events, item => item.Kind == SessionEventKind.CompletionRequestPrepared);
        Assert.Single(events, item => item.Kind == SessionEventKind.AgentActionProduced);
        Assert.DoesNotContain(events, item => item.Kind is SessionEventKind.CompletionAttemptStarted or SessionEventKind.CompletionAttemptFailed);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!predicate()) { await Task.Delay(10, deadline.Token); }
    }

    private sealed class FeatureClient(bool note, bool forbidMainAndFeature = false) : ICompletionClient, ICompletionClientFactory {
        private int _mainCalls;
        private int _featureCalls;
        public string Name => "feature-settlement-test";
        public string ApiSpecId => "openai-chat/strict";
        internal int MainCalls => Volatile.Read(ref _mainCalls);
        internal int FeatureCalls => Volatile.Read(ref _featureCalls);
        internal CompletionRequest? FirstFeatureRequest { get; private set; }
        internal CompletionRequest? RetriedFeatureRequest { get; private set; }
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            string toolName = note ? CharacterNoteExtractor.ToolName : OutboundMailExtractor.ToolName;
            ActionMessage message;
            if (request.PromptPrefix.OutputContract.Tools.Any(tool => tool.Name == toolName)) {
                Assert.False(forbidMainAndFeature, "Cold reconciliation must not re-extract a captured Action.");
                int attempt = Interlocked.Increment(ref _featureCalls);
                FirstFeatureRequest ??= request;
                if (attempt == 1) {
                    throw new CompletionFailureException(new(CompletionFailureKind.Http, 503), "temporary synthetic failure");
                }
                if (attempt == 2) {
                    RetriedFeatureRequest = request;
                    string arguments = note ? JsonSerializer.Serialize(new { textStartLine = 2, textEndLine = 2 })
                        : JsonSerializer.Serialize(new { recipient = "Codex", bodyStartLine = 4, bodyEndLine = 4,
                            evidenceStartLine = 5, evidenceEndLine = 5 });
                    message = new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(toolName, "artifact", arguments))]);
                }
                else {
                    Assert.Equal(3, attempt);
                    Assert.Contains(request.TailMessages, item => item is ActionMessage);
                    message = new ActionMessage([]);
                }
            }
            else if (request.ModelId == "model-a") {
                Assert.False(forbidMainAndFeature, "Cold reconciliation must not regenerate the main Action.");
                Assert.Equal(1, Interlocked.Increment(ref _mainCalls));
                message = new ActionMessage([new ActionBlock.Text(VisibleAction)]);
            }
            else {
                // Leave optional DerivedInfo Pending; it is not part of this
                // extraction/capture test and must not mutate the saved ExactText.
                message = new ActionMessage([]);
            }
            return Task.FromResult(new CompletionResult(message, CompletionDescriptor.From(this, request)));
        }
    }

    private sealed class CountingTransport : IGalateaDurableDelegateTransport {
        private int _starts;
        internal int Starts => Volatile.Read(ref _starts);
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(GalateaEnsureDelegateBindingRequest request, CancellationToken ct) =>
            Task.FromResult(new GalateaDelegateBindingEstablished(request.BindingOperationId, "thread-feature"));
        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(GalateaStartDelegateTurnRequest request, CancellationToken ct) {
            Assert.Equal(1, Interlocked.Increment(ref _starts));
            return Task.FromResult(new GalateaDelegateTurnAccepted(request.DispatchId, request.ThreadId, "turn-feature"));
        }
        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(GalateaInspectDelegateDispatchRequest request, CancellationToken ct) =>
            Task.FromResult<GalateaDelegateDispatchInspection>(new GalateaDelegateDispatchInspection.Completed(
                request.DispatchId, request.ThreadId, "turn-feature", "inspection complete", GalateaDelegateInspectionSource.Persistent));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RetryClock : TimeProvider {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private long _ticks;
        internal TaskCompletionSource BackoffScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref _ticks));
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            lock (_gate) {
                var timer = new Timer(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                if (dueTime >= TimeSpan.FromSeconds(5) && dueTime <= TimeSpan.FromSeconds(6)) { BackoffScheduled.TrySetResult(); }
                return timer;
            }
        }
        internal void Advance(TimeSpan elapsed) {
            Timer[] due;
            lock (_gate) {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in due) {
                    timer.Due = timer.Period > TimeSpan.Zero ? _ticks + timer.Period.Ticks : long.MaxValue;
                }
            }
            foreach (var timer in due) { timer.Callback(timer.State); }
        }
        private sealed class Timer(RetryClock clock, TimerCallback callback, object? state) : ITimer {
            internal TimerCallback Callback => callback;
            internal object? State => state;
            internal long Due;
            internal TimeSpan Period;
            public bool Change(TimeSpan dueTime, TimeSpan period) {
                lock (clock._gate) {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                    Period = period;
                    return true;
                }
            }
            public void Dispose() { lock (clock._gate) { clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
