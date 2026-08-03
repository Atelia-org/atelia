using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionCompletedTurnTests : IDisposable {
    private static readonly CompletionDescriptor ImportedInvocation = new(
        "import",
        "legacy-import-v1",
        "model-a"
    );

    private static readonly SessionToolRuntimeIdentity ToolRuntimeIdentity = new(
        "test-tool-host",
        "test-tool-implementations-v1",
        "test-tool-capabilities-v1"
    );

    private readonly List<string> _paths = [];

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort test cleanup.
            }
        }
    }

    [Fact]
    public void ReadRecentCompletedTurns_UsesExactHeadNewestFirstAndPreservesBlocks() {
        string path = NewPath();
        EventAddress firstAction;
        EventAddress secondAction;
        var reasoningInvocation = new CompletionDescriptor(
            "reasoning",
            "reasoning-v1",
            "model-a"
        );
        ActionMessage structured = new([
            new ActionBlock.Text("second-a"),
            new ActionBlock.TextReasoningBlock(
                "thought",
                reasoningInvocation,
                "opaque"
            ),
            new ActionBlock.Text("second-b")
        ]);

        using (var engine = Create(path)) {
            _ = engine.AppendObservation("one");
            firstAction = engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("first")]),
                ImportedInvocation
            );
            _ = engine.AppendObservation("two");
            secondAction = engine.AppendImportedAgentAction(
                structured,
                ImportedInvocation
            );

            SessionCompletedTurnsSnapshot latest =
                engine.ReadRecentCompletedTurns(maximumCount: 1);
            SessionCompletedTurnProjection turn = Assert.Single(
                latest.Turns
            );
            Assert.Equal(secondAction, latest.CapturedHead);
            Assert.Equal("two", turn.ObservationContent);
            Assert.Equal(secondAction, turn.TerminalAction.Address);
            Assert.Equal(structured.Blocks, turn.TerminalAction.Message.Blocks);

            SessionCompletedTurnsSnapshot historical =
                engine.ReadRecentCompletedTurnsAt(firstAction, 10);
            Assert.Equal("one", Assert.Single(historical.Turns)
                .ObservationContent);

            Assert.Empty(engine.ReadRecentCompletedTurns(0).Turns);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => engine.ReadRecentCompletedTurns(-1)
            );
        }

        using var readOnly = SessionJournalEngine.OpenReadOnly(path);
        Assert.Equal(
            ["two", "one"],
            readOnly.ReadRecentCompletedTurns(10).Turns
                .Select(static turn => turn.ObservationContent)
                .ToArray()
        );
    }

    [Fact]
    public void ReadRecentCompletedTurns_IncompleteAndSetupTailsKeepEarlierCompletedTurns() {
        string path = NewPath();
        using var engine = Create(path);
        _ = engine.AppendObservation("completed");
        EventAddress completedAction = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("done")]),
            ImportedInvocation
        );
        EventAddress pendingObservation = engine.AppendObservation("pending");

        SessionCompletedTurnProjection projected = Assert.Single(
            engine.ReadRecentCompletedTurnsAt(
                pendingObservation,
                maximumCount: 10
            ).Turns
        );
        Assert.Equal(completedAction, projected.TerminalAction.Address);

        SessionTurnRetractionResult unavailableAtObservation =
            engine.RewindLatestCompletedTurn(pendingObservation);
        var observationBoundary = Assert.IsType<
            SessionTurnRetractionResult.Unavailable
        >(unavailableAtObservation).Boundary;
        Assert.Equal(
            SessionExecutionPhase.AwaitingAgentAction,
            observationBoundary.Phase
        );
        Assert.Equal(pendingObservation, engine.ReadCurrentHead());
    }

    [Fact]
    public void ReadRecentCompletedTurns_ActiveToolTailUsesLastTerminalCut() {
        string path = NewPath();
        var source = new TestContextCandidateSource();
        ToolSession tools = new ToolRegistry([
            new TextTool("lookup", "unused")
        ]).CreateSession();
        using var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(
                path,
                Options
            ),
            Runtime(new QueueCompletionClient(), source, tools)
        );
        _ = engine.AppendObservation("earlier");
        EventAddress earlierTerminal =
            engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("earlier done")]),
                ImportedInvocation
            );
        _ = engine.AppendObservation("active tool turn");
        EventAddress toolAction = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("calling"),
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                )
            ]),
            ImportedInvocation
        );

        SessionCompletedTurnProjection projected = Assert.Single(
            engine.ReadRecentCompletedTurnsAt(toolAction, 10).Turns
        );
        Assert.Equal(earlierTerminal, projected.TerminalAction.Address);
        var unavailable = Assert.IsType<
            SessionTurnRetractionResult.Unavailable
        >(engine.RewindLatestCompletedTurn(toolAction));
        Assert.Equal(
            SessionExecutionPhase.AwaitingToolExecution,
            unavailable.Boundary.Phase
        );
        Assert.Equal(toolAction, engine.ReadCurrentHead());
    }

    [Fact]
    public async Task ToolLoop_ProjectsAndRewindsOneVisibleTerminalTurn() {
        string path = NewPath();
        var source = new TestContextCandidateSource();
        var client = new QueueCompletionClient();
        client.Enqueue(new CompletionResult(
            new ActionMessage([
                new ActionBlock.Text("intermediate"),
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                )
            ]),
            client.Descriptor("model-a")
        ));
        var terminal = new ActionMessage([
            new ActionBlock.Text("final-a"),
            new ActionBlock.Text("final-b")
        ]);
        client.Enqueue(new CompletionResult(
            terminal,
            client.Descriptor("model-a")
        ));
        ToolSession tools = new ToolRegistry([
            new TextTool("lookup", "tool result")
        ]).CreateSession();

        using var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(
                path,
                Options
            ),
            Runtime(client, source, tools)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            source
        );
        EventAddress beforeTurn = engine.ReadCurrentHead()!.Value;

        _ = await engine.SendAsync("use a tool", CancellationToken.None);
        EventAddress terminalHead = engine.ReadCurrentHead()!.Value;
        SessionCompletedTurnProjection projected = Assert.Single(
            engine.ReadRecentCompletedTurnsAt(terminalHead, 10).Turns
        );
        Assert.Equal("use a tool", projected.ObservationContent);
        Assert.Equal(terminal.Blocks, projected.TerminalAction.Message.Blocks);
        Assert.DoesNotContain(
            projected.TerminalAction.Message.Blocks,
            static block => block is ActionBlock.ToolCall
        );

        var moved = Assert.IsType<SessionTurnRetractionResult.Moved>(
            engine.RewindLatestCompletedTurn(terminalHead)
        );
        Assert.Equal(beforeTurn, moved.NewHead);
        Assert.Equal(terminalHead, moved.PreviousHead);
        Assert.Equal("use a tool", moved.Turn.ObservationContent);
        Assert.Equal(terminalHead, moved.Turn.TerminalAction!.Address);
        Assert.Equal(
            terminal.Blocks,
            moved.Turn.TerminalAction.Message.Blocks
        );
        Assert.Empty(engine.ReadRecentCompletedTurns(10).Turns);

        SessionCompletedTurnProjection retained = Assert.Single(
            engine.ReadRecentCompletedTurnsAt(terminalHead, 10).Turns
        );
        Assert.Equal("use a tool", retained.ObservationContent);
    }

    [Fact]
    public async Task MultiRoundToolLoop_EmptyTerminalActionRemainsAuthoritative() {
        string path = NewPath();
        var source = new TestContextCandidateSource();
        var client = new QueueCompletionClient();
        client.Enqueue(new CompletionResult(
            new ActionMessage([
                new ActionBlock.Text("first intermediate"),
                new ActionBlock.ToolCall(
                    new RawToolCall("alpha", "call-a", "{}")
                ),
                new ActionBlock.ToolCall(
                    new RawToolCall("beta", "call-b", "{}")
                )
            ]),
            client.Descriptor("model-a")
        ));
        client.Enqueue(new CompletionResult(
            new ActionMessage([
                new ActionBlock.Text("second intermediate"),
                new ActionBlock.ToolCall(
                    new RawToolCall("alpha", "call-c", "{}")
                )
            ]),
            client.Descriptor("model-a")
        ));
        client.Enqueue(new CompletionResult(
            new ActionMessage([new ActionBlock.Text(string.Empty)]),
            client.Descriptor("model-a")
        ));
        ToolSession tools = new ToolRegistry([
            new TextTool("alpha", "alpha result"),
            new TextTool("beta", "beta result")
        ]).CreateSession();

        using var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(
                path,
                Options
            ),
            Runtime(client, source, tools)
        );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            source
        );
        _ = await engine.SendAsync("multi round", CancellationToken.None);
        EventAddress terminalHead = engine.ReadCurrentHead()!.Value;

        SessionCompletedTurnProjection projected = Assert.Single(
            engine.ReadRecentCompletedTurns(10).Turns
        );
        Assert.Equal(terminalHead, projected.TerminalAction.Address);
        var terminalText = Assert.IsType<ActionBlock.Text>(
            Assert.Single(projected.TerminalAction.Message.Blocks)
        );
        Assert.Equal(string.Empty, terminalText.Content);
    }

    [Fact]
    public async Task ToolContinuationFailure_AbandonsBackToOriginalObservationPredecessor() {
        string path = NewPath();
        var source = new TestContextCandidateSource();
        var client = new QueueCompletionClient();
        client.Enqueue(new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                )
            ]),
            client.Descriptor("model-a")
        ));
        client.Enqueue(new CompletionResult(
            new ActionMessage([new ActionBlock.Text("partial")]),
            client.Descriptor("model-a"),
            termination: CompletionTermination.Failed(
                "provider-failed",
                "known failure"
            )
        ));
        ToolSession tools = new ToolRegistry([
            new TextTool("lookup", "tool result")
        ]).CreateSession();

        using var engine = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Create(
                path,
                Options
            ),
            Runtime(client, source, tools)
        );
        _ = engine.AppendObservation("earlier");
        EventAddress earlierTerminal =
            engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("earlier done")]),
                ImportedInvocation
            );
        await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
            path,
            engine,
            source
        );
        EventAddress beforeTurn = engine.ReadCurrentHead()!.Value;
        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            () => engine.SendAsync("will fail", CancellationToken.None)
        );
        EventAddress failedHead = engine.ReadCurrentHead()!.Value;
        Assert.Equal(
            SessionExecutionPhase.TurnFailed,
            engine.InspectExecutionBoundary().Phase
        );
        SessionCompletedTurnProjection earlier = Assert.Single(
            engine.ReadRecentCompletedTurnsAt(failedHead, 10).Turns
        );
        Assert.Equal(earlierTerminal, earlier.TerminalAction.Address);

        var rewindUnavailable = Assert.IsType<
            SessionTurnRetractionResult.Unavailable
        >(engine.RewindLatestCompletedTurn(failedHead));
        Assert.Equal(
            SessionExecutionPhase.TurnFailed,
            rewindUnavailable.Boundary.Phase
        );

        var abandoned = Assert.IsType<SessionTurnRetractionResult.Moved>(
            engine.AbandonFailedTurn(failedHead)
        );
        Assert.Equal(beforeTurn, abandoned.NewHead);
        Assert.Equal("will fail", abandoned.Turn.ObservationContent);
        Assert.Null(abandoned.Turn.TerminalAction);
        Assert.Equal(
            earlierTerminal,
            Assert.Single(engine.ReadRecentCompletedTurns(10).Turns)
                .TerminalAction.Address
        );
        Assert.Equal(
            SessionExecutionPhase.Idle,
            engine.InspectExecutionBoundary().Phase
        );
    }

    [Fact]
    public void Rewind_RejectsSetupSuffixAndReturnsRetryableForStaleExpectedHead() {
        string path = NewPath();
        using var engine = Create(path);
        _ = engine.AppendObservation("one");
        EventAddress terminal = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("done")]),
            ImportedInvocation
        );
        EventAddress setup = engine.AppendSystemPromptSetup("prompt-b");

        Assert.Equal(
            terminal,
            Assert.Single(
                engine.ReadRecentCompletedTurnsAt(setup, 10).Turns
            ).TerminalAction.Address
        );

        var unavailable = Assert.IsType<
            SessionTurnRetractionResult.Unavailable
        >(engine.RewindLatestCompletedTurn(setup));
        Assert.Equal(SessionExecutionPhase.Idle, unavailable.Boundary.Phase);
        Assert.Equal(
            SessionEventKind.SystemPromptSetup,
            unavailable.Boundary.HeadKind
        );
        Assert.Equal(setup, engine.ReadCurrentHead());

        var retryable = Assert.IsType<
            SessionTurnRetractionResult.Retryable
        >(engine.RewindLatestCompletedTurn(terminal));
        Assert.Equal(terminal, retryable.ExpectedHead);
        Assert.Equal(setup, retryable.ObservedHead);
        Assert.Equal(setup, engine.ReadCurrentHead());
    }

    [Fact]
    public void Rewind_CasRaceReturnsRetryableWithoutRemovingConcurrentHead() {
        string path = NewPath();
        EventAddress terminal;
        using (var setup = Create(path)) {
            _ = setup.AppendObservation("one");
            terminal = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("done")]),
                ImportedInvocation
            );
        }

        SessionJournalEngine? racing = null;
        EventAddress? concurrentObservation = null;
        bool raced = false;
        var hooks = new SessionJournalTestHooks(
            BeforeTurnRefMove: journal => {
                if (raced) {
                    return;
                }
                raced = true;
                EventAddress observed =
                    journal.GetHead(racing!.BranchRefId)!.Value;
                concurrentObservation = journal.CommitToRef(
                    racing.BranchRefId,
                    observed,
                    SessionEventCodec.Encode(
                        SessionEventKind.ObservationAccepted,
                        new ObservationAcceptedBody("concurrent")
                    ),
                    opaqueEventKind:
                        (uint)SessionEventKind.ObservationAccepted,
                    hint: default
                ).Unwrap().EventAddress;
            }
        );
        using (racing = SessionJournalEngine.OpenForTest(
                   path,
                   runtime: null!,
                   hooks
               )) {
            var retryable = Assert.IsType<
                SessionTurnRetractionResult.Retryable
            >(racing.RewindLatestCompletedTurn(terminal));
            Assert.Equal(terminal, retryable.ExpectedHead);
            Assert.Equal(concurrentObservation, retryable.ObservedHead);
            Assert.Equal(concurrentObservation, racing.ReadCurrentHead());
        }
    }

    [Fact]
    public async Task Abandon_CasRaceReturnsRetryableWithoutMovingConcurrentHead() {
        string path = NewPath();
        var source = new TestContextCandidateSource();
        var client = new QueueCompletionClient();
        client.Enqueue(new CompletionResult(
            new ActionMessage([new ActionBlock.Text("partial")]),
            client.Descriptor("model-a"),
            termination: CompletionTermination.Failed("known")
        ));
        SessionJournalEngine? racing = null;
        EventAddress? concurrentHead = null;
        EventAddress? idleHead = null;
        bool raced = false;
        var hooks = new SessionJournalTestHooks(
            BeforeTurnRefMove: journal => {
                if (raced) {
                    return;
                }
                raced = true;
                EventAddress observed =
                    journal.GetHead(racing!.BranchRefId)!.Value;
                Assert.True(journal.MoveRef(
                    racing.BranchRefId,
                    observed,
                    idleHead!.Value
                ).IsSuccess);
                concurrentHead = idleHead;
            }
        );
        using (racing = SessionJournalEngine.CreateForTest(
                   path,
                   Options,
                   Runtime(client, source),
                   hooks
               )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                racing,
                source
            );
            idleHead = racing.ReadCurrentHead()!.Value;
            await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
                () => racing.SendAsync("failed", CancellationToken.None)
            );
            EventAddress failedHead = racing.ReadCurrentHead()!.Value;

            var retryable = Assert.IsType<
                SessionTurnRetractionResult.Retryable
            >(racing.AbandonFailedTurn(failedHead));
            Assert.Equal(failedHead, retryable.ExpectedHead);
            Assert.Equal(concurrentHead, retryable.ObservedHead);
            Assert.Equal(concurrentHead, racing.ReadCurrentHead());
        }
    }

    [Fact]
    public void ReadOnlyEngine_AllowsProjectionButRejectsRetraction() {
        string path = NewPath();
        EventAddress terminal;
        using (var setup = Create(path)) {
            _ = setup.AppendObservation("one");
            terminal = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("done")]),
                ImportedInvocation
            );
        }

        using var readOnly = SessionJournalEngine.OpenReadOnly(path);
        Assert.Single(readOnly.ReadRecentCompletedTurns(10).Turns);
        InvalidOperationException error = Assert.Throws<
            InvalidOperationException
        >(() => readOnly.RewindLatestCompletedTurn(terminal));
        Assert.Contains("read-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveToolTailWithoutSessionCreatedFailsFast() {
        string path = NewPath();
        EventAddress promptAddress;
        using (var setup = Create(path)) {
            promptAddress = setup.ReadCurrentLineageHeaders()
                .HeadToRoot.Single(static node =>
                    node.Kind == SessionEventKind.SystemPromptSetup
                ).Address;
        }

        EventAddress toolAction;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            _ = journal.CreateBranch(
                "malformed",
                promptAddress
            ).Unwrap();
            EventAddress observation = journal.CommitToRef(
                "malformed",
                promptAddress,
                SessionEventCodec.Encode(
                    SessionEventKind.ObservationAccepted,
                    new ObservationAcceptedBody("missing created")
                ),
                opaqueEventKind:
                    (uint)SessionEventKind.ObservationAccepted,
                hint: default
            ).Unwrap().EventAddress;
            var action = new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                )
            ]);
            toolAction = journal.CommitToRef(
                "malformed",
                observation,
                SessionEventCodec.Encode(
                    SessionEventKind.ImportedAgentAction,
                    new AgentActionProducedBody(
                        action,
                        ImportedInvocation,
                        SessionOperationalSemantics
                            .BuildObservationCorrelationId(observation),
                        new SessionExecutionCheckpoint(0),
                        ToolRuntimeIdentity
                    )
                ),
                opaqueEventKind:
                    (uint)SessionEventKind.ImportedAgentAction,
                hint: default
            ).Unwrap().EventAddress;
        }

        using var malformed = SessionJournalEngine.Open(
            path,
            "malformed"
        );
        Assert.Throws<InvalidDataException>(
            () => malformed.ReadRecentCompletedTurnsAt(toolAction, 10)
        );
    }

    private static SessionCreateOptions Options => new(
        "model-a",
        "prompt-a",
        "surface-a"
    );

    private static SessionJournalEngine Create(string path) =>
        SessionJournalEngine.Create(path, Options);

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
            "atelia-session-completed-turn-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class QueueCompletionClient : ICompletionClient {
        private readonly Queue<CompletionResult> _results = [];

        public string Name => "scripted";
        public string ApiSpecId => "test-api-v1";

        public CompletionDescriptor Descriptor(string modelId) => new(
            Name,
            ApiSpecId,
            modelId
        );

        public void Enqueue(CompletionResult result) =>
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
