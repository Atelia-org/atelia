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
/// The next Parent address required to continue a deliberately bounded lineage read.
/// </summary>
public sealed class SessionCurrentLineageContinuation {
    public SessionCurrentLineageContinuation(EventAddress nextAddress) {
        if (nextAddress == default) {
            throw new ArgumentException(
                "A lineage continuation address cannot be default.",
                nameof(nextAddress)
            );
        }
        NextAddress = nextAddress;
    }

    public EventAddress NextAddress { get; }
}

/// <summary>
/// Stable evidence that an exact required anchor was not proven within one bounded prefix.
/// This is not evidence that the anchor is off the selected lineage.
/// </summary>
public sealed class SessionCurrentLineageBeyondPrefix {
    public SessionCurrentLineageBeyondPrefix(
        EventAddress requiredAnchor,
        EventAddress capturedHead,
        int headerCount,
        EventAddress nextAddress
    ) {
        if (requiredAnchor == default) {
            throw new ArgumentException(
                "The required lineage anchor cannot be default.",
                nameof(requiredAnchor)
            );
        }
        if (capturedHead == default) {
            throw new ArgumentException(
                "The captured lineage head cannot be default.",
                nameof(capturedHead)
            );
        }
        if (headerCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(headerCount),
                "A bounded lineage prefix must contain at least one header."
            );
        }
        if (nextAddress == default) {
            throw new ArgumentException(
                "The next lineage address cannot be default.",
                nameof(nextAddress)
            );
        }
        RequiredAnchor = requiredAnchor;
        CapturedHead = capturedHead;
        HeaderCount = headerCount;
        NextAddress = nextAddress;
    }

    public EventAddress RequiredAnchor { get; }
    public EventAddress CapturedHead { get; }
    public int HeaderCount { get; }
    public EventAddress NextAddress { get; }
}

/// <summary>
/// Closed result of looking for an exact raw address in one bounded lineage prefix.
/// OffLineage is produced only after the prefix has reached the root.
/// </summary>
public abstract class SessionCurrentLineageAnchorLookup {
    private SessionCurrentLineageAnchorLookup() { }

    public sealed class Found : SessionCurrentLineageAnchorLookup {
        internal Found(int index) => Index = index;

        public int Index { get; }
    }

    public sealed class OffLineage : SessionCurrentLineageAnchorLookup {
        internal OffLineage(
            EventAddress requiredAnchor,
            EventAddress capturedHead
        ) {
            RequiredAnchor = requiredAnchor;
            CapturedHead = capturedHead;
        }

        public EventAddress RequiredAnchor { get; }
        public EventAddress CapturedHead { get; }
    }

    public sealed class BeyondPrefix : SessionCurrentLineageAnchorLookup {
        internal BeyondPrefix(
            SessionCurrentLineageBeyondPrefix evidence
        ) => Evidence = evidence;

        public SessionCurrentLineageBeyondPrefix Evidence { get; }
    }
}

/// <summary>
/// Store-neutral, header-only bounded prefix of one exact Parent lineage. Entries are ordered
/// from the captured head toward the root. A continuation and a reached root are mutually
/// exclusive; callers must explicitly request another read rather than receiving hidden paging.
/// </summary>
public sealed class SessionCurrentLineagePrefix {
    private readonly IReadOnlyDictionary<EventAddress, int> _indexes;

    public SessionCurrentLineagePrefix(
        EventAddress capturedHead,
        int maxHeaderCount,
        IReadOnlyList<SessionCurrentLineageHeader> headToOldest,
        SessionCurrentLineageContinuation? continuation,
        SessionCurrentLineageDiagnostics diagnostics
    ) {
        if (capturedHead == default) {
            throw new ArgumentException(
                "The captured lineage head cannot be default.",
                nameof(capturedHead)
            );
        }
        if (maxHeaderCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxHeaderCount),
                "A bounded lineage read must allow at least one header."
            );
        }
        ArgumentNullException.ThrowIfNull(headToOldest);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (headToOldest.Count == 0
            || headToOldest.Count > maxHeaderCount) {
            throw new ArgumentException(
                "A bounded lineage prefix must contain between one and maxHeaderCount headers.",
                nameof(headToOldest)
            );
        }
        var entries = new SessionCurrentLineageHeader[
            headToOldest.Count
        ];
        for (int index = 0; index < entries.Length; index++) {
            entries[index] = headToOldest[index];
        }
        if (entries[0].Address != capturedHead) {
            throw new ArgumentException(
                "The first bounded lineage header must equal the captured head.",
                nameof(headToOldest)
            );
        }

        var indexes = new Dictionary<EventAddress, int>(entries.Length);
        for (int index = 0; index < entries.Length; index++) {
            SessionCurrentLineageHeader entry = entries[index]
                ?? throw new ArgumentException(
                    "A bounded lineage prefix cannot contain a null header.",
                    nameof(headToOldest)
                );
            if (entry.Address == default) {
                throw new ArgumentException(
                    "A bounded lineage header address cannot be default.",
                    nameof(headToOldest)
                );
            }
            if (!Enum.IsDefined(entry.Kind)) {
                throw new ArgumentException(
                    $"The bounded lineage header at '{entry.Address}' has unknown kind '{entry.Kind}'.",
                    nameof(headToOldest)
                );
            }
            if (!indexes.TryAdd(entry.Address, index)) {
                throw new ArgumentException(
                    $"The bounded lineage prefix repeats address '{entry.Address}'.",
                    nameof(headToOldest)
                );
            }
            if (index > 0
                && entries[index - 1].Parent != entry.Address) {
                throw new ArgumentException(
                    "Bounded lineage headers must form one contiguous Parent chain.",
                    nameof(headToOldest)
                );
            }
        }

        SessionCurrentLineageHeader tail = entries[^1];
        if (continuation is null) {
            if (tail.Parent is not null) {
                throw new ArgumentException(
                    "A bounded lineage prefix that has not reached the root requires a continuation.",
                    nameof(continuation)
                );
            }
        }
        else {
            if (entries.Length != maxHeaderCount
                || tail.Parent != continuation.NextAddress) {
                throw new ArgumentException(
                    "A lineage continuation requires a full prefix and must equal the tail Parent.",
                    nameof(continuation)
                );
            }
            if (indexes.ContainsKey(continuation.NextAddress)) {
                throw new ArgumentException(
                    "A lineage continuation cannot point back into the bounded prefix.",
                    nameof(continuation)
                );
            }
        }
        if (diagnostics.HeaderVisits != entries.Length
            || diagnostics.PayloadReads != 0
            || diagnostics.DecodedPayloadBytes != 0) {
            throw new ArgumentException(
                "Bounded lineage diagnostics must account for every header and contain no payload reads.",
                nameof(diagnostics)
            );
        }

        CapturedHead = capturedHead;
        MaxHeaderCount = maxHeaderCount;
        HeadToOldest = Array.AsReadOnly(entries);
        Continuation = continuation;
        Diagnostics = diagnostics;
        _indexes = indexes;
    }

    public EventAddress CapturedHead { get; }
    public int MaxHeaderCount { get; }
    public IReadOnlyList<SessionCurrentLineageHeader> HeadToOldest { get; }
    public SessionCurrentLineageContinuation? Continuation { get; }
    public SessionCurrentLineageDiagnostics Diagnostics { get; }
    public bool IsComplete => Continuation is null;

    public SessionCurrentLineageAnchorLookup Lookup(
        EventAddress requiredAnchor
    ) {
        if (requiredAnchor == default) {
            throw new ArgumentException(
                "The required lineage anchor cannot be default.",
                nameof(requiredAnchor)
            );
        }
        if (_indexes.TryGetValue(requiredAnchor, out int index)) {
            return new SessionCurrentLineageAnchorLookup.Found(index);
        }
        if (Continuation is null) {
            return new SessionCurrentLineageAnchorLookup.OffLineage(
                requiredAnchor,
                CapturedHead
            );
        }
        return new SessionCurrentLineageAnchorLookup.BeyondPrefix(
            new SessionCurrentLineageBeyondPrefix(
                requiredAnchor,
                CapturedHead,
                HeadToOldest.Count,
                Continuation.NextAddress
            )
        );
    }
}

/// <summary>
/// Closed result of a planning-window read whose raw interval is bounded before payload access.
/// </summary>
public abstract class SessionHistoryPlanningWindowReadResult {
    private SessionHistoryPlanningWindowReadResult() { }

    public sealed class Available : SessionHistoryPlanningWindowReadResult {
        internal Available(
            SessionHistoryPlanningWindow window,
            SessionCurrentLineageDiagnostics prefixDiagnostics
        ) {
            Window = window;
            PrefixDiagnostics = prefixDiagnostics;
        }

        public SessionHistoryPlanningWindow Window { get; }
        public SessionCurrentLineageDiagnostics PrefixDiagnostics { get; }
    }

    public sealed class BeyondPrefix : SessionHistoryPlanningWindowReadResult {
        internal BeyondPrefix(
            SessionCurrentLineageBeyondPrefix evidence,
            SessionCurrentLineageDiagnostics diagnostics
        ) {
            Evidence = evidence;
            Diagnostics = diagnostics;
        }

        public SessionCurrentLineageBeyondPrefix Evidence { get; }
        public SessionCurrentLineageDiagnostics Diagnostics { get; }
    }
}

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
