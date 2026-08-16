using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// One raw, completed visible turn. The observation content is intentionally not display-normalized;
/// Host-specific wrappers and presentation cleanup remain outside SessionJournal.
/// </summary>
public sealed record SessionCompletedTurnProjection(
    EventAddress ObservationAddress,
    string ObservationContent,
    SessionTerminalActionProjection TerminalAction
);

public sealed record SessionTerminalActionProjection(
    EventAddress Address,
    ActionMessage Message
);

/// <summary>
/// Completed visible turns at one immutable raw head, ordered newest first.
/// Protocol events, tool results, and non-terminal tool-call Actions are not returned.
/// </summary>
public sealed record SessionCompletedTurnsSnapshot(
    EventAddress? CapturedHead,
    IReadOnlyList<SessionCompletedTurnProjection> Turns
);

public enum SessionCompletedTurnsLimit {
    MaximumExaminedHeaders,
    MaximumDecodedLogicalPayloadBytes
}

/// <summary>
/// Closed outcome of the bounded completed-turn projection. Constructors are SessionJournal-owned;
/// callers can inspect durable evidence without constructing counterfeit outcomes.
/// </summary>
public abstract class SessionCompletedTurnsReadResult {
    private SessionCompletedTurnsReadResult() { }

    public sealed class Snapshot : SessionCompletedTurnsReadResult {
        internal Snapshot(SessionCompletedTurnsSnapshot value) {
            Value = value;
        }
        public SessionCompletedTurnsSnapshot Value { get; }
    }

    public sealed class LimitExceeded : SessionCompletedTurnsReadResult {
        internal LimitExceeded(SessionCompletedTurnsLimit limit) {
            Limit = limit;
        }
        public SessionCompletedTurnsLimit Limit { get; }
    }

    public sealed class UnsupportedSchema : SessionCompletedTurnsReadResult {
        internal UnsupportedSchema(string detail) {
            Detail = detail;
        }
        public string Detail { get; }
    }

    public sealed class Corruption : SessionCompletedTurnsReadResult {
        internal Corruption(string detail) {
            Detail = detail;
        }
        public string Detail { get; }
    }
}

/// <summary>
/// Repository- and branch-bound capability for one exact completed-turn rewind. The observation
/// remains readable so a host can encode its success receipt before attempting the ref CAS.
/// </summary>
public sealed class SessionPreparedCompletedTurnRewind {
    internal SessionPreparedCompletedTurnRewind(
        string ownerPath,
        RefId branchRefId,
        EventAddress expectedHead,
        EventAddress newHead,
        SessionRetractedTurnProjection turn
    ) {
        OwnerPath = ownerPath;
        BranchRefId = branchRefId;
        ExpectedHead = expectedHead;
        NewHead = newHead;
        Turn = turn;
    }

    internal string OwnerPath { get; }
    internal RefId BranchRefId { get; }
    internal EventAddress NewHead { get; }
    internal SessionRetractedTurnProjection Turn { get; }

    public EventAddress ExpectedHead { get; }
    public EventAddress ObservationAddress => Turn.ObservationAddress;
    public string ObservationContent => Turn.ObservationContent;
}

public abstract class SessionCompletedTurnRewindPrepareResult {
    private SessionCompletedTurnRewindPrepareResult() { }

    public sealed class Prepared : SessionCompletedTurnRewindPrepareResult {
        internal Prepared(SessionPreparedCompletedTurnRewind value) {
            Value = value;
        }
        public SessionPreparedCompletedTurnRewind Value { get; }
    }

    public sealed class Unavailable : SessionCompletedTurnRewindPrepareResult {
        internal Unavailable(SessionExecutionBoundaryInspection boundary) {
            Boundary = boundary;
        }
        public SessionExecutionBoundaryInspection Boundary { get; }
    }

    public sealed class Retryable : SessionCompletedTurnRewindPrepareResult {
        internal Retryable(
            EventAddress expectedHead,
            EventAddress? observedHead
        ) {
            ExpectedHead = expectedHead;
            ObservedHead = observedHead;
        }
        public EventAddress ExpectedHead { get; }
        public EventAddress? ObservedHead { get; }
    }

    public sealed class LimitExceeded : SessionCompletedTurnRewindPrepareResult {
        internal LimitExceeded(SessionCompletedTurnsLimit limit) {
            Limit = limit;
        }
        public SessionCompletedTurnsLimit Limit { get; }
    }

    public sealed class UnsupportedSchema : SessionCompletedTurnRewindPrepareResult {
        internal UnsupportedSchema(string detail) {
            Detail = detail;
        }
        public string Detail { get; }
    }

    public sealed class Corruption : SessionCompletedTurnRewindPrepareResult {
        internal Corruption(string detail) {
            Detail = detail;
        }
        public string Detail { get; }
    }
}

/// <summary>
/// The raw visible turn removed from the selected lineage. A known failed turn has no terminal
/// Action; a completed rewind carries the same terminal Action returned by recent projection.
/// </summary>
public sealed record SessionRetractedTurnProjection(
    EventAddress ObservationAddress,
    string ObservationContent,
    SessionTerminalActionProjection? TerminalAction
);

/// <summary>
/// Shared exact-head result for the two narrow turn-retraction operations. Unavailable reports the
/// exact boundary without inventing a second reason taxonomy: the requested operation and boundary
/// phase/kind completely determine why no move is legal.
/// </summary>
public abstract record SessionTurnRetractionResult {
    private SessionTurnRetractionResult() { }

    public sealed record Moved(
        EventAddress PreviousHead,
        EventAddress NewHead,
        SessionRetractedTurnProjection Turn
    ) : SessionTurnRetractionResult;

    public sealed record Unavailable(
        SessionExecutionBoundaryInspection Boundary
    ) : SessionTurnRetractionResult;

    public sealed record Retryable(
        EventAddress ExpectedHead,
        EventAddress? ObservedHead
    ) : SessionTurnRetractionResult;
}
