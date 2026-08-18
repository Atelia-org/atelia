using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.SessionJournal.Offline;

internal sealed class SessionJournalOfflineExecutionPhaseJsonConverter
    : JsonConverter<SessionExecutionPhase> {
    public SessionJournalOfflineExecutionPhaseJsonConverter() {
    }

    public override SessionExecutionPhase Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) {
        _ = typeToConvert;
        _ = options;
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException(
                "Offline validation execution phase must be a string."
            );
        }
        return reader.GetString() switch {
            "empty" => SessionExecutionPhase.Empty,
            "idle" => SessionExecutionPhase.Idle,
            "awaiting-agent-action" =>
                SessionExecutionPhase.AwaitingAgentAction,
            "awaiting-completion-dispatch" =>
                SessionExecutionPhase.AwaitingCompletionDispatch,
            "awaiting-completion" =>
                SessionExecutionPhase.AwaitingCompletion,
            "awaiting-tool-execution" =>
                SessionExecutionPhase.AwaitingToolExecution,
            "turn-failed" => SessionExecutionPhase.TurnFailed,
            _ => throw new JsonException(
                "Offline validation execution phase is unsupported."
            )
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        SessionExecutionPhase value,
        JsonSerializerOptions options
    ) {
        _ = options;
        writer.WriteStringValue(value switch {
            SessionExecutionPhase.Empty => "empty",
            SessionExecutionPhase.Idle => "idle",
            SessionExecutionPhase.AwaitingAgentAction =>
                "awaiting-agent-action",
            SessionExecutionPhase.AwaitingCompletionDispatch =>
                "awaiting-completion-dispatch",
            SessionExecutionPhase.AwaitingCompletion =>
                "awaiting-completion",
            SessionExecutionPhase.AwaitingToolExecution =>
                "awaiting-tool-execution",
            SessionExecutionPhase.TurnFailed => "turn-failed",
            _ => throw new JsonException(
                "Offline validation execution phase is unsupported."
            )
        });
    }
}

internal sealed class SessionJournalOfflineEventKindJsonConverter
    : JsonConverter<SessionEventKind> {
    public SessionJournalOfflineEventKindJsonConverter() {
    }

    public override SessionEventKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) {
        _ = typeToConvert;
        _ = options;
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException(
                "Offline validation event kind must be a string."
            );
        }
        return reader.GetString() switch {
            "runtime-config-setup" =>
                SessionEventKind.RuntimeConfigSetup,
            "system-prompt-setup" =>
                SessionEventKind.SystemPromptSetup,
            "session-created" => SessionEventKind.SessionCreated,
            "observation-accepted" =>
                SessionEventKind.ObservationAccepted,
            "agent-action-produced" =>
                SessionEventKind.AgentActionProduced,
            "tool-execution-started" =>
                SessionEventKind.ToolExecutionStarted,
            "tool-result-observed" =>
                SessionEventKind.ToolResultObserved,
            "completion-request-prepared" =>
                SessionEventKind.CompletionRequestPrepared,
            "completion-attempt-failed" =>
                SessionEventKind.CompletionAttemptFailed,
            "imported-agent-action" =>
                SessionEventKind.ImportedAgentAction,
            "completion-attempt-started" =>
                SessionEventKind.CompletionAttemptStarted,
            _ => throw new JsonException(
                "Offline validation event kind is unsupported."
            )
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        SessionEventKind value,
        JsonSerializerOptions options
    ) {
        _ = options;
        writer.WriteStringValue(value switch {
            SessionEventKind.RuntimeConfigSetup =>
                "runtime-config-setup",
            SessionEventKind.SystemPromptSetup =>
                "system-prompt-setup",
            SessionEventKind.SessionCreated => "session-created",
            SessionEventKind.ObservationAccepted =>
                "observation-accepted",
            SessionEventKind.AgentActionProduced =>
                "agent-action-produced",
            SessionEventKind.ToolExecutionStarted =>
                "tool-execution-started",
            SessionEventKind.ToolResultObserved =>
                "tool-result-observed",
            SessionEventKind.CompletionRequestPrepared =>
                "completion-request-prepared",
            SessionEventKind.CompletionAttemptFailed =>
                "completion-attempt-failed",
            SessionEventKind.ImportedAgentAction =>
                "imported-agent-action",
            SessionEventKind.CompletionAttemptStarted =>
                "completion-attempt-started",
            _ => throw new JsonException(
                "Offline validation event kind is unsupported."
            )
        });
    }
}
