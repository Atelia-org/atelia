using Atelia.Galatea.Server;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaTurnStopControllerTests : IDisposable {
    private readonly List<string> _paths = [];

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort test cleanup.
            }
        }
    }

    [Fact]
    public void LiveTurn_StopBeforeBackgroundCancelsPreparation() {
        GalateaLiveTurn turn = Turn();

        bool accepted = turn.RequestStop();

        Assert.True(accepted);
        Assert.True(turn.StopRequested);
        Assert.True(turn.PreDispatchStopToken.IsCancellationRequested);
        Assert.False(turn.Observer.ShouldStop);
        Assert.Equal(
            GalateaTurnStopPhase.PreDispatch,
            turn.StopController.Phase
        );
    }

    [Fact]
    public void StopWinsTransition_ThrowsCancellation() {
        var controller = new GalateaTurnStopController();
        Assert.True(controller.RequestStop());

        OperationCanceledException exception = Assert.Throws<
            OperationCanceledException
        >(() => controller.EnterObserverOnlyOrThrow(
            CancellationToken.None
        ));

        Assert.Equal(
            controller.PreDispatchStopToken,
            exception.CancellationToken
        );
        Assert.True(
            controller.PreDispatchStopToken.IsCancellationRequested
        );
        Assert.False(controller.Observer.ShouldStop);
        Assert.Equal(
            GalateaTurnStopPhase.PreDispatch,
            controller.Phase
        );
    }

    [Fact]
    public void TransitionWinsStop_UsesOnlyObserver() {
        var controller = new GalateaTurnStopController();
        controller.EnterObserverOnlyOrThrow(CancellationToken.None);

        Assert.True(controller.RequestStop());

        Assert.True(controller.StopRequested);
        Assert.True(controller.Observer.ShouldStop);
        Assert.False(
            controller.PreDispatchStopToken.IsCancellationRequested
        );
        Assert.Equal(
            GalateaTurnStopPhase.ObserverOnly,
            controller.Phase
        );
    }

    [Fact]
    public void CompletedTurnRejectsStop() {
        var controller = new GalateaTurnStopController();
        controller.Complete();

        Assert.False(controller.RequestStop());
        Assert.False(controller.StopRequested);
        Assert.False(controller.Observer.ShouldStop);
        Assert.False(
            controller.PreDispatchStopToken.IsCancellationRequested
        );
        Assert.Equal(
            GalateaTurnStopPhase.Completed,
            controller.Phase
        );
    }

    [Fact]
    public void StreamObserverStopFlagIsMonotonic() {
        var controller = new GalateaTurnStopController();
        controller.EnterObserverOnlyOrThrow(CancellationToken.None);
        Assert.True(controller.RequestStop());

        controller.Observer.ShouldStop = false;

        Assert.True(controller.Observer.ShouldStop);
    }

    [Theory]
    [InlineData(SessionContextLifecycleStatus.Ready)]
    [InlineData(SessionContextLifecycleStatus.RawHistoryAuthorized)]
    public async Task LifecycleGate_PreAppendSuccessTransitions(
        SessionContextLifecycleStatus status
    ) {
        using SessionJournalEngine engine = CreateEngine();
        var controller = new GalateaTurnStopController();
        var inner = new StubLifecycle(
            (_, _, _) => ValueTask.FromResult(
                new SessionContextLifecycleResult(status)
            )
        );
        var gate = new GalateaFreshSendLifecycleGate(
            inner,
            controller
        );

        SessionContextLifecycleResult result = await gate.PrepareAsync(
            engine.ReadView,
            Request(engine, pendingObservation: "pending"),
            CancellationToken.None
        );

        Assert.Equal(status, result.Status);
        Assert.Equal(1, inner.CallCount);
        Assert.Equal(
            GalateaTurnStopPhase.ObserverOnly,
            controller.Phase
        );
    }

    [Theory]
    [InlineData(SessionContextLifecycleStatus.Backpressure)]
    [InlineData(SessionContextLifecycleStatus.Unavailable)]
    public async Task LifecycleGate_PreAppendFailureStatusDoesNotTransition(
        SessionContextLifecycleStatus status
    ) {
        using SessionJournalEngine engine = CreateEngine();
        var controller = new GalateaTurnStopController();
        var gate = new GalateaFreshSendLifecycleGate(
            new StubLifecycle(
                (_, _, _) => ValueTask.FromResult(
                    new SessionContextLifecycleResult(status, "blocked")
                )
            ),
            controller
        );

        SessionContextLifecycleResult result = await gate.PrepareAsync(
            engine.ReadView,
            Request(engine, pendingObservation: "pending"),
            CancellationToken.None
        );

        Assert.Equal(status, result.Status);
        Assert.Equal(
            GalateaTurnStopPhase.PreDispatch,
            controller.Phase
        );
    }

    [Fact]
    public async Task LifecycleGate_InnerExceptionDoesNotTransition() {
        using SessionJournalEngine engine = CreateEngine();
        var controller = new GalateaTurnStopController();
        var gate = new GalateaFreshSendLifecycleGate(
            new StubLifecycle(
                (_, _, _) => ValueTask.FromException<
                    SessionContextLifecycleResult
                >(new InvalidOperationException("failed"))
            ),
            controller
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.PrepareAsync(
                engine.ReadView,
                Request(engine, pendingObservation: "pending"),
                CancellationToken.None
            ).AsTask()
        );
        Assert.Equal(
            GalateaTurnStopPhase.PreDispatch,
            controller.Phase
        );
    }

    [Fact]
    public async Task LifecycleGate_CancellationDoesNotTransition() {
        using SessionJournalEngine engine = CreateEngine();
        var controller = new GalateaTurnStopController();
        var gate = new GalateaFreshSendLifecycleGate(
            new StubLifecycle(
                static (_, _, cancellationToken) =>
                    ValueTask.FromCanceled<
                        SessionContextLifecycleResult
                    >(cancellationToken)
            ),
            controller
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.PrepareAsync(
                engine.ReadView,
                Request(engine, pendingObservation: "pending"),
                cancellation.Token
            ).AsTask()
        );
        Assert.Equal(
            GalateaTurnStopPhase.PreDispatch,
            controller.Phase
        );
    }

    [Fact]
    public async Task LifecycleGate_PostAppendSuccessDoesNotTransition() {
        using SessionJournalEngine engine = CreateEngine();
        var controller = new GalateaTurnStopController();
        var gate = new GalateaFreshSendLifecycleGate(
            new StubLifecycle(
                (_, _, _) => ValueTask.FromResult(
                    SessionContextLifecycleResult.Ready
                )
            ),
            controller
        );

        _ = await gate.PrepareAsync(
            engine.ReadView,
            Request(engine, pendingObservation: null),
            CancellationToken.None
        );

        Assert.Equal(
            GalateaTurnStopPhase.PreDispatch,
            controller.Phase
        );
    }

    [Fact]
    public async Task LifecycleGate_TransitionsOnlyOnFirstPreAppendSuccess() {
        using SessionJournalEngine engine = CreateEngine();
        var controller = new GalateaTurnStopController();
        var inner = new StubLifecycle(
            (_, _, _) => ValueTask.FromResult(
                SessionContextLifecycleResult.Ready
            )
        );
        var gate = new GalateaFreshSendLifecycleGate(
            inner,
            controller
        );
        SessionContextLifecycleRequest request = Request(
            engine,
            pendingObservation: "pending"
        );

        _ = await gate.PrepareAsync(
            engine.ReadView,
            request,
            CancellationToken.None
        );
        controller.Complete();
        _ = await gate.PrepareAsync(
            engine.ReadView,
            request,
            CancellationToken.None
        );

        Assert.Equal(2, inner.CallCount);
        Assert.Equal(
            GalateaTurnStopPhase.Completed,
            controller.Phase
        );
    }

    [Fact]
    public async Task LifecycleGate_PostAppendWorkRemainsObserverOnly() {
        using SessionJournalEngine engine = CreateEngine();
        var controller = new GalateaTurnStopController();
        var postAppendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releasePostAppend = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        CancellationToken observedPostAppendToken = default;
        int callCount = 0;
        var inner = new StubLifecycle(
            async (_, _, cancellationToken) => {
                if (Interlocked.Increment(ref callCount) == 1) {
                    return SessionContextLifecycleResult.Ready;
                }
                observedPostAppendToken = cancellationToken;
                postAppendEntered.TrySetResult();
                await releasePostAppend.Task.ConfigureAwait(false);
                return SessionContextLifecycleResult.Ready;
            }
        );
        var gate = new GalateaFreshSendLifecycleGate(
            inner,
            controller
        );

        _ = await gate.PrepareAsync(
            engine.ReadView,
            Request(engine, pendingObservation: "pending"),
            CancellationToken.None
        );
        Task<SessionContextLifecycleResult> postAppend = gate
            .PrepareAsync(
                engine.ReadView,
                Request(engine, pendingObservation: null),
                controller.PreDispatchStopToken
            )
            .AsTask();
        await postAppendEntered.Task;

        Assert.True(controller.RequestStop());
        Assert.True(controller.Observer.ShouldStop);
        Assert.False(
            controller.PreDispatchStopToken.IsCancellationRequested
        );
        Assert.False(observedPostAppendToken.IsCancellationRequested);

        releasePostAppend.TrySetResult();
        _ = await postAppend;
    }

    private GalateaLiveTurn Turn() => new(
        "message",
        new GalateaTurnOptions("test")
    );

    private SessionJournalEngine CreateEngine() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-stop-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "prompt-a",
                "surface-a"
            )
        );
    }

    private static SessionContextLifecycleRequest Request(
        SessionJournalEngine engine,
        string? pendingObservation
    ) => new(
        new SessionContextSelectionRequest(
            engine.ReadCurrentHead()
                ?? throw new InvalidOperationException(
                    "Test SessionJournal has no head."
                ),
            NthPrevious: 0
        ),
        SessionExecutionPhase.Idle,
        pendingObservation
    );

    private sealed class StubLifecycle(
        Func<
            SessionJournalReadView,
            SessionContextLifecycleRequest,
            CancellationToken,
            ValueTask<SessionContextLifecycleResult>
        > handler
    ) : ISessionContextLifecycleCoordinator {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            Interlocked.Increment(ref _callCount);
            return handler(readView, request, cancellationToken);
        }
    }
}
