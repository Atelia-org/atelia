using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

internal sealed record SessionTailContextProjectionResult(
    string SystemPrompt,
    ImmutableArray<IHistoryMessage> Context,
    EventAddress RawStartExclusive,
    string RawRangeSha256,
    ImmutableArray<SessionRequestArtifactContextSnapshot> ContextSnapshots,
    SessionTailProjectionDiagnostics Diagnostics
);

internal static class SessionTailContextProjection {
    internal static SessionExecutionRecovery ValidateReplaySafeBoundary(
        SessionJournalEventReader reader,
        EventAddress anchor,
        CancellationToken cancellationToken = default
    ) {
        SessionExecutionRecovery recovery =
            SessionExecutionTailResolver.Resolve(
                reader,
                anchor,
                cancellationToken
            );
        if (!SessionOperationalSemantics.IsReplaySafePhase(
                recovery.State.Phase
            )) {
            throw new InvalidDataException(
                $"Session history anchor '{anchor}' in phase "
                + $"'{recovery.State.Phase}' is not replay-safe."
            );
        }
        return recovery;
    }

    internal static TailFoldResult FoldSuffix(
        SessionDependencyClosedFoldSeed seed,
        IReadOnlyList<DecodedSessionEvent> events,
        ICollection<SessionHistoryPlanningUnit>? planningUnits = null,
        ICollection<SessionHistoryPlanningBoundary>?
            replaySafeBoundaries = null
    ) {
        EventAddress runtimeAddress =
            seed.GoverningSetup.RuntimeConfigSetupAddress;
        SessionRuntimeConfiguration runtimeConfig =
            seed.GoverningSetup.RuntimeConfig;
        EventAddress promptAddress =
            seed.GoverningSetup.SystemPromptSetupAddress;
        string systemPrompt = seed.GoverningSetup.SystemPrompt;
        var context = new List<IHistoryMessage>();
        ActionMessage? openAction = null;
        var observedResults = new Dictionary<string, ToolResultObservedBody>(StringComparer.Ordinal);
        RawToolCall? pendingCall = null;
        bool pendingStarted = false;
        long executionSequenceCheckpoint =
            seed.ToolExecutionSequenceCheckpoint;
        string? activeCorrelationId = seed.ActiveCorrelationId;
        SessionExecutionPhase phase = seed.Phase;
        SessionToolRuntimeIdentity? pendingToolRuntimeIdentity = null;
        CompletionRequestPreparedBody? sourcePrepared = null;
        EventAddress? sourcePreparedAddress = null;
        EventAddress? activeAttemptAddress = null;
        EventAddress? firstObservedToolResultAddress = null;
        EventAddress? lastObservedToolResultAddress = null;
        SessionEventKind? priorKind = seed.HeadKind;
        EventAddress? priorAddress = seed.Head;

        foreach (DecodedSessionEvent ev in events) {
            if (ev.Parent != priorAddress) {
                throw new InvalidDataException(
                    $"{ev.Kind} at {ev.Address} does not directly descend from the prior suffix event."
                );
            }
            switch (ev.Kind) {
                case SessionEventKind.RuntimeConfigSetup:
                    EnsureSetupPhase(ev, phase, openAction, sourcePrepared);
                    runtimeAddress = ev.Address;
                    runtimeConfig = RequireBody<SessionRuntimeConfiguration>(ev);
                    phase = phase == SessionExecutionPhase.Empty
                        ? SessionExecutionPhase.Empty
                        : SessionExecutionPhase.Idle;
                    activeCorrelationId = null;
                    break;
                case SessionEventKind.SystemPromptSetup:
                    EnsureSetupPhase(ev, phase, openAction, sourcePrepared);
                    promptAddress = ev.Address;
                    systemPrompt = RequireBody<SystemPromptSetupBody>(ev).Content;
                    phase = phase == SessionExecutionPhase.Empty
                        ? SessionExecutionPhase.Empty
                        : SessionExecutionPhase.Idle;
                    activeCorrelationId = null;
                    break;
                case SessionEventKind.SessionCreated:
                    EnsureNoOpenTool(ev, openAction);
                    _ = RequireBody<SessionCreatedBody>(ev);
                    if (phase != SessionExecutionPhase.Empty
                        || priorKind != SessionEventKind.SystemPromptSetup) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must complete the bootstrap setup run."
                        );
                    }
                    executionSequenceCheckpoint = 0;
                    activeCorrelationId = null;
                    phase = SessionExecutionPhase.Idle;
                    break;
                case SessionEventKind.CompletionAttemptFailed: {
                    EnsureNoOpenTool(ev, openAction);
                    _ = RequireBody<CompletionAttemptFailedBody>(ev);
                    if (phase != SessionExecutionPhase.AwaitingCompletion
                        || sourcePrepared is null
                        || ev.Parent != activeAttemptAddress) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not fail the active suffix completion attempt."
                        );
                    }
                    sourcePrepared = null;
                    sourcePreparedAddress = null;
                    activeAttemptAddress = null;
                    activeCorrelationId = null;
                    phase = SessionExecutionPhase.TurnFailed;
                    break;
                }
                case SessionEventKind.CompletionAttemptStarted: {
                    EnsureNoOpenTool(ev, openAction);
                    _ = RequireBody<CompletionAttemptStartedBody>(ev);
                    if (!SessionOperationalSemantics
                            .IsPreparedOrAttemptPhase(phase)
                        || sourcePrepared is null
                        || sourcePreparedAddress is null
                        || ev.Parent != (activeAttemptAddress
                            ?? sourcePreparedAddress)) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not continue the active suffix Prepared attempt chain."
                        );
                    }
                    activeAttemptAddress = ev.Address;
                    phase = SessionExecutionPhase.AwaitingCompletion;
                    break;
                }
                case SessionEventKind.CompletionRequestPrepared: {
                    EnsureNoOpenTool(ev, openAction);
                    CompletionRequestPreparedBody prepared =
                        RequireBody<CompletionRequestPreparedBody>(ev);
                    if (phase != SessionExecutionPhase.AwaitingAgentAction
                        || sourcePrepared is not null) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} requires an unprepared suffix completion boundary."
                        );
                    }
                    string expectedReason = priorKind switch {
                        SessionEventKind.ObservationAccepted => "observation",
                        SessionEventKind.ToolResultObserved => "tool-continuation",
                        _ => throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not follow a completion boundary."
                        )
                    };
                    if (!string.Equals(prepared.Origin.Reason, expectedReason, StringComparison.Ordinal)
                        || !string.Equals(prepared.Origin.CorrelationId, activeCorrelationId, StringComparison.Ordinal)) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} reason or correlation does not match its suffix completion boundary."
                        );
                    }
                    if (prepared.Execution.LastIssuedToolExecutionSequence
                        != executionSequenceCheckpoint) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} changes the suffix execution checkpoint."
                        );
                    }
                    executionSequenceCheckpoint =
                        prepared.Execution.LastIssuedToolExecutionSequence;
                    sourcePrepared = prepared;
                    sourcePreparedAddress = ev.Address;
                    activeAttemptAddress = null;
                    activeCorrelationId = prepared.Origin.CorrelationId;
                    phase = SessionExecutionPhase.AwaitingCompletionDispatch;
                    break;
                }
                case SessionEventKind.ObservationAccepted:
                    EnsureNoOpenTool(ev, openAction);
                    if (phase != SessionExecutionPhase.Idle) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must appear at an idle suffix boundary; an exact failed turn must be abandoned first."
                        );
                    }
                    var observation = new ObservationMessage(
                        RequireBody<ObservationAcceptedBody>(ev).Content
                    );
                    context.Add(observation);
                    planningUnits?.Add(new SessionHistoryPlanningUnit(
                        observation,
                        ev.Address,
                        ev.Address
                    ));
                    activeCorrelationId =
                        SessionOperationalSemantics
                            .BuildObservationCorrelationId(
                                ev.Address
                            );
                    sourcePrepared = null;
                    sourcePreparedAddress = null;
                    activeAttemptAddress = null;
                    phase = SessionExecutionPhase.AwaitingAgentAction;
                    break;
                case SessionEventKind.AgentActionProduced:
                case SessionEventKind.ImportedAgentAction: {
                    EnsureNoOpenTool(ev, openAction);
                    AgentActionProducedBody actionBody =
                        RequireBody<AgentActionProducedBody>(ev);
                    ActionMessage action = actionBody.Action;
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateActionToolDeclarations(action)
                    );
                    bool isPreparedAction =
                        ev.Kind == SessionEventKind.AgentActionProduced
                        && phase == SessionExecutionPhase.AwaitingCompletion;
                    bool isImportedAction =
                        ev.Kind == SessionEventKind.ImportedAgentAction
                        && phase == SessionExecutionPhase.AwaitingAgentAction
                        && sourcePrepared is null
                        && priorKind is (
                            SessionEventKind.ObservationAccepted
                            or SessionEventKind.ToolResultObserved
                        );
                    if (!isPreparedAction && !isImportedAction) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not directly follow its required completion boundary."
                        );
                    }
                    if (!string.Equals(
                            actionBody.CorrelationId,
                            activeCorrelationId,
                            StringComparison.Ordinal
                        )
                        || actionBody.Execution.LastIssuedToolExecutionSequence
                            != executionSequenceCheckpoint) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} changes the suffix correlation or execution checkpoint."
                        );
                    }
                    executionSequenceCheckpoint =
                        actionBody.Execution.LastIssuedToolExecutionSequence;
                    if (ev.Kind == SessionEventKind.AgentActionProduced) {
                        SessionToolRuntimeIdentity? expectedRuntimeIdentity =
                            action.ToolCalls.Count == 0
                                ? null
                                : sourcePrepared?.ToolSet.RuntimeIdentity;
                        if (sourcePrepared is null
                            || ev.Parent != activeAttemptAddress
                            || activeAttemptAddress is null
                            || !string.Equals(actionBody.CorrelationId, sourcePrepared.Origin.CorrelationId, StringComparison.Ordinal)
                            || actionBody.Execution != sourcePrepared.Execution) {
                            throw new InvalidDataException(
                                $"{ev.Kind} at {ev.Address} does not match its suffix Prepared snapshot."
                            );
                        }
                        ThrowIfOperationalViolation(
                            ev,
                            SessionOperationalSemantics
                                .ValidateToolRuntimeIdentityMatch(
                                    expectedRuntimeIdentity,
                                    actionBody
                                        .ToolRuntimeIdentity
                                )
                        );
                    }
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateRequiredToolRuntimeIdentity(
                                action,
                                actionBody.ToolRuntimeIdentity
                            )
                    );
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateUnexpectedToolRuntimeIdentity(
                                action,
                                actionBody.ToolRuntimeIdentity
                            )
                    );
                    activeCorrelationId = actionBody.CorrelationId;
                    sourcePrepared = null;
                    sourcePreparedAddress = null;
                    activeAttemptAddress = null;
                    context.Add(action);
                    planningUnits?.Add(new SessionHistoryPlanningUnit(
                        action,
                        ev.Address,
                        ev.Address
                    ));
                    if (action.ToolCalls.Count > 0) {
                        pendingToolRuntimeIdentity =
                            actionBody.ToolRuntimeIdentity;
                        openAction = action;
                        observedResults.Clear();
                        pendingCall = action.ToolCalls[0];
                        pendingStarted = false;
                        firstObservedToolResultAddress = null;
                        lastObservedToolResultAddress = null;
                        phase = SessionExecutionPhase.AwaitingToolExecution;
                    }
                    else {
                        activeCorrelationId = null;
                        phase = SessionExecutionPhase.Idle;
                    }
                    break;
                }
                case SessionEventKind.ToolExecutionStarted: {
                    if (phase != SessionExecutionPhase.AwaitingToolExecution
                        || openAction is null
                        || pendingCall is null
                        || pendingStarted) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} has no unstarted suffix-local pending tool call.");
                    }
                    ToolExecutionStartedBody started = RequireBody<ToolExecutionStartedBody>(ev);
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidatePendingToolCallMatch(
                                pendingCall,
                                started.ToolCallId,
                                started.ToolName,
                                started.RawArgumentsJson
                            )
                    );
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateToolRuntimeIdentityMatch(
                                pendingToolRuntimeIdentity,
                                started.ToolRuntimeIdentity
                            )
                    );
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateReservedStartSequence(
                                executionSequenceCheckpoint,
                                started.ExecutionSequence
                            )
                    );
                    executionSequenceCheckpoint = started.ExecutionSequence;
                    pendingStarted = true;
                    break;
                }
                case SessionEventKind.ToolResultObserved: {
                    if (phase != SessionExecutionPhase.AwaitingToolExecution
                        || openAction is null
                        || pendingCall is null
                        || !pendingStarted) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} has no started suffix-local pending tool call.");
                    }
                    ToolResultObservedBody result = RequireBody<ToolResultObservedBody>(ev);
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidatePendingToolCallMatch(
                                pendingCall,
                                result.ToolCallId,
                                result.ToolName,
                                rawArgumentsJson: null
                            )
                    );
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateReservedResultSequence(
                                executionSequenceCheckpoint,
                                result.ExecutionSequence
                            )
                    );
                    if (!observedResults.TryAdd(result.ToolCallId, result)) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} duplicates suffix tool result '{result.ToolCallId}'.");
                    }
                    firstObservedToolResultAddress ??= ev.Address;
                    lastObservedToolResultAddress = ev.Address;
                    pendingCall =
                        SessionOperationalSemantics
                            .SelectNextPendingDeclaredCall(
                                openAction,
                                observedResults
                            );
                    pendingStarted = false;
                    if (pendingCall is null) {
                        ToolResultsMessage toolResults =
                            ProjectToolResults(openAction, observedResults);
                        context.Add(toolResults);
                        planningUnits?.Add(
                            new SessionHistoryPlanningUnit(
                                toolResults,
                                firstObservedToolResultAddress.Value,
                                lastObservedToolResultAddress.Value
                            )
                        );
                        openAction = null;
                        observedResults.Clear();
                        pendingToolRuntimeIdentity = null;
                        firstObservedToolResultAddress = null;
                        lastObservedToolResultAddress = null;
                        phase = SessionExecutionPhase.AwaitingAgentAction;
                    }
                    break;
                }
                default:
                    throw new InvalidDataException($"Unsupported suffix event kind '{ev.Kind}'.");
            }
            if (SessionOperationalSemantics.IsReplaySafePhase(
                    phase
                )
                && openAction is null
                && pendingCall is null
                && !pendingStarted
                && sourcePrepared is null) {
                replaySafeBoundaries?.Add(
                    new SessionHistoryPlanningBoundary(
                        ev.Address,
                        context.Count
                    )
                );
            }
            priorKind = ev.Kind;
            priorAddress = ev.Address;
        }
        if (openAction is not null || pendingCall is not null || pendingStarted) {
            throw new InvalidDataException("Recap raw suffix ends with unresolved tool dependencies.");
        }

        EventAddress finalHead = events.Count == 0 ? seed.Head : events[^1].Address;
        return new TailFoldResult(
            new SessionGoverningSetup(
                finalHead,
                runtimeAddress,
                runtimeConfig,
                promptAddress,
                systemPrompt
            ),
            context,
            activeCorrelationId,
            executionSequenceCheckpoint,
            phase
        );
    }

    private static void EnsureSetupPhase(
        DecodedSessionEvent ev,
        SessionExecutionPhase phase,
        ActionMessage? openAction,
        CompletionRequestPreparedBody? sourcePrepared
    ) {
        EnsureNoOpenTool(ev, openAction);
        if (sourcePrepared is not null
            || phase is not (
                SessionExecutionPhase.Empty
                or SessionExecutionPhase.Idle
            )) {
            throw new InvalidDataException(
                $"{ev.Kind} at {ev.Address} must appear only at a setup or idle suffix boundary."
            );
        }
    }

    private static ToolResultsMessage ProjectToolResults(
        ActionMessage action,
        IReadOnlyDictionary<string, ToolResultObservedBody> observedResults
    ) {
        var results = new ToolResult[action.ToolCalls.Count];
        for (int i = 0; i < action.ToolCalls.Count; i++) {
            RawToolCall call = action.ToolCalls[i];
            ToolResultObservedBody body = observedResults[call.ToolCallId];
            results[i] = new ToolResult(body.ToolName, body.ToolCallId, body.Status, body.Blocks);
        }
        return new ToolResultsMessage(content: null, results);
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

    private static void EnsureNoOpenTool(DecodedSessionEvent ev, ActionMessage? openAction) {
        if (openAction is not null) {
            throw new InvalidDataException($"{ev.Kind} at {ev.Address} appears before suffix tool dependencies are closed.");
        }
    }

    private static T RequireBody<T>(DecodedSessionEvent ev) where T : class
        => ev.Body as T
            ?? throw new InvalidDataException($"{ev.Kind} at {ev.Address} decoded to unexpected body type.");

    internal sealed record TailFoldResult(
        SessionGoverningSetup GoverningSetup,
        IReadOnlyList<IHistoryMessage> Context,
        string? ActiveCorrelationId,
        long ToolExecutionSequenceCheckpoint,
        SessionExecutionPhase Phase
    );

}
