using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Getter.Tests;

public sealed partial class GetterVerticalTests {
    [Fact]
    public async Task ReserveBootstrapIsDistinctAndSameLifecycleAuthorized() {
        using Fixture fixture = await CreateBuiltFixture(turns: 2);
        UpdateCadence(fixture.Journal, minimumRecentHistoryLoad: 1_000_000);
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;

        RecapGridContextResolveResult.ReserveBootstrapRawOnly bootstrap =
            Assert.IsType<
                RecapGridContextResolveResult.ReserveBootstrapRawOnly>(
                getter.Resolve(boundary, 0)
            );
        Assert.Equal(fixture.TimelineHead, bootstrap.Evidence.TimelineHead);
        Assert.Equal(fixture.Rows.Count, bootstrap.Evidence.VerifiedRows);
        Assert.True(
            bootstrap.Evidence.RetainedHistoryLoad.Value
                < bootstrap.Evidence.RequiredHistoryLoad.Value
        );
        Assert.Equal(
            SessionContextCandidateSelectionStatus.RawHistoryAuthorized,
            (await getter.SelectAsync(
                new SessionContextSelectionRequest(boundary, 0),
                CancellationToken.None
            )).Status
        );
        Assert.Equal(
            SessionContextLifecycleStatus.RawHistoryAuthorized,
            (await getter.PrepareAsync(
                fixture.Journal.ReadView,
                new SessionContextLifecycleRequest(
                    new SessionContextSelectionRequest(boundary, 0),
                    SessionExecutionPhase.Idle,
                    SessionContextLifecycleTrigger.PreObservation,
                    "pending"
                ),
                CancellationToken.None
            )).Status
        );
    }

    [Fact]
    public async Task CadenceHeadFencesSelectionAndPolicyMismatchFailsClosed() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;
        RecapGridContextSelection selected = Select(getter, boundary);

        UpdateCadence(fixture.Journal, minimumRecentHistoryLoad: 2);
        RecapGridContextMaterializeResult.Stale stale = Assert.IsType<
            RecapGridContextMaterializeResult.Stale>(
            getter.Materialize(selected)
        );
        Assert.Equal(RecapGridContextComponent.Cadence, stale.Component);

        UpdateCadence(
            fixture.Journal,
            minimumRecentHistoryLoad: 2,
            targetHistoryLoad: 2
        );
        using RecapGridContextHandle mismatched = OpenGetter(fixture.Journal);
        RecapGridContextResolveResult.Invalid invalid = Assert.IsType<
            RecapGridContextResolveResult.Invalid>(
            mismatched.Resolve(boundary, 0)
        );
        Assert.Equal("CadenceTimelinePolicyMismatch", invalid.Code);
    }

    [Fact]
    public async Task ReserveBootstrapNeverMasksUnhealthyCrossedArtifacts() {
        using Fixture fixture = await CreateBuiltFixture(turns: 2);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;
        RowViewDigest crossedDigest;
        CellDigest crossedCell;
        using (RecapGridContextHandle getter = OpenGetter(fixture.Journal)) {
            RecapGridContextSelection current = Select(getter, boundary);
            crossedDigest = current.SelectedView.PreviousViewDigest!.Value;
        }
        using (RecapGridStoreReaderHandle store = Assert.IsType<
               RecapGridStoreReaderOpenResult.Opened>(
               RecapGridStoreFactory.OpenReader(fixture.Path)).Handle) {
            RecapRowView crossed = Assert.IsType<
                RecapGridStoreReadResult<RecapRowView>.Found>(
                store.Reader.ReadView(crossedDigest)
            ).Value;
            crossedCell = Assert.Single(crossed.OrderedCells).CellDigest;
        }
        UpdateCadence(fixture.Journal, minimumRecentHistoryLoad: 1_000_000);
        ExecuteStoreSql(
            fixture.Path,
            "PRAGMA foreign_keys=OFF; DELETE FROM cell_artifact WHERE cell_digest=$digest;",
            ("$digest", crossedCell.Value)
        );

        using RecapGridContextHandle unhealthy = OpenGetter(fixture.Journal);
        RecapGridContextResolveResult.Invalid invalid = Assert.IsType<
            RecapGridContextResolveResult.Invalid>(
            unhealthy.Resolve(boundary, 0)
        );
        Assert.Equal(RecapGridContextComponent.Store, invalid.Component);
        Assert.NotEmpty(invalid.Code);
    }

    [Fact]
    public async Task NthPreviousStartsOnlyAfterRecentReserveAnchor() {
        using Fixture fixture = await CreateBuiltFixture(turns: 2);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;
        long crossingRequirement = FindCrossingRequirement(
            fixture,
            boundary
        );
        UpdateCadence(fixture.Journal, crossingRequirement);
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);

        RecapGridContextSelection anchor = Select(getter, boundary, nth: 0);
        Assert.Equal(
            fixture.Rows[^2].Descriptor.RowId,
            anchor.SelectedRowId
        );
        RecapGridContextSelection previous = Select(
            getter,
            boundary,
            nth: 1
        );
        Assert.Equal(
            fixture.Rows[^3].Descriptor.RowId,
            previous.SelectedRowId
        );
    }

    [Fact]
    public async Task ReentrantDisposeClosesAfterCurrentResolve() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        RecapGridContextHandle? getter = null;
        getter = OpenGetterForTest(
            fixture.Journal,
            new GetterTestHooks(BeforeTerminalFence: _ => getter!.Dispose())
        );
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;

        Assert.IsType<RecapGridContextResolveResult.Selected>(
            getter.Resolve(boundary, 0)
        );
        Assert.IsType<RecapGridContextResolveResult.Disposed>(
            getter.Resolve(boundary, 0)
        );
        getter.Dispose();
    }

    [Fact]
    public async Task SelectionsAndNeutralDescriptorsAreOwnedByOneGetter() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        using RecapGridContextHandle first = OpenGetter(fixture.Journal);
        using RecapGridContextHandle second = OpenGetter(fixture.Journal);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;

        RecapGridContextSelection selection = Select(first, boundary);
        RecapGridContextMaterializeResult.Stale direct = Assert.IsType<
            RecapGridContextMaterializeResult.Stale>(
            second.Materialize(selection)
        );
        Assert.Equal(RecapGridContextComponent.Store, direct.Component);

        SessionContextCandidateDescriptor descriptor = Assert.IsType<
            SessionContextCandidateSelection>(await first.SelectAsync(
            new SessionContextSelectionRequest(boundary, 0),
            CancellationToken.None
        )).Candidate!;
        Assert.IsType<SessionContextCandidateMaterializationResult.Stale>(
            await second.MaterializeAsync(descriptor, CancellationToken.None)
        );
    }

    [Fact]
    public async Task MaterializeValidatesOriginalWitnessScopeAndMapsComponent() {
        using Fixture ownerFixture = await CreateBuiltFixture(turns: 1);
        using Fixture foreignFixture = await CreateBuiltFixture(turns: 1);
        using RecapGridContextHandle owner = OpenGetter(ownerFixture.Journal);
        using RecapGridContextHandle foreign = OpenGetter(foreignFixture.Journal);
        RecapGridContextSelection expected = Select(
            owner,
            ownerFixture.Journal.ReadCurrentHead()!.Value
        );
        RecapGridContextSelection foreignSelection = Select(
            foreign,
            foreignFixture.Journal.ReadCurrentHead()!.Value
        );

        RecapGridContextMaterializeResult.Available available = Assert.IsType<
            RecapGridContextMaterializeResult.Available>(
            owner.Materialize(expected)
        );
        Assert.NotEmpty(available.Candidate.Contributions);

        var forged = new RecapGridContextSelection(
            expected.CompletionBoundary,
            expected.NthPrevious,
            expected.TimelineHead,
            expected.CadenceHead,
            expected.ControlHead,
            expected.StoreIdentity,
            expected.Recipe,
            foreignSelection.SelectedRow,
            expected.SelectedView,
            expected.CurrentFulfilledKey,
            expected.CurrentViewDigest,
            expected.Owner,
            expected.OwnerNonce,
            expected.HandleToken,
            expected.SnapshotToken
        );
        RecapGridContextMaterializeResult.Invalid invalid = Assert.IsType<
            RecapGridContextMaterializeResult.Invalid>(
            owner.Materialize(forged)
        );
        Assert.Equal(RecapGridContextComponent.Timeline, invalid.Component);
        Assert.Equal("AncestorWitnessScopeMismatch", invalid.Code);

        RecapGridContextMaterializeResult.Invalid unsupported = Assert.IsType<
            RecapGridContextMaterializeResult.Invalid>(
            RecapGridContextHandle.MapResolveForMaterialize(
                new RecapGridContextResolveResult.UnsupportedSchema(
                    RecapGridContextComponent.Control,
                    99
                )
            )
        );
        Assert.Equal(RecapGridContextComponent.Control, unsupported.Component);
        RecapGridContextMaterializeResult.Invalid offPath = Assert.IsType<
            RecapGridContextMaterializeResult.Invalid>(
            RecapGridContextHandle.MapResolveForMaterialize(
                new RecapGridContextResolveResult.NotOnSelectedPath(
                    expected.SelectedRowId
                )
            )
        );
        Assert.Equal(RecapGridContextComponent.Timeline, offPath.Component);
    }

    [Fact]
    public async Task NeutralPhaseTwoMapsControlUnsupportedSchemaToInvalid() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;
        SessionContextCandidateDescriptor descriptor = Assert.IsType<
            SessionContextCandidateSelection>(await getter.SelectAsync(
            new SessionContextSelectionRequest(boundary, 0),
            CancellationToken.None
        )).Candidate!;

        var paths = new ControlPaths(
            fixture.Path,
            fixture.Journal.BranchRefId,
            fixture.TimelineHead.TimelineId
        );
        byte[] current = File.ReadAllBytes(paths.StatePath);
        byte[] prefix = "{\"schemaVersion\":2,"u8.ToArray();
        Assert.True(current.AsSpan().StartsWith(prefix));
        byte[] unsupported = [
            .. "{\"schemaVersion\":99,"u8.ToArray(),
            .. current.AsSpan(prefix.Length)
        ];
        File.WriteAllBytes(paths.StatePath, unsupported);

        SessionContextCandidateMaterializationResult.Invalid invalid =
            Assert.IsType<
                SessionContextCandidateMaterializationResult.Invalid>(
                await getter.MaterializeAsync(
                    descriptor,
                    CancellationToken.None
                )
            );
        Assert.Contains(
            "Control schema 99 is unsupported.",
            invalid.Detail,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task SemanticTerminalsAreFencedAfterDeterministicHook() {
        using Fixture missing = await CreateControlFixture(
            turns: 1,
            activate: true,
            createStore: false
        );
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(
            HistoryTimelineFactory.Open(missing.Journal.ReadView, _estimator)
        ).Handle;
        PartitionPolicyRevision nextPolicy = PartitionPolicyRevision.Create(
            missing.TimelineHead.TimelineId,
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(2),
            maxRawEvents: 64,
            maxRenderedBytes: 1024 * 1024
        );
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            timeline.Coordinator.PutPolicy(nextPolicy)
        );
        bool unfulfilledHook = false;
        using RecapGridContextHandle unfulfilled = OpenGetterForTest(
            missing.Journal,
            new GetterTestHooks(BeforeTerminalFence: result => {
                Assert.IsType<RecapGridContextResolveResult.Unfulfilled>(result);
                unfulfilledHook = true;
                Assert.IsType<HistoryTimelinePolicyCasResult.Applied>(
                    timeline.Coordinator.CompareExchangePolicy(
                        missing.TimelineHead,
                        nextPolicy.PolicyDigest
                    )
                );
            })
        );
        RecapGridContextResolveResult.Stale unfulfilledStale = Assert.IsType<
            RecapGridContextResolveResult.Stale>(unfulfilled.Resolve(
            missing.Journal.ReadCurrentHead()!.Value,
            0
        ));
        Assert.True(unfulfilledHook);
        Assert.Equal(
            RecapGridContextComponent.Timeline,
            unfulfilledStale.Component
        );

        using Fixture ordinalFixture = await CreateBuiltFixture(turns: 1);
        using RecapGridControlHandle control = Assert.IsType<
            RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(
                ordinalFixture.Path,
                ordinalFixture.Journal.BranchRefId,
                ordinalFixture.Admission
            )
        ).Handle;
        bool ordinalHook = false;
        using RecapGridContextHandle ordinal = OpenGetterForTest(
            ordinalFixture.Journal,
            new GetterTestHooks(BeforeTerminalFence: result => {
                Assert.IsType<RecapGridContextResolveResult.OrdinalUnavailable>(
                    result
                );
                ordinalHook = true;
                Assert.IsType<RecapGridControlActivateResult.Applied>(
                    control.Coordinator.CompareExchangeActiveRecipe(
                        ordinalFixture.ControlHead,
                        ordinalFixture.TimelineHead,
                        nextRecipeDigest: null,
                        purpose: RecapGridControlActivationPurpose.Direct
                    )
                );
            })
        );
        RecapGridContextResolveResult.Stale ordinalStale = Assert.IsType<
            RecapGridContextResolveResult.Stale>(ordinal.Resolve(
            ordinalFixture.Journal.ReadCurrentHead()!.Value,
            ordinalFixture.Rows.Count
        ));
        Assert.True(ordinalHook);
        Assert.Equal(RecapGridContextComponent.Control, ordinalStale.Component);
    }

    [Fact]
    public async Task RawAndSelectedTerminalsAreAlsoFencedAfterHook() {
        using Fixture rawFixture = await CreateControlFixture(
            turns: 1,
            activate: false,
            createStore: false
        );
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(HistoryTimelineFactory.Open(
            rawFixture.Journal.ReadView,
            _estimator
        )).Handle;
        PartitionPolicyRevision nextPolicy = PartitionPolicyRevision.Create(
            rawFixture.TimelineHead.TimelineId,
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(2),
            maxRawEvents: 64,
            maxRenderedBytes: 1024 * 1024
        );
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            timeline.Coordinator.PutPolicy(nextPolicy)
        );
        using RecapGridContextHandle raw = OpenGetterForTest(
            rawFixture.Journal,
            new GetterTestHooks(BeforeTerminalFence: result => {
                Assert.IsType<RecapGridContextResolveResult.RawHistoryAuthorized>(
                    result
                );
                Assert.IsType<HistoryTimelinePolicyCasResult.Applied>(
                    timeline.Coordinator.CompareExchangePolicy(
                        rawFixture.TimelineHead,
                        nextPolicy.PolicyDigest
                    )
                );
            })
        );
        Assert.Equal(
            RecapGridContextComponent.Timeline,
            Assert.IsType<RecapGridContextResolveResult.Stale>(raw.Resolve(
                rawFixture.Journal.ReadCurrentHead()!.Value,
                0
            )).Component
        );

        using Fixture selectedFixture = await CreateBuiltFixture(turns: 1);
        using RecapGridControlHandle control = Assert.IsType<
            RecapGridControlOpenResult.Opened>(RecapGridControlFactory.Open(
            selectedFixture.Path,
            selectedFixture.Journal.BranchRefId,
            selectedFixture.Admission
        )).Handle;
        using RecapGridContextHandle selected = OpenGetterForTest(
            selectedFixture.Journal,
            new GetterTestHooks(BeforeTerminalFence: result => {
                Assert.IsType<RecapGridContextResolveResult.Selected>(result);
                Assert.IsType<RecapGridControlActivateResult.Applied>(
                    control.Coordinator.CompareExchangeActiveRecipe(
                        selectedFixture.ControlHead,
                        selectedFixture.TimelineHead,
                        nextRecipeDigest: null,
                        purpose: RecapGridControlActivationPurpose.Direct
                    )
                );
            })
        );
        Assert.Equal(
            RecapGridContextComponent.Control,
            Assert.IsType<RecapGridContextResolveResult.Stale>(
                selected.Resolve(
                    selectedFixture.Journal.ReadCurrentHead()!.Value,
                    0
                )
            ).Component
        );
    }

    [Fact]
    public async Task ProvenanceUsesOneSharedExactReadBudget() {
        using Fixture fixture = await CreateBuiltFixture(turns: 3);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;
        RecapGridContextProvenance baseline;
        using (RecapGridContextHandle getter = OpenGetter(fixture.Journal)) {
            baseline = Assert.IsType<RecapGridContextMaterializeResult.Available>(
                getter.Materialize(Select(getter, boundary))
            ).Provenance;
        }
        Assert.Equal(fixture.Rows.Count, baseline.ExaminedRows);
        Assert.Equal(fixture.Rows.Count, baseline.ExaminedCells);
        Assert.True(baseline.ExaminedCanonicalUtf8Bytes > 0);
        Assert.Equal(
            RecapGridProvenanceStatus.Verified,
            baseline.PriorInputAligned
        );
        Assert.Equal(
            RecapGridProvenanceStatus.Verified,
            baseline.FullRebuildChain
        );

        using (RecapGridContextHandle exact = OpenGetterForTest(
                   fixture.Journal,
                   new GetterTestHooks(ProvenanceBudget:
                       new GetterProvenanceReadBudget(
                           baseline.ExaminedRows,
                           baseline.ExaminedCells,
                           baseline.ExaminedCanonicalUtf8Bytes
                       )))) {
            RecapGridContextProvenance observed = Assert.IsType<
                RecapGridContextMaterializeResult.Available>(
                exact.Materialize(Select(exact, boundary))
            ).Provenance;
            Assert.Equal(baseline, observed);
        }

        using RecapGridContextHandle capped = OpenGetterForTest(
            fixture.Journal,
            new GetterTestHooks(ProvenanceBudget:
                new GetterProvenanceReadBudget(
                    baseline.ExaminedRows - 1,
                    baseline.ExaminedCells,
                    baseline.ExaminedCanonicalUtf8Bytes
                ))
        );
        RecapGridContextMaterializeResult.Available cappedAvailable =
            Assert.IsType<RecapGridContextMaterializeResult.Available>(
                capped.Materialize(Select(capped, boundary))
            );
        Assert.Equal(
            RecapGridProvenanceStatus.Incomplete,
            cappedAvailable.Provenance.FullRebuildChain
        );
        Assert.Equal(
            baseline.ExaminedRows - 1,
            cappedAvailable.Provenance.ExaminedRows
        );
        Assert.Equal(
            baseline.ExaminedCells - 1,
            cappedAvailable.Provenance.ExaminedCells
        );
        Assert.True(
            cappedAvailable.Provenance.ExaminedCanonicalUtf8Bytes
                < baseline.ExaminedCanonicalUtf8Bytes
        );

        int byteCap = baseline.ExaminedCanonicalUtf8Bytes - 1;
        using RecapGridContextHandle byteCapped = OpenGetterForTest(
            fixture.Journal,
            new GetterTestHooks(ProvenanceBudget:
                new GetterProvenanceReadBudget(
                    baseline.ExaminedRows,
                    baseline.ExaminedCells,
                    byteCap
                ))
        );
        RecapGridContextProvenance byteEvidence = Assert.IsType<
            RecapGridContextMaterializeResult.Available>(
            byteCapped.Materialize(Select(byteCapped, boundary))
        ).Provenance;
        Assert.Equal(
            RecapGridProvenanceStatus.Incomplete,
            byteEvidence.FullRebuildChain
        );
        // The single artifact that crosses the byte ceiling is still counted;
        // no unaccounted lookup can make an incomplete proof look verified.
        Assert.True(byteEvidence.ExaminedCanonicalUtf8Bytes > byteCap);
        Assert.True(
            byteEvidence.ExaminedCanonicalUtf8Bytes
                <= baseline.ExaminedCanonicalUtf8Bytes
        );

        int predecessorLookups = 0;
        using RecapGridContextHandle oneRow = OpenGetterForTest(
            fixture.Journal,
            new GetterTestHooks(
                ProvenanceBudget: new GetterProvenanceReadBudget(
                    MaximumRows: 1,
                    baseline.ExaminedCells,
                    baseline.ExaminedCanonicalUtf8Bytes
                ),
                BeforeProvenancePredecessorLookup:
                    () => predecessorLookups++
            )
        );
        RecapGridContextProvenance oneRowEvidence = Assert.IsType<
            RecapGridContextMaterializeResult.Available>(
            oneRow.Materialize(Select(oneRow, boundary))
        ).Provenance;
        Assert.Equal(0, predecessorLookups);
        Assert.Equal(1, oneRowEvidence.ExaminedRows);
        Assert.Equal(1, oneRowEvidence.ExaminedCells);
        Assert.Equal(
            RecapGridProvenanceStatus.Incomplete,
            oneRowEvidence.PriorInputAligned
        );
        Assert.Equal(
            RecapGridProvenanceStatus.Incomplete,
            oneRowEvidence.FullRebuildChain
        );
    }

    [Fact]
    public async Task RewindAndSameOrdinalSiblingInvalidateOldSelection() {
        using Fixture fixture = await CreateBuiltFixture(turns: 2);
        Assert.True(fixture.Rows.Count >= 2);
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        EventAddress oldRawHead = fixture.Journal.ReadCurrentHead()!.Value;
        RecapGridContextSelection oldSelection = Select(getter, oldRawHead);
        Assert.NotNull(oldSelection.SelectedView.PreviousViewDigest);

        SessionTurnRetractionResult.Moved moved = Assert.IsType<
            SessionTurnRetractionResult.Moved>(
            fixture.Journal.RewindLatestCompletedTurn(oldRawHead)
        );
        TimelineHeadRef commonHead;
        using (HistoryTimelineHandle timeline = Assert.IsType<
               HistoryTimelineOpenResult.Opened>(HistoryTimelineFactory.Open(
                   fixture.Journal.ReadView,
                   _estimator
               )).Handle) {
            HistoryTimelineReconcileResult.Reconciled reconciled = Assert.IsType<
                HistoryTimelineReconcileResult.Reconciled>(
                timeline.Coordinator.ReconcileSelectedPath(
                    fixture.TimelineHead,
                    fixture.Journal.ReadView
                )
            );
            commonHead = reconciled.Head;
            Assert.NotNull(commonHead.HeadRowId);
            Assert.NotEqual(oldSelection.SelectedRowId, commonHead.HeadRowId);
            Assert.Contains(
                fixture.Rows,
                row => row.Descriptor.RowId == commonHead.HeadRowId
            );
        }

        _ = await fixture.Journal.SendAsync(
            moved.NewHead,
            "sibling-observation"
        );
        (TimelineHeadRef siblingHead,
            IReadOnlyList<HistoryTimelineSelectedRow> committed) =
            CommitAllRows(fixture.Journal);
        Assert.NotEmpty(committed);
        HistoryTimelineSelectedRow sibling = committed[^1];
        Assert.Equal(
            commonHead.HeadRowId,
            committed[0].Descriptor.PreviousRowId
        );
        Assert.NotEqual(oldSelection.SelectedRowId, sibling.Descriptor.RowId);

        using (RecapGridStoreHandle store = Assert.IsType<
               RecapGridStoreOpenResult.Opened>(RecapGridStoreFactory.Open(
                   fixture.Path
               )).Handle) {
            RecapRowView previous = oldSelection.SelectedView;
            while (previous.HistoryRowId != commonHead.HeadRowId) {
                Assert.NotNull(previous.PreviousViewDigest);
                previous = Assert.IsType<
                    RecapGridStoreReadResult<RecapRowView>.Found>(
                    store.Reader.ReadView(
                        previous.PreviousViewDigest!.Value
                    )
                ).Value;
            }
            foreach ((HistoryTimelineSelectedRow row, int index) in committed
                         .Select((row, index) => (row, index))) {
                RecapCellArtifact previousCell = Assert.IsType<
                    RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                    store.Reader.ReadCell(
                        previous.OrderedCells[0].CellDigest
                    )
                ).Value;
                PriorInputReference prior =
                    new PriorInputReference.Projection(
                        PriorInputProjection.Create([
                            new PriorProjectedContent(
                                previousCell.LogicalColumnId,
                                previousCell.ContentDigest
                            )
                        ]).Digest
                    );
                EvaluationKey evaluation = EvaluationKey.Create(
                    row.Descriptor.DescriptorDigest,
                    fixture.Definition.Digest,
                    prior
                );
                RecapCellArtifact cell = RecapCellArtifact.Create(
                    fixture.Definition.LogicalColumnId,
                    fixture.Definition.Digest,
                    evaluation,
                    RecapCellOutcome.Updated,
                    $"sibling-recap-{index}",
                    fixture.Definition.MaxContentUtf8Bytes
                );
                Assert.IsType<RecapGridCellPutResult.Inserted>(
                    store.Writer.PutCell(cell)
                );
                RowBuildSpec spec = RowBuildSpec.CreateFull(
                    fixture.Recipe,
                    new RowViewCoordinate(
                        fixture.Journal.BranchRefId,
                        row.Descriptor.TimelineId,
                        row.Descriptor.RowId,
                        row.Descriptor.DescriptorDigest,
                        fixture.Recipe.Digest,
                        fixture.Recipe.Target.Digest,
                        row.Descriptor.PreviousRowId,
                        previous.Digest,
                        bootstrapCompleted: true
                    ),
                    prior,
                    [new RowBuildAssignment.Evaluate(
                        fixture.Definition.LogicalColumnId,
                        evaluation
                    )]
                );
                RecapRowView siblingView = RecapRowView.Create(spec, [cell]);
                Assert.IsType<RecapGridRowViewPutResult.Inserted>(
                    store.Writer.PutRowView(spec, siblingView)
                );
                previous = siblingView;
            }
            FulfilledViewKey fulfilled = FulfilledViewKey.Create(
                fixture.Journal.BranchRefId,
                siblingHead,
                sibling.Descriptor.DescriptorDigest,
                fixture.Recipe
            );
            Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
                store.Writer.PutFulfilled(fulfilled, previous.Digest)
            );
        }

        Assert.Equal(
            RecapGridContextComponent.Timeline,
            Assert.IsType<RecapGridContextMaterializeResult.Stale>(
                getter.Materialize(oldSelection)
            ).Component
        );
        EventAddress siblingRawHead = fixture.Journal.ReadCurrentHead()!.Value;
        RecapGridContextSelection siblingSelection = Select(
            getter,
            siblingRawHead
        );
        Assert.Equal(sibling.Descriptor.RowId, siblingSelection.SelectedRowId);
        Assert.Equal(
            $"sibling-recap-{committed.Count - 1}",
            Assert.Single(Assert.IsType<
                RecapGridContextMaterializeResult.Available>(
                getter.Materialize(siblingSelection)
            ).Candidate.Contributions).ExactText
        );
    }

    [Fact]
    public async Task ResolveAndMaterializeDrainBeforeConcurrentDispose() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        bool armed = false;
        RecapGridContextHandle getter = OpenGetterForTest(
            fixture.Journal,
            new GetterTestHooks(BeforeTerminalFence: _ => {
                if (!armed) {
                    return;
                }
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            })
        );
        RecapGridContextSelection selection = Select(getter, boundary);
        armed = true;

        Task<RecapGridContextMaterializeResult> materialize = Task.Run(
            () => getter.Materialize(selection)
        );
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Task dispose = Task.Run(getter.Dispose);
        await Task.Delay(100);
        Assert.False(dispose.IsCompleted);
        release.Set();
        Assert.IsType<RecapGridContextMaterializeResult.Available>(
            await materialize
        );
        await dispose;
        Assert.IsType<RecapGridContextResolveResult.Disposed>(
            getter.Resolve(boundary, 0)
        );

        using Fixture resolveFixture = await CreateBuiltFixture(turns: 1);
        EventAddress resolveBoundary = resolveFixture.Journal
            .ReadCurrentHead()!.Value;
        using var resolveEntered = new ManualResetEventSlim(false);
        using var resolveRelease = new ManualResetEventSlim(false);
        RecapGridContextHandle resolving = OpenGetterForTest(
            resolveFixture.Journal,
            new GetterTestHooks(BeforeTerminalFence: _ => {
                resolveEntered.Set();
                Assert.True(resolveRelease.Wait(TimeSpan.FromSeconds(10)));
            })
        );
        Task<RecapGridContextResolveResult> resolve = Task.Run(
            () => resolving.Resolve(resolveBoundary, 0)
        );
        Assert.True(resolveEntered.Wait(TimeSpan.FromSeconds(10)));
        Task resolveDispose = Task.Run(resolving.Dispose);
        await Task.Delay(100);
        Assert.False(resolveDispose.IsCompleted);
        resolveRelease.Set();
        Assert.IsType<RecapGridContextResolveResult.Selected>(await resolve);
        await resolveDispose;
        Assert.IsType<RecapGridContextResolveResult.Disposed>(
            resolving.Resolve(resolveBoundary, 0)
        );
    }

    private RecapGridContextHandle OpenGetterForTest(
        SessionJournalEngine journal,
        GetterTestHooks hooks
    ) => Assert.IsType<RecapGridContextOpenResult.Opened>(
        RecapGridContextFactory.OpenForTest(
            journal.ReadView,
            hooks,
            _estimator
        )
    ).Handle;

    private static void UpdateCadence(
        SessionJournalEngine journal,
        long minimumRecentHistoryLoad,
        long targetHistoryLoad = 1
    ) {
        using RecapGridCadenceHandle cadence = Assert.IsType<
            RecapGridCadenceOpenResult.Opened>(
            RecapGridCadenceFactory.OpenMutable(journal)
        ).Handle;
        RecapGridCadenceSnapshot current = Assert.IsType<
            RecapGridCadenceReadResult.Available>(
            cadence.Reader.ReadSnapshot()
        ).Snapshot;
        Assert.IsType<RecapGridCadenceCompareExchangeResult.Updated>(
            cadence.Coordinator.CompareExchangePolicy(
                current.Head,
                new RecapGridCadencePolicySpec(
                    minimumRecentHistoryLoad,
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    targetHistoryLoad,
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024
                )
            )
        );
    }

    private long FindCrossingRequirement(
        Fixture fixture,
        EventAddress boundary
    ) {
        using HistoryTimelineBuildReadSession session = Assert.IsType<
            HistoryTimelineBuildReadSessionOpenResult.Opened>(
            HistoryTimelineFactory.OpenBuildReadSession(
                fixture.Journal.ReadView,
                _estimator
            )
        ).Session;
        for (long required = 2; required <= 10_000; required++) {
            HistoryRecentReserveAnchorResult result =
                session.FindRecentReserveAnchor(
                    fixture.TimelineHead,
                    boundary,
                    new HistoryRecentReserveRequirement(
                        fixture.TimelineHead.ActivePartitionPolicyDigest,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        new HistoryLoadUnit(required)
                    )
                );
            if (result is HistoryRecentReserveAnchorResult.Eligible eligible
                && eligible.HeadThroughAnchor.Count == 2) {
                return required;
            }
            if (result is HistoryRecentReserveAnchorResult
                    .ReserveBootstrapRequired) {
                break;
            }
        }
        throw new Xunit.Sdk.XunitException(
            "The fixture did not expose a two-row recent-reserve anchor."
        );
    }
}
