using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// One dependency-closed history message materialized from a bounded raw interval.
/// </summary>
public sealed record SessionHistoryPlanningUnit(
    IHistoryMessage Message,
    EventAddress SourceStartInclusive,
    EventAddress SourceEndInclusive
);

/// <summary>
/// A raw boundary after which <see cref="CompletedUnitCount"/> complete history units have
/// materialized. Only these boundaries may be selected as a replay-safe context anchor.
/// </summary>
public sealed record SessionHistoryPlanningBoundary(
    EventAddress Address,
    int CompletedUnitCount
);

public sealed record SessionHistoryPlanningDiagnostics(
    long HeaderVisits,
    long PayloadReads,
    long DecodedPayloadBytes,
    int DecodedEventCount
);

/// <summary>
/// Store-neutral, bounded planning projection for one captured raw head. The interval is
/// exclusive of <see cref="StartExclusive"/> and inclusive of <see cref="ObservedRawHead"/>.
/// </summary>
public sealed record SessionHistoryPlanningWindow(
    EventAddress ObservedRawHead,
    EventAddress StartExclusive,
    SessionContextAnchorSetupReferences StartSetups,
    SessionContextAnchorSetupReferences EndSetups,
    IReadOnlyList<EventAddress> RawAddresses,
    IReadOnlyList<SessionHistoryPlanningUnit> Units,
    IReadOnlyList<SessionHistoryPlanningBoundary> ReplaySafeBoundaries,
    IReadOnlyDictionary<
        EventAddress,
        SessionContextAnchorSetupReferences
    > ReplaySafeBoundarySetups,
    SessionHistoryPlanningDiagnostics Diagnostics
) {
    internal IReadOnlyList<SessionRawRangeHashEntry> RawHashEntries {
        get;
        init;
    } = Array.Empty<SessionRawRangeHashEntry>();

    internal SessionTailContextProjection.TailFoldResult? Folded {
        get;
        init;
    }
}

/// <summary>
/// One header-only node on the captured selected branch Parent lineage.
/// </summary>
public sealed record SessionCurrentLineageHeader(
    EventAddress Address,
    EventAddress? Parent,
    SessionEventKind Kind
);

public sealed record SessionCurrentLineageDiagnostics(
    long HeaderVisits,
    long PayloadReads,
    long DecodedPayloadBytes
);

/// <summary>
/// Store-neutral header-only snapshot of the selected branch lineage. Entries are ordered from
/// captured head toward the root. No event payload is read or decoded while producing it.
/// </summary>
public sealed record SessionCurrentLineageSnapshot(
    EventAddress CapturedHead,
    IReadOnlyList<SessionCurrentLineageHeader> HeadToRoot,
    SessionCurrentLineageDiagnostics Diagnostics
);

/// <summary>
/// Core-produced verified setup seed for one replay-safe planning start. Consumers may retain and
/// pass it back to SessionJournal, but cannot construct or alter its decoded setup authority.
/// </summary>
public sealed class SessionHistoryPlanningSeed {
    internal SessionHistoryPlanningSeed(
        string ownerPath,
        EventAddress address,
        SessionContextAnchorSetupReferences setups,
        SessionGoverningSetup governingSetup,
        SessionExecutionRecovery? executionRecovery = null
    ) {
        OwnerPath = ownerPath;
        Address = address;
        Setups = setups;
        GoverningSetup = governingSetup;
        ExecutionRecovery = executionRecovery;
    }

    internal string OwnerPath { get; }
    internal SessionGoverningSetup GoverningSetup { get; }
    internal SessionExecutionRecovery? ExecutionRecovery { get; }

    public EventAddress Address { get; }
    public SessionContextAnchorSetupReferences Setups { get; }
}

public sealed record SessionHistoryPlanningSeedBatch(
    SessionCurrentLineageSnapshot Lineage,
    IReadOnlyList<SessionHistoryPlanningSeed> Seeds,
    SessionCurrentLineageDiagnostics Diagnostics
);
