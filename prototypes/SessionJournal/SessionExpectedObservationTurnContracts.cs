using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Exact durable expectation used to reconcile one Observation append after a
/// caller crash. The selected head is fenced separately from the fresh idle
/// base so a historical or orphan raw address cannot satisfy the proof.
/// </summary>
public sealed record SessionExpectedObservationTurnRequest(
    EventAddress ExpectedSelectedHead,
    EventAddress FreshBaseHead,
    string ExactObservationContent,
    EventAddress? ExpectedObservationAddress = null
);

public enum SessionExpectedObservationConflictReason {
    FreshBaseNotIdle,
    NoVisibleTurn,
    ObservationParentMismatch,
    ObservationAddressMismatch,
    ObservationContentMismatch
}

public sealed record SessionExpectedObservationTurnDiagnostics(
    int HeaderVisits,
    long DecodedLogicalPayloadBytes
);

/// <summary>
/// SessionJournal-owned evidence that the exact canonical Observation raw
/// event is a direct child of the expected fresh base. The enclosing outcome
/// states whether that event remains selected or has been abandoned.
/// </summary>
public sealed class SessionExpectedObservationTurnEvidence {
    internal SessionExpectedObservationTurnEvidence(
        EventAddress capturedHead,
        SessionExecutionBoundaryInspection boundary,
        EventAddress observationAddress,
        EventAddress observationParent,
        SessionExpectedObservationTurnDiagnostics diagnostics
    ) {
        CapturedHead = capturedHead;
        Boundary = boundary;
        ObservationAddress = observationAddress;
        ObservationParent = observationParent;
        Diagnostics = diagnostics;
    }

    public EventAddress CapturedHead { get; }
    public SessionExecutionBoundaryInspection Boundary { get; }
    public EventAddress ObservationAddress { get; }
    public EventAddress ObservationParent { get; }
    public SessionExpectedObservationTurnDiagnostics Diagnostics { get; }
}

/// <summary>
/// Closed result of proving one expected Observation turn at an exact selected
/// head. Constructors are SessionJournal-owned so callers cannot counterfeit
/// proof outcomes.
/// </summary>
public abstract class SessionExpectedObservationTurnReadResult {
    private SessionExpectedObservationTurnReadResult() { }

    public sealed class NotAppended
        : SessionExpectedObservationTurnReadResult {
        internal NotAppended(
            EventAddress selectedHead,
            SessionExecutionBoundaryInspection boundary,
            SessionExpectedObservationTurnDiagnostics diagnostics
        ) {
            SelectedHead = selectedHead;
            Boundary = boundary;
            Diagnostics = diagnostics;
        }

        public EventAddress SelectedHead { get; }
        public SessionExecutionBoundaryInspection Boundary { get; }
        public SessionExpectedObservationTurnDiagnostics Diagnostics { get; }
    }

    public sealed class InProgress
        : SessionExpectedObservationTurnReadResult {
        internal InProgress(SessionExpectedObservationTurnEvidence evidence) {
            Evidence = evidence;
        }

        public SessionExpectedObservationTurnEvidence Evidence { get; }
    }

    /// <summary>
    /// The exact Observation exists as an immutable raw direct child, but it
    /// is detached from the selected head captured at its fresh base.
    /// </summary>
    public sealed class Abandoned
        : SessionExpectedObservationTurnReadResult {
        internal Abandoned(SessionExpectedObservationTurnEvidence evidence) {
            Evidence = evidence;
        }

        public SessionExpectedObservationTurnEvidence Evidence { get; }
    }

    public sealed class Terminal
        : SessionExpectedObservationTurnReadResult {
        internal Terminal(
            SessionExpectedObservationTurnEvidence evidence,
            SessionTerminalActionProjection terminalAction
        ) {
            Evidence = evidence;
            TerminalAction = terminalAction;
        }

        public SessionExpectedObservationTurnEvidence Evidence { get; }
        public SessionTerminalActionProjection TerminalAction { get; }
    }

    public sealed class Conflict
        : SessionExpectedObservationTurnReadResult {
        internal Conflict(
            SessionExpectedObservationConflictReason reason,
            EventAddress capturedHead,
            EventAddress? observedObservationAddress,
            SessionExpectedObservationTurnDiagnostics diagnostics
        ) {
            Reason = reason;
            CapturedHead = capturedHead;
            ObservedObservationAddress = observedObservationAddress;
            Diagnostics = diagnostics;
        }

        public SessionExpectedObservationConflictReason Reason { get; }
        public EventAddress CapturedHead { get; }
        public EventAddress? ObservedObservationAddress { get; }
        public SessionExpectedObservationTurnDiagnostics Diagnostics { get; }
    }

    public sealed class Retryable
        : SessionExpectedObservationTurnReadResult {
        internal Retryable(
            EventAddress expectedSelectedHead,
            EventAddress? observedSelectedHead
        ) {
            ExpectedSelectedHead = expectedSelectedHead;
            ObservedSelectedHead = observedSelectedHead;
        }

        public EventAddress ExpectedSelectedHead { get; }
        public EventAddress? ObservedSelectedHead { get; }
    }

    public sealed class LimitExceeded
        : SessionExpectedObservationTurnReadResult {
        internal LimitExceeded(SessionCompletedTurnsLimit limit) {
            Limit = limit;
        }

        public SessionCompletedTurnsLimit Limit { get; }
    }

    public sealed class UnsupportedSchema
        : SessionExpectedObservationTurnReadResult {
        internal UnsupportedSchema(string detail) {
            Detail = detail;
        }

        public string Detail { get; }
    }

    public sealed class Corruption
        : SessionExpectedObservationTurnReadResult {
        internal Corruption(string detail) {
            Detail = detail;
        }

        public string Detail { get; }
    }
}
