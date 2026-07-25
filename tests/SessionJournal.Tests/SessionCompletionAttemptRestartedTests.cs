using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionCompletionAttemptRestartedTests {
    private const string SampleAddressText = "ej1:00000000000000010000000100000000";

    [Fact]
    public void CompletionAttemptRestarted_RoundtripsCanonicalPayload() {
        EventAddress sourcePreparedAddress = EventAddressTextCodec.Parse(SampleAddressText);
        var body = new CompletionAttemptRestartedBody(
            "attempt-2",
            "attempt-1",
            sourcePreparedAddress
        );

        byte[] encoded = SessionEventCodec.Encode(SessionEventKind.CompletionAttemptRestarted, body);
        var decoded = Assert.IsType<CompletionAttemptRestartedBody>(
            SessionEventCodec.Decode(SessionEventKind.CompletionAttemptRestarted, encoded, out int version)
        );

        Assert.Equal(
            "{\"v\":1,\"body\":{\"attemptId\":\"attempt-2\",\"replacesAttemptId\":\"attempt-1\",\"sourcePreparedAddress\":\""
                + SampleAddressText
                + "\"}}",
            Encoding.UTF8.GetString(encoded)
        );
        Assert.Equal(1, version);
        Assert.Equal(body, decoded);
        Assert.Equal(encoded, SessionEventCodec.Encode(SessionEventKind.CompletionAttemptRestarted, decoded));
    }

    [Theory]
    [InlineData("""{"v":1,"unknown":true,"body":{"attemptId":"a2","replacesAttemptId":"a1","sourcePreparedAddress":"ej1:00000000000000010000000100000000"}}""")]
    [InlineData("""{"v":1,"body":{"attemptId":"a2","attemptId":"a3","replacesAttemptId":"a1","sourcePreparedAddress":"ej1:00000000000000010000000100000000"}}""")]
    [InlineData("""{"v":1,"body":{"attemptId":"a2","replacesAttemptId":"a1","sourcePreparedAddress":"ej1:00000000000000010000000100000000","unknown":true}}""")]
    [InlineData("""{"v":1,"body":{"attemptId":"same","replacesAttemptId":"same","sourcePreparedAddress":"ej1:00000000000000010000000100000000"}}""")]
    [InlineData("""{"v":1,"body":{"attemptId":" ","replacesAttemptId":"a1","sourcePreparedAddress":"ej1:00000000000000010000000100000000"}}""")]
    [InlineData("""{"v":1,"body":{"attemptId":"a2","replacesAttemptId":"a1","sourcePreparedAddress":"not-an-address"}}""")]
    public void CompletionAttemptRestarted_StrictDecodeRejectsInvalidPayload(string json) {
        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.CompletionAttemptRestarted,
            Encoding.UTF8.GetBytes(json),
            out _
        ));
    }

    [Fact]
    public void CompletionAttemptRestarted_EncodeRejectsInvalidAttemptIdentityOrSource() {
        EventAddress source = EventAddressTextCodec.Parse(SampleAddressText);

        Assert.Throws<ArgumentException>(() => SessionEventCodec.Encode(
            SessionEventKind.CompletionAttemptRestarted,
            new CompletionAttemptRestartedBody("same", "same", source)
        ));
        Assert.Throws<ArgumentException>(() => SessionEventCodec.Encode(
            SessionEventKind.CompletionAttemptRestarted,
            new CompletionAttemptRestartedBody("attempt-2", "attempt-1", default)
        ));
    }

    [Fact]
    public void Reducer_RestartsAdvanceOnlyActiveAttemptAndActionDescendsFromLatestAttempt() {
        IReadOnlyList<DecodedSessionEvent> prefix = CreatePreparedPrefix(out EventAddress sourcePrepared);
        EventAddress restart1 = Address(6);
        EventAddress restart2 = Address(7);
        EventAddress action = Address(8);
        var throughRestart2 = prefix.Concat([
            Event(
                SessionEventKind.CompletionAttemptRestarted,
                new CompletionAttemptRestartedBody("attempt-2", "attempt-1", sourcePrepared),
                restart1,
                sourcePrepared
            ),
            Event(
                SessionEventKind.CompletionAttemptRestarted,
                new CompletionAttemptRestartedBody("attempt-3", "attempt-2", sourcePrepared),
                restart2,
                restart1
            )
        ]).ToArray();

        SessionProjection awaiting = SessionReducer.Reduce(throughRestart2);

        Assert.Equal(SessionExecutionPhase.AwaitingCompletion, awaiting.ExecutionState.Phase);
        Assert.Equal(SessionEventKind.CompletionAttemptRestarted, awaiting.ExecutionState.HeadKind);
        Assert.Equal(sourcePrepared, awaiting.ExecutionState.PendingRequestPreparedAddress);
        Assert.Equal(restart2, awaiting.ExecutionState.ActiveCompletionAttemptAddress);
        Assert.Equal("attempt-3", awaiting.ExecutionState.PendingCompletionAttemptId);
        Assert.Equal(
            $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(Address(4))}",
            awaiting.ExecutionState.ActiveCorrelationId
        );
        Assert.Single(awaiting.Context);
        Assert.Equal("runtime-model", awaiting.Config?.ModelId);
        Assert.Equal("system prompt", awaiting.SystemPrompt);

        SessionProjection completed = SessionReducer.Reduce(throughRestart2.Append(
            Event(
                SessionEventKind.AgentActionProduced,
                new AgentActionProducedBody(
                    new ActionMessage([new ActionBlock.Text("done")]),
                    new CompletionDescriptor("provider", "api", "runtime-model")
                ),
                action,
                restart2
            )
        ).ToArray());

        Assert.Equal(SessionExecutionPhase.Idle, completed.ExecutionState.Phase);
        Assert.Null(completed.ExecutionState.PendingRequestPreparedAddress);
        Assert.Null(completed.ExecutionState.ActiveCompletionAttemptAddress);
        Assert.Null(completed.ExecutionState.PendingCompletionAttemptId);
        Assert.Null(completed.ExecutionState.ActiveCorrelationId);
        Assert.Collection(
            completed.Context,
            message => Assert.IsType<ObservationMessage>(message),
            message => Assert.IsType<ActionMessage>(message)
        );
    }

    [Fact]
    public void Reducer_FailureDescendsFromLatestAttemptAndMatchesItsId() {
        IReadOnlyList<DecodedSessionEvent> prefix = CreatePreparedPrefix(out EventAddress sourcePrepared);
        EventAddress restart = Address(6);
        EventAddress failure = Address(7);
        var events = prefix.Concat([
            Event(
                SessionEventKind.CompletionAttemptRestarted,
                new CompletionAttemptRestartedBody("attempt-2", "attempt-1", sourcePrepared),
                restart,
                sourcePrepared
            ),
            Event(
                SessionEventKind.CompletionAttemptFailed,
                new CompletionAttemptFailedBody(
                    "attempt-2",
                    CompletionTerminationKind.Failed,
                    null,
                    null,
                    Array.AsReadOnly(Array.Empty<string>())
                ),
                failure,
                restart
            )
        ]).ToArray();

        SessionProjection projection = SessionReducer.Reduce(events);

        Assert.Equal(SessionExecutionPhase.TurnFailed, projection.ExecutionState.Phase);
        Assert.Null(projection.ExecutionState.PendingRequestPreparedAddress);
        Assert.Null(projection.ExecutionState.ActiveCompletionAttemptAddress);
        Assert.Null(projection.ExecutionState.PendingCompletionAttemptId);
    }

    [Theory]
    [InlineData("wrong-parent")]
    [InlineData("wrong-source")]
    [InlineData("wrong-replaces")]
    [InlineData("same-id")]
    public void Reducer_RejectsRestartThatDoesNotContinueActiveAttempt(string mutation) {
        IReadOnlyList<DecodedSessionEvent> prefix = CreatePreparedPrefix(out EventAddress sourcePrepared);
        EventAddress parent = sourcePrepared;
        string attemptId = "attempt-2";
        string replacesAttemptId = "attempt-1";
        EventAddress source = sourcePrepared;
        switch (mutation) {
            case "wrong-parent":
                parent = Address(4);
                break;
            case "wrong-source":
                source = Address(2);
                break;
            case "wrong-replaces":
                replacesAttemptId = "not-active";
                break;
            case "same-id":
                attemptId = replacesAttemptId;
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation '{mutation}'.");
        }

        var restarted = Event(
            SessionEventKind.CompletionAttemptRestarted,
            new CompletionAttemptRestartedBody(attemptId, replacesAttemptId, source),
            Address(6),
            parent
        );

        Assert.Throws<InvalidDataException>(() => SessionReducer.Reduce(prefix.Append(restarted).ToArray()));
    }

    [Theory]
    [InlineData(SessionEventKind.AgentActionProduced)]
    [InlineData(SessionEventKind.CompletionAttemptFailed)]
    public void Reducer_RejectsTerminalEventThatBypassesActiveRestart(SessionEventKind terminalKind) {
        IReadOnlyList<DecodedSessionEvent> prefix = CreatePreparedPrefix(out EventAddress sourcePrepared);
        EventAddress restart = Address(6);
        var restarted = Event(
            SessionEventKind.CompletionAttemptRestarted,
            new CompletionAttemptRestartedBody("attempt-2", "attempt-1", sourcePrepared),
            restart,
            sourcePrepared
        );
        object terminalBody = terminalKind switch {
            SessionEventKind.AgentActionProduced => new AgentActionProducedBody(
                new ActionMessage([new ActionBlock.Text("done")]),
                new CompletionDescriptor("provider", "api", "runtime-model")
            ),
            SessionEventKind.CompletionAttemptFailed => new CompletionAttemptFailedBody(
                "attempt-2",
                CompletionTerminationKind.Failed,
                null,
                null,
                Array.AsReadOnly(Array.Empty<string>())
            ),
            _ => throw new InvalidOperationException()
        };
        var terminal = Event(terminalKind, terminalBody, Address(7), sourcePrepared);

        Assert.Throws<InvalidDataException>(() => SessionReducer.Reduce(
            prefix.Append(restarted).Append(terminal).ToArray()
        ));
    }

    [Fact]
    public void Reducer_RejectsRestartWithoutPreparedSource() {
        IReadOnlyList<DecodedSessionEvent> prefix = CreatePreparedPrefix(out EventAddress sourcePrepared);
        var restarted = Event(
            SessionEventKind.CompletionAttemptRestarted,
            new CompletionAttemptRestartedBody("attempt-2", "attempt-1", sourcePrepared),
            Address(6),
            Address(4)
        );

        Assert.Throws<InvalidDataException>(() => SessionReducer.Reduce(
            prefix.Take(prefix.Count - 1).Append(restarted).ToArray()
        ));
    }

    [Fact]
    public void Reducer_RejectsAttemptIdReusedFromEarlierInActiveChain() {
        IReadOnlyList<DecodedSessionEvent> prefix = CreatePreparedPrefix(
            out EventAddress sourcePrepared
        );
        EventAddress restart1 = Address(6);
        var events = prefix.Concat([
            Event(
                SessionEventKind.CompletionAttemptRestarted,
                new CompletionAttemptRestartedBody(
                    "attempt-2",
                    "attempt-1",
                    sourcePrepared
                ),
                restart1,
                sourcePrepared
            ),
            Event(
                SessionEventKind.CompletionAttemptRestarted,
                new CompletionAttemptRestartedBody(
                    "attempt-1",
                    "attempt-2",
                    sourcePrepared
                ),
                Address(7),
                restart1
            )
        ]).ToArray();

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => SessionReducer.Reduce(events)
        );
        Assert.Contains("entire active attempt chain", error.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<DecodedSessionEvent> CreatePreparedPrefix(out EventAddress sourcePrepared) {
        EventAddress runtime = Address(1);
        EventAddress prompt = Address(2);
        EventAddress created = Address(3);
        EventAddress observation = Address(4);
        sourcePrepared = Address(5);
        string correlation = $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}";

        return [
            Event(
                SessionEventKind.RuntimeConfigSetup,
                new SessionRuntimeConfiguration("runtime-model", "surface", SessionJournalDefaults.Schema),
                runtime,
                null
            ),
            Event(
                SessionEventKind.SystemPromptSetup,
                new SystemPromptSetupBody("system prompt"),
                prompt,
                runtime
            ),
            Event(SessionEventKind.SessionCreated, new SessionCreatedBody(), created, prompt),
            Event(
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("observe"),
                observation,
                created
            ),
            Event(
                SessionEventKind.CompletionRequestPrepared,
                CreatePreparedBody("attempt-1", correlation, runtime, prompt),
                sourcePrepared,
                observation
            )
        ];
    }

    private static CompletionRequestPreparedBody CreatePreparedBody(
        string attemptId,
        string correlationId,
        EventAddress runtime,
        EventAddress prompt
    ) {
        ImmutableArray<ToolDefinition> tools = ImmutableArray<ToolDefinition>.Empty;
        return new CompletionRequestPreparedBody(
            new SessionRequestAttempt(attemptId, correlationId, "observation", null),
            new SessionContextPlan(
                SessionRequestManifestDefaults.FullRawSelectionPolicyId,
                SessionRequestManifestDefaults.FullRawPlannerFingerprint,
                null,
                new string('a', 64),
                [],
                [],
                SessionRequestManifestDefaults.FullRawRenderingProfileId,
                "runtime-model",
                1,
                "observation"
            ),
            new SessionGoverningSetupReferences(
                new SessionSetupReference(runtime, 1, new string('b', 64)),
                new SessionSetupReference(prompt, 1, new string('c', 64))
            ),
            new SessionRequestParameters("runtime-model", null),
            new SessionRequestToolSet(
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestCanonicalizer.ComputeToolSetSha256(tools),
                tools
            ),
            new SessionRequestRendering(
                SessionRequestManifestDefaults.FullRawContextRendererId,
                SessionRequestManifestDefaults.FullRawContextRendererFingerprint,
                SessionRequestManifestDefaults.CanonicalRequestCodecId,
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestManifestDefaults.ReasoningCodecSetFingerprint
            ),
            new SessionRequestTarget(
                new SessionCompletionTargetIdentity("connection", "kind", "fingerprint", "adapter"),
                "surface",
                "client",
                "api"
            ),
            new SessionRequestCommitment(
                SessionRequestManifestDefaults.CommitmentAlgorithm,
                1,
                new string('d', 64)
            )
        );
    }

    private static DecodedSessionEvent Event(
        SessionEventKind kind,
        object body,
        EventAddress address,
        EventAddress? parent
    ) => new(kind, 1, body, address, parent);

    private static EventAddress Address(int ticket)
        => EventAddressTextCodec.Parse($"ej1:{ticket:x16}0000000100000000");
}
