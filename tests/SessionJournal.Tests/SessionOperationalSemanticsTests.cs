using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionOperationalSemanticsTests {
    [Fact]
    public void KindClassifications_AreExactAndDisjoint() {
        IReadOnlySet<SessionEventKind> setupKinds =
            new HashSet<SessionEventKind> {
                SessionEventKind.RuntimeConfigSetup,
                SessionEventKind.SystemPromptSetup
            };
        IReadOnlySet<SessionEventKind> actionKinds =
            new HashSet<SessionEventKind> {
                SessionEventKind.AgentActionProduced,
                SessionEventKind.ImportedAgentAction
            };
        IReadOnlySet<SessionEventKind> toolSegmentKinds =
            new HashSet<SessionEventKind> {
                SessionEventKind.ToolExecutionStarted,
                SessionEventKind.ToolResultObserved
            };

        foreach (SessionEventKind kind
                 in Enum.GetValues<SessionEventKind>()) {
            Assert.Equal(
                setupKinds.Contains(kind),
                SessionOperationalSemantics.IsSetupKind(kind)
            );
            Assert.Equal(
                actionKinds.Contains(kind),
                SessionOperationalSemantics.IsActionKind(kind)
            );
            Assert.Equal(
                toolSegmentKinds.Contains(kind),
                SessionOperationalSemantics
                    .IsToolSegmentKind(kind)
            );
        }
        var unknown = (SessionEventKind)uint.MaxValue;
        Assert.False(
            SessionOperationalSemantics.IsSetupKind(unknown)
        );
        Assert.False(
            SessionOperationalSemantics.IsActionKind(unknown)
        );
        Assert.False(
            SessionOperationalSemantics.IsToolSegmentKind(unknown)
        );
    }

    [Fact]
    public void PhaseClassifications_AreExact() {
        IReadOnlySet<SessionExecutionPhase> replaySafe =
            new HashSet<SessionExecutionPhase> {
                SessionExecutionPhase.Empty,
                SessionExecutionPhase.Idle,
                SessionExecutionPhase.AwaitingAgentAction,
                SessionExecutionPhase.TurnFailed
            };
        IReadOnlySet<SessionExecutionPhase> preparedOrAttempt =
            new HashSet<SessionExecutionPhase> {
                SessionExecutionPhase.AwaitingCompletionDispatch,
                SessionExecutionPhase.AwaitingCompletion
            };

        foreach (SessionExecutionPhase phase
                 in Enum.GetValues<SessionExecutionPhase>()) {
            Assert.Equal(
                replaySafe.Contains(phase),
                SessionOperationalSemantics
                    .IsReplaySafePhase(phase)
            );
            Assert.Equal(
                preparedOrAttempt.Contains(phase),
                SessionOperationalSemantics
                    .IsPreparedOrAttemptPhase(phase)
            );
        }
        var unknown = (SessionExecutionPhase)int.MaxValue;
        Assert.False(
            SessionOperationalSemantics
                .IsReplaySafePhase(unknown)
        );
        Assert.False(
            SessionOperationalSemantics
                .IsPreparedOrAttemptPhase(unknown)
        );
    }

    [Fact]
    public void ObservationCorrelationIdentity_UsesCanonicalAddressText() {
        EventAddress observation = EventAddressTextCodec.Parse(
            "ej1:000000000000002a0000000100000000"
        );

        string correlationId =
            SessionOperationalSemantics
                .BuildObservationCorrelationId(observation);

        Assert.Equal(
            "atelia.session-journal.turn.v1:"
            + EventAddressTextCodec.Format(observation),
            correlationId
        );
    }

    [Theory]
    [InlineData("", "tool", "{}")]
    [InlineData("call", "", "{}")]
    [InlineData("call", "tool", "")]
    [InlineData(" ", "tool", "{}")]
    public void ActionDeclarations_ReportInvalidIdentityCode(
        string callId,
        string toolName,
        string rawArguments
    ) {
        ActionMessage action = ToolAction(
            new RawToolCall(toolName, callId, rawArguments)
        );

        SessionOperationalViolation? violation =
            SessionOperationalSemantics
                .ValidateActionToolDeclarations(action);

        Assert.Equal(
            SessionOperationalViolation.InvalidToolCallIdentity,
            violation
        );
    }

    [Fact]
    public void ActionDeclarations_ReportDuplicateCallIdCode() {
        ActionMessage action = ToolAction(
            new RawToolCall("first", "duplicate", "{}"),
            new RawToolCall("second", "duplicate", "{\"x\":1}")
        );

        SessionOperationalViolation? violation =
            SessionOperationalSemantics
                .ValidateActionToolDeclarations(action);

        Assert.Equal(
            SessionOperationalViolation.DuplicateToolCallId,
            violation
        );
    }

    [Fact]
    public void ActionRuntimeIdentityShape_ReportsPresenceCodes() {
        ActionMessage terminal = new([
            new ActionBlock.Text("done")
        ]);
        ActionMessage tool = ToolAction(
            new RawToolCall("tool", "call", "{}")
        );
        SessionToolRuntimeIdentity identity = Identity("host");

        Assert.Null(
            SessionOperationalSemantics
                .ValidateRequiredToolRuntimeIdentity(
                    terminal,
                    runtimeIdentity: null
                )
        );
        Assert.Null(
            SessionOperationalSemantics
                .ValidateRequiredToolRuntimeIdentity(
                    terminal,
                    identity
                )
        );
        Assert.Equal(
            SessionOperationalViolation.MissingToolRuntimeIdentity,
            SessionOperationalSemantics
                .ValidateRequiredToolRuntimeIdentity(
                    tool,
                    runtimeIdentity: null
                )
        );
        Assert.Null(
            SessionOperationalSemantics
                .ValidateRequiredToolRuntimeIdentity(
                    tool,
                    identity
                )
        );
        Assert.Equal(
            SessionOperationalViolation
                .UnexpectedToolRuntimeIdentity,
            SessionOperationalSemantics
                .ValidateUnexpectedToolRuntimeIdentity(
                    terminal,
                    identity
                )
        );
        Assert.Null(
            SessionOperationalSemantics
                .ValidateUnexpectedToolRuntimeIdentity(
                    terminal,
                    runtimeIdentity: null
                )
        );
        Assert.Null(
            SessionOperationalSemantics
                .ValidateUnexpectedToolRuntimeIdentity(
                    tool,
                    identity
                )
        );
    }

    [Fact]
    public void PendingCallMatch_ReportsExactMismatchCode() {
        var pending = new RawToolCall(
            "lookup",
            "call-1",
            "{\"query\":\"one\"}"
        );

        Assert.Equal(
            SessionOperationalViolation.PendingToolCallIdMismatch,
            SessionOperationalSemantics
                .ValidatePendingToolCallMatch(
                    pending,
                    "call-2",
                    "lookup",
                    "{\"query\":\"one\"}"
                )
        );
        Assert.Equal(
            SessionOperationalViolation.PendingToolNameMismatch,
            SessionOperationalSemantics
                .ValidatePendingToolCallMatch(
                    pending,
                    "call-1",
                    "search",
                    "{\"query\":\"one\"}"
                )
        );
        Assert.Equal(
            SessionOperationalViolation.PendingToolArgumentsMismatch,
            SessionOperationalSemantics
                .ValidatePendingToolCallMatch(
                    pending,
                    "call-1",
                    "lookup",
                    "{\"query\":\"two\"}"
                )
        );
        Assert.Null(
            SessionOperationalSemantics
                .ValidatePendingToolCallMatch(
                    pending,
                    "call-1",
                    "lookup",
                    "{\"query\":\"one\"}"
                )
        );
        Assert.Null(
            SessionOperationalSemantics
                .ValidatePendingToolCallMatch(
                    pending,
                    "call-1",
                    "lookup",
                    rawArgumentsJson: null
                )
        );
    }

    [Fact]
    public void RuntimeAndSequenceValidators_ReportStableCodes() {
        SessionToolRuntimeIdentity expected = Identity("expected");
        SessionToolRuntimeIdentity actual = Identity("actual");

        Assert.Null(
            SessionOperationalSemantics
                .ValidateToolRuntimeIdentityMatch(
                    expected,
                    expected
                )
        );
        Assert.Equal(
            SessionOperationalViolation.ToolRuntimeIdentityMismatch,
            SessionOperationalSemantics
                .ValidateToolRuntimeIdentityMatch(expected, actual)
        );
        Assert.Null(
            SessionOperationalSemantics
                .ValidateReservedStartSequence(4, 5)
        );
        Assert.Equal(
            SessionOperationalViolation
                .ReservedStartSequenceMismatch,
            SessionOperationalSemantics
                .ValidateReservedStartSequence(4, 6)
        );
        Assert.Throws<OverflowException>(() =>
            SessionOperationalSemantics
                .ValidateReservedStartSequence(
                    long.MaxValue,
                    long.MinValue
                )
        );
        Assert.Null(
            SessionOperationalSemantics
                .ValidateReservedResultSequence(5, 5)
        );
        Assert.Equal(
            SessionOperationalViolation
                .ReservedResultSequenceMismatch,
            SessionOperationalSemantics
                .ValidateReservedResultSequence(5, 4)
        );
    }

    [Fact]
    public void NextPendingCall_FollowsDeclarationOrder() {
        RawToolCall first = new("first", "call-1", "{}");
        RawToolCall second = new("second", "call-2", "{}");
        ActionMessage action = ToolAction(first, second);
        var observed =
            new Dictionary<string, ToolResultObservedBody>(
                StringComparer.Ordinal
            );
        var secondOnly =
            new Dictionary<string, ToolResultObservedBody>(
                StringComparer.Ordinal
            ) {
                [second.ToolCallId] = Result(second, 2)
            };

        Assert.Equal(
            first,
            SessionOperationalSemantics
                .SelectNextPendingDeclaredCall(action, observed)
        );
        Assert.Equal(
            first,
            SessionOperationalSemantics
                .SelectNextPendingDeclaredCall(
                    action,
                    secondOnly
                )
        );
        observed.Add(
            first.ToolCallId,
            Result(first, 1)
        );
        Assert.Equal(
            second,
            SessionOperationalSemantics
                .SelectNextPendingDeclaredCall(action, observed)
        );
        observed.Add(
            second.ToolCallId,
            Result(second, 2)
        );
        Assert.Null(
            SessionOperationalSemantics
                .SelectNextPendingDeclaredCall(action, observed)
        );
    }

    private static ActionMessage ToolAction(
        params RawToolCall[] calls
    ) => new([
        .. calls.Select(
            static call => new ActionBlock.ToolCall(call)
        )
    ]);

    private static SessionToolRuntimeIdentity Identity(string host) =>
        new(host, "implementations", "capabilities");

    private static ToolResultObservedBody Result(
        RawToolCall call,
        long sequence
    ) => new(
        call.ToolCallId,
        call.ToolName,
        sequence,
        ToolExecutionStatus.Success,
        Array.Empty<ToolResultBlock>()
    );
}
