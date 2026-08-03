using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionDesiredSetupReconciliationTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void ReconcileBoth_PreservesRepositoryOwnedFieldsAndIsIdempotent() {
        string path = NewPath();
        EventAddress initialHead;
        using (var created = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "prompt-a",
                "surface-a",
                Schema: "schema-owned",
                DerivedContextNthPrevious: 7
            )
        )) {
            initialHead = created.InspectExecutionBoundary().Head!.Value;
            var desired = new SessionDesiredSetup(
                "model-b",
                "surface-b",
                "prompt-b"
            );

            var changed = Assert.IsType<
                SessionDesiredSetupReconciliationResult.Ready
            >(created.ReconcileDesiredSetup(initialHead, desired));

            Assert.True(changed.RuntimeConfigChanged);
            Assert.True(changed.SystemPromptChanged);
            Assert.NotEqual(initialHead, changed.GoverningSetup.Head);
            Assert.Equal("model-b", changed.GoverningSetup.RuntimeConfig.ModelId);
            Assert.Equal(
                "surface-b",
                changed.GoverningSetup.RuntimeConfig.CompletionSurfaceId
            );
            Assert.Equal("schema-owned", changed.GoverningSetup.RuntimeConfig.Schema);
            Assert.Equal(7, changed.GoverningSetup.RuntimeConfig.DerivedContext.NthPrevious);
            Assert.Equal("prompt-b", changed.GoverningSetup.SystemPrompt);

            var unchanged = Assert.IsType<
                SessionDesiredSetupReconciliationResult.Ready
            >(created.ReconcileDesiredSetup(
                changed.GoverningSetup.Head,
                desired
            ));
            Assert.False(unchanged.RuntimeConfigChanged);
            Assert.False(unchanged.SystemPromptChanged);
            Assert.Equal(
                changed.GoverningSetup.Head,
                unchanged.GoverningSetup.Head
            );
        }

        using var inspection = SessionJournalEngine.OpenReadOnly(path);
        SessionCurrentLineageSnapshot lineage =
            inspection.ReadCurrentLineageHeaders();
        Assert.Equal(
            2,
            lineage.HeadToRoot.Count(static entry =>
                entry.Kind == SessionEventKind.RuntimeConfigSetup
            )
        );
        Assert.Equal(
            2,
            lineage.HeadToRoot.Count(static entry =>
                entry.Kind == SessionEventKind.SystemPromptSetup
            )
        );
    }

    [Fact]
    public void PromptAppendFailure_LeavesRuntimeIntentAndRetryOnlyCompletesPrompt() {
        string path = NewPath();
        using (SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "prompt-a", "surface-a")
        )) {
        }
        var hooks = new SessionJournalTestHooks(
            BeforeCommit: (kind, _) => {
                if (kind == SessionEventKind.SystemPromptSetup) {
                    throw new IOException("simulated prompt append failure");
                }
            }
        );
        EventAddress headAfterRuntime;
        using (var failing = SessionJournalEngine.OpenForTest(
            path,
            null,
            hooks,
            new EventJournalOptions()
        )) {
            EventAddress expected =
                failing.InspectExecutionBoundary().Head!.Value;
            Assert.Throws<IOException>(() => failing.ReconcileDesiredSetup(
                expected,
                new SessionDesiredSetup("model-b", "surface-b", "prompt-b")
            ));
            headAfterRuntime = failing.InspectExecutionBoundary().Head!.Value;
            SessionGoverningSetup partial =
                failing.ResolveGoverningSetup(headAfterRuntime);
            Assert.Equal("model-b", partial.RuntimeConfig.ModelId);
            Assert.Equal("prompt-a", partial.SystemPrompt);
        }

        using (var retrying = SessionJournalEngine.Open(path)) {
            var retry = Assert.IsType<
                SessionDesiredSetupReconciliationResult.Ready
            >(retrying.ReconcileDesiredSetup(
                headAfterRuntime,
                new SessionDesiredSetup("model-b", "surface-b", "prompt-b")
            ));
            Assert.False(retry.RuntimeConfigChanged);
            Assert.True(retry.SystemPromptChanged);
            Assert.Equal("prompt-b", retry.GoverningSetup.SystemPrompt);
        }

        using var inspection = SessionJournalEngine.OpenReadOnly(path);
        SessionCurrentLineageSnapshot lineage =
            inspection.ReadCurrentLineageHeaders();
        Assert.Equal(
            2,
            lineage.HeadToRoot.Count(static entry =>
                entry.Kind == SessionEventKind.RuntimeConfigSetup
            )
        );
        Assert.Equal(
            2,
            lineage.HeadToRoot.Count(static entry =>
                entry.Kind == SessionEventKind.SystemPromptSetup
            )
        );
    }

    [Fact]
    public void ConcurrentObservationAfterRuntimeAppend_PreventsPromptInsertion() {
        string path = NewPath();
        using (SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "prompt-a", "surface-a")
        )) {
        }
        EventAddress? concurrentObservation = null;
        SessionJournalEngine? racing = null;
        var hooks = new SessionJournalTestHooks(
            BeforeCommit: (kind, journal) => {
                if (kind != SessionEventKind.SystemPromptSetup) {
                    return;
                }
                EventAddress observed =
                    journal.GetHead(racing!.BranchRefId)!.Value;
                concurrentObservation = journal.CommitToRef(
                    racing.BranchRefId,
                    observed,
                    SessionEventCodec.Encode(
                        SessionEventKind.ObservationAccepted,
                        new ObservationAcceptedBody(
                            "concurrent observation"
                        )
                    ),
                    opaqueEventKind:
                        (uint)SessionEventKind.ObservationAccepted,
                    hint: default
                ).Unwrap().EventAddress;
            }
        );
        using (racing = SessionJournalEngine.OpenForTest(
                   path,
                   null!,
                   hooks
               )) {
            EventAddress expected =
                racing.InspectExecutionBoundary().Head!.Value;

            var retry = Assert.IsType<
                SessionDesiredSetupReconciliationResult.Retryable
            >(racing.ReconcileDesiredSetup(
                expected,
                new SessionDesiredSetup("model-b", "surface-b", "prompt-b")
            ));

            Assert.NotNull(concurrentObservation);
            Assert.Equal(concurrentObservation, retry.ObservedHead);
            SessionExecutionBoundaryInspection boundary =
                racing.InspectExecutionBoundary();
            Assert.Equal(concurrentObservation, boundary.Head);
            Assert.Equal(
                SessionExecutionPhase.AwaitingAgentAction,
                boundary.Phase
            );
            SessionCurrentLineageSnapshot lineage =
                racing.ReadCurrentLineageHeaders();
            Assert.Equal(
                2,
                lineage.HeadToRoot.Count(static entry =>
                    entry.Kind == SessionEventKind.RuntimeConfigSetup
                )
            );
            Assert.Equal(
                1,
                lineage.HeadToRoot.Count(static entry =>
                    entry.Kind == SessionEventKind.SystemPromptSetup
                )
            );
        }
    }

    [Fact]
    public void ActiveObservationAndStaleHead_AreNonMutatingResults() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "prompt-a", "surface-a")
        );
        EventAddress idleHead = engine.InspectExecutionBoundary().Head!.Value;
        EventAddress observation = engine.AppendObservation("hello");

        var stale = Assert.IsType<
            SessionDesiredSetupReconciliationResult.Retryable
        >(engine.ReconcileDesiredSetup(
            idleHead,
            new SessionDesiredSetup("model-b", "surface-b", "prompt-b")
        ));
        Assert.Equal(idleHead, stale.ExpectedHead);
        Assert.Equal(observation, stale.ObservedHead);

        var active = Assert.IsType<
            SessionDesiredSetupReconciliationResult.Unavailable
        >(engine.ReconcileDesiredSetup(
            observation,
            new SessionDesiredSetup("model-b", "surface-b", "prompt-b")
        ));
        Assert.Equal(SessionDesiredSetupUnavailableReason.ActiveTurn, active.Reason);
        Assert.Equal(observation, engine.InspectExecutionBoundary().Head);
    }

    [Fact]
    public void EmptyProvisioningShell_IsUnavailableWithoutCreatingEvents() {
        string path = NewPath();
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            _ = journal.CreateBranch(SessionJournalDefaults.MainBranchName, null)
                .Unwrap();
        }

        using var engine = SessionJournalEngine.Open(path);
        var unavailable = Assert.IsType<
            SessionDesiredSetupReconciliationResult.Unavailable
        >(engine.ReconcileDesiredSetup(
            expectedHead: null,
            new SessionDesiredSetup("model-a", "surface-a", "prompt-a")
        ));

        Assert.Equal(
            SessionDesiredSetupUnavailableReason.Unprovisioned,
            unavailable.Reason
        );
        Assert.Equal(SessionExecutionPhase.Empty, unavailable.Phase);
        Assert.Null(engine.InspectExecutionBoundary().Head);
    }

    [Fact]
    public void PreCanceledReconciliationDoesNotMutateIdleHead() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "prompt-a", "surface-a")
        );
        EventAddress head = engine.InspectExecutionBoundary().Head!.Value;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            engine.ReconcileDesiredSetup(
                head,
                new SessionDesiredSetup("model-b", "surface-b", "prompt-b"),
                cancellation.Token
            )
        );

        Assert.Equal(head, engine.InspectExecutionBoundary().Head);
    }

    [Fact]
    public async Task TurnFailed_MustBeAbandonedBeforeDesiredSetupSync() {
        string path = NewPath();
        var client = new FailingCompletionClient();
        var source = new TestContextCandidateSource();
        SessionRuntime runtime = new(
            client,
            CompletionTarget: new SessionCompletionTargetIdentity(
                "connection-a",
                "test",
                "connection-fingerprint-a",
                "adapter-fingerprint-a"
            ),
            ContextCandidateSource: source
        );
        using var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(
                path,
                new SessionCreateOptions("model-a", "prompt-a", "surface-a")
            ),
            runtime
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            source
        );
        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            () => engine.SendAsync("fail", CancellationToken.None)
        );
        EventAddress failedHead = engine.InspectExecutionBoundary().Head!.Value;

        var unavailable = Assert.IsType<
            SessionDesiredSetupReconciliationResult.Unavailable
        >(engine.ReconcileDesiredSetup(
            failedHead,
            new SessionDesiredSetup("model-b", "surface-b", "prompt-b")
        ));

        Assert.Equal(
            SessionDesiredSetupUnavailableReason.FailedTurnMustBeAbandoned,
            unavailable.Reason
        );
        Assert.Equal(failedHead, engine.InspectExecutionBoundary().Head);
        Assert.Equal(1, client.CallCount);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-desired-setup-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class FailingCompletionClient : ICompletionClient {
        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        public int CallCount { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text("partial")]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId),
                termination: CompletionTermination.Failed("known")
            ));
        }
    }
}
