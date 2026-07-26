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
    private readonly List<string> _paths = [];

    [Fact]
    public void Resolve_AllDurableHeadPhases_MatchesFullReducerOracle() {
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
                SessionJournalDefaults.Schema
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
            new SessionCreatedBody()
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
                "attempt-1",
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
            SessionEventKind.CompletionAttemptRestarted,
            new CompletionAttemptRestartedBody(
                "attempt-2",
                "attempt-1",
                prepared
            )
        );
        EventAddress action = Commit(
            journal,
            restarted,
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
                "attempt-3",
                correlation,
                "tool-continuation",
                runtime,
                prompt,
                checkpoint: 2
            )
        );
        EventAddress failed = Commit(
            journal,
            continuationPrepared,
            SessionEventKind.CompletionAttemptFailed,
            new CompletionAttemptFailedBody(
                "attempt-3",
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
        EventAddress imported = Commit(
            journal,
            observationAfterFailure,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([new ActionBlock.Text("done")]),
                Invocation(),
                Correlation(observationAfterFailure),
                new SessionExecutionCheckpoint(2),
                ToolRuntimeIdentity: null
            )
        );
        EventAddress setup = Commit(
            journal,
            imported,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-B")
        );

        EventAddress[] heads = [
            runtime,
            prompt,
            created,
            observation,
            prepared,
            restarted,
            action,
            started1,
            result1,
            started2,
            result2,
            continuationPrepared,
            failed,
            observationAfterFailure,
            imported,
            setup
        ];
        foreach (EventAddress head in heads) {
            SessionExecutionState expected = FullOracle(journal, head);
            var reader = new SessionJournalEventReader(journal);
            SessionExecutionRecovery actual =
                SessionExecutionTailResolver.Resolve(reader, head);
            Assert.True(
                expected == actual.State,
                $"Head {head} differed.{Environment.NewLine}Expected: {expected}{Environment.NewLine}Actual:   {actual.State}"
            );
            SessionJournalReadDiagnostics reads = reader.CaptureDiagnostics();
            Assert.Equal(0, reads.ChronologicalChainReadCount);
            Assert.Equal(0, reads.FullProjectionInvocationCount);
            Assert.Equal(
                actual.Diagnostics.HeaderReadCount,
                reads.HeaderPreviewReadCount
            );
            Assert.Equal(
                actual.Diagnostics.PayloadReadCount,
                reads.PayloadReadCount
            );
            if (head == observation || head == observationAfterFailure) {
                Assert.Null(actual.Boundary.SourcePrepared);
                Assert.Null(actual.Boundary.SourceAction);
                Assert.Equal(head, actual.Boundary.SourceObservation);
            }
            if (head == runtime) {
                Assert.Equal(1, actual.Diagnostics.PayloadReadCount);
            }
            if (head == prompt) {
                Assert.Equal(2, actual.Diagnostics.PayloadReadCount);
            }
        }
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
                "pending-attempt",
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
            SessionExecutionPhase.AwaitingCompletion,
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
        SessionExecutionState expected = FullOracle(journal, terminal);
        var reader = new SessionJournalEventReader(journal);

        SessionExecutionRecovery recovery =
            SessionExecutionTailResolver.Resolve(reader, terminal);

        Assert.Equal(expected, recovery.State);
        Assert.Equal(SessionExecutionPhase.Idle, recovery.State.Phase);
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
                SessionJournalDefaults.Schema
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
            new SessionCreatedBody()
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

        foreach (EventAddress exactHead in new[] { observation, left, right }) {
            SessionExecutionRecovery recovery =
                SessionExecutionTailResolver.Resolve(
                    new SessionJournalEventReader(journal),
                    exactHead
                );
            Assert.Equal(FullOracle(journal, exactHead), recovery.State);
            Assert.Equal(exactHead, recovery.Head);
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
        SessionExecutionState expectedCurrent = FullOracle(journal, right);
        journal.Dispose();

        using var reopened = SessionJournalEngine.Open(path);
        SessionExecutionRecovery current = reopened.ResolveExecutionTail();
        Assert.Equal(right, current.Head);
        Assert.Equal(expectedCurrent, current.State);
        Assert.Equal(right, current.Boundary.SourceAction);
    }

    [Fact]
    public void Resolve_ControlledWriterHeads_MatchFullProjectionOracle() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system", "surface-A")
        );
        SessionExecutionRecovery created = engine.ResolveExecutionTail();
        Assert.Equal(engine.Project().ExecutionState, created.State);

        EventAddress observation = engine.AppendObservation("hello");
        Assert.Equal(
            engine.Project().ExecutionState,
            engine.ResolveExecutionTail(observation).State
        );

        EventAddress action = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("world")]),
            Invocation()
        );
        Assert.Equal(
            engine.Project().ExecutionState,
            engine.ResolveExecutionTail(action).State
        );
    }

    [Theory]
    [InlineData("wrong-parent")]
    [InlineData("wrong-correlation")]
    [InlineData("wrong-checkpoint")]
    [InlineData("wrong-runtime")]
    [InlineData("wrong-attempt")]
    [InlineData("duplicate-call-id")]
    [InlineData("result-before-start")]
    [InlineData("out-of-order-start")]
    public void Resolve_MalformedOperationalTail_FailsFast(string mutation) {
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
                SessionJournalDefaults.Schema
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
            new SessionCreatedBody()
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
                "attempt-1",
                correlation,
                "observation",
                runtime,
                prompt,
                checkpoint: 0
            )
        );
        if (mutation == "wrong-attempt") {
            EventAddress malformedRestart = Commit(
                journal,
                prepared,
                SessionEventKind.CompletionAttemptRestarted,
                new CompletionAttemptRestartedBody(
                    "attempt-2",
                    "not-attempt-1",
                    prepared
                )
            );
            var attemptReader = new SessionJournalEventReader(journal);
            Assert.Throws<InvalidDataException>(() =>
                SessionExecutionTailResolver.Resolve(
                    attemptReader,
                    malformedRestart
                )
            );
            Assert.Equal(
                0,
                attemptReader.CaptureDiagnostics().ChronologicalChainReadCount
            );
            return;
        }
        RawToolCall[] calls = mutation == "duplicate-call-id"
            ? [
                new RawToolCall("alpha", "call-1", "{}"),
                new RawToolCall("beta", "call-1", "{}")
            ]
            : [
                new RawToolCall("alpha", "call-1", "{}"),
                new RawToolCall("beta", "call-2", "{}")
            ];
        EventAddress action = Commit(
            journal,
            prepared,
            mutation == "wrong-parent"
                ? SessionEventKind.ImportedAgentAction
                : SessionEventKind.AgentActionProduced,
            new AgentActionProducedBody(
                new ActionMessage(calls.Select(call =>
                    (ActionBlock)new ActionBlock.ToolCall(call)
                ).ToArray()),
                Invocation(),
                mutation == "wrong-correlation" ? "wrong" : correlation,
                new SessionExecutionCheckpoint(
                    mutation == "wrong-checkpoint" ? 9 : 0
                ),
                mutation == "wrong-runtime"
                    ? ToolIdentity with { HostId = "other-host" }
                    : ToolIdentity
            )
        );
        EventAddress malformedHead = mutation switch {
            "result-before-start" => Commit(
                journal,
                action,
                SessionEventKind.ToolResultObserved,
                Result("call-1", "alpha", 1)
            ),
            "out-of-order-start" => Commit(
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
            ),
            _ => action
        };
        var reader = new SessionJournalEventReader(journal);

        Assert.Throws<InvalidDataException>(() =>
            SessionExecutionTailResolver.Resolve(reader, malformedHead)
        );
        Assert.Equal(0, reader.CaptureDiagnostics().ChronologicalChainReadCount);
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
                SessionJournalDefaults.Schema
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
            new SessionCreatedBody()
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
                SessionJournalDefaults.Schema
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
            new SessionCreatedBody()
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

    private static SessionExecutionState FullOracle(
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
        return SessionReducer.Reduce(events).ExecutionState;
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
        string attemptId,
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
        return new CompletionRequestPreparedBody(
            new SessionRequestAttempt(attemptId, correlation, reason, null),
            new SessionExecutionCheckpoint(checkpoint),
            new SessionContextPlan(
                SessionRequestManifestDefaults.FullRawSelectionPolicyId,
                SessionRequestManifestDefaults.FullRawPlannerFingerprint,
                RawStartExclusive: null,
                RawRangeSha256: new string('a', 64),
                ArtifactInputs: [],
                RecalledInputs: [],
                SessionRequestManifestDefaults.FullRawRenderingProfileId,
                ModelProfileId: "model-A",
                EstimatedInputTokens: 1,
                reason
            ),
            new SessionGoverningSetupReferences(
                new SessionSetupReference(runtime, 1, new string('b', 64)),
                new SessionSetupReference(prompt, 1, new string('c', 64))
            ),
            new SessionRequestParameters("model-A", MaxTokens: null),
            new SessionRequestToolSet(
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestCanonicalizer.ComputeToolSetSha256(tools),
                tools,
                ToolIdentity
            ),
            new SessionRequestRendering(
                SessionRequestManifestDefaults.FullRawContextRendererId,
                SessionRequestManifestDefaults.FullRawContextRendererFingerprint,
                SessionRequestManifestDefaults.CanonicalRequestCodecId,
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestManifestDefaults.ReasoningCodecSetFingerprint
            ),
            new SessionRequestTarget(
                new SessionCompletionTargetIdentity(
                    "connection",
                    "test",
                    "connection-fingerprint",
                    "adapter-fingerprint"
                ),
                "surface-A",
                "scripted",
                "test-api-v1"
            ),
            new SessionRequestCommitment(
                SessionRequestManifestDefaults.CommitmentAlgorithm,
                ByteLength: 1,
                Sha256: new string('d', 64)
            )
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
}
