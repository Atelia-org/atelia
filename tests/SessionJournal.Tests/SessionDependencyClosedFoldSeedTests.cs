using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionDependencyClosedFoldSeedTests {
    private static readonly EventAddress RuntimeSetup = Address(1);
    private static readonly EventAddress PromptSetup = Address(2);
    private static readonly EventAddress Head = Address(3);
    private static readonly EventAddress Next = Address(4);
    private static readonly SessionToolRuntimeIdentity ToolRuntimeIdentity =
        new("host", "implementations", "capabilities");

    [Theory]
    [InlineData(
        SessionExecutionPhase.Empty,
        SessionEventKind.SystemPromptSetup
    )]
    [InlineData(
        SessionExecutionPhase.Idle,
        SessionEventKind.SessionCreated
    )]
    [InlineData(
        SessionExecutionPhase.Idle,
        SessionEventKind.RuntimeConfigSetup
    )]
    [InlineData(
        SessionExecutionPhase.Idle,
        SessionEventKind.SystemPromptSetup
    )]
    [InlineData(
        SessionExecutionPhase.Idle,
        SessionEventKind.AgentActionProduced
    )]
    [InlineData(
        SessionExecutionPhase.Idle,
        SessionEventKind.ImportedAgentAction
    )]
    [InlineData(
        SessionExecutionPhase.AwaitingAgentAction,
        SessionEventKind.ObservationAccepted
    )]
    [InlineData(
        SessionExecutionPhase.AwaitingAgentAction,
        SessionEventKind.ToolResultObserved
    )]
    [InlineData(
        SessionExecutionPhase.TurnFailed,
        SessionEventKind.CompletionAttemptFailed
    )]
    public void Create_AcceptsDependencyClosedPhaseAndHeadMatrix(
        SessionExecutionPhase phase,
        SessionEventKind headKind
    ) {
        SessionExecutionRecovery recovery = Recovery(
            Head,
            new SessionExecutionState(
                phase,
                headKind,
                ToolExecutionSequenceCheckpoint: 7,
                ActiveCorrelationId:
                    phase
                        == SessionExecutionPhase.AwaitingAgentAction
                            ? "correlation"
                            : null
            )
        );

        SessionDependencyClosedFoldSeed seed =
            SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                recovery
            );

        Assert.Equal(Head, seed.Head);
        Assert.Equal(headKind, seed.HeadKind);
        Assert.Equal(phase, seed.Phase);
        Assert.Equal(7, seed.ToolExecutionSequenceCheckpoint);
        Assert.Equal(
            recovery.State.ActiveCorrelationId,
            seed.ActiveCorrelationId
        );
    }

    [Fact]
    public void Fold_EmptyPromptSeed_CompletesBootstrapAtSessionCreated() {
        SessionDependencyClosedFoldSeed seed =
            SessionDependencyClosedFoldSeed.Create(
                Setup(PromptSetup),
                Recovery(
                    PromptSetup,
                    new SessionExecutionState(
                        SessionExecutionPhase.Empty,
                        SessionEventKind.SystemPromptSetup
                    )
                )
            );
        DecodedSessionEvent created = Event(
            SessionEventKind.SessionCreated,
            new SessionCreatedBody(SessionCreationOrigin.Native),
            Head,
            PromptSetup
        );

        SessionTailContextProjection.TailFoldResult folded =
            SessionTailContextProjection.FoldSuffix(
                seed,
                [created]
            );

        Assert.Equal(Head, folded.GoverningSetup.Head);
        Assert.Equal(SessionExecutionPhase.Idle, folded.Phase);
        Assert.Equal(0, folded.ToolExecutionSequenceCheckpoint);
        Assert.Null(folded.ActiveCorrelationId);
        Assert.Empty(folded.Context);
    }

    [Fact]
    public void Fold_EmptyLineageBootstrapStartsFromCreatedIdleSeed() {
        SessionDependencyClosedFoldSeed seed =
            SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                Recovery(
                    Head,
                    new SessionExecutionState(
                        SessionExecutionPhase.Idle,
                        SessionEventKind.SessionCreated
                    )
                )
            );

        SessionTailContextProjection.TailFoldResult empty =
            SessionTailContextProjection.FoldSuffix(
                seed,
                Array.Empty<DecodedSessionEvent>()
            );
        DecodedSessionEvent observation = Event(
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("hello"),
            Next,
            Head
        );
        SessionTailContextProjection.TailFoldResult observed =
            SessionTailContextProjection.FoldSuffix(
                seed,
                [observation]
            );

        Assert.Equal(SessionExecutionPhase.Idle, empty.Phase);
        Assert.Equal(Head, empty.GoverningSetup.Head);
        Assert.Equal(
            SessionExecutionPhase.AwaitingAgentAction,
            observed.Phase
        );
        Assert.Equal(Next, observed.GoverningSetup.Head);
        Assert.Equal(
            $"atelia.session-journal.turn.v1:"
            + EventAddressTextCodec.Format(Next),
            observed.ActiveCorrelationId
        );
    }

    [Fact]
    public void Create_RejectsMissingOrMismatchedHeadFacts() {
        Assert.Throws<InvalidDataException>(
            () => SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                Recovery(
                    null,
                    new SessionExecutionState(
                        SessionExecutionPhase.Idle,
                        SessionEventKind.SessionCreated
                    )
                )
            )
        );
        Assert.Throws<InvalidDataException>(
            () => SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                Recovery(
                    Next,
                    new SessionExecutionState(
                        SessionExecutionPhase.Idle,
                        SessionEventKind.SessionCreated
                    )
                )
            )
        );
        Assert.Throws<InvalidDataException>(
            () => SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                Recovery(
                    Head,
                    new SessionExecutionState(
                        SessionExecutionPhase.Idle,
                        HeadKind: null
                    )
                )
            )
        );
    }

    [Theory]
    [InlineData(
        SessionExecutionPhase.Empty,
        SessionEventKind.RuntimeConfigSetup
    )]
    [InlineData(
        SessionExecutionPhase.Empty,
        SessionEventKind.SessionCreated
    )]
    [InlineData(
        SessionExecutionPhase.Idle,
        SessionEventKind.ObservationAccepted
    )]
    [InlineData(
        SessionExecutionPhase.AwaitingAgentAction,
        SessionEventKind.SessionCreated
    )]
    [InlineData(
        SessionExecutionPhase.TurnFailed,
        SessionEventKind.ToolResultObserved
    )]
    [InlineData(
        SessionExecutionPhase.AwaitingCompletionDispatch,
        SessionEventKind.CompletionRequestPrepared
    )]
    [InlineData(
        SessionExecutionPhase.AwaitingCompletion,
        SessionEventKind.CompletionAttemptStarted
    )]
    [InlineData(
        SessionExecutionPhase.AwaitingToolExecution,
        SessionEventKind.ToolExecutionStarted
    )]
    public void Create_RejectsInvalidOrOperationalPhaseAndHeadMatrix(
        SessionExecutionPhase phase,
        SessionEventKind headKind
    ) {
        Assert.Throws<InvalidDataException>(
            () => SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                Recovery(
                    Head,
                    new SessionExecutionState(
                        phase,
                        headKind,
                        ActiveCorrelationId:
                            phase
                                == SessionExecutionPhase
                                    .AwaitingAgentAction
                                    ? "correlation"
                                    : null
                    )
                )
            )
        );
    }

    [Fact]
    public void Create_RejectsNegativeCheckpointAndCorrelationDrift() {
        Assert.Throws<InvalidDataException>(
            () => SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                Recovery(
                    Head,
                    new SessionExecutionState(
                        SessionExecutionPhase.Idle,
                        SessionEventKind.SessionCreated,
                        ToolExecutionSequenceCheckpoint: -1
                    )
                )
            )
        );
        Assert.Throws<InvalidDataException>(
            () => SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                Recovery(
                    Head,
                    new SessionExecutionState(
                        SessionExecutionPhase.AwaitingAgentAction,
                        SessionEventKind.ObservationAccepted
                    )
                )
            )
        );
        Assert.Throws<InvalidDataException>(
            () => SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                Recovery(
                    Head,
                    new SessionExecutionState(
                        SessionExecutionPhase.AwaitingAgentAction,
                        SessionEventKind.ObservationAccepted,
                        ActiveCorrelationId: " "
                    )
                )
            )
        );
    }

    [Theory]
    [InlineData(
        SessionExecutionPhase.Empty,
        SessionEventKind.SystemPromptSetup
    )]
    [InlineData(
        SessionExecutionPhase.Idle,
        SessionEventKind.SessionCreated
    )]
    [InlineData(
        SessionExecutionPhase.TurnFailed,
        SessionEventKind.CompletionAttemptFailed
    )]
    public void Create_RejectsStaleCorrelationOutsideAwaitingAgentAction(
        SessionExecutionPhase phase,
        SessionEventKind headKind
    ) {
        Assert.Throws<InvalidDataException>(
            () => SessionDependencyClosedFoldSeed.Create(
                Setup(Head),
                Recovery(
                    Head,
                    new SessionExecutionState(
                        phase,
                        headKind,
                        ActiveCorrelationId: "stale"
                    )
                )
            )
        );
    }

    [Fact]
    public void Create_RejectsEveryPendingOperationalField() {
        SessionExecutionState baseline = new(
            SessionExecutionPhase.Idle,
            SessionEventKind.SessionCreated
        );
        SessionExecutionState[] pendingStates = [
            baseline with {
                PendingToolCall =
                    new RawToolCall("tool", "call", "{}")
            },
            baseline with {
                PendingOperationId = "operation"
            },
            baseline with {
                PendingToolExecutionStarted = true
            },
            baseline with {
                PendingRequestPreparedAddress = Next
            },
            baseline with {
                ActiveCompletionAttemptAddress = Next
            },
            baseline with {
                PendingToolRuntimeIdentity = ToolRuntimeIdentity
            }
        ];

        foreach (SessionExecutionState state in pendingStates) {
            Assert.Throws<InvalidDataException>(
                () => SessionDependencyClosedFoldSeed.Create(
                    Setup(Head),
                    Recovery(Head, state)
                )
            );
        }
    }

    private static SessionGoverningSetup Setup(EventAddress head) =>
        new(
            head,
            RuntimeSetup,
            new SessionRuntimeConfiguration(
                "model",
                "surface",
                SessionJournalDefaults.Schema,
                new(0)
            ),
            PromptSetup,
            "system"
        );

    private static SessionExecutionRecovery Recovery(
        EventAddress? head,
        SessionExecutionState state
    ) => new(
        head,
        state,
        new SessionExecutionRecoveryBoundary(
            SourcePrepared: null,
            SourceAction: null,
            SourceObservation: null,
            LatestExecutionCheckpoint: null
        ),
        default
    );

    private static DecodedSessionEvent Event(
        SessionEventKind kind,
        object body,
        EventAddress address,
        EventAddress parent
    ) => new(
        kind,
        SessionEventCodec.GetExpectedBodySchemaVersion(kind),
        body,
        address,
        parent
    );

    private static EventAddress Address(ulong ticket) =>
        EventAddressTextCodec.Parse(
            $"ej1:{ticket:x16}0000000100000000"
        );
}
