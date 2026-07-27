using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionCompletionAttemptStartedTests {
    [Fact]
    public void Codec_RoundtripsStrictEmptyBody() {
        var body = new CompletionAttemptStartedBody();

        byte[] encoded = SessionEventCodec.Encode(
            SessionEventKind.CompletionAttemptStarted,
            body
        );
        var decoded = Assert.IsType<CompletionAttemptStartedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.CompletionAttemptStarted,
                encoded,
                out int version
            )
        );

        Assert.Equal("""{"v":1,"body":{}}""", Encoding.UTF8.GetString(encoded));
        Assert.Equal(1, version);
        Assert.Equal(body, decoded);
    }

    [Theory]
    [InlineData("""{"v":1,"unknown":true,"body":{}}""")]
    [InlineData("""{"v":1,"body":{"attemptId":"opaque"}}""")]
    public void Codec_RejectsNonEmptyOrNonExactPayload(string json) {
        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.CompletionAttemptStarted,
            Encoding.UTF8.GetBytes(json),
            out _
        ));
    }

    [Fact]
    public void Reducer_PreparedAndStartedHaveDistinctPhasesAndAddressIdentity() {
        IReadOnlyList<DecodedSessionEvent> preparedEvents =
            CreatePreparedPrefix(out EventAddress prepared);

        SessionExecutionState preparedState =
            SessionReducer.Reduce(preparedEvents).ExecutionState;
        Assert.Equal(
            SessionExecutionPhase.AwaitingCompletionDispatch,
            preparedState.Phase
        );
        Assert.Equal(prepared, preparedState.PendingRequestPreparedAddress);
        Assert.Null(preparedState.ActiveCompletionAttemptAddress);

        EventAddress started1 = Address(6);
        EventAddress started2 = Address(7);
        SessionExecutionState startedState = SessionReducer.Reduce(
            preparedEvents.Concat([
                Event(
                    SessionEventKind.CompletionAttemptStarted,
                    new CompletionAttemptStartedBody(),
                    started1,
                    prepared
                ),
                Event(
                    SessionEventKind.CompletionAttemptStarted,
                    new CompletionAttemptStartedBody(),
                    started2,
                    started1
                )
            ]).ToArray()
        ).ExecutionState;

        Assert.Equal(SessionExecutionPhase.AwaitingCompletion, startedState.Phase);
        Assert.Equal(prepared, startedState.PendingRequestPreparedAddress);
        Assert.Equal(started2, startedState.ActiveCompletionAttemptAddress);
    }

    [Theory]
    [InlineData(SessionEventKind.AgentActionProduced)]
    [InlineData(SessionEventKind.CompletionAttemptFailed)]
    public void Reducer_RejectsTerminalThatBypassesLatestStarted(
        SessionEventKind terminalKind
    ) {
        IReadOnlyList<DecodedSessionEvent> prefix =
            CreatePreparedPrefix(out EventAddress prepared);
        EventAddress started = Address(6);
        object terminalBody = terminalKind switch {
            SessionEventKind.AgentActionProduced => new AgentActionProducedBody(
                new ActionMessage([new ActionBlock.Text("done")]),
                new CompletionDescriptor("provider", "api", "runtime-model"),
                Correlation(Address(4)),
                new SessionExecutionCheckpoint(0),
                ToolRuntimeIdentity: null
            ),
            SessionEventKind.CompletionAttemptFailed =>
                new CompletionAttemptFailedBody(
                    CompletionTerminationKind.Failed,
                    null,
                    null,
                    Array.AsReadOnly(Array.Empty<string>())
                ),
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<InvalidDataException>(() => SessionReducer.Reduce(
            prefix.Concat([
                Event(
                    SessionEventKind.CompletionAttemptStarted,
                    new CompletionAttemptStartedBody(),
                    started,
                    prepared
                ),
                Event(terminalKind, terminalBody, Address(7), prepared)
            ]).ToArray()
        ));
    }

    [Fact]
    public void Reducer_RejectsStartedWithoutPrepared() {
        IReadOnlyList<DecodedSessionEvent> prefix =
            CreatePreparedPrefix(out _);
        Assert.Throws<InvalidDataException>(() => SessionReducer.Reduce(
            prefix.Take(prefix.Count - 1).Append(
                Event(
                    SessionEventKind.CompletionAttemptStarted,
                    new CompletionAttemptStartedBody(),
                    Address(6),
                    Address(4)
                )
            ).ToArray()
        ));
    }

    private static IReadOnlyList<DecodedSessionEvent> CreatePreparedPrefix(
        out EventAddress prepared
    ) {
        EventAddress runtime = Address(1);
        EventAddress prompt = Address(2);
        EventAddress created = Address(3);
        EventAddress observation = Address(4);
        prepared = Address(5);
        return [
            Event(
                SessionEventKind.RuntimeConfigSetup,
                new SessionRuntimeConfiguration(
                    "runtime-model",
                    "surface",
                    SessionJournalDefaults.Schema
                ),
                runtime,
                null
            ),
            Event(
                SessionEventKind.SystemPromptSetup,
                new SystemPromptSetupBody("system prompt"),
                prompt,
                runtime
            ),
            Event(
                SessionEventKind.SessionCreated,
                new SessionCreatedBody(),
                created,
                prompt
            ),
            Event(
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("observe"),
                observation,
                created
            ),
            Event(
                SessionEventKind.CompletionRequestPrepared,
                CreatePreparedBody(Correlation(observation), runtime, prompt),
                prepared,
                observation
            )
        ];
    }

    private static CompletionRequestPreparedBody CreatePreparedBody(
        string correlationId,
        EventAddress runtime,
        EventAddress prompt
    ) {
        ImmutableArray<ToolDefinition> tools =
            ImmutableArray<ToolDefinition>.Empty;
        return PreparedV3Fixture.Create(
            correlationId,
            "observation",
            runtime,
            prompt,
            runtime,
            prompt,
            "runtime-model",
            tools,
            toolRuntimeIdentity: null
        );
    }

    private static DecodedSessionEvent Event(
        SessionEventKind kind,
        object body,
        EventAddress address,
        EventAddress? parent
    ) => new(
        kind,
        SessionEventCodec.GetExpectedBodySchemaVersion(kind),
        body,
        address,
        parent
    );

    private static string Correlation(EventAddress observation)
        => $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}";

    private static EventAddress Address(int ticket)
        => EventAddressTextCodec.Parse($"ej1:{ticket:x16}0000000100000000");
}
