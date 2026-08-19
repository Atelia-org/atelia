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
        var supplemental = new ExternalSupplementalSource(
            new SessionSupplementalContextSelection.Selected(
                "external exact supplemental"
            )
        );
        var runtime = new SessionRuntime(
            new RejectingCompletionClient(),
            ContextCandidateSource: source,
            ContextLifecycle: lifecycle,
            SupplementalContextSource: supplemental
        );
        engine.UseRuntime(runtime);
        Assert.Same(supplemental, runtime.SupplementalContextSource);

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

        var rawHistoryLifecycle = new ExternalLifecycle(
            SessionContextLifecycleResult.RawHistoryAuthorized
        );
        SessionContextLifecycleResult rawHistoryAuthorized = await engine
            .PrepareContextLifecycleMaintenanceAsync(
                head,
                rawHistoryLifecycle,
                "pending-observation",
                CancellationToken.None
            );
        Assert.Same(
            SessionContextLifecycleResult.RawHistoryAuthorized,
            rawHistoryAuthorized
        );
        Assert.Equal(
            SessionContextLifecycleStatus.RawHistoryAuthorized,
            rawHistoryAuthorized.Status
        );

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
        Func<SessionPendingToolBoundaryResult, EventAddress>
            readPendingBoundaryHead = ReadPendingBoundaryHead;
        Assert.NotNull(readPendingBoundaryHead);

        var backpressureLifecycle = new ExternalLifecycle(
            new SessionContextLifecycleResult(
                SessionContextLifecycleStatus.Backpressure,
                "Derived maintenance is temporarily full."
            )
        );
        SessionJournalNotReadyException backpressure = await Assert.ThrowsAsync<
            SessionJournalNotReadyException
        >(() => engine.PrepareContextLifecycleMaintenanceAsync(
            head,
            backpressureLifecycle,
            "pending-observation",
            CancellationToken.None
        ).AsTask());
        Assert.Equal(
            SessionJournalNotReadyReason.RecapMaintenanceBackpressure,
            backpressure.Reason
        );

        var unavailableLifecycle = new ExternalLifecycle(
            new SessionContextLifecycleResult(
                SessionContextLifecycleStatus.Unavailable,
                "Derived maintenance is unavailable."
            )
        );
        SessionJournalNotReadyException unavailable = await Assert.ThrowsAsync<
            SessionJournalNotReadyException
        >(() => engine.PrepareContextLifecycleMaintenanceAsync(
            head,
            unavailableLifecycle,
            "pending-observation",
            CancellationToken.None
        ).AsTask());
        Assert.Equal(
            SessionJournalNotReadyReason.RecapMaintenanceUnavailable,
            unavailable.Reason
        );

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

        var supplementalRequest = new SessionSupplementalContextRequest(
            head,
            "external exact query"
        );
        SessionSupplementalContextSelection supplementalSelection =
            await supplemental.SelectAsync(
                supplementalRequest,
                CancellationToken.None
            );
        Assert.Equal(
            "external exact query",
            supplemental.LastRequest!.ExactObservationContent
        );
        Assert.Equal(
            "external exact supplemental",
            Assert.IsType<SessionSupplementalContextSelection.Selected>(
                supplementalSelection
            ).ExactObservationContent
        );
    }

    [Fact]
    public async Task ExternalCandidateSourceCanProduceLegalPublicOutcomes() {
        EventAddress anchor = EventAddressTextCodec.Parse(
            "ej1:00000000000000010000000100000000"
        );
        var setup = new SessionContextSetupReference(
            anchor,
            1,
            new string('a', 64)
        );
        var anchorSetups = new SessionContextAnchorSetupReferences(
            setup,
            setup
        );
        var descriptor = new SessionContextCandidateDescriptor(
            "external-handle",
            "external-snapshot",
            anchor,
            anchorSetups
        );
        var candidate = new SessionContextCandidate(
            anchor,
            anchorSetups,
            Array.Empty<SessionContextContribution>()
        );
        SessionContextCandidateSelection[] selections = [
            new(
                SessionContextCandidateSelectionStatus.Selected,
                descriptor
            ),
            new(
                SessionContextCandidateSelectionStatus.EmptyLineage,
                Candidate: null
            ),
            new(
                SessionContextCandidateSelectionStatus.OrdinalUnavailable,
                Candidate: null
            ),
            new(
                SessionContextCandidateSelectionStatus.ExactPublishedSetInvalid,
                Candidate: null
            ),
            new(
                SessionContextCandidateSelectionStatus.StoreUnavailable,
                Candidate: null
            ),
            SessionContextCandidateSelection.BeyondPrefix(
                "The exact anchor is beyond the bounded prefix."
            ),
            new(
                SessionContextCandidateSelectionStatus.RawHistoryAuthorized,
                Candidate: null
            )
        ];

        foreach (SessionContextCandidateSelection expected in selections) {
            var source = new ExternalCandidateSource(
                selection: expected
            );
            SessionContextCandidateSelection observed = await source
                .SelectAsync(
                    new SessionContextSelectionRequest(anchor, 0),
                    CancellationToken.None
                );
            observed.ValidateShape();
            Assert.Same(expected, observed);
        }

        SessionContextCandidateMaterializationResult[] materializations = [
            new SessionContextCandidateMaterializationResult.Materialized(
                candidate
            ),
            new SessionContextCandidateMaterializationResult.Stale(
                "The selected snapshot changed."
            ),
            new SessionContextCandidateMaterializationResult.Busy(
                "The derived store is busy."
            ),
            new SessionContextCandidateMaterializationResult.Disposed(
                "The derived store was disposed."
            ),
            new SessionContextCandidateMaterializationResult.Invalid(
                "The selected snapshot is invalid."
            )
        ];

        foreach (
            SessionContextCandidateMaterializationResult expected
            in materializations
        ) {
            var source = new ExternalCandidateSource(
                materialization: expected
            );
            SessionContextCandidateMaterializationResult observed =
                await source.MaterializeAsync(
                    descriptor,
                    CancellationToken.None
                );
            Assert.Same(expected, observed);
        }
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

    [Fact]
    public void SupplementalContractsExposeOnlyValidatedReadOnlyDataShapes() {
        Type request = typeof(SessionSupplementalContextRequest);
        Type selection = typeof(SessionSupplementalContextSelection);
        Type selected = typeof(SessionSupplementalContextSelection.Selected);

        Assert.True(request.IsSealed);
        Assert.True(selection.IsAbstract);
        Assert.True(selected.IsSealed);
        Assert.All(request.GetProperties(), static property =>
            Assert.Null(property.SetMethod));
        Assert.All(selected.GetProperties(), static property =>
            Assert.Null(property.SetMethod));
        Assert.Equal(
            new[] { "NoMatch", "Selected" },
            selection.GetNestedTypes(BindingFlags.Public)
                .Select(static type => type.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
    }

    public void Dispose() {
        if (Directory.Exists(_path)) {
            Directory.Delete(_path, recursive: true);
        }
    }

    private static EventAddress ReadPendingBoundaryHead(
        SessionPendingToolBoundaryResult result
    ) => result switch {
        SessionPendingToolBoundaryResult.Settled settled => settled.Head,
        SessionPendingToolBoundaryResult.MorePending morePending =>
            morePending.Head,
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private sealed class ExternalCandidateSource
        : ICoherentContextCandidateSource {
        private readonly SessionContextCandidateSelection _selection;
        private readonly SessionContextCandidateMaterializationResult
            _materialization;

        internal ExternalCandidateSource(
            SessionContextCandidateSelection? selection = null,
            SessionContextCandidateMaterializationResult? materialization = null
        ) {
            _selection = selection ?? new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus.EmptyLineage,
                Candidate: null
            );
            _materialization = materialization
                ?? new SessionContextCandidateMaterializationResult.Invalid(
                    "External fixture has no materialized candidate."
                );
        }

        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) {
            request.ValidateShape();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_selection);
        }

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) {
            ArgumentNullException.ThrowIfNull(descriptor);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_materialization);
        }
    }

    private sealed class ExternalLifecycle
        : ISessionContextLifecycleCoordinator {
        private readonly SessionContextLifecycleResult _result;

        internal ExternalLifecycle(
            SessionContextLifecycleResult? result = null
        ) => _result = result ?? SessionContextLifecycleResult.Ready;

        internal SessionContextLifecycleRequest? LastRequest { get; private set; }

        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            Assert.NotNull(readView);
            LastRequest = request;
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class ExternalSupplementalSource(
        SessionSupplementalContextSelection selection
    ) : ISessionSupplementalContextSource {
        internal SessionSupplementalContextRequest? LastRequest {
            get;
            private set;
        }

        public ValueTask<SessionSupplementalContextSelection> SelectAsync(
            SessionSupplementalContextRequest request,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(selection);
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
