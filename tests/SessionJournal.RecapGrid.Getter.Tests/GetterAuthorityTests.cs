using Atelia.EventJournal;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Getter.Tests;

public sealed partial class GetterVerticalTests {
    [Fact]
    public async Task NeutralEngineFoldsRawTailExactlyAfterSelectedAnchor() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        var completion = new RecordingCompletionClient();
        fixture.Journal.UseRuntime(new SessionRuntime(
            completion,
            CompletionTarget: new SessionCompletionTargetIdentity(
                "getter-e2e",
                "test",
                "getter-e2e-v1",
                "getter-e2e-adapter-v1"
            ),
            ContextCandidateSource: getter,
            ContextLifecycle: getter
        ));

        EventAddress before = fixture.Journal.ReadCurrentHead()!.Value;
        _ = await fixture.Journal.SendAsync(before, "tail-observation");

        CompletionRequest request = Assert.IsType<CompletionRequest>(
            completion.LastRequest
        );
        string recap = $"recap-{fixture.Rows.Count - 1}";
        Assert.Equal(1, Count(request.PromptPrefix.SystemPrompt, recap));
        Assert.Equal(2, request.PromptPrefix.SharedContextMessages.Count());
        Assert.IsType<ActionMessage>(
            request.PromptPrefix.SharedContextMessages[0]);
        Assert.Equal(
            "tail-observation",
            Assert.IsType<ObservationMessage>(
                request.PromptPrefix.SharedContextMessages[1]
            ).Content
        );
        Assert.DoesNotContain(
            "observation-0",
            request.PromptPrefix.SharedContextMessages
                .Select(MessageText)
        );
        Assert.Contains(
            "answer-1",
            request.PromptPrefix.SharedContextMessages
                .Select(MessageText)
        );
    }

    [Fact]
    public async Task NeutralHandleRequiresExactCompactCanonicalEncoding() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        SessionContextCandidateDescriptor descriptor = Assert.IsType<
            SessionContextCandidateSelection>(await getter.SelectAsync(
            new SessionContextSelectionRequest(
                fixture.Journal.ReadCurrentHead()!.Value,
                0
            ),
            CancellationToken.None
        )).Candidate!;
        string leadingZero = descriptor.Handle[..(
            descriptor.Handle.LastIndexOf('|') + 1)] + "00";

        Assert.IsType<SessionContextCandidateMaterializationResult.Invalid>(
            await getter.MaterializeAsync(
                descriptor with { Handle = leadingZero },
                CancellationToken.None
            )
        );
        Assert.IsType<SessionContextCandidateMaterializationResult.Invalid>(
            await getter.MaterializeAsync(
                descriptor with { Handle = new string('x', 513) },
                CancellationToken.None
            )
        );
        Assert.IsType<SessionContextCandidateMaterializationResult.Invalid>(
            await getter.MaterializeAsync(
                descriptor with { Handle = "wrong" + descriptor.Handle },
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task SelectThenMaterializeRejectsRawTimelineAndControlDrift() {
        using Fixture rawFixture = await CreateBuiltFixture(turns: 1);
        using (RecapGridContextHandle getter = OpenGetter(rawFixture.Journal)) {
            EventAddress boundary = rawFixture.Journal.ReadCurrentHead()!.Value;
            RecapGridContextSelection selected = Select(getter, boundary);
            _ = await rawFixture.Journal.SendAsync(boundary, "raw-drift");
            RecapGridContextMaterializeResult.Stale stale = Assert.IsType<
                RecapGridContextMaterializeResult.Stale>(
                getter.Materialize(selected)
            );
            Assert.Equal(RecapGridContextComponent.RawAuthority,
                stale.Component);
        }

        using Fixture timelineFixture = await CreateBuiltFixture(turns: 1);
        using (RecapGridContextHandle getter = OpenGetter(
                   timelineFixture.Journal)) {
            EventAddress boundary = timelineFixture.Journal
                .ReadCurrentHead()!.Value;
            RecapGridContextSelection selected = Select(getter, boundary);
            using HistoryTimelineHandle timeline = Assert.IsType<
                HistoryTimelineOpenResult.Opened>(
                HistoryTimelineFactory.Open(
                    timelineFixture.Journal.ReadView,
                    _estimator
                )
            ).Handle;
            PartitionPolicyRevision next = PartitionPolicyRevision.Create(
                timelineFixture.TimelineHead.TimelineId,
                HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                new HistoryLoadUnit(2),
                maxRawEvents: 64,
                maxRenderedBytes: 1024 * 1024
            );
            Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
                timeline.Coordinator.PutPolicy(next)
            );
            Assert.IsType<HistoryTimelinePolicyCasResult.Applied>(
                timeline.Coordinator.CompareExchangePolicy(
                    timelineFixture.TimelineHead,
                    next.PolicyDigest
                )
            );
            RecapGridContextMaterializeResult.Stale stale = Assert.IsType<
                RecapGridContextMaterializeResult.Stale>(
                getter.Materialize(selected)
            );
            Assert.Equal(RecapGridContextComponent.Timeline, stale.Component);
        }

        using Fixture controlFixture = await CreateBuiltFixture(turns: 1);
        using (RecapGridContextHandle getter = OpenGetter(
                   controlFixture.Journal)) {
            EventAddress boundary = controlFixture.Journal
                .ReadCurrentHead()!.Value;
            RecapGridContextSelection selected = Select(getter, boundary);
            using RecapGridControlHandle control = Assert.IsType<
                RecapGridControlOpenResult.Opened>(
                RecapGridControlFactory.Open(
                    controlFixture.Path,
                    controlFixture.Journal.BranchRefId,
                    controlFixture.Admission
                )
            ).Handle;
            Assert.IsType<RecapGridControlActivateResult.Applied>(
                control.Coordinator.CompareExchangeActiveRecipe(
                    controlFixture.ControlHead,
                    controlFixture.TimelineHead,
                    nextRecipeDigest: null,
                    purpose: RecapGridControlActivationPurpose.Direct
                )
            );
            RecapGridContextMaterializeResult.Stale stale = Assert.IsType<
                RecapGridContextMaterializeResult.Stale>(
                getter.Materialize(selected)
            );
            Assert.Equal(RecapGridContextComponent.Control, stale.Component);
        }
    }

    [Fact]
    public async Task LazyStoreLeaseBlocksResetAndNewIdentityHasNoFallback() {
        using Fixture fixture = await CreateBuiltFixture(turns: 1);
        RecapGridStorePhysicalWitness witness = Assert.IsType<
            RecapGridStorePrepareResetResult.Prepared>(
            RecapGridStoreMaintenance.PrepareReset(fixture.Path)
        ).Witness;
        RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        EventAddress boundary = fixture.Journal.ReadCurrentHead()!.Value;
        RecapGridContextSelection selected = Select(getter, boundary);
        RecapGridStoreIdentity oldIdentity = selected.StoreIdentity;

        Assert.IsType<RecapGridStoreResetResult.Busy>(
            RecapGridStoreMaintenance.Reset(fixture.Path, witness)
        );
        getter.Dispose();
        RecapGridStoreResetResult.Reset reset = Assert.IsType<
            RecapGridStoreResetResult.Reset>(
            RecapGridStoreMaintenance.Reset(fixture.Path, witness)
        );
        Assert.NotEqual(oldIdentity, reset.Identity);

        using RecapGridContextHandle reopened = OpenGetter(fixture.Journal);
        Assert.IsType<RecapGridContextResolveResult.Unfulfilled>(
            reopened.Resolve(boundary, 0)
        );
    }

    [Fact]
    public async Task MissingOrCorruptViewCellAndPreviousLinkFailClosed() {
        using Fixture missingCell = await CreateBuiltFixture(turns: 1);
        using (RecapGridContextHandle getter = OpenGetter(
                   missingCell.Journal)) {
            RecapGridContextSelection selected = Select(
                getter,
                missingCell.Journal.ReadCurrentHead()!.Value
            );
            CellDigest missingDigest = selected.SelectedView
                .OrderedCells[0].CellDigest;
            ExecuteStoreSql(
                missingCell.Path,
                "PRAGMA foreign_keys=OFF; DELETE FROM cell_artifact WHERE cell_digest=$digest;",
                ("$digest", missingDigest.Value)
            );
            RecapGridContextMaterializeResult.Invalid invalid = Assert.IsType<
                RecapGridContextMaterializeResult.Invalid>(
                getter.Materialize(selected)
            );
            Assert.Equal(RecapGridContextComponent.Store, invalid.Component);
        }

        using Fixture corruptView = await CreateBuiltFixture(turns: 1);
        RowViewDigest viewDigest;
        using (RecapGridContextHandle getter = OpenGetter(
                   corruptView.Journal)) {
            viewDigest = Select(
                getter,
                corruptView.Journal.ReadCurrentHead()!.Value
            ).SelectedViewDigest;
        }
        ExecuteStoreSql(
            corruptView.Path,
            "UPDATE row_view SET canonical=X'00' WHERE view_digest=$digest;",
            ("$digest", viewDigest.Value)
        );
        using (RecapGridContextHandle getter = OpenGetter(
                   corruptView.Journal)) {
            Assert.IsType<RecapGridContextResolveResult.Invalid>(
                getter.Resolve(
                    corruptView.Journal.ReadCurrentHead()!.Value,
                    0
                )
            );
        }

        using Fixture brokenPrevious = await CreateBuiltFixture(turns: 2);
        RowViewDigest previousDigest;
        using (RecapGridContextHandle getter = OpenGetter(
                   brokenPrevious.Journal)) {
            previousDigest = Select(
                getter,
                brokenPrevious.Journal.ReadCurrentHead()!.Value
            ).SelectedView.PreviousViewDigest!.Value;
        }
        ExecuteStoreSql(
            brokenPrevious.Path,
            "PRAGMA foreign_keys=OFF; DELETE FROM row_view_member WHERE view_digest=$digest; DELETE FROM row_view WHERE view_digest=$digest;",
            ("$digest", previousDigest.Value)
        );
        using (RecapGridContextHandle getter = OpenGetter(
                   brokenPrevious.Journal)) {
            RecapGridContextResolveResult.Invalid invalid = Assert.IsType<
                RecapGridContextResolveResult.Invalid>(
                getter.Resolve(
                    brokenPrevious.Journal.ReadCurrentHead()!.Value,
                    1
                )
            );
            Assert.Equal("PreviousViewMissing", invalid.Code);
        }
    }

    [Fact]
    public async Task EmptyContentFailsAndNeutralCapExactMaterializes() {
        using Fixture empty = await CreateBuiltFixture(
            turns: 1,
            contentFactory: static _ => string.Empty
        );
        using (RecapGridContextHandle getter = OpenGetter(empty.Journal)) {
            RecapGridContextSelection selected = Select(
                getter,
                empty.Journal.ReadCurrentHead()!.Value
            );
            RecapGridContextMaterializeResult.Invalid invalid = Assert.IsType<
                RecapGridContextMaterializeResult.Invalid>(
                getter.Materialize(selected)
            );
            Assert.Equal("SelectedCellContentLimit", invalid.Code);
        }

        string exact = new('x',
            SessionContextContributionContract.MaxContributionUtf8Bytes);
        using Fixture capped = await CreateBuiltFixture(
            turns: 1,
            contentFactory: _ => exact,
            maximumContentUtf8Bytes:
                SessionContextContributionContract.MaxContributionUtf8Bytes
        );
        using RecapGridContextHandle cappedGetter = OpenGetter(capped.Journal);
        RecapGridContextSelection cappedSelection = Select(
            cappedGetter,
            capped.Journal.ReadCurrentHead()!.Value
        );
        Assert.Equal(exact, Assert.Single(Assert.IsType<
            RecapGridContextMaterializeResult.Available>(
            cappedGetter.Materialize(cappedSelection)
        ).Candidate.Contributions).ExactText);
    }

    [Fact]
    public async Task GetterRejectsForgedActiveDuplicateAndOversizeDefinitions() {
        using Fixture duplicate = await CreateControlFixture(
            turns: 1,
            activate: false,
            createStore: false
        );
        MaintainerDefinitionRevision second =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.second"),
                duplicate.Family.Digest,
                duplicate.Definition.Target,
                duplicate.Definition.Capability,
                duplicate.Definition.DeclarativeSpec,
                1024
            );
        GridBuildRecipe duplicateRecipe = GridBuildRecipe.CreateFull(
            duplicate.TimelineHead.TimelineId,
            duplicate.Rows[^1].Descriptor.RowId,
            BuildTarget.Create([
                new BuildTargetColumn(
                    duplicate.Definition.LogicalColumnId,
                    duplicate.Definition.Digest
                ),
                new BuildTargetColumn(second.LogicalColumnId, second.Digest)
            ])
        );
        ForgeActiveRecipe(duplicate, second, duplicateRecipe);
        using (RecapGridContextHandle getter = OpenGetter(
                   duplicate.Journal)) {
            RecapGridContextResolveResult.Invalid invalid = Assert.IsType<
                RecapGridContextResolveResult.Invalid>(
                getter.Resolve(
                    duplicate.Journal.ReadCurrentHead()!.Value,
                    0
                )
            );
            Assert.Equal("ActiveRecipeContextShapeInvalid", invalid.Code);
        }

        using Fixture oversize = await CreateControlFixture(
            turns: 1,
            activate: false,
            createStore: false
        );
        MaintainerDefinitionRevision large =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.large"),
                oversize.Family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "large"
                ),
                oversize.Definition.Capability,
                oversize.Definition.DeclarativeSpec,
                SessionContextContributionContract.MaxContributionUtf8Bytes
                    + 1
            );
        GridBuildRecipe largeRecipe = GridBuildRecipe.CreateFull(
            oversize.TimelineHead.TimelineId,
            oversize.Rows[^1].Descriptor.RowId,
            BuildTarget.Create([
                new BuildTargetColumn(large.LogicalColumnId, large.Digest)
            ])
        );
        ForgeActiveRecipe(oversize, large, largeRecipe);
        using (RecapGridContextHandle getter = OpenGetter(
                   oversize.Journal)) {
            RecapGridContextResolveResult.Invalid invalid = Assert.IsType<
                RecapGridContextResolveResult.Invalid>(
                getter.Resolve(
                    oversize.Journal.ReadCurrentHead()!.Value,
                    0
                )
            );
            Assert.Equal("ActiveRecipeContextShapeInvalid", invalid.Code);
        }
    }

    [Fact]
    public async Task OffScopePreviousViewMakesProvenanceIncompleteNotVerified() {
        using Fixture fixture = await CreateBuiltFixture(turns: 2);
        RecapGridContextSelection original;
        using (RecapGridContextHandle getter = OpenGetter(fixture.Journal)) {
            original = Select(
                getter,
                fixture.Journal.ReadCurrentHead()!.Value
            );
        }
        using RecapGridStoreHandle store = Assert.IsType<
            RecapGridStoreOpenResult.Opened>(
            RecapGridStoreFactory.Open(fixture.Path)
        ).Handle;
        RecapCellArtifact cell = Assert.IsType<
            RecapGridStoreReadResult<RecapCellArtifact>.Found>(
            store.Reader.ReadCell(
                original.SelectedView.OrderedCells[0].CellDigest
            )
        ).Value;
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            fixture.Recipe,
            original.SelectedRow.Descriptor.RowId,
            original.SelectedDescriptorDigest,
            // This canonical view is self-row scoped rather than predecessor
            // scoped. Store accepts it as content; Getter provenance must not.
            original.SelectedView.Digest,
            cell.EvaluationKey.PriorInput,
            [new RowBuildAssignment.Evaluate(
                cell.LogicalColumnId,
                cell.EvaluationKey
            )]
        );
        RecapRowView wrong = RecapRowView.Create(spec, [cell]);
        Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            store.Writer.PutRowView(spec, wrong)
        );
        store.Dispose();
        ExecuteStoreSql(
            fixture.Path,
            "UPDATE fulfilled_view_ref SET view_digest=$view;",
            ("$view", wrong.Digest.Value)
        );

        using RecapGridContextHandle reopened = OpenGetter(fixture.Journal);
        RecapGridContextMaterializeResult.Available available = Assert.IsType<
            RecapGridContextMaterializeResult.Available>(
            reopened.Materialize(Select(
                reopened,
                fixture.Journal.ReadCurrentHead()!.Value
            ))
        );
        Assert.Equal(
            RecapGridProvenanceStatus.Incomplete,
            available.Provenance.PriorInputAligned
        );
        Assert.Equal(
            RecapGridProvenanceStatus.Incomplete,
            available.Provenance.FullRebuildChain
        );
    }

    private static void ForgeActiveRecipe(
        Fixture fixture,
        MaintainerDefinitionRevision additionalDefinition,
        GridBuildRecipe recipe
    ) {
        var paths = new ControlPaths(
            fixture.Path,
            fixture.Journal.BranchRefId,
            fixture.TimelineHead.TimelineId
        );
        ControlState state = ControlState.Decode(File.ReadAllBytes(
            paths.StatePath
        ));
        HistorySegmentDescriptor bootstrap = fixture.Rows[^1].Descriptor;
        state = state.WithDefinition(additionalDefinition)
            .WithRecipe(new RegisteredGridRecipe(
                recipe,
                new RegisteredRecipeBootstrap(
                    fixture.TimelineHead,
                    bootstrap.RowId,
                    bootstrap.DescriptorDigest
                )
            ))
            .WithActive(recipe.Digest);
        File.WriteAllBytes(paths.StatePath, state.CanonicalBytes);
    }

    private static RecapGridContextSelection Select(
        RecapGridContextHandle getter,
        EventAddress boundary,
        int nth = 0
    ) => Assert.IsType<RecapGridContextResolveResult.Selected>(
        getter.Resolve(boundary, nth)
    ).Selection;

    private static void ExecuteStoreSql(
        string repositoryPath,
        string sql,
        params (string Name, object Value)[] parameters
    ) {
        string database = Path.Combine(
            repositoryPath,
            "derived",
            "recap-grid",
            "v1",
            "grid.sqlite"
        );
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString()
        );
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }

    private static string MessageText(IHistoryMessage message) => message switch {
        ObservationMessage observation => observation.Content ?? string.Empty,
        ActionMessage action => action.GetFlattenedText(),
        _ => string.Empty
    };

    private static int Count(string source, string value) {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(
                   value,
                   start,
                   StringComparison.Ordinal
               )) >= 0) {
            count++;
            start += value.Length;
        }
        return count;
    }

    private sealed class RecordingCompletionClient : ICompletionClient {
        internal CompletionRequest? LastRequest { get; private set; }
        public string Name => "getter-e2e";
        public string ApiSpecId => "getter-e2e-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text("e2e-answer")]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }
}
