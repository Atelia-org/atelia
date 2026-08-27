using System.Collections.Concurrent;
using System.Threading.Channels;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationCoordinatorTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Unmatched_IsSideEffectFree_AndBatchDedupeIsAtomic() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        EventAddress head = Head(1);

        Assert.True(coordinator.TryCaptureBatch(
            "turn-1",
            head,
            [Mail("Alice", "first"), Mail("Bob", "second")]
        ));
        Assert.False(coordinator.TryCaptureBatch(
            "turn-duplicate",
            head,
            [Mail("Codex", "must not be admitted")]
        ));

        Assert.Equal(2, coordinator.Snapshot().Count);
        Assert.All(coordinator.Snapshot(), static candidate =>
            Assert.Equal(
                GalateaDelegateCandidateState.Unrouted,
                candidate.State
            )
        );
        Assert.Equal(0, sidecar.StartCount);
    }

    [Fact]
    public async Task DispatchId_IsStableVersionedAndOrdinalSensitive() {
        EventAddress head = Head(1);
        string first = GalateaDelegationCoordinator.CreateDispatchId(
            "alice",
            head,
            0
        );

        Assert.Equal(
            "gd1-281b3cc441d4e4a41136140beadda9674315f51eb8fefc174321897c804ae6c7",
            first
        );
        Assert.Equal(
            first,
            GalateaDelegationCoordinator.CreateDispatchId(
                "alice",
                head,
                0
            )
        );
        Assert.NotEqual(
            first,
            GalateaDelegationCoordinator.CreateDispatchId(
                "alice",
                head,
                1
            )
        );
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MultipleMails_AreFifoSingleActiveAndReuseFixedThread() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        Assert.True(coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [
                Mail("Codex", "body one", subject: "never forwarded"),
                Mail("Codex", "body two", replyId: "0123456789abcdef0123456789abcdef")
            ]
        ));

        FakeCall first = await sidecar.NextCallAsync();
        Assert.Null(first.Request.ThreadId);
        Assert.Equal("body one", first.Request.Body);
        Assert.Equal(1, sidecar.StartCount);
        Assert.Equal(1, sidecar.MaximumActiveCount);
        first.Accept("thread-fixed", "codex-turn-1");
        await WaitForStateAsync(
            coordinator,
            0,
            GalateaDelegateCandidateState.Running
        );
        Assert.False(sidecar.TryReadCall(out _));
        first.Complete("reply one");

        FakeCall second = await sidecar.NextCallAsync();
        Assert.Equal("thread-fixed", second.Request.ThreadId);
        Assert.Equal("body two", second.Request.Body);
        Assert.Equal(1, sidecar.MaximumActiveCount);
        second.Accept("thread-fixed", "codex-turn-2");
        second.Complete("reply two");
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        Assert.Equal("thread-fixed", coordinator.BoundThreadIdForTest);
        using GalateaDelegationCoordinator.GalateaReadyReplyLease lease =
            coordinator.BeginReadyReplyCutoff();
        Assert.Equal(
            ["reply one", "reply two"],
            lease.Notices.Select(static notice => notice.Body)
        );
        Assert.Equal([1L, 2L], coordinator.Snapshot()
            .Select(static candidate => candidate.CompletionSequence));
        lease.Commit();
    }

    [Fact]
    public async Task AcceptedThreadMismatch_QuarantinesRouteWithoutSecondSideEffect() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [Mail("Codex", "one")]
        );
        FakeCall first = await sidecar.NextCallAsync();
        first.Accept("unexpected-thread", "turn-a");
        first.Complete("unused");
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        Assert.False(coordinator.IsQuarantinedForTest);
        Assert.Equal("unexpected-thread", coordinator.BoundThreadIdForTest);
        // First dispatch legitimately establishes the binding. Exercise a
        // mismatch on a later accepted continuation.
        coordinator.TryCaptureBatch(
            "turn-2",
            Head(2),
            [Mail("Codex", "three"), Mail("Codex", "four")]
        );
        FakeCall mismatch = await sidecar.NextCallAsync();
        Assert.Equal("unexpected-thread", mismatch.Request.ThreadId);
        mismatch.Accept("wrong-thread", "turn-b");
        mismatch.Complete("unused");
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        Assert.True(coordinator.IsQuarantinedForTest);
        Assert.Equal(2, sidecar.StartCount);
        Assert.Equal(
            2,
            coordinator.Snapshot().Count(static candidate =>
                candidate.State
                    == GalateaDelegateCandidateState.FailureReady)
        );
    }

    [Fact]
    public async Task TerminalMismatch_QuarantinesAndDoesNotOverwriteBinding() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [Mail("Codex", "one"), Mail("Codex", "two")]
        );
        FakeCall first = await sidecar.NextCallAsync();
        first.Accept("thread-fixed", "turn-a");
        first.SetTerminal(new GalateaDelegateTerminal.Completed(
            first.Request.DispatchId,
            "wrong-thread",
            "turn-a",
            "must not arrive"
        ));
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        Assert.True(coordinator.IsQuarantinedForTest);
        Assert.Equal("thread-fixed", coordinator.BoundThreadIdForTest);
        Assert.Equal(1, sidecar.StartCount);
        Assert.All(coordinator.Snapshot(), static candidate =>
            Assert.Equal(
                GalateaDelegateCandidateState.FailureReady,
                candidate.State
            )
        );
    }

    [Theory]
    [InlineData("start", "START_OUTCOME_UNKNOWN")]
    [InlineData("start", "PROCESS_EXIT")]
    public async Task StartFailure_IsOneShotDeliveryFailure(
        string stage,
        string code
    ) {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [Mail("Codex", "one")]
        );
        FakeCall call = await sidecar.NextCallAsync();
        call.Reject(stage, code);
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        Assert.Equal(1, sidecar.StartCount);
        using GalateaDelegationCoordinator.GalateaReadyReplyLease lease =
            coordinator.BeginReadyReplyCutoff();
        GalateaReadyNotice.DeliveryFailure failure = Assert.IsType<
            GalateaReadyNotice.DeliveryFailure>(Assert.Single(lease.Notices));
        Assert.Contains(code, failure.Body, StringComparison.Ordinal);
        lease.Commit();
        Assert.Empty(coordinator.BeginReadyReplyCutoff().Notices);
    }

    [Fact]
    public async Task TerminalFailureAndInvalidFinalBecomeBoundedFailures() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(
            sidecar,
            maximumReplyUtf8Bytes: 4
        );
        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [
                Mail("Codex", "one"),
                Mail("Codex", "two"),
                Mail("Codex", "three")
            ]
        );

        FakeCall failed = await sidecar.NextCallAsync();
        failed.Accept("thread-fixed", "turn-1");
        failed.Fail("turn", "PROVIDER_FAILED");
        FakeCall blank = await sidecar.NextCallAsync();
        blank.Accept("thread-fixed", "turn-2");
        blank.Complete("  ");
        FakeCall oversized = await sidecar.NextCallAsync();
        oversized.Accept("thread-fixed", "turn-3");
        oversized.Complete("12345");
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        using GalateaDelegationCoordinator.GalateaReadyReplyLease lease =
            coordinator.BeginReadyReplyCutoff();
        Assert.Equal(3, lease.Notices.Count);
        Assert.All(lease.Notices, static notice =>
            Assert.IsType<GalateaReadyNotice.DeliveryFailure>(notice));
        Assert.Contains("PROVIDER_FAILED", lease.Notices[0].Body);
        Assert.Contains("FINAL_BLANK", lease.Notices[1].Body);
        Assert.Contains("FINAL_TOO_LARGE", lease.Notices[2].Body);
    }

    [Fact]
    public async Task FaultedCompletionBecomesDeliveryFailure() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [Mail("Codex", "one")]
        );
        FakeCall call = await sidecar.NextCallAsync();
        call.Accept("thread-fixed", "turn-1");
        call.FaultCompletion(new IOException("process exited"));
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        using GalateaDelegationCoordinator.GalateaReadyReplyLease lease =
            coordinator.BeginReadyReplyCutoff();
        Assert.Contains(
            "SIDECAR_COMPLETION_FAILED",
            Assert.Single(lease.Notices).Body
        );
    }

    [Fact]
    public async Task TaskLimitFailsBeforeSidecarAndDoesNotForwardMetadata() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(
            sidecar,
            maximumTaskUtf8Bytes: 3
        );
        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [Mail("Codex", "four", subject: "secret subject")]
        );
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        Assert.Equal(0, sidecar.StartCount);
        using GalateaDelegationCoordinator.GalateaReadyReplyLease lease =
            coordinator.BeginReadyReplyCutoff();
        Assert.Contains("TASK_INVALID_OR_TOO_LARGE",
            Assert.Single(lease.Notices).Body);
    }

    [Fact]
    public async Task LeaseCutoffRollbackCommitAndOneShotAreStrict() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        await CompleteOneAsync(coordinator, sidecar, Head(1), "reply one");

        GalateaDelegationCoordinator.GalateaReadyReplyLease first =
            coordinator.BeginReadyReplyCutoff();
        Assert.Equal("reply one", Assert.Single(first.Notices).Body);
        Assert.Throws<InvalidOperationException>(
            coordinator.BeginReadyReplyCutoff
        );
        first.Rollback();

        GalateaDelegationCoordinator.GalateaReadyReplyLease second =
            coordinator.BeginReadyReplyCutoff();
        Assert.Equal("reply one", Assert.Single(second.Notices).Body);
        second.Commit();
        using GalateaDelegationCoordinator.GalateaReadyReplyLease empty =
            coordinator.BeginReadyReplyCutoff();
        Assert.Empty(empty.Notices);
        empty.Commit();
        Assert.Equal(
            GalateaDelegateCandidateState.Consumed,
            Assert.Single(coordinator.Snapshot()).State
        );
    }

    [Fact]
    public async Task CutoffDoesNotClaimCompletionsThatBecomeReadyLater() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        await CompleteOneAsync(coordinator, sidecar, Head(1), "reply one");
        GalateaDelegationCoordinator.GalateaReadyReplyLease cutoff =
            coordinator.BeginReadyReplyCutoff();

        coordinator.TryCaptureBatch(
            "turn-2",
            Head(2),
            [Mail("Codex", "two")]
        );
        FakeCall later = await sidecar.NextCallAsync();
        later.Accept("thread-fixed", "turn-2");
        later.Complete("reply two");
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        Assert.Equal("reply one", Assert.Single(cutoff.Notices).Body);
        cutoff.Commit();
        using GalateaDelegationCoordinator.GalateaReadyReplyLease next =
            coordinator.BeginReadyReplyCutoff();
        Assert.Equal("reply two", Assert.Single(next.Notices).Body);
    }

    [Fact]
    public async Task CutoffLeasesOnlyExactRenderableFifoPrefix() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(
            sidecar,
            maximumReplyUtf8Bytes:
                GalateaPlayerObservationEnvelope.MaximumReplyUtf8Bytes,
            maximumInboxUtf8Bytes: 1_000_000
        );
        string fenceHeavyReply = new('~', 250_000);
        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [Mail("Codex", "one"), Mail("Codex", "two")]
        );
        FakeCall first = await sidecar.NextCallAsync();
        first.Accept("thread-fixed", "turn-1");
        first.Complete(fenceHeavyReply);
        FakeCall second = await sidecar.NextCallAsync();
        second.Accept("thread-fixed", "turn-2");
        second.Complete(fenceHeavyReply);
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        string fenceHeavyPlayerText = new('~', 60_000);
        GalateaDelegationCoordinator.GalateaReadyReplyLease prefix =
            coordinator.BeginReadyReplyCutoff(fenceHeavyPlayerText);
        Assert.Single(prefix.Notices);
        prefix.Commit();
        using GalateaDelegationCoordinator.GalateaReadyReplyLease rest =
            coordinator.BeginReadyReplyCutoff(fenceHeavyPlayerText);
        Assert.Single(rest.Notices);
    }

    [Fact]
    public async Task CutoffSplitsMoreThanSixteenReadyRepliesIntoFifoRounds() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(
            sidecar,
            maximumQueuedMails: 32,
            maximumInboxReplies: 32
        );
        Assert.True(coordinator.TryCaptureBatch(
            "turn-many",
            Head(20),
            Enumerable.Range(0, 17)
                .Select(index => Mail("Codex", $"task-{index}"))
                .ToArray()
        ));
        for (int index = 0; index < 17; index++) {
            FakeCall call = await sidecar.NextCallAsync();
            call.Accept("thread-fixed", $"codex-turn-{index}");
            call.Complete($"reply-{index}");
        }
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        GalateaDelegationCoordinator.GalateaReadyReplyLease first =
            coordinator.BeginReadyReplyCutoff("player");
        Assert.Equal(
            Enumerable.Range(0, 16).Select(index => $"reply-{index}"),
            first.Notices.Select(static notice => notice.Body)
        );
        first.Commit();

        using GalateaDelegationCoordinator.GalateaReadyReplyLease second =
            coordinator.BeginReadyReplyCutoff("player");
        Assert.Equal(
            "reply-16",
            Assert.Single(second.Notices).Body
        );
    }

    [Fact]
    public async Task FullInboxBackpressuresTerminalUntilLeaseCommits() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(
            sidecar,
            maximumInboxReplies: 1
        );
        await CompleteOneAsync(coordinator, sidecar, Head(1), "reply one");
        coordinator.TryCaptureBatch(
            "turn-2",
            Head(2),
            [Mail("Codex", "two")]
        );
        FakeCall second = await sidecar.NextCallAsync();
        second.Accept("thread-fixed", "turn-2");
        second.Complete("reply two");
        await WaitForStateAsync(
            coordinator,
            1,
            GalateaDelegateCandidateState.Running
        );

        GalateaDelegationCoordinator.GalateaReadyReplyLease first =
            coordinator.BeginReadyReplyCutoff();
        Assert.Equal("reply one", Assert.Single(first.Notices).Body);
        first.Commit();
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);
        using GalateaDelegationCoordinator.GalateaReadyReplyLease next =
            coordinator.BeginReadyReplyCutoff();
        Assert.Equal("reply two", Assert.Single(next.Notices).Body);
    }

    [Fact]
    public async Task CaptureDuringReleasedButIncompletePumpStartsSuccessor() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        var oldPumpReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var allowOldPumpReturn = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int hookInvocation = 0;
        coordinator.TestHooksForTest = new() {
            AfterPumpOwnershipReleasedBeforeReturn = async () => {
                if (Interlocked.Increment(ref hookInvocation) != 1) {
                    return;
                }
                oldPumpReleased.SetResult();
                await allowOldPumpReturn.Task.ConfigureAwait(false);
            }
        };

        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [Mail("Codex", "one")]
        );
        Task oldPump = coordinator.PumpTaskForTest;
        FakeCall first = await sidecar.NextCallAsync();
        first.Accept("thread-fixed", "turn-1");
        first.Complete("reply one");
        await oldPumpReleased.Task.WaitAsync(Deadline);
        Assert.False(oldPump.IsCompleted);

        coordinator.TryCaptureBatch(
            "turn-2",
            Head(2),
            [Mail("Codex", "two")]
        );
        FakeCall second = await sidecar.NextCallAsync();
        Assert.Equal("thread-fixed", second.Request.ThreadId);
        second.Accept("thread-fixed", "turn-2");
        second.Complete("reply two");

        allowOldPumpReturn.SetResult();
        await oldPump.WaitAsync(Deadline);
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);
        Assert.Equal(2, sidecar.StartCount);
        Assert.All(coordinator.Snapshot(), static candidate =>
            Assert.Equal(
                GalateaDelegateCandidateState.ReplyReady,
                candidate.State
            )
        );
    }

    [Fact]
    public async Task UndoQueuedWinsGate_WhileStartingRunningAndReadyContinue() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        EventAddress head = Head(1);
        coordinator.TryCaptureBatch(
            "turn-1",
            head,
            [Mail("Codex", "one"), Mail("Codex", "two")]
        );
        FakeCall first = await sidecar.NextCallAsync();

        coordinator.RetractSourceAction(head);
        GalateaDelegateCandidateSnapshot[] retracted = [..
            coordinator.Snapshot()
        ];
        Assert.True(retracted[0].SourceRetracted);
        Assert.Equal(
            GalateaDelegateCandidateState.RetractedBeforeDispatch,
            retracted[1].State
        );
        first.Accept("thread-fixed", "turn-1");
        first.Complete("reply survives Undo");
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);
        Assert.Equal(1, sidecar.StartCount);
        Assert.Equal(
            GalateaDelegateCandidateState.ReplyReady,
            coordinator.Snapshot()[0].State
        );

        EventAddress runningHead = Head(2);
        coordinator.TryCaptureBatch(
            "turn-2",
            runningHead,
            [Mail("Codex", "three")]
        );
        FakeCall running = await sidecar.NextCallAsync();
        running.Accept("thread-fixed", "turn-2");
        await WaitForStateAsync(
            coordinator,
            2,
            GalateaDelegateCandidateState.Running
        );
        coordinator.RetractSourceAction(runningHead);
        Assert.True(coordinator.Snapshot()[2].SourceRetracted);
        running.Complete("running reply survives Undo");
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);

        using GalateaDelegationCoordinator.GalateaReadyReplyLease lease =
            coordinator.BeginReadyReplyCutoff();
        coordinator.RetractSourceAction(head);
        Assert.Equal(
            GalateaDelegateCandidateState.Leased,
            coordinator.Snapshot()[0].State
        );
        lease.Commit();
        coordinator.RetractSourceAction(head);
        Assert.Equal(
            GalateaDelegateCandidateState.Consumed,
            coordinator.Snapshot()[0].State
        );
    }

    [Fact]
    public async Task UndoUnroutedDeletesPayloadButKeepsDedupeTombstone() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(sidecar);
        EventAddress head = Head(1);
        coordinator.TryCaptureBatch(
            "turn-1",
            head,
            [Mail("Alice", "private body")]
        );

        coordinator.RetractSourceAction(head);
        GalateaDelegateCandidateSnapshot snapshot = Assert.Single(
            coordinator.Snapshot()
        );
        Assert.Equal(
            GalateaDelegateCandidateState.RetractedBeforeDispatch,
            snapshot.State
        );
        Assert.Null(snapshot.TaskBody);
        Assert.False(coordinator.TryCaptureBatch(
            "turn-replayed",
            head,
            [Mail("Codex", "must not execute")]
        ));
        Assert.Equal(0, sidecar.StartCount);
    }

    [Fact]
    public async Task QueueCapacityRejectsWholeBatchWithoutPartialAdmission() {
        var sidecar = new FakeSidecar();
        await using var coordinator = Create(
            sidecar,
            maximumQueuedMails: 1
        );
        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [Mail("Codex", "occupies capacity")]
        );
        _ = await sidecar.NextCallAsync();

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.TryCaptureBatch(
                "turn-2",
                Head(2),
                [Mail("Alice", "unmatched"), Mail("Codex", "routed")]
            )
        );
        Assert.Single(coordinator.Snapshot());
    }

    [Fact]
    public async Task DisposeCancelsPumpAndLateStartCannotWriteInbox() {
        var sidecar = new FakeSidecar(ignoreStartCancellation: true);
        var coordinator = Create(sidecar);
        coordinator.TryCaptureBatch(
            "turn-1",
            Head(1),
            [Mail("Codex", "one")]
        );
        FakeCall call = await sidecar.NextCallAsync();

        await coordinator.DisposeAsync().AsTask().WaitAsync(Deadline);
        call.Accept("late-thread", "late-turn");
        call.Complete("late reply");
        await Task.Yield();

        GalateaDelegateCandidateSnapshot snapshot = Assert.Single(
            coordinator.Snapshot()
        );
        Assert.Equal(
            GalateaDelegateCandidateState.Starting,
            snapshot.State
        );
        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.BeginReadyReplyCutoff()
        );
    }

    [Fact]
    public async Task ShutdownRollsBackLeaseAndCallerCleanupIsIdempotent() {
        var sidecar = new FakeSidecar();
        var coordinator = Create(sidecar);
        await CompleteOneAsync(
            coordinator,
            sidecar,
            Head(1),
            "ready reply"
        );
        GalateaDelegationCoordinator.GalateaReadyReplyLease rollbackLease =
            coordinator.BeginReadyReplyCutoff();

        await coordinator.DisposeAsync();
        rollbackLease.Rollback();
        rollbackLease.Rollback();
        rollbackLease.Dispose();
        Assert.Equal(
            GalateaDelegateCandidateState.ReplyReady,
            Assert.Single(coordinator.Snapshot()).State
        );

        var secondSidecar = new FakeSidecar();
        var second = Create(secondSidecar);
        await CompleteOneAsync(
            second,
            secondSidecar,
            Head(2),
            "second reply"
        );
        GalateaDelegationCoordinator.GalateaReadyReplyLease commitLease =
            second.BeginReadyReplyCutoff();
        await second.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(commitLease.Commit);
        commitLease.Dispose();
    }

    private static async Task CompleteOneAsync(
        GalateaDelegationCoordinator coordinator,
        FakeSidecar sidecar,
        EventAddress head,
        string final
    ) {
        coordinator.TryCaptureBatch(
            $"turn-{EventAddressTextCodec.Format(head)}",
            head,
            [Mail("Codex", $"body-{final}")]
        );
        FakeCall call = await sidecar.NextCallAsync();
        call.Accept(
            coordinator.BoundThreadIdForTest ?? "thread-fixed",
            $"codex-{EventAddressTextCodec.Format(head)}"
        );
        call.Complete(final);
        await coordinator.PumpTaskForTest.WaitAsync(Deadline);
    }

    private static async Task WaitForStateAsync(
        GalateaDelegationCoordinator coordinator,
        int index,
        GalateaDelegateCandidateState state
    ) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (coordinator.Snapshot()[index].State != state) {
            await Task.Yield();
            deadline.Token.ThrowIfCancellationRequested();
        }
    }

    private static GalateaDelegationCoordinator Create(
        FakeSidecar sidecar,
        int maximumQueuedMails = 16,
        int maximumTaskUtf8Bytes = 100_000,
        int maximumReplyUtf8Bytes = 100_000,
        int maximumInboxReplies = 16,
        int maximumInboxUtf8Bytes = 1_000_000
    ) => new(
        "alice",
        new GalateaDelegateRouteConfig(
            "Codex",
            "codex-app-server",
            "/repo",
            GalateaDelegateMode.Work,
            Network: false,
            maximumQueuedMails,
            maximumTaskUtf8Bytes,
            maximumReplyUtf8Bytes,
            maximumInboxReplies,
            maximumInboxUtf8Bytes
        ),
        sidecar
    );

    private static SendMailIntent Mail(
        string recipient,
        string body,
        string? subject = null,
        string? replyId = null
    ) => new(
        recipient,
        subject,
        body,
        replyId,
        "sent"
    );

    private static EventAddress Head(uint value) =>
        EventAddressTextCodec.Parse(
            $"ej1:{value:x16}{value:x8}{value:x8}"
        );

    private sealed class FakeSidecar(
        bool ignoreStartCancellation = false
    ) : IGalateaDelegateSidecar {
        private readonly Channel<FakeCall> _calls =
            Channel.CreateUnbounded<FakeCall>();
        private readonly ConcurrentQueue<
            GalateaDelegateDispatchRequest> _requests = [];
        private int _activeCount;
        private int _maximumActiveCount;

        internal int StartCount => _requests.Count;
        internal int MaximumActiveCount => Volatile.Read(
            ref _maximumActiveCount
        );

        public Task<GalateaDelegateAcceptedHandle> StartAsync(
            GalateaDelegateDispatchRequest request,
            CancellationToken ct
        ) {
            var call = new FakeCall(request, OnTerminal);
            _requests.Enqueue(request);
            int active = Interlocked.Increment(ref _activeCount);
            int observed;
            while (active > (observed = Volatile.Read(
                       ref _maximumActiveCount))
                && Interlocked.CompareExchange(
                    ref _maximumActiveCount,
                    active,
                    observed
                ) != observed) { }
            Assert.True(_calls.Writer.TryWrite(call));
            return ignoreStartCancellation
                ? call.Accepted.Task
                : call.Accepted.Task.WaitAsync(ct);
        }

        internal async Task<FakeCall> NextCallAsync() =>
            await _calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);

        internal bool TryReadCall(out FakeCall call) =>
            _calls.Reader.TryRead(out call!);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void OnTerminal() => Interlocked.Decrement(
            ref _activeCount
        );
    }

    private sealed class FakeCall {
        private readonly Action _onTerminal;
        private int _terminal;

        internal FakeCall(
            GalateaDelegateDispatchRequest request,
            Action onTerminal
        ) {
            Request = request;
            _onTerminal = onTerminal;
        }

        internal GalateaDelegateDispatchRequest Request { get; }
        internal TaskCompletionSource<GalateaDelegateAcceptedHandle>
            Accepted { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        internal TaskCompletionSource<GalateaDelegateTerminal> Completion {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Accept(string threadId, string turnId) =>
            Assert.True(Accepted.TrySetResult(
                new GalateaDelegateAcceptedHandle(
                    Request.DispatchId,
                    threadId,
                    turnId,
                    Completion.Task
                )
            ));

        internal void Reject(string stage, string code) =>
            Assert.True(Accepted.TrySetException(
                new GalateaDelegateStartException(stage, code)
            ));

        internal void Complete(string final) => SetTerminal(
            new GalateaDelegateTerminal.Completed(
                Request.DispatchId,
                Accepted.Task.Result.ThreadId,
                Accepted.Task.Result.TurnId,
                final
            )
        );

        internal void Fail(string stage, string code) => SetTerminal(
            new GalateaDelegateTerminal.Failed(
                Request.DispatchId,
                Accepted.Task.Result.ThreadId,
                Accepted.Task.Result.TurnId,
                stage,
                code
            )
        );

        internal void FaultCompletion(Exception exception) {
            Assert.True(Completion.TrySetException(exception));
            if (Interlocked.Exchange(ref _terminal, 1) == 0) {
                _onTerminal();
            }
        }

        internal void SetTerminal(GalateaDelegateTerminal terminal) {
            Assert.True(Completion.TrySetResult(terminal));
            if (Interlocked.Exchange(ref _terminal, 1) == 0) {
                _onTerminal();
            }
        }
    }
}
