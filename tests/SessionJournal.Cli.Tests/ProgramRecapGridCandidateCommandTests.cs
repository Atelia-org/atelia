using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramRecapGridCandidateCommandTests : IDisposable {
    private readonly List<string> _externalPaths = [];
    private readonly string _root = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-recap-grid-candidate-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void InitSyncDiagnosticsProgressAndMaterializeNeverConstructProvider() {
        CreateJournal();
        string admission = WriteAdmission(["create"]);
        string refId;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId.ToHexString();
        }

        (int initCode, JsonElement init) = RunCaptured(
            "init",
            "--input", _root,
            "--confirm-ref", refId,
            "--admission", admission,
            "--partition-algorithm",
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--target-history-load", "1",
            "--max-raw-events", "64",
            "--max-rendered-bytes", "1048576"
        );
        Assert.Equal(0, initCode);
        Assert.Equal("ready", init.GetProperty("status").GetString());

        Assert.Equal(0, Run("timeline", "inspect", "--input", _root));
        Assert.Equal(0, Run("timeline", "verify", "--input", _root));
        Assert.Equal(0, Run("control", "inspect", "--input", _root));
        Assert.Equal(0, Run("control", "verify", "--input", _root));

        (int syncCode, JsonElement sync) = RunCaptured(
            "timeline", "sync",
            "--input", _root,
            "--confirm-ref", refId,
            "--max-rows", "16"
        );
        Assert.Equal(0, syncCode);
        Assert.Equal(
            "synchronized",
            sync.GetProperty("status").GetString()
        );

        Assert.Equal(2, Run(
            "progress",
            "--input", _root,
            "--live",
            "--max-selected-rows", "64",
            "--max-recipe-row-steps", "64",
            "--max-new-calls", "0",
            "--max-elapsed-ms", "10000"
        ));
        EventAddress head;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            head = journal.ReadView.ReadCurrentHead()!.Value;
        }
        Assert.Equal(0, Run(
            "materialize",
            "--input", _root,
            "--boundary", EventAddressTextCodec.Format(head),
            "--nth-previous", "0"
        ));
    }

    [Fact]
    public void WrongRefAndUnknownOptionAreSyntaxFailuresWithNoDerivedMutation() {
        CreateJournal();
        string admission = WriteAdmission(["create"]);
        var provider = new DeterministicCompletionClientFactory();
        Assert.Equal(1, Run(
            "init",
            "--input", _root,
            "--confirm-ref", new string('0', 16),
            "--admission", admission,
            "--partition-algorithm",
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--target-history-load", "1",
            "--max-raw-events", "64",
            "--max-rendered-bytes", "1048576"
        ));
        Assert.False(Directory.Exists(Path.Combine(_root, "derived")));
        Assert.Equal(1, RunWithFactory(
            provider,
            "build", "--input", _root,
            "--confirm-ref", new string('0', 16),
            "--live",
            "--max-selected-rows", "1",
            "--max-recipe-row-steps", "1",
            "--max-new-calls", "0",
            "--max-elapsed-ms", "1",
            "--routes", Path.Combine(_root, "missing-routes.json"),
            "--connections", Path.Combine(_root, "missing-connections.json")
        ));
        Assert.Equal(0, provider.CallCount);
        Assert.False(Directory.Exists(Path.Combine(_root, "derived")));
        Assert.Equal(1, Run(
            "timeline", "inspect", "--input", _root, "--unknown", "x"
        ));
        string refId;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId.ToHexString();
        }
        Assert.Equal(1, Run(
            "build", "--input", _root, "--confirm-ref", refId,
            "--live", "--live",
            "--max-selected-rows", "1",
            "--max-recipe-row-steps", "1",
            "--max-new-calls", "0",
            "--max-elapsed-ms", "1",
            "--routes", Path.Combine(_root, "missing-routes.json"),
            "--connections", Path.Combine(_root, "missing-connections.json")
        ));
        Assert.False(Directory.Exists(Path.Combine(_root, "derived")));
        Assert.Equal(1, Run(
            "materialize", "--input", _root,
            "--boundary", "unused",
            "--include-content", "--include-content"
        ));
        Assert.Equal(1, Run(
            "control", "activate", "--input", _root,
            "--deactivate", "--deactivate"
        ));
        Assert.False(Directory.Exists(Path.Combine(_root, "derived")));
    }

    [Fact]
    public void SyncUsesBoundedOfflineAuditWhenOnlinePrefixCannotReachBootstrap() {
        CreateJournal(turns: 12);
        string admission = WriteAdmission(["create"]);
        string refId;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId.ToHexString();
        }
        Assert.Equal(0, Run(
            "init", "--input", _root, "--confirm-ref", refId,
            "--admission", admission,
            "--partition-algorithm",
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--target-history-load", "1", "--max-raw-events", "3",
            "--max-rendered-bytes", "1048576"
        ));

        int auditEvents = CountSelectedAuditEvents();
        DomainSnapshot beforeLimit = SnapshotDomains();
        RecapGridCandidateCommands.MaximumAuditEventsForTest.Value =
            auditEvents - 1;
        try {
            (int limitedCode, JsonElement limited) = RunCaptured(
                "timeline", "sync", "--input", _root,
                "--confirm-ref", refId, "--max-rows", "64"
            );
            Assert.Equal(2, limitedCode);
            Assert.Equal(
                "audit-limit",
                limited.GetProperty("status").GetString()
            );
            AssertDomainsEqual(beforeLimit, SnapshotDomains());
        }
        finally {
            RecapGridCandidateCommands.MaximumAuditEventsForTest.Value = null;
        }

        RecapGridCandidateCommands.MaximumAuditEventsForTest.Value =
            auditEvents;
        (int code, JsonElement report) result;
        try {
            result = RunCaptured(
                "timeline", "sync", "--input", _root,
                "--confirm-ref", refId, "--max-rows", "64"
            );
        }
        finally {
            RecapGridCandidateCommands.MaximumAuditEventsForTest.Value = null;
        }
        (int code, JsonElement report) = result;

        Assert.Equal(0, code);
        Assert.Equal(
            "offline-build",
            report.GetProperty("detail").GetProperty("mode").GetString()
        );
        using HistoryTimelineReaderHandle timeline = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened
        >(HistoryTimelineMaintenance.OpenReader(
            _root,
            RefId.ParseHex(refId).Value
        )).Handle;
        TimelineHeadRef head = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(timeline.Reader.ReadSnapshot()).Head;
        Assert.NotNull(head.HeadRowId);
        (int pageCode, JsonElement page) = RunCaptured(
            "timeline", "export", "--input", _root, "--max-rows", "1"
        );
        Assert.Equal(0, pageCode);
        string continuation = page.GetProperty("detail")
            .GetProperty("next").GetString()!;
        using (SessionJournalEngine selected =
               SessionJournalEngine.OpenReadOnly(_root))
        using (HistoryTimelineHandle writer = Assert.IsType<
                   HistoryTimelineOpenResult.Opened
               >(HistoryTimelineFactory.Open(
                   selected.ReadView,
                   new O200kBaseHistoryUnitLoadEstimator()
               )).Handle) {
            Assert.IsType<HistoryTimelinePolicyCasResult.Applied>(
                writer.Coordinator.CompareExchangePolicy(
                    head,
                    head.ActivePartitionPolicyDigest
                )
            );
        }
        (int staleCode, JsonElement stale) = RunCaptured(
            "timeline", "export", "--input", _root,
            "--after", continuation, "--max-rows", "1"
        );
        Assert.Equal(2, staleCode);
        Assert.Equal(
            "stale-timeline-head",
            stale.GetProperty("status").GetString()
        );
    }

    [Fact]
    public void InitPreflightsAdmissionAndPolicyBeforeAnyDomainMutation() {
        CreateJournal();
        string refId;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId.ToHexString();
        }
        string malformedAdmission = WriteBytes(
            "malformed-admission.json",
            "{}"u8.ToArray()
        );
        Assert.Equal(1, Run(
            "init", "--input", _root, "--confirm-ref", refId,
            "--admission", malformedAdmission,
            "--partition-algorithm",
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--target-history-load", "1", "--max-raw-events", "64",
            "--max-rendered-bytes", "1048576"
        ));
        Assert.False(Directory.Exists(Path.Combine(_root, "derived")));
        Assert.False(Directory.Exists(Path.Combine(_root, "control")));

        string admission = WriteAdmission(["create"]);
        Assert.Equal(1, Run(
            "init", "--input", _root, "--confirm-ref", refId,
            "--admission", admission,
            "--partition-algorithm", "unknown-partition-v1",
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--target-history-load", "1", "--max-raw-events", "64",
            "--max-rendered-bytes", "1048576"
        ));
        Assert.False(Directory.Exists(Path.Combine(_root, "derived")));
        Assert.False(Directory.Exists(Path.Combine(_root, "control")));
    }

    [Fact]
    public void OfflineAuditDriftIsTypedRawHeadChangedWithoutRetry() {
        CreateJournal(turns: 12);
        string admission = WriteAdmission(["create"]);
        RefId refId;
        EventAddress head;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId;
            head = journal.ReadView.ReadCurrentHead()!.Value;
        }
        Assert.Equal(0, RunInit(
            SessionJournalDefaults.MainBranchName,
            refId,
            admission,
            maxRawEvents: 3
        ));
        int hookCalls = 0;
        RecapGridCandidateCommands.BeforeAuditCompleteForTest.Value = () => {
            hookCalls++;
            throw new SessionSelectedLineageAuditChangedException(
                SessionSelectedLineageAuditChangeKind.RawHeadChanged,
                head,
                observedHead: null,
                "deterministic candidate CLI drift"
            );
        };
        try {
            (int code, JsonElement report) = RunCaptured(
                "timeline", "sync", "--input", _root,
                "--confirm-ref", refId.ToHexString(), "--max-rows", "64"
            );
            Assert.Equal(2, code);
            Assert.Equal(
                "raw-head-changed",
                report.GetProperty("status").GetString()
            );
            Assert.Equal(1, hookCalls);
        }
        finally {
            RecapGridCandidateCommands.BeforeAuditCompleteForTest.Value = null;
        }
    }

    [Fact]
    public void NewBranchInitCreatesItsOwnTimelineWithoutRefFallback() {
        CreateJournal();
        RefId mainRef;
        EventAddress mainHead;
        RefId featureRef;
        using (Atelia.EventJournal.EventJournal journal =
               Atelia.EventJournal.EventJournal.OpenExisting(_root)) {
            mainRef = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Value;
            mainHead = journal.GetHead(mainRef)!.Value;
            featureRef = journal.CreateBranch("feature", mainHead).Value;
        }
        using (SessionJournalEngine featureSession =
               SessionJournalEngine.Open(_root, "feature")) {
            _ = featureSession.AppendObservation("feature observation");
            _ = featureSession.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("feature answer")]),
                new CompletionDescriptor("import", "v1", "model")
            );
        }
        string admission = WriteAdmission(["create"]);
        Assert.Equal(0, RunInit(
            SessionJournalDefaults.MainBranchName,
            mainRef,
            admission
        ));
        Assert.Equal(0, RunInit("feature", featureRef, admission));

        ActiveTimelineLocator main = Assert.IsType<
            HistoryTimelineInspectResult.Available
        >(HistoryTimelineMaintenance.Inspect(_root, mainRef)).Locator;
        ActiveTimelineLocator featureLocator = Assert.IsType<
            HistoryTimelineInspectResult.Available
        >(HistoryTimelineMaintenance.Inspect(_root, featureRef)).Locator;
        Assert.NotEqual(main.ActiveTimelineId, featureLocator.ActiveTimelineId);
        Assert.Equal(mainRef, main.RefId);
        Assert.Equal(featureRef, featureLocator.RefId);
    }

    [Fact]
    public void MaintenanceCommandsMutateOnlyTheirExactDurabilityDomain() {
        CreateJournal();
        string admission = WriteAdmission(["create"]);
        RefId refId;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId;
        }
        Assert.Equal(0, RunInit(
            SessionJournalDefaults.MainBranchName,
            refId,
            admission
        ));

        string external = _root + "-maintenance";
        _externalPaths.Add(external);
        Directory.CreateDirectory(external);
        string timelineBackup = Path.Combine(external, "timeline-backup");
        string controlBackup = Path.Combine(external, "control-backup");

        DomainSnapshot before = SnapshotDomains();
        Assert.Equal(0, Run(
            "timeline", "backup", "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--output", timelineBackup
        ));
        AssertDomainsEqual(before, SnapshotDomains());

        (ActiveTimelineLocator locator, TimelineHeadRef timelineHead) =
            ReadTimelineAuthority(refId);
        string locatorFile = WriteExternal(
            external,
            "locator.json",
            locator.ToCanonicalBytes()
        );
        string timelineHeadFile = WriteExternal(
            external,
            "timeline-head.json",
            timelineHead.ToCanonicalBytes()
        );
        using (HistoryTimelineReaderHandle busyTimeline = Assert.IsType<
                   HistoryTimelineReaderOpenResult.Opened
               >(HistoryTimelineMaintenance.OpenReader(
                   _root,
                   refId
               )).Handle) {
            (int busyCode, JsonElement busy) = RunCaptured(
                "timeline", "restore", "--input", _root,
                "--confirm-ref", refId.ToHexString(),
                "--confirm-locator", locatorFile,
                "--confirm-head", timelineHeadFile,
                "--backup", timelineBackup
            );
            Assert.Equal(2, busyCode);
            Assert.Equal("busy", busy.GetProperty("status").GetString());
        }
        before = SnapshotDomains();
        Assert.Equal(0, Run(
            "timeline", "restore", "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--confirm-locator", locatorFile,
            "--confirm-head", timelineHeadFile,
            "--backup", timelineBackup
        ));
        DomainSnapshot afterTimelineRestore = SnapshotDomains();
        AssertSnapshotEqual(before.Raw, afterTimelineRestore.Raw);
        AssertSnapshotEqual(before.Control, afterTimelineRestore.Control);
        AssertSnapshotEqual(before.Grid, afterTimelineRestore.Grid);

        ControlHeadRef controlHead = ReadControlHead(refId.ToHexString());
        before = SnapshotDomains();
        Assert.Equal(0, Run([
            "control", "backup", "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            .. ControlHeadArguments(controlHead),
            "--output", controlBackup
        ]));
        AssertDomainsEqual(before, SnapshotDomains());

        using (RecapGridControlReaderHandle busyControl = Assert.IsType<
                   RecapGridControlReaderOpenResult.Opened
               >(RecapGridControlFactory.OpenReader(_root, refId)).Handle) {
            (int busyCode, JsonElement busy) = RunCaptured([
                "control", "restore", "--input", _root,
                "--confirm-ref", refId.ToHexString(),
                .. ControlHeadArguments(controlHead),
                "--backup", controlBackup
            ]);
            Assert.Equal(2, busyCode);
            Assert.Equal("busy", busy.GetProperty("status").GetString());
        }
        before = SnapshotDomains();
        Assert.Equal(0, Run([
            "control", "restore", "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            .. ControlHeadArguments(controlHead),
            "--backup", controlBackup
        ]));
        DomainSnapshot afterControlRestore = SnapshotDomains();
        AssertSnapshotEqual(before.Raw, afterControlRestore.Raw);
        AssertSnapshotEqual(before.Timeline, afterControlRestore.Timeline);
        AssertSnapshotEqual(before.Grid, afterControlRestore.Grid);
        Assert.NotEqual(
            controlHead.InstanceId,
            ReadControlHead(refId.ToHexString()).InstanceId
        );

        ControlHeadRef restoredControl = ReadControlHead(
            refId.ToHexString()
        );
        before = SnapshotDomains();
        Assert.Equal(0, Run([
            "control", "reinitialize", "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            .. ControlHeadArguments(restoredControl)
        ]));
        DomainSnapshot afterReinitialize = SnapshotDomains();
        AssertSnapshotEqual(before.Raw, afterReinitialize.Raw);
        AssertSnapshotEqual(before.Timeline, afterReinitialize.Timeline);
        AssertSnapshotEqual(before.Grid, afterReinitialize.Grid);

        before = SnapshotDomains();
        (int prepareCode, JsonElement prepared) = RunGridCaptured(
            "reset", "--prepare", "--input", _root
        );
        Assert.Equal(0, prepareCode);
        JsonElement witness = prepared.GetProperty("detail");
        using (RecapGridStoreReaderHandle busyGrid = Assert.IsType<
                   RecapGridStoreReaderOpenResult.Opened
               >(RecapGridStoreFactory.OpenReader(_root)).Handle) {
            (int busyCode, JsonElement busy) = RunGridCaptured(
                "reset", "--input", _root,
                "--confirm-length", witness.GetProperty("length")
                    .GetInt64().ToString(),
                "--confirm-sha256",
                witness.GetProperty("sha256").GetString()!
            );
            Assert.Equal(2, busyCode);
            Assert.Equal("busy", busy.GetProperty("status").GetString());
        }
        Assert.Equal(0, RunGrid(
            "reset", "--input", _root,
            "--confirm-length", witness.GetProperty("length")
                .GetInt64().ToString(),
            "--confirm-sha256", witness.GetProperty("sha256").GetString()!
        ));
        DomainSnapshot afterGridReset = SnapshotDomains();
        AssertSnapshotEqual(before.Raw, afterGridReset.Raw);
        AssertSnapshotEqual(before.Timeline, afterGridReset.Timeline);
        AssertSnapshotEqual(before.Control, afterGridReset.Control);

        (locator, _) = ReadTimelineAuthority(refId);
        locatorFile = WriteExternal(
            external,
            "locator-abandon.json",
            locator.ToCanonicalBytes()
        );
        before = SnapshotDomains();
        Assert.Equal(0, Run(
            "timeline", "abandon", "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--confirm-locator", locatorFile,
            "--partition-algorithm",
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--target-history-load", "1", "--max-raw-events", "64",
            "--max-rendered-bytes", "1048576"
        ));
        DomainSnapshot afterAbandon = SnapshotDomains();
        AssertSnapshotEqual(before.Raw, afterAbandon.Raw);
        AssertSnapshotEqual(before.Control, afterAbandon.Control);
        AssertSnapshotEqual(before.Grid, afterAbandon.Grid);
    }

    [Fact]
    public void ExplicitCandidateBuildUsesExactRuntimeRouteAndPromotesOnlyAfterZeroCallRevalidation() {
        CreateJournal();
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain the inquiry.",
            [new FamilyToolDefinition(
                "submit",
                "Submit content.",
                new FamilyObjectInputSchema([
                    new FamilyToolProperty(
                        "outcome",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String,
                            orderedEnum: ["updated", "keep-unchanged"]
                        ),
                        required: true
                    ),
                    new FamilyToolProperty(
                        "content",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String,
                            nullable: true
                        ),
                        required: true
                    )
                ])
            )],
            new FamilyOutputProtocol(
                RecapCompletionProtocolV1.OutputProtocolId,
                "submit",
                FamilyToolChoice.Required,
                allowParallel: false
            ),
            new FamilyInputRenderingProtocol(
                RecapCompletionProtocolV1.InputProtocolId,
                RecapCompletionProtocolV1.PriorProjectionSchemaId,
                RecapCompletionProtocolV1.HistorySegmentRenderingSchemaId
            )
        );
        MaintainerDefinitionRevision definition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.culprit"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "culprit"
                ),
                new MaintainerCapabilitySpec(
                    RecapCompletionProtocolV1.RuntimeProtocolId,
                    MaintainerReadableScope
                        .FullPriorBuildTargetAndCurrentHistorySegmentV1
                ),
                new MaintainerDeclarativeSpec(
                    "Who is the culprit?",
                    "Maintain the culprit hypothesis."
                ),
                16 * 1024
            );
        string admission = WriteAdmission(
            [
                "create", "register-family", "register-definition",
                "register-recipe", "activate", "promote"
            ],
            [family.Digest.Value],
            [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrierTokens.System]
        );
        string refId;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId.ToHexString();
        }
        Assert.Equal(0, Run(
            "init", "--input", _root, "--confirm-ref", refId,
            "--admission", admission,
            "--partition-algorithm",
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--target-history-load", "1", "--max-raw-events", "64",
            "--max-rendered-bytes", "1048576"
        ));
        Assert.Equal(0, Run(
            "timeline", "sync", "--input", _root,
            "--confirm-ref", refId, "--max-rows", "16"
        ));
        TimelineHeadRef timelineHead;
        HistoryTimelineSelectedRow selectedRow;
        using (HistoryTimelineReaderHandle timeline = Assert.IsType<
                   HistoryTimelineReaderOpenResult.Opened
               >(HistoryTimelineMaintenance.OpenReader(
                   _root,
                   RefId.ParseHex(refId).Value
               )).Handle) {
            timelineHead = Assert.IsType<HistoryTimelineSnapshotResult.Available>(
                timeline.Reader.ReadSnapshot()
            ).Head;
            selectedRow = Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
                timeline.Reader.ReadSelectedRow(
                    timelineHead,
                    timelineHead.HeadRowId!.Value
                )
            ).Row;
        }
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineHead.TimelineId,
            selectedRow.Descriptor.RowId,
            BuildTarget.Create([
                new BuildTargetColumn(
                    definition.LogicalColumnId,
                    definition.Digest
                )
            ])
        );
        string familyFile = WriteBytes("family.json", family.ToCanonicalBytes());
        string definitionFile = WriteBytes(
            "definition.json",
            definition.ToCanonicalBytes()
        );
        string recipeFile = WriteBytes("recipe.json", recipe.ToCanonicalBytes());
        Assert.Equal(0, Run(
            "control", "put-family", "--input", _root,
            "--confirm-ref", refId, "--admission", admission,
            "--family", familyFile
        ));
        Assert.Equal(0, Run(
            "control", "put-definition", "--input", _root,
            "--confirm-ref", refId, "--admission", admission,
            "--definition", definitionFile
        ));
        Assert.Equal(0, Run(
            "control", "put-recipe", "--input", _root,
            "--confirm-ref", refId, "--admission", admission,
            "--recipe", recipeFile
        ));

        ControlHeadRef inactiveHead = ReadControlHead(refId);
        Assert.Equal(0, Run(DirectActivationArgs(
            refId,
            admission,
            inactiveHead,
            timelineHead,
            recipe.Digest
        )));
        Assert.Equal(recipe.Digest, ReadControlHead(refId).ActiveRecipeDigest);
        (int staleControlCode, JsonElement staleControl) = RunCaptured(
            DirectActivationArgs(
                refId,
                admission,
                inactiveHead,
                timelineHead,
                recipe.Digest
            )
        );
        Assert.Equal(2, staleControlCode);
        Assert.Equal(
            "stale-control-head",
            staleControl.GetProperty("status").GetString()
        );
        ControlHeadRef activeHead = ReadControlHead(refId);
        Assert.Equal(0, Run(DirectActivationArgs(
            refId,
            admission,
            activeHead,
            timelineHead,
            recipeDigest: null
        )));
        ControlHeadRef deactivatedHead = ReadControlHead(refId);
        Assert.Null(deactivatedHead.ActiveRecipeDigest);

        using (SessionJournalEngine selected =
               SessionJournalEngine.OpenReadOnly(_root))
        using (HistoryTimelineHandle writer = Assert.IsType<
                   HistoryTimelineOpenResult.Opened
               >(HistoryTimelineFactory.Open(
                   selected.ReadView,
                   new O200kBaseHistoryUnitLoadEstimator()
               )).Handle) {
            Assert.IsType<HistoryTimelinePolicyCasResult.Applied>(
                writer.Coordinator.CompareExchangePolicy(
                    timelineHead,
                    timelineHead.ActivePartitionPolicyDigest
                )
            );
        }
        (int staleTimelineCode, JsonElement staleTimeline) = RunCaptured(
            DirectActivationArgs(
                refId,
                admission,
                deactivatedHead,
                timelineHead,
                recipe.Digest
            )
        );
        Assert.Equal(2, staleTimelineCode);
        Assert.Equal(
            "stale-timeline-head",
            staleTimeline.GetProperty("status").GetString()
        );

        string routes = WriteBytes(
            "routes.json",
            RecapGridRouteManifest.Create([
                new RecapGridRouteManifestEntry(
                    new RecapCompletionRouteKey(
                        family.Digest,
                        RecapCompletionProtocolV1.RuntimeProtocolId,
                        null
                    ),
                    "test",
                    1,
                    TimeSpan.FromSeconds(30),
                    128
                )
            ]).ToCanonicalBytes()
        );
        string connections = Path.Combine(_root, "connections.json");
        File.WriteAllText(connections, """
            {
              "connections": [{
                "id": "test",
                "kind": "test",
                "modelId": "test-model",
                "completionSurfaceId": "test-v1",
                "baseAddress": "https://example.invalid"
              }]
            }
            """);
        var factory = new DeterministicCompletionClientFactory();
        string malformedConnections = Path.Combine(
            _root,
            "connections-invalid.json"
        );
        File.WriteAllBytes(malformedConnections, [0xff]);
        Dictionary<string, byte[]> beforeMalformed = SnapshotDirectory(_root);
        Assert.Equal(1, RunWithFactory(factory,
            "build", "--input", _root, "--confirm-ref", refId,
            "--recipe", recipe.Digest.Value,
            "--max-selected-rows", "64", "--max-recipe-row-steps", "64",
            "--max-new-calls", "8", "--max-elapsed-ms", "10000",
            "--routes", routes, "--connections", malformedConnections
        ));
        Assert.Equal(0, factory.CallCount);
        AssertSnapshotEqual(beforeMalformed, SnapshotDirectory(_root));

        (int buildCode, JsonElement build) = RunCapturedWithFactory(factory,
            "build", "--input", _root, "--confirm-ref", refId,
            "--recipe", recipe.Digest.Value,
            "--max-selected-rows", "64", "--max-recipe-row-steps", "64",
            "--max-new-calls", "8", "--max-elapsed-ms", "10000",
            "--routes", routes, "--connections", connections
        );
        Assert.Equal(0, buildCode);
        Assert.Equal("fulfilled", build.GetProperty("status").GetString());
        JsonElement evidence = build.GetProperty("detail")
            .GetProperty("evidence");
        Assert.NotEmpty(evidence.GetProperty("Events").EnumerateArray());
        Assert.True(evidence.GetProperty("RetainedUtf8Bytes").GetInt32() > 0);
        Assert.Equal(1, factory.CallCount);
        Assert.Equal(1, factory.DisposeCount);
        Assert.Null(ReadControlHead(refId).ActiveRecipeDigest);

        (int progressCode, JsonElement progress) = RunCaptured(
            "progress", "--input", _root,
            "--recipe", recipe.Digest.Value,
            "--max-selected-rows", "64",
            "--max-recipe-row-steps", "64",
            "--max-new-calls", "0",
            "--max-elapsed-ms", "10000"
        );
        Assert.Equal(0, progressCode);
        Assert.Equal("complete", progress.GetProperty("status").GetString());
        Assert.Equal(1, factory.CallCount);

        Assert.Equal(0, Run(
            "control", "promote", "--input", _root,
            "--confirm-ref", refId, "--admission", admission,
            "--recipe", recipe.Digest.Value,
            "--max-selected-rows", "64", "--max-recipe-row-steps", "64",
            "--max-new-calls", "0", "--max-elapsed-ms", "10000"
        ));
        Assert.Equal(recipe.Digest, ReadControlHead(refId).ActiveRecipeDigest);
        Assert.Equal(1, factory.CallCount);

        (int materializeCode, JsonElement materialized) = RunCaptured(
            "materialize", "--input", _root,
            "--boundary", EventAddressTextCodec.Format(
                selectedRow.Descriptor.EndInclusive
            ),
            "--nth-previous", "0", "--include-content"
        );
        Assert.Equal(0, materializeCode);
        Assert.Equal(
            "available",
            materialized.GetProperty("status").GetString()
        );
        Assert.Equal(1, factory.CallCount);
    }

    private void CreateJournal(int turns = 1) {
        using SessionJournalLegacyImportWriter writer =
            SessionJournalLegacyImportWriter.Create(
                _root,
                new SessionCreateOptions("model", "system", "candidate-cli")
            );
        for (int index = 0; index < turns; index++) {
            _ = writer.AppendObservation($"observation {index}");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"answer {index}")
                ]),
                new CompletionDescriptor("import", "v1", "model")
            );
        }
    }

    private int CountSelectedAuditEvents() {
        using SessionJournalEngine engine =
            SessionJournalEngine.OpenReadOnly(_root);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        while (!audit.IsCaptureComplete) {
            _ = audit.ReadNextPage(
                SessionSelectedLineageAuditLimits.MaximumPageEventCount
            );
        }
        _ = audit.Complete();
        return checked((int)audit.EventCount);
    }

    private string WriteAdmission(
        string[] permissions,
        string[]? families = null,
        string[]? capabilities = null,
        string[]? carriers = null
    ) {
        string path = Path.Combine(_root, "admission.json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            v = 1,
            permissions,
            familyAllowlist = families ?? Array.Empty<string>(),
            capabilityFingerprintAllowlist = capabilities
                ?? Array.Empty<string>(),
            targetCarrierAllowlist = carriers ?? Array.Empty<string>(),
            logicalColumnPrefixes = new[] { "case." },
            maximumBootstrapRows = 64,
            maximumProjectedCalls = 1024
        });
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WriteBytes(string name, byte[] bytes) {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string WriteExternal(
        string directory,
        string name,
        byte[] bytes
    ) {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private (ActiveTimelineLocator Locator, TimelineHeadRef Head)
        ReadTimelineAuthority(RefId refId) {
        ActiveTimelineLocator locator = Assert.IsType<
            HistoryTimelineInspectResult.Available
        >(HistoryTimelineMaintenance.Inspect(_root, refId)).Locator;
        using HistoryTimelineReaderHandle handle = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened
        >(HistoryTimelineMaintenance.OpenReader(_root, refId)).Handle;
        TimelineHeadRef head = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        return (locator, head);
    }

    private static string[] ControlHeadArguments(ControlHeadRef head) => [
        "--confirm-instance", head.InstanceId.Value,
        "--confirm-timeline", head.TimelineId.Value,
        "--confirm-generation", head.Generation.ToString(),
        "--confirm-state", head.StateDigest.Value,
        "--confirm-active", head.ActiveRecipeDigest?.Value ?? "none"
    ];

    private DomainSnapshot SnapshotDomains() => new(
        SnapshotRawAuthority(),
        SnapshotDirectory(Path.Combine(
            _root,
            "derived",
            "history-timeline"
        )),
        SnapshotDirectory(Path.Combine(
            _root,
            "control",
            "recap-grid"
        )),
        SnapshotDirectory(Path.Combine(
            _root,
            "derived",
            "recap-grid"
        ))
    );

    private Dictionary<string, byte[]> SnapshotRawAuthority() {
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(
                     _root,
                     "*",
                     SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(_root, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("derived/", StringComparison.Ordinal)
                || relative.StartsWith("control/", StringComparison.Ordinal)
                || relative.EndsWith("admission.json", StringComparison.Ordinal)
                || relative.EndsWith("routes.json", StringComparison.Ordinal)
                || relative.EndsWith("connections.json",
                    StringComparison.Ordinal)) {
                continue;
            }
            snapshot.Add(relative, File.ReadAllBytes(file));
        }
        return snapshot;
    }

    private static Dictionary<string, byte[]> SnapshotDirectory(string root) {
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) { return snapshot; }
        foreach (string file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories)) {
            snapshot.Add(
                Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes(file)
            );
        }
        return snapshot;
    }

    private static void AssertDomainsEqual(
        DomainSnapshot expected,
        DomainSnapshot actual
    ) {
        AssertSnapshotEqual(expected.Raw, actual.Raw);
        AssertSnapshotEqual(expected.Timeline, actual.Timeline);
        AssertSnapshotEqual(expected.Control, actual.Control);
        AssertSnapshotEqual(expected.Grid, actual.Grid);
    }

    private static void AssertSnapshotEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual
    ) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string path, byte[] bytes) in expected) {
            Assert.Equal(bytes, actual[path]);
        }
    }

    private sealed record DomainSnapshot(
        IReadOnlyDictionary<string, byte[]> Raw,
        IReadOnlyDictionary<string, byte[]> Timeline,
        IReadOnlyDictionary<string, byte[]> Control,
        IReadOnlyDictionary<string, byte[]> Grid
    );

    private string[] DirectActivationArgs(
        string refId,
        string admission,
        ControlHeadRef controlHead,
        TimelineHeadRef timelineHead,
        GridBuildRecipeDigest? recipeDigest
    ) {
        string timeline = WriteBytes(
            $"timeline-{Guid.NewGuid():N}.json",
            timelineHead.ToCanonicalBytes()
        );
        var args = new List<string> {
            "control", "activate", "--input", _root,
            "--confirm-ref", refId,
            "--admission", admission,
            "--confirm-instance", controlHead.InstanceId.Value,
            "--confirm-timeline", controlHead.TimelineId.Value,
            "--confirm-generation", controlHead.Generation.ToString(),
            "--confirm-state", controlHead.StateDigest.Value,
            "--confirm-active", controlHead.ActiveRecipeDigest?.Value ?? "none",
            "--confirm-timeline-head", timeline
        };
        if (recipeDigest is { } recipe) {
            args.Add("--recipe");
            args.Add(recipe.Value);
        }
        else {
            args.Add("--deactivate");
        }
        return args.ToArray();
    }

    private int RunInit(
        string branch,
        RefId refId,
        string admission,
        int maxRawEvents = 64
    ) => Run(
        "init", "--input", _root, "--branch", branch,
        "--confirm-ref", refId.ToHexString(), "--admission", admission,
        "--partition-algorithm",
        HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
        "--history-load-estimator",
        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
        "--target-history-load", "1",
        "--max-raw-events", maxRawEvents.ToString(),
        "--max-rendered-bytes", "1048576"
    );

    private ControlHeadRef ReadControlHead(string refId) {
        using RecapGridControlReaderHandle handle = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened
        >(RecapGridControlFactory.OpenReader(
            _root,
            RefId.ParseHex(refId).Value
        )).Handle;
        return Assert.IsType<RecapGridControlSnapshotResult.Available>(
            handle.Reader.ReadSnapshot()
        ).Snapshot.Head;
    }

    private static int Run(params string[] args) => Program.MainCore(
        ["recap-grid", "candidate", .. args],
        ThrowingCompletionClientFactory.Instance
    );

    private static int RunWithFactory(
        ICompletionClientFactory factory,
        params string[] args
    ) => Program.MainCore(
        ["recap-grid", "candidate", .. args],
        factory
    );

    private static int RunGrid(params string[] args) => Program.MainCore(
        ["recap-grid", .. args],
        ThrowingCompletionClientFactory.Instance
    );

    private static (int ExitCode, JsonElement Json) RunCaptured(
        params string[] args
    ) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int exitCode = Run(args);
            string json = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries
            )[^1];
            using JsonDocument document = JsonDocument.Parse(json);
            return (exitCode, document.RootElement.Clone());
        }
        finally {
            Console.SetOut(original);
        }
    }

    private static (int ExitCode, JsonElement Json) RunGridCaptured(
        params string[] args
    ) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int exitCode = RunGrid(args);
            string json = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries
            )[^1];
            using JsonDocument document = JsonDocument.Parse(json);
            return (exitCode, document.RootElement.Clone());
        }
        finally {
            Console.SetOut(original);
        }
    }

    private static (int ExitCode, JsonElement Json) RunCapturedWithFactory(
        ICompletionClientFactory factory,
        params string[] args
    ) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int exitCode = RunWithFactory(factory, args);
            string json = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries
            )[^1];
            using JsonDocument document = JsonDocument.Parse(json);
            return (exitCode, document.RootElement.Clone());
        }
        finally {
            Console.SetOut(original);
        }
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
        foreach (string path in _externalPaths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path)) {
                File.Delete(path);
            }
        }
    }

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        internal static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(CompletionConnectionConfig connection)
            => throw new InvalidOperationException(
                $"Candidate diagnostics must not construct '{connection.Id}'."
            );
    }

    private sealed class DeterministicCompletionClientFactory
        : ICompletionClientFactory {
        internal int CallCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CallCount++;
            return new DeterministicCompletionClient(
                () => DisposeCount++
            );
        }
    }

    private sealed class DeterministicCompletionClient(Action onDispose)
        : ICompletionClient, IDisposable {
        private int _disposed;
        public string Name => "candidate-test";
        public string ApiSpecId => "candidate-test-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new CompletionResult(
            new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                "submit",
                "candidate-call",
                "{\"outcome\":\"updated\",\"content\":\"candidate result\"}"
            ))]),
            new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
        ));

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => StreamCompletionAsync(request, observer, cancellationToken);

        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) {
                onDispose();
            }
        }
    }
}
