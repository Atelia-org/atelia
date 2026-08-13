using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Getter.Tests;

public sealed partial class GetterVerticalTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public async Task ExactCurrentFulfillmentMaterializesNeutralCandidate() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;

        RecapGridContextSelection selection = Assert.IsType<
            RecapGridContextResolveResult.Selected
        >(getter.Resolve(boundary, 0)).Selection;
        RecapGridContextMaterializeResult.Available materialized = Assert.IsType<
            RecapGridContextMaterializeResult.Available
        >(getter.Materialize(selection));

        Assert.Equal(fixture.Rows[^1].Descriptor.EndInclusive,
            materialized.Candidate.SetAdmissionAnchor);
        Assert.Equal(fixture.Rows[^1].Descriptor.EndSetups,
            materialized.Candidate.AnchorSetups);
        SessionContextContribution contribution = Assert.Single(
            materialized.Candidate.Contributions
        );
        string expectedContent = $"recap-{fixture.Rows.Count - 1}";
        Assert.Equal(expectedContent, contribution.ExactText);
        Assert.Equal(fixture.Rows[^1].Descriptor.EndInclusive,
            contribution.AbsorbedThrough);
        Assert.Equal(
            SessionContextContributionHasher.ComputeSha256(expectedContent),
            contribution.ContentSha256
        );
        Assert.Equal(
            RecapGridProvenanceStatus.Verified,
            materialized.Provenance.MembershipComplete
        );

        SessionContextCandidateSelection neutral = await getter.SelectAsync(
            new SessionContextSelectionRequest(boundary, 0),
            CancellationToken.None
        );
        Assert.Equal(SessionContextCandidateSelectionStatus.Selected,
            neutral.Status);
        Assert.True(neutral.Candidate!.Handle.Length <= 512);
        Assert.True(neutral.Candidate.SnapshotToken.Length <= 512);
        SessionContextCandidateMaterializationResult.Materialized neutralValue =
            Assert.IsType<
                SessionContextCandidateMaterializationResult.Materialized>(
                await getter.MaterializeAsync(
                    neutral.Candidate,
                    CancellationToken.None
                )
            );
        Assert.Equal(expectedContent, Assert.Single(
            neutralValue.Candidate.Contributions).ExactText);
    }

    [Fact]
    public async Task NthPreviousFollowsExactViewAndTimelinePredecessors() {
        using Fixture fixture = await CreateBuiltFixture(turns: 3);
        Assert.True(fixture.Rows.Count >= 3);
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;

        RecapGridContextSelection current = Assert.IsType<
            RecapGridContextResolveResult.Selected
        >(getter.Resolve(boundary, 0)).Selection;
        RecapGridContextSelection previous = Assert.IsType<
            RecapGridContextResolveResult.Selected
        >(getter.Resolve(boundary, 1)).Selection;
        RecapGridContextSelection secondPrevious = Assert.IsType<
            RecapGridContextResolveResult.Selected
        >(getter.Resolve(boundary, 2)).Selection;

        Assert.Equal(fixture.Rows[^1].Descriptor.RowId, current.SelectedRowId);
        Assert.Equal(
            fixture.Rows[^1].Descriptor.PreviousRowId,
            previous.SelectedRowId
        );
        Assert.Equal(
            "recap-" + (fixture.Rows.Count - 2),
            Assert.Single(Assert.IsType<
                RecapGridContextMaterializeResult.Available>(
                getter.Materialize(previous)
            ).Candidate.Contributions).ExactText
        );
        Assert.Equal(
            "recap-" + (fixture.Rows.Count - 3),
            Assert.Single(Assert.IsType<
                RecapGridContextMaterializeResult.Available>(
                getter.Materialize(secondPrevious)
            ).Candidate.Contributions).ExactText
        );
        Assert.IsType<RecapGridContextResolveResult.OrdinalUnavailable>(
            getter.Resolve(boundary, fixture.Rows.Count)
        );
        Assert.IsType<RecapGridContextResolveResult.LimitExceeded>(
            getter.Resolve(
                boundary,
                RecapGridGetterLimits.MaximumNthPrevious + 1
            )
        );
    }

    [Fact]
    public async Task NoActiveAndEmptyTimelineActiveAuthorizeRawWithoutStore() {
        using Fixture noActive = await CreateControlFixture(
            turns: 1,
            activate: false,
            createStore: false
        );
        using RecapGridContextHandle noActiveGetter = OpenGetter(
            noActive.Journal
        );
        EventAddress mature = noActive.Journal.ReadCurrentHead()!.Value;
        Assert.IsType<RecapGridContextResolveResult.RawHistoryAuthorized>(
            noActiveGetter.Resolve(mature, 0)
        );
        Assert.Equal(
            SessionContextLifecycleStatus.RawHistoryAuthorized,
            (await noActiveGetter.PrepareAsync(
                noActive.Journal.ReadView,
                new SessionContextLifecycleRequest(
                    new SessionContextSelectionRequest(mature, 0),
                    SessionExecutionPhase.Idle,
                    SessionContextLifecycleTrigger.PreObservation,
                    "pending"
                ),
                CancellationToken.None
            )).Status
        );

        using Fixture emptyActive = await CreateControlFixture(
            turns: 0,
            activate: true,
            createStore: false
        );
        using RecapGridContextHandle emptyGetter = OpenGetter(
            emptyActive.Journal
        );
        Assert.IsType<RecapGridContextResolveResult.RawHistoryAuthorized>(
            emptyGetter.Resolve(
                emptyActive.Journal.ReadCurrentHead()!.Value,
                0
            )
        );
    }

    [Fact]
    public async Task NonemptyActiveMissingCurrentFulfillmentNeverFallsBackRaw() {
        using Fixture fixture = await CreateControlFixture(
            turns: 1,
            activate: true,
            createStore: false
        );
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        RecapGridContextResolveResult.Unfulfilled missing = Assert.IsType<
            RecapGridContextResolveResult.Unfulfilled
        >(getter.Resolve(fixture.Journal.ReadCurrentHead()!.Value, 0));
        Assert.Equal(fixture.Recipe.Digest, missing.Key.RecipeDigest);
        SessionContextCandidateSelection neutral = await getter.SelectAsync(
            new SessionContextSelectionRequest(
                fixture.Journal.ReadCurrentHead()!.Value,
                0
            ),
            CancellationToken.None
        );
        Assert.Equal(
            SessionContextCandidateSelectionStatus.StoreUnavailable,
            neutral.Status
        );
    }

    [Fact]
    public async Task DisposeRejectsTypedReadsAndMaterialization() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;
        RecapGridContextSelection selection = Assert.IsType<
            RecapGridContextResolveResult.Selected
        >(getter.Resolve(boundary, 0)).Selection;
        getter.Dispose();

        Assert.IsType<RecapGridContextResolveResult.Disposed>(
            getter.Resolve(boundary, 0)
        );
        Assert.IsType<RecapGridContextMaterializeResult.Disposed>(
            getter.Materialize(selection)
        );
    }

    private async Task<Fixture> CreateBuiltFixture(
        int turns,
        Func<int, string>? contentFactory = null,
        int maximumContentUtf8Bytes = 16 * 1024
    ) {
        Fixture fixture = await CreateControlFixture(
            turns,
            activate: true,
            createStore: true,
            maximumContentUtf8Bytes
        );
        using RecapGridStoreHandle store = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(fixture.Path)).Handle;
        RecapRowView? previous = null;
        var cells = new List<RecapCellArtifact>();
        for (int index = 0; index < fixture.Rows.Count; index++) {
            HistorySegmentDescriptor descriptor = fixture.Rows[index].Descriptor;
            PriorInputReference prior = previous is null
                ? PriorInputReference.FirstRow.Value
                : new PriorInputReference.Projection(
                    PriorInputProjection.Create([
                        new PriorProjectedContent(
                            cells[^1].LogicalColumnId,
                            cells[^1].ContentDigest
                        )
                    ]).Digest
                );
            EvaluationKey key = EvaluationKey.Create(
                descriptor.DescriptorDigest,
                fixture.Definition.Digest,
                prior
            );
            RecapCellArtifact cell = RecapCellArtifact.Create(
                fixture.Definition.LogicalColumnId,
                fixture.Definition.Digest,
                key,
                RecapCellOutcome.Updated,
                contentFactory?.Invoke(index) ?? $"recap-{index}",
                fixture.Definition.MaxContentUtf8Bytes
            );
            Assert.IsType<RecapGridCellPutResult.Inserted>(
                store.Writer.PutCell(cell)
            );
            RowBuildSpec spec = RowBuildSpec.CreateFull(
                fixture.Recipe,
                new RowViewCoordinate(
                    fixture.Journal.BranchRefId,
                    descriptor.TimelineId,
                    descriptor.RowId,
                    descriptor.DescriptorDigest,
                    fixture.Recipe.Digest,
                    fixture.Recipe.Target.Digest,
                    descriptor.PreviousRowId,
                    previous?.Digest,
                    bootstrapCompleted: true
                ),
                prior,
                [new RowBuildAssignment.Evaluate(
                    fixture.Definition.LogicalColumnId,
                    key
                )]
            );
            RecapRowView view = RecapRowView.Create(spec, [cell]);
            Assert.IsType<RecapGridRowViewPutResult.Inserted>(
                store.Writer.PutRowView(spec, view)
            );
            previous = view;
            cells.Add(cell);
        }
        HistorySegmentDescriptor head = fixture.Rows[^1].Descriptor;
        FulfilledViewKey fulfilled = FulfilledViewKey.Create(
            fixture.Journal.BranchRefId,
            fixture.TimelineHead,
            head.DescriptorDigest,
            fixture.Recipe
        );
        Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
            store.Writer.PutFulfilled(fulfilled, previous!.Digest)
        );
        return fixture;
    }

    private async Task<Fixture> CreateControlFixture(
        int turns,
        bool activate,
        bool createStore,
        int maximumContentUtf8Bytes = 16 * 1024
    ) {
        string path = NewPath();
        SessionJournalEngine journal = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "getter")
        );
        journal.UseRuntime(new SessionRuntime(
            new TextCompletionClient(),
            CompletionTarget: new SessionCompletionTargetIdentity(
                "getter-tests",
                "test",
                "getter-tests-v1",
                "getter-tests-adapter-v1"
            ),
            ContextCandidateSource: new EmptySource(),
            ContextLifecycle: new RawLifecycle()
        ));
        for (int index = 0; index < turns; index++) {
            _ = await journal.SendAsync(
                journal.ReadCurrentHead()!.Value,
                $"observation-{index}"
            );
        }
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 1024,
                    maxRenderedBytes: 1024 * 1024
                ),
                _estimator
            )
        );
        (TimelineHeadRef timelineHead,
            IReadOnlyList<HistoryTimelineSelectedRow> rows) =
            CommitAllRows(journal);
        if (createStore) {
            Assert.IsType<RecapGridStoreCreateResult.Created>(
                RecapGridStoreFactory.Create(path)
            );
        }
        (FamilyDefinition family,
            MaintainerDefinitionRevision definition) = Values(
                maximumContentUtf8Bytes
            );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 1_000_000,
            maximumProjectedCalls: 1024
        );
        ControlHeadRef controlHead = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            admission
        )).Head;
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineHead.TimelineId,
            timelineHead.HeadRowId,
            BuildTarget.Create([
                new BuildTargetColumn(
                    definition.LogicalColumnId,
                    definition.Digest
                )
            ])
        );
        using (RecapGridControlHandle control = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   admission
               )).Handle) {
            controlHead = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutFamilyDefinition(controlHead, family)
            ).Head;
            controlHead = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutMaintainerDefinition(
                    controlHead,
                    definition
                )
            ).Head;
            controlHead = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(
                    controlHead,
                    timelineHead,
                    recipe,
                    rows.Count == 0 ? null : rows[^1].Witness
                )
            ).Head;
            if (activate) {
                controlHead = Assert.IsType<
                    RecapGridControlActivateResult.Applied>(
                    control.Coordinator.CompareExchangeActiveRecipe(
                        controlHead,
                        timelineHead,
                        recipe.Digest,
                        RecapGridControlActivationPurpose.Direct
                    )
                ).Head;
            }
        }
        return new Fixture(
            path,
            journal,
            timelineHead,
            rows,
            admission,
            family,
            definition,
            recipe,
            controlHead
        );
    }

    private (TimelineHeadRef, IReadOnlyList<HistoryTimelineSelectedRow>)
        CommitAllRows(SessionJournalEngine journal) {
        EnsureCadence(journal);
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(journal.ReadView, _estimator)).Handle;
        using RecapGridCadenceHandle cadence = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutable(journal)).Handle;
        using RecapGridCadenceTimelineSealOperation seal = Assert.IsType<
            RecapGridCadenceTimelineSealOpenResult.Opened
        >(cadence.BeginTimelineSeal(timeline)).Operation;
        var rows = new List<HistoryTimelineSelectedRow>();
        while (true) {
            TimelineHeadRef before = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(timeline.Reader.ReadSnapshot()).Head;
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured
            >(timeline.Coordinator.CaptureOnline(
                before,
                journal.ReadView
            )).Capture;
            HistoryTimelinePlanResult plan = seal.PlanNextRow(
                before,
                capture
            );
            if (plan is HistoryTimelinePlanResult.NotEnough
                or HistoryTimelinePlanResult.RecentReserveNotReached) {
                return (before, rows.AsReadOnly());
            }
            HistoryRowCommitCandidate candidate = Assert.IsType<
                HistoryTimelinePlanResult.Selected
            >(plan).Candidate;
            TimelineHeadRef committed = Assert.IsType<
                HistoryTimelineCommitResult.Committed
            >(seal.CommitRow(candidate)).Head;
            rows.Add(Assert.IsType<
                HistoryTimelineReaderRowResult.Selected
            >(timeline.Reader.ReadSelectedRow(
                committed,
                committed.HeadRowId!.Value
            )).Row);
        }
    }

    private static void EnsureCadence(SessionJournalEngine journal) {
        RecapGridCadenceCreateResult result = RecapGridCadenceFactory.Create(
            journal,
            new RecapGridCadencePolicySpec(
                minimumRecentHistoryLoad: 1,
                HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                targetHistoryLoad: 1,
                maxRawEvents: 1024,
                maxRenderedBytes: 1024 * 1024));
        Assert.True(result is RecapGridCadenceCreateResult.Created
            or RecapGridCadenceCreateResult.AlreadyExists,
            $"Cadence create failed: {result.GetType().Name}");
    }

    private static (FamilyDefinition, MaintainerDefinitionRevision) Values(
        int maximumContentUtf8Bytes = 16 * 1024
    ) {
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain one exact hypothesis.",
            [new FamilyToolDefinition(
                "submit",
                "Submit the recap.",
                new FamilyObjectInputSchema([
                    new FamilyToolProperty(
                        "content",
                        new FamilyScalarInputSchema(FamilyScalarType.String),
                        true
                    )
                ])
            )],
            new FamilyOutputProtocol(
                "output-v1",
                "submit",
                FamilyToolChoice.Required,
                false
            ),
            new FamilyInputRenderingProtocol(
                "input-v1",
                "prior-v1",
                "history-v1"
            )
        );
        var capability = new MaintainerCapabilitySpec(
            "runtime-v1",
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1
        );
        MaintainerDefinitionRevision definition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.culprit"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "culprit"
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    "Who is the culprit?",
                    "Maintain the current hypothesis."
                ),
                maximumContentUtf8Bytes
            );
        return (family, definition);
    }

    private RecapGridContextHandle OpenGetter(SessionJournalEngine journal)
        => Assert.IsType<RecapGridContextOpenResult.Opened>(
            RecapGridContextFactory.Open(journal.ReadView, _estimator)
        ).Handle;

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-recap-grid-getter-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private sealed class Fixture : IDisposable {
        internal Fixture(
            string path,
            SessionJournalEngine journal,
            TimelineHeadRef timelineHead,
            IReadOnlyList<HistoryTimelineSelectedRow> rows,
            RecapGridControlAdmission admission,
            FamilyDefinition family,
            MaintainerDefinitionRevision definition,
            GridBuildRecipe recipe,
            ControlHeadRef controlHead
        ) {
            Path = path;
            Journal = journal;
            TimelineHead = timelineHead;
            Rows = rows;
            Admission = admission;
            Family = family;
            Definition = definition;
            Recipe = recipe;
            ControlHead = controlHead;
        }
        internal string Path { get; }
        internal SessionJournalEngine Journal { get; }
        internal TimelineHeadRef TimelineHead { get; }
        internal IReadOnlyList<HistoryTimelineSelectedRow> Rows { get; }
        internal RecapGridControlAdmission Admission { get; }
        internal FamilyDefinition Family { get; }
        internal MaintainerDefinitionRevision Definition { get; }
        internal GridBuildRecipe Recipe { get; }
        internal ControlHeadRef ControlHead { get; }
        public void Dispose() => Journal.Dispose();
    }

    private sealed class TextCompletionClient : ICompletionClient {
        private int _count;
        public string Name => "getter-tests";
        public string ApiSpecId => "getter-tests-v1";
        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text($"answer-{++_count}")
                ]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }

    private sealed class EmptySource : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(new SessionContextCandidateSelection(
            SessionContextCandidateSelectionStatus.EmptyLineage,
            null
        ));

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException();
    }

    private sealed class RawLifecycle : ISessionContextLifecycleCoordinator {
        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(
            SessionContextLifecycleResult.RawHistoryAuthorized
        );
    }
}
