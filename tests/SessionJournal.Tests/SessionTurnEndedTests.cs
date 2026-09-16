using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionTurnEndedTests : IDisposable {
    private readonly List<string> _paths = [];
    private static readonly SessionCreateOptions Options = new("model-a", "system", "surface");
    private static readonly SessionToolRuntimeIdentity ToolsIdentity = new("test-host", "test-tools", "test-capabilities");

    [Theory]
    [InlineData(SessionTurnEndReason.Stopped)]
    [InlineData(SessionTurnEndReason.Rejected)]
    [InlineData(SessionTurnEndReason.Incomplete)]
    public void EndedObservation_IsDurableTypedClosureAndCanBeUndone(SessionTurnEndReason reason) {
        string path = NewPath();
        EventAddress baseHead;
        EventAddress observation;
        EventAddress closed;
        using (var engine = SessionJournalEngine.Create(path, Options)) {
            baseHead = engine.ReadCurrentHead()!.Value;
            observation = engine.AppendObservation("input");
            closed = Assert.IsType<SessionTurnEndResult.Ended>(engine.EndPendingTurn(observation, reason)).End.Address;
            Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
            Assert.IsType<SessionTurnEndResult.Unavailable>(engine.EndPendingTurn(closed, reason));
            Assert.IsType<SessionTurnEndResult.Retryable>(engine.EndPendingTurn(observation, reason));
        }
        using var reopened = SessionJournalEngine.Open(path);
        var proof = Assert.IsType<SessionExpectedObservationTurnReadResult.Terminated>(
            reopened.ReadView.ProveExpectedObservationTurnAtSelectedHead(new(closed, baseHead, "input", observation)));
        Assert.Equal(reason, proof.End.Reason);
        Assert.Equal(observation, proof.Evidence.ObservationAddress);
        var recent = Assert.IsType<SessionCompletedTurnsReadResult.Snapshot>(reopened.ReadRecentCompletedTurns(1));
        var turn = Assert.Single(recent.Value.Turns);
        Assert.Null(turn.TerminalAction);
        Assert.Equal(closed, Assert.IsType<SessionClosedTurnOutcome.Terminated>(turn.Outcome).End.Address);
        var rewind = Assert.IsType<SessionTurnRetractionResult.Moved>(reopened.RewindLatestCompletedTurn(closed));
        Assert.Equal(baseHead, rewind.NewHead);
        Assert.Equal(reason, rewind.Turn.End!.Reason);
    }

    [Fact]
    public async Task ToolBatch_MustFinishBeforeEnd_AndToolFactsRemainSelected() {
        string path = NewPath();
        var tool = new CountingTool();
        var tools = new ToolRegistry([tool]).CreateSession();
        using var engine = SessionJournalEngine.Create(path, Options);
        engine.UseRuntime(new(new ScriptClient(), tools, ToolRuntimeIdentity: ToolsIdentity));
        EventAddress observation = engine.AppendObservation("use two tools");
        EventAddress action = engine.AppendImportedAgentAction(new ActionMessage([
            new ActionBlock.ToolCall(new RawToolCall("count", "one", "{}")),
            new ActionBlock.ToolCall(new RawToolCall("count", "two", "{}"))
        ]), new("import", "legacy-import-v1", "model-a"));
        Assert.IsType<SessionTurnEndResult.Unavailable>(engine.EndPendingTurn(action, SessionTurnEndReason.Stopped));
        var first = Assert.IsType<SessionPendingToolBoundaryResult.MorePending>(
            await engine.ExecutePendingToolToBoundaryAsync(action, tools, ToolsIdentity));
        Assert.IsType<SessionTurnEndResult.Unavailable>(engine.EndPendingTurn(first.Head, SessionTurnEndReason.Stopped));
        var second = Assert.IsType<SessionPendingToolBoundaryResult.Settled>(
            await engine.ExecutePendingToolToBoundaryAsync(first.Head, tools, ToolsIdentity));
        EventAddress closed = Assert.IsType<SessionTurnEndResult.Ended>(
            engine.EndPendingTurn(second.Head, SessionTurnEndReason.Stopped)).End.Address;
        Assert.Equal(2, tool.Calls);
        var recent = Assert.IsType<SessionCompletedTurnsReadResult.Snapshot>(engine.ReadRecentCompletedTurns(1));
        Assert.Equal(observation, Assert.Single(recent.Value.Turns).ObservationAddress);
        engine.Dispose();
        Assert.Equal(second.Head, ReadParent(path, closed));
    }

    [Fact]
    public async Task FailedPureGeneration_LeavesPrepared_AndResumeCommitsDirectlyOnce() {
        string path = NewPath();
        var client = new ScriptClient { Fail = true };
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.CreateForTest(path, Options, Runtime(client, source), new());
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(path, engine, source, fixtureId: "retry-core");
        await Assert.ThrowsAsync<IOException>(() => engine.SendAsync("input", CancellationToken.None));
        EventAddress prepared = engine.ReadCurrentHead()!.Value;
        Assert.Equal(SessionEventKind.CompletionRequestPrepared, engine.InspectExecutionBoundary().HeadKind);
        client.Fail = false;
        await engine.ResumeAsync(prepared, CancellationToken.None);
        EventAddress action = engine.ReadCurrentHead()!.Value;
        _ = SessionEventCodec.Decode(SessionEventKind.AgentActionProduced, engine.ReadPayloadBytes(action), out int version);
        Assert.Equal(2, version);
        Assert.Equal(2, client.Calls);
        Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
        engine.Dispose();
        Assert.Equal(prepared, ReadParent(path, action));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndCommitFailure_PoisonsWriter_ColdReopenDeterminesAuthority(bool afterCommit) {
        string path = NewPath();
        EventAddress pending;
        using (var engine = SessionJournalEngine.CreateForTest(path, Options, new SessionRuntime(new ScriptClient()),
            new SessionJournalTestHooks(
                BeforeCommit: (kind, _) => { if (!afterCommit && kind == SessionEventKind.TurnEnded) { throw new IOException("before"); } },
                AfterCommitBeforeReturn: (kind, _) => { if (afterCommit && kind == SessionEventKind.TurnEnded) { throw new IOException("after"); } }))) {
            pending = engine.AppendObservation("input");
            Assert.Throws<IOException>(() => engine.EndPendingTurn(pending, SessionTurnEndReason.Stopped));
            Assert.Throws<SessionJournalReopenRequiredException>(() => engine.ReadCurrentHead());
        }
        using var reopened = SessionJournalEngine.Open(path);
        Assert.Equal(afterCommit ? SessionExecutionPhase.Idle : SessionExecutionPhase.AwaitingAgentAction,
            reopened.InspectExecutionBoundary().Phase);
    }

    [Fact]
    public async Task PreparedBoundaryRecovery_RequiresNoCandidate_AndNeverExecutesTools() {
        string path = NewPath();
        var client = new ScriptClient { Fail = true };
        var source = new TestContextCandidateSource();
        var tool = new CountingTool();
        var tools = new ToolRegistry([tool]).CreateSession();
        SessionRuntime runtime = Runtime(client, source) with { ToolSession = tools, ToolRuntimeIdentity = ToolsIdentity };
        using var engine = SessionJournalEngine.CreateForTest(path, Options, runtime, new());
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(path, engine, source, fixtureId: "frozen-boundary");
        await Assert.ThrowsAsync<IOException>(() => engine.SendAsync("input", CancellationToken.None));
        EventAddress prepared = engine.ReadCurrentHead()!.Value;
        engine.UseRuntime(runtime with { ContextCandidateSource = null, ContextLifecycle = null });
        client.Fail = false;
        client.Message = new ActionMessage([new ActionBlock.ToolCall(new RawToolCall("count", "one", "{}"))]);
        SessionPreparedCompletionBoundaryResult boundary = await engine.ResumePreparedCompletionToBoundaryAsync(prepared);
        Assert.Equal(boundary.ActionAddress, engine.ReadCurrentHead());
        Assert.Single(boundary.Message.ToolCalls);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(2, client.Calls);
        Assert.IsType<SessionRuntimeRecoveryRequirements.ToolContinuationRequired>(engine.InspectRuntimeRecoveryRequirements());
        await Assert.ThrowsAsync<SessionJournalExpectedHeadMismatchException>(
            () => engine.ResumePreparedCompletionToBoundaryAsync(prepared));
        Assert.Equal(2, client.Calls);
        var settled = Assert.IsType<SessionPendingToolBoundaryResult.Settled>(
            await engine.ExecutePendingToolToBoundaryAsync(boundary.ActionAddress, tools, ToolsIdentity));
        Assert.Equal(1, tool.Calls);
        Assert.IsType<SessionRuntimeRecoveryRequirements.NewRequestRequired>(engine.InspectRuntimeRecoveryRequirements());
        Assert.IsType<SessionTurnEndResult.Ended>(engine.EndPendingTurn(settled.Head, SessionTurnEndReason.Stopped));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActionCommitFailure_ReopenResumesOnlyWhenNotPublished(bool afterCommit) {
        string path = NewPath();
        var client = new ScriptClient();
        var source = new TestContextCandidateSource();
        SessionRuntime runtime = Runtime(client, source);
        using (var engine = SessionJournalEngine.CreateForTest(path, Options, runtime,
            new SessionJournalTestHooks(
                BeforeCommit: (kind, _) => { if (!afterCommit && kind == SessionEventKind.AgentActionProduced) { throw new IOException("before"); } },
                AfterCommitBeforeReturn: (kind, _) => { if (afterCommit && kind == SessionEventKind.AgentActionProduced) { throw new IOException("after"); } }))) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(path, engine, source, fixtureId: "action-publish");
            await Assert.ThrowsAsync<IOException>(() => engine.SendAsync("input", CancellationToken.None));
            Assert.Throws<SessionJournalReopenRequiredException>(() => engine.ReadCurrentHead());
        }
        using var reopened = SessionJournalEngine.OpenForTest(path, runtime, new());
        Assert.Equal(afterCommit ? SessionExecutionPhase.Idle : SessionExecutionPhase.AwaitingCompletion,
            reopened.InspectExecutionBoundary().Phase);
        await reopened.ResumeAsync(reopened.ReadCurrentHead()!.Value, CancellationToken.None);
        Assert.Equal(afterCommit ? 1 : 2, client.Calls);
    }

    [Fact]
    public void UndoRefPublishedThenThrows_PoisonsWriterAndReopensAtRewoundHead() {
        string path = NewPath();
        EventAddress baseHead;
        using (var engine = SessionJournalEngine.CreateForTest(path, Options, new SessionRuntime(new ScriptClient()),
            new SessionJournalTestHooks(AfterTurnRefMoveBeforeReturn: () => throw new IOException("published rewind")))) {
            baseHead = engine.ReadCurrentHead()!.Value;
            var observation = engine.AppendObservation("input");
            var closed = Assert.IsType<SessionTurnEndResult.Ended>(engine.EndPendingTurn(observation, SessionTurnEndReason.Stopped));
            Assert.Throws<IOException>(() => engine.RewindLatestCompletedTurn(closed.End.Address));
            Assert.Throws<SessionJournalReopenRequiredException>(() => engine.ReadCurrentHead());
        }
        using var reopened = SessionJournalEngine.Open(path);
        Assert.Equal(baseHead, reopened.ReadCurrentHead());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgedPreparedInsideToolBatch_CannotBeEnded_EvenThroughLegacyFailed(bool legacyFailed) {
        string path = NewPath();
        EventAddress firstResult;
        EventAddress observation;
        using (var engine = SessionJournalEngine.Create(path, Options)) {
            var tools = new ToolRegistry([new CountingTool()]).CreateSession();
            engine.UseRuntime(new(new ScriptClient(), tools, ToolRuntimeIdentity: ToolsIdentity));
            observation = engine.AppendObservation("input");
            var action = engine.AppendImportedAgentAction(new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("count", "one", "{}")),
                new ActionBlock.ToolCall(new RawToolCall("count", "two", "{}"))
            ]), new("import", "legacy-import-v1", "model-a"));
            firstResult = Assert.IsType<SessionPendingToolBoundaryResult.MorePending>(
                await engine.ExecutePendingToolToBoundaryAsync(action, tools, ToolsIdentity)).Head;
        }
        EventAddress forged;
        using (var raw = EventJournal.EventJournal.OpenExisting(path)) {
            RefId branch = raw.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            var prepared = PreparedFixture.Create(SessionOperationalSemantics.BuildObservationCorrelationId(observation),
                "tool-continuation", observation, observation, observation, observation, "model-a", [], null, checkpoint: 1);
            forged = raw.CommitToRef(branch, firstResult, SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, prepared),
                opaqueEventKind: (uint)SessionEventKind.CompletionRequestPrepared, hint: default).Unwrap().EventAddress;
            if (legacyFailed) {
                forged = raw.CommitToRef(branch, forged, SessionEventCodec.Encode(SessionEventKind.CompletionAttemptStarted, new CompletionAttemptStartedBody()),
                    opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted, hint: default).Unwrap().EventAddress;
                forged = raw.CommitToRef(branch, forged, SessionEventCodec.Encode(SessionEventKind.CompletionAttemptFailed,
                    new CompletionAttemptFailedBody(CompletionTerminationKind.Failed, "failed", null, [])),
                    opaqueEventKind: (uint)SessionEventKind.CompletionAttemptFailed, hint: default).Unwrap().EventAddress;
            }
        }
        using var reopened = SessionJournalEngine.Open(path);
        Assert.Throws<InvalidDataException>(() => reopened.EndPendingTurn(forged, SessionTurnEndReason.Stopped));
        Assert.Equal(forged, reopened.ReadCurrentHead());
    }

    private static SessionRuntime Runtime(ICompletionClient client, TestContextCandidateSource source) => new(
        client, CompletionTarget: new("test", "test", "fingerprint"), ContextCandidateSource: source);
    private static EventAddress? ReadParent(string path, EventAddress address) {
        using var journal = EventJournal.EventJournal.OpenReadOnlyExisting(path);
        using EventFrame frame = journal.ReadEvent(address).Unwrap();
        return frame.Header.Parent;
    }
    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), "session-turn-ended", Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }
    public void Dispose() {
        foreach (string path in _paths) { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }
    private sealed class ScriptClient : ICompletionClient {
        public string Name => "script";
        public string ApiSpecId => "script-v1";
        public bool Fail { get; set; }
        public ActionMessage Message { get; set; } = new([new ActionBlock.Text("done")]);
        public int Calls { get; private set; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Calls++;
            if (Fail) { throw new IOException("transport"); }
            return Task.FromResult(new CompletionResult(Message, new(Name, ApiSpecId, "model-a")));
        }
    }
    private sealed class CountingTool : ITool {
        public int Calls { get; private set; }
        public ToolDefinition Definition { get; } = new("count", "count", new ToolSchema.Object());
        public ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
            Calls++;
            return ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Success, "done"));
        }
    }
}
