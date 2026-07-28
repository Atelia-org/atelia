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
        IReadOnlySet<SessionExecutionPhase> idleOrFailed =
            new HashSet<SessionExecutionPhase> {
                SessionExecutionPhase.Idle,
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
                idleOrFailed.Contains(phase),
                SessionOperationalSemantics
                    .IsIdleOrFailedPhase(phase)
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
                .IsIdleOrFailedPhase(unknown)
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
}
