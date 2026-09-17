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
        Assert.Equal(
            fixture.Definition.Target,
            contribution.Target
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
    public async Task PersistedRowWorkProducerOverridesCurrentRootTargetForReadAndMaterialization() {
        using Fixture fixture = await CreateControlFixture(
            turns: 1, activate: true, createStore: true);
        MaintainerDefinitionRevision alternate = MaintainerDefinitionRevision.Create(
            fixture.Definition.LogicalColumnId,
            fixture.Family.Digest,
            new ContextHeaderBlockTarget(
                ContextHeaderCarrier.System,
                "culprit",
                "Old producer heading"
            ),
            fixture.Definition.Capability,
            fixture.Definition.DeclarativeSpec,
            fixture.Definition.MaxContentUtf8Bytes
        );
        using (RecapGridControlHandle control = Assert.IsType<
            RecapGridControlOpenResult.Opened>(RecapGridControlFactory.Open(
                fixture.Path, fixture.Journal.BranchRefId, fixture.Admission)).Handle) {
            ControlHeadRef head = Assert.IsType<
                RecapGridControlSnapshotResult.Available>(
                control.Reader.ReadSnapshot()).Snapshot.Head;
            _ = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutMaintainerDefinition(head, alternate));
        }
        HistorySegmentDescriptor descriptor = Assert.Single(fixture.Rows).Descriptor;
        BuildTarget producer = BuildTarget.Create([
            new BuildTargetColumn(alternate.LogicalColumnId, alternate.Digest)
        ]);
        var key = new RowWorkKey(
            fixture.Journal.BranchRefId,
            descriptor.TimelineId,
            fixture.Recipe.Digest,
            descriptor.RowId
        );
        var work = new RowWork(
            key, producer, null, null,
            [new RowWorkAssignment(alternate.LogicalColumnId, null)]
        );
        var slot = new CellSlot(
            fixture.Recipe.Digest, descriptor.RowId,
            work.WorkId, alternate.LogicalColumnId);
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            fixture.Recipe,
            new RowViewCoordinate(
                fixture.Journal.BranchRefId, descriptor.TimelineId,
                descriptor.RowId, fixture.Recipe.Digest, producer.Digest,
                null, null, bootstrapCompleted: true),
            [new RowBuildAssignment.Evaluate(slot)],
            work
        );
        using (RecapGridStoreHandle store = Assert.IsType<
            RecapGridStoreOpenResult.Opened>(
            RecapGridStoreFactory.Open(fixture.Path)).Handle) {
            Assert.IsType<RecapGridRowWorkPutResult.Inserted>(
                store.Writer.PutRowWork(work));
            RecapCellArtifact cell = Assert.IsType<
                RecapGridCellPutResult.Inserted>(store.Writer.PutCell(
                    spec,
                    RecapCellDraft.Create(slot, alternate.Digest,
                        RecapCellOutcome.Updated, "old-producer-content",
                        alternate.MaxContentUtf8Bytes))).Winner;
            RecapRowView row = Assert.IsType<
                RecapGridRowViewPutResult.Inserted>(
                store.Writer.PutRowView(spec, [cell])).Winner;
            FulfilledViewKey fulfilled = FulfilledViewKey.Create(
                fixture.Journal.BranchRefId, fixture.TimelineHead,
                descriptor.RowId, fixture.Recipe);
            Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
                store.Writer.PutFulfilled(fulfilled, row.Id));
        }
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        RecapGridContextSelection selection = Assert.IsType<
            RecapGridContextResolveResult.Selected>(getter.Resolve(
                fixture.Journal.ReadCurrentHead()!.Value, 0)).Selection;
        SessionContextContribution contribution = Assert.Single(Assert.IsType<
            RecapGridContextMaterializeResult.Available>(
            getter.Materialize(selection)).Candidate.Contributions);
        Assert.Equal("old-producer-content", contribution.ExactText);
        Assert.Equal(alternate.Target, contribution.Target);
    }

    [Fact]
    public async Task CurrentReserveAndNthPreviousUseEachRowsFrozenProducer() {
        using Fixture fixture = await CreateControlFixture(
            turns: 2,
            activate: true,
            createStore: true
        );
        Assert.Equal(3, fixture.Rows.Count);
        MaintainerDefinitionRevision reserveProducer =
            MaintainerDefinitionRevision.Create(
                fixture.Definition.LogicalColumnId,
                fixture.Family.Digest,
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.System,
                    "reserve-producer",
                    "Reserve producer heading"
                ),
                fixture.Definition.Capability,
                new MaintainerDeclarativeSpec(
                    "Reserve producer question",
                    "Maintain the reserve producer context."
                ),
                fixture.Definition.MaxContentUtf8Bytes
            );
        MaintainerDefinitionRevision currentProducer =
            MaintainerDefinitionRevision.Create(
                fixture.Definition.LogicalColumnId,
                fixture.Family.Digest,
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.System,
                    "current-producer",
                    "Current producer heading"
                ),
                fixture.Definition.Capability,
                new MaintainerDeclarativeSpec(
                    "Current producer question",
                    "Maintain the current producer context."
                ),
                fixture.Definition.MaxContentUtf8Bytes
            );
        using (RecapGridControlHandle control = Assert.IsType<
            RecapGridControlOpenResult.Opened>(RecapGridControlFactory.Open(
                fixture.Path,
                fixture.Journal.BranchRefId,
                fixture.Admission
            )).Handle) {
            ControlHeadRef head = Assert.IsType<
                RecapGridControlSnapshotResult.Available>(
                control.Reader.ReadSnapshot()).Snapshot.Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutMaintainerDefinition(
                    head,
                    reserveProducer
                )).Head;
            _ = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutMaintainerDefinition(
                    head,
                    currentProducer
                ));
        }

        MaintainerDefinitionRevision[] producers = [
            fixture.Definition,
            reserveProducer,
            currentProducer
        ];
        RecapRowView? previous = null;
        using (RecapGridStoreHandle store = Assert.IsType<
            RecapGridStoreOpenResult.Opened>(RecapGridStoreFactory.Open(
                fixture.Path
            )).Handle) {
            for (int index = 0; index < fixture.Rows.Count; index++) {
                HistorySegmentDescriptor descriptor =
                    fixture.Rows[index].Descriptor;
                MaintainerDefinitionRevision producer = producers[index];
                BuildTarget target = BuildTarget.Create([
                    new BuildTargetColumn(
                        producer.LogicalColumnId,
                        producer.Digest
                    )
                ]);
                var work = new RowWork(
                    new RowWorkKey(
                        fixture.Journal.BranchRefId,
                        descriptor.TimelineId,
                        fixture.Recipe.Digest,
                        descriptor.RowId
                    ),
                    target,
                    descriptor.PreviousRowId,
                    previous?.Id,
                    [new RowWorkAssignment(producer.LogicalColumnId, null)]
                );
                var slot = new CellSlot(
                    fixture.Recipe.Digest,
                    descriptor.RowId,
                    work.WorkId,
                    producer.LogicalColumnId
                );
                RowBuildSpec spec = RowBuildSpec.CreateFull(
                    fixture.Recipe,
                    new RowViewCoordinate(
                        fixture.Journal.BranchRefId,
                        descriptor.TimelineId,
                        descriptor.RowId,
                        fixture.Recipe.Digest,
                        target.Digest,
                        descriptor.PreviousRowId,
                        previous?.Id,
                        bootstrapCompleted: true
                    ),
                    [new RowBuildAssignment.Evaluate(slot)],
                    work
                );
                Assert.IsType<RecapGridRowWorkPutResult.Inserted>(
                    store.Writer.PutRowWork(work)
                );
                RowWork persisted = Assert.IsType<
                    RecapGridStoreReadResult<RowWork>.Found>(
                    store.Reader.ReadRowWork(work.Key)).Value;
                Assert.Equal(work.WorkId, persisted.WorkId);
                Assert.Equal(previous?.Id, persisted.PreviousRowResultId);
                Assert.Equal(target.Digest, persisted.ProducerTarget.Digest);
                RecapCellArtifact cell = Assert.IsType<
                    RecapGridCellPutResult.Inserted>(store.Writer.PutCell(
                        spec,
                        RecapCellDraft.Create(
                            slot,
                            producer.Digest,
                            RecapCellOutcome.Updated,
                            $"producer-{index}",
                            producer.MaxContentUtf8Bytes
                        )
                    )).Winner;
                previous = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
                    store.Writer.PutRowView(spec, [cell])).Winner;
            }
            Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
                store.Writer.PutFulfilled(
                    FulfilledViewKey.Create(
                        fixture.Journal.BranchRefId,
                        fixture.TimelineHead,
                        fixture.Rows[^1].Descriptor.RowId,
                        fixture.Recipe
                    ),
                    previous!.Id
                )
            );
        }

        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;
        using (RecapGridContextHandle current = OpenGetter(fixture.Journal)) {
            AssertMaterializedProducer(
                current,
                boundary,
                nthPrevious: 0,
                fixture.Rows[^1].Descriptor.RowId,
                currentProducer,
                "producer-2"
            );
        }

        UpdateCadence(
            fixture.Journal,
            FindCrossingRequirement(fixture, boundary)
        );
        using (RecapGridContextHandle reserve = OpenGetter(fixture.Journal)) {
            AssertMaterializedProducer(
                reserve,
                boundary,
                nthPrevious: 0,
                fixture.Rows[^2].Descriptor.RowId,
                reserveProducer,
                "producer-1"
            );
            AssertMaterializedProducer(
                reserve,
                boundary,
                nthPrevious: 1,
                fixture.Rows[^3].Descriptor.RowId,
                fixture.Definition,
                "producer-0"
            );
        }
    }

    [Fact]
    public async Task EvaluatedMemberMustMatchItsFrozenWorkId() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        HistorySegmentDescriptor row = Assert.Single(fixture.Rows).Descriptor;
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(
                fixture.Definition.LogicalColumnId,
                fixture.Definition.Digest
            )
        ]);
        GridBuildRecipe decoyRecipe = GridBuildRecipe.CreateFull(
            fixture.TimelineHead.TimelineId,
            fixture.TimelineHead.HeadRowId,
            target,
            fixture.Recipe.Digest
        );
        CellId replacement;
        RowResultId rootView;
        using (RecapGridStoreHandle store = Assert.IsType<
            RecapGridStoreOpenResult.Opened>(RecapGridStoreFactory.Open(
                fixture.Path
            )).Handle) {
            var decoyWork = new RowWork(
                new RowWorkKey(
                    fixture.Journal.BranchRefId,
                    row.TimelineId,
                    decoyRecipe.Digest,
                    row.RowId
                ),
                target,
                null,
                null,
                [new RowWorkAssignment(
                    fixture.Definition.LogicalColumnId,
                    null
                )]
            );
            var decoySlot = new CellSlot(
                decoyRecipe.Digest,
                row.RowId,
                decoyWork.WorkId,
                fixture.Definition.LogicalColumnId
            );
            RowBuildSpec decoySpec = RowBuildSpec.CreateFull(
                decoyRecipe,
                new RowViewCoordinate(
                    fixture.Journal.BranchRefId,
                    row.TimelineId,
                    row.RowId,
                    decoyRecipe.Digest,
                    target.Digest,
                    null,
                    null,
                    bootstrapCompleted: true
                ),
                [new RowBuildAssignment.Evaluate(decoySlot)],
                decoyWork
            );
            Assert.IsType<RecapGridRowWorkPutResult.Inserted>(
                store.Writer.PutRowWork(decoyWork));
            replacement = Assert.IsType<RecapGridCellPutResult.Inserted>(
                store.Writer.PutCell(
                    decoySpec,
                    RecapCellDraft.Create(
                        decoySlot,
                        fixture.Definition.Digest,
                        RecapCellOutcome.Updated,
                        "wrong-work",
                        fixture.Definition.MaxContentUtf8Bytes
                    ))).Winner.Id;
            rootView = Assert.IsType<
                RecapGridStoreReadResult<RecapRowView>.Found>(
                store.Reader.ReadViewAt(new RowViewAssignmentKey(
                    fixture.Journal.BranchRefId,
                    row.TimelineId,
                    fixture.Recipe.Digest,
                    row.RowId
                ))).Value.Id;
        }
        ExecuteStoreSql(
            fixture.Path,
            "PRAGMA foreign_keys=OFF; UPDATE row_view_member SET cell_id=$replacement WHERE row_result_id=$row;",
            ("$replacement", replacement.Value),
            ("$row", rootView.Value)
        );
        using RecapGridContextHandle corrupted = OpenGetter(fixture.Journal);
        RecapGridContextResolveResult.Invalid invalid = Assert.IsType<
            RecapGridContextResolveResult.Invalid>(corrupted.Resolve(
                fixture.Journal.ReadCurrentHead()!.Value,
                0
            ));
        Assert.Equal("RowWorkMemberMismatch", invalid.Code);
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

    private static void AssertMaterializedProducer(
        RecapGridContextHandle getter,
        EventAddress boundary,
        int nthPrevious,
        HistoryRowId expectedRow,
        MaintainerDefinitionRevision expectedProducer,
        string expectedContent
    ) {
        RecapGridContextSelection selection = Assert.IsType<
            RecapGridContextResolveResult.Selected>(getter.Resolve(
                boundary,
                nthPrevious
            )).Selection;
        Assert.Equal(expectedRow, selection.SelectedRowId);
        RecapGridContextMaterializeResult.Available available = Assert.IsType<
            RecapGridContextMaterializeResult.Available>(
            getter.Materialize(selection));
        SessionContextContribution contribution = Assert.Single(
            available.Candidate.Contributions);
        Assert.Equal(expectedProducer.Target, contribution.Target);
        Assert.Equal(expectedContent, contribution.ExactText);
        Assert.Equal(
            RecapGridProvenanceStatus.Verified,
            available.Provenance.MembershipComplete
        );
        Assert.Equal(
            RecapGridProvenanceStatus.Verified,
            available.Provenance.PriorSourceAligned
        );
        Assert.Equal(
            RecapGridProvenanceStatus.Verified,
            available.Provenance.FullRebuildChain
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
        for (int index = 0; index < fixture.Rows.Count; index++) {
            HistorySegmentDescriptor descriptor = fixture.Rows[index].Descriptor;
            var work = new RowWork(
                new RowWorkKey(
                    fixture.Journal.BranchRefId,
                    descriptor.TimelineId,
                    fixture.Recipe.Digest,
                    descriptor.RowId
                ),
                fixture.Recipe.Target,
                descriptor.PreviousRowId,
                previous?.Id,
                [new RowWorkAssignment(
                    fixture.Definition.LogicalColumnId,
                    null
                )]
            );
            var slot = new CellSlot(
                fixture.Recipe.Digest,
                descriptor.RowId,
                work.WorkId,
                fixture.Definition.LogicalColumnId
            );
            RowBuildSpec spec = RowBuildSpec.CreateFull(
                fixture.Recipe,
                new RowViewCoordinate(fixture.Journal.BranchRefId, descriptor.TimelineId,
                    descriptor.RowId, fixture.Recipe.Digest,
                    fixture.Recipe.Target.Digest, descriptor.PreviousRowId, previous?.Id,
                    bootstrapCompleted: true),
                [new RowBuildAssignment.Evaluate(slot)],
                work);
            Assert.IsType<RecapGridRowWorkPutResult.Inserted>(
                store.Writer.PutRowWork(work));
            RecapCellArtifact cell = Assert.IsType<RecapGridCellPutResult.Inserted>(
                store.Writer.PutCell(spec, RecapCellDraft.Create(slot, fixture.Definition.Digest,
                    RecapCellOutcome.Updated, contentFactory?.Invoke(index) ?? $"recap-{index}",
                    fixture.Definition.MaxContentUtf8Bytes))).Winner;
            previous = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
                store.Writer.PutRowView(spec, [cell])).Winner;
        }
        HistorySegmentDescriptor head = fixture.Rows[^1].Descriptor;
        FulfilledViewKey fulfilled = FulfilledViewKey.Create(
            fixture.Journal.BranchRefId,
            fixture.TimelineHead,
            head.RowId,
            fixture.Recipe
        );
        Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
            store.Writer.PutFulfilled(fulfilled, previous!.Id)
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
                "getter-tests-v1"
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
            [],
            new FamilyOutputProtocol(
                "output-v1",
                FamilyOutputMode.FullReplacementText
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
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.System,
                    "culprit",
                    "Derived context from prior history: culprit"
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
