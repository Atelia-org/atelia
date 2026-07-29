using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.Offline;

internal sealed class SessionJournalOfflineForwardFold {
    private readonly Dictionary<SessionEventKind, int> _eventKindCounts =
        [];
    private readonly Dictionary<string, ObservedToolResult>
        _observedResults = new(StringComparer.Ordinal);
    private readonly List<string> _historyContributionHashes = [];

    private SessionRuntimeConfiguration? _runtimeConfig;
    private EventAddress? _runtimeConfigSetupAddress;
    private string? _systemPrompt;
    private EventAddress? _systemPromptSetupAddress;
    private bool _sessionCreated;
    private SessionEventKind? _headKind;
    private IReadOnlyList<RawToolCall>? _openToolCalls;
    private RawToolCall? _pendingToolCall;
    private string? _pendingOperationId;
    private bool _pendingToolExecutionStarted;
    private SessionToolRuntimeIdentity? _pendingToolRuntimeIdentity;
    private long _toolExecutionSequenceCheckpoint;
    private EventAddress? _pendingRequestPreparedAddress;
    private SessionJournalAuditPreparedFact? _pendingRequest;
    private EventAddress? _activeCompletionAttemptAddress;
    private string? _activeCorrelationId;
    private int _preparedRequestCount;
    private int _observationCount;
    private int _agentActionCount;
    private int _importedAgentActionCount;
    private int _toolResultHistoryCount;
    private int _historyContributionCount;
    private bool _completed;

    public void Accept(SessionJournalAuditEvent auditEvent) {
        if (_completed) {
            throw new InvalidOperationException(
                "SessionJournal offline fold is already complete."
            );
        }
        ArgumentNullException.ThrowIfNull(auditEvent);
        _eventKindCounts[auditEvent.Kind] =
            checked(_eventKindCounts.GetValueOrDefault(
                auditEvent.Kind
            ) + 1);

        switch (auditEvent.Kind) {
            case SessionEventKind.RuntimeConfigSetup:
                EnsureSetupBoundary(auditEvent);
                _runtimeConfig =
                    RequireFact<SessionJournalAuditRuntimeConfigFact>(
                        auditEvent
                    ).Configuration;
                _runtimeConfigSetupAddress = auditEvent.Address;
                break;
            case SessionEventKind.SystemPromptSetup:
                EnsureSetupBoundary(auditEvent);
                _systemPrompt =
                    RequireFact<SessionJournalAuditSystemPromptFact>(
                        auditEvent
                    ).SystemPrompt;
                _systemPromptSetupAddress = auditEvent.Address;
                break;
            case SessionEventKind.SessionCreated:
                _ = RequireFact<
                    SessionJournalAuditSessionCreatedFact
                >(auditEvent);
                EnsureSetupBoundary(auditEvent);
                if (_runtimeConfig is null) {
                    throw Error(
                        auditEvent,
                        "requires a prior runtime-config-setup"
                    );
                }
                if (_systemPrompt is null) {
                    throw Error(
                        auditEvent,
                        "requires a prior system-prompt-setup"
                    );
                }
                _sessionCreated = true;
                ResetTurnState(resetSequence: true);
                break;
            case SessionEventKind.ObservationAccepted:
                AcceptObservation(auditEvent);
                break;
            case SessionEventKind.CompletionRequestPrepared:
                AcceptPrepared(auditEvent);
                break;
            case SessionEventKind.CompletionAttemptStarted:
                AcceptAttemptStarted(auditEvent);
                break;
            case SessionEventKind.CompletionAttemptFailed:
                AcceptAttemptFailed(auditEvent);
                break;
            case SessionEventKind.AgentActionProduced:
            case SessionEventKind.ImportedAgentAction:
                AcceptAction(auditEvent);
                break;
            case SessionEventKind.ToolExecutionStarted:
                AcceptToolExecutionStarted(auditEvent);
                break;
            case SessionEventKind.ToolResultObserved:
                AcceptToolResultObserved(auditEvent);
                break;
            default:
                throw new NotSupportedException(
                    $"Session event kind '{auditEvent.Kind}' is not "
                    + "implemented in the offline audit fold."
                );
        }
        _headKind = auditEvent.Kind;
    }

    public SessionJournalOfflineFoldResult Complete() {
        if (_completed) {
            throw new InvalidOperationException(
                "SessionJournal offline fold is already complete."
            );
        }
        _completed = true;
        SessionExecutionState executionState = DeriveExecutionState();
        string commitment =
            SessionHistorySemanticCommitment
                .ComputeSequenceSha256(
                    _historyContributionHashes
                );
        SessionJournalOfflineEventKindCount[] eventKindCounts = [
            .. _eventKindCounts
                .OrderBy(static entry => (uint)entry.Key)
                .Select(static entry =>
                    new SessionJournalOfflineEventKindCount(
                        entry.Key,
                        entry.Value
                    )
                )
        ];
        return new SessionJournalOfflineFoldResult(
            executionState,
            _runtimeConfigSetupAddress,
            _runtimeConfig,
            _systemPromptSetupAddress,
            _systemPrompt,
            _preparedRequestCount,
            _observationCount,
            _agentActionCount,
            _importedAgentActionCount,
            _toolResultHistoryCount,
            _historyContributionCount,
            SessionHistorySemanticCommitment.CodecId,
            commitment,
            Array.AsReadOnly(eventKindCounts)
        );
    }

    private void AcceptObservation(
        SessionJournalAuditEvent auditEvent
    ) {
        EnsureSessionCreated(auditEvent);
        bool idleBoundary =
            _headKind is (
                SessionEventKind.SessionCreated
                or SessionEventKind.RuntimeConfigSetup
                or SessionEventKind.SystemPromptSetup
                or SessionEventKind.CompletionAttemptFailed
            )
            || _headKind is (
                SessionEventKind.AgentActionProduced
                or SessionEventKind.ImportedAgentAction
            ) && _openToolCalls is null;
        if (!idleBoundary) {
            throw Error(
                auditEvent,
                "must appear only at an idle session boundary"
            );
        }
        SessionJournalAuditObservationFact fact =
            RequireFact<SessionJournalAuditObservationFact>(
                auditEvent
            );
        _observationCount = checked(_observationCount + 1);
        AppendHistoryContribution(
            fact.SemanticContributionSha256
        );
        ResetTurnState(resetSequence: false);
        _activeCorrelationId =
            BuildObservationCorrelationId(auditEvent.Address);
    }

    private void AcceptPrepared(
        SessionJournalAuditEvent auditEvent
    ) {
        EnsureSessionCreated(auditEvent);
        SessionJournalAuditPreparedFact fact =
            RequireFact<SessionJournalAuditPreparedFact>(
                auditEvent
            );
        bool completionBoundary =
            _headKind == SessionEventKind.ObservationAccepted
            || _headKind == SessionEventKind.ToolResultObserved
                && _pendingToolCall is null;
        if (!completionBoundary
            || _openToolCalls is not null
            || _pendingRequestPreparedAddress is not null) {
            throw Error(
                auditEvent,
                "requires an observation or fully-settled tool result "
                + "immediately before it"
            );
        }
        if (_activeCorrelationId is null
            || !string.Equals(
                fact.CorrelationId,
                _activeCorrelationId,
                StringComparison.Ordinal
            )) {
            throw Error(
                auditEvent,
                "has a correlation id that does not match the active turn"
            );
        }
        string expectedReason =
            _headKind == SessionEventKind.ObservationAccepted
                ? "observation"
                : "tool-continuation";
        if (!string.Equals(
                fact.Reason,
                expectedReason,
                StringComparison.Ordinal
            )) {
            throw Error(
                auditEvent,
                $"reason '{fact.Reason}' does not match predecessor "
                + $"'{_headKind}'"
            );
        }
        if (fact.LastIssuedToolExecutionSequence
            != _toolExecutionSequenceCheckpoint) {
            throw Error(
                auditEvent,
                $"checkpoint {fact.LastIssuedToolExecutionSequence} "
                + "does not match current last-issued sequence "
                + $"{_toolExecutionSequenceCheckpoint}"
            );
        }
        _pendingRequestPreparedAddress = auditEvent.Address;
        _pendingRequest = fact;
        _activeCompletionAttemptAddress = null;
        _preparedRequestCount = checked(_preparedRequestCount + 1);
    }

    private void AcceptAttemptStarted(
        SessionJournalAuditEvent auditEvent
    ) {
        EnsureSessionCreated(auditEvent);
        _ = RequireFact<
            SessionJournalAuditCompletionAttemptStartedFact
        >(auditEvent);
        if (_headKind is not (
                SessionEventKind.CompletionRequestPrepared
                or SessionEventKind.CompletionAttemptStarted
            )
            || _pendingRequestPreparedAddress is null
            || auditEvent.Parent
                != (_activeCompletionAttemptAddress
                    ?? _pendingRequestPreparedAddress)) {
            throw Error(
                auditEvent,
                "must directly follow the Prepared event or latest "
                + "active completion attempt"
            );
        }
        _activeCompletionAttemptAddress = auditEvent.Address;
    }

    private void AcceptAttemptFailed(
        SessionJournalAuditEvent auditEvent
    ) {
        EnsureSessionCreated(auditEvent);
        _ = RequireFact<
            SessionJournalAuditCompletionAttemptFailedFact
        >(auditEvent);
        if (_headKind != SessionEventKind.CompletionAttemptStarted
            || _pendingRequestPreparedAddress is null
            || _activeCompletionAttemptAddress
                is not { } activeAttemptAddress
            || auditEvent.Parent != activeAttemptAddress) {
            throw Error(
                auditEvent,
                "must directly follow the active completion attempt"
            );
        }
        _pendingRequestPreparedAddress = null;
        _pendingRequest = null;
        _activeCompletionAttemptAddress = null;
        _activeCorrelationId = null;
    }

    private void AcceptAction(
        SessionJournalAuditEvent auditEvent
    ) {
        EnsureSessionCreated(auditEvent);
        SessionJournalAuditActionFact fact =
            RequireFact<SessionJournalAuditActionFact>(auditEvent);
        EventAddress? activeAttempt =
            _activeCompletionAttemptAddress;
        bool preparedAction =
            auditEvent.Kind
                == SessionEventKind.AgentActionProduced
            && _pendingRequestPreparedAddress.HasValue
            && activeAttempt.HasValue;
        bool importedAction =
            auditEvent.Kind
                == SessionEventKind.ImportedAgentAction
            && _pendingRequestPreparedAddress is null
            && _activeCompletionAttemptAddress is null
            && (_headKind == SessionEventKind.ObservationAccepted
                || _headKind
                    == SessionEventKind.ToolResultObserved
                    && _pendingToolCall is null);
        if (!preparedAction && !importedAction) {
            throw Error(
                auditEvent,
                "does not follow a completion boundary"
            );
        }
        if (preparedAction
            && auditEvent.Parent
                != activeAttempt.GetValueOrDefault()) {
            throw Error(
                auditEvent,
                "must directly descend from active completion attempt "
                + $"{activeAttempt}"
            );
        }
        if (_activeCorrelationId is null
            || !string.Equals(
                fact.CorrelationId,
                _activeCorrelationId,
                StringComparison.Ordinal
            )) {
            throw Error(
                auditEvent,
                "correlation id does not match its active completion "
                + "boundary"
            );
        }
        ValidateActionToolDeclarations(auditEvent, fact.ToolCalls);
        if (fact.LastIssuedToolExecutionSequence
            != _toolExecutionSequenceCheckpoint) {
            throw Error(
                auditEvent,
                $"checkpoint {fact.LastIssuedToolExecutionSequence} "
                + "does not match current last-issued sequence "
                + $"{_toolExecutionSequenceCheckpoint}"
            );
        }
        SessionToolRuntimeIdentity? expectedRuntimeIdentity =
            fact.ToolCalls.Count == 0
                ? null
                : preparedAction
                    ? _pendingRequest?.ToolRuntimeIdentity
                    : fact.ToolRuntimeIdentity;
        EnsureRuntimeIdentityMatch(
            auditEvent,
            expectedRuntimeIdentity,
            fact.ToolRuntimeIdentity
        );
        if (fact.ToolCalls.Count > 0
            && fact.ToolRuntimeIdentity is null) {
            throw Error(
                auditEvent,
                "contains tool calls without a durable tool runtime identity"
            );
        }

        _agentActionCount = checked(_agentActionCount + 1);
        if (auditEvent.Kind
            == SessionEventKind.ImportedAgentAction) {
            _importedAgentActionCount =
                checked(_importedAgentActionCount + 1);
        }
        AppendHistoryContribution(
            fact.SemanticContributionSha256
        );
        _openToolCalls =
            fact.ToolCalls.Count == 0 ? null : fact.ToolCalls;
        _observedResults.Clear();
        _pendingToolCall = fact.ToolCalls.FirstOrDefault();
        _pendingOperationId = null;
        _pendingToolExecutionStarted = false;
        _pendingToolRuntimeIdentity = fact.ToolRuntimeIdentity;
        _pendingRequestPreparedAddress = null;
        _pendingRequest = null;
        _activeCompletionAttemptAddress = null;
        if (fact.ToolCalls.Count == 0) {
            _activeCorrelationId = null;
            _pendingToolRuntimeIdentity = null;
        }
    }

    private void AcceptToolExecutionStarted(
        SessionJournalAuditEvent auditEvent
    ) {
        EnsureSessionCreated(auditEvent);
        SessionJournalAuditToolExecutionStartedFact fact =
            RequireFact<
                SessionJournalAuditToolExecutionStartedFact
            >(auditEvent);
        EnsureOpenAction(auditEvent);
        if (_pendingToolCall is null) {
            throw Error(
                auditEvent,
                "requires a current pending tool call"
            );
        }
        if (_pendingToolExecutionStarted
            || _pendingOperationId is not null) {
            throw Error(
                auditEvent,
                "duplicates an already-started tool execution"
            );
        }
        EnsurePendingToolCallMatch(
            auditEvent,
            _pendingToolCall,
            fact.ToolCallId,
            fact.ToolName,
            fact.RawArgumentsJson
        );
        EnsureRuntimeIdentityMatch(
            auditEvent,
            _pendingToolRuntimeIdentity,
            fact.ToolRuntimeIdentity
        );
        if (fact.ExecutionSequence
            != checked(_toolExecutionSequenceCheckpoint + 1)) {
            throw Error(
                auditEvent,
                "does not reserve the next tool execution sequence"
            );
        }
        _pendingOperationId = fact.OperationId;
        _pendingToolExecutionStarted = true;
        _toolExecutionSequenceCheckpoint =
            fact.ExecutionSequence;
    }

    private void AcceptToolResultObserved(
        SessionJournalAuditEvent auditEvent
    ) {
        EnsureSessionCreated(auditEvent);
        SessionJournalAuditToolResultObservedFact fact =
            RequireFact<
                SessionJournalAuditToolResultObservedFact
            >(auditEvent);
        EnsureOpenAction(auditEvent);
        if (_pendingToolCall is null) {
            throw Error(
                auditEvent,
                "requires a current pending tool call"
            );
        }
        if (!_pendingToolExecutionStarted
            || _pendingOperationId is null) {
            throw Error(
                auditEvent,
                "requires a preceding start for the current tool call"
            );
        }
        EnsurePendingToolCallMatch(
            auditEvent,
            _pendingToolCall,
            fact.ToolCallId,
            fact.ToolName,
            rawArgumentsJson: null
        );
        if (fact.ExecutionSequence
            != _toolExecutionSequenceCheckpoint) {
            throw Error(
                auditEvent,
                "does not repeat the active reserved tool execution "
                + "sequence"
            );
        }
        if (!_observedResults.TryAdd(
                fact.ToolCallId,
                new ObservedToolResult(
                    fact.SemanticResultSha256
                )
            )) {
            throw Error(
                auditEvent,
                $"duplicates result for tool call '{fact.ToolCallId}'"
            );
        }
        _pendingOperationId = null;
        _pendingToolExecutionStarted = false;
        _pendingToolCall = SelectNextPendingDeclaredCall();
        if (_pendingToolCall is null) {
            string[] semanticResultHashes = [
                .. _openToolCalls!.Select(call =>
                    _observedResults[call.ToolCallId]
                        .SemanticResultSha256
                )
            ];
            AppendHistoryContribution(
                SessionHistorySemanticCommitment
                    .ComputeToolResultsContributionSha256(
                        semanticResultHashes
                    )
            );
            _toolResultHistoryCount =
                checked(_toolResultHistoryCount + 1);
            _openToolCalls = null;
            _observedResults.Clear();
            _pendingToolRuntimeIdentity = null;
        }
    }

    private SessionExecutionState DeriveExecutionState()
        => _headKind switch {
            null => new SessionExecutionState(
                SessionExecutionPhase.Empty,
                HeadKind: null
            ),
            SessionEventKind.RuntimeConfigSetup
                or SessionEventKind.SystemPromptSetup =>
                _sessionCreated
                    ? new SessionExecutionState(
                        SessionExecutionPhase.Idle,
                        _headKind,
                        ToolExecutionSequenceCheckpoint:
                            _toolExecutionSequenceCheckpoint
                    )
                    : new SessionExecutionState(
                        SessionExecutionPhase.Empty,
                        _headKind
                    ),
            SessionEventKind.SessionCreated =>
                new SessionExecutionState(
                    SessionExecutionPhase.Idle,
                    _headKind
                ),
            SessionEventKind.ObservationAccepted =>
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingAgentAction,
                    _headKind,
                    ToolExecutionSequenceCheckpoint:
                        _toolExecutionSequenceCheckpoint,
                    ActiveCorrelationId: _activeCorrelationId
                ),
            SessionEventKind.CompletionRequestPrepared =>
                new SessionExecutionState(
                    SessionExecutionPhase
                        .AwaitingCompletionDispatch,
                    _headKind,
                    ToolExecutionSequenceCheckpoint:
                        _toolExecutionSequenceCheckpoint,
                    PendingRequestPreparedAddress:
                        _pendingRequestPreparedAddress,
                    ActiveCorrelationId: _activeCorrelationId
                ),
            SessionEventKind.CompletionAttemptStarted =>
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingCompletion,
                    _headKind,
                    ToolExecutionSequenceCheckpoint:
                        _toolExecutionSequenceCheckpoint,
                    PendingRequestPreparedAddress:
                        _pendingRequestPreparedAddress,
                    ActiveCorrelationId: _activeCorrelationId,
                    ActiveCompletionAttemptAddress:
                        _activeCompletionAttemptAddress
                ),
            SessionEventKind.CompletionAttemptFailed =>
                new SessionExecutionState(
                    SessionExecutionPhase.TurnFailed,
                    _headKind,
                    ToolExecutionSequenceCheckpoint:
                        _toolExecutionSequenceCheckpoint
                ),
            SessionEventKind.AgentActionProduced
                or SessionEventKind.ImportedAgentAction =>
                DeriveActionState(),
            SessionEventKind.ToolExecutionStarted =>
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingToolExecution,
                    _headKind,
                    _pendingToolCall,
                    _pendingOperationId,
                    _pendingToolExecutionStarted,
                    _toolExecutionSequenceCheckpoint,
                    ActiveCorrelationId: _activeCorrelationId,
                    PendingToolRuntimeIdentity:
                        _pendingToolRuntimeIdentity
                ),
            SessionEventKind.ToolResultObserved
                when _pendingToolCall is null =>
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingAgentAction,
                    _headKind,
                    ToolExecutionSequenceCheckpoint:
                        _toolExecutionSequenceCheckpoint,
                    ActiveCorrelationId: _activeCorrelationId
                ),
            SessionEventKind.ToolResultObserved =>
                new SessionExecutionState(
                    SessionExecutionPhase.AwaitingToolExecution,
                    _headKind,
                    _pendingToolCall,
                    ToolExecutionSequenceCheckpoint:
                        _toolExecutionSequenceCheckpoint,
                    ActiveCorrelationId: _activeCorrelationId,
                    PendingToolRuntimeIdentity:
                        _pendingToolRuntimeIdentity
                ),
            _ => throw new NotSupportedException(
                $"Session event kind '{_headKind}' is not implemented "
                + "in the offline execution fold."
            )
        };

    private SessionExecutionState DeriveActionState() {
        if (_openToolCalls is null) {
            return new SessionExecutionState(
                SessionExecutionPhase.Idle,
                _headKind,
                ToolExecutionSequenceCheckpoint:
                    _toolExecutionSequenceCheckpoint
            );
        }
        return new SessionExecutionState(
            SessionExecutionPhase.AwaitingToolExecution,
            _headKind,
            _pendingToolCall,
            ToolExecutionSequenceCheckpoint:
                _toolExecutionSequenceCheckpoint,
            ActiveCorrelationId: _activeCorrelationId,
            PendingToolRuntimeIdentity:
                _pendingToolRuntimeIdentity
        );
    }

    private void EnsureSetupBoundary(
        SessionJournalAuditEvent auditEvent
    ) {
        bool hasNoPendingAction =
            _openToolCalls is null
            && _pendingToolCall is null
            && _pendingOperationId is null
            && !_pendingToolExecutionStarted
            && _pendingRequestPreparedAddress is null;
        bool setupOrIdle =
            _headKind is null
                or SessionEventKind.RuntimeConfigSetup
                or SessionEventKind.SystemPromptSetup
                or SessionEventKind.SessionCreated
                or SessionEventKind.CompletionAttemptFailed
            || _headKind is (
                SessionEventKind.AgentActionProduced
                or SessionEventKind.ImportedAgentAction
            ) && hasNoPendingAction;
        if (!setupOrIdle) {
            throw Error(
                auditEvent,
                "must appear only at setup or idle session boundaries"
            );
        }
    }

    private void EnsureSessionCreated(
        SessionJournalAuditEvent auditEvent
    ) {
        if (!_sessionCreated) {
            throw Error(
                auditEvent,
                "requires a prior session-created marker"
            );
        }
    }

    private void EnsureOpenAction(
        SessionJournalAuditEvent auditEvent
    ) {
        if (_openToolCalls is null) {
            throw Error(
                auditEvent,
                "requires a prior agent action with pending tool calls"
            );
        }
    }

    private static void ValidateActionToolDeclarations(
        SessionJournalAuditEvent auditEvent,
        IReadOnlyList<RawToolCall> calls
    ) {
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawToolCall call in calls) {
            if (string.IsNullOrWhiteSpace(call.ToolCallId)
                || string.IsNullOrWhiteSpace(call.ToolName)
                || string.IsNullOrWhiteSpace(
                    call.RawArgumentsJson
                )) {
                throw Error(
                    auditEvent,
                    "contains a tool call with an empty id, name, "
                    + "or raw arguments"
                );
            }
            if (!callIds.Add(call.ToolCallId)) {
                throw Error(
                    auditEvent,
                    "contains a duplicate tool call id"
                );
            }
        }
    }

    private static void EnsurePendingToolCallMatch(
        SessionJournalAuditEvent auditEvent,
        RawToolCall pending,
        string toolCallId,
        string toolName,
        string? rawArgumentsJson
    ) {
        if (!string.Equals(
                pending.ToolCallId,
                toolCallId,
                StringComparison.Ordinal
            )) {
            throw Error(
                auditEvent,
                "targets a tool call other than the next declared call"
            );
        }
        if (!string.Equals(
                pending.ToolName,
                toolName,
                StringComparison.Ordinal
            )) {
            throw Error(
                auditEvent,
                "uses a tool name other than the next declared call"
            );
        }
        if (rawArgumentsJson is not null
            && !string.Equals(
                pending.RawArgumentsJson,
                rawArgumentsJson,
                StringComparison.Ordinal
            )) {
            throw Error(
                auditEvent,
                "uses raw arguments other than the next declared call"
            );
        }
    }

    private static void EnsureRuntimeIdentityMatch(
        SessionJournalAuditEvent auditEvent,
        SessionToolRuntimeIdentity? expected,
        SessionToolRuntimeIdentity? actual
    ) {
        if (expected != actual) {
            throw Error(
                auditEvent,
                "has a tool runtime identity that does not match its "
                + "durable source"
            );
        }
    }

    private RawToolCall? SelectNextPendingDeclaredCall() {
        foreach (RawToolCall call in _openToolCalls!) {
            if (!_observedResults.ContainsKey(call.ToolCallId)) {
                return call;
            }
        }
        return null;
    }

    private void ResetTurnState(bool resetSequence) {
        _openToolCalls = null;
        _observedResults.Clear();
        _pendingToolCall = null;
        _pendingOperationId = null;
        _pendingToolExecutionStarted = false;
        _pendingToolRuntimeIdentity = null;
        if (resetSequence) {
            _toolExecutionSequenceCheckpoint = 0;
        }
        _pendingRequestPreparedAddress = null;
        _pendingRequest = null;
        _activeCompletionAttemptAddress = null;
        _activeCorrelationId = null;
    }

    private void AppendHistoryContribution(string semanticSha256) {
        _historyContributionHashes.Add(semanticSha256);
        _historyContributionCount =
            checked(_historyContributionCount + 1);
    }

    private static string BuildObservationCorrelationId(
        EventAddress observationAddress
    ) =>
        "atelia.session-journal.turn.v1:"
        + EventAddressTextCodec.Format(observationAddress);

    private static T RequireFact<T>(
        SessionJournalAuditEvent auditEvent
    ) where T : SessionJournalAuditFact =>
        auditEvent.Fact as T
        ?? throw Error(
            auditEvent,
            $"has audit fact '{auditEvent.Fact.GetType().Name}', "
            + $"expected '{typeof(T).Name}'"
        );

    private static InvalidDataException Error(
        SessionJournalAuditEvent auditEvent,
        string message
    ) => new(
        $"{auditEvent.Kind} at {auditEvent.Address} {message}."
    );

    private sealed record ObservedToolResult(
        string SemanticResultSha256
    );
}
