using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

internal static class SessionReducer {
    public static SessionProjection Reduce(IReadOnlyList<DecodedSessionEvent> events) {
        ArgumentNullException.ThrowIfNull(events);

        SessionConfiguration? config = null;
        var context = new List<IHistoryMessage>();
        SessionEventKind? headKind = null;
        ActionMessage? openAction = null;
        var observedResults = new Dictionary<string, ToolResultObservedBody>(StringComparer.Ordinal);
        RawToolCall? pendingToolCall = null;
        string? pendingOperationId = null;
        bool pendingToolExecutionStarted = false;
        long toolExecutionSequenceCheckpoint = 0;

        foreach (DecodedSessionEvent ev in events) {
            headKind = ev.Kind;
            switch (ev.Kind) {
                case SessionEventKind.SessionCreated: {
                    config = RequireBody<SessionConfiguration>(ev);
                    openAction = null;
                    observedResults.Clear();
                    pendingToolCall = null;
                    pendingOperationId = null;
                    pendingToolExecutionStarted = false;
                    toolExecutionSequenceCheckpoint = 0;
                    break;
                }
                case SessionEventKind.ObservationAccepted: {
                    var body = RequireBody<ObservationAcceptedBody>(ev);
                    context.Add(new ObservationMessage(body.Content));
                    openAction = null;
                    observedResults.Clear();
                    pendingToolCall = null;
                    pendingOperationId = null;
                    pendingToolExecutionStarted = false;
                    break;
                }
                case SessionEventKind.AgentActionProduced: {
                    var body = RequireBody<AgentActionProducedBody>(ev);
                    context.Add(body.Action);
                    openAction = body.Action;
                    observedResults.Clear();
                    pendingToolCall = body.Action.ToolCalls.FirstOrDefault();
                    pendingOperationId = null;
                    pendingToolExecutionStarted = false;
                    break;
                }
                case SessionEventKind.ToolExecutionStarted: {
                    var body = RequireBody<ToolExecutionStartedBody>(ev);
                    EnsureOpenAction(ev, openAction);
                    EnsureDeclaredToolCall(ev, openAction!, body.ToolCallId, body.ToolName, body.RawArgumentsJson);
                    pendingToolCall = new RawToolCall(body.ToolName, body.ToolCallId, body.RawArgumentsJson);
                    pendingOperationId = body.OperationId;
                    pendingToolExecutionStarted = true;
                    break;
                }
                case SessionEventKind.ToolResultObserved: {
                    var body = RequireBody<ToolResultObservedBody>(ev);
                    EnsureOpenAction(ev, openAction);
                    RawToolCall declared = EnsureDeclaredToolCall(ev, openAction!, body.ToolCallId, body.ToolName, rawArgumentsJson: null);
                    observedResults[body.ToolCallId] = body;
                    pendingOperationId = null;
                    pendingToolExecutionStarted = false;
                    toolExecutionSequenceCheckpoint++;
                    pendingToolCall = NextPendingToolCall(openAction!, observedResults);
                    if (pendingToolCall is null) {
                        context.Add(ProjectToolResults(openAction!, observedResults));
                        openAction = null;
                        observedResults.Clear();
                    }
                    else if (string.Equals(pendingToolCall.ToolCallId, declared.ToolCallId, StringComparison.Ordinal)) {
                        throw new InvalidDataException($"tool-result-observed did not advance pending tool call '{declared.ToolCallId}'.");
                    }
                    break;
                }
                default:
                    throw new NotSupportedException($"Session event kind '{ev.Kind}' is not implemented in Slice C reducer.");
            }
        }

        var state = DeriveExecutionState(headKind, openAction, pendingToolCall, pendingOperationId, pendingToolExecutionStarted, toolExecutionSequenceCheckpoint);
        return new SessionProjection(
            config,
            context.Count == 0 ? Array.AsReadOnly(Array.Empty<IHistoryMessage>()) : Array.AsReadOnly(context.ToArray()),
            state,
            events.Count == 0 ? null : events[^1].Address
        );
    }

    internal static SessionProjection Empty => new(
        Config: null,
        Context: Array.AsReadOnly(Array.Empty<IHistoryMessage>()),
        ExecutionState: new SessionExecutionState(SessionExecutionPhase.Empty, HeadKind: null),
        Head: null
    );

    private static SessionExecutionState DeriveExecutionState(
        SessionEventKind? headKind,
        ActionMessage? openAction,
        RawToolCall? pendingToolCall,
        string? pendingOperationId,
        bool pendingToolExecutionStarted,
        long toolExecutionSequenceCheckpoint
    )
        => headKind switch {
            null => new SessionExecutionState(SessionExecutionPhase.Empty, null),
            SessionEventKind.SessionCreated => new SessionExecutionState(SessionExecutionPhase.Idle, headKind),
            SessionEventKind.ObservationAccepted => new SessionExecutionState(SessionExecutionPhase.AwaitingAgentAction, headKind),
            SessionEventKind.AgentActionProduced => DeriveActionState(SessionEventKind.AgentActionProduced, openAction, toolExecutionSequenceCheckpoint),
            SessionEventKind.ToolExecutionStarted => new SessionExecutionState(
                SessionExecutionPhase.AwaitingToolExecution,
                headKind,
                pendingToolCall,
                pendingOperationId,
                pendingToolExecutionStarted,
                toolExecutionSequenceCheckpoint
            ),
            SessionEventKind.ToolResultObserved when pendingToolCall is null => new SessionExecutionState(SessionExecutionPhase.AwaitingAgentAction, headKind, ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint),
            SessionEventKind.ToolResultObserved => new SessionExecutionState(SessionExecutionPhase.AwaitingToolExecution, headKind, pendingToolCall, ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint),
            _ => throw new NotSupportedException($"Session event kind '{headKind}' is not implemented in Slice C execution reducer.")
        };

    private static SessionExecutionState DeriveActionState(SessionEventKind headKind, ActionMessage? action, long toolExecutionSequenceCheckpoint) {
        if (action is null) {
            throw new InvalidDataException("agent-action-produced head requires a replayed ActionMessage.");
        }

        RawToolCall? pending = action.ToolCalls.FirstOrDefault();
        return pending is null
            ? new SessionExecutionState(SessionExecutionPhase.Idle, headKind, ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint)
            : new SessionExecutionState(SessionExecutionPhase.AwaitingToolExecution, headKind, pending, ToolExecutionSequenceCheckpoint: toolExecutionSequenceCheckpoint);
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

    private static RawToolCall EnsureDeclaredToolCall(
        DecodedSessionEvent ev,
        ActionMessage openAction,
        string toolCallId,
        string toolName,
        string? rawArgumentsJson
    ) {
        RawToolCall? declared = openAction.ToolCalls.FirstOrDefault(call => string.Equals(call.ToolCallId, toolCallId, StringComparison.Ordinal));
        if (declared is null) {
            throw new InvalidDataException($"{ev.Kind} at {ev.Address} references undeclared tool call '{toolCallId}'.");
        }

        if (!string.Equals(declared.ToolName, toolName, StringComparison.Ordinal)) {
            throw new InvalidDataException($"{ev.Kind} at {ev.Address} tool name '{toolName}' does not match declared tool '{declared.ToolName}'.");
        }

        if (rawArgumentsJson is not null && !string.Equals(declared.RawArgumentsJson, rawArgumentsJson, StringComparison.Ordinal)) {
            throw new InvalidDataException($"{ev.Kind} at {ev.Address} raw arguments do not match declared tool call '{toolCallId}'.");
        }

        return declared;
    }

    private static T RequireBody<T>(DecodedSessionEvent ev) where T : class
        => ev.Body as T ?? throw new InvalidDataException($"Event kind '{ev.Kind}' body is not '{typeof(T).Name}'.");
}
