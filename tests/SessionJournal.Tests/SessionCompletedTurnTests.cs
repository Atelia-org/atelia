using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using System.Reflection;
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
                Snapshot(engine.ReadRecentCompletedTurns(maximumCount: 1));
            SessionCompletedTurnProjection turn = Assert.Single(
                latest.Turns
            );
            Assert.Equal(secondAction, latest.CapturedHead);
            Assert.Equal("two", turn.ObservationContent);
            Assert.Equal(secondAction, turn.TerminalAction.Address);
            Assert.Equal(structured.Blocks, turn.TerminalAction.Message.Blocks);

            SessionCompletedTurnsSnapshot historical =
                Snapshot(engine.ReadRecentCompletedTurnsAt(firstAction, 10));
            Assert.Equal("one", Assert.Single(historical.Turns)
                .ObservationContent);

            Assert.Empty(Snapshot(engine.ReadRecentCompletedTurns(0)).Turns);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => engine.ReadRecentCompletedTurns(-1)
            );
        }

        using var readOnly = SessionJournalEngine.OpenReadOnly(path);
        Assert.Equal(
            ["two", "one"],
            Snapshot(readOnly.ReadRecentCompletedTurns(10)).Turns
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
            Snapshot(engine.ReadRecentCompletedTurnsAt(
                pendingObservation,
                maximumCount: 10
            )).Turns
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
            Snapshot(engine.ReadRecentCompletedTurnsAt(toolAction, 10)).Turns
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
            Snapshot(engine.ReadRecentCompletedTurnsAt(terminalHead, 10)).Turns
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
        Assert.Empty(Snapshot(engine.ReadRecentCompletedTurns(10)).Turns);

        SessionCompletedTurnProjection retained = Assert.Single(
            Snapshot(engine.ReadRecentCompletedTurnsAt(terminalHead, 10)).Turns
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
            Snapshot(engine.ReadRecentCompletedTurns(10)).Turns
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
            Snapshot(engine.ReadRecentCompletedTurnsAt(failedHead, 10)).Turns
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
            Assert.Single(Snapshot(engine.ReadRecentCompletedTurns(10)).Turns)
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
                Snapshot(engine.ReadRecentCompletedTurnsAt(setup, 10)).Turns
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
        SessionPreparedCompletedTurnRewind prepared;
        using (var setup = Create(path)) {
            _ = setup.AppendObservation("one");
            terminal = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("done")]),
                ImportedInvocation
            );
            prepared = Assert.IsType<
                SessionCompletedTurnRewindPrepareResult.Prepared
            >(setup.PrepareLatestCompletedTurnRewind(terminal)).Value;
        }

        using var readOnly = SessionJournalEngine.OpenReadOnly(path);
        Assert.Single(Snapshot(readOnly.ReadRecentCompletedTurns(10)).Turns);
        InvalidOperationException error = Assert.Throws<
            InvalidOperationException
        >(() => readOnly.RewindLatestCompletedTurn(terminal));
        Assert.Contains("read-only", error.Message, StringComparison.Ordinal);
        InvalidOperationException commitError = Assert.Throws<
            InvalidOperationException
        >(() => readOnly.CommitPreparedCompletedTurnRewind(prepared));
        Assert.Contains(
            "read-only",
            commitError.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(terminal, readOnly.ReadCurrentHead());
    }

    [Fact]
    public async Task BoundedRecent_ExactBudgetsPassAndMaxMinusOneFailsBeforeExtraRead() {
        string path = NewPath();
        using var engine = Create(path);
        for (int index = 1; index <= 7; index++) {
            _ = engine.AppendObservation($"user-{index}");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"assistant-{index}")
                ]),
                ImportedInvocation
            );
        }

        var measured = new SessionCompletedTurnsReadBudget(
            maximumHeaderVisits: 4_096,
            maximumDecodedLogicalPayloadBytes: 16L * 1024 * 1024
        );
        SessionCompletedTurnsSnapshot snapshot = Snapshot(
            engine.ReadRecentCompletedTurns(6, measured)
        );
        Assert.Equal(
            ["user-7", "user-6", "user-5", "user-4", "user-3", "user-2"],
            snapshot.Turns.Select(static turn => turn.ObservationContent)
        );
        Assert.True(measured.HeaderVisits > 1);
        Assert.True(measured.DecodedLogicalPayloadBytes > 1);

        var exact = new SessionCompletedTurnsReadBudget(
            measured.HeaderVisits,
            measured.DecodedLogicalPayloadBytes
        );
        _ = Snapshot(engine.ReadRecentCompletedTurns(6, exact));

        var headerShort = new SessionCompletedTurnsReadBudget(
            measured.HeaderVisits - 1,
            measured.DecodedLogicalPayloadBytes
        );
        var headerLimit = Assert.IsType<
            SessionCompletedTurnsReadResult.LimitExceeded
        >(engine.ReadRecentCompletedTurns(6, headerShort));
        Assert.Equal(
            SessionCompletedTurnsLimit.MaximumExaminedHeaders,
            headerLimit.Limit
        );
        Assert.Equal(
            headerShort.MaximumHeaderVisits,
            headerShort.HeaderVisits
        );

        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        var payloadShort = new SessionCompletedTurnsReadBudget(
            measured.HeaderVisits,
            measured.DecodedLogicalPayloadBytes - 1
        );
        var payloadLimit = Assert.IsType<
            SessionCompletedTurnsReadResult.LimitExceeded
        >(engine.ReadRecentCompletedTurns(6, payloadShort));
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();
        Assert.Equal(
            SessionCompletedTurnsLimit
                .MaximumDecodedLogicalPayloadBytes,
            payloadLimit.Limit
        );
        Assert.True(
            payloadShort.DecodedLogicalPayloadBytes
                <= payloadShort.MaximumDecodedLogicalPayloadBytes
        );
        Assert.True(
            after.LogicalPayloadByteCount
                - before.LogicalPayloadByteCount
            <= payloadShort.MaximumDecodedLogicalPayloadBytes
        );

        engine.Dispose();
        using var firstScopeEntered = new ManualResetEventSlim();
        using var releaseFirstScope = new ManualResetEventSlim();
        int scopeEntries = 0;
        int throwOnEntry = 0;
        var hooks = new SessionJournalTestHooks(
            AfterCompletedTurnsBudgetEntered: () => {
                int entry = Interlocked.Increment(ref scopeEntries);
                if (entry == 1) {
                    firstScopeEntered.Set();
                    releaseFirstScope.Wait();
                }
                if (entry == Volatile.Read(ref throwOnEntry)) {
                    throw new InvalidOperationException(
                        "completed-turn scope fixture"
                    );
                }
            }
        );
        using var concurrent =
            SessionJournalEngine.OpenReadOnlyForTest(path, hooks);
        Task<SessionCompletedTurnsReadResult> limitedTask = Task.Run(
            () => concurrent.ReadRecentCompletedTurns(
                6,
                new SessionCompletedTurnsReadBudget(
                    measured.HeaderVisits - 1,
                    measured.DecodedLogicalPayloadBytes
                )
            )
        );
        Assert.True(firstScopeEntered.Wait(TimeSpan.FromSeconds(10)));
        SessionCompletedTurnsReadResult available;
        try {
            available = await Task.Run(
                () => concurrent.ReadRecentCompletedTurns(
                    6,
                    new SessionCompletedTurnsReadBudget(
                        measured.HeaderVisits,
                        measured.DecodedLogicalPayloadBytes
                    )
                )
            ).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally {
            releaseFirstScope.Set();
        }
        SessionCompletedTurnsReadResult limited =
            await limitedTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<SessionCompletedTurnsReadResult.LimitExceeded>(
            limited
        );
        Assert.IsType<SessionCompletedTurnsReadResult.Snapshot>(
            available
        );

        Volatile.Write(ref throwOnEntry, 3);
        InvalidOperationException fixture = Assert.Throws<
            InvalidOperationException
        >(() => concurrent.ReadRecentCompletedTurns(6));
        Assert.Equal("completed-turn scope fixture", fixture.Message);
        _ = Snapshot(concurrent.ReadRecentCompletedTurns(6));
    }

    [Fact]
    public void BoundedContracts_AreOwnerIssuedAndDoNotExportBudgets() {
        Assert.Empty(
            typeof(SessionPreparedCompletedTurnRewind).GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnsReadResult.Snapshot)
                .GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnsReadResult.LimitExceeded)
                .GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnsReadResult.UnsupportedSchema)
                .GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnsReadResult.Corruption)
                .GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnRewindPrepareResult.Prepared)
                .GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnRewindPrepareResult.Unavailable)
                .GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnRewindPrepareResult.Retryable)
                .GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnRewindPrepareResult.LimitExceeded)
                .GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnRewindPrepareResult.UnsupportedSchema)
                .GetConstructors()
        );
        Assert.Empty(
            typeof(SessionCompletedTurnRewindPrepareResult.Corruption)
                .GetConstructors()
        );
        Assert.DoesNotContain(
            typeof(SessionJournalEngine).Assembly.GetExportedTypes(),
            static type => type.Name.Contains(
                "ReadBudget",
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public void BoundedResultContracts_DoNotExposeRecordSynthesis() {
        Type[] resultTypes = [
            typeof(SessionCompletedTurnsReadResult),
            typeof(SessionCompletedTurnsReadResult.Snapshot),
            typeof(SessionCompletedTurnsReadResult.LimitExceeded),
            typeof(SessionCompletedTurnsReadResult.UnsupportedSchema),
            typeof(SessionCompletedTurnsReadResult.Corruption),
            typeof(SessionCompletedTurnRewindPrepareResult),
            typeof(SessionCompletedTurnRewindPrepareResult.Prepared),
            typeof(SessionCompletedTurnRewindPrepareResult.Unavailable),
            typeof(SessionCompletedTurnRewindPrepareResult.Retryable),
            typeof(SessionCompletedTurnRewindPrepareResult.LimitExceeded),
            typeof(SessionCompletedTurnRewindPrepareResult.UnsupportedSchema),
            typeof(SessionCompletedTurnRewindPrepareResult.Corruption)
        ];

        Assert.Equal(12, resultTypes.Length);
        foreach (Type resultType in resultTypes) {
            AssertPlainClassShape(resultType);
        }
    }

    [Fact]
    public void PreparedRewind_DoesNotMoveBeforeCommitAndCasIsReplaySafe() {
        string path = NewPath();
        using var engine = Create(path);
        EventAddress before = engine.ReadCurrentHead()!.Value;
        _ = engine.AppendObservation("one");
        EventAddress terminal = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("done")]),
            ImportedInvocation
        );

        var prepared = Assert.IsType<
            SessionCompletedTurnRewindPrepareResult.Prepared
        >(engine.PrepareLatestCompletedTurnRewind(terminal)).Value;
        Assert.Equal(terminal, engine.ReadCurrentHead());
        Assert.Equal("one", prepared.ObservationContent);

        var moved = Assert.IsType<SessionTurnRetractionResult.Moved>(
            engine.CommitPreparedCompletedTurnRewind(prepared)
        );
        Assert.Equal(before, moved.NewHead);
        Assert.Equal("one", moved.Turn.ObservationContent);

        var replay = Assert.IsType<SessionTurnRetractionResult.Retryable>(
            engine.CommitPreparedCompletedTurnRewind(prepared)
        );
        Assert.Equal(before, replay.ObservedHead);
        Assert.Equal(before, engine.ReadCurrentHead());
    }

    [Fact]
    public void PreparedRewind_RejectsForeignRepositoryAndBranch() {
        string ownerPath = NewPath();
        string foreignPath = NewPath();
        EventAddress terminal;
        SessionPreparedCompletedTurnRewind prepared;
        using (var owner = Create(ownerPath)) {
            _ = owner.AppendObservation("one");
            terminal = owner.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("done")]),
                ImportedInvocation
            );
            prepared = Assert.IsType<
                SessionCompletedTurnRewindPrepareResult.Prepared
            >(owner.PrepareLatestCompletedTurnRewind(terminal)).Value;
        }

        using (var journal =
               EventJournal.EventJournal.OpenExisting(ownerPath)) {
            _ = journal.CreateBranch("foreign", terminal).Unwrap();
        }
        using (var foreignBranch = SessionJournalEngine.Open(
                   ownerPath,
                   "foreign"
               )) {
            Assert.Throws<ArgumentException>(() =>
                foreignBranch.CommitPreparedCompletedTurnRewind(
                    prepared
                )
            );
            Assert.Equal(terminal, foreignBranch.ReadCurrentHead());
        }

        using (var foreignRepository = Create(foreignPath)) {
            EventAddress foreignHead =
                foreignRepository.ReadCurrentHead()!.Value;
            Assert.Throws<ArgumentException>(() =>
                foreignRepository.CommitPreparedCompletedTurnRewind(
                    prepared
                )
            );
            Assert.Equal(
                foreignHead,
                foreignRepository.ReadCurrentHead()
            );
        }

        using var reopenedOwner = SessionJournalEngine.Open(ownerPath);
        Assert.Equal(terminal, reopenedOwner.ReadCurrentHead());
    }

    [Fact]
    public void BoundedRecent_SessionCreatedStartHasNoOffByOne() {
        string path = NewPath();
        using var engine = Create(path);
        _ = engine.AppendObservation("only");
        EventAddress terminal = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("done")]),
            ImportedInvocation
        );

        SessionCompletedTurnsSnapshot snapshot = Snapshot(
            engine.ReadRecentCompletedTurnsAt(terminal, 6)
        );
        Assert.Equal(terminal, snapshot.CapturedHead);
        Assert.Equal("only", Assert.Single(snapshot.Turns).ObservationContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedRecent_UnknownKindOrFutureBodySchemaIsUnsupported(
        bool futureBodySchema
    ) {
        string path = NewPath();
        EventAddress head;
        using (var setup = Create(path)) {
            head = setup.ReadCurrentHead()!.Value;
        }

        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            _ = journal.CreateBranch("unsupported", head).Unwrap();
            byte[] payload = futureBodySchema
                ? "{\"v\":2147483647,\"body\":{}}"u8.ToArray()
                : "{}"u8.ToArray();
            uint kind = futureBodySchema
                ? (uint)SessionEventKind.ObservationAccepted
                : uint.MaxValue;
            head = journal.CommitToRef(
                "unsupported",
                head,
                payload,
                opaqueEventKind: kind,
                hint: default
            ).Unwrap().EventAddress;
        }

        using var unsupported = SessionJournalEngine.Open(
            path,
            "unsupported"
        );
        Assert.IsType<SessionCompletedTurnsReadResult.UnsupportedSchema>(
            unsupported.ReadRecentCompletedTurnsAt(head, 6)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedRecentAndPreparedRewind_StorageReadFailureIsCorruption(
        bool truncateBeforeHeader
    ) {
        var journalOptions = new EventJournalOptions {
            EventSegmentStoreOptions = new RbfSegmentStoreOptions {
                SegmentSizeThresholdBytes = 4,
                HistoricalReaderPoolCapacity = 0,
                CacheMode = RbfCacheMode.Off
            }
        };
        string path = NewPath();
        EventAddress observation;
        EventAddress terminal;
        using (var setup = SessionJournalEngine.CreateForTest(
                   path,
                   Options,
                   runtime: null,
                   new SessionJournalTestHooks(),
                   journalOptions
               )) {
            observation = setup.AppendObservation("one");
            terminal = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("done")]),
                ImportedInvocation
            );
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(
                   path,
                   journalOptions
               )) {
            _ = journal.AppendEventFrame(
                terminal,
                SessionEventCodec.Encode(
                    SessionEventKind.ObservationAccepted,
                    new ObservationAcceptedBody("unreferenced")
                ),
                (uint)SessionEventKind.ObservationAccepted,
                hint: default
            ).Unwrap();
        }

        int corruptions = 0;
        var hooks = new SessionJournalTestHooks(
            AfterCompletedTurnsBudgetEntered: () => {
                if (Interlocked.Increment(ref corruptions) == 1) {
                    if (truncateBeforeHeader) {
                        TruncateEventSegment(
                            path,
                            observation.SegmentNumber
                        );
                    }
                    else {
                        CorruptFramePayloadByte(path, observation);
                    }
                }
            }
        );
        using var corrupted = SessionJournalEngine.OpenForTest(
            path,
            runtime: null,
            hooks,
            journalOptions
        );
        Assert.Equal(terminal, corrupted.ReadCurrentHead());
        Assert.IsType<SessionCompletedTurnsReadResult.Corruption>(
            corrupted.ReadRecentCompletedTurnsAt(terminal, 6)
        );
        Assert.Equal(terminal, corrupted.ReadCurrentHead());
        Assert.IsType<SessionCompletedTurnRewindPrepareResult.Corruption>(
            corrupted.PrepareLatestCompletedTurnRewind(terminal)
        );
    }

    [Fact]
    public void AbandonFailedTurn_DoesNotExposeInternalBoundaryExceptions() {
        string limitedPath = NewPath();
        using (var limited = Create(limitedPath)) {
            EventAddress pending = limited.AppendObservation("pending");
            InvalidOperationException limit = Assert.Throws<
                InvalidOperationException
            >(() => limited.AbandonFailedTurn(
                pending,
                new SessionCompletedTurnsReadBudget(
                    maximumHeaderVisits: 1,
                    maximumDecodedLogicalPayloadBytes: 16 * 1024 * 1024
                )
            ));
            Assert.Null(limit.InnerException);
        }

        string unsupportedPath = NewPath();
        EventAddress unsupportedHead;
        using (var setup = Create(unsupportedPath)) {
            unsupportedHead = setup.ReadCurrentHead()!.Value;
        }
        using (var journal =
               EventJournal.EventJournal.OpenExisting(unsupportedPath)) {
            unsupportedHead = journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                unsupportedHead,
                "{}"u8.ToArray(),
                opaqueEventKind: uint.MaxValue,
                hint: default
            ).Unwrap().EventAddress;
        }
        using var unsupported = SessionJournalEngine.Open(
            unsupportedPath
        );
        NotSupportedException schema = Assert.Throws<
            NotSupportedException
        >(() => unsupported.AbandonFailedTurn(unsupportedHead));
        Assert.Null(schema.InnerException);
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
        Assert.IsType<SessionCompletedTurnsReadResult.Corruption>(
            malformed.ReadRecentCompletedTurnsAt(toolAction, 10)
        );
    }

    private static SessionCreateOptions Options => new(
        "model-a",
        "prompt-a",
        "surface-a"
    );

    private static SessionCompletedTurnsSnapshot Snapshot(
        SessionCompletedTurnsReadResult result
    ) => Assert.IsType<SessionCompletedTurnsReadResult.Snapshot>(
        result
    ).Value;

    private static void AssertPlainClassShape(Type type) {
        const BindingFlags DeclaredMembers =
            BindingFlags.DeclaredOnly
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic;

        Assert.True(type.IsClass);
        Assert.Null(type.GetMethod("<Clone>$", DeclaredMembers));
        Assert.Null(type.GetProperty("EqualityContract", DeclaredMembers));
        Assert.DoesNotContain(
            type.GetMethods(DeclaredMembers),
            static method => method.Name is
                "op_Equality"
                or "op_Inequality"
                or nameof(object.Equals)
                or nameof(object.GetHashCode)
                or nameof(object.ToString)
        );
        Assert.DoesNotContain(
            type.GetConstructors(DeclaredMembers),
            constructor => {
                ParameterInfo[] parameters = constructor.GetParameters();
                return parameters.Length == 1
                    && parameters[0].ParameterType == type
                    && (
                        constructor.IsFamily
                        || constructor.IsFamilyOrAssembly
                        || constructor.IsFamilyAndAssembly
                    );
            }
        );
    }

    private static SessionJournalEngine Create(string path) =>
        SessionJournalEngine.Create(path, Options);

    private static void CorruptFramePayloadByte(
        string path,
        EventAddress address
    ) {
        string segmentPath = Assert.Single(
            Directory.GetFiles(
                Path.Combine(path, "events"),
                $"{address.SegmentNumber:x8}.rbf",
                SearchOption.AllDirectories
            )
        );
        using var stream = new FileStream(
            segmentPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );
        long payloadOffset = checked(address.Ticket.Offset + 4);
        stream.Position = payloadOffset;
        int value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = payloadOffset;
        stream.WriteByte((byte)(value ^ 0x01));
        stream.Flush(flushToDisk: true);
    }

    private static void TruncateEventSegment(
        string path,
        uint segmentNumber
    ) {
        string segmentPath = Assert.Single(
            Directory.GetFiles(
                Path.Combine(path, "events"),
                $"{segmentNumber:x8}.rbf",
                SearchOption.AllDirectories
            )
        );
        using var stream = new FileStream(
            segmentPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None
        );
        stream.SetLength(4);
        stream.Flush(flushToDisk: true);
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
