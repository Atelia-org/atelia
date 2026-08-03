using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Reconstructs only the durable execution state at an exact raw head. This resolver
/// never materializes conversation context, reads artifacts, or asks EventJournal for
/// a chronological chain.
/// </summary>
internal static class SessionExecutionTailResolver {
    internal sealed record PreparedAttemptChain(
        EventAddress SourcePreparedAddress,
        EventAddress? SourcePreparedParent,
        EventAddress? ActiveAttemptAddress,
        CompletionRequestPreparedBody SourceManifest
    );

    public static SessionExecutionRecovery Resolve(
        SessionJournalEventReader reader,
        EventAddress? head,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        if (head is null) {
            return new SessionExecutionRecovery(
                Head: null,
                new SessionExecutionState(SessionExecutionPhase.Empty, HeadKind: null),
                EmptyBoundary,
                default
            );
        }

        var resolver = new Resolver(reader, cancellationToken);
        SessionExecutionRecovery resolved = resolver.ResolveHead(head.Value);
        return resolved with {
            Diagnostics = new SessionExecutionRecoveryDiagnostics(
                resolver.HeaderReadCount,
                resolver.PayloadReadCount
            )
        };
    }

    private static SessionExecutionRecoveryBoundary EmptyBoundary { get; } = new(
        SourcePrepared: null,
        SourceAction: null,
        SourceObservation: null,
        LatestExecutionCheckpoint: null
    );

    private sealed class Resolver(
        SessionJournalEventReader reader,
        CancellationToken cancellationToken
    ) {
        private readonly SessionJournalEventReader _reader = reader;
        private readonly CancellationToken _cancellationToken = cancellationToken;

        public int HeaderReadCount { get; private set; }

        public int PayloadReadCount { get; private set; }

        public SessionExecutionRecovery ResolveHead(EventAddress head) {
            EventFrameHeader header = ReadHeader(head);
            var kind = (SessionEventKind)header.OpaqueEventKind;
            return kind switch {
                _ when SessionOperationalSemantics.IsSetupKind(kind) =>
                    ResolveSetupRun(head, kind),
                SessionEventKind.SessionCreated => ResolveCreated(head),
                SessionEventKind.ObservationAccepted => ResolveObservation(head),
                SessionEventKind.CompletionRequestPrepared
                    or SessionEventKind.CompletionAttemptStarted =>
                    ResolvePrepared(head, kind),
                SessionEventKind.CompletionAttemptFailed => ResolveFailure(head),
                _ when SessionOperationalSemantics.IsActionKind(kind) =>
                    ResolveAction(head, kind, validateSource: true),
                _ when SessionOperationalSemantics.IsToolSegmentKind(
                    kind
                ) =>
                    ResolveToolSegment(head, validateActionSource: true),
                _ => throw new InvalidDataException(
                    $"Unsupported SessionJournal execution head kind '{kind}' at {head}."
                )
            };
        }

        private SessionExecutionRecovery ResolveSetupRun(
            EventAddress head,
            SessionEventKind headKind
        ) {
            EventAddress? cursor = head;
            int setupCount = 0;
            SessionEventKind? oldestSetupKind = null;
            while (cursor is { } address) {
                EventFrameHeader header = ReadHeader(address);
                var kind = (SessionEventKind)header.OpaqueEventKind;
                if (!SessionOperationalSemantics.IsSetupKind(kind)) {
                    break;
                }
                DecodedSessionEvent setup = ReadDecoded(address, kind);
                if (kind == SessionEventKind.RuntimeConfigSetup) {
                    _ = RequireBody<SessionRuntimeConfiguration>(setup);
                }
                else {
                    _ = RequireBody<SystemPromptSetupBody>(setup);
                }
                setupCount++;
                oldestSetupKind = kind;
                cursor = setup.Parent;
            }

            if (cursor is null) {
                bool validBootstrapPrefix =
                    setupCount == 1 && oldestSetupKind == SessionEventKind.RuntimeConfigSetup
                    || setupCount == 2 && headKind == SessionEventKind.SystemPromptSetup
                        && oldestSetupKind == SessionEventKind.RuntimeConfigSetup;
                if (!validBootstrapPrefix) {
                    throw new InvalidDataException(
                        $"Setup run ending at {head} is not a valid SessionJournal bootstrap prefix."
                    );
                }
                return Recovery(
                    head,
                    new SessionExecutionState(SessionExecutionPhase.Empty, headKind),
                    EmptyBoundary
                );
            }

            SessionExecutionRecovery predecessor = ResolveHead(cursor.Value);
            if (predecessor.State.Phase
                == SessionExecutionPhase.TurnFailed) {
                throw new InvalidDataException(
                    $"Setup run ending at {head} cannot descend from a "
                    + "failed turn; the exact CompletionAttemptFailed "
                    + "head must be abandoned first."
                );
            }
            if (predecessor.State.Phase
                != SessionExecutionPhase.Idle) {
                throw new InvalidDataException(
                    $"Setup run ending at {head} must descend from an "
                    + "idle terminal boundary."
                );
            }

            return Recovery(
                head,
                new SessionExecutionState(
                    SessionExecutionPhase.Idle,
                    headKind,
                    ToolExecutionSequenceCheckpoint:
                        predecessor.State.ToolExecutionSequenceCheckpoint
                ),
                predecessor.Boundary
            );
        }

        private SessionExecutionRecovery ResolveCreated(EventAddress head) {
            DecodedSessionEvent created = ReadDecoded(head, SessionEventKind.SessionCreated);
            _ = RequireBody<SessionCreatedBody>(created);
            EventAddress promptAddress = created.Parent
                ?? throw new InvalidDataException(
                    $"SessionCreated at {head} requires a SystemPromptSetup parent."
                );
            DecodedSessionEvent prompt = ReadDecoded(
                promptAddress,
                SessionEventKind.SystemPromptSetup
            );
            _ = RequireBody<SystemPromptSetupBody>(prompt);
            EventAddress runtimeAddress = prompt.Parent
                ?? throw new InvalidDataException(
                    $"Bootstrap SystemPromptSetup at {promptAddress} requires a RuntimeConfigSetup parent."
                );
            DecodedSessionEvent runtime = ReadDecoded(
                runtimeAddress,
                SessionEventKind.RuntimeConfigSetup
            );
            _ = RequireBody<SessionRuntimeConfiguration>(runtime);
            if (runtime.Parent is not null) {
                throw new InvalidDataException(
                    $"Bootstrap RuntimeConfigSetup at {runtimeAddress} must be the journal root."
                );
            }

            return Recovery(
                head,
                new SessionExecutionState(
                    SessionExecutionPhase.Idle,
                    SessionEventKind.SessionCreated
                ),
                EmptyBoundary with { LatestExecutionCheckpoint = head }
            );
        }

        private SessionExecutionRecovery ResolveObservation(EventAddress head) {
            DecodedSessionEvent observation = ReadDecoded(
                head,
                SessionEventKind.ObservationAccepted
            );
            _ = RequireBody<ObservationAcceptedBody>(observation);
            EventAddress parent = observation.Parent
                ?? throw new InvalidDataException(
                    $"ObservationAccepted at {head} requires an idle predecessor."
                );
            SessionExecutionRecovery idle = ResolveHead(parent);
            if (idle.State.Phase != SessionExecutionPhase.Idle) {
                throw new InvalidDataException(
                    $"ObservationAccepted at {head} must directly descend from an idle boundary; an exact failed turn must be abandoned first."
                );
            }
            string correlationId =
                SessionOperationalSemantics
                    .BuildObservationCorrelationId(head);
            return Recovery(
                head,
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingAgentAction,
                    SessionEventKind.ObservationAccepted,
                    ToolExecutionSequenceCheckpoint:
                        idle.State.ToolExecutionSequenceCheckpoint,
                    ActiveCorrelationId: correlationId
                ),
                new SessionExecutionRecoveryBoundary(
                    SourcePrepared: null,
                    SourceAction: null,
                    SourceObservation: head,
                    LatestExecutionCheckpoint:
                        idle.Boundary.LatestExecutionCheckpoint
                )
            );
        }

        private SessionExecutionRecovery ResolvePrepared(
            EventAddress head,
            SessionEventKind headKind
        ) {
            PreparedAttemptChain chain =
                ResolvePreparedAttemptChain(head);
            SourceBoundary source = ValidatePreparedSourceBoundary(
                chain,
                terminalAddress: head
            );
            return Recovery(
                head,
                new SessionExecutionState(
                    headKind == SessionEventKind.CompletionRequestPrepared
                        ? SessionExecutionPhase.AwaitingCompletionDispatch
                        : SessionExecutionPhase.AwaitingCompletion,
                    headKind,
                    ToolExecutionSequenceCheckpoint:
                        chain.SourceManifest.Execution
                            .LastIssuedToolExecutionSequence,
                    PendingRequestPreparedAddress:
                        chain.SourcePreparedAddress,
                    ActiveCorrelationId:
                        chain.SourceManifest.Origin.CorrelationId,
                    ActiveCompletionAttemptAddress:
                        chain.ActiveAttemptAddress
                ),
                new SessionExecutionRecoveryBoundary(
                    chain.SourcePreparedAddress,
                    SourceAction: source.SourceAction,
                    SourceObservation: source.SourceObservation,
                    LatestExecutionCheckpoint:
                        chain.SourcePreparedAddress
                ),
                new SessionPreparedRuntimeRecoverySnapshot(
                    chain.SourceManifest.Target.Connection,
                    chain.SourceManifest.Target.ClientName,
                    chain.SourceManifest.Target.ApiSpecId,
                    chain.SourceManifest.ToolSet.Sha256,
                    chain.SourceManifest.ToolSet.RuntimeIdentity
                )
            );
        }

        private SessionExecutionRecovery ResolveFailure(EventAddress head) {
            DecodedSessionEvent failureEvent = ReadDecoded(
                head,
                SessionEventKind.CompletionAttemptFailed
            );
            CompletionAttemptFailedBody failure =
                RequireBody<CompletionAttemptFailedBody>(failureEvent);
            EventAddress activeAttempt = failureEvent.Parent
                ?? throw new InvalidDataException(
                    $"CompletionAttemptFailed at {head} requires an active attempt parent."
                );
            PreparedAttemptChain chain =
                ResolvePreparedAttemptChain(activeAttempt);
            _ = ValidatePreparedSourceBoundary(chain, head);
            if (chain.ActiveAttemptAddress != activeAttempt) {
                throw new InvalidDataException(
                    $"CompletionAttemptFailed at {head} does not directly descend from the latest active attempt."
                );
            }
            return Recovery(
                head,
                new SessionExecutionState(
                    SessionExecutionPhase.TurnFailed,
                    SessionEventKind.CompletionAttemptFailed,
                    ToolExecutionSequenceCheckpoint:
                        chain.SourceManifest.Execution
                            .LastIssuedToolExecutionSequence
                ),
                new SessionExecutionRecoveryBoundary(
                    chain.SourcePreparedAddress,
                    SourceAction: null,
                    SourceObservation:
                        TryObservationSource(chain.SourceManifest, chain.SourcePreparedParent),
                    LatestExecutionCheckpoint:
                        chain.SourcePreparedAddress
                )
            );
        }

        private SessionExecutionRecovery ResolveAction(
            EventAddress head,
            SessionEventKind kind,
            bool validateSource
        ) {
            DecodedSessionEvent actionEvent = ReadDecoded(head, kind);
            AgentActionProducedBody action =
                RequireBody<AgentActionProducedBody>(actionEvent);
            ValidateActionBody(actionEvent, action);

            ActionSource source = validateSource
                ? ValidateActionSource(actionEvent, action)
                : new ActionSource(
                    SourcePrepared: null,
                    SourceObservation: null
                );
            RawToolCall? pending = action.Action.ToolCalls.FirstOrDefault();
            SessionExecutionState state = pending is null
                ? new SessionExecutionState(
                    SessionExecutionPhase.Idle,
                    kind,
                    ToolExecutionSequenceCheckpoint:
                        action.Execution.LastIssuedToolExecutionSequence
                )
                : new SessionExecutionState(
                    SessionExecutionPhase.AwaitingToolExecution,
                    kind,
                    PendingToolCall: pending,
                    ToolExecutionSequenceCheckpoint:
                        action.Execution.LastIssuedToolExecutionSequence,
                    ActiveCorrelationId: action.CorrelationId,
                    PendingToolRuntimeIdentity:
                        action.ToolRuntimeIdentity
                );
            return Recovery(
                head,
                state,
                new SessionExecutionRecoveryBoundary(
                    source.SourcePrepared,
                    head,
                    source.SourceObservation,
                    head
                )
            );
        }

        private SessionExecutionRecovery ResolveToolSegment(
            EventAddress head,
            bool validateActionSource
        ) {
            var reverse = new List<DecodedSessionEvent>();
            EventAddress cursor = head;
            DecodedSessionEvent actionEvent;
            while (true) {
                DecodedSessionEvent current = ReadDecoded(cursor);
                if (SessionOperationalSemantics.IsToolSegmentKind(
                        current.Kind
                    )) {
                    reverse.Add(current);
                    cursor = current.Parent
                        ?? throw new InvalidDataException(
                            $"{current.Kind} at {current.Address} requires a current Action ancestor."
                        );
                    continue;
                }
                if (SessionOperationalSemantics.IsActionKind(
                        current.Kind
                    )) {
                    actionEvent = current;
                    break;
                }
                throw new InvalidDataException(
                    $"Tool execution tail at {head} reached '{current.Kind}' at {current.Address} instead of its Action."
                );
            }

            AgentActionProducedBody action =
                RequireBody<AgentActionProducedBody>(actionEvent);
            ValidateActionBody(actionEvent, action);
            if (action.Action.ToolCalls.Count == 0) {
                throw new InvalidDataException(
                    $"Tool execution tail at {head} descends from an Action without tool calls."
                );
            }
            ActionSource source = validateActionSource
                ? ValidateActionSource(actionEvent, action)
                : new ActionSource(null, null);

            reverse.Reverse();
            int callIndex = 0;
            ToolExecutionStartedBody? activeStart = null;
            long checkpoint =
                action.Execution.LastIssuedToolExecutionSequence;
            EventAddress latestCheckpoint = actionEvent.Address;
            foreach (DecodedSessionEvent ev in reverse) {
                if (callIndex >= action.Action.ToolCalls.Count) {
                    throw new InvalidDataException(
                        $"{ev.Kind} at {ev.Address} appears after all declared tool calls were settled."
                    );
                }
                RawToolCall call = action.Action.ToolCalls[callIndex];
                switch (ev.Kind) {
                    case SessionEventKind.ToolExecutionStarted: {
                        if (activeStart is not null) {
                            throw new InvalidDataException(
                                $"{ev.Kind} at {ev.Address} duplicates the active tool start."
                            );
                        }
                        ToolExecutionStartedBody started =
                            RequireBody<ToolExecutionStartedBody>(ev);
                        ThrowIfOperationalViolation(
                            ev,
                            SessionOperationalSemantics
                                .ValidatePendingToolCallMatch(
                                    call,
                                    started.ToolCallId,
                                    started.ToolName,
                                    started.RawArgumentsJson
                                )
                        );
                        ThrowIfOperationalViolation(
                            ev,
                            SessionOperationalSemantics
                                .ValidateToolRuntimeIdentityMatch(
                                    action.ToolRuntimeIdentity,
                                    started.ToolRuntimeIdentity
                                )
                        );
                        ThrowIfOperationalViolation(
                            ev,
                            SessionOperationalSemantics
                                .ValidateReservedStartSequence(
                                    checkpoint,
                                    started.ExecutionSequence
                                )
                        );
                        activeStart = started;
                        checkpoint = started.ExecutionSequence;
                        latestCheckpoint = ev.Address;
                        break;
                    }
                    case SessionEventKind.ToolResultObserved: {
                        ToolResultObservedBody result =
                            RequireBody<ToolResultObservedBody>(ev);
                        if (activeStart is null) {
                            throw new InvalidDataException(
                                $"{ev.Kind} at {ev.Address} requires the declared call's preceding start."
                            );
                        }
                        ThrowIfOperationalViolation(
                            ev,
                            SessionOperationalSemantics
                                .ValidatePendingToolCallMatch(
                                    call,
                                    result.ToolCallId,
                                    result.ToolName,
                                    rawArgumentsJson: null
                                )
                        );
                        ThrowIfOperationalViolation(
                            ev,
                            SessionOperationalSemantics
                                .ValidateReservedResultSequence(
                                    activeStart.ExecutionSequence,
                                    result.ExecutionSequence
                                )
                        );
                        activeStart = null;
                        callIndex++;
                        latestCheckpoint = ev.Address;
                        break;
                    }
                    default:
                        throw new InvalidOperationException(
                            $"Unexpected tool tail event '{ev.Kind}'."
                        );
                }
            }

            RawToolCall? pending = callIndex < action.Action.ToolCalls.Count
                ? action.Action.ToolCalls[callIndex]
                : null;
            SessionExecutionState state;
            if (pending is null) {
                state = new SessionExecutionState(
                    SessionExecutionPhase.AwaitingAgentAction,
                    reverse.Count == 0
                        ? actionEvent.Kind
                        : reverse[^1].Kind,
                    ToolExecutionSequenceCheckpoint: checkpoint,
                    ActiveCorrelationId: action.CorrelationId
                );
            }
            else {
                state = new SessionExecutionState(
                    SessionExecutionPhase.AwaitingToolExecution,
                    reverse.Count == 0
                        ? actionEvent.Kind
                        : reverse[^1].Kind,
                    PendingToolCall: pending,
                    PendingOperationId: activeStart?.OperationId,
                    PendingToolExecutionStarted: activeStart is not null,
                    ToolExecutionSequenceCheckpoint: checkpoint,
                    ActiveCorrelationId: action.CorrelationId,
                    PendingToolRuntimeIdentity:
                        action.ToolRuntimeIdentity
                );
            }

            return Recovery(
                head,
                state,
                new SessionExecutionRecoveryBoundary(
                    source.SourcePrepared,
                    actionEvent.Address,
                    source.SourceObservation,
                    latestCheckpoint
                )
            );
        }

        internal PreparedAttemptChain ResolvePreparedAttemptChain(
            EventAddress activeAttemptHead
        ) {
            var newestToOldestStarts =
                new List<(EventAddress Address, EventAddress Parent)>();
            EventAddress cursor = activeAttemptHead;
            CompletionRequestPreparedBody sourceManifest;
            EventAddress sourcePreparedAddress;
            EventAddress? sourcePreparedParent;
            while (true) {
                DecodedSessionEvent ev = ReadDecoded(cursor);
                if (ev.Kind == SessionEventKind.CompletionAttemptStarted) {
                    EventAddress parent = ev.Parent
                        ?? throw new InvalidDataException(
                            $"CompletionAttemptStarted at {cursor} requires a Prepared or prior Started parent."
                        );
                    _ = RequireBody<CompletionAttemptStartedBody>(ev);
                    newestToOldestStarts.Add((cursor, parent));
                    cursor = parent;
                    continue;
                }
                if (ev.Kind != SessionEventKind.CompletionRequestPrepared) {
                    throw new InvalidDataException(
                        $"Prepared recovery chain reached '{ev.Kind}' at {cursor} instead of CompletionRequestPrepared."
                    );
                }
                sourceManifest = RequireBody<CompletionRequestPreparedBody>(ev);
                sourcePreparedAddress = cursor;
                sourcePreparedParent = ev.Parent;
                break;
            }

            EventAddress expectedParent = sourcePreparedAddress;
            EventAddress? activeAttemptAddress = null;
            foreach (var entry in newestToOldestStarts.AsEnumerable().Reverse()) {
                if (entry.Parent != expectedParent) {
                    throw new InvalidDataException(
                        $"CompletionAttemptStarted at {entry.Address} does not strictly continue the source attempt chain."
                    );
                }
                expectedParent = entry.Address;
                activeAttemptAddress = entry.Address;
            }
            if (expectedParent != activeAttemptHead) {
                throw new InvalidDataException(
                    "Prepared recovery chain does not terminate at the exact active attempt head."
                );
            }

            return new PreparedAttemptChain(
                sourcePreparedAddress,
                sourcePreparedParent,
                activeAttemptAddress,
                sourceManifest
            );
        }

        private SourceBoundary ValidatePreparedSourceBoundary(
            PreparedAttemptChain chain,
            EventAddress terminalAddress
        ) {
            EventAddress sourceAddress = chain.SourcePreparedParent
                ?? throw new InvalidDataException(
                    $"CompletionRequestPrepared at {chain.SourcePreparedAddress} requires a completion boundary parent."
                );
            if (string.IsNullOrWhiteSpace(
                    chain.SourceManifest.Origin.CorrelationId
                )) {
                throw new InvalidDataException(
                    $"CompletionRequestPrepared at {chain.SourcePreparedAddress} requires a correlation id."
                );
            }

            switch (chain.SourceManifest.Origin.Reason) {
                case "observation": {
                    DecodedSessionEvent observation = ReadDecoded(
                        sourceAddress,
                        SessionEventKind.ObservationAccepted
                    );
                    _ = RequireBody<ObservationAcceptedBody>(observation);
                    if (!string.Equals(
                            chain.SourceManifest.Origin.CorrelationId,
                            SessionOperationalSemantics
                                .BuildObservationCorrelationId(
                                    sourceAddress
                                ),
                            StringComparison.Ordinal
                        )) {
                        throw new InvalidDataException(
                            $"CompletionRequestPrepared at {chain.SourcePreparedAddress} does not match its source observation correlation."
                        );
                    }
                    return new SourceBoundary(
                        SourceAction: null,
                        SourceObservation: sourceAddress
                    );
                }
                case "tool-continuation": {
                    DecodedSessionEvent result = ReadDecoded(
                        sourceAddress,
                        SessionEventKind.ToolResultObserved
                    );
                    ToolResultObservedBody resultBody =
                        RequireBody<ToolResultObservedBody>(result);
                    if (resultBody.ExecutionSequence !=
                        chain.SourceManifest.Execution
                            .LastIssuedToolExecutionSequence) {
                        throw new InvalidDataException(
                            $"CompletionRequestPrepared at {chain.SourcePreparedAddress} checkpoint does not match its direct ToolResultObserved source."
                        );
                    }
                    return new SourceBoundary(
                        SourceAction: null,
                        SourceObservation: null
                    );
                }
                default:
                    throw new InvalidDataException(
                        $"CompletionRequestPrepared at {chain.SourcePreparedAddress} has unsupported reason '{chain.SourceManifest.Origin.Reason}'."
                    );
            }
        }

        private ActionSource ValidateActionSource(
            DecodedSessionEvent actionEvent,
            AgentActionProducedBody action
        ) {
            EventAddress parent = actionEvent.Parent
                ?? throw new InvalidDataException(
                    $"{actionEvent.Kind} at {actionEvent.Address} requires a completion boundary parent."
                );
            if (actionEvent.Kind == SessionEventKind.AgentActionProduced) {
                PreparedAttemptChain chain =
                    ResolvePreparedAttemptChain(parent);
                if (chain.ActiveAttemptAddress != parent) {
                    throw new InvalidDataException(
                        $"{actionEvent.Kind} at {actionEvent.Address} must directly descend from CompletionAttemptStarted."
                    );
                }
                SourceBoundary source = ValidatePreparedSourceBoundary(
                    chain,
                    actionEvent.Address
                );
                if (!string.Equals(
                        action.CorrelationId,
                        chain.SourceManifest.Origin.CorrelationId,
                        StringComparison.Ordinal
                    )) {
                    throw new InvalidDataException(
                        $"{actionEvent.Kind} at {actionEvent.Address} correlation id does not match its source Prepared."
                    );
                }
                if (action.Execution != chain.SourceManifest.Execution) {
                    throw new InvalidDataException(
                        $"{actionEvent.Kind} at {actionEvent.Address} checkpoint does not match its source Prepared."
                    );
                }
                SessionToolRuntimeIdentity? expectedIdentity =
                    action.Action.ToolCalls.Count == 0
                        ? null
                        : chain.SourceManifest.ToolSet.RuntimeIdentity;
                ThrowIfOperationalViolation(
                    actionEvent,
                    SessionOperationalSemantics
                        .ValidateToolRuntimeIdentityMatch(
                            expectedIdentity,
                            action.ToolRuntimeIdentity
                        )
                );
                return new ActionSource(
                    chain.SourcePreparedAddress,
                    source.SourceObservation
                );
            }

            EventFrameHeader parentHeader = ReadHeader(parent);
            var parentKind =
                (SessionEventKind)parentHeader.OpaqueEventKind;
            switch (parentKind) {
                case SessionEventKind.ObservationAccepted: {
                    DecodedSessionEvent observation = ReadDecoded(
                        parent,
                        SessionEventKind.ObservationAccepted
                    );
                    _ = RequireBody<ObservationAcceptedBody>(observation);
                    if (!string.Equals(
                            action.CorrelationId,
                            SessionOperationalSemantics
                                .BuildObservationCorrelationId(
                                    parent
                                ),
                            StringComparison.Ordinal
                        )) {
                        throw new InvalidDataException(
                            $"{actionEvent.Kind} at {actionEvent.Address} correlation id does not match its direct ObservationAccepted source."
                        );
                    }
                    _ = observation.Parent
                        ?? throw new InvalidDataException(
                            $"ObservationAccepted at {parent} requires an idle predecessor."
                        );
                    // Imported Action is itself the durable checkpoint cut. Replaying
                    // the observation's predecessor here would recursively cross every
                    // prior imported turn and destroy tail boundedness.
                    return new ActionSource(null, parent);
                }
                case SessionEventKind.ToolResultObserved: {
                    SessionExecutionRecovery settled =
                        ResolveToolSegment(parent, validateActionSource: false);
                    if (settled.State.Phase !=
                            SessionExecutionPhase.AwaitingAgentAction
                        || action.Execution.LastIssuedToolExecutionSequence !=
                            settled.State.ToolExecutionSequenceCheckpoint
                        || !string.Equals(
                            action.CorrelationId,
                            settled.State.ActiveCorrelationId,
                            StringComparison.Ordinal
                        )) {
                        throw new InvalidDataException(
                            $"{actionEvent.Kind} at {actionEvent.Address} does not match its settled tool-result boundary."
                        );
                    }
                    return new ActionSource(null, null);
                }
                default:
                    throw new InvalidDataException(
                        $"{actionEvent.Kind} at {actionEvent.Address} must directly follow ObservationAccepted or a settled ToolResultObserved."
                    );
            }
        }

        private static void ValidateActionBody(
            DecodedSessionEvent actionEvent,
            AgentActionProducedBody action
        ) {
            if (string.IsNullOrWhiteSpace(action.CorrelationId)) {
                throw new InvalidDataException(
                    $"{actionEvent.Kind} at {actionEvent.Address} requires a non-empty correlation id."
                );
            }
            if (action.Execution.LastIssuedToolExecutionSequence < 0) {
                throw new InvalidDataException(
                    $"{actionEvent.Kind} at {actionEvent.Address} has a negative execution checkpoint."
                );
            }
            ThrowIfOperationalViolation(
                actionEvent,
                SessionOperationalSemantics
                    .ValidateActionToolDeclarations(
                        action.Action
                    )
            );
            ThrowIfOperationalViolation(
                actionEvent,
                SessionOperationalSemantics
                    .ValidateRequiredToolRuntimeIdentity(
                        action.Action,
                        action.ToolRuntimeIdentity
                    )
            );
            ThrowIfOperationalViolation(
                actionEvent,
                SessionOperationalSemantics
                    .ValidateUnexpectedToolRuntimeIdentity(
                        action.Action,
                        action.ToolRuntimeIdentity
                    )
            );
        }

        private static void ThrowIfOperationalViolation(
            DecodedSessionEvent ev,
            SessionOperationalViolation? violation
        ) {
            if (violation is { } value) {
                throw SessionOperationalSemantics
                    .CreateInvalidDataException(
                        $"{ev.Kind} at {ev.Address}",
                        value
                    );
            }
        }

        private DecodedSessionEvent ReadDecoded(
            EventAddress address,
            SessionEventKind? expectedKind = null
        ) {
            _cancellationToken.ThrowIfCancellationRequested();
            PayloadReadCount++;
            using SessionJournalEventFrame frame = _reader.ReadEvent(address).Unwrap();
            ValidateHeader(address, frame.Header);
            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            if (expectedKind is { } expected && kind != expected) {
                throw new InvalidDataException(
                    $"Tail dependency expected '{expected}' at {address}, got '{kind}'."
                );
            }
            object body = SessionEventCodec.Decode(
                kind,
                frame.Payload,
                out int version
            );
            return new DecodedSessionEvent(
                kind,
                version,
                body,
                address,
                frame.Header.Parent
            );
        }

        private EventFrameHeader ReadHeader(EventAddress address) {
            _cancellationToken.ThrowIfCancellationRequested();
            HeaderReadCount++;
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateHeader(address, header);
            return header;
        }

        private static void ValidateHeader(
            EventAddress address,
            EventFrameHeader header
        ) {
            if (!Enum.IsDefined(
                    typeof(SessionEventKind),
                    header.OpaqueEventKind
                )) {
                throw new InvalidDataException(
                    $"Unknown SessionJournal event kind '{header.OpaqueEventKind}' at {address}."
                );
            }
            if (header.Hint != default(AddressHint)) {
                throw new InvalidDataException(
                    $"SessionJournal trunk requires EventAddress hint 0, got '{header.Hint}' at {address}."
                );
            }
        }

        private SessionExecutionRecovery Recovery(
            EventAddress head,
            SessionExecutionState state,
            SessionExecutionRecoveryBoundary boundary,
            SessionPreparedRuntimeRecoverySnapshot? preparedRuntime = null
        ) => new(
            head,
            state,
            boundary,
            default,
            preparedRuntime
        );

        private static T RequireBody<T>(DecodedSessionEvent ev)
            where T : class
            => ev.Body as T
                ?? throw new InvalidDataException(
                    $"Event kind '{ev.Kind}' at {ev.Address} body is not '{typeof(T).Name}'."
                );

        private static EventAddress? TryObservationSource(
            CompletionRequestPreparedBody manifest,
            EventAddress? sourceParent
        ) => string.Equals(
                manifest.Origin.Reason,
                "observation",
                StringComparison.Ordinal
            )
                ? sourceParent
                : null;

        private readonly record struct SourceBoundary(
            EventAddress? SourceAction,
            EventAddress? SourceObservation
        );

        private readonly record struct ActionSource(
            EventAddress? SourcePrepared,
            EventAddress? SourceObservation
        );
    }
}
