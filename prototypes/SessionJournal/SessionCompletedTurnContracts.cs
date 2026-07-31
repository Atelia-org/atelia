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
