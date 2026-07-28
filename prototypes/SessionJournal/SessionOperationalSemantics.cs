using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

internal enum SessionOperationalViolation {
    InvalidToolCallIdentity,
    DuplicateToolCallId,
    MissingToolRuntimeIdentity,
    UnexpectedToolRuntimeIdentity,
    PendingToolCallIdMismatch,
    PendingToolNameMismatch,
    PendingToolArgumentsMismatch,
    ToolRuntimeIdentityMismatch,
    ReservedStartSequenceMismatch,
    ReservedResultSequenceMismatch,
}

/// <summary>
/// Pure, context-free classification and identity rules shared by operational
/// replay consumers. This layer performs no IO, traversal, or materialization.
/// </summary>
internal static class SessionOperationalSemantics {
    internal static bool IsSetupKind(SessionEventKind kind) =>
        kind is (
            SessionEventKind.RuntimeConfigSetup
            or SessionEventKind.SystemPromptSetup
        );

    internal static bool IsActionKind(SessionEventKind kind) =>
        kind is (
            SessionEventKind.AgentActionProduced
            or SessionEventKind.ImportedAgentAction
        );

    internal static bool IsToolSegmentKind(SessionEventKind kind) =>
        kind is (
            SessionEventKind.ToolExecutionStarted
            or SessionEventKind.ToolResultObserved
        );

    internal static bool IsReplaySafePhase(SessionExecutionPhase phase) =>
        phase is (
            SessionExecutionPhase.Empty
            or SessionExecutionPhase.Idle
            or SessionExecutionPhase.AwaitingAgentAction
            or SessionExecutionPhase.TurnFailed
        );

    internal static bool IsIdleOrFailedPhase(
        SessionExecutionPhase phase
    ) =>
        phase is (
            SessionExecutionPhase.Idle
            or SessionExecutionPhase.TurnFailed
        );

    internal static bool IsPreparedOrAttemptPhase(
        SessionExecutionPhase phase
    ) =>
        phase is (
            SessionExecutionPhase.AwaitingCompletionDispatch
            or SessionExecutionPhase.AwaitingCompletion
        );

    internal static string BuildObservationCorrelationId(
        EventAddress observationAddress
    ) =>
        $"atelia.session-journal.turn.v1:"
        + EventAddressTextCodec.Format(observationAddress);

    internal static SessionOperationalViolation?
        ValidateActionToolDeclarations(
        ActionMessage action
    ) {
        ArgumentNullException.ThrowIfNull(action);
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawToolCall call in action.ToolCalls) {
            if (string.IsNullOrWhiteSpace(call.ToolCallId)
                || string.IsNullOrWhiteSpace(call.ToolName)
                || string.IsNullOrWhiteSpace(
                    call.RawArgumentsJson
                )) {
                return SessionOperationalViolation
                    .InvalidToolCallIdentity;
            }
            if (!callIds.Add(call.ToolCallId)) {
                return SessionOperationalViolation
                    .DuplicateToolCallId;
            }
        }
        return null;
    }

    internal static SessionOperationalViolation?
        ValidateRequiredToolRuntimeIdentity(
        ActionMessage action,
        SessionToolRuntimeIdentity? runtimeIdentity
    ) {
        ArgumentNullException.ThrowIfNull(action);
        return action.ToolCalls.Count > 0
            && runtimeIdentity is null
            ? SessionOperationalViolation.MissingToolRuntimeIdentity
            : null;
    }

    internal static SessionOperationalViolation?
        ValidateUnexpectedToolRuntimeIdentity(
        ActionMessage action,
        SessionToolRuntimeIdentity? runtimeIdentity
    ) {
        ArgumentNullException.ThrowIfNull(action);
        return action.ToolCalls.Count == 0
            && runtimeIdentity is not null
            ? SessionOperationalViolation.UnexpectedToolRuntimeIdentity
            : null;
    }

    internal static SessionOperationalViolation?
        ValidatePendingToolCallMatch(
        RawToolCall pending,
        string toolCallId,
        string toolName,
        string? rawArgumentsJson
    ) {
        ArgumentNullException.ThrowIfNull(pending);
        if (!string.Equals(
                pending.ToolCallId,
                toolCallId,
                StringComparison.Ordinal
            )) {
            return SessionOperationalViolation
                .PendingToolCallIdMismatch;
        }
        if (!string.Equals(
                pending.ToolName,
                toolName,
                StringComparison.Ordinal
            )) {
            return SessionOperationalViolation.PendingToolNameMismatch;
        }
        if (rawArgumentsJson is not null
            && !string.Equals(
                pending.RawArgumentsJson,
                rawArgumentsJson,
                StringComparison.Ordinal
            )) {
            return SessionOperationalViolation
                .PendingToolArgumentsMismatch;
        }
        return null;
    }

    internal static SessionOperationalViolation?
        ValidateToolRuntimeIdentityMatch(
        SessionToolRuntimeIdentity? expected,
        SessionToolRuntimeIdentity? actual
    ) =>
        expected == actual
            ? null
            : SessionOperationalViolation
                .ToolRuntimeIdentityMismatch;

    internal static SessionOperationalViolation?
        ValidateReservedStartSequence(
        long currentCheckpoint,
        long startedSequence
    ) =>
        startedSequence == checked(currentCheckpoint + 1)
            ? null
            : SessionOperationalViolation
                .ReservedStartSequenceMismatch;

    internal static SessionOperationalViolation?
        ValidateReservedResultSequence(
        long reservedSequence,
        long resultSequence
    ) =>
        resultSequence == reservedSequence
            ? null
            : SessionOperationalViolation
                .ReservedResultSequenceMismatch;

    internal static RawToolCall? SelectNextPendingDeclaredCall(
        ActionMessage action,
        IReadOnlyDictionary<string, ToolResultObservedBody>
            observedResults
    ) {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(observedResults);
        foreach (RawToolCall call in action.ToolCalls) {
            if (!observedResults.ContainsKey(call.ToolCallId)) {
                return call;
            }
        }
        return null;
    }

    internal static InvalidDataException CreateInvalidDataException(
        string context,
        SessionOperationalViolation violation
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        string description = violation switch {
            SessionOperationalViolation.InvalidToolCallIdentity =>
                "contains a tool call with an empty id, name, or raw arguments",
            SessionOperationalViolation.DuplicateToolCallId =>
                "contains a duplicate tool call id",
            SessionOperationalViolation.MissingToolRuntimeIdentity =>
                "contains tool calls without a durable tool runtime identity",
            SessionOperationalViolation.UnexpectedToolRuntimeIdentity =>
                "has a tool runtime identity without tool calls",
            SessionOperationalViolation.PendingToolCallIdMismatch =>
                "targets a tool call other than the next declared call",
            SessionOperationalViolation.PendingToolNameMismatch =>
                "uses a tool name other than the next declared call",
            SessionOperationalViolation.PendingToolArgumentsMismatch =>
                "uses raw arguments other than the next declared call",
            SessionOperationalViolation.ToolRuntimeIdentityMismatch =>
                "has a tool runtime identity that does not match its durable source",
            SessionOperationalViolation.ReservedStartSequenceMismatch =>
                "does not reserve the next tool execution sequence",
            SessionOperationalViolation.ReservedResultSequenceMismatch =>
                "does not repeat the active reserved tool execution sequence",
            _ => throw new ArgumentOutOfRangeException(
                nameof(violation),
                violation,
                "Unknown operational violation."
            )
        };
        return new InvalidDataException(
            $"{context} {description} ({violation})."
        );
    }
}
