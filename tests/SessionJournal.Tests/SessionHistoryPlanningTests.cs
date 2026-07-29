using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionHistoryPlanningTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void GenesisWindow_ExposesDependencyClosedReplaySafeUnits() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        );
        EventAddress firstObservation = engine.AppendObservation("one");
        EventAddress firstAction = engine.AppendImportedAgentAction(
            TextAction("answer-one"),
            Descriptor
        );
        EventAddress secondObservation = engine.AppendObservation("two");
        EventAddress secondAction = engine.AppendImportedAgentAction(
            TextAction("answer-two"),
            Descriptor
        );

        SessionHistoryPlanningWindow window =
            engine.ReadHistoryPlanningWindow();

        Assert.Equal(secondAction, window.ObservedRawHead);
        Assert.Equal(
            [
                firstObservation,
                firstAction,
                secondObservation,
                secondAction
            ],
            window.Units.Select(static unit => unit.SourceEndInclusive)
        );
        Assert.Equal(
            [
                firstObservation,
                firstAction,
                secondObservation,
                secondAction
            ],
            window.ReplaySafeBoundaries
                .Where(static boundary =>
                    boundary.CompletedUnitCount > 0
                )
                .Select(static boundary => boundary.Address)
        );
        Assert.Equal(4, window.Diagnostics.DecodedEventCount);
        Assert.True(window.Diagnostics.PayloadReads >= 5);
        Assert.True(window.Diagnostics.DecodedPayloadBytes > 0);
    }

    [Fact]
    public void IncrementalWindow_DoesNotDecodeColdPrefix() {
        SessionHistoryPlanningDiagnostics small =
            MeasureIncrementalWindow(
                coldTurnCount: 1,
                useDurableSeed: false
            ).Window;
        SessionHistoryPlanningDiagnostics large =
            MeasureIncrementalWindow(
                coldTurnCount: 10_001,
                useDurableSeed: false
            ).Window;

        Assert.Equal(small.PayloadReads, large.PayloadReads);
        Assert.Equal(
            small.DecodedPayloadBytes,
            large.DecodedPayloadBytes
        );
        Assert.Equal(small.DecodedEventCount, large.DecodedEventCount);
        Assert.True(
            large.HeaderVisits > small.HeaderVisits,
            "The raw setup proof remains header-only across the cold prefix."
        );
    }

    [Fact]
    public void DurableSetupSeed_KeepsTenThousandTurnColdPrefixOutOfReads() {
        SessionJournalReadDiagnostics small =
            MeasureIncrementalWindow(
                coldTurnCount: 1,
                useDurableSeed: true
            ).Total;
        SessionJournalReadDiagnostics large =
            MeasureIncrementalWindow(
                coldTurnCount: 10_001,
                useDurableSeed: true
            ).Total;

        Assert.Equal(small.HeaderPreviewReadCount, large.HeaderPreviewReadCount);
        Assert.Equal(small.PayloadReadCount, large.PayloadReadCount);
        Assert.Equal(
            small.LogicalPayloadByteCount,
            large.LogicalPayloadByteCount
        );
    }

    [Fact]
    public void CurrentLineageSnapshot_IsHeaderOnlyAndHeadToRoot() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress observation =
            engine.AppendObservation("snapshot-observation");
        EventAddress head = engine.AppendImportedAgentAction(
            TextAction("snapshot-answer"),
            Descriptor
        );
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        SessionCurrentLineageSnapshot snapshot =
            engine.ReadCurrentLineageHeaders();

        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();
        Assert.Equal(head, snapshot.CapturedHead);
        Assert.Equal(head, snapshot.HeadToRoot[0].Address);
        Assert.Equal(
            observation,
            snapshot.HeadToRoot[0].Parent
        );
        Assert.Equal(
            snapshot.HeadToRoot.Count,
            snapshot.Diagnostics.HeaderVisits
        );
        Assert.Equal(0, snapshot.Diagnostics.PayloadReads);
        Assert.Equal(0, snapshot.Diagnostics.DecodedPayloadBytes);
        Assert.Equal(
            before.PayloadReadCount,
            after.PayloadReadCount
        );
        for (int index = 0;
             index < snapshot.HeadToRoot.Count - 1;
             index++) {
            Assert.Equal(
                snapshot.HeadToRoot[index + 1].Address,
                snapshot.HeadToRoot[index].Parent
            );
        }
        Assert.Null(snapshot.HeadToRoot[^1].Parent);
    }

    [Fact]
    public void BatchSetupSeed_RemainsLazyAboutExecutionRecovery() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress created =
            engine.ResolveExecutionTail().Head!.Value;
        SessionHistoryPlanningSeedBatch batch =
            engine.ReadHistoryPlanningSeeds([created]);

        SessionHistoryPlanningSeed seed =
            Assert.Single(batch.Seeds);
        Assert.Equal(created, seed.Address);
        Assert.Null(seed.ExecutionRecovery);
        Assert.Equal(2, batch.Diagnostics.PayloadReads);
    }

    private (
        SessionHistoryPlanningDiagnostics Window,
        SessionJournalReadDiagnostics Total
    ) MeasureIncrementalWindow(
        int coldTurnCount,
        bool useDurableSeed
    ) {
        Assert.True(coldTurnCount >= 1);
        string path = NewPath();
        EventAddress start;
        EventAddress head;
        SessionContextAnchorSetupReferences setups;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            RefId main = journal.CreateBranch(
                SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
            var runtimeBody = new SessionRuntimeConfiguration(
                    "model-A",
                    "surface-A",
                    SessionJournalDefaults.Schema,
                    new(0)
                );
            byte[] runtimePayload = SessionEventCodec.Encode(
                SessionEventKind.RuntimeConfigSetup,
                runtimeBody
            );
            EventAddress runtime = journal.AppendEventFrame(
                parent: null,
                runtimePayload,
                opaqueEventKind:
                    (uint)SessionEventKind.RuntimeConfigSetup,
                hint: default
            ).Unwrap();
            var promptBody = new SystemPromptSetupBody("system-A");
            byte[] promptPayload = SessionEventCodec.Encode(
                SessionEventKind.SystemPromptSetup,
                promptBody
            );
            EventAddress prompt = journal.AppendEventFrame(
                runtime,
                promptPayload,
                opaqueEventKind:
                    (uint)SessionEventKind.SystemPromptSetup,
                hint: default
            ).Unwrap();
            setups = new SessionContextAnchorSetupReferences(
                new SessionContextSetupReference(
                    runtime,
                    SessionEventCodec.GetExpectedBodySchemaVersion(
                        SessionEventKind.RuntimeConfigSetup
                    ),
                    SessionRequestCanonicalizer.Sha256Hex(
                        runtimePayload
                    )
                ),
                new SessionContextSetupReference(
                    prompt,
                    SessionEventCodec.GetExpectedBodySchemaVersion(
                        SessionEventKind.SystemPromptSetup
                    ),
                    SessionRequestCanonicalizer.Sha256Hex(
                        promptPayload
                    )
                )
            );
            EventAddress cursor = AppendRaw(
                journal,
                prompt,
                SessionEventKind.SessionCreated,
                new SessionCreatedBody(SessionCreationOrigin.Native)
            );
            start = cursor;
            for (int i = 0; i < coldTurnCount; i++) {
                EventAddress coldObservation = AppendRaw(
                    journal,
                    cursor,
                    SessionEventKind.ObservationAccepted,
                    new ObservationAcceptedBody("cold-observation")
                );
                cursor = AppendRaw(
                    journal,
                    coldObservation,
                    SessionEventKind.ImportedAgentAction,
                    ImportedAction(
                        coldObservation,
                        "cold-answer"
                    )
                );
                start = cursor;
            }
            EventAddress recentOne = AppendRaw(
                journal,
                cursor,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("recent-one")
            );
            cursor = AppendRaw(
                journal,
                recentOne,
                SessionEventKind.ImportedAgentAction,
                ImportedAction(recentOne, "recent-answer-one")
            );
            EventAddress recentTwo = AppendRaw(
                journal,
                cursor,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("recent-two")
            );
            head = AppendRaw(
                journal,
                recentTwo,
                SessionEventKind.ImportedAgentAction,
                ImportedAction(recentTwo, "recent-answer-two")
            );
            Assert.True(journal.MoveRef(main, null, head).Unwrap());
        }

        using var engine = SessionJournalEngine.Open(path);
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        SessionHistoryPlanningWindow window;
        if (useDurableSeed) {
            SessionHistoryPlanningSeed seed =
                engine.CreateHistoryPlanningSeed(start, setups);
            window = engine.ReadHistoryPlanningWindowAt(head, seed);
        }
        else {
            window = engine.ReadHistoryPlanningWindow(start);
        }
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();

        Assert.Equal(head, window.ObservedRawHead);
        Assert.Equal(4, window.Units.Count);
        Assert.Equal(4, window.Diagnostics.DecodedEventCount);
        Assert.Equal(4, window.ReplaySafeBoundaries.Count);
        if (!useDurableSeed) {
            Assert.Equal(
                after.HeaderPreviewReadCount
                    - before.HeaderPreviewReadCount,
                window.Diagnostics.HeaderVisits
            );
            Assert.Equal(
                after.PayloadReadCount - before.PayloadReadCount,
                window.Diagnostics.PayloadReads
            );
            Assert.Equal(
                after.LogicalPayloadByteCount
                    - before.LogicalPayloadByteCount,
                window.Diagnostics.DecodedPayloadBytes
            );
        }
        return (window.Diagnostics, after - before);
    }

    [Fact]
    public void MultiToolWindow_AllowsOnlyDependencyClosedFinalResultBoundary() {
        string path = NewPath();
        EventAddress firstResult;
        EventAddress finalResult;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            journal.CreateBranch(
                SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
            EventAddress runtime = Commit(
                journal,
                expectedParent: null,
                SessionEventKind.RuntimeConfigSetup,
                new SessionRuntimeConfiguration(
                    "model-A",
                    "surface-A",
                    SessionJournalDefaults.Schema,
                    new(0)
                )
            );
            EventAddress prompt = Commit(
                journal,
                runtime,
                SessionEventKind.SystemPromptSetup,
                new SystemPromptSetupBody("system-A")
            );
            EventAddress created = Commit(
                journal,
                prompt,
                SessionEventKind.SessionCreated,
                new SessionCreatedBody(SessionCreationOrigin.Native)
            );
            EventAddress observation = Commit(
                journal,
                created,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("use two tools")
            );
            string correlation =
                $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}";
            var identity = new SessionToolRuntimeIdentity(
                "host",
                "implementations",
                "capabilities"
            );
            var action = new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                ),
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-2", "{}")
                )
            ]);
            EventAddress imported = Commit(
                journal,
                observation,
                SessionEventKind.ImportedAgentAction,
                new AgentActionProducedBody(
                    action,
                    Descriptor,
                    correlation,
                    new SessionExecutionCheckpoint(0),
                    identity
                )
            );
            EventAddress firstStarted = Commit(
                journal,
                imported,
                SessionEventKind.ToolExecutionStarted,
                new ToolExecutionStartedBody(
                    "call-1",
                    "lookup",
                    "{}",
                    "operation-1",
                    1,
                    identity
                )
            );
            firstResult = Commit(
                journal,
                firstStarted,
                SessionEventKind.ToolResultObserved,
                new ToolResultObservedBody(
                    "call-1",
                    "lookup",
                    1,
                    ToolExecutionStatus.Success,
                    [new ToolResultBlock.Text("one")]
                )
            );
            EventAddress secondStarted = Commit(
                journal,
                firstResult,
                SessionEventKind.ToolExecutionStarted,
                new ToolExecutionStartedBody(
                    "call-2",
                    "lookup",
                    "{}",
                    "operation-2",
                    2,
                    identity
                )
            );
            finalResult = Commit(
                journal,
                secondStarted,
                SessionEventKind.ToolResultObserved,
                new ToolResultObservedBody(
                    "call-2",
                    "lookup",
                    2,
                    ToolExecutionStatus.Success,
                    [new ToolResultBlock.Text("two")]
                )
            );
        }

        using var engine = SessionJournalEngine.Open(path);
        SessionHistoryPlanningWindow window =
            engine.ReadHistoryPlanningWindow();

        Assert.DoesNotContain(
            window.ReplaySafeBoundaries,
            boundary => boundary.Address == firstResult
        );
        Assert.Contains(
            window.ReplaySafeBoundaries,
            boundary => boundary.Address == finalResult
                && boundary.CompletedUnitCount == 3
        );
        Assert.Throws<InvalidDataException>(
            () => engine.ReadHistoryPlanningWindow(firstResult)
        );
        SessionHistoryPlanningWindow empty =
            engine.ReadHistoryPlanningWindow(finalResult);
        Assert.Empty(empty.Units);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup.
            }
        }
    }

    private string NewPath() {
        // This suite measures read amplification, not storage flush latency. On Linux use
        // tmpfs so the 10k+ real EventFrame fixture does not turn every durable append into
        // an unrelated disk-fsync benchmark.
        string temporaryRoot = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            temporaryRoot,
            "atelia-session-history-planning-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static ActionMessage TextAction(string text) =>
        new([new ActionBlock.Text(text)]);

    private static CompletionDescriptor Descriptor { get; } =
        new("import", "import-v1", "model-A");

    private static AgentActionProducedBody ImportedAction(
        EventAddress observation,
        string text
    ) => new(
        TextAction(text),
        Descriptor,
        $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}",
        new SessionExecutionCheckpoint(0),
        ToolRuntimeIdentity: null
    );

    private static EventAddress AppendRaw(
        EventJournal.EventJournal journal,
        EventAddress? parent,
        SessionEventKind kind,
        object body
    ) => journal.AppendEventFrame(
        parent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap();

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress? expectedParent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        expectedParent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;
}
