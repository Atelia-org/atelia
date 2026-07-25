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
        long toolExecutionSequenceCheckpoint = 0;
        EventAddress? firstObservedToolResultAddress = null;
        EventAddress? lastObservedToolResultAddress = null;
        EventAddress? pendingRequestPreparedAddress = null;
        string? pendingCompletionAttemptId = null;
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
                    firstObservedToolResultAddress = null;
                    lastObservedToolResultAddress = null;
                    toolExecutionSequenceCheckpoint = 0;
                    pendingRequestPreparedAddress = null;
                    pendingCompletionAttemptId = null;
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
                    firstObservedToolResultAddress = null;
                    lastObservedToolResultAddress = null;
                    pendingRequestPreparedAddress = null;
                    pendingCompletionAttemptId = null;
                    activeCorrelationId = BuildCorrelationId(ev.Address);
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
                        || !string.Equals(body.Attempt.CorrelationId, activeCorrelationId, StringComparison.Ordinal)) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} has a correlation id that does not match the active turn.");
                    }
                    pendingRequestPreparedAddress = ev.Address;
                    pendingCompletionAttemptId = body.Attempt.AttemptId;
                    break;
                }
                case SessionEventKind.CompletionAttemptFailed: {
                    EnsureSessionCreated(ev, sessionCreated);
                    var body = RequireBody<CompletionAttemptFailedBody>(ev);
                    if (headKind != SessionEventKind.CompletionRequestPrepared
                        || pendingRequestPreparedAddress is not { } preparedAddress
                        || ev.Parent != preparedAddress) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must directly follow the active completion-request-prepared event."
                        );
                    }
                    if (!string.Equals(body.AttemptId, pendingCompletionAttemptId, StringComparison.Ordinal)) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} does not match the active completion attempt.");
                    }
                    pendingRequestPreparedAddress = null;
                    pendingCompletionAttemptId = null;
                    activeCorrelationId = null;
                    break;
                }
                case SessionEventKind.AgentActionProduced:
                case SessionEventKind.ImportedAgentAction: {
                    EnsureSessionCreated(ev, sessionCreated);
                    EventAddress? preparedAddress = pendingRequestPreparedAddress;
                    bool isPreparedAction = ev.Kind == SessionEventKind.AgentActionProduced
                        && preparedAddress.HasValue;
                    bool isImportedAction = ev.Kind == SessionEventKind.ImportedAgentAction
                        && pendingRequestPreparedAddress is null
                        && (headKind == SessionEventKind.ObservationAccepted
                            || headKind == SessionEventKind.ToolResultObserved && pendingToolCall is null);
                    if (!isPreparedAction && !isImportedAction) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} does not follow a completion boundary.");
                    }
                    if (isPreparedAction && ev.Parent != preparedAddress.GetValueOrDefault()) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must directly descend from completion-request-prepared {preparedAddress}."
                        );
                    }
                    var body = RequireBody<AgentActionProducedBody>(ev);
                    context.Add(body.Action);
                    addressedMessages?.Add(new AddressedSessionHistoryMessage(body.Action, ev.Address, ev.Address));
                    openAction = body.Action.ToolCalls.Count == 0 ? null : body.Action;
                    observedResults.Clear();
                    pendingToolCall = body.Action.ToolCalls.FirstOrDefault();
                    pendingOperationId = null;
                    pendingToolExecutionStarted = false;
                    firstObservedToolResultAddress = null;
                    lastObservedToolResultAddress = null;
                    pendingRequestPreparedAddress = null;
                    pendingCompletionAttemptId = null;
                    if (body.Action.ToolCalls.Count == 0) {
                        activeCorrelationId = null;
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
                    EnsureMatchesPendingToolCall(ev, pendingToolCall, body.ToolCallId, body.ToolName, body.RawArgumentsJson);
                    pendingOperationId = body.OperationId;
                    pendingToolExecutionStarted = true;
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
                    EnsureMatchesPendingToolCall(ev, pendingToolCall, body.ToolCallId, body.ToolName, rawArgumentsJson: null);
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
                    toolExecutionSequenceCheckpoint++;
                    pendingToolCall = NextPendingToolCall(openAction!, observedResults);
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
            toolExecutionSequenceCheckpoint,
            pendingRequestPreparedAddress,
            pendingCompletionAttemptId,
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
        long toolExecutionSequenceCheckpoint,
        EventAddress? pendingRequestPreparedAddress,
        string? pendingCompletionAttemptId,
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
                SessionExecutionPhase.AwaitingCompletion,
                headKind,
                ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint,
                PendingRequestPreparedAddress: pendingRequestPreparedAddress,
                PendingCompletionAttemptId: pendingCompletionAttemptId,
                ActiveCorrelationId: activeCorrelationId
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
                activeCorrelationId
            ),
            SessionEventKind.ImportedAgentAction => DeriveActionState(
                SessionEventKind.ImportedAgentAction,
                openAction,
                toolExecutionSequenceCheckpoint,
                activeCorrelationId
            ),
            SessionEventKind.ToolExecutionStarted => new SessionExecutionState(
                SessionExecutionPhase.AwaitingToolExecution,
                headKind,
                pendingToolCall,
                pendingOperationId,
                pendingToolExecutionStarted,
                toolExecutionSequenceCheckpoint,
                ActiveCorrelationId: activeCorrelationId
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
                ActiveCorrelationId: activeCorrelationId
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
        string? activeCorrelationId
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
                ActiveCorrelationId: activeCorrelationId
            );
    }

    private static RawToolCall? NextPendingToolCall(ActionMessage action, IReadOnlyDictionary<string, ToolResultObservedBody> observedResults) {
        foreach (RawToolCall call in action.ToolCalls) {
            if (!observedResults.ContainsKey(call.ToolCallId)) { return call; }
        }

        return null;
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

    private static void EnsureMatchesPendingToolCall(
        DecodedSessionEvent ev,
        RawToolCall pending,
        string toolCallId,
        string toolName,
        string? rawArgumentsJson
    ) {
        if (!string.Equals(pending.ToolCallId, toolCallId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"{ev.Kind} at {ev.Address} targets tool call '{toolCallId}' while current pending call is '{pending.ToolCallId}'."
            );
        }
        if (!string.Equals(pending.ToolName, toolName, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"{ev.Kind} at {ev.Address} tool name '{toolName}' does not match current pending tool '{pending.ToolName}'."
            );
        }
        if (rawArgumentsJson is not null
            && !string.Equals(pending.RawArgumentsJson, rawArgumentsJson, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"{ev.Kind} at {ev.Address} raw arguments do not match current pending tool call '{pending.ToolCallId}'."
            );
        }
    }

    private static T RequireBody<T>(DecodedSessionEvent ev) where T : class
        => ev.Body as T ?? throw new InvalidDataException($"Event kind '{ev.Kind}' body is not '{typeof(T).Name}'.");

    private static string BuildCorrelationId(EventAddress observationAddress)
        => $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observationAddress)}";
}
