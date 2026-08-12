using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionExecutionTailResolverTests : IDisposable {
    private static readonly SessionToolRuntimeIdentity ToolIdentity = new(
        "tail-host",
        "tail-implementations-v1",
        "tail-capabilities-v1"
    );
    private const string ToolRuntimeIdentityJson =
        "\"toolRuntimeIdentity\":{\"hostId\":\"tail-host\","
        + "\"implementationSetFingerprint\":\"tail-implementations-v1\","
        + "\"capabilitySetFingerprint\":\"tail-capabilities-v1\"}";
    private readonly List<string> _paths = [];

    [Fact]
    public void DurableHeadMatrix_MatchesTailResolverAndFoldContracts() {
        string path = NewPath();
        var calls = new[] {
            new RawToolCall("alpha", "call-1", """{"n":1}"""),
            new RawToolCall("beta", "call-2", """{"n":2}""")
        };
        using var journal = EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(SessionJournalDefaults.MainBranchName, null).Unwrap();
        EventAddress runtime = Commit(
            journal,
            null,
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
            new ObservationAcceptedBody("observe")
        );
        string correlation = Correlation(observation);
        EventAddress prepared = Commit(
            journal,
            observation,
            SessionEventKind.CompletionRequestPrepared,
            PreparedBody(
                correlation,
                "observation",
                runtime,
                prompt,
                checkpoint: 0
            )
        );
        EventAddress restarted = Commit(
            journal,
            prepared,
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody()
        );
        EventAddress restartedAgain = Commit(
            journal,
            restarted,
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody()
        );
        EventAddress action = Commit(
            journal,
            restartedAgain,
            SessionEventKind.AgentActionProduced,
            new AgentActionProducedBody(
                new ActionMessage(calls.Select(call =>
                    (ActionBlock)new ActionBlock.ToolCall(call)
                ).ToArray()),
                Invocation(),
                correlation,
                new SessionExecutionCheckpoint(0),
                ToolIdentity
            )
        );
        EventAddress started1 = Commit(
            journal,
            action,
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                "call-1",
                "alpha",
                """{"n":1}""",
                "operation-1",
                1,
                ToolIdentity
            )
        );
        EventAddress result1 = Commit(
            journal,
            started1,
            SessionEventKind.ToolResultObserved,
            Result("call-1", "alpha", 1)
        );
        EventAddress started2 = Commit(
            journal,
            result1,
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                "call-2",
                "beta",
                """{"n":2}""",
                "operation-2",
                2,
                ToolIdentity
            )
        );
        EventAddress result2 = Commit(
            journal,
            started2,
            SessionEventKind.ToolResultObserved,
            Result("call-2", "beta", 2)
        );
        EventAddress continuationPrepared = Commit(
            journal,
            result2,
            SessionEventKind.CompletionRequestPrepared,
            PreparedBody(
                correlation,
                "tool-continuation",
                runtime,
                prompt,
                checkpoint: 2
            )
        );
        EventAddress completionStarted = Commit(
            journal,
            continuationPrepared,
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody()
        );
        EventAddress failed = Commit(
            journal,
            completionStarted,
            SessionEventKind.CompletionAttemptFailed,
            new CompletionAttemptFailedBody(
                CompletionTerminationKind.Failed,
                "provider-failure",
                "expected",
                Array.Empty<string>()
            )
        );
        EventAddress observationAfterFailure = Commit(
            journal,
            failed,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("try again")
        );
        EventAddress importedObservation = CommitScenarioObservation(
            journal,
            "imported-terminal",
            created,
            "imported observation"
        );
        EventAddress imported = CommitToBranch(
            journal,
            "imported-terminal",
            importedObservation,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([new ActionBlock.Text("done")]),
                Invocation(),
                Correlation(importedObservation),
                new SessionExecutionCheckpoint(0),
                ToolRuntimeIdentity: null
            )
        );
        EventAddress setup = CommitToBranch(
            journal,
            "imported-terminal",
            imported,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-B")
        );

        EventAddress liveObservation = CommitScenarioObservation(
            journal,
            "live-terminal",
            created,
            "live terminal"
        );
        EventAddress livePrepared = CommitToBranch(
            journal,
            "live-terminal",
            liveObservation,
            SessionEventKind.CompletionRequestPrepared,
            PreparedBody(
                Correlation(liveObservation),
                "observation",
                runtime,
                prompt,
                checkpoint: 0
            )
        );
        EventAddress liveAttempt = CommitToBranch(
            journal,
            "live-terminal",
            livePrepared,
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody()
        );
        EventAddress liveAttemptAgain = CommitToBranch(
            journal,
            "live-terminal",
            liveAttempt,
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody()
        );
        EventAddress liveTerminal = CommitToBranch(
            journal,
            "live-terminal",
            liveAttemptAgain,
            SessionEventKind.AgentActionProduced,
            new AgentActionProducedBody(
                new ActionMessage([new ActionBlock.Text("live done")]),
                Invocation(),
                Correlation(liveObservation),
                new SessionExecutionCheckpoint(0),
                ToolRuntimeIdentity: null
            )
        );

        EventAddress singleObservation = CommitScenarioObservation(
            journal,
            "single-tool",
            created,
            "single tool"
        );
        RawToolCall singleCall =
            new("alpha", "single-call", """{"n":1}""");
        EventAddress singleAction = CommitToBranch(
            journal,
            "single-tool",
            singleObservation,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([
                    new ActionBlock.ToolCall(singleCall)
                ]),
                Invocation(),
                Correlation(singleObservation),
                new SessionExecutionCheckpoint(0),
                ToolIdentity
            )
        );
        EventAddress singleStarted = CommitToBranch(
            journal,
            "single-tool",
            singleAction,
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                singleCall.ToolCallId,
                singleCall.ToolName,
                singleCall.RawArgumentsJson,
                "single-operation",
                1,
                ToolIdentity
            )
        );
        EventAddress singleResult = CommitToBranch(
            journal,
            "single-tool",
            singleStarted,
            SessionEventKind.ToolResultObserved,
            Result(
                singleCall.ToolCallId,
                singleCall.ToolName,
                1
            )
        );

        var scenarios = new[] {
            Scenario(
                "genesis-created",
                prompt,
                created,
                SessionExecutionPhase.Idle,
                SessionEventKind.SessionCreated,
                foldable: true
            ),
            Scenario(
                "observation",
                created,
                observation,
                SessionExecutionPhase.AwaitingAgentAction,
                SessionEventKind.ObservationAccepted,
                foldable: true
            ),
            Scenario(
                "prepared",
                created,
                prepared,
                SessionExecutionPhase.AwaitingCompletionDispatch,
                SessionEventKind.CompletionRequestPrepared,
                foldable: true
            ),
            Scenario(
                "attempt",
                created,
                restarted,
                SessionExecutionPhase.AwaitingCompletion,
                SessionEventKind.CompletionAttemptStarted,
                foldable: true
            ),
            Scenario(
                "repeated-attempt",
                created,
                restartedAgain,
                SessionExecutionPhase.AwaitingCompletion,
                SessionEventKind.CompletionAttemptStarted,
                foldable: true
            ),
            Scenario(
                "multi-tool-action",
                created,
                action,
                SessionExecutionPhase.AwaitingToolExecution,
                SessionEventKind.AgentActionProduced,
                foldable: false
            ),
            Scenario(
                "multi-tool-first-start",
                created,
                started1,
                SessionExecutionPhase.AwaitingToolExecution,
                SessionEventKind.ToolExecutionStarted,
                foldable: false
            ),
            Scenario(
                "multi-tool-partial-result",
                created,
                result1,
                SessionExecutionPhase.AwaitingToolExecution,
                SessionEventKind.ToolResultObserved,
                foldable: false
            ),
            Scenario(
                "multi-tool-next-start",
                created,
                started2,
                SessionExecutionPhase.AwaitingToolExecution,
                SessionEventKind.ToolExecutionStarted,
                foldable: false
            ),
            Scenario(
                "multi-tool-final-result",
                created,
                result2,
                SessionExecutionPhase.AwaitingAgentAction,
                SessionEventKind.ToolResultObserved,
                foldable: true
            ),
            Scenario(
                "settled-continuation-prepared",
                created,
                continuationPrepared,
                SessionExecutionPhase.AwaitingCompletionDispatch,
                SessionEventKind.CompletionRequestPrepared,
                foldable: true
            ),
            Scenario(
                "known-failure",
                created,
                failed,
                SessionExecutionPhase.TurnFailed,
                SessionEventKind.CompletionAttemptFailed,
                foldable: true
            ),
            Scenario(
                "imported-terminal",
                created,
                imported,
                SessionExecutionPhase.Idle,
                SessionEventKind.ImportedAgentAction,
                foldable: true
            ),
            Scenario(
                "terminal-to-setup",
                created,
                setup,
                SessionExecutionPhase.Idle,
                SessionEventKind.SystemPromptSetup,
                foldable: true
            ),
            Scenario(
                "live-terminal-after-repeated-attempt",
                created,
                liveTerminal,
                SessionExecutionPhase.Idle,
                SessionEventKind.AgentActionProduced,
                foldable: true
            ),
            Scenario(
                "single-tool-action",
                created,
                singleAction,
                SessionExecutionPhase.AwaitingToolExecution,
                SessionEventKind.ImportedAgentAction,
                foldable: false
            ),
            Scenario(
                "single-tool-start",
                created,
                singleStarted,
                SessionExecutionPhase.AwaitingToolExecution,
                SessionEventKind.ToolExecutionStarted,
                foldable: false
            ),
            Scenario(
                "single-tool-final-result",
                created,
                singleResult,
                SessionExecutionPhase.AwaitingAgentAction,
                SessionEventKind.ToolResultObserved,
                foldable: true
            )
        };

        Assert.Equal(
            1,
            AssertResolverState(
                journal,
                runtime,
                SessionExecutionPhase.Empty,
                SessionEventKind.RuntimeConfigSetup
            )
                .Diagnostics.PayloadReadCount
        );
        Assert.Equal(
            2,
            AssertResolverState(
                journal,
                prompt,
                SessionExecutionPhase.Empty,
                SessionEventKind.SystemPromptSetup
            )
                .Diagnostics.PayloadReadCount
        );
        foreach (DifferentialScenario scenario in scenarios) {
            AssertTailScenario(
                journal,
                runtime,
                prompt,
                scenario
            );
        }
        AssertMalformedConsumerMatrix(
            journal,
            runtime,
            prompt,
            created,
            observationAfterFailure
        );
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void Resolve_TerminalImportedColdPrefix_HasConstantReads(int turns) {
        string path = CreateImportedColdPrefix(turns);
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(main)
            ?? throw new InvalidDataException("Fixture has no head.");
        var reader = new SessionJournalEventReader(journal);

        SessionExecutionRecovery recovery =
            SessionExecutionTailResolver.Resolve(reader, head);

        Assert.Equal(SessionExecutionPhase.Idle, recovery.State.Phase);
        Assert.Equal(2, recovery.Diagnostics.PayloadReadCount);
        Assert.Equal(2, recovery.Diagnostics.HeaderReadCount);
        Assert.Equal(0, reader.CaptureDiagnostics().ChronologicalChainReadCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void Resolve_PreparedAfterColdPrefix_HasConstantReads(int turns) {
        string path = CreateImportedColdPrefix(turns);
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress prior = journal.GetHead(main)
            ?? throw new InvalidDataException("Fixture has no head.");
        IReadOnlyList<EventAddress> bootstrap =
            journal.ReadChronologicalChain(prior, checkedRead: true).Unwrap();
        EventAddress observation = Commit(
            journal,
            prior,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("pending")
        );
        EventAddress prepared = Commit(
            journal,
            observation,
            SessionEventKind.CompletionRequestPrepared,
            PreparedBody(
                Correlation(observation),
                "observation",
                bootstrap[0],
                bootstrap[1],
                checkpoint: 0
            )
        );
        var reader = new SessionJournalEventReader(journal);

        SessionExecutionRecovery recovery =
            SessionExecutionTailResolver.Resolve(reader, prepared);

        Assert.Equal(
            SessionExecutionPhase.AwaitingCompletionDispatch,
            recovery.State.Phase
        );
        Assert.Equal(2, recovery.Diagnostics.PayloadReadCount);
        Assert.Equal(1, recovery.Diagnostics.HeaderReadCount);
        Assert.Equal(0, reader.CaptureDiagnostics().ChronologicalChainReadCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void Resolve_ImportedAfterSettledResult_MatchesOracleWithConstantReads(
        int turns
    ) {
        string path = CreateImportedColdPrefix(turns);
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress prior = journal.GetHead(main)
            ?? throw new InvalidDataException("Fixture has no head.");
        EventAddress observation = Commit(
            journal,
            prior,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("use tool")
        );
        string correlation = Correlation(observation);
        EventAddress action = Commit(
            journal,
            observation,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([
                    new ActionBlock.ToolCall(
                        new RawToolCall("alpha", "call-1", "{}")
                    )
                ]),
                Invocation(),
                correlation,
                new SessionExecutionCheckpoint(0),
                ToolIdentity
            )
        );
        EventAddress started = Commit(
            journal,
            action,
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                "call-1",
                "alpha",
                "{}",
                "operation-1",
                1,
                ToolIdentity
            )
        );
        EventAddress result = Commit(
            journal,
            started,
            SessionEventKind.ToolResultObserved,
            Result("call-1", "alpha", 1)
        );
        EventAddress terminal = Commit(
            journal,
            result,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([new ActionBlock.Text("done")]),
                Invocation(),
                correlation,
                new SessionExecutionCheckpoint(1),
                ToolRuntimeIdentity: null
            )
        );
        var reader = new SessionJournalEventReader(journal);

        SessionExecutionRecovery recovery =
            SessionExecutionTailResolver.Resolve(reader, terminal);

        Assert.Equal(SessionExecutionPhase.Idle, recovery.State.Phase);
        Assert.Equal(
            SessionEventKind.ImportedAgentAction,
            recovery.State.HeadKind
        );
        Assert.Equal(terminal, recovery.Boundary.SourceAction);
        Assert.Equal(4, recovery.Diagnostics.PayloadReadCount);
        Assert.Equal(2, recovery.Diagnostics.HeaderReadCount);
        Assert.Equal(0, reader.CaptureDiagnostics().ChronologicalChainReadCount);
    }

    [Fact]
    public void Resolve_ExactHeadsAcrossBranchAndRewind_StayOnTheirLineage() {
        string path = NewPath();
        using var journal = EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(SessionJournalDefaults.MainBranchName, null).Unwrap();
        EventAddress runtime = Commit(
            journal,
            null,
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
            new SystemPromptSetupBody("system")
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
            new ObservationAcceptedBody("fork")
        );
        journal.CreateBranch("left", observation).Unwrap();
        journal.CreateBranch("right", observation).Unwrap();
        EventAddress left = CommitToBranch(
            journal,
            "left",
            observation,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([new ActionBlock.Text("left")]),
                Invocation(),
                Correlation(observation),
                new SessionExecutionCheckpoint(0),
                ToolRuntimeIdentity: null
            )
        );
        EventAddress right = CommitToBranch(
            journal,
            "right",
            observation,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([new ActionBlock.Text("right")]),
                Invocation(),
                Correlation(observation),
                new SessionExecutionCheckpoint(0),
                ToolRuntimeIdentity: null
            )
        );

        foreach ((
            EventAddress ExactHead,
            SessionExecutionPhase ExpectedPhase,
            SessionEventKind ExpectedKind
        ) item in new[] {
            (
                observation,
                SessionExecutionPhase.AwaitingAgentAction,
                SessionEventKind.ObservationAccepted
            ),
            (
                left,
                SessionExecutionPhase.Idle,
                SessionEventKind.ImportedAgentAction
            ),
            (
                right,
                SessionExecutionPhase.Idle,
                SessionEventKind.ImportedAgentAction
            )
        }) {
            SessionExecutionRecovery recovery =
                SessionExecutionTailResolver.Resolve(
                    new SessionJournalEventReader(journal),
                    item.ExactHead
                );
            Assert.Equal(item.ExpectedPhase, recovery.State.Phase);
            Assert.Equal(item.ExpectedKind, recovery.State.HeadKind);
            Assert.Equal(item.ExactHead, recovery.Head);
        }
        Assert.Equal(left, SessionExecutionTailResolver.Resolve(
            new SessionJournalEventReader(journal),
            left
        ).Boundary.SourceAction);
        Assert.Equal(right, SessionExecutionTailResolver.Resolve(
            new SessionJournalEventReader(journal),
            right
        ).Boundary.SourceAction);

        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        Assert.True(journal.MoveRef(main, observation, left).Unwrap());
        Assert.True(journal.MoveRef(main, left, observation).Unwrap());
        Assert.True(journal.MoveRef(main, observation, right).Unwrap());
        journal.Dispose();

        using var reopened = SessionJournalEngine.Open(path);
        SessionExecutionRecovery current = reopened.ResolveExecutionTail();
        Assert.Equal(right, current.Head);
        Assert.Equal(SessionExecutionPhase.Idle, current.State.Phase);
        Assert.Equal(
            SessionEventKind.ImportedAgentAction,
            current.State.HeadKind
        );
        Assert.Equal(right, current.Boundary.SourceAction);
    }

    [Fact]
    public void Resolve_ControlledWriterHeads_ReportExpectedStates() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system", "surface-A")
        );
        SessionExecutionRecovery created = engine.ResolveExecutionTail();
        Assert.Equal(SessionExecutionPhase.Idle, created.State.Phase);
        Assert.Equal(SessionEventKind.SessionCreated, created.State.HeadKind);

        EventAddress observation = engine.AppendObservation("hello");
        SessionExecutionState observed =
            engine.ResolveExecutionTail(observation).State;
        Assert.Equal(
            SessionExecutionPhase.AwaitingAgentAction,
            observed.Phase
        );
        Assert.Equal(
            SessionEventKind.ObservationAccepted,
            observed.HeadKind
        );

        EventAddress action = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("world")]),
            Invocation()
        );
        SessionExecutionState completed =
            engine.ResolveExecutionTail(action).State;
        Assert.Equal(SessionExecutionPhase.Idle, completed.Phase);
        Assert.Equal(
            SessionEventKind.ImportedAgentAction,
            completed.HeadKind
        );
    }

    [Theory]
    [InlineData("wrong-parent")]
    [InlineData("wrong-attempt")]
    [InlineData("started-without-prepared")]
    [InlineData("wrong-correlation")]
    [InlineData("wrong-checkpoint")]
    [InlineData("wrong-runtime")]
    [InlineData("missing-runtime")]
    [InlineData("extra-runtime")]
    [InlineData("duplicate-call-id")]
    [InlineData("result-before-start")]
    [InlineData("duplicate-start")]
    [InlineData("duplicate-result")]
    [InlineData("out-of-order-start")]
    [InlineData("arguments-mismatch")]
    [InlineData("sequence-gap")]
    [InlineData("sequence-repeat")]
    [InlineData("setup-pending-prepared")]
    [InlineData("setup-pending-tool")]
    public void MalformedOperationalMatrix_TailConsumersFailFast(
        string mutation
    ) {
        string path = NewPath();
        using var journal = EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(SessionJournalDefaults.MainBranchName, null).Unwrap();
        EventAddress runtime = Commit(
            journal,
            null,
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
            new SystemPromptSetupBody("system")
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
            new ObservationAcceptedBody("observe")
        );
        string correlation = Correlation(observation);

        if (mutation == "started-without-prepared") {
            EventAddress startedWithoutPrepared = Commit(
                journal,
                observation,
                SessionEventKind.CompletionAttemptStarted,
                new CompletionAttemptStartedBody()
            );
            AssertMalformedConsumerMatrix(
                journal,
                runtime,
                prompt,
                created,
                startedWithoutPrepared
            );
            return;
        }

        if (mutation == "wrong-parent") {
            EventAddress wrongParentHead = Commit(
                journal,
                observation,
                SessionEventKind.ToolExecutionStarted,
                new ToolExecutionStartedBody(
                    "call-1",
                    "alpha",
                    "{}",
                    "operation-1",
                    1,
                    ToolIdentity
                )
            );
            AssertMalformedConsumerMatrix(
                journal,
                runtime,
                prompt,
                created,
                wrongParentHead
            );
            return;
        }

        EventAddress prepared = Commit(
            journal,
            observation,
            SessionEventKind.CompletionRequestPrepared,
            PreparedBody(
                correlation,
                "observation",
                runtime,
                prompt,
                checkpoint: 0
            )
        );
        if (mutation == "setup-pending-prepared") {
            EventAddress pendingSetup = Commit(
                journal,
                prepared,
                SessionEventKind.SystemPromptSetup,
                new SystemPromptSetupBody("not allowed")
            );
            AssertMalformedConsumerMatrix(
                journal,
                runtime,
                prompt,
                created,
                pendingSetup
            );
            return;
        }
        if (mutation == "wrong-attempt") {
            EventAddress wrongAttemptHead = Commit(
                journal,
                prepared,
                SessionEventKind.AgentActionProduced,
                new AgentActionProducedBody(
                    new ActionMessage([
                        new ActionBlock.Text("skipped attempt")
                    ]),
                    Invocation(),
                    correlation,
                    new SessionExecutionCheckpoint(0),
                    ToolRuntimeIdentity: null
                )
            );
            AssertMalformedConsumerMatrix(
                journal,
                runtime,
                prompt,
                created,
                wrongAttemptHead
            );
            return;
        }
        EventAddress completionStarted = Commit(
            journal,
            prepared,
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody()
        );
        RawToolCall[] calls = mutation switch {
            "extra-runtime" => [],
            "duplicate-call-id" => [
                new RawToolCall("alpha", "call-1", "{}"),
                new RawToolCall("beta", "call-1", "{}")
            ],
            _ => [
                new RawToolCall("alpha", "call-1", "{}"),
                new RawToolCall("beta", "call-2", "{}")
            ]
        };
        var actionBody = new AgentActionProducedBody(
                new ActionMessage(calls.Select(call =>
                    (ActionBlock)new ActionBlock.ToolCall(call)
                ).ToArray()),
                Invocation(),
                mutation == "wrong-correlation" ? "wrong" : correlation,
                new SessionExecutionCheckpoint(
                    mutation == "wrong-checkpoint" ? 9 : 0
                ),
                mutation switch {
                    "wrong-runtime" =>
                        ToolIdentity with { HostId = "other-host" },
                    "missing-runtime" => null,
                    _ => ToolIdentity
                }
            );
        EventAddress action;
        if (mutation == "missing-runtime") {
            AgentActionProducedBody validBody = actionBody with {
                ToolRuntimeIdentity = ToolIdentity
            };
            byte[] validPayload = SessionEventCodec.Encode(
                SessionEventKind.AgentActionProduced,
                validBody
            );
            action = CommitRawMutation(
                journal,
                completionStarted,
                SessionEventKind.AgentActionProduced,
                validPayload,
                ToolRuntimeIdentityJson,
                "\"toolRuntimeIdentity\":null"
            );
        }
        else if (mutation == "extra-runtime") {
            AgentActionProducedBody validBody = actionBody with {
                ToolRuntimeIdentity = null
            };
            byte[] validPayload = SessionEventCodec.Encode(
                SessionEventKind.AgentActionProduced,
                validBody
            );
            action = CommitRawMutation(
                journal,
                completionStarted,
                SessionEventKind.AgentActionProduced,
                validPayload,
                "\"toolRuntimeIdentity\":null",
                ToolRuntimeIdentityJson
            );
        }
        else {
            action = Commit(
                journal,
                completionStarted,
                SessionEventKind.AgentActionProduced,
                actionBody
            );
        }

        EventAddress malformedHead;
        switch (mutation) {
            case "result-before-start":
                malformedHead = Commit(
                    journal,
                    action,
                    SessionEventKind.ToolResultObserved,
                    Result("call-1", "alpha", 1)
                );
                break;
            case "out-of-order-start":
                malformedHead = Commit(
                    journal,
                    action,
                    SessionEventKind.ToolExecutionStarted,
                    new ToolExecutionStartedBody(
                        "call-2",
                        "beta",
                        "{}",
                        "operation-2",
                        1,
                        ToolIdentity
                    )
                );
                break;
            case "arguments-mismatch":
                malformedHead = Commit(
                    journal,
                    action,
                    SessionEventKind.ToolExecutionStarted,
                    new ToolExecutionStartedBody(
                        "call-1",
                        "alpha",
                        """{"wrong":true}""",
                        "operation-1",
                        1,
                        ToolIdentity
                    )
                );
                break;
            case "sequence-gap":
                malformedHead = Commit(
                    journal,
                    action,
                    SessionEventKind.ToolExecutionStarted,
                    new ToolExecutionStartedBody(
                        "call-1",
                        "alpha",
                        "{}",
                        "operation-1",
                        2,
                        ToolIdentity
                    )
                );
                break;
            case "duplicate-start": {
                EventAddress firstStart = Commit(
                    journal,
                    action,
                    SessionEventKind.ToolExecutionStarted,
                    new ToolExecutionStartedBody(
                        "call-1",
                        "alpha",
                        "{}",
                        "operation-1",
                        1,
                        ToolIdentity
                    )
                );
                malformedHead = Commit(
                    journal,
                    firstStart,
                    SessionEventKind.ToolExecutionStarted,
                    new ToolExecutionStartedBody(
                        "call-1",
                        "alpha",
                        "{}",
                        "operation-duplicate",
                        2,
                        ToolIdentity
                    )
                );
                break;
            }
            case "duplicate-result": {
                EventAddress firstStart = Commit(
                    journal,
                    action,
                    SessionEventKind.ToolExecutionStarted,
                    new ToolExecutionStartedBody(
                        "call-1",
                        "alpha",
                        "{}",
                        "operation-1",
                        1,
                        ToolIdentity
                    )
                );
                EventAddress firstResult = Commit(
                    journal,
                    firstStart,
                    SessionEventKind.ToolResultObserved,
                    Result("call-1", "alpha", 1)
                );
                malformedHead = Commit(
                    journal,
                    firstResult,
                    SessionEventKind.ToolResultObserved,
                    Result("call-1", "alpha", 1)
                );
                break;
            }
            case "sequence-repeat": {
                EventAddress firstStart = Commit(
                    journal,
                    action,
                    SessionEventKind.ToolExecutionStarted,
                    new ToolExecutionStartedBody(
                        "call-1",
                        "alpha",
                        "{}",
                        "operation-1",
                        1,
                        ToolIdentity
                    )
                );
                EventAddress firstResult = Commit(
                    journal,
                    firstStart,
                    SessionEventKind.ToolResultObserved,
                    Result("call-1", "alpha", 1)
                );
                malformedHead = Commit(
                    journal,
                    firstResult,
                    SessionEventKind.ToolExecutionStarted,
                    new ToolExecutionStartedBody(
                        "call-2",
                        "beta",
                        "{}",
                        "operation-2",
                        1,
                        ToolIdentity
                    )
                );
                break;
            }
            case "setup-pending-tool":
                malformedHead = Commit(
                    journal,
                    action,
                    SessionEventKind.RuntimeConfigSetup,
                    new SessionRuntimeConfiguration(
                        "model-B",
                        "surface-B",
                        SessionJournalDefaults.Schema,
                        new(0)
                    )
                );
                break;
            default:
                malformedHead = action;
                break;
        }

        AssertMalformedConsumerMatrix(
            journal,
            runtime,
            prompt,
            created,
            malformedHead,
            canDecodeSuffix: mutation is not (
                "missing-runtime"
                or "extra-runtime"
            )
        );
    }

    private static void AssertMalformedConsumerMatrix(
        EventJournal.EventJournal journal,
        EventAddress runtime,
        EventAddress prompt,
        EventAddress cut,
        EventAddress malformedHead,
        bool canDecodeSuffix = true
    ) {
        var reader = new SessionJournalEventReader(journal);

        Assert.Throws<InvalidDataException>(() =>
            SessionExecutionTailResolver.Resolve(
                reader,
                malformedHead
            )
        );
        Assert.Equal(
            0,
            reader.CaptureDiagnostics()
                .ChronologicalChainReadCount
        );
        if (!canDecodeSuffix) {
            // Runtime identity presence is also a codec-owned wire
            // invariant. The raw mutation proves both audit routes reject
            // it before a decoded suffix exists for FoldSuffix.
            return;
        }

        SessionExecutionRecovery cutRecovery =
            SessionExecutionTailResolver.Resolve(
                new SessionJournalEventReader(journal),
                cut
            );
        SessionDependencyClosedFoldSeed seed =
            SessionDependencyClosedFoldSeed.Create(
                new SessionGoverningSetup(
                    cut,
                    runtime,
                    new SessionRuntimeConfiguration(
                        "model-A",
                        "surface-A",
                        SessionJournalDefaults.Schema,
                        new(0)
                    ),
                    prompt,
                    "system"
                ),
                cutRecovery
            );
        IReadOnlyList<DecodedSessionEvent> suffix =
            ReadDecodedSuffix(
                journal,
                cut,
                malformedHead
            );
        Assert.Throws<InvalidDataException>(() =>
            SessionTailContextProjection.FoldSuffix(
                seed,
                suffix
            )
        );
    }

    [Fact]
    public void ActionCodec_RequiresCorrelationIdExactly() {
        const string missing =
            """{"v":1,"body":{"action":[{"kind":"text","content":"done"}],"invocation":{"providerId":"p","apiSpecId":"a","model":"m"},"execution":{"lastIssuedToolExecutionSequence":0},"toolRuntimeIdentity":null}}""";
        const string extra =
            """{"v":1,"body":{"action":[{"kind":"text","content":"done"}],"invocation":{"providerId":"p","apiSpecId":"a","model":"m"},"correlationId":"c","execution":{"lastIssuedToolExecutionSequence":0},"toolRuntimeIdentity":null,"unknown":true}}""";

        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.AgentActionProduced,
            Encoding.UTF8.GetBytes(missing),
            out _
        ));
        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.AgentActionProduced,
            Encoding.UTF8.GetBytes(extra),
            out _
        ));
    }

    [Fact]
    public void Resolve_MalformedNearHeadSetupPayload_FailsFast() {
        string path = NewPath();
        using var journal = EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(SessionJournalDefaults.MainBranchName, null).Unwrap();
        EventAddress runtime = Commit(
            journal,
            null,
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
            new SystemPromptSetupBody("system")
        );
        EventAddress created = Commit(
            journal,
            prompt,
            SessionEventKind.SessionCreated,
            new SessionCreatedBody(SessionCreationOrigin.Native)
        );
        EventAddress malformed = journal.CommitToRef(
            SessionJournalDefaults.MainBranchName,
            created,
            Encoding.UTF8.GetBytes(
                """{"v":1,"body":{"content":42}}"""
            ),
            opaqueEventKind: (uint)SessionEventKind.SystemPromptSetup,
            hint: default
        ).Unwrap().EventAddress;
        var reader = new SessionJournalEventReader(journal);

        Assert.Throws<InvalidDataException>(() =>
            SessionExecutionTailResolver.Resolve(reader, malformed)
        );
        Assert.Equal(1, reader.CaptureDiagnostics().PayloadReadCount);
        Assert.Equal(0, reader.CaptureDiagnostics().ChronologicalChainReadCount);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private string CreateImportedColdPrefix(int turns) {
        string path = NewPath();
        using var journal = EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(SessionJournalDefaults.MainBranchName, null).Unwrap();
        EventAddress head = Commit(
            journal,
            null,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration(
                "model-A",
                "surface-A",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        head = Commit(
            journal,
            head,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system")
        );
        head = Commit(
            journal,
            head,
            SessionEventKind.SessionCreated,
            new SessionCreatedBody(SessionCreationOrigin.Native)
        );
        for (int i = 0; i < turns; i++) {
            EventAddress observation = Commit(
                journal,
                head,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody($"observation-{i}")
            );
            head = Commit(
                journal,
                observation,
                SessionEventKind.ImportedAgentAction,
                new AgentActionProducedBody(
                    new ActionMessage([new ActionBlock.Text($"action-{i}")]),
                    Invocation(),
                    Correlation(observation),
                    new SessionExecutionCheckpoint(0),
                    ToolRuntimeIdentity: null
                )
            );
        }
        return path;
    }

    private static DifferentialScenario Scenario(
        string name,
        EventAddress cut,
        EventAddress head,
        SessionExecutionPhase expectedPhase,
        SessionEventKind expectedHeadKind,
        bool foldable
    ) => new(
        name,
        cut,
        head,
        expectedPhase,
        expectedHeadKind,
        foldable
    );

    private static EventAddress CommitScenarioObservation(
        EventJournal.EventJournal journal,
        string branchName,
        EventAddress parent,
        string content
    ) {
        journal.CreateBranch(branchName, parent).Unwrap();
        return CommitToBranch(
            journal,
            branchName,
            parent,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody(content)
        );
    }

    private static SessionExecutionRecovery AssertResolverState(
        EventJournal.EventJournal journal,
        EventAddress head,
        SessionExecutionPhase expectedPhase,
        SessionEventKind expectedHeadKind
    ) {
        var reader = new SessionJournalEventReader(journal);

        SessionExecutionRecovery actual =
            SessionExecutionTailResolver.Resolve(reader, head);

        Assert.Equal(expectedPhase, actual.State.Phase);
        Assert.Equal(expectedHeadKind, actual.State.HeadKind);
        AssertResolverDiagnostics(reader, actual);
        return actual;
    }

    private static void AssertTailScenario(
        EventJournal.EventJournal journal,
        EventAddress runtime,
        EventAddress prompt,
        DifferentialScenario scenario
    ) {
        var reader = new SessionJournalEventReader(journal);
        SessionExecutionRecovery resolved =
            SessionExecutionTailResolver.Resolve(
                reader,
                scenario.Head
            );
        Assert.Equal(scenario.ExpectedPhase, resolved.State.Phase);
        Assert.Equal(scenario.ExpectedHeadKind, resolved.State.HeadKind);
        AssertResolverDiagnostics(reader, resolved);
        if (resolved.State.HeadKind
            == SessionEventKind.ObservationAccepted) {
            Assert.Null(resolved.Boundary.SourcePrepared);
            Assert.Null(resolved.Boundary.SourceAction);
            Assert.Equal(
                scenario.Head,
                resolved.Boundary.SourceObservation
            );
        }

        SessionExecutionRecovery cutRecovery =
            SessionExecutionTailResolver.Resolve(
                new SessionJournalEventReader(journal),
                scenario.Cut
            );
        var setup = new SessionGoverningSetup(
            scenario.Cut,
            runtime,
            new SessionRuntimeConfiguration(
                "model-A",
                "surface-A",
                SessionJournalDefaults.Schema,
                new(0)
            ),
            prompt,
            "system-A"
        );
        SessionDependencyClosedFoldSeed seed =
            SessionDependencyClosedFoldSeed.Create(
                setup,
                cutRecovery
            );
        IReadOnlyList<DecodedSessionEvent> suffix =
            ReadDecodedSuffix(
                journal,
                scenario.Cut,
                scenario.Head
        );
        if (!scenario.Foldable) {
            // A request-context fold cannot materialize a suffix whose
            // final Action/tool dependency is still open.
            Assert.Throws<SessionSelectedLineageOpenDependencyException>(() =>
                SessionTailContextProjection.FoldSuffix(
                    seed,
                    suffix
                )
            );
            Assert.Equal(
                SessionExecutionPhase.AwaitingToolExecution,
                resolved.State.Phase
            );
            Assert.NotNull(resolved.State.PendingToolCall);
            return;
        }

        var replaySafeBoundaries =
            new List<SessionHistoryPlanningBoundary>();
        SessionTailContextProjection.TailFoldResult folded =
            SessionTailContextProjection.FoldSuffix(
                seed,
                suffix,
                replaySafeBoundaries: replaySafeBoundaries
            );

        Assert.Equal(scenario.Head, folded.GoverningSetup.Head);
        Assert.Equal(resolved.State.Phase, folded.Phase);
        Assert.Equal(
            resolved.State.ToolExecutionSequenceCheckpoint,
            folded.ToolExecutionSequenceCheckpoint
        );
        Assert.Equal(
            resolved.State.ActiveCorrelationId,
            folded.ActiveCorrelationId
        );
        SessionEventKind foldedHeadKind = suffix.Count == 0
            ? seed.HeadKind
            : suffix[^1].Kind;
        Assert.Equal(resolved.State.HeadKind, foldedHeadKind);
        bool replaySafe =
            SessionOperationalSemantics.IsReplaySafePhase(
                resolved.State.Phase
            );
        Assert.Equal(
            replaySafe,
            SessionOperationalSemantics.IsReplaySafePhase(
                folded.Phase
            )
        );
        Assert.Equal(
            replaySafe,
            replaySafeBoundaries.Any(
                boundary => boundary.Address == scenario.Head
            )
        );
    }

    private static void AssertResolverDiagnostics(
        SessionJournalEventReader reader,
        SessionExecutionRecovery recovery
    ) {
        SessionJournalReadDiagnostics reads =
            reader.CaptureDiagnostics();
        Assert.Equal(0, reads.ChronologicalChainReadCount);
        Assert.Equal(0, reads.ChronologicalEventCount);
        Assert.Equal(
            recovery.Diagnostics.HeaderReadCount,
            reads.HeaderPreviewReadCount
        );
        Assert.Equal(
            recovery.Diagnostics.PayloadReadCount,
            reads.PayloadReadCount
        );
    }

    private static IReadOnlyList<DecodedSessionEvent>
        ReadDecodedSuffix(
        EventJournal.EventJournal journal,
        EventAddress cut,
        EventAddress head
    ) {
        IReadOnlyList<DecodedSessionEvent> chain =
            ReadDecodedChain(journal, head);
        int cutIndex = -1;
        for (int i = 0; i < chain.Count; i++) {
            if (chain[i].Address == cut) {
                cutIndex = i;
                break;
            }
        }
        Assert.True(
            cutIndex >= 0,
            $"Cut {cut} is not an ancestor of head {head}."
        );
        return chain.Skip(cutIndex + 1).ToArray();
    }

    private static IReadOnlyList<DecodedSessionEvent>
        ReadDecodedChain(
        EventJournal.EventJournal journal,
        EventAddress head
    ) {
        IReadOnlyList<EventAddress> chain =
            journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        var events = new List<DecodedSessionEvent>(chain.Count);
        foreach (EventAddress address in chain) {
            using EventFrame frame = journal.ReadEvent(address).Unwrap();
            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(
                kind,
                frame.Payload,
                out int version
            );
            events.Add(new DecodedSessionEvent(
                kind,
                version,
                body,
                address,
                frame.Header.Parent
            ));
        }
        return events;
    }

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress? parent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        parent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static EventAddress CommitRawMutation(
        EventJournal.EventJournal journal,
        EventAddress parent,
        SessionEventKind kind,
        byte[] validPayload,
        string oldFragment,
        string newFragment
    ) {
        string validJson = Encoding.UTF8.GetString(validPayload);
        Assert.Contains(
            oldFragment,
            validJson,
            StringComparison.Ordinal
        );
        string malformedJson = validJson.Replace(
            oldFragment,
            newFragment,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            oldFragment,
            malformedJson,
            StringComparison.Ordinal
        );
        return journal.CommitToRef(
            SessionJournalDefaults.MainBranchName,
            parent,
            Encoding.UTF8.GetBytes(malformedJson),
            opaqueEventKind: (uint)kind,
            hint: default
        ).Unwrap().EventAddress;
    }

    private static EventAddress CommitToBranch(
        EventJournal.EventJournal journal,
        string branchName,
        EventAddress? parent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        branchName,
        parent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static ToolResultObservedBody Result(
        string callId,
        string name,
        long sequence
    ) => new(
        callId,
        name,
        sequence,
        ToolExecutionStatus.Success,
        Array.Empty<ToolResultBlock>()
    );

    private static CompletionRequestPreparedBody PreparedBody(
        string correlation,
        string reason,
        EventAddress runtime,
        EventAddress prompt,
        long checkpoint
    ) {
        ImmutableArray<ToolDefinition> tools = [
            new ToolDefinition("alpha", "Alpha", new ToolSchema.Object()),
            new ToolDefinition("beta", "Beta", new ToolSchema.Object())
        ];
        return PreparedV5Fixture.Create(
            correlation,
            reason,
            runtime,
            prompt,
            runtime,
            prompt,
            "model-A",
            tools,
            ToolIdentity,
            checkpoint
        );
    }

    private static CompletionDescriptor Invocation()
        => new("scripted", "test-api-v1", "model-A");

    private static string Correlation(EventAddress observation)
        => $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}";

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-tail-resolver-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed record DifferentialScenario(
        string Name,
        EventAddress Cut,
        EventAddress Head,
        SessionExecutionPhase ExpectedPhase,
        SessionEventKind ExpectedHeadKind,
        bool Foldable
    );
}
