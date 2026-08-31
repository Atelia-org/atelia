using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteDerivedInfoReconcilerTests {
    private static readonly CompletionDescriptor Invocation = new(
        "fixture",
        "fixture-v1",
        "model-a"
    );

    [Fact]
    public async Task PendingMaterializesExactTurnAndAppliesDerivedInfo() {
        using var fixture = await Fixture.CreateAsync();
        (EventAddress source, GalateaTerminalActionExtractionTarget target) =
            fixture.AppendTurn(
                "raw observation for the note",
                "I record the lighthouse route."
            );
        CharacterNoteDefaultPodReconcileResult exact =
            await fixture.Reconciler.ReconcileTargetAsync(
                fixture.Engine,
                target
            );
        CharacterNoteAppliedMemo applied = Assert.Single(Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.AppliedNow
        >(exact).Memos);
        var enricher = new RecordingEnricher();

        CharacterNoteDerivedInfoReconcileResult derived =
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                enricher
            );

        Assert.Equal(source, Assert.IsType<
            CharacterNoteDerivedInfoReconcileResult.Applied
        >(derived).SourceAction);
        CharacterNoteDerivedInfoEnrichmentRequest request =
            Assert.Single(enricher.Requests);
        Assert.Equal("raw observation for the note",
            request.ObservationContent);
        Assert.Equal("I record the lighthouse route.",
            request.VisibleActionText);
        Assert.Equal(applied.ArtifactOrdinal,
            Assert.Single(request.Targets).ArtifactOrdinal);
        Assert.Equal(applied.ExactText,
            Assert.Single(request.Targets).ExactText);

        global::Atelia.MemoPod.MemoPod pod =
            global::Atelia.MemoPod.MemoPod.Open(
                fixture.StatePath,
                CharacterNoteDefaultPodV1.PodId
            );
        Memo memo = pod.Get(applied.MemoId);
        Assert.Equal("Title 0", memo.Title);
        Assert.Equal("Gist 0.", memo.Gist);
        Assert.Equal("Summary 0", memo.Summary);
        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.NoWork>(
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                enricher
            )
        );
    }

    [Fact]
    public async Task ProviderAndContextFailuresLeavePendingDurableWork() {
        using var providerFixture = await Fixture.CreateAsync();
        (_, GalateaTerminalActionExtractionTarget providerTarget) =
            providerFixture.AppendTurn("observation", "save provider note");
        _ = await providerFixture.Reconciler.ReconcileTargetAsync(
            providerFixture.Engine,
            providerTarget
        );
        string settledBefore = providerFixture.Reconciler
            .ReadStatusSnapshot().SettledDefaultPodStateIdentity!;

        CharacterNoteDerivedInfoReconcileResult failed =
            await providerFixture.Reconciler.ReconcileNextDerivedInfoAsync(
                providerFixture.Materialize,
                new ThrowingEnricher()
            );

        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes
                .DerivedInfoProviderUnavailable,
            Assert.IsType<
                CharacterNoteDerivedInfoReconcileResult.Deferred
            >(failed).Code
        );
        Assert.Equal(settledBefore, providerFixture.Reconciler
            .ReadStatusSnapshot().SettledDefaultPodStateIdentity);
        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Applied>(
            await providerFixture.Reconciler
                .ReconcileNextDerivedInfoAsync(
                    providerFixture.Materialize,
                    new RecordingEnricher()
                )
        );

        using var contextFixture = await Fixture.CreateAsync();
        EventAddress contextSource = contextFixture.AppendAction(
            "observation",
            "actual visible Action"
        );
        var counterfeitTarget = new GalateaTerminalActionExtractionTarget(
            contextSource,
            "counterfeit visible Action"
        );
        _ = await contextFixture.Reconciler.ReconcileTargetAsync(
            contextFixture.Engine,
            counterfeitTarget
        );
        (_, GalateaTerminalActionExtractionTarget healthyTarget) =
            contextFixture.AppendTurn(
                "healthy observation",
                "healthy visible Action"
            );
        _ = await contextFixture.Reconciler.ReconcileTargetAsync(
            contextFixture.Engine,
            healthyTarget
        );
        var contextEnricher = new RecordingEnricher();

        CharacterNoteDerivedInfoReconcileResult mismatch =
            await contextFixture.Reconciler.ReconcileNextDerivedInfoAsync(
                contextFixture.Materialize,
                contextEnricher
            );

        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes.DerivedInfoContextMismatch,
            Assert.IsType<
                CharacterNoteDerivedInfoReconcileResult.Deferred
            >(mismatch).Code
        );
        Assert.Equal(CharacterMemoryStoreState.Ready,
            contextFixture.Reconciler.ReadStatusSnapshot().StoreState);
        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Applied>(
            await contextFixture.Reconciler.ReconcileNextDerivedInfoAsync(
                contextFixture.Materialize,
                contextEnricher
            )
        );
        Assert.Single(contextEnricher.Requests);
    }

    [Fact]
    public async Task ProviderDoesNotHoldGateAndPreparedNeverRecallsModel() {
        var access = new FaultingPodAccess();
        using var fixture = await Fixture.CreateAsync(access);
        (_, GalateaTerminalActionExtractionTarget first) = fixture.AppendTurn(
            "first observation",
            "save first note"
        );
        _ = await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            first
        );

        bool materializerActive = false;
        bool materializerExited = false;
        async ValueTask<CharacterNoteDerivedInfoEnrichmentRequest>
            MaterializeInScope(
            CharacterMemoryDerivedInfoWorkSnapshot work,
            CancellationToken cancellationToken
        ) {
            materializerActive = true;
            try {
                return await fixture.Materialize(work, cancellationToken);
            }
            finally {
                materializerActive = false;
                materializerExited = true;
            }
        }

        var blocked = new BlockingEnricher();
        Task<CharacterNoteDerivedInfoReconcileResult> derivedTask =
            fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                MaterializeInScope,
                blocked
            ).AsTask();
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(materializerExited);
        Assert.False(materializerActive);

        access.NextFreezeFault = FreezeFault.BeforePublish;
        (_, GalateaTerminalActionExtractionTarget second) = fixture.AppendTurn(
            "second observation",
            "save second note"
        );
        CharacterNoteDefaultPodReconcileResult exact =
            await fixture.Reconciler.ReconcileTargetAsync(
                fixture.Engine,
                second
            );
        Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.DeferredAfterCapture
        >(exact);

        blocked.Release.SetResult();
        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Deferred>(
            await derivedTask
        );
        Assert.Equal(1, blocked.CallCount);

        CharacterNotePendingReconcileResult pending =
            await fixture.Reconciler.ReconcilePendingAsync();
        Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(
            Assert.IsType<CharacterNotePendingReconcileResult.Reconciled>(
                pending
            ).Result
        );
        var mustNotRun = new ThrowingEnricher();

        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Applied>(
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                mustNotRun
            )
        );
        Assert.Equal(0, mustNotRun.CallCount);
    }

    [Fact]
    public async Task MaterializerCancellationLeavesPendingAndSkipsProvider() {
        using var fixture = await Fixture.CreateAsync();
        (_, GalateaTerminalActionExtractionTarget target) = fixture.AppendTurn(
            "observation",
            "save cancellation note"
        );
        _ = await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            target
        );
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var enricher = new RecordingEnricher();

        CharacterNoteDerivedInfoReconcileResult deferred =
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                enricher,
                canceled.Token
            );

        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes
                .DerivedInfoContextUnavailable,
            Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Deferred>(
                deferred
            ).Code
        );
        Assert.Empty(enricher.Requests);
        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Applied>(
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                enricher
            )
        );
    }

    [Fact]
    public async Task PreCaptureCancellationWhileWaitingForPodGateDoesNotCapture() {
        var access = new FaultingPodAccess();
        var extractor = new BlockingNoteExtractor();
        using var fixture = await Fixture.CreateAsync(access, extractor);
        (_, GalateaTerminalActionExtractionTarget first) = fixture.AppendTurn(
            "first observation",
            "save first gate note"
        );
        _ = await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            first
        );

        extractor.ArmNextCall();
        using var canceled = new CancellationTokenSource();
        (_, GalateaTerminalActionExtractionTarget second) = fixture.AppendTurn(
            "second observation",
            "save canceled gate note"
        );
        Task<CharacterNoteDefaultPodReconcileResult> exactTask =
            fixture.Reconciler.ReconcileTargetAsync(
                fixture.Engine,
                second,
                canceled.Token
            ).AsTask();
        await extractor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        access.ArmBlockingFreeze();
        Task<CharacterNoteDerivedInfoReconcileResult> derivedTask =
            fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                new RecordingEnricher()
            ).AsTask();
        await access.FreezeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try {
            extractor.Release.SetResult();
            await extractor.Completed.Task.WaitAsync(
                TimeSpan.FromSeconds(10)
            );
            await Assert.ThrowsAsync<TimeoutException>(
                async () => await exactTask.WaitAsync(
                    TimeSpan.FromMilliseconds(100)
                )
            );
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await exactTask.WaitAsync(
                    TimeSpan.FromSeconds(10)
                )
            );
        }
        finally {
            extractor.Release.TrySetResult();
            access.ReleaseFreeze.TrySetResult();
        }

        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Applied>(
            await derivedTask
        );
        Assert.Equal(2, extractor.CallCount);

        Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(
            await fixture.Reconciler.ReconcileTargetAsync(
                fixture.Engine,
                second
            )
        );
        Assert.Equal(3, extractor.CallCount);
    }

    [Fact]
    public async Task PreparedAggregateCapacityRejectsExactWork() {
        var access = new FaultingPodAccess();
        using var fixture = await Fixture.CreateAsync(access);
        (_, GalateaTerminalActionExtractionTarget target) = fixture.AppendTurn(
            "observation",
            "save capacity note"
        );
        CharacterNoteAppliedMemo applied = Assert.Single(Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.AppliedNow
        >(await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            target
        )).Memos);
        access.ReportDerivedInfoCapacityFull = true;

        CharacterNoteDerivedInfoReconcileResult result =
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                new RecordingEnricher()
            );

        Assert.Equal(
            CharacterNoteDefaultPodOutcomeCodes
                .DerivedInfoCapacityExceeded,
            Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Rejected>(
                result
            ).Code
        );
        Memo memo = global::Atelia.MemoPod.MemoPod.Open(
            fixture.StatePath,
            CharacterNoteDefaultPodV1.PodId
        ).Get(applied.MemoId);
        Assert.Null(memo.Title);
        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.NoWork>(
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                new ThrowingEnricher()
            )
        );
    }

    [Fact]
    public async Task AdmissionReplaysDurablePlannedBaseWithoutProvider() {
        var access = new FaultingPodAccess();
        using var fixture = await Fixture.CreateAsync(access);
        (_, GalateaTerminalActionExtractionTarget target) = fixture.AppendTurn(
            "observation",
            "save replay note"
        );
        _ = await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            target
        );
        access.NextFreezeFault = FreezeFault.BeforePublish;

        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Deferred>(
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                new RecordingEnricher()
            )
        );

        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Applied>(
            await fixture.Reconciler
                .ReconcileActiveDerivedInfoPlanAsync()
        );
    }

    [Fact]
    public async Task AdmissionRecoversDurablePlannedTargetWithoutProvider() {
        var access = new FaultingPodAccess();
        using var fixture = await Fixture.CreateAsync(access);
        (_, GalateaTerminalActionExtractionTarget target) = fixture.AppendTurn(
            "observation",
            "save restart note"
        );
        CharacterNoteAppliedMemo applied = Assert.Single(Assert.IsType<
            CharacterNoteDefaultPodReconcileResult.AppliedNow
        >(await fixture.Reconciler.ReconcileTargetAsync(
            fixture.Engine,
            target
        )).Memos);
        access.NextFreezeFault = FreezeFault.AfterPublishLoseRecoveryOpen;

        CharacterNoteDerivedInfoReconcileResult interrupted =
            await fixture.Reconciler.ReconcileNextDerivedInfoAsync(
                fixture.Materialize,
                new RecordingEnricher()
            );
        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Deferred>(
            interrupted
        );
        Assert.NotNull(fixture.Reconciler.ReadStatusSnapshot()
            .ActiveDerivedInfoWork);

        await fixture.ReopenAsync();
        CharacterNoteDerivedInfoReconcileResult recovered =
            await fixture.Reconciler
                .ReconcileActiveDerivedInfoPlanAsync();

        Assert.IsType<CharacterNoteDerivedInfoReconcileResult.Applied>(
            recovered
        );
        Assert.Null(fixture.Reconciler.ReadStatusSnapshot()
            .ActiveDerivedInfoWork);
        Memo memo = global::Atelia.MemoPod.MemoPod.Open(
            fixture.StatePath,
            CharacterNoteDefaultPodV1.PodId
        ).Get(applied.MemoId);
        Assert.Equal("Title 0", memo.Title);
    }

    private sealed class RecordingNoteExtractor : ICharacterNoteExtractor {
        public string ContractId => "note-extractor-test-v1";

        public ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
            string visibleActionText,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<CharacterNoteIntent>>([
                new CharacterNoteIntent(
                    "Exact memo: " + visibleActionText,
                    visibleActionText
                )
            ]);
        }
    }

    private sealed class BlockingNoteExtractor : ICharacterNoteExtractor {
        private int _blockNext;

        public string ContractId => "blocking-note-extractor-test-v1";
        internal int CallCount { get; private set; }
        internal TaskCompletionSource Started { get; private set; } = NewGate();
        internal TaskCompletionSource Release { get; private set; } = NewGate();
        internal TaskCompletionSource Completed { get; private set; } = NewGate();

        internal void ArmNextCall() {
            Started = NewGate();
            Release = NewGate();
            Completed = NewGate();
            Volatile.Write(ref _blockNext, 1);
        }

        public async ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
            string visibleActionText,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (Interlocked.Exchange(ref _blockNext, 0) == 1) {
                Started.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
                Completed.SetResult();
            }
            return [new CharacterNoteIntent(
                "Exact memo: " + visibleActionText,
                visibleActionText
            )];
        }

        private static TaskCompletionSource NewGate() => new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
    }

    private class RecordingEnricher : ICharacterNoteDerivedInfoEnricher {
        public string ContractId => "derived-info-test-v1";
        internal List<CharacterNoteDerivedInfoEnrichmentRequest> Requests {
            get;
        } = [];

        public virtual ValueTask<IReadOnlyList<CharacterNoteDerivedInfo>>
            EnrichAsync(
            CharacterNoteDerivedInfoEnrichmentRequest request,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult<IReadOnlyList<
                CharacterNoteDerivedInfo>>(request.Targets.Select(
                    static target => new CharacterNoteDerivedInfo(
                        target.ArtifactOrdinal,
                        $"Title {target.ArtifactOrdinal}",
                        $"Gist {target.ArtifactOrdinal}.",
                        $"Summary {target.ArtifactOrdinal}"
                    )
                ).ToArray());
        }
    }

    private sealed class ThrowingEnricher
        : ICharacterNoteDerivedInfoEnricher {
        public string ContractId => "throwing-derived-info-test-v1";
        internal int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<CharacterNoteDerivedInfo>> EnrichAsync(
            CharacterNoteDerivedInfoEnrichmentRequest request,
            CancellationToken cancellationToken
        ) {
            _ = request;
            _ = cancellationToken;
            CallCount++;
            throw new InvalidDataException("invalid provider output");
        }
    }

    private sealed class BlockingEnricher : RecordingEnricher {
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal int CallCount { get; private set; }

        public override async ValueTask<IReadOnlyList<
            CharacterNoteDerivedInfo>> EnrichAsync(
            CharacterNoteDerivedInfoEnrichmentRequest request,
            CancellationToken cancellationToken
        ) {
            CallCount++;
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.EnrichAsync(request, cancellationToken);
        }
    }

    private enum FreezeFault {
        None,
        BeforePublish,
        BlockBeforePublish,
        AfterPublishLoseRecoveryOpen,
    }

    private sealed class FaultingPodAccess : ICharacterNoteDefaultPodAccess {
        internal FreezeFault NextFreezeFault { get; set; }
        internal bool ReportDerivedInfoCapacityFull { get; set; }
        internal TaskCompletionSource FreezeStarted { get; private set; } =
            NewGate();
        internal TaskCompletionSource ReleaseFreeze { get; private set; } =
            NewGate();
        private bool _failNextOpen;

        internal void ArmBlockingFreeze() {
            FreezeStarted = NewGate();
            ReleaseFreeze = NewGate();
            NextFreezeFault = FreezeFault.BlockBeforePublish;
        }

        public ICharacterNoteDefaultPodHandle Create(
            string rootPath,
            MemoPodId podId,
            string topic
        ) => Wrap(CharacterNoteMemoPodAccess.Instance.Create(
            rootPath,
            podId,
            topic
        ));

        public ICharacterNoteDefaultPodHandle Open(
            string rootPath,
            MemoPodId podId
        ) {
            if (_failNextOpen) {
                _failNextOpen = false;
                throw Failure("recovery open");
            }
            return Wrap(CharacterNoteMemoPodAccess.Instance.Open(
                rootPath,
                podId
            ));
        }

        private ICharacterNoteDefaultPodHandle Wrap(
            ICharacterNoteDefaultPodHandle inner
        ) => new Handle(this, inner);

        private static CharacterNoteDefaultPodAccessException Failure(
            string operation
        ) => new(
            CharacterNoteDefaultPodFailureKind.IoFailure,
            $"simulated {operation} failure"
        );

        private static TaskCompletionSource NewGate() => new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        private sealed class Handle(
            FaultingPodAccess owner,
            ICharacterNoteDefaultPodHandle inner
        ) : ICharacterNoteDefaultPodHandle {
            public MemoPodId PodId => inner.PodId;
            public MemoPodPhase Phase => inner.Phase;
            public int ActiveMemoCount => inner.ActiveMemoCount;
            public int ActiveExactTextUtf8Bytes =>
                inner.ActiveExactTextUtf8Bytes;
            public int ActiveDerivedInfoUtf8Bytes =>
                owner.ReportDerivedInfoCapacityFull
                    ? MemoPodLimits
                        .MaximumActiveMemoDerivedInfoUtf8Bytes
                    : inner.ActiveDerivedInfoUtf8Bytes;
            public MemoId Append(string exactText) => inner.Append(exactText);
            public Memo Get(MemoId id) => inner.Get(id);
            public void UpdateDerivedInfo(
                MemoId id,
                string title,
                string gist,
                string summary
            ) => inner.UpdateDerivedInfo(id, title, gist, summary);
            public void ResumeEditing() => inner.ResumeEditing();
            public string ComputeStateIdentity() =>
                inner.ComputeStateIdentity();

            public async Task FreezeAsync(
                CancellationToken cancellationToken = default
            ) {
                FreezeFault fault = owner.NextFreezeFault;
                owner.NextFreezeFault = FreezeFault.None;
                if (fault is FreezeFault.BeforePublish) {
                    throw Failure("pre-publish");
                }
                if (fault is FreezeFault.BlockBeforePublish) {
                    owner.FreezeStarted.SetResult();
                    await owner.ReleaseFreeze.Task.WaitAsync(
                        cancellationToken
                    );
                }
                await inner.FreezeAsync(cancellationToken);
                if (fault is FreezeFault.AfterPublishLoseRecoveryOpen) {
                    owner._failNextOpen = true;
                    throw Failure("post-publish response");
                }
            }

            public void ConfirmCurrentDocumentDurability() =>
                inner.ConfirmCurrentDocumentDurability();
        }
    }

    private sealed class Fixture : IDisposable {
        private readonly FixturePaths _paths;
        private readonly ICharacterNoteDefaultPodAccess _access;
        private readonly ICharacterNoteExtractor _extractor;

        private Fixture(
            FixturePaths paths,
            SessionJournalEngine engine,
            CharacterNoteDefaultPodReconciler reconciler,
            ICharacterNoteExtractor extractor,
            ICharacterNoteDefaultPodAccess access
        ) {
            _paths = paths;
            Engine = engine;
            Reconciler = reconciler;
            _extractor = extractor;
            _access = access;
        }

        internal string StatePath => _paths.StatePath;
        internal SessionJournalEngine Engine { get; }
        internal CharacterNoteDefaultPodReconciler Reconciler {
            get;
            private set;
        }

        internal static async ValueTask<Fixture> CreateAsync(
            ICharacterNoteDefaultPodAccess? access = null,
            ICharacterNoteExtractor? extractor = null
        ) {
            var paths = new FixturePaths();
            SessionJournalEngine engine = SessionJournalEngine.Create(
                paths.SessionPath,
                new SessionCreateOptions(
                    "model-a",
                    "system-a",
                    "surface-a"
                )
            );
            ICharacterNoteExtractor selectedExtractor = extractor
                ?? new RecordingNoteExtractor();
            CharacterMemorySqliteStore store =
                CharacterMemorySqliteStore.CreateNew(
                    paths.StatePath,
                    Owner(engine),
                    Baseline(engine),
                    CharacterNoteDefaultPodV1.EmptyStateIdentity
                );
            ICharacterNoteDefaultPodAccess selected = access
                ?? CharacterNoteMemoPodAccess.Instance;
            CharacterNoteDefaultPodReconciler reconciler =
                await CharacterNoteDefaultPodReconciler.AttachAsync(
                    store,
                    selectedExtractor,
                    selected
                );
            return new Fixture(
                paths,
                engine,
                reconciler,
                selectedExtractor,
                selected
            );
        }

        internal ValueTask<CharacterNoteDerivedInfoEnrichmentRequest>
            Materialize(
            CharacterMemoryDerivedInfoWorkSnapshot work,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(
            CharacterNoteDerivedInfoContextMaterializer.Materialize(
                Engine,
                work,
                cancellationToken
            )
        );

        internal (EventAddress,
            GalateaTerminalActionExtractionTarget) AppendTurn(
            string observation,
            string visibleAction
        ) {
            EventAddress source = AppendAction(observation, visibleAction);
            return (source, new GalateaTerminalActionExtractionTarget(
                source,
                visibleAction
            ));
        }

        internal EventAddress AppendAction(
            string observation,
            string visibleAction
        ) {
            _ = Engine.AppendObservation(observation);
            return Engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text(visibleAction)
                ]),
                Invocation
            );
        }

        internal async ValueTask ReopenAsync() {
            Reconciler.Dispose();
            CharacterMemorySqliteStore store =
                CharacterMemorySqliteStore.OpenExisting(
                    _paths.StatePath,
                    Owner(Engine)
                );
            Reconciler = await CharacterNoteDefaultPodReconciler.AttachAsync(
                store,
                _extractor,
                _access
            );
        }

        public void Dispose() {
            Reconciler.Dispose();
            Engine.Dispose();
            _paths.Dispose();
        }
    }

    private sealed class FixturePaths : IDisposable {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-info-reconciler-tests-"
                + Guid.NewGuid().ToString("N")
        );

        internal FixturePaths() => Directory.CreateDirectory(_root);
        internal string SessionPath => Path.Combine(_root, "session");
        internal string StatePath => Path.Combine(_root, "memory");

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
    }

    private static CharacterMemoryStoreOwner Owner(
        SessionJournalEngine engine
    ) => new("user", engine.Path);

    private static CharacterMemoryStoreBaseline Baseline(
        SessionJournalEngine engine
    ) => new(
        engine.ReadView.ReadPhysicalAppendFrontier(),
        EventAddressTextCodec.FormatNullable(engine.ReadCurrentHead())
    );
}
