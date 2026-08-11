using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class ProgramRecapGridCommandTests : IDisposable {
    private readonly List<string> _externalPaths = [];
    private readonly string _root = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-recap-grid-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void OperatorProvisionBuiltInIsExplicitReplayableAndAdmitted() {
        CreateJournal();
        Assert.True(RecapGridAgentControlBuiltIns
            .TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV1,
                out RecapGridControlRegistrationBundle? bundle
            ));
        string createOnly = WriteAdmission(["create"]);
        RefId refId;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId;
        }
        Assert.Equal(0, RunInit(
            SessionJournalDefaults.MainBranchName,
            refId,
            createOnly
        ));
        ControlHeadRef initial = ReadControlHead(refId.ToHexString());

        (int unauthorizedCode, JsonElement unauthorized) = RunCaptured(
            "control", "provision-built-in",
            "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--admission", createOnly,
            "--asset",
            RecapGridAgentControlBuiltIns.MysteryInvestigationV1
        );
        Assert.Equal(2, unauthorizedCode);
        Assert.Equal(
            "unauthorized",
            unauthorized.GetProperty("status").GetString()
        );
        Assert.Equal(initial, ReadControlHead(refId.ToHexString()));

        string admitted = WriteAdmission(
            ["create", "register-family", "register-definition"],
            bundle!.Families.Select(static value => value.Digest.Value)
                .ToArray(),
            bundle.Definitions.Select(static value =>
                value.Capability.CapabilityFingerprint).Distinct().ToArray(),
            [ContextHeaderCarrierTokens.System]
        );
        (int appliedCode, JsonElement applied) = RunCaptured(
            "control", "provision-built-in",
            "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--admission", admitted,
            "--asset",
            RecapGridAgentControlBuiltIns.MysteryInvestigationV1
        );
        Assert.Equal(0, appliedCode);
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        ControlHeadRef provisioned = ReadControlHead(refId.ToHexString());
        Assert.Equal(initial.Generation + 1, provisioned.Generation);

        (int replayCode, JsonElement replay) = RunCaptured(
            "control", "provision-built-in",
            "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--admission", admitted,
            "--asset",
            RecapGridAgentControlBuiltIns.MysteryInvestigationV1
        );
        Assert.Equal(0, replayCode);
        Assert.Equal("replayed", replay.GetProperty("status").GetString());
        Assert.Equal(provisioned, ReadControlHead(refId.ToHexString()));

        (int unknownCode, JsonElement unknown) = RunCaptured(
            "control", "provision-built-in",
            "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--admission", admitted,
            "--asset", "unknown-built-in"
        );
        Assert.Equal(2, unknownCode);
        Assert.Equal(
            "built-in-asset-absent",
            unknown.GetProperty("status").GetString()
        );
        Assert.Equal(provisioned, ReadControlHead(refId.ToHexString()));

        Assert.Equal(0, Run([
            "control", "reinitialize",
            "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            .. ControlHeadArguments(provisioned)
        ]));
        ControlHeadRef reinitialized = ReadControlHead(
            refId.ToHexString()
        );
        Assert.NotEqual(provisioned.InstanceId, reinitialized.InstanceId);
        (int reappliedCode, JsonElement reapplied) = RunCaptured(
            "control", "provision-built-in",
            "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--admission", admitted,
            "--asset",
            RecapGridAgentControlBuiltIns.MysteryInvestigationV1
        );
        Assert.Equal(0, reappliedCode);
        Assert.Equal("applied", reapplied.GetProperty("status").GetString());
        ControlHeadRef reprovisioned = ReadControlHead(
            refId.ToHexString()
        );
        Assert.Equal(reinitialized.Generation + 1,
            reprovisioned.Generation);
        (int retryCode, JsonElement retry) = RunCaptured(
            "control", "provision-built-in",
            "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--admission", admitted,
            "--asset",
            RecapGridAgentControlBuiltIns.MysteryInvestigationV1
        );
        Assert.Equal(0, retryCode);
        Assert.Equal("replayed", retry.GetProperty("status").GetString());
        Assert.Equal(reprovisioned, ReadControlHead(refId.ToHexString()));

        using RecapGridControlReaderHandle reader = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened
        >(RecapGridControlFactory.OpenReader(_root, refId)).Handle;
        RecapGridControlSnapshot snapshot = Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(reader.Reader.ReadSnapshot()).Snapshot;
        Assert.Single(snapshot.Families);
        Assert.Equal(2, snapshot.Definitions.Count);
    }

    [Fact]
    public void ComposeFullRecipeUsesExactEmptyTimelineAndOrderedDefinitions() {
        CreateJournal();
        Assert.True(RecapGridAgentControlBuiltIns
            .TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV1,
                out RecapGridControlRegistrationBundle? bundle
            ));
        RefId refId;
        using (SessionJournalEngine journal =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId;
        }
        Assert.Equal(0, RunInit(
            SessionJournalDefaults.MainBranchName,
            refId,
            WriteAdmission(["create"])
        ));
        string admitted = WriteAdmission(
            ["create", "register-family", "register-definition"],
            bundle!.Families.Select(static value => value.Digest.Value)
                .ToArray(),
            bundle.Definitions.Select(static value =>
                value.Capability.CapabilityFingerprint).Distinct().ToArray(),
            [ContextHeaderCarrierTokens.System]
        );
        Assert.Equal(0, Run(
            "control", "provision-built-in",
            "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--admission", admitted,
            "--asset", RecapGridAgentControlBuiltIns.MysteryInvestigationV1
        ));
        string output = _root + "-full-recipe.json";
        _externalPaths.Add(output);

        string[] arguments = [
            "control", "compose-full-recipe",
            "--input", _root,
            "--output", output,
            .. bundle.Definitions.SelectMany(static definition =>
                new[] { "--definition", definition.Digest.Value! })
        ];
        Assert.Equal(0, Run(arguments));

        GridBuildRecipe recipe = GridBuildRecipe.DecodeCanonical(
            File.ReadAllBytes(output)
        );
        Assert.Equal(GridBuildRecipeKind.Full, recipe.Kind);
        Assert.Null(recipe.BootstrapThroughRowId);
        Assert.Equal(
            bundle.Definitions.Select(static value => value.Digest),
            recipe.Target.OrderedColumns.Select(static value =>
                value.DefinitionDigest)
        );

        Assert.Equal(0, Run(
            "timeline", "sync",
            "--input", _root,
            "--confirm-ref", refId.ToHexString(),
            "--max-rows", "16"
        ));
        string nonemptyOutput = _root + "-nonempty-full-recipe.json";
        _externalPaths.Add(nonemptyOutput);
        string[] nonemptyArguments = [
            "control", "compose-full-recipe",
            "--input", _root,
            "--output", nonemptyOutput,
            .. bundle.Definitions.SelectMany(static definition =>
                new[] { "--definition", definition.Digest.Value! })
        ];
        Assert.Equal(0, Run(nonemptyArguments));

        GridBuildRecipe nonemptyRecipe = GridBuildRecipe.DecodeCanonical(
            File.ReadAllBytes(nonemptyOutput)
        );
        using HistoryTimelineReaderHandle timeline = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened
        >(HistoryTimelineMaintenance.OpenReader(_root, refId)).Handle;
        TimelineHeadRef timelineHead = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(timeline.Reader.ReadSnapshot()).Head;
        Assert.NotNull(timelineHead.HeadRowId);
        Assert.Equal(
            timelineHead.HeadRowId,
            nonemptyRecipe.BootstrapThroughRowId
        );
    }

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
        Assert.Equal(1, RunWithFactory(
            provider,
            "run-online-turn",
            "--input", _root,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", new string('0', 16),
            "--message", "must not run",
            "--connection", "missing",
            "--connections", Path.Combine(_root, "missing-connections.json"),
            "--routes", Path.Combine(_root, "missing-routes.json")
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
        RecapGridCommands.MaximumAuditEventsForTest.Value =
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
            RecapGridCommands.MaximumAuditEventsForTest.Value = null;
        }

        RecapGridCommands.MaximumAuditEventsForTest.Value =
            auditEvents;
        (int code, JsonElement report) result;
        try {
            result = RunCaptured(
                "timeline", "sync", "--input", _root,
                "--confirm-ref", refId, "--max-rows", "64"
            );
        }
        finally {
            RecapGridCommands.MaximumAuditEventsForTest.Value = null;
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
        RecapGridCommands.BeforeAuditCompleteForTest.Value = () => {
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
            RecapGridCommands.BeforeAuditCompleteForTest.Value = null;
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
    public async Task ExplicitCandidateBuildUsesExactRuntimeRouteAndPromotesOnlyAfterZeroCallRevalidation() {
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
        MaintainerDefinitionRevision suspicion =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.x-suspicion"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "x-suspicion"
                ),
                new MaintainerCapabilitySpec(
                    RecapCompletionProtocolV1.RuntimeProtocolId,
                    MaintainerReadableScope
                        .FullPriorBuildTargetAndCurrentHistorySegmentV1
                ),
                new MaintainerDeclarativeSpec(
                    "Is X suspicious?",
                    "Maintain the exact evidence for and against X."
                ),
                16 * 1024
            );
        var admissionValue = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1024
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
        GridBuildRecipe baseRecipe = GridBuildRecipe.CreateFull(
            timelineHead.TimelineId,
            selectedRow.Descriptor.RowId,
            BuildTarget.Create([
                new BuildTargetColumn(
                    definition.LogicalColumnId,
                    definition.Digest
                )
            ])
        );
        GridBuildRecipe recipe = GridBuildRecipe.CreateOverlay(
            baseRecipe,
            selectedRow.Descriptor.RowId,
            BuildTarget.Create([
                new BuildTargetColumn(
                    definition.LogicalColumnId,
                    definition.Digest
                ),
                new BuildTargetColumn(
                    suspicion.LogicalColumnId,
                    suspicion.Digest
                )
            ]),
            [suspicion.LogicalColumnId]
        );
        RecapGridAgentControlProfile agentProfile =
            RecapGridAgentControlProfile.Create(
                "candidate-build-v1",
                admissionValue
            );
        async Task AgentApply(
            RecapGridAgentControlHandle agent,
            string action,
            byte[] canonicalBytes,
            string suffix,
            int operationSequence
        ) {
            ToolCallExecutionResult result = await agent.ToolSession
                .ExecuteReservedAsync(
                    new RawToolCall(
                        "recap_grid.control",
                        $"control-{suffix}",
                        JsonSerializer.Serialize(new {
                            action,
                            canonicalValueBase64 = Convert.ToBase64String(
                                canonicalBytes
                            )
                        })
                    ),
                    operationSequence,
                    $"candidate-build:{suffix}",
                    CancellationToken.None
                );
            Assert.Equal(ToolExecutionStatus.Success,
                result.ExecuteResult.Status);
            Assert.Contains("\"status\":\"applied\"",
                result.ExecuteResult.GetFlattenedText());
        }
        using (SessionJournalEngine agentOwner =
               SessionJournalEngine.OpenReadOnly(_root))
        using (RecapGridAgentControlHandle agent = Assert.IsType<
                   RecapGridAgentControlOpenResult.Opened
               >(RecapGridAgentControlFactory.Bind(
                   agentOwner.ReadView,
                   agentProfile,
                   new O200kBaseHistoryUnitLoadEstimator()
               )).Handle) {
            await AgentApply(agent, "register-family",
                family.ToCanonicalBytes(), "family", 1);
            await AgentApply(agent, "register-definition",
                definition.ToCanonicalBytes(), "culprit-definition", 2);
            await AgentApply(agent, "register-definition",
                suspicion.ToCanonicalBytes(), "suspicion-definition", 3);
            await AgentApply(agent, "register-recipe",
                baseRecipe.ToCanonicalBytes(), "base-recipe", 4);
            await AgentApply(agent, "register-recipe",
                recipe.ToCanonicalBytes(), "overlay-recipe", 5);
        }

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

        int requestsBeforeOnline = factory.RequestCount;
        int recapRequestsBeforePromotion = factory.RecapRequestCount;
        int receiptsBeforePromotion = ReadControlReceiptCount(
            refId,
            timelineHead.TimelineId
        );
        factory.EmitAgentControlPromoteRecipeOnce = recipe.Digest.Value;
        string oldV8 = Path.Combine(
            _root, "derived", "recap", "v8", "corrupt-sentinel.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(oldV8)!);
        File.WriteAllBytes(oldV8, [8, 0, 8, 0, 7]);
        byte[] oldV8Before = File.ReadAllBytes(oldV8);
        (int firstOnlineCode, JsonElement firstOnline) =
            RunCapturedWithFactory(factory,
                "run-online-turn",
                "--input", _root,
                "--branch", SessionJournalDefaults.MainBranchName,
                "--confirm-ref", refId,
                "--message", "continue the investigation",
                "--connection", "test",
                "--admission", admission,
                "--connections", connections,
                "--routes", routes);
        Assert.Equal(0, firstOnlineCode);
        Assert.Equal("completed",
            firstOnline.GetProperty("status").GetString());
        Assert.Equal(requestsBeforeOnline + 2, factory.RequestCount);
        Assert.Equal(1, factory.PromoteToolCallCount);
        Assert.Null(factory.EmitAgentControlPromoteRecipeOnce);
        Assert.Equal(
            recapRequestsBeforePromotion,
            factory.RecapRequestCount
        );
        Assert.Equal(recipe.Digest,
            ReadControlHead(refId).ActiveRecipeDigest);
        Assert.Equal(
            receiptsBeforePromotion + 1,
            ReadControlReceiptCount(refId, timelineHead.TimelineId)
        );
        Assert.Contains(
            factory.Requests.Skip(requestsBeforeOnline),
            static request => request.PromptPrefix.OutputContract.Tools.All(
                    static tool => !string.Equals(
                        tool.Name,
                        "submit",
                        StringComparison.Ordinal
                    ))
                && request.PromptPrefix.SystemPrompt.Contains(
                    "candidate result",
                    StringComparison.Ordinal
                )
        );
        int clientsAfterFirstOnline = factory.CallCount;
        Assert.Equal(oldV8Before, File.ReadAllBytes(oldV8));
        EventAddress materializeBoundary;
        using (SessionJournalEngine current =
               SessionJournalEngine.OpenReadOnly(_root)) {
            materializeBoundary = current.ReadCurrentHead()
                ?? throw new InvalidOperationException(
                    "Candidate Host left no current raw head."
                );
        }

        (int materializeCode, JsonElement materialized) = RunCaptured(
            "materialize", "--input", _root,
            "--boundary", EventAddressTextCodec.Format(materializeBoundary),
            "--nth-previous", "0", "--include-content"
        );
        Assert.True(materializeCode == 0, materialized.GetRawText());
        Assert.Equal(
            "available",
            materialized.GetProperty("status").GetString()
        );
        Assert.Equal(clientsAfterFirstOnline, factory.CallCount);

        int beforeSecondOnline = factory.RequestCount;
        (int secondOnlineCode, JsonElement secondOnline) =
            RunCapturedWithFactory(factory,
                "run-online-turn",
                "--input", _root,
                "--branch", SessionJournalDefaults.MainBranchName,
                "--confirm-ref", refId,
                "--message", "reconsider X",
                "--connection", "test",
                "--admission", admission,
                "--connections", connections,
                "--routes", routes);
        Assert.Equal(0, secondOnlineCode);
        Assert.Equal("completed",
            secondOnline.GetProperty("status").GetString());
        Assert.True(factory.RequestCount >= beforeSecondOnline + 2);
        Assert.Contains(factory.Requests, static request =>
            request.PromptPrefix.OutputContract.Tools.All(static tool =>
                !string.Equals(tool.Name, "submit", StringComparison.Ordinal))
            && request.PromptPrefix.SystemPrompt.Contains(
                "candidate result",
                StringComparison.Ordinal
            ));
        Assert.Equal(oldV8Before, File.ReadAllBytes(oldV8));
        Atelia.SessionJournal.Offline.SessionJournalOfflineValidationReport
            raw = await Atelia.SessionJournal.Offline
                .SessionJournalOfflineValidator.ValidateAsync(_root);
        Assert.True(raw.PreparedRequestCount >= 2);
        Assert.True(raw.ToolResultHistoryCount >= 1);
        Assert.Equal(SessionExecutionPhase.Idle, raw.ExecutionPhase);
    }

    [Fact]
    public async Task PreparedRecoveryBindsExactConnectionWithoutRoutesOrDerivedState() {
        var factory = new DeterministicCompletionClientFactory();
        CompletionConnectionConfig connection = CandidateConnection();
        await CreatePreparedAsync(connection);
        string refId;
        using (SessionJournalEngine reader =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = reader.BranchRefId.ToHexString();
        }
        string connections = WriteConnections();
        string missingRoutes = Path.Combine(_root, "must-not-read-routes.json");

        (int code, JsonElement report) = RunCapturedWithFactory(
            factory,
            "run-online-turn",
            "--input", _root,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", refId,
            "--connection", connection.Id,
            "--connections", connections,
            "--routes", missingRoutes);

        Assert.Equal(0, code);
        Assert.Equal("completed", report.GetProperty("status").GetString());
        Assert.Equal(1, factory.CallCount);
        Assert.Equal(1, factory.RequestCount);
        Assert.False(Directory.Exists(Path.Combine(_root, "derived")));
    }

    [Fact]
    public async Task ToolContinuationBindsFrozenProfileAndReplaysReceiptAfterRewind() {
        CompletionConnectionConfig connection = CandidateConnection();
        Assert.True(RecapGridAgentControlBuiltIns
            .TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV1,
                out RecapGridControlRegistrationBundle? builtIn
            ));
        var admissionValue = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [builtIn!.Families[0].Digest],
            builtIn.Definitions.Select(static value =>
                value.Capability.CapabilityFingerprint),
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1_024
        );
        using (SessionJournalEngine.Create(
                   _root,
                   new SessionCreateOptions(
                       connection.ModelId,
                       "system",
                       connection.CompletionSurfaceId))) { }
        string admission = WriteAdmission(
            [
                "create", "register-family", "register-definition",
                "register-recipe", "activate", "promote"
            ],
            [builtIn.Families[0].Digest.Value],
            builtIn.Definitions.Select(static value =>
                value.Capability.CapabilityFingerprint).ToArray(),
            [ContextHeaderCarrierTokens.System]
        );
        string refId;
        using (SessionJournalEngine reader =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = reader.BranchRefId.ToHexString();
        }
        Assert.Equal(0, Run(
            "init", "--input", _root, "--confirm-ref", refId,
            "--admission", admission,
            "--partition-algorithm",
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--target-history-load", "1",
            "--max-raw-events", "64",
            "--max-rendered-bytes", "1048576"
        ));

        EventAddress actionHead;
        RecapGridAgentControlHandle agent;
        using (SessionJournalEngine bindingOwner =
               SessionJournalEngine.OpenReadOnly(_root)) {
            agent = Assert.IsType<RecapGridAgentControlOpenResult.Opened>(
                RecapGridAgentControlFactory.Bind(
                    bindingOwner.ReadView,
                    RecapGridAgentControlProfile.Create(
                        "recap-grid-cli-v1",
                        admissionValue
                    ),
                    new O200kBaseHistoryUnitLoadEstimator()
                )
            ).Handle;
        }
        using (agent) {
            var fixtureClient = new ControlToolCallClient();
            CompletionDispatchIdentity identity =
                CompletionDispatchIdentityFactory.Create(
                    connection,
                    fixtureClient
                );
            var runtime = new SessionRuntime(
                fixtureClient,
                agent.ToolSession,
                CompletionTargetIdentityFactory.Create(identity),
                ToolRuntimeIdentity: agent.RuntimeIdentity,
                ContextCandidateSource: new EmptyCandidateSource()
            );
            using SessionJournalEngine engine =
                SessionJournalEngine.OpenForTest(
                    _root,
                    runtime,
                    new SessionJournalTestHooks(
                        SessionJournalFailpoint.AfterActionCommitted
                    )
                );
            _ = await Assert.ThrowsAsync<
                SessionJournalFailpointException>(() => engine.SendAsync(
                    engine.ReadCurrentHead()!.Value,
                    "provision from tool"
                ));
            actionHead = engine.ReadCurrentHead()!.Value;
        }

        string connections = WriteConnections();
        string routes = WriteBytes(
            "tool-continuation-routes.json",
            RecapGridRouteManifest.Create([]).ToCanonicalBytes()
        );
        var provider = new DeterministicCompletionClientFactory();
        Assert.Equal(1, RunWithFactory(
            provider,
            "run-online-turn",
            "--input", _root,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", refId,
            "--connection", connection.Id,
            "--admission", admission,
            "--connections", connections
        ));
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, RunWithFactory(
            provider,
            "run-online-turn",
            "--input", _root,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", refId,
            "--connection", connection.Id,
            "--admission", admission,
            "--connections", connections,
            "--routes", routes
        ));
        ControlHeadRef applied = ReadControlHead(refId);
        Assert.Equal(1, applied.Generation);

        EventAddress toolResult = ReadLatestSelectedAddressByKind(
            SessionEventKind.ToolResultObserved
        );
        using (var journal = Atelia.EventJournal.EventJournal.OpenExisting(
                   _root)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            EventAddress completed = journal.GetHead(main)!.Value;
            Assert.True(journal.MoveRef(
                main,
                completed,
                toolResult
            ).Unwrap());
        }
        int callsBeforeToolResultResume = provider.CallCount;
        Assert.Equal(1, RunWithFactory(
            provider,
            "run-online-turn",
            "--input", _root,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", refId,
            "--connection", connection.Id,
            "--admission", admission,
            "--connections", connections
        ));
        Assert.Equal(callsBeforeToolResultResume, provider.CallCount);
        Assert.Equal(0, RunWithFactory(
            provider,
            "run-online-turn",
            "--input", _root,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", refId,
            "--connection", connection.Id,
            "--admission", admission,
            "--connections", connections,
            "--routes", routes
        ));
        Assert.Equal(1, ReadControlHead(refId).Generation);

        using (var journal = Atelia.EventJournal.EventJournal.OpenExisting(
                   _root)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            EventAddress completed = journal.GetHead(main)!.Value;
            Assert.True(journal.MoveRef(
                main,
                completed,
                actionHead
            ).Unwrap());
        }
        Assert.Equal(0, RunWithFactory(
            provider,
            "run-online-turn",
            "--input", _root,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", refId,
            "--connection", connection.Id,
            "--admission", admission,
            "--connections", connections,
            "--routes", routes
        ));
        Assert.Equal(1, ReadControlHead(refId).Generation);
    }

    [Fact]
    public async Task StartedRefuseReturnsBeforeConnectionsOrProvider() {
        var factory = new DeterministicCompletionClientFactory();
        CompletionConnectionConfig connection = CandidateConnection();
        EventAddress prepared = await CreatePreparedAsync(connection);
        using (var journal = Atelia.EventJournal.EventJournal.OpenExisting(
                   _root)) {
            _ = journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                prepared,
                SessionEventCodec.Encode(
                    SessionEventKind.CompletionAttemptStarted,
                    new CompletionAttemptStartedBody()),
                opaqueEventKind:
                    (uint)SessionEventKind.CompletionAttemptStarted,
                hint: default).Unwrap();
        }
        string refId;
        using (SessionJournalEngine reader =
               SessionJournalEngine.OpenReadOnly(_root)) {
            refId = reader.BranchRefId.ToHexString();
        }

        (int code, JsonElement report) = RunCapturedWithFactory(
            factory,
            "run-online-turn",
            "--input", _root,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", refId,
            "--connections", Path.Combine(_root, "missing-connections.json"),
            "--routes", Path.Combine(_root, "missing-routes.json"));

        Assert.Equal(2, code);
        Assert.Equal(
            "started-outcome-uncertain",
            report.GetProperty("status").GetString());
        Assert.Equal(0, factory.CallCount);
        Assert.False(Directory.Exists(Path.Combine(_root, "derived")));
    }

    private void CreateJournal(int turns = 1) {
        using SessionJournalLegacyImportWriter writer =
            SessionJournalLegacyImportWriter.Create(
                _root,
                new SessionCreateOptions("model", "system", "recap-grid-cli")
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

    private async Task<EventAddress> CreatePreparedAsync(
        CompletionConnectionConfig connection
    ) {
        var client = new DeterministicCompletionClient(
            static _ => { },
            static () => { },
            static _ => new ActionMessage([
                new ActionBlock.Text("prepared")
            ]));
        CompletionDispatchIdentity identity =
            CompletionDispatchIdentityFactory.Create(connection, client);
        var runtime = new SessionRuntime(
            client,
            CompletionTarget:
                CompletionTargetIdentityFactory.Create(identity),
            ContextCandidateSource: new EmptyCandidateSource());
        using SessionJournalEngine engine =
            SessionJournalEngine.CreateForTest(
                _root,
                new SessionCreateOptions(
                    connection.ModelId,
                    "system",
                    "candidate-online"),
                runtime,
                new SessionJournalTestHooks(
                    SessionJournalFailpoint
                        .AfterRequestPreparedCommitted));
        await Assert.ThrowsAsync<SessionJournalFailpointException>(() =>
            engine.SendAsync(
                engine.ReadCurrentHead()!.Value,
                "prepared recovery"));
        return engine.ReadCurrentHead()!.Value;
    }

    private string WriteConnections() {
        string path = Path.Combine(_root, "connections.json");
        File.WriteAllText(path, """
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
        return path;
    }

    private static CompletionConnectionConfig CandidateConnection() => new(
        "test",
        "test",
        "test-model",
        "test-v1",
        "https://example.invalid");

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

    private EventAddress ReadLatestSelectedAddressByKind(
        SessionEventKind kind
    ) {
        using SessionJournalEngine engine =
            SessionJournalEngine.OpenReadOnly(_root);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        EventAddress? found = null;
        while (!audit.IsCaptureComplete) {
            SessionSelectedLineageAuditPage page = audit.ReadNextPage(
                SessionSelectedLineageAuditLimits.MaximumPageEventCount
            );
            found ??= page.HeadToOldest.FirstOrDefault(
                entry => entry.Kind == kind
            )?.Address;
        }
        _ = audit.Complete();
        return found ?? throw new InvalidOperationException(
            $"Selected lineage does not contain {kind}."
        );
    }

    private string WriteAdmission(
        string[] permissions,
        string[]? families = null,
        string[]? capabilities = null,
        string[]? carriers = null
    ) {
        string path = Path.Combine(_root, "admission.json");
        RecapGridControlPermission permissionSet =
            RecapGridControlPermission.None;
        foreach (string permission in permissions) {
            permissionSet |= permission switch {
                "create" => RecapGridControlPermission.Create,
                "register-family" => RecapGridControlPermission
                    .RegisterFamily,
                "register-definition" => RecapGridControlPermission
                    .RegisterDefinition,
                "register-recipe" => RecapGridControlPermission
                    .RegisterRecipe,
                "activate" => RecapGridControlPermission.Activate,
                "promote" => RecapGridControlPermission.Promote,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(permissions),
                    permission,
                    "Unknown test admission permission."
                )
            };
        }
        ContextHeaderCarrier[] parsedCarriers = (carriers
                ?? Array.Empty<string>())
            .Select(static token => ContextHeaderCarrierTokens
                .TryParseStorageToken(token, out ContextHeaderCarrier carrier)
                    ? carrier
                    : throw new ArgumentException(
                        "Unknown test admission carrier."
                    ))
            .ToArray();
        var admission = new RecapGridControlAdmission(
            permissionSet,
            (families ?? Array.Empty<string>()).Select(
                static value => new FamilyDefinitionDigest(value)),
            capabilities ?? Array.Empty<string>(),
            parsedCarriers,
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1024
        );
        File.WriteAllBytes(path, admission.ToCanonicalBytes());
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

    private int ReadControlReceiptCount(string refId, TimelineId timelineId) {
        string path = Path.Combine(
            _root,
            "control",
            "recap-grid",
            "v1",
            "refs",
            refId,
            "timelines",
            timelineId.Value,
            "control.json"
        );
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(path)
        );
        return document.RootElement
            .GetProperty("operationReceipts")
            .GetArrayLength();
    }

    private static int Run(params string[] args) => Program.MainCore(
        ProgramArguments(args),
        ThrowingCompletionClientFactory.Instance
    );

    private static int RunWithFactory(
        ICompletionClientFactory factory,
        params string[] args
    ) => Program.MainCore(
        ProgramArguments(args),
        factory
    );

    private static string[] ProgramArguments(string[] args)
        => args.Length > 0
            && string.Equals(
                args[0],
                "run-online-turn",
                StringComparison.Ordinal
            )
            ? args
            : ["recap-grid", .. args];

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
        TextWriter originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try {
            Console.SetOut(output);
            Console.SetError(error);
            int exitCode = RunWithFactory(factory, args);
            string[] lines = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries
            );
            Assert.True(lines.Length > 0,
                $"Candidate command produced no JSON. stderr={error}");
            string json = lines[^1];
            using JsonDocument document = JsonDocument.Parse(json);
            return (exitCode, document.RootElement.Clone());
        }
        finally {
            Console.SetOut(original);
            Console.SetError(originalError);
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
        internal int RequestCount { get; private set; }
        internal int RecapRequestCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal int PromoteToolCallCount { get; private set; }
        internal string? EmitAgentControlPromoteRecipeOnce { get; set; }
        internal List<CompletionRequest> Requests { get; } = [];

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CallCount++;
            return new DeterministicCompletionClient(
                request => {
                    RequestCount++;
                    if (request.PromptPrefix.OutputContract.Tools.Any(
                            static tool => string.Equals(
                                tool.Name,
                                "submit",
                                StringComparison.Ordinal))) {
                        RecapRequestCount++;
                    }
                    Requests.Add(request);
                },
                () => DisposeCount++,
                CreateAction
            );
        }

        private ActionMessage CreateAction(CompletionRequest request) {
            if (request.PromptPrefix.OutputContract.Tools.Any(static tool =>
                    string.Equals(
                        tool.Name,
                        "submit",
                        StringComparison.Ordinal))) {
                return new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall(
                        "submit",
                        "candidate-call",
                        "{\"outcome\":\"updated\",\"content\":\"candidate result\"}"
                    ))
                ]);
            }
            if (EmitAgentControlPromoteRecipeOnce is { } recipeDigest
                && request.PromptPrefix.OutputContract.Tools.Any(
                    static tool => string.Equals(
                        tool.Name,
                        "recap_grid.control",
                        StringComparison.Ordinal))) {
                EmitAgentControlPromoteRecipeOnce = null;
                PromoteToolCallCount++;
                return new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall(
                        "recap_grid.control",
                        "promote-candidate-call",
                        JsonSerializer.Serialize(new {
                            action = "promote",
                            recipeDigest
                        })
                    ))
                ]);
            }
            return new ActionMessage([
                new ActionBlock.Text("candidate agent answer")
            ]);
        }
    }

    private sealed class DeterministicCompletionClient(
        Action<CompletionRequest> onRequest,
        Action onDispose,
        Func<CompletionRequest, ActionMessage> createAction
    )
        : ICompletionClient, IDisposable {
        private int _disposed;
        public string Name => "candidate-test";
        public string ApiSpecId => "candidate-test-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            onRequest(request);
            ActionMessage action = createAction(request);
            return Task.FromResult(new CompletionResult(
                action,
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)));
        }

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

    private sealed class ControlToolCallClient : ICompletionClient {
        public string Name => "candidate-test";
        public string ApiSpecId => "candidate-test-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new CompletionResult(
            new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                "recap_grid.control",
                "control-call",
                "{\"action\":\"provision-built-in\","
                + "\"builtInAssetId\":\"mystery-investigation-v1\"}"
            ))]),
            new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
        ));
    }

    private sealed class EmptyCandidateSource
        : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(new SessionContextCandidateSelection(
            SessionContextCandidateSelectionStatus.EmptyLineage,
            null));

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "Empty lineage must not materialize a candidate.");
    }
}
