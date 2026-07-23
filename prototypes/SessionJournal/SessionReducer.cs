using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

internal static class SessionReducer {
    public static SessionProjection Reduce(IReadOnlyList<DecodedSessionEvent> events) {
        ArgumentNullException.ThrowIfNull(events);

        SessionConfiguration? config = null;
        var context = new List<IHistoryMessage>();
        SessionEventKind? headKind = null;
        ActionMessage? lastAction = null;

        foreach (DecodedSessionEvent ev in events) {
            headKind = ev.Kind;
            switch (ev.Kind) {
                case SessionEventKind.SessionCreated: {
                    var body = RequireBody<SessionConfiguration>(ev);
                    config = body;
                    lastAction = null;
                    break;
                }
                case SessionEventKind.ObservationAccepted: {
                    var body = RequireBody<ObservationAcceptedBody>(ev);
                    context.Add(new ObservationMessage(body.Content));
                    lastAction = null;
                    break;
                }
                case SessionEventKind.AssistantActionProduced: {
                    var body = RequireBody<AssistantActionProducedBody>(ev);
                    context.Add(body.Action);
                    lastAction = body.Action;
                    break;
                }
                default:
                    throw new NotSupportedException($"Session event kind '{ev.Kind}' is not implemented in Slice A reducer.");
            }
        }

        var state = DeriveExecutionState(headKind, lastAction);
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

    private static SessionExecutionState DeriveExecutionState(SessionEventKind? headKind, ActionMessage? lastAction)
        => headKind switch {
            null => new SessionExecutionState(SessionExecutionPhase.Empty, null),
            SessionEventKind.SessionCreated => new SessionExecutionState(SessionExecutionPhase.Idle, headKind),
            SessionEventKind.ObservationAccepted => new SessionExecutionState(SessionExecutionPhase.AwaitingAssistantAction, headKind),
            SessionEventKind.AssistantActionProduced => DeriveActionState(SessionEventKind.AssistantActionProduced, lastAction),
            _ => throw new NotSupportedException($"Session event kind '{headKind}' is not implemented in Slice A execution reducer.")
        };

    private static SessionExecutionState DeriveActionState(SessionEventKind headKind, ActionMessage? action) {
        if (action is null) {
            throw new InvalidDataException("assistant-action-produced head requires a replayed ActionMessage.");
        }

        RawToolCall? pending = action.ToolCalls.FirstOrDefault();
        return pending is null
            ? new SessionExecutionState(SessionExecutionPhase.Idle, headKind)
            : new SessionExecutionState(SessionExecutionPhase.AwaitingToolExecution, headKind, pending);
    }

    private static T RequireBody<T>(DecodedSessionEvent ev) where T : class
        => ev.Body as T ?? throw new InvalidDataException($"Event kind '{ev.Kind}' body is not '{typeof(T).Name}'.");
}
