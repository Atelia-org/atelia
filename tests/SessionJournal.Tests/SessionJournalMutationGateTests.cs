using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalMutationGateTests : IDisposable {
    private readonly string _root = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "atelia-session-journal-mutation-gate-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task BlockedSendRejectsSecondMutationsWithoutSideEffectsAndReleases() {
        var client = new BlockingFirstCompletionClient();
        var candidates = new TestContextCandidateSource();
        SessionRuntime runtime = CreateRuntime(client, candidates);
        var rejectedClient = new KnownFailureCompletionClient();
        SessionRuntime rejectedRuntime = CreateRuntime(
            rejectedClient,
            new TestContextCandidateSource(),
            connectionId: "rejected-connection"
        );
        SessionJournalEngine engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(
                _root,
                new SessionCreateOptions(
                    "model-A",
                    "system-A",
                    "surface-A"
                )
            ),
            runtime
        );
        Task<TurnResult>? active = null;
        try {
            _ = engine.AppendObservation("prepared rewind candidate");
            EventAddress preparedHead =
                engine.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text("prepared response")
                    ]),
                    new CompletionDescriptor(
                        "fixture",
                        "fixture-v1",
                        "model-A"
                    )
                );
            SessionPreparedCompletedTurnRewind prepared = Assert.IsType<
                SessionCompletedTurnRewindPrepareResult.Prepared
            >(engine.PrepareLatestCompletedTurnRewind(preparedHead)).Value;
            await CoherentArtifactSetTestFixture
                .ActivateAtCurrentHeadAsync(_root, engine, candidates);

            active = engine.SendAsync(
                "first",
                CancellationToken.None
            );
            await client.WaitUntilBlockedAsync();
            EventAddress blockedHead = engine.ReadCurrentHead()!.Value;
            int selectionCount = candidates.SelectionCount;

            SessionJournalConcurrentMutationException secondSend =
                await Assert.ThrowsAsync<
                    SessionJournalConcurrentMutationException
                >(() => engine.SendAsync(
                    "second",
                    CancellationToken.None
                ));
            AssertMutation(
                secondSend,
                attempted: "SendAsync",
                active: "SendAsync"
            );

            SessionJournalConcurrentMutationException resume =
                await Assert.ThrowsAsync<
                    SessionJournalConcurrentMutationException
                >(() => engine.ResumeAsync(
                    blockedHead,
                    CancellationToken.None
                ));
            AssertMutation(
                resume,
                attempted: "ResumeAsync",
                active: "SendAsync"
            );

            SessionJournalConcurrentMutationException useRuntime =
                Assert.Throws<SessionJournalConcurrentMutationException>(
                    () => engine.UseRuntime(rejectedRuntime)
                );
            AssertMutation(
                useRuntime,
                attempted: "UseRuntime",
                active: "SendAsync"
            );

            var desired = new SessionDesiredSetup(
                "model-B",
                "surface-B",
                "system-B"
            );
            SessionJournalConcurrentMutationException reconcile =
                Assert.Throws<SessionJournalConcurrentMutationException>(
                    () => engine.ReconcileDesiredSetup(
                        blockedHead,
                        desired
                    )
                );
            AssertMutation(
                reconcile,
                attempted: "ReconcileDesiredSetup",
                active: "SendAsync"
            );
            Assert.Equal(blockedHead, engine.ReadCurrentHead());

            SessionJournalConcurrentMutationException abandon =
                Assert.Throws<SessionJournalConcurrentMutationException>(
                    () => engine.AbandonFailedTurn(blockedHead)
                );
            AssertMutation(
                abandon,
                attempted: "AbandonFailedTurn",
                active: "SendAsync"
            );
            Assert.Equal(blockedHead, engine.ReadCurrentHead());

            SessionJournalConcurrentMutationException rewind =
                Assert.Throws<SessionJournalConcurrentMutationException>(
                    () => engine.RewindLatestCompletedTurn(blockedHead)
                );
            AssertMutation(
                rewind,
                attempted: "RewindLatestCompletedTurn",
                active: "SendAsync"
            );
            Assert.Equal(blockedHead, engine.ReadCurrentHead());

            SessionJournalConcurrentMutationException preparedCommit =
                Assert.Throws<SessionJournalConcurrentMutationException>(
                    () => engine.CommitPreparedCompletedTurnRewind(
                        prepared
                    )
                );
            AssertMutation(
                preparedCommit,
                attempted: "CommitPreparedCompletedTurnRewind",
                active: "SendAsync"
            );
            Assert.Equal(blockedHead, engine.ReadCurrentHead());

            SessionJournalConcurrentMutationException dispose =
                Assert.Throws<SessionJournalConcurrentMutationException>(
                    engine.Dispose
                );
            AssertMutation(
                dispose,
                attempted: "Dispose",
                active: "SendAsync"
            );

            Assert.Equal(1, client.CallCount);
            Assert.Equal(selectionCount, candidates.SelectionCount);
            Assert.Equal(blockedHead, engine.ReadCurrentHead());

            client.ReleaseFirst();
            TurnResult first = await active;
            Assert.Equal("first-result", first.Message.GetFlattenedText());

            EventAddress nextExpectedHead =
                engine.ReadCurrentHead()!.Value;
            TurnResult second = await engine.SendAsync(
                nextExpectedHead,
                "after-release",
                CancellationToken.None
            );
            Assert.Equal(
                "later-result",
                second.Message.GetFlattenedText()
            );
            Assert.Equal(2, client.CallCount);
            Assert.Equal(0, rejectedClient.CallCount);

            ResumeOutcome idle = await engine.ResumeAsync(
                engine.ReadCurrentHead()!.Value,
                CancellationToken.None
            );
            Assert.False(idle.Advanced);
        }
        finally {
            client.ReleaseFirst();
            if (active is { IsCompleted: false }) {
                await active;
            }
            engine.Dispose();
        }

        Assert.Throws<ObjectDisposedException>(() =>
            engine.ReadCurrentHead()
        );
    }

    [Fact]
    public void SequentialDoubleDisposeIsIdempotent() {
        SessionJournalEngine engine = SessionJournalEngine.Create(
            _root,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );

        engine.Dispose();
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => engine.ReadCurrentHead()
        );
    }

    [Fact]
    public async Task KnownProviderFailureReleasesLeaseForExactAbandon() {
        var client = new KnownFailureCompletionClient();
        var candidates = new TestContextCandidateSource();
        using SessionJournalEngine engine =
            SessionJournalTestRuntime.Attach(
                SessionJournalEngine.Create(
                    _root,
                    new SessionCreateOptions(
                        "model-A",
                        "system-A",
                        "surface-A"
                    )
                ),
                CreateRuntime(client, candidates)
            );
        await CoherentArtifactSetTestFixture
            .ActivateAtCurrentHeadAsync(_root, engine, candidates);

        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            () => engine.SendAsync("fails", CancellationToken.None)
        );
        EventAddress failedHead = engine.ReadCurrentHead()!.Value;

        Assert.IsType<SessionTurnRetractionResult.Moved>(
            engine.AbandonFailedTurn(failedHead)
        );
        Assert.Equal(SessionExecutionPhase.Idle,
            engine.InspectExecutionBoundary().Phase);
    }

    [Fact]
    public void CanceledReconciliationReleasesLease() {
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            _root,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress head = engine.ReadCurrentHead()!.Value;
        var desired = new SessionDesiredSetup(
            "model-A",
            "surface-A",
            "system-A"
        );
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            engine.ReconcileDesiredSetup(
                head,
                desired,
                canceled.Token
            )
        );

        Assert.IsType<SessionDesiredSetupReconciliationResult.Ready>(
            engine.ReconcileDesiredSetup(head, desired)
        );
    }

    [Fact]
    public void ReentrantDirectMutationIsTypedBeforeCommitAndReleases() {
        SessionJournalEngine? engine = null;
        bool reenter = true;
        var hooks = new SessionJournalTestHooks(
            BeforeCommit: (kind, _) => {
                if (reenter
                    && kind == SessionEventKind.ObservationAccepted) {
                    engine!.AppendSystemPromptSetup("must-not-commit");
                }
            }
        );
        engine = SessionJournalEngine.CreateForTest(
            _root,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            ),
            CreateRuntime(
                new KnownFailureCompletionClient(),
                new TestContextCandidateSource()
            ),
            hooks
        );
        using (engine) {
            EventAddress originalHead =
                engine.ReadCurrentHead()!.Value;

            SessionJournalConcurrentMutationException error =
                Assert.Throws<SessionJournalConcurrentMutationException>(
                    () => engine.AppendObservation("blocked")
                );

            AssertMutation(
                error,
                attempted: "AppendSystemPromptSetup",
                active: "AppendObservation"
            );
            Assert.Equal(originalHead, engine.ReadCurrentHead());

            reenter = false;
            EventAddress appended = engine.AppendObservation("allowed");
            Assert.Equal(appended, engine.ReadCurrentHead());
        }
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for the isolated test repository.
        }
    }

    private static SessionRuntime CreateRuntime(
        ICompletionClient client,
        TestContextCandidateSource candidates,
        string connectionId = "test-connection"
    ) => new(
        client,
        CompletionTarget: new SessionCompletionTargetIdentity(
            connectionId,
            "test",
            $"{connectionId}-fingerprint-v1",
            "test-request-adapter-v1"
        ),
        ContextCandidateSource: candidates
    );

    private static void AssertMutation(
        SessionJournalConcurrentMutationException exception,
        string attempted,
        string active
    ) {
        Assert.Equal(attempted, exception.AttemptedOperation);
        Assert.Equal(active, exception.ActiveOperation);
    }

    private sealed class BlockingFirstCompletionClient
        : ICompletionClient {
        private readonly TaskCompletionSource _blocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _callCount;

        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        internal int CallCount => Volatile.Read(ref _callCount);

        internal Task WaitUntilBlockedAsync() => _blocked.Task;

        internal void ReleaseFirst() => _release.TrySetResult();

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            int call = Interlocked.Increment(ref _callCount);
            if (call == 1) {
                _blocked.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return Success(
                request,
                call == 1 ? "first-result" : "later-result"
            );
        }
    }

    private sealed class KnownFailureCompletionClient
        : ICompletionClient {
        private int _callCount;

        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _callCount);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text("failed-result")
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                ),
                termination: CompletionTermination.Failed(
                    "controlled-failure"
                )
            ));
        }
    }

    private static CompletionResult Success(
        CompletionRequest request,
        string text
    ) => new(
        new ActionMessage([new ActionBlock.Text(text)]),
        new CompletionDescriptor(
            "scripted",
            "test-api-v1",
            request.ModelId
        )
    );
}
