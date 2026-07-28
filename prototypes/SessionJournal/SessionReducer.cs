using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

internal static class SessionReducer {
    public static SessionProjection Reduce(
        IReadOnlyList<DecodedSessionEvent> events,
        ICollection<AddressedSessionHistoryMessage>? addressedMessages = null
    ) {
        ArgumentNullException.ThrowIfNull(events);

        SessionRuntimeConfiguration? config = null;
        string? systemPrompt = null;
        bool sessionCreated = false;
        var context = new List<IHistoryMessage>();
        SessionEventKind? headKind = null;
        ActionMessage? openAction = null;
        var observedResults = new Dictionary<string, ToolResultObservedBody>(StringComparer.Ordinal);
        RawToolCall? pendingToolCall = null;
        string? pendingOperationId = null;
        bool pendingToolExecutionStarted = false;
        SessionToolRuntimeIdentity? pendingToolRuntimeIdentity = null;
        long toolExecutionSequenceCheckpoint = 0;
        EventAddress? firstObservedToolResultAddress = null;
        EventAddress? lastObservedToolResultAddress = null;
        EventAddress? pendingRequestPreparedAddress = null;
        CompletionRequestPreparedBody? pendingRequestManifest = null;
        EventAddress? activeCompletionAttemptAddress = null;
        string? activeCorrelationId = null;

        foreach (DecodedSessionEvent ev in events) {
            switch (ev.Kind) {
                case SessionEventKind.RuntimeConfigSetup: {
                    EnsureSetupBoundary(ev, headKind, openAction, pendingToolCall, pendingOperationId, pendingToolExecutionStarted, pendingRequestPreparedAddress);
                    config = RequireBody<SessionRuntimeConfiguration>(ev);
                    break;
                }
                case SessionEventKind.SystemPromptSetup: {
                    EnsureSetupBoundary(ev, headKind, openAction, pendingToolCall, pendingOperationId, pendingToolExecutionStarted, pendingRequestPreparedAddress);
                    systemPrompt = RequireBody<SystemPromptSetupBody>(ev).Content;
                    break;
                }
                case SessionEventKind.SessionCreated: {
                    _ = RequireBody<SessionCreatedBody>(ev);
                    EnsureSetupBoundary(ev, headKind, openAction, pendingToolCall, pendingOperationId, pendingToolExecutionStarted, pendingRequestPreparedAddress);
                    if (config is null) { throw new InvalidDataException($"{ev.Kind} at {ev.Address} requires a prior runtime-config-setup."); }
                    if (systemPrompt is null) { throw new InvalidDataException($"{ev.Kind} at {ev.Address} requires a prior system-prompt-setup."); }
                    sessionCreated = true;
                    openAction = null;
                    observedResults.Clear();
                    pendingToolCall = null;
                    pendingOperationId = null;
                    pendingToolExecutionStarted = false;
                    pendingToolRuntimeIdentity = null;
                    firstObservedToolResultAddress = null;
                    lastObservedToolResultAddress = null;
                    toolExecutionSequenceCheckpoint = 0;
                    pendingRequestPreparedAddress = null;
                    pendingRequestManifest = null;
                    activeCompletionAttemptAddress = null;
                    activeCorrelationId = null;
                    break;
                }
                case SessionEventKind.ObservationAccepted: {
                    EnsureSessionCreated(ev, sessionCreated);
                    if (headKind is not (SessionEventKind.SessionCreated or SessionEventKind.RuntimeConfigSetup or SessionEventKind.SystemPromptSetup)
                        && !(headKind == SessionEventKind.AgentActionProduced && openAction is null)
                        && !(headKind == SessionEventKind.ImportedAgentAction && openAction is null)
                        && headKind != SessionEventKind.CompletionAttemptFailed) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} must appear only at an idle session boundary.");
                    }
                    var body = RequireBody<ObservationAcceptedBody>(ev);
                    var message = new ObservationMessage(body.Content);
                    context.Add(message);
                    addressedMessages?.Add(new AddressedSessionHistoryMessage(message, ev.Address, ev.Address));
                    openAction = null;
                    observedResults.Clear();
                    pendingToolCall = null;
                    pendingOperationId = null;
                    pendingToolExecutionStarted = false;
                    pendingToolRuntimeIdentity = null;
                    firstObservedToolResultAddress = null;
                    lastObservedToolResultAddress = null;
                    pendingRequestPreparedAddress = null;
                    pendingRequestManifest = null;
                    activeCompletionAttemptAddress = null;
                    activeCorrelationId =
                        SessionOperationalSemantics
                            .BuildObservationCorrelationId(
                                ev.Address
                            );
                    break;
                }
                case SessionEventKind.CompletionRequestPrepared: {
                    EnsureSessionCreated(ev, sessionCreated);
                    var body = RequireBody<CompletionRequestPreparedBody>(ev);
                    bool isCompletionBoundary = headKind == SessionEventKind.ObservationAccepted
                        || headKind == SessionEventKind.ToolResultObserved && pendingToolCall is null;
                    if (!isCompletionBoundary || openAction is not null || pendingRequestPreparedAddress is not null) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} requires an observation or fully-settled tool result immediately before it."
                        );
                    }
                    if (activeCorrelationId is null
                        || !string.Equals(body.Origin.CorrelationId, activeCorrelationId, StringComparison.Ordinal)) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} has a correlation id that does not match the active turn.");
                    }
                    string expectedReason = headKind == SessionEventKind.ObservationAccepted
                        ? "observation"
                        : "tool-continuation";
                    if (!string.Equals(body.Origin.Reason, expectedReason, StringComparison.Ordinal)) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} reason '{body.Origin.Reason}' does not match predecessor '{headKind}'."
                        );
                    }
                    if (body.Execution.LastIssuedToolExecutionSequence != toolExecutionSequenceCheckpoint) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} checkpoint {body.Execution.LastIssuedToolExecutionSequence} "
                            + $"does not match current last-issued sequence {toolExecutionSequenceCheckpoint}."
                        );
                    }
                    pendingRequestPreparedAddress = ev.Address;
                    pendingRequestManifest = body;
                    activeCompletionAttemptAddress = null;
                    break;
                }
                case SessionEventKind.CompletionAttemptStarted: {
                    EnsureSessionCreated(ev, sessionCreated);
                    _ = RequireBody<CompletionAttemptStartedBody>(ev);
                    if (headKind is not (SessionEventKind.CompletionRequestPrepared
                            or SessionEventKind.CompletionAttemptStarted)
                        || pendingRequestPreparedAddress is null
                        || ev.Parent != (activeCompletionAttemptAddress
                            ?? pendingRequestPreparedAddress)) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must directly follow the Prepared event or latest active completion attempt."
                        );
                    }
                    activeCompletionAttemptAddress = ev.Address;
                    break;
                }
                case SessionEventKind.CompletionAttemptFailed: {
                    EnsureSessionCreated(ev, sessionCreated);
                    var body = RequireBody<CompletionAttemptFailedBody>(ev);
                    if (headKind != SessionEventKind.CompletionAttemptStarted
                        || pendingRequestPreparedAddress is null
                        || activeCompletionAttemptAddress is not { } activeAttemptAddress
                        || ev.Parent != activeAttemptAddress) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must directly follow the active completion attempt."
                        );
                    }
                    pendingRequestPreparedAddress = null;
                    pendingRequestManifest = null;
                    activeCompletionAttemptAddress = null;
                    activeCorrelationId = null;
                    break;
                }
                case SessionEventKind.AgentActionProduced:
                case SessionEventKind.ImportedAgentAction: {
                    EnsureSessionCreated(ev, sessionCreated);
                    EventAddress? activeAttemptAddress = activeCompletionAttemptAddress;
                    bool isPreparedAction = ev.Kind == SessionEventKind.AgentActionProduced
                        && pendingRequestPreparedAddress.HasValue
                        && activeAttemptAddress.HasValue;
                    bool isImportedAction = ev.Kind == SessionEventKind.ImportedAgentAction
                        && pendingRequestPreparedAddress is null
                        && activeCompletionAttemptAddress is null
                        && (headKind == SessionEventKind.ObservationAccepted
                            || headKind == SessionEventKind.ToolResultObserved && pendingToolCall is null);
                    if (!isPreparedAction && !isImportedAction) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} does not follow a completion boundary.");
                    }
                    if (isPreparedAction && ev.Parent != activeAttemptAddress.GetValueOrDefault()) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must directly descend from active completion attempt {activeAttemptAddress}."
                        );
                    }
                    var body = RequireBody<AgentActionProducedBody>(ev);
                    if (activeCorrelationId is null
                        || !string.Equals(
                            body.CorrelationId,
                            activeCorrelationId,
                            StringComparison.Ordinal
                        )) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} correlation id does not match its active completion boundary."
                        );
                    }
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateActionToolDeclarations(
                                body.Action
                            )
                    );
                    if (body.Execution.LastIssuedToolExecutionSequence != toolExecutionSequenceCheckpoint) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} checkpoint {body.Execution.LastIssuedToolExecutionSequence} "
                            + $"does not match current last-issued sequence {toolExecutionSequenceCheckpoint}."
                        );
                    }
                    SessionToolRuntimeIdentity? expectedToolRuntimeIdentity =
                        body.Action.ToolCalls.Count == 0
                            ? null
                            : isPreparedAction
                                ? pendingRequestManifest?.ToolSet.RuntimeIdentity
                                : body.ToolRuntimeIdentity;
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateToolRuntimeIdentityMatch(
                                expectedToolRuntimeIdentity,
                                body.ToolRuntimeIdentity
                            )
                    );
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateRequiredToolRuntimeIdentity(
                                body.Action,
                                body.ToolRuntimeIdentity
                            )
                    );
                    // Full replay preserves the imported wire contract:
                    // a terminal ImportedAgentAction may carry source runtime
                    // identity even though it declares no tool calls.
                    context.Add(body.Action);
                    addressedMessages?.Add(new AddressedSessionHistoryMessage(body.Action, ev.Address, ev.Address));
                    openAction = body.Action.ToolCalls.Count == 0 ? null : body.Action;
                    observedResults.Clear();
                    pendingToolCall = body.Action.ToolCalls.FirstOrDefault();
                    pendingOperationId = null;
                    pendingToolExecutionStarted = false;
                    pendingToolRuntimeIdentity = body.ToolRuntimeIdentity;
                    firstObservedToolResultAddress = null;
                    lastObservedToolResultAddress = null;
                    pendingRequestPreparedAddress = null;
                    pendingRequestManifest = null;
                    activeCompletionAttemptAddress = null;
                    if (body.Action.ToolCalls.Count == 0) {
                        activeCorrelationId = null;
                        pendingToolRuntimeIdentity = null;
                    }
                    break;
                }
                case SessionEventKind.ToolExecutionStarted: {
                    EnsureSessionCreated(ev, sessionCreated);
                    var body = RequireBody<ToolExecutionStartedBody>(ev);
                    EnsureOpenAction(ev, openAction);
                    if (pendingToolCall is null) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} requires a current pending tool call.");
                    }
                    if (pendingToolExecutionStarted || pendingOperationId is not null) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} duplicates an already-started tool execution.");
                    }
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidatePendingToolCallMatch(
                                pendingToolCall,
                                body.ToolCallId,
                                body.ToolName,
                                body.RawArgumentsJson
                            )
                    );
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateToolRuntimeIdentityMatch(
                                pendingToolRuntimeIdentity,
                                body.ToolRuntimeIdentity
                            )
                    );
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateReservedStartSequence(
                                toolExecutionSequenceCheckpoint,
                                body.ExecutionSequence
                            )
                    );
                    pendingOperationId = body.OperationId;
                    pendingToolExecutionStarted = true;
                    toolExecutionSequenceCheckpoint = body.ExecutionSequence;
                    break;
                }
                case SessionEventKind.ToolResultObserved: {
                    EnsureSessionCreated(ev, sessionCreated);
                    var body = RequireBody<ToolResultObservedBody>(ev);
                    EnsureOpenAction(ev, openAction);
                    if (pendingToolCall is null) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} requires a current pending tool call.");
                    }
                    if (!pendingToolExecutionStarted || pendingOperationId is null) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} requires a preceding start for the current tool call.");
                    }
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidatePendingToolCallMatch(
                                pendingToolCall,
                                body.ToolCallId,
                                body.ToolName,
                                rawArgumentsJson: null
                            )
                    );
                    ThrowIfOperationalViolation(
                        ev,
                        SessionOperationalSemantics
                            .ValidateReservedResultSequence(
                                toolExecutionSequenceCheckpoint,
                                body.ExecutionSequence
                            )
                    );
                    if (observedResults.ContainsKey(body.ToolCallId)) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} duplicates result for tool call '{body.ToolCallId}'.");
                    }
                    observedResults[body.ToolCallId] = body;
                    if (!firstObservedToolResultAddress.HasValue) {
                        firstObservedToolResultAddress = ev.Address;
                    }
                    lastObservedToolResultAddress = ev.Address;
                    pendingOperationId = null;
                    pendingToolExecutionStarted = false;
                    pendingToolCall =
                        SessionOperationalSemantics
                            .SelectNextPendingDeclaredCall(
                                openAction!,
                                observedResults
                            );
                    if (pendingToolCall is null) {
                        ToolResultsMessage message = ProjectToolResults(openAction!, observedResults);
                        context.Add(message);
                        addressedMessages?.Add(new AddressedSessionHistoryMessage(
                            message,
                            firstObservedToolResultAddress.GetValueOrDefault(ev.Address),
                            lastObservedToolResultAddress.GetValueOrDefault(ev.Address)
                        ));
                        openAction = null;
                        observedResults.Clear();
                        firstObservedToolResultAddress = null;
                        lastObservedToolResultAddress = null;
                        pendingToolRuntimeIdentity = null;
                    }
                    break;
                }
                default:
                    throw new NotSupportedException($"Session event kind '{ev.Kind}' is not implemented in Slice C reducer.");
            }

            headKind = ev.Kind;
        }

        var state = DeriveExecutionState(
            headKind,
            sessionCreated,
            openAction,
            pendingToolCall,
            pendingOperationId,
            pendingToolExecutionStarted,
            pendingToolRuntimeIdentity,
            toolExecutionSequenceCheckpoint,
            pendingRequestPreparedAddress,
            activeCompletionAttemptAddress,
            activeCorrelationId
        );
        return new SessionProjection(
            config,
            systemPrompt,
            context.Count == 0 ? Array.AsReadOnly(Array.Empty<IHistoryMessage>()) : Array.AsReadOnly(context.ToArray()),
            state,
            events.Count == 0 ? null : events[^1].Address
        );
    }

    internal static SessionProjection Empty => new(
        Config: null,
        SystemPrompt: null,
        Context: Array.AsReadOnly(Array.Empty<IHistoryMessage>()),
        ExecutionState: new SessionExecutionState(SessionExecutionPhase.Empty, HeadKind: null),
        Head: null
    );

    private static SessionExecutionState DeriveExecutionState(
        SessionEventKind? headKind,
        bool sessionCreated,
        ActionMessage? openAction,
        RawToolCall? pendingToolCall,
        string? pendingOperationId,
        bool pendingToolExecutionStarted,
        SessionToolRuntimeIdentity? pendingToolRuntimeIdentity,
        long toolExecutionSequenceCheckpoint,
        EventAddress? pendingRequestPreparedAddress,
        EventAddress? activeCompletionAttemptAddress,
        string? activeCorrelationId
    )
        => headKind switch {
            null => new SessionExecutionState(SessionExecutionPhase.Empty, null),
            SessionEventKind.RuntimeConfigSetup => DeriveSetupState(headKind.Value, sessionCreated, toolExecutionSequenceCheckpoint),
            SessionEventKind.SystemPromptSetup => DeriveSetupState(headKind.Value, sessionCreated, toolExecutionSequenceCheckpoint),
            SessionEventKind.SessionCreated => new SessionExecutionState(SessionExecutionPhase.Idle, headKind),
            SessionEventKind.ObservationAccepted => new SessionExecutionState(
                SessionExecutionPhase.AwaitingAgentAction,
                headKind,
                ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint,
                ActiveCorrelationId: activeCorrelationId
            ),
            SessionEventKind.CompletionRequestPrepared => new SessionExecutionState(
                SessionExecutionPhase.AwaitingCompletionDispatch,
                headKind,
                ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint,
                PendingRequestPreparedAddress: pendingRequestPreparedAddress,
                ActiveCorrelationId: activeCorrelationId
            ),
            SessionEventKind.CompletionAttemptStarted => new SessionExecutionState(
                SessionExecutionPhase.AwaitingCompletion,
                headKind,
                ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint,
                PendingRequestPreparedAddress: pendingRequestPreparedAddress,
                ActiveCorrelationId: activeCorrelationId,
                ActiveCompletionAttemptAddress: activeCompletionAttemptAddress
            ),
            SessionEventKind.CompletionAttemptFailed => new SessionExecutionState(
                SessionExecutionPhase.TurnFailed,
                headKind,
                ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint
            ),
            SessionEventKind.AgentActionProduced => DeriveActionState(
                SessionEventKind.AgentActionProduced,
                openAction,
                toolExecutionSequenceCheckpoint,
                activeCorrelationId,
                pendingToolRuntimeIdentity
            ),
            SessionEventKind.ImportedAgentAction => DeriveActionState(
                SessionEventKind.ImportedAgentAction,
                openAction,
                toolExecutionSequenceCheckpoint,
                activeCorrelationId,
                pendingToolRuntimeIdentity
            ),
            SessionEventKind.ToolExecutionStarted => new SessionExecutionState(
                SessionExecutionPhase.AwaitingToolExecution,
                headKind,
                pendingToolCall,
                pendingOperationId,
                pendingToolExecutionStarted,
                toolExecutionSequenceCheckpoint,
                ActiveCorrelationId: activeCorrelationId,
                PendingToolRuntimeIdentity: pendingToolRuntimeIdentity
            ),
            SessionEventKind.ToolResultObserved when pendingToolCall is null => new SessionExecutionState(
                SessionExecutionPhase.AwaitingAgentAction,
                headKind,
                ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint,
                ActiveCorrelationId: activeCorrelationId
            ),
            SessionEventKind.ToolResultObserved => new SessionExecutionState(
                SessionExecutionPhase.AwaitingToolExecution,
                headKind,
                pendingToolCall,
                ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint,
                ActiveCorrelationId: activeCorrelationId,
                PendingToolRuntimeIdentity: pendingToolRuntimeIdentity
            ),
            _ => throw new NotSupportedException($"Session event kind '{headKind}' is not implemented in Slice C execution reducer.")
        };

    private static SessionExecutionState DeriveSetupState(
        SessionEventKind headKind,
        bool sessionCreated,
        long toolExecutionSequenceCheckpoint
    )
        => sessionCreated
            ? new SessionExecutionState(SessionExecutionPhase.Idle, headKind, ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint)
            : new SessionExecutionState(SessionExecutionPhase.Empty, headKind);

    private static SessionExecutionState DeriveActionState(
        SessionEventKind headKind,
        ActionMessage? action,
        long toolExecutionSequenceCheckpoint,
        string? activeCorrelationId,
        SessionToolRuntimeIdentity? pendingToolRuntimeIdentity
    ) {
        if (action is null) {
            return new SessionExecutionState(SessionExecutionPhase.Idle, headKind, ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint);
        }

        RawToolCall? pending = action.ToolCalls.FirstOrDefault();
        return pending is null
            ? new SessionExecutionState(SessionExecutionPhase.Idle, headKind, ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint)
            : new SessionExecutionState(
                SessionExecutionPhase.AwaitingToolExecution,
                headKind,
                pending,
                ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint,
                ActiveCorrelationId: activeCorrelationId,
                PendingToolRuntimeIdentity: pendingToolRuntimeIdentity
            );
    }

    private static ToolResultsMessage ProjectToolResults(ActionMessage action, IReadOnlyDictionary<string, ToolResultObservedBody> observedResults) {
        var results = new ToolResult[action.ToolCalls.Count];
        for (int i = 0; i < action.ToolCalls.Count; i++) {
            RawToolCall call = action.ToolCalls[i];
            if (!observedResults.TryGetValue(call.ToolCallId, out ToolResultObservedBody? body)) {
                throw new InvalidDataException($"Missing observed tool result for call '{call.ToolCallId}'.");
            }

            results[i] = new ToolResult(body.ToolName, body.ToolCallId, body.Status, body.Blocks);
        }

        return new ToolResultsMessage(content: null, results);
    }

    private static void EnsureOpenAction(DecodedSessionEvent ev, ActionMessage? openAction) {
        if (openAction is null) {
            throw new InvalidDataException($"{ev.Kind} at {ev.Address} requires a prior agent action with pending tool calls.");
        }
    }

    private static void EnsureSetupBoundary(
        DecodedSessionEvent ev,
        SessionEventKind? headKind,
        ActionMessage? openAction,
        RawToolCall? pendingToolCall,
        string? pendingOperationId,
        bool pendingToolExecutionStarted,
        EventAddress? pendingRequestPreparedAddress
    ) {
        bool hasNoPendingAction = openAction is null
            && pendingToolCall is null
            && pendingOperationId is null
            && !pendingToolExecutionStarted
            && pendingRequestPreparedAddress is null;
        bool isSetupOrIdle = headKind is null or SessionEventKind.RuntimeConfigSetup or SessionEventKind.SystemPromptSetup or SessionEventKind.SessionCreated
            or SessionEventKind.CompletionAttemptFailed
            || headKind is SessionEventKind.AgentActionProduced or SessionEventKind.ImportedAgentAction
                && hasNoPendingAction;
        if (!isSetupOrIdle) {
            throw new InvalidDataException($"{ev.Kind} at {ev.Address} must appear only at setup or idle session boundaries.");
        }
    }

    private static void EnsureSessionCreated(DecodedSessionEvent ev, bool sessionCreated) {
        if (!sessionCreated) {
            throw new InvalidDataException($"{ev.Kind} at {ev.Address} requires a prior session-created marker.");
        }
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

    private static T RequireBody<T>(DecodedSessionEvent ev) where T : class
        => ev.Body as T ?? throw new InvalidDataException($"Event kind '{ev.Kind}' body is not '{typeof(T).Name}'.");

}
