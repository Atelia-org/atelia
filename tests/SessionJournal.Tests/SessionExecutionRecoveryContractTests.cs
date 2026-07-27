using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionExecutionRecoveryContractTests : IDisposable {
    private static readonly SessionToolRuntimeIdentity ToolRuntimeIdentity = new(
        "test-tool-host",
        "test-tool-implementations-v1",
        "test-tool-capabilities-v1"
    );
    private readonly List<string> _tempDirectories = [];

    [Fact]
    public void FullReducerReferenceOracle_FreezesExecutionStateAcrossHeadPhases() {
        EventAddress runtime = Address(1);
        EventAddress prompt = Address(2);
        EventAddress created = Address(3);
        EventAddress observation = Address(4);
        EventAddress prepared = Address(5);
        EventAddress action = Address(6);
        EventAddress started1 = Address(7);
        EventAddress result1 = Address(8);
        EventAddress started2 = Address(9);
        EventAddress result2 = Address(10);
        EventAddress continuationPrepared = Address(11);
        EventAddress restarted = Address(12);
        EventAddress failed = Address(13);
        EventAddress terminalAction = Address(14);
        EventAddress importedAction = Address(15);
        EventAddress promptAfterIdle = Address(16);
        EventAddress completionStarted = Address(17);
        string correlation = BuildCorrelationId(observation);
        var call1 = new RawToolCall("alpha", "call-1", """{"n":1}""");
        var call2 = new RawToolCall("beta", "call-2", """{"n":2}""");

        DecodedSessionEvent[] bootstrap = [
            Event(
                SessionEventKind.RuntimeConfigSetup,
                new SessionRuntimeConfiguration("model-A", "surface-A", SessionJournalDefaults.Schema),
                runtime,
                null
            ),
            Event(
                SessionEventKind.SystemPromptSetup,
                new SystemPromptSetupBody("system-A"),
                prompt,
                runtime
            ),
            Event(SessionEventKind.SessionCreated, new SessionCreatedBody(), created, prompt)
        ];
        DecodedSessionEvent observationEvent = Event(
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("observe"),
            observation,
            created
        );
        DecodedSessionEvent preparedEvent = Event(
            SessionEventKind.CompletionRequestPrepared,
            PreparedBody(correlation, "observation", runtime, prompt),
            prepared,
            observation
        );
        DecodedSessionEvent actionEvent = Event(
            SessionEventKind.AgentActionProduced,
            new AgentActionProducedBody(
                new ActionMessage([
                    new ActionBlock.ToolCall(call1),
                    new ActionBlock.ToolCall(call2)
                ]),
                Invocation(),
                correlation,
                new SessionExecutionCheckpoint(0),
                ToolRuntimeIdentity
            ),
            action,
            completionStarted
        );
        DecodedSessionEvent completionStartedEvent = Event(
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody(),
            completionStarted,
            prepared
        );
        DecodedSessionEvent started1Event = Event(
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                call1.ToolCallId,
                call1.ToolName,
                call1.RawArgumentsJson,
                "operation-1",
                1,
                ToolRuntimeIdentity
            ),
            started1,
            action
        );
        DecodedSessionEvent result1Event = Event(
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(
                call1.ToolCallId,
                call1.ToolName,
                1,
                ToolExecutionStatus.Success,
                Array.Empty<ToolResultBlock>()
            ),
            result1,
            started1
        );
        DecodedSessionEvent started2Event = Event(
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                call2.ToolCallId,
                call2.ToolName,
                call2.RawArgumentsJson,
                "operation-2",
                2,
                ToolRuntimeIdentity
            ),
            started2,
            result1
        );
        DecodedSessionEvent result2Event = Event(
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(
                call2.ToolCallId,
                call2.ToolName,
                2,
                ToolExecutionStatus.Success,
                Array.Empty<ToolResultBlock>()
            ),
            result2,
            started2
        );
        DecodedSessionEvent continuationPreparedEvent = Event(
            SessionEventKind.CompletionRequestPrepared,
            PreparedBody(correlation, "tool-continuation", runtime, prompt, checkpoint: 2),
            continuationPrepared,
            result2
        );
        DecodedSessionEvent restartedEvent = Event(
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody(),
            restarted,
            continuationPrepared
        );
        DecodedSessionEvent failedEvent = Event(
            SessionEventKind.CompletionAttemptFailed,
            new CompletionAttemptFailedBody(
                CompletionTerminationKind.Failed,
                "provider",
                "known failure",
                []
            ),
            failed,
            restarted
        );

        var scenarios = new[] {
            Scenario(
                "created-idle",
                bootstrap,
                new SessionExecutionState(SessionExecutionPhase.Idle, SessionEventKind.SessionCreated)
            ),
            Scenario(
                "observation",
                [.. bootstrap, observationEvent],
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingAgentAction,
                    SessionEventKind.ObservationAccepted,
                    ActiveCorrelationId: correlation
                )
            ),
            Scenario(
                "prepared",
                [.. bootstrap, observationEvent, preparedEvent],
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingCompletionDispatch,
                    SessionEventKind.CompletionRequestPrepared,
                    PendingRequestPreparedAddress: prepared,
                    ActiveCorrelationId: correlation
                )
            ),
            Scenario(
                "action-with-tools",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    actionEvent
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingToolExecution,
                    SessionEventKind.AgentActionProduced,
                    PendingToolCall: call1,
                    ActiveCorrelationId: correlation,
                    PendingToolRuntimeIdentity: ToolRuntimeIdentity
                )
            ),
            Scenario(
                "tool-started",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    actionEvent,
                    started1Event
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingToolExecution,
                    SessionEventKind.ToolExecutionStarted,
                    PendingToolCall: call1,
                    PendingOperationId: "operation-1",
                    PendingToolExecutionStarted: true,
                    ToolExecutionSequenceCheckpoint: 1,
                    ActiveCorrelationId: correlation,
                    PendingToolRuntimeIdentity: ToolRuntimeIdentity
                )
            ),
            Scenario(
                "partial-tool-results",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    actionEvent,
                    started1Event,
                    result1Event
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingToolExecution,
                    SessionEventKind.ToolResultObserved,
                    PendingToolCall: call2,
                    ToolExecutionSequenceCheckpoint: 1,
                    ActiveCorrelationId: correlation,
                    PendingToolRuntimeIdentity: ToolRuntimeIdentity
                )
            ),
            Scenario(
                "second-tool-started",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    actionEvent,
                    started1Event,
                    result1Event,
                    started2Event
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingToolExecution,
                    SessionEventKind.ToolExecutionStarted,
                    PendingToolCall: call2,
                    PendingOperationId: "operation-2",
                    PendingToolExecutionStarted: true,
                    ToolExecutionSequenceCheckpoint: 2,
                    ActiveCorrelationId: correlation,
                    PendingToolRuntimeIdentity: ToolRuntimeIdentity
                )
            ),
            Scenario(
                "tool-results-settled",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    actionEvent,
                    started1Event,
                    result1Event,
                    started2Event,
                    result2Event
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingAgentAction,
                    SessionEventKind.ToolResultObserved,
                    ToolExecutionSequenceCheckpoint: 2,
                    ActiveCorrelationId: correlation
                )
            ),
            Scenario(
                "tool-continuation-prepared",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    actionEvent,
                    started1Event,
                    result1Event,
                    started2Event,
                    result2Event,
                    continuationPreparedEvent
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingCompletionDispatch,
                    SessionEventKind.CompletionRequestPrepared,
                    ToolExecutionSequenceCheckpoint: 2,
                    PendingRequestPreparedAddress: continuationPrepared,
                    ActiveCorrelationId: correlation
                )
            ),
            Scenario(
                "attempt-restarted",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    actionEvent,
                    started1Event,
                    result1Event,
                    started2Event,
                    result2Event,
                    continuationPreparedEvent,
                    restartedEvent
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingCompletion,
                    SessionEventKind.CompletionAttemptStarted,
                    ToolExecutionSequenceCheckpoint: 2,
                    PendingRequestPreparedAddress: continuationPrepared,
                    ActiveCorrelationId: correlation,
                    ActiveCompletionAttemptAddress: restarted
                )
            ),
            Scenario(
                "attempt-failed",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    actionEvent,
                    started1Event,
                    result1Event,
                    started2Event,
                    result2Event,
                    continuationPreparedEvent,
                    restartedEvent,
                    failedEvent
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.TurnFailed,
                    SessionEventKind.CompletionAttemptFailed,
                    ToolExecutionSequenceCheckpoint: 2
                )
            ),
            Scenario(
                "terminal-live-action",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    Event(
                        SessionEventKind.AgentActionProduced,
                        new AgentActionProducedBody(
                            new ActionMessage([new ActionBlock.Text("done")]),
                            Invocation(),
                            correlation,
                            new SessionExecutionCheckpoint(0),
                            ToolRuntimeIdentity: null
                        ),
                        terminalAction,
                        completionStarted
                    )
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.Idle,
                    SessionEventKind.AgentActionProduced
                )
            ),
            Scenario(
                "terminal-imported-action",
                [
                    .. bootstrap,
                    observationEvent,
                    Event(
                        SessionEventKind.ImportedAgentAction,
                        new AgentActionProducedBody(
                            new ActionMessage([new ActionBlock.Text("imported")]),
                            Invocation(),
                            correlation,
                            new SessionExecutionCheckpoint(0),
                            ToolRuntimeIdentity: null
                        ),
                        importedAction,
                        observation
                    )
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.Idle,
                    SessionEventKind.ImportedAgentAction
                )
            ),
            Scenario(
                "setup-run-after-idle",
                [
                    .. bootstrap,
                    observationEvent,
                    preparedEvent,
                    completionStartedEvent,
                    Event(
                        SessionEventKind.AgentActionProduced,
                        new AgentActionProducedBody(
                            new ActionMessage([new ActionBlock.Text("done")]),
                            Invocation(),
                            correlation,
                            new SessionExecutionCheckpoint(0),
                            ToolRuntimeIdentity: null
                        ),
                        terminalAction,
                        completionStarted
                    ),
                    Event(
                        SessionEventKind.SystemPromptSetup,
                        new SystemPromptSetupBody("system-B"),
                        promptAfterIdle,
                        terminalAction
                    )
                ],
                new SessionExecutionState(
                    SessionExecutionPhase.Idle,
                    SessionEventKind.SystemPromptSetup
                )
            )
        };

        Assert.Equal(
            new SessionExecutionState(SessionExecutionPhase.Empty, HeadKind: null),
            SessionReducer.Empty.ExecutionState
        );
        foreach (ExecutionScenario scenario in scenarios) {
            SessionExecutionState actual =
                SessionReducer.Reduce(scenario.Events).ExecutionState;
            Assert.True(
                scenario.Expected == actual,
                $"Reference oracle scenario '{scenario.Name}' differed.{Environment.NewLine}"
                    + $"Expected: {scenario.Expected}{Environment.NewLine}"
                    + $"Actual:   {actual}"
            );
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10001)]
    public async Task ResumeIdle_ColdPrefixDiagnosticsStayTailBounded(
        int turnCount
    ) {
        string path = CreateColdIdleJournal(turnCount);
        using var reopened = SessionJournalEngine.Open(path);
        SessionJournalReadDiagnostics before = reopened.CaptureReadDiagnostics();

        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        SessionJournalReadDiagnostics delta =
            reopened.CaptureReadDiagnostics() - before;
        Assert.False(outcome.Advanced);
        Assert.Equal(2, delta.HeaderPreviewReadCount);
        Assert.Equal(2, delta.PayloadReadCount);
        Assert.True(delta.LogicalPayloadByteCount > 0);
        Assert.Equal(0, delta.ChronologicalChainReadCount);
        Assert.Equal(0, delta.ChronologicalEventCount);
        Assert.Equal(0, delta.FullProjectionInvocationCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public async Task ResumeStartedRefusal_DiagnosticsStayLocalAcrossColdPrefixLengths(
        int turnCount
    ) {
        string path = CreateColdIdleJournal(turnCount);
        var client = new NeverCompletionClient();
        var candidateSource = new TestContextCandidateSource();
        SessionRuntime runtime = CreateRuntime(client) with {
            ContextCandidateSource = candidateSource
        };
        using (var preparing = SessionJournalEngine.OpenForTest(
            path,
            runtime,
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted
            )
        )) {
            await CoherentArtifactSetTestFixture.ActivateAtCurrentHeadAsync(
                path,
                preparing,
                candidateSource,
                fixtureId: $"prepared-refusal-{turnCount}"
            );
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => preparing.SendAsync("pending", CancellationToken.None)
            );
        }

        using var reopened = SessionJournalEngine.Open(path, runtime);
        SessionJournalReadDiagnostics before = reopened.CaptureReadDiagnostics();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopened.ResumeAsync(CancellationToken.None)
        );

        SessionJournalReadDiagnostics delta =
            reopened.CaptureReadDiagnostics() - before;
        Assert.Equal(1, delta.HeaderPreviewReadCount);
        Assert.Equal(3, delta.PayloadReadCount);
        Assert.True(delta.LogicalPayloadByteCount > 0);
        Assert.Equal(0, delta.ChronologicalChainReadCount);
        Assert.Equal(0, delta.ChronologicalEventCount);
        Assert.Equal(0, delta.FullProjectionInvocationCount);
        Assert.Equal(0, client.Calls);
    }

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private string CreateColdIdleJournal(int turnCount) {
        string tempRoot = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            tempRoot,
            "atelia-session-journal-cold-prefix-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        using (SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
        }

        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        EventAddress head = journal.GetHead(main)
            ?? throw new InvalidDataException("Created SessionJournal has no head.");
        for (int i = 0; i < turnCount; i++) {
            EventAddress observation = Commit(
                journal,
                head,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody($"observation-{i}")
            );
            head = observation;
            head = Commit(
                journal,
                head,
                SessionEventKind.ImportedAgentAction,
                new AgentActionProducedBody(
                    new ActionMessage([new ActionBlock.Text($"action-{i}")]),
                    Invocation(),
                    BuildCorrelationId(observation),
                    new SessionExecutionCheckpoint(0),
                    ToolRuntimeIdentity: null
                )
            );
        }
        return path;
    }

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress parent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        parent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static CompletionRequestPreparedBody PreparedBody(
        string correlationId,
        string reason,
        EventAddress runtime,
        EventAddress prompt,
        long checkpoint = 0
    ) {
        ImmutableArray<ToolDefinition> tools = [
            new ToolDefinition("alpha", "Alpha", new ToolSchema.Object()),
            new ToolDefinition("beta", "Beta", new ToolSchema.Object())
        ];
        return PreparedV4Fixture.Create(
            correlationId,
            reason,
            runtime,
            prompt,
            runtime,
            prompt,
            "model-A",
            tools,
            ToolRuntimeIdentity,
            checkpoint
        );
    }

    private static ExecutionScenario Scenario(
        string name,
        IReadOnlyList<DecodedSessionEvent> events,
        SessionExecutionState expected
    ) => new(name, events, expected);

    private static DecodedSessionEvent Event(
        SessionEventKind kind,
        object body,
        EventAddress address,
        EventAddress? parent
    ) => new(
        kind,
        BodySchemaVersion: kind == SessionEventKind.CompletionRequestPrepared ? 2 : 1,
        body,
        address,
        parent
    );

    private static EventAddress Address(int ticket)
        => EventAddressTextCodec.Parse($"ej1:{ticket:x16}0000000100000000");

    private static string BuildCorrelationId(EventAddress observation)
        => $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}";

    private static CompletionDescriptor Invocation()
        => new("scripted", "test-api-v1", "model-A");

    private static SessionRuntime CreateRuntime(ICompletionClient client)
        => new(
            client,
            CompletionTarget: new SessionCompletionTargetIdentity(
                "test-connection",
                "test",
                "test-connection-fingerprint-v1",
                "test-request-adapter-v1"
            ),
            ContextCandidateSource: new TestContextCandidateSource()
        );

    private string NewJournalPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-journal-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed record ExecutionScenario(
        string Name,
        IReadOnlyList<DecodedSessionEvent> Events,
        SessionExecutionState Expected
    );

    private sealed class NeverCompletionClient : ICompletionClient {
        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        public int Calls { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            throw new InvalidOperationException("Completion should not be called.");
        }
    }
}
