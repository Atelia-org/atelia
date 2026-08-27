using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionExpectedObservationTurnTests : IDisposable {
    private const string Observation = "exact canonical observation";
    private static readonly CompletionDescriptor ImportedInvocation = new(
        "import",
        "legacy-import-v1",
        "model-a"
    );
    private static readonly SessionToolRuntimeIdentity ToolRuntimeIdentity = new(
        "expected-observation-test-host",
        "expected-observation-tools-v1",
        "expected-observation-capabilities-v1"
    );
    private static readonly SessionCreateOptions Options = new(
        "model-a",
        "system-a",
        "surface-a"
    );

    private readonly List<string> _paths = [];

    [Fact]
    public void NotAppendedAndAbandoned_DistinguishKnownOrphanEvidence() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(path, Options);
        EventAddress baseHead = engine.ReadCurrentHead()!.Value;

        var notAppended = Assert.IsType<
            SessionExpectedObservationTurnReadResult.NotAppended
        >(Prove(engine, baseHead, baseHead));
        Assert.Equal(SessionExecutionPhase.Idle, notAppended.Boundary.Phase);

        EventAddress observation = engine.AppendObservation(Observation);
        var invalidBase = Assert.IsType<
            SessionExpectedObservationTurnReadResult.Conflict
        >(Prove(engine, observation, observation));
        Assert.Equal(
            SessionExpectedObservationConflictReason.FreshBaseNotIdle,
            invalidBase.Reason
        );
        Assert.True(engine.MoveCurrentHeadForTest(observation, baseHead));

        var abandoned = Assert.IsType<
            SessionExpectedObservationTurnReadResult.Abandoned
        >(Prove(engine, baseHead, baseHead, observation));
        Assert.Equal(observation, abandoned.Evidence.ObservationAddress);
        Assert.Equal(baseHead, abandoned.Evidence.ObservationParent);
        Assert.Equal(baseHead, abandoned.Evidence.CapturedHead);
        Assert.Equal(SessionExecutionPhase.Idle,
            abandoned.Evidence.Boundary.Phase);

        Assert.IsType<SessionExpectedObservationTurnReadResult.NotAppended>(
            Prove(engine, baseHead, baseHead)
        );
        var mismatch = Assert.IsType<
            SessionExpectedObservationTurnReadResult.Conflict
        >(Prove(
            engine,
            baseHead,
            baseHead,
            observation,
            content: Observation + " changed"
        ));
        Assert.Equal(
            SessionExpectedObservationConflictReason
                .ObservationContentMismatch,
            mismatch.Reason
        );

        Assert.IsType<SessionExpectedObservationTurnReadResult.Corruption>(
            Prove(engine, baseHead, baseHead, baseHead)
        );
    }

    [Theory]
    [InlineData(
        (int)SessionJournalFailpoint.AfterObservationCommitted,
        SessionExecutionPhase.AwaitingAgentAction,
        SessionEventKind.ObservationAccepted)]
    [InlineData(
        (int)SessionJournalFailpoint.AfterRequestPreparedCommitted,
        SessionExecutionPhase.AwaitingCompletionDispatch,
        SessionEventKind.CompletionRequestPrepared)]
    [InlineData(
        (int)SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted,
        SessionExecutionPhase.AwaitingCompletion,
        SessionEventKind.CompletionAttemptStarted)]
    public async Task ObservationPreparedAndStarted_ReturnInProgress(
        int failpointValue,
        SessionExecutionPhase expectedPhase,
        SessionEventKind expectedKind
    ) {
        var failpoint = (SessionJournalFailpoint)failpointValue;
        string path = NewPath();
        var source = new TestContextCandidateSource();
        var client = new QueueCompletionClient();
        client.Enqueue(Success("terminal"));
        using var engine = SessionJournalEngine.CreateForTest(
            path,
            Options,
            Runtime(client, source),
            new SessionJournalTestHooks(failpoint)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            source,
            fixtureId: "expected-observation-" + failpoint
        );
        EventAddress baseHead = engine.ReadCurrentHead()!.Value;

        await Assert.ThrowsAsync<SessionJournalFailpointException>(
            () => engine.SendAsync(Observation, CancellationToken.None)
        );
        EventAddress selectedHead = engine.ReadCurrentHead()!.Value;
        EventAddress observation = FindCurrentObservation(engine);

        var inProgress = Assert.IsType<
            SessionExpectedObservationTurnReadResult.InProgress
        >(Prove(engine, selectedHead, baseHead, observation));
        Assert.Equal(expectedPhase, inProgress.Evidence.Boundary.Phase);
        Assert.Equal(expectedKind, inProgress.Evidence.Boundary.HeadKind);
        Assert.Equal(observation, inProgress.Evidence.ObservationAddress);
        Assert.Equal(baseHead, inProgress.Evidence.ObservationParent);
    }

    [Fact]
    public async Task ToolActionAndToolResult_ReturnSameInProgressObservation() {
        string path = NewPath();
        var source = new TestContextCandidateSource();
        var tools = new ToolRegistry([
            new TextTool("lookup", "tool result")
        ]).CreateSession();
        using var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(path, Options),
            Runtime(new QueueCompletionClient(), source, tools)
        );
        EventAddress baseHead = engine.ReadCurrentHead()!.Value;
        EventAddress observation = engine.AppendObservation(Observation);
        EventAddress action = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("calling"),
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                )
            ]),
            ImportedInvocation
        );

        var atAction = Assert.IsType<
            SessionExpectedObservationTurnReadResult.InProgress
        >(Prove(engine, action, baseHead, observation));
        Assert.Equal(SessionExecutionPhase.AwaitingToolExecution,
            atAction.Evidence.Boundary.Phase);

        var settled = Assert.IsType<
            SessionPendingToolBoundaryResult.Settled
        >(await engine.ExecutePendingToolToBoundaryAsync(
            action,
            tools,
            ToolRuntimeIdentity
        ));
        var atResult = Assert.IsType<
            SessionExpectedObservationTurnReadResult.InProgress
        >(Prove(engine, settled.Head, baseHead, observation));
        Assert.Equal(SessionExecutionPhase.AwaitingAgentAction,
            atResult.Evidence.Boundary.Phase);
        Assert.Equal(SessionEventKind.ToolResultObserved,
            atResult.Evidence.Boundary.HeadKind);
        Assert.Equal(observation, atResult.Evidence.ObservationAddress);
    }

    [Fact]
    public async Task TurnFailed_ReturnsInProgressWithoutTerminalAction() {
        string path = NewPath();
        var source = new TestContextCandidateSource();
        var client = new QueueCompletionClient();
        client.Enqueue(new CompletionResult(
            new ActionMessage([new ActionBlock.Text("partial")]),
            client.Descriptor("model-a"),
            termination: CompletionTermination.Failed(
                "provider-failed",
                "known failure"
            )
        ));
        using var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(path, Options),
            Runtime(client, source)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            source,
            fixtureId: "expected-observation-failed"
        );
        EventAddress baseHead = engine.ReadCurrentHead()!.Value;

        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            () => engine.SendAsync(Observation, CancellationToken.None)
        );
        EventAddress failedHead = engine.ReadCurrentHead()!.Value;
        EventAddress observation = FindCurrentObservation(engine);

        var inProgress = Assert.IsType<
            SessionExpectedObservationTurnReadResult.InProgress
        >(Prove(engine, failedHead, baseHead, observation));
        Assert.Equal(SessionExecutionPhase.TurnFailed,
            inProgress.Evidence.Boundary.Phase);
        Assert.Equal(SessionEventKind.CompletionAttemptFailed,
            inProgress.Evidence.Boundary.HeadKind);
    }

    [Fact]
    public void TerminalAndSetupSuffix_ReturnExactActionAndReadOnlyEvidence() {
        string path = NewPath();
        EventAddress baseHead;
        EventAddress observation;
        EventAddress terminal;
        EventAddress setupHead;
        var action = new ActionMessage([
            new ActionBlock.Text("final-a"),
            new ActionBlock.Text("final-b")
        ]);
        using (var engine = SessionJournalEngine.Create(path, Options)) {
            baseHead = engine.ReadCurrentHead()!.Value;
            observation = engine.AppendObservation(Observation);
            terminal = engine.AppendImportedAgentAction(
                action,
                ImportedInvocation
            );

            AssertTerminal(
                Prove(engine, terminal, baseHead, observation),
                terminal,
                action
            );
            AssertTerminal(
                Prove(engine, terminal, baseHead),
                terminal,
                action
            );
            _ = engine.AppendRuntimeConfigSetup(new(
                "model-b",
                "surface-b",
                SessionJournalDefaults.Schema,
                new SessionDerivedContextConfiguration(0)
            ));
            setupHead = engine.AppendSystemPromptSetup("system-b");
            AssertTerminal(
                Prove(engine, setupHead, baseHead, observation),
                terminal,
                action
            );
        }

        using var readOnly = SessionJournalEngine.OpenReadOnly(path);
        AssertTerminal(
            Prove(readOnly, setupHead, baseHead, observation),
            terminal,
            action
        );
    }

    [Fact]
    public void ConflictAndRetryable_AreClosedAndDoNotAcceptAnotherTurn() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(path, Options);
        EventAddress baseHead = engine.ReadCurrentHead()!.Value;
        EventAddress firstObservation = engine.AppendObservation("first");
        EventAddress firstTerminal = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("first done")]),
            ImportedInvocation
        );
        EventAddress secondObservation = engine.AppendObservation("second");

        var parentConflict = Assert.IsType<
            SessionExpectedObservationTurnReadResult.Conflict
        >(Prove(
            engine,
            secondObservation,
            baseHead,
            secondObservation,
            content: "second"
        ));
        Assert.Equal(
            SessionExpectedObservationConflictReason
                .ObservationParentMismatch,
            parentConflict.Reason
        );

        var retryable = Assert.IsType<
            SessionExpectedObservationTurnReadResult.Retryable
        >(Prove(
            engine,
            firstTerminal,
            baseHead,
            firstObservation,
            content: "first"
        ));
        Assert.Equal(secondObservation, retryable.ObservedSelectedHead);

        var addressConflict = Assert.IsType<
            SessionExpectedObservationTurnReadResult.Conflict
        >(Prove(
            engine,
            secondObservation,
            firstTerminal,
            firstObservation,
            content: "second"
        ));
        Assert.Equal(
            SessionExpectedObservationConflictReason
                .ObservationAddressMismatch,
            addressConflict.Reason
        );

        var contentConflict = Assert.IsType<
            SessionExpectedObservationTurnReadResult.Conflict
        >(Prove(
            engine,
            secondObservation,
            firstTerminal,
            secondObservation,
            content: "not second"
        ));
        Assert.Equal(
            SessionExpectedObservationConflictReason
                .ObservationContentMismatch,
            contentConflict.Reason
        );
    }

    [Fact]
    public void SelectedHeadRace_ReturnsRetryableAfterExactRawProof() {
        string path = NewPath();
        SessionJournalEngine? engine = null;
        EventAddress baseHead = default;
        EventAddress observation = default;
        bool moveOnce = true;
        var hooks = new SessionJournalTestHooks(
            AfterCompletedTurnsBudgetEntered: () => {
                if (moveOnce) {
                    moveOnce = false;
                    Assert.True(engine!.MoveCurrentHeadForTest(
                        observation,
                        baseHead
                    ));
                }
            }
        );
        engine = SessionJournalEngine.CreateForTest(
            path,
            Options,
            Runtime(
                new QueueCompletionClient(),
                new TestContextCandidateSource()
            ),
            hooks
        );
        using (engine) {
            baseHead = engine.ReadCurrentHead()!.Value;
            observation = engine.AppendObservation(Observation);

            var retryable = Assert.IsType<
                SessionExpectedObservationTurnReadResult.Retryable
            >(Prove(engine, observation, baseHead, observation));
            Assert.Equal(observation, retryable.ExpectedSelectedHead);
            Assert.Equal(baseHead, retryable.ObservedSelectedHead);
        }
    }

    [Theory]
    [InlineData(true, SessionCompletedTurnsLimit.MaximumExaminedHeaders)]
    [InlineData(false,
        SessionCompletedTurnsLimit.MaximumDecodedLogicalPayloadBytes)]
    public void InternalBudgetExhaustion_ReturnsTypedLimit(
        bool constrainHeaders,
        SessionCompletedTurnsLimit expectedLimit
    ) {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(path, Options);
        EventAddress baseHead = engine.ReadCurrentHead()!.Value;
        EventAddress observation = engine.AppendObservation(Observation);
        EventAddress terminal = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("done")]),
            ImportedInvocation
        );
        var budget = constrainHeaders
            ? new SessionCompletedTurnsReadBudget(
                maximumHeaderVisits: 1,
                maximumDecodedLogicalPayloadBytes: 1024 * 1024
            )
            : new SessionCompletedTurnsReadBudget(
                maximumHeaderVisits: 4096,
                maximumDecodedLogicalPayloadBytes: 1
            );

        var limited = Assert.IsType<
            SessionExpectedObservationTurnReadResult.LimitExceeded
        >(engine.ProveExpectedObservationTurnAtSelectedHead(
            Request(terminal, baseHead, observation),
            budget
        ));
        Assert.Equal(expectedLimit, limited.Limit);
    }

    [Fact]
    public void UnknownKnownOrphanKind_ReturnsUnsupportedSchema() {
        string path = NewPath();
        EventAddress baseHead;
        using (var engine = SessionJournalEngine.Create(path, Options)) {
            baseHead = engine.ReadCurrentHead()!.Value;
        }
        EventAddress unknown;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            unknown = journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                baseHead,
                "{}"u8.ToArray(),
                opaqueEventKind: 999,
                hint: default
            ).Unwrap().EventAddress;
            Assert.True(journal.MoveRef(main, unknown, baseHead).Unwrap());
        }

        using var readOnly = SessionJournalEngine.OpenReadOnly(path);
        Assert.IsType<
            SessionExpectedObservationTurnReadResult.UnsupportedSchema
        >(Prove(readOnly, baseHead, baseHead, unknown));
    }

    [Fact]
    public void ProofEvidenceAndClosedOutcomesAreOwnerIssued() {
        Assert.Empty(typeof(SessionExpectedObservationTurnEvidence)
            .GetConstructors());
        Type[] outcomes = [
            typeof(SessionExpectedObservationTurnReadResult.NotAppended),
            typeof(SessionExpectedObservationTurnReadResult.Abandoned),
            typeof(SessionExpectedObservationTurnReadResult.InProgress),
            typeof(SessionExpectedObservationTurnReadResult.Terminal),
            typeof(SessionExpectedObservationTurnReadResult.Conflict),
            typeof(SessionExpectedObservationTurnReadResult.Retryable),
            typeof(SessionExpectedObservationTurnReadResult.LimitExceeded),
            typeof(SessionExpectedObservationTurnReadResult.UnsupportedSchema),
            typeof(SessionExpectedObservationTurnReadResult.Corruption)
        ];
        Assert.All(outcomes, static outcome =>
            Assert.Empty(outcome.GetConstructors()));
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static SessionExpectedObservationTurnReadResult Prove(
        SessionJournalEngine engine,
        EventAddress selectedHead,
        EventAddress baseHead,
        EventAddress? observationAddress = null,
        string content = Observation
    ) => engine.ReadView.ProveExpectedObservationTurnAtSelectedHead(
        Request(selectedHead, baseHead, observationAddress, content)
    );

    private static SessionExpectedObservationTurnRequest Request(
        EventAddress selectedHead,
        EventAddress baseHead,
        EventAddress? observationAddress = null,
        string content = Observation
    ) => new(
        selectedHead,
        baseHead,
        content,
        observationAddress
    );

    private static EventAddress FindCurrentObservation(
        SessionJournalEngine engine
    ) => engine.ReadCurrentLineagePrefix(16).HeadToOldest
        .First(static entry =>
            entry.Kind == SessionEventKind.ObservationAccepted)
        .Address;

    private static void AssertTerminal(
        SessionExpectedObservationTurnReadResult result,
        EventAddress terminalAddress,
        ActionMessage action
    ) {
        var terminal = Assert.IsType<
            SessionExpectedObservationTurnReadResult.Terminal
        >(result);
        Assert.Equal(terminalAddress, terminal.TerminalAction.Address);
        Assert.Equal(action.Blocks, terminal.TerminalAction.Message.Blocks);
        Assert.Equal(SessionExecutionPhase.Idle,
            terminal.Evidence.Boundary.Phase);
    }

    private static CompletionResult Success(string text) {
        var client = new QueueCompletionClient();
        return new CompletionResult(
            new ActionMessage([new ActionBlock.Text(text)]),
            client.Descriptor("model-a")
        );
    }

    private static SessionRuntime Runtime(
        ICompletionClient client,
        TestContextCandidateSource source,
        ToolSession? tools = null
    ) => new(
        client,
        tools,
        new SessionCompletionTargetIdentity(
            "test-connection",
            "test",
            "test-connection-fingerprint-v1",
            "test-request-adapter-v1"
        ),
        ToolRuntimeIdentity: ToolRuntimeIdentity,
        ContextCandidateSource: source
    );

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-expected-observation-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class QueueCompletionClient : ICompletionClient {
        private readonly Queue<CompletionResult> _results = [];

        public string Name => "scripted";
        public string ApiSpecId => "test-api-v1";

        internal CompletionDescriptor Descriptor(string modelId) => new(
            Name,
            ApiSpecId,
            modelId
        );

        internal void Enqueue(CompletionResult result) =>
            _results.Enqueue(result);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            if (_results.Count == 0) {
                throw new InvalidOperationException(
                    "No scripted completion remains."
                );
            }
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class TextTool(string name, string result) : ITool {
        public ToolDefinition Definition { get; } = new(
            name,
            $"Tool {name}.",
            new ToolSchema.Object()
        );

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    result
                )
            );
        }
    }
}
