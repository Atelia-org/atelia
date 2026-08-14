using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public void CreatePutActivateReopenUsesCanonicalWholeState() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);

        RecapGridControlCreateResult.Created created = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        ));
        Assert.Equal(0, created.Head.Generation);
        Assert.IsType<RecapGridControlCreateResult.AlreadyExists>(
            RecapGridControlFactory.Create(
                path,
                journal.BranchRefId,
                values.Admission
            )
        );

        ControlHeadRef activeHead;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   values.Admission
               )).Handle) {
            ControlHeadRef familyHead = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(handle.Coordinator.PutFamilyDefinition(
                created.Head,
                values.Family
            )).Head;
            ControlHeadRef definitionHead = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(handle.Coordinator.PutMaintainerDefinition(
                familyHead,
                values.Definition
            )).Head;
            ControlHeadRef recipeHead = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(handle.Coordinator.PutBuildRecipe(
                definitionHead,
                values.TimelineHead,
                values.Recipe,
                bootstrapWitness: null
            )).Head;
            activeHead = Assert.IsType<
                RecapGridControlActivateResult.Applied
            >(handle.Coordinator.CompareExchangeActiveRecipe(
                recipeHead,
                values.TimelineHead,
                values.Recipe.Digest,
                RecapGridControlActivationPurpose.Direct
            )).Head;

            RecapGridControlSnapshot snapshot = Assert.IsType<
                RecapGridControlSnapshotResult.Available
            >(handle.Reader.ReadSnapshot()).Snapshot;
            Assert.Equal(activeHead, snapshot.Head);
            Assert.Equal(values.Recipe.Digest, snapshot.ActiveRecipe!.Recipe.Digest);
            Assert.Single(snapshot.Families);
            Assert.Single(snapshot.Definitions);
            Assert.Single(snapshot.Recipes);
        }

        using RecapGridControlReaderHandle reopened = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened
        >(RecapGridControlFactory.OpenReader(
            path,
            journal.BranchRefId
        )).Handle;
        Assert.Equal(
            activeHead,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                reopened.Reader.ReadSnapshot()
            ).Snapshot.Head
        );
    }

    [Fact]
    public void IdempotentPutDoesNotAdvanceAndWholeHeadConflicts() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;

        ControlHeadRef stored = Assert.IsType<
            RecapGridControlPutResult.Stored
        >(handle.Coordinator.PutFamilyDefinition(
            initial,
            values.Family
        )).Head;
        Assert.Equal(
            stored,
            Assert.IsType<RecapGridControlPutResult.AlreadyPresent>(
                handle.Coordinator.PutFamilyDefinition(
                    stored,
                    values.Family
                )
            ).Head
        );
        Assert.IsType<RecapGridControlPutResult.StaleControlHead>(
            handle.Coordinator.PutMaintainerDefinition(
                initial,
                values.Definition
            )
        );
        Assert.Equal(
            stored,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()
            ).Snapshot.Head
        );
    }

    [Fact]
    public void DisposedHandleRejectsReaderAndWriter() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        handle.Dispose();

        Assert.IsType<RecapGridControlSnapshotResult.Disposed>(
            handle.Reader.ReadSnapshot()
        );
        Assert.IsType<RecapGridControlPutResult.Disposed>(
            handle.Coordinator.PutFamilyDefinition(
                initial,
                values.Family
            )
        );
    }

    [Fact]
    public void NonemptyRecipeRequiresExactOwnedBootstrapWitness() {
        string path = NewPath();
        using (var import = SessionJournalLegacyImportWriter.Create(
            path,
            new SessionCreateOptions("model", "system", "surface")
        )) {
            _ = import.AppendObservation("The room was locked.");
            _ = import.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text(
                        "The service passage is relevant."
                    )
                ]),
                new CompletionDescriptor("import", "v1", "model")
            );
        }
        using SessionJournalEngine journal = OpenWithTimeline(path);
        TimelineRow committed = CommitTimelineRow(journal);
        Values values = ValuesFor(path, journal);
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            committed.Head.TimelineId,
            committed.Row.Descriptor.RowId,
            BuildTarget.Create([
                new BuildTargetColumn(
                    values.Definition.LogicalColumnId,
                    values.Definition.Digest
                )
            ])
        );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [values.Family.Digest],
            [values.Definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 1,
            maximumProjectedCalls: 1
        );
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            admission
        )).Head;
        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            admission
        )).Handle;
        ControlHeadRef family = Assert.IsType<
            RecapGridControlPutResult.Stored
        >(handle.Coordinator.PutFamilyDefinition(
            initial,
            values.Family
        )).Head;
        ControlHeadRef definition = Assert.IsType<
            RecapGridControlPutResult.Stored
        >(handle.Coordinator.PutMaintainerDefinition(
            family,
            values.Definition
        )).Head;

        Assert.Equal(
            "BootstrapWitnessRequired",
            Assert.IsType<RecapGridControlPutResult.Invalid>(
                handle.Coordinator.PutBuildRecipe(
                    definition,
                    committed.Head,
                    recipe,
                    bootstrapWitness: null
                )
            ).Code
        );
        Assert.IsType<RecapGridControlPutResult.Stored>(
            handle.Coordinator.PutBuildRecipe(
                definition,
                committed.Head,
                recipe,
                committed.Row.Witness
            )
        );
    }

    [Fact]
    public void UnauthorizedCreateLeavesControlRootAbsent() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.RegisterFamily,
            [values.Family.Digest],
            [values.Definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );

        Assert.Equal(
            "Create",
            Assert.IsType<RecapGridControlCreateResult.Unauthorized>(
                RecapGridControlFactory.Create(
                    path,
                    journal.BranchRefId,
                    admission
                )
            ).Rule
        );
        Assert.False(Directory.Exists(Path.Combine(path, "control")));
    }

    [Fact]
    public void RecipeGraphErrorsAreTypedAndDoNotMutateHead() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        ControlHeadRef family = Assert.IsType<
            RecapGridControlPutResult.Stored
        >(handle.Coordinator.PutFamilyDefinition(
            initial,
            values.Family
        )).Head;
        ControlHeadRef definition = Assert.IsType<
            RecapGridControlPutResult.Stored
        >(handle.Coordinator.PutMaintainerDefinition(
            family,
            values.Definition
        )).Head;

        GridBuildRecipe wrongColumn = GridBuildRecipe.CreateFull(
            values.TimelineHead.TimelineId,
            null,
            BuildTarget.Create([
                new BuildTargetColumn(
                    new LogicalColumnId("case.other"),
                    values.Definition.Digest
                )
            ])
        );
        Assert.IsType<RecapGridControlPutResult.Unauthorized>(
            handle.Coordinator.PutBuildRecipe(
                definition,
                values.TimelineHead,
                wrongColumn,
                null
            )
        );
        GridBuildRecipe wrongTimeline = GridBuildRecipe.CreateFull(
            new TimelineId(new string('e', 32)),
            null,
            values.Recipe.Target
        );
        Assert.IsType<RecapGridControlPutResult.Unauthorized>(
            handle.Coordinator.PutBuildRecipe(
                definition,
                values.TimelineHead,
                wrongTimeline,
                null
            )
        );
        Assert.Equal(
            definition,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()
            ).Snapshot.Head
        );
    }

    [Fact]
    public void ActivationRechecksAdmissionAndSeparatesPromotionAuthority() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        ControlHeadRef registered;
        using (RecapGridControlHandle broad = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   values.Admission
               )).Handle) {
            ControlHeadRef family = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(broad.Coordinator.PutFamilyDefinition(
                initial,
                values.Family
            )).Head;
            ControlHeadRef definition = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(broad.Coordinator.PutMaintainerDefinition(
                family,
                values.Definition
            )).Head;
            registered = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(broad.Coordinator.PutBuildRecipe(
                definition,
                values.TimelineHead,
                values.Recipe,
                null
            )).Head;
        }

        var narrow = new RecapGridControlAdmission(
            RecapGridControlPermission.Activate,
            Array.Empty<FamilyDefinitionDigest>(),
            Array.Empty<string>(),
            Array.Empty<ContextHeaderCarrier>(),
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   narrow
               )).Handle) {
            Assert.IsType<RecapGridControlActivateResult.Unauthorized>(
                handle.Coordinator.CompareExchangeActiveRecipe(
                    registered,
                    values.TimelineHead,
                    values.Recipe.Digest,
                    RecapGridControlActivationPurpose.Direct
                )
            );
            Assert.Equal(
                registered,
                Assert.IsType<RecapGridControlSnapshotResult.Available>(
                    handle.Reader.ReadSnapshot()
                ).Snapshot.Head
            );
        }

        var promotion = new RecapGridControlAdmission(
            RecapGridControlPermission.Promote,
            [values.Family.Digest],
            [values.Definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        ControlHeadRef promoted;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   promotion
               )).Handle) {
            promoted = Assert.IsType<
                RecapGridControlActivateResult.Applied
            >(handle.Coordinator.CompareExchangeActiveRecipe(
                registered,
                values.TimelineHead,
                values.Recipe.Digest,
                RecapGridControlActivationPurpose.Promotion
            )).Head;
            Assert.IsType<RecapGridControlActivateResult.Unauthorized>(
                handle.Coordinator.CompareExchangeActiveRecipe(
                    promoted,
                    values.TimelineHead,
                    nextRecipeDigest: null,
                    purpose: RecapGridControlActivationPurpose.Direct
                )
            );
        }
        var deactivate = new RecapGridControlAdmission(
            RecapGridControlPermission.Activate,
            Array.Empty<FamilyDefinitionDigest>(),
            Array.Empty<string>(),
            Array.Empty<ContextHeaderCarrier>(),
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        using RecapGridControlHandle deactivator = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            deactivate
        )).Handle;
        Assert.Null(Assert.IsType<RecapGridControlActivateResult.Applied>(
            deactivator.Coordinator.CompareExchangeActiveRecipe(
                promoted,
                values.TimelineHead,
                nextRecipeDigest: null,
                purpose: RecapGridControlActivationPurpose.Direct
            )
        ).Head.ActiveRecipeDigest);
    }

    [Fact]
    public void PutRecipeRechecksEntireBaseClosureAdmissionWithoutMutation() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        FamilyDefinition secondFamily = FamilyDefinition.Create(
            "Maintain the secondary theory.",
            values.Family.OrderedTools,
            values.Family.OutputProtocol,
            values.Family.InputRenderingProtocol
        );
        MaintainerDefinitionRevision secondDefinition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.secondary"),
                secondFamily.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "secondary"
                ),
                values.Definition.Capability,
                new MaintainerDeclarativeSpec(
                    "Secondary theory",
                    "Maintain the secondary theory."
                ),
                values.Definition.MaxContentUtf8Bytes
            );
        var broadAdmission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [values.Family.Digest, secondFamily.Digest],
            [values.Definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        ControlHeadRef head = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            broadAdmission
        )).Head;
        using (RecapGridControlHandle broad = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   broadAdmission
               )).Handle) {
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                broad.Coordinator.PutFamilyDefinition(
                    head,
                    values.Family
                )
            ).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                broad.Coordinator.PutFamilyDefinition(
                    head,
                    secondFamily
                )
            ).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                broad.Coordinator.PutMaintainerDefinition(
                    head,
                    values.Definition
                )
            ).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                broad.Coordinator.PutMaintainerDefinition(
                    head,
                    secondDefinition
                )
            ).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                broad.Coordinator.PutBuildRecipe(
                    head,
                    values.TimelineHead,
                    values.Recipe,
                    null
                )
            ).Head;
        }
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            values.Recipe,
            null,
            BuildTarget.Create([
                new BuildTargetColumn(
                    secondDefinition.LogicalColumnId,
                    secondDefinition.Digest
                )
            ]),
            [secondDefinition.LogicalColumnId]
        );
        var narrowAdmission = new RecapGridControlAdmission(
            RecapGridControlPermission.RegisterRecipe,
            [secondFamily.Digest],
            [secondDefinition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case.secondary"],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        string statePath = ControlStatePath(
            path,
            journal.BranchRefId,
            head
        );
        byte[] before = File.ReadAllBytes(statePath);
        using RecapGridControlHandle narrow = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            narrowAdmission
        )).Handle;
        Assert.Equal(
            "RecipeClosureAdmission",
            Assert.IsType<RecapGridControlPutResult.Unauthorized>(
                narrow.Coordinator.PutBuildRecipe(
                    head,
                    values.TimelineHead,
                    values.Recipe,
                    null
                )
            ).Rule
        );
        Assert.Equal(
            "RecipeClosureAdmission",
            Assert.IsType<RecapGridControlPutResult.Unauthorized>(
                narrow.Coordinator.PutBuildRecipe(
                    head,
                    values.TimelineHead,
                    overlay,
                    null
                )
            ).Rule
        );
        Assert.Equal(before, File.ReadAllBytes(statePath));
        Assert.Equal(
            head,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                narrow.Reader.ReadSnapshot()
            ).Snapshot.Head
        );
    }

    [Fact]
    public void PutRecipeBudgetsProjectedCallsAcrossEntireBaseClosure() {
        string path = NewPath();
        using (var import = SessionJournalLegacyImportWriter.Create(
            path,
            new SessionCreateOptions("model", "system", "surface")
        )) {
            for (int index = 0; index < 2; index++) {
                _ = import.AppendObservation($"observation-{index}");
                _ = import.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"answer-{index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model")
                );
            }
        }
        using SessionJournalEngine journal = OpenWithTimeline(path);
        IReadOnlyList<TimelineRow> rows = CommitTimelineRows(journal, 2);
        Values values = ValuesFor(path, journal);
        HistoryTimelineSelectedRow witness;
        using (HistoryTimelineReaderHandle timeline = Assert.IsType<
               HistoryTimelineReaderOpenResult.Opened
               >(HistoryTimelineMaintenance.OpenReader(
                   path,
                   journal.BranchRefId
               )).Handle) {
            witness = Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
                timeline.Reader.ReadSelectedRow(
                    values.TimelineHead,
                    rows[1].Row.Descriptor.RowId
                )
            ).Row;
        }
        GridBuildRecipe baseRecipe = GridBuildRecipe.CreateFull(
            values.TimelineHead.TimelineId,
            witness.Descriptor.RowId,
            values.Recipe.Target
        );
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            baseRecipe,
            witness.Descriptor.RowId,
            values.Recipe.Target,
            [values.Definition.LogicalColumnId]
        );
        var broadAdmission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [values.Family.Digest],
            [values.Definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 2,
            maximumProjectedCalls: 4
        );
        ControlHeadRef head = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            broadAdmission
        )).Head;
        using (RecapGridControlHandle broad = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   broadAdmission
               )).Handle) {
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                broad.Coordinator.PutFamilyDefinition(head, values.Family)
            ).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                broad.Coordinator.PutMaintainerDefinition(
                    head,
                    values.Definition
                )
            ).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                broad.Coordinator.PutBuildRecipe(
                    head,
                    values.TimelineHead,
                    baseRecipe,
                    witness.Witness
                )
            ).Head;
        }
        var narrowAdmission = new RecapGridControlAdmission(
            RecapGridControlPermission.RegisterRecipe,
            [values.Family.Digest],
            [values.Definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 2,
            maximumProjectedCalls: 2
        );
        string statePath = ControlStatePath(
            path,
            journal.BranchRefId,
            head
        );
        byte[] before = File.ReadAllBytes(statePath);
        using RecapGridControlHandle narrow = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            narrowAdmission
        )).Handle;
        Assert.Equal(
            "MaximumProjectedCalls",
            Assert.IsType<RecapGridControlPutResult.Unauthorized>(
                narrow.Coordinator.PutBuildRecipe(
                    head,
                    values.TimelineHead,
                    overlay,
                    witness.Witness
                )
            ).Rule
        );
        Assert.Equal(before, File.ReadAllBytes(statePath));
    }

    [Fact]
    public void StateEntryCapsFailBeforeConstructingAnOversizedState() {
        Values values = ValuesForScopeOnly();
        RefId refId = new(1);
        TimelineId timelineId = values.Recipe.TimelineId;
        ControlState empty = ControlState.CreateEmpty(refId, timelineId);
        ControlState oneFamily = empty.WithFamilyForTest(
            values.Family,
            maximumCount: 1
        );
        FamilyDefinition otherFamily = FamilyDefinition.Create(
            "A distinct allowed family.",
            values.Family.OrderedTools,
            values.Family.OutputProtocol,
            values.Family.InputRenderingProtocol
        );
        Assert.Throws<ControlLimitException>(() =>
            oneFamily.WithFamilyForTest(otherFamily, maximumCount: 1));

        ControlState oneDefinition = oneFamily.WithDefinitionForTest(
            values.Definition,
            maximumCount: 1
        );
        MaintainerDefinitionRevision otherDefinition =
            MaintainerDefinitionRevision.Create(
                values.Definition.LogicalColumnId,
                values.Definition.FamilyDigest,
                values.Definition.Target,
                values.Definition.Capability,
                new MaintainerDeclarativeSpec(
                    "A revised topic",
                    "A revised prompt"
                ),
                values.Definition.MaxContentUtf8Bytes
            );
        Assert.Throws<ControlLimitException>(() =>
            oneDefinition.WithDefinitionForTest(
                otherDefinition,
                maximumCount: 1
            ));

        TimelineHeadRef timelineHead = new(
            timelineId,
            refId,
            null,
            new string('a', 64),
            null,
            0,
            HistoryTimelineSelectedPath.EmptyDigest,
            generation: 0
        );
        var first = new RegisteredGridRecipe(
            values.Recipe,
            new RegisteredRecipeBootstrap(
                timelineHead,
                null,
                null
            )
        );
        ControlState oneRecipe = oneDefinition.WithRecipeForTest(
            first,
            maximumCount: 1
        );
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            values.Recipe,
            null,
            values.Recipe.Target,
            [values.Definition.LogicalColumnId]
        );
        var second = new RegisteredGridRecipe(
            overlay,
            new RegisteredRecipeBootstrap(
                timelineHead,
                null,
                null
            )
        );
        Assert.Throws<ControlLimitException>(() =>
            oneRecipe.WithRecipeForTest(second, maximumCount: 1));
        Assert.Equal(0, empty.Head.Generation);
        Assert.Equal(1, oneFamily.Head.Generation);
        Assert.Equal(2, oneDefinition.Head.Generation);
        Assert.Equal(3, oneRecipe.Head.Generation);
    }

    [Fact]
    public async Task TwoHandlesWithSameExpectedHeadHaveOneWinner() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        FamilyDefinition other = FamilyDefinition.Create(
            "Maintain another line of inquiry.",
            values.Family.OrderedTools,
            values.Family.OutputProtocol,
            values.Family.InputRenderingProtocol
        );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.Create
                | RecapGridControlPermission.RegisterFamily,
            [values.Family.Digest, other.Digest],
            Array.Empty<string>(),
            Array.Empty<ContextHeaderCarrier>(),
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            admission
        )).Head;
        using RecapGridControlHandle first = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            admission
        )).Handle;
        using RecapGridControlHandle second = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            admission
        )).Handle;
        using var barrier = new Barrier(2);
        Task<RecapGridControlPutResult> firstTask = Task.Run(() => {
            barrier.SignalAndWait();
            return first.Coordinator.PutFamilyDefinition(
                initial,
                values.Family
            );
        });
        Task<RecapGridControlPutResult> secondTask = Task.Run(() => {
            barrier.SignalAndWait();
            return second.Coordinator.PutFamilyDefinition(initial, other);
        });
        RecapGridControlPutResult[] results = await Task.WhenAll(
            firstTask,
            secondTask
        );
        Assert.Single(results.OfType<RecapGridControlPutResult.Stored>());
        int busyIndex = Array.FindIndex(
            results,
            static result => result is RecapGridControlPutResult.Busy
        );
        if (busyIndex >= 0) {
            results[busyIndex] = busyIndex == 0
                ? first.Coordinator.PutFamilyDefinition(
                    initial,
                    values.Family
                )
                : second.Coordinator.PutFamilyDefinition(initial, other);
        }
        Assert.True(
            results.OfType<RecapGridControlPutResult.StaleControlHead>()
                .Count() == 1,
            string.Join(",", results.Select(static result => result switch {
                RecapGridControlPutResult.Invalid invalid
                    => $"Invalid({invalid.Code}:{invalid.Detail})",
                _ => result.GetType().Name
            }))
        );
        Assert.Single(Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(first.Reader.ReadSnapshot()).Snapshot.Families);
    }

    [Fact]
    public void SharedHandleBlocksExclusiveLifetimeUntilDisposed() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        _ = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(
                path,
                journal.BranchRefId,
                values.Admission
            )
        );
        RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        var paths = new ControlPaths(
            path,
            journal.BranchRefId,
            values.TimelineHead.TimelineId
        );
        Assert.Throws<ControlBusyException>(() =>
            ControlDurableFiles.AcquireExclusiveLifetime(
                paths,
                create: false
            ));
        handle.Dispose();
        using FileStream exclusive = ControlDurableFiles
            .AcquireExclusiveLifetime(paths, create: false);
    }

    [Fact]
    public void AncestorBudgetCountsRootThroughBootstrapNotNewerSuffix() {
        string path = NewPath();
        using (var import = SessionJournalLegacyImportWriter.Create(
            path,
            new SessionCreateOptions("model", "system", "surface")
        )) {
            for (int index = 0; index < 3; index++) {
                _ = import.AppendObservation($"observation-{index}");
                _ = import.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"answer-{index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model")
                );
            }
        }
        using SessionJournalEngine journal = OpenWithTimeline(path);
        IReadOnlyList<TimelineRow> rows = CommitTimelineRows(
            journal,
            count: 3
        );
        Values values = ValuesFor(path, journal);
        TimelineRow bootstrap = rows[1];
        HistoryTimelineSelectedRow currentWitness;
        using (HistoryTimelineReaderHandle timeline = Assert.IsType<
               HistoryTimelineReaderOpenResult.Opened
               >(HistoryTimelineMaintenance.OpenReader(
                   path,
                   journal.BranchRefId
               )).Handle) {
            currentWitness = Assert.IsType<
                HistoryTimelineReaderRowResult.Selected
            >(timeline.Reader.ReadSelectedRow(
                values.TimelineHead,
                bootstrap.Row.Descriptor.RowId
            )).Row;
        }
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            values.TimelineHead.TimelineId,
            bootstrap.Row.Descriptor.RowId,
            values.Recipe.Target
        );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [values.Family.Digest],
            [values.Definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 2,
            maximumProjectedCalls: 2
        );
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            admission
        )).Head;
        ControlHeadRef registered;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   admission
               )).Handle) {
            ControlHeadRef family = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(handle.Coordinator.PutFamilyDefinition(
                initial,
                values.Family
            )).Head;
            ControlHeadRef definition = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(handle.Coordinator.PutMaintainerDefinition(
                family,
                values.Definition
            )).Head;
            RecapGridControlPutResult putRecipe =
                handle.Coordinator.PutBuildRecipe(
                definition,
                values.TimelineHead,
                recipe,
                currentWitness.Witness
            );
            Assert.True(
                putRecipe is RecapGridControlPutResult.Stored,
                putRecipe is RecapGridControlPutResult.Invalid invalid
                    ? $"{invalid.Code}:{invalid.Detail}"
                    : putRecipe.GetType().Name
            );
            registered = ((RecapGridControlPutResult.Stored)putRecipe).Head;
        }
        var tooNarrow = new RecapGridControlAdmission(
            RecapGridControlPermission.Activate,
            [values.Family.Digest],
            [values.Definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 1,
            maximumProjectedCalls: 1
        );
        using RecapGridControlHandle narrow = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            tooNarrow
        )).Handle;
        Assert.Equal(
            "MaximumBootstrapRows",
            Assert.IsType<RecapGridControlActivateResult.Unauthorized>(
                narrow.Coordinator.CompareExchangeActiveRecipe(
                    registered,
                    values.TimelineHead,
                    recipe.Digest,
                    RecapGridControlActivationPurpose.Direct
                )
            ).Rule
        );
    }

    [Fact]
    public void BaseDepthAndDecodedGraphCorruptionFailClosed() {
        Values values = ValuesForScopeOnly();
        RefId refId = values.TimelineHead.RefId;
        TimelineHeadRef timelineHead = values.TimelineHead;
        ControlState state = ControlState.CreateEmpty(
            refId,
            timelineHead.TimelineId
        ).WithFamily(values.Family).WithDefinition(values.Definition);
        GridBuildRecipe current = values.Recipe;
        state = state.WithRecipe(new RegisteredGridRecipe(
            current,
            new RegisteredRecipeBootstrap(
                timelineHead,
                null,
                null
            )
        ));
        for (int depth = 1;
             depth <= ControlStorageLimits.MaximumRecipeBaseDepth;
             depth++) {
            current = GridBuildRecipe.CreateOverlay(
                current,
                null,
                values.Recipe.Target,
                [values.Definition.LogicalColumnId]
            );
            state = state.WithRecipe(new RegisteredGridRecipe(
                current,
                new RegisteredRecipeBootstrap(
                    timelineHead,
                    null,
                    null
                )
            ));
        }
        GridBuildRecipe overDepth = GridBuildRecipe.CreateOverlay(
            current,
            null,
            values.Recipe.Target,
            [values.Definition.LogicalColumnId]
        );
        Assert.Equal(
            "RecipeBaseDepthInvalid",
            Assert.Throws<ControlStoreException>(() =>
                state.WithRecipe(new RegisteredGridRecipe(
                    overDepth,
                    new RegisteredRecipeBootstrap(
                        timelineHead,
                        null,
                        null
                    )
                ))).Code
        );

        ControlFileDto canonical = JsonSerializer.Deserialize<
            ControlFileDto
        >(state.CanonicalBytes, ControlJson.Options)!;
        ControlFileDto wrongScope = Recode(canonical with {
            Head = canonical.Head with { RefId = 2 }
        });
        Assert.Equal(
            "RecipeScopeInvalid",
            Assert.Throws<ControlStoreException>(() => ControlState.Decode(
                JsonSerializer.SerializeToUtf8Bytes(
                    wrongScope,
                    ControlJson.Options
                )
            )).Code
        );

        GridBuildRecipe wrongColumn = GridBuildRecipe.CreateFull(
            timelineHead.TimelineId,
            null,
            BuildTarget.Create([
                new BuildTargetColumn(
                    new LogicalColumnId("case.other"),
                    values.Definition.Digest
                )
            ])
        );
        RecipeEntryDto wrongEntry = new(
            wrongColumn.Digest.Value,
            wrongColumn.ToCanonicalBytes(),
            canonical.Recipes[0].Bootstrap
        );
        ControlFileDto wrongColumnFile = Recode(canonical with {
            Recipes = [wrongEntry]
        });
        Assert.Equal(
            "RecipeColumnDefinitionMismatch",
            Assert.Throws<ControlStoreException>(() => ControlState.Decode(
                JsonSerializer.SerializeToUtf8Bytes(
                    wrongColumnFile,
                    ControlJson.Options
                )
            )).Code
        );

        RecipeEntryDto overEntry = new(
            overDepth.Digest.Value,
            overDepth.ToCanonicalBytes(),
            canonical.Recipes[0].Bootstrap
        );
        RecipeEntryDto[] tooDeep = [
            .. canonical.Recipes,
            overEntry
        ];
        Array.Sort(
            tooDeep,
            static (left, right) => string.CompareOrdinal(
                left.Digest,
                right.Digest
            )
        );
        ControlFileDto tooDeepFile = Recode(canonical with {
            Recipes = tooDeep
        });
        Assert.Equal(
            "RecipeBaseDepthInvalid",
            Assert.Throws<ControlStoreException>(() => ControlState.Decode(
                JsonSerializer.SerializeToUtf8Bytes(
                    tooDeepFile,
                    ControlJson.Options
                )
            )).Code
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private SessionJournalEngine CreateTimeline(string path) {
        SessionJournalEngine journal = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "surface")
        );
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 8,
                    maxRenderedBytes: 1024 * 1024
                ),
                _estimator
            )
        );
        return journal;
    }

    private SessionJournalEngine OpenWithTimeline(string path) {
        SessionJournalEngine journal = SessionJournalEngine.Open(path);
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 8,
                    maxRenderedBytes: 1024 * 1024
                ),
                _estimator
            )
        );
        return journal;
    }

    private TimelineRow CommitTimelineRow(SessionJournalEngine journal) {
        return Assert.Single(CommitTimelineRows(journal, count: 1));
    }

    private IReadOnlyList<TimelineRow> CommitTimelineRows(
        SessionJournalEngine journal,
        int count
    ) {
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
        var rows = new List<TimelineRow>(count);
        for (int index = 0; index < count; index++) {
            TimelineHeadRef before = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(timeline.Reader.ReadSnapshot()).Head;
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured
            >(timeline.Coordinator.CaptureOnline(
                before,
                journal.ReadView
            )).Capture;
            HistoryTimelinePlanResult.Selected selected = Assert.IsType<
                HistoryTimelinePlanResult.Selected
            >(seal.PlanNextRow(before, capture));
            TimelineHeadRef committed = Assert.IsType<
                HistoryTimelineCommitResult.Committed
            >(seal.CommitRow(selected.Candidate)).Head;
            HistoryTimelineSelectedRow row = Assert.IsType<
                HistoryTimelineReaderRowResult.Selected
            >(timeline.Reader.ReadSelectedRow(
                committed,
                committed.HeadRowId!.Value
            )).Row;
            rows.Add(new TimelineRow(committed, row));
        }
        return rows;
    }

    private static void EnsureCadence(SessionJournalEngine journal) {
        RecapGridCadenceCreateResult result = RecapGridCadenceFactory.Create(
            journal,
            new RecapGridCadencePolicySpec(
                minimumRecentHistoryLoad: 1,
                HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                targetHistoryLoad: 1,
                maxRawEvents: 8,
                maxRenderedBytes: 1024 * 1024));
        Assert.True(result is RecapGridCadenceCreateResult.Created
            or RecapGridCadenceCreateResult.AlreadyExists,
            $"Cadence create failed: {result.GetType().Name}");
    }

    private static Values ValuesFor(
        string repositoryPath,
        SessionJournalEngine journal
    ) {
        TimelineHeadRef timelineHead;
        using (HistoryTimelineReaderHandle timeline = Assert.IsType<
               HistoryTimelineReaderOpenResult.Opened
               >(HistoryTimelineMaintenance.OpenReader(
                   repositoryPath,
                   journal.BranchRefId
               )).Handle) {
            timelineHead = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(timeline.Reader.ReadSnapshot()).Head;
        }
        return ValuesFor(timelineHead);
    }

    private static Values ValuesForScopeOnly() => ValuesFor(
        new TimelineHeadRef(
            new TimelineId("00112233445566778899aabbccddeeff"),
            new RefId(1),
            null,
            new string('a', 64),
            null,
            0,
            HistoryTimelineSelectedPath.EmptyDigest,
            generation: 0
        )
    );

    private static Values ValuesFor(TimelineHeadRef timelineHead) {
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain one line of inquiry.",
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
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "culprit"
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    "Who is the culprit?",
                    "Maintain the culprit hypothesis."
                ),
                maxContentUtf8Bytes: 16 * 1024
            );
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineHead.TimelineId,
            bootstrapThroughRowId: null,
            BuildTarget.Create([
                new BuildTargetColumn(
                    definition.LogicalColumnId,
                    definition.Digest
                )
            ])
        );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        return new Values(
            family,
            definition,
            recipe,
            timelineHead,
            admission
        );
    }

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-recap-grid-control-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static ControlFileDto Recode(ControlFileDto source) {
        var body = new ControlBodyDto(
            ControlState.SchemaVersion,
            source.Head.InstanceId,
            source.Head.RefId,
            source.Head.TimelineId,
            source.Head.Generation,
            source.Head.ActiveRecipeDigest,
            source.Families,
            source.Definitions,
            source.Recipes,
            source.OperationReceipts
        );
        string digest = Hash(
            "atelia.recap-grid.control-state.v2",
            JsonSerializer.SerializeToUtf8Bytes(body, ControlJson.Options)
        );
        return source with {
            Head = source.Head with { StateDigest = digest }
        };
    }

    private static string Hash(
        string domain,
        ReadOnlySpan<byte> value
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        Append(Encoding.UTF8.GetBytes(domain));
        Append(value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(ReadOnlySpan<byte> bytes) {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    private sealed record Values(
        FamilyDefinition Family,
        MaintainerDefinitionRevision Definition,
        GridBuildRecipe Recipe,
        TimelineHeadRef TimelineHead,
        RecapGridControlAdmission Admission
    );

    private sealed record TimelineRow(
        TimelineHeadRef Head,
        HistoryTimelineSelectedRow Row
    );
}
