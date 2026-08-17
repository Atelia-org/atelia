using System.Reflection;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.PublicSurface.Tests;

public sealed class SessionJournalNamedRoleTests : IDisposable {
    private readonly string _path = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-session-journal-public-surface-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task ExternalCompositionCanImplementContextRolesAndUseExactHeadOwnerCalls() {
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            _path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress head = engine.ReadCurrentHead()!.Value;
        var source = new ExternalCandidateSource();
        var lifecycle = new ExternalLifecycle();
        engine.UseRuntime(new SessionRuntime(
            new RejectingCompletionClient(),
            ContextCandidateSource: source,
            ContextLifecycle: lifecycle
        ));

        SessionDesiredSetupReconciliationResult.Ready desired = Assert.IsType<
            SessionDesiredSetupReconciliationResult.Ready
        >(engine.ReconcileDesiredSetup(
            head,
            new SessionDesiredSetup(
                "model-A",
                "surface-A",
                "system-A"
            )
        ));
        Assert.False(desired.RuntimeConfigChanged);
        Assert.False(desired.SystemPromptChanged);

        var selectionRequest = new SessionContextSelectionRequest(head, 0);
        SessionContextCandidateSelection selection = await source.SelectAsync(
            selectionRequest,
            CancellationToken.None
        );
        Assert.Equal(
            SessionContextCandidateSelectionStatus.EmptyLineage,
            selection.Status
        );
        selection.ValidateShape();

        var setup = new SessionContextSetupReference(
            head,
            1,
            new string('a', 64)
        );
        SessionContextCandidateMaterializationResult materialized =
            await source.MaterializeAsync(
                new SessionContextCandidateDescriptor(
                    "external-handle",
                    "external-snapshot",
                    head,
                    new SessionContextAnchorSetupReferences(setup, setup)
                ),
                CancellationToken.None
            );
        Assert.IsType<SessionContextCandidateMaterializationResult.Invalid>(
            materialized
        );

        SessionContextLifecycleResult lifecycleResult = await engine
            .PrepareContextLifecycleMaintenanceAsync(
                head,
                lifecycle,
                "pending-observation",
                CancellationToken.None
            );
        Assert.Same(SessionContextLifecycleResult.Ready, lifecycleResult);
        Assert.Equal(
            SessionContextLifecycleTrigger.PreObservation,
            lifecycle.LastRequest!.Trigger
        );
        Assert.Equal("pending-observation", lifecycle.LastRequest.PendingObservation);

        Func<EventAddress, string, CancellationToken, Task<TurnResult>> send =
            engine.SendAsync;
        Func<EventAddress, CancellationToken, Task<ResumeOutcome>> resume =
            engine.ResumeAsync;
        Func<EventAddress, ToolSession, SessionToolRuntimeIdentity,
            CancellationToken, Task<SessionPendingToolBoundaryResult>>
            executePending = engine.ExecutePendingToolToBoundaryAsync;
        Func<EventAddress, ISessionContextLifecycleCoordinator, string?,
            CancellationToken, ValueTask<SessionContextLifecycleResult>>
            prepareLifecycle = engine.PrepareContextLifecycleMaintenanceAsync;
        Assert.NotNull(send);
        Assert.NotNull(executePending);
        Assert.NotNull(prepareLifecycle);
        SessionPendingToolBoundaryResult.Settled settled = new(head);
        SessionPendingToolBoundaryResult.MorePending morePending = new(head);
        Assert.Equal(head, settled.Head);
        Assert.Equal(head, morePending.Head);

        ResumeOutcome idleResume = await resume(
            head,
            CancellationToken.None
        );
        Assert.False(idleResume.Advanced);
        Assert.IsType<SessionTurnRetractionResult.Unavailable>(
            engine.AbandonFailedTurn(head)
        );
        Assert.IsType<SessionTurnRetractionResult.Unavailable>(
            engine.RewindLatestCompletedTurn(head)
        );
    }

    [Fact]
    public void SelectedLineageAuditSnapshotIsOwnerIssuedReadOnlyOutput() {
        Type contract = typeof(ISessionSelectedLineageAuditPageSnapshot);
        Type concrete = typeof(SessionSelectedLineageAuditSnapshot);

        Assert.True(contract.IsInterface);
        Assert.True(contract.IsAssignableFrom(concrete));
        Assert.True(concrete.IsSealed);
        Assert.Empty(concrete.GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
        Assert.All(contract.GetProperties(), static property =>
            Assert.Null(property.SetMethod));
    }

    public void Dispose() {
        if (Directory.Exists(_path)) {
            Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class ExternalCandidateSource
        : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(new SessionContextCandidateSelection(
            SessionContextCandidateSelectionStatus.EmptyLineage,
            Candidate: null
        ));

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<SessionContextCandidateMaterializationResult>(
            new SessionContextCandidateMaterializationResult.Invalid(
                "External fixture has no materialized candidate."
            )
        );
    }

    private sealed class ExternalLifecycle
        : ISessionContextLifecycleCoordinator {
        internal SessionContextLifecycleRequest? LastRequest { get; private set; }

        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            Assert.NotNull(readView);
            LastRequest = request;
            return ValueTask.FromResult(SessionContextLifecycleResult.Ready);
        }
    }

    private sealed class RejectingCompletionClient : ICompletionClient {
        public string Name => "public-surface-client";
        public string ApiSpecId => "public-surface-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException(
            "The public-surface oracle must not dispatch a provider request."
        );
    }
}
