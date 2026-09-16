using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Offline.Tests;

public sealed class SessionJournalOfflineForwardFoldTests {
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    public void ActionSchemaControlsParentContract(int version, bool started, bool valid) {
        var fixture = new Fixture();
        fixture.Prepare();
        if (started) { fixture.Start(); }
        if (!valid) {
            Assert.Throws<InvalidDataException>(() => fixture.Action(version));
            return;
        }
        fixture.Action(version);
        var result = fixture.Fold.Complete();
        Assert.Equal(SessionExecutionPhase.Idle, result.ExecutionState.Phase);
    }

    [Fact]
    public void PreparedUsesUnifiedCompletionPhase() {
        var fixture = new Fixture();
        fixture.Prepare();
        Assert.Equal(SessionExecutionPhase.AwaitingCompletion,
            fixture.Fold.Complete().ExecutionState.Phase);
    }

    [Theory]
    [InlineData("observation")]
    [InlineData("prepared")]
    [InlineData("started")]
    [InlineData("failed")]
    public void TurnEndedClosesSafeFrontierAndRetainsHistory(string frontier) {
        var fixture = new Fixture();
        if (frontier != "observation") { fixture.Prepare(); }
        if (frontier is "started" or "failed") { fixture.Start(); }
        if (frontier == "failed") {
            fixture.Add(SessionEventKind.CompletionAttemptFailed,
                new SessionJournalAuditCompletionAttemptFailedFact(CompletionTerminationKind.Failed));
        }
        fixture.End();
        var result = fixture.Fold.Complete();
        Assert.Equal(SessionExecutionPhase.Idle, result.ExecutionState.Phase);
        Assert.Equal(2, result.HistoryContributionCount);
        Assert.Equal(0, result.AgentActionCount);
        Assert.Equal(SessionHistorySemanticCommitment.ComputeSequenceSha256([
            Fixture.Hash,
            SessionHistorySemanticCommitment.ComputeTurnEndContributionSha256(SessionTurnEndReason.Stopped)
        ]), result.HistorySemanticCommitmentSha256);
    }

    [Fact]
    public void TurnEndedAllowsFollowingObservation() {
        var fixture = new Fixture();
        fixture.End();
        fixture.Add(SessionEventKind.ObservationAccepted,
            new SessionJournalAuditObservationFact(Fixture.Hash));
        Assert.Equal(SessionExecutionPhase.AwaitingAgentAction,
            fixture.Fold.Complete().ExecutionState.Phase);
    }

    [Fact]
    public void TurnEndedRejectsPendingToolBatch() {
        var fixture = new Fixture();
        fixture.Add(SessionEventKind.ImportedAgentAction,
            new SessionJournalAuditActionFact(
                [new RawToolCall("tool", "call-1", "{}")], fixture.Correlation, 0,
                new SessionToolRuntimeIdentity("host", "impl", "caps"), Fixture.Hash));
        Assert.Throws<InvalidDataException>(fixture.End);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TurnEndedRequiresEntireToolBatchAndPreservesSequence(bool secondTool) {
        var fixture = new Fixture();
        var identity = new SessionToolRuntimeIdentity("host", "impl", "caps");
        RawToolCall[] calls = secondTool
            ? [new("tool", "call-1", "{}"), new("tool", "call-2", "{}")]
            : [new("tool", "call-1", "{}")];
        fixture.Add(SessionEventKind.ImportedAgentAction,
            new SessionJournalAuditActionFact(calls, fixture.Correlation, 0, identity, Fixture.Hash));
        fixture.Add(SessionEventKind.ToolExecutionStarted,
            new SessionJournalAuditToolExecutionStartedFact("call-1", "tool", "{}", "operation", 1, identity));
        fixture.Add(SessionEventKind.ToolResultObserved,
            new SessionJournalAuditToolResultObservedFact("call-1", "tool", 1,
                ToolExecutionStatus.Success, Fixture.Hash));
        if (secondTool) {
            Assert.Throws<InvalidDataException>(fixture.End);
            return;
        }
        fixture.End();
        var result = fixture.Fold.Complete();
        Assert.Equal(SessionExecutionPhase.Idle, result.ExecutionState.Phase);
        Assert.Equal(1, result.ExecutionState.ToolExecutionSequenceCheckpoint);
        Assert.Equal(4, result.HistoryContributionCount);
        Assert.Equal(1, result.ToolResultHistoryCount);
    }

    [Fact]
    public void NewActionCannotFollowLegacyFailure() {
        var fixture = new Fixture();
        fixture.Prepare();
        fixture.Start();
        fixture.Add(SessionEventKind.CompletionAttemptFailed,
            new SessionJournalAuditCompletionAttemptFailedFact(CompletionTerminationKind.Failed));
        Assert.Throws<InvalidDataException>(() => fixture.Action(2));
    }

    [Theory]
    [InlineData(SessionTurnEndReason.Rejected)]
    [InlineData(SessionTurnEndReason.Incomplete)]
    public void LegacyFailureCanOnlyBeExplicitlyStopped(SessionTurnEndReason reason) {
        var fixture = new Fixture();
        fixture.Prepare();
        fixture.Start();
        fixture.Add(SessionEventKind.CompletionAttemptFailed,
            new SessionJournalAuditCompletionAttemptFailedFact(CompletionTerminationKind.Failed));
        Assert.Throws<InvalidDataException>(() => fixture.Add(SessionEventKind.TurnEnded,
            new SessionJournalAuditTurnEndedFact(reason)));
    }

    [Fact]
    public void NewActionCannotSkipLatestHistoricalStarted() {
        var fixture = new Fixture();
        fixture.Prepare();
        EventAddress prepared = fixture.Head;
        fixture.Start();
        Assert.Throws<InvalidDataException>(() => fixture.Add(
            SessionEventKind.AgentActionProduced,
            new SessionJournalAuditActionFact([], fixture.Correlation, 0, null, Fixture.Hash),
            version: 2, parentOverride: prepared));
    }

    [Fact]
    public void TurnEndedCannotFollowAlreadyCompletedAction() {
        var fixture = new Fixture();
        fixture.Prepare();
        fixture.Action(2);
        Assert.Throws<InvalidDataException>(fixture.End);
    }

    private sealed class Fixture {
        public static readonly string Hash = new('a', 64);
        public SessionJournalOfflineForwardFold Fold { get; } = new();
        public string Correlation { get; }
        public EventAddress Head => _head!.Value;
        private EventAddress? _head;
        private int _sequence;

        public Fixture() {
            Add(SessionEventKind.RuntimeConfigSetup,
                new SessionJournalAuditRuntimeConfigFact(new SessionRuntimeConfiguration(
                    "model", "surface", SessionJournalDefaults.Schema, new SessionDerivedContextConfiguration(1))));
            Add(SessionEventKind.SystemPromptSetup,
                new SessionJournalAuditSystemPromptFact(SessionInputContent.Text("system")));
            Add(SessionEventKind.SessionCreated,
                new SessionJournalAuditSessionCreatedFact(SessionCreationOrigin.Native));
            Add(SessionEventKind.ObservationAccepted, new SessionJournalAuditObservationFact(Hash));
            Correlation = "atelia.session-journal.turn.v1:" + EventAddressTextCodec.Format(_head!.Value);
        }

        public void Prepare() => Add(SessionEventKind.CompletionRequestPrepared,
            new SessionJournalAuditPreparedFact(Correlation, "observation", 0, null));
        public void Start() => Add(SessionEventKind.CompletionAttemptStarted,
            new SessionJournalAuditCompletionAttemptStartedFact());
        public void Action(int version) => Add(SessionEventKind.AgentActionProduced,
            new SessionJournalAuditActionFact([], Correlation, 0, null, Hash), version);
        public void End() => Add(SessionEventKind.TurnEnded,
            new SessionJournalAuditTurnEndedFact(SessionTurnEndReason.Stopped));
        public void Add(SessionEventKind kind, SessionJournalAuditFact fact, int version = 1,
            EventAddress? parentOverride = null) {
            EventAddress address = EventAddressTextCodec.Parse(
                $"ej1:{++_sequence:x8}000000010000000100000000");
            Fold.Accept(new(address, parentOverride ?? _head, kind, version, 0, Hash, fact));
            _head = address;
        }
    }
}
