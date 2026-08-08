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
    /// <summary>
    /// Canonical commitment to the exact raw interval represented by this
    /// materialized window. The value is produced by SessionJournal from the
    /// authoritative event headers and payload hashes.
    /// </summary>
    public string RawRangeSha256 { get; internal init; } = string.Empty;

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

/// <summary>
/// Physical SessionJournal reads performed by one operation. Reusing an already captured prefix
/// contributes zero here even when the operation logically examines retained headers.
/// </summary>
public sealed record SessionCurrentLineageDiagnostics(
    long HeaderVisits,
    long PayloadReads,
    long DecodedPayloadBytes
);

/// <summary>
/// Headers logically examined by one proof operation. This is coverage rather than I/O: for a
/// fresh bounded proof it may match the physical header visits, while reusing an already captured
/// prefix contributes no additional reads. It is therefore reported separately from
/// <see cref="SessionCurrentLineageDiagnostics"/>.
/// </summary>
public sealed class SessionCurrentLineageLogicalCoverage {
    internal SessionCurrentLineageLogicalCoverage(int headerCount) {
        if (headerCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(headerCount),
                "Logical lineage coverage must contain at least one header."
            );
        }
        HeaderCount = headerCount;
    }

    public int HeaderCount { get; }
}

/// <summary>
/// The next Parent address required to continue a deliberately bounded lineage read.
/// </summary>
public sealed class SessionCurrentLineageContinuation {
    internal SessionCurrentLineageContinuation(EventAddress nextAddress) {
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
    internal SessionCurrentLineageBeyondPrefix(
        EventAddress? requiredAnchor,
        EventAddress capturedHead,
        int headerCount,
        EventAddress nextAddress
    ) {
        if (requiredAnchor is { } exactRequiredAnchor
            && exactRequiredAnchor == default) {
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

    /// <summary>
    /// The exact requested ancestor, or null when the bounded search is for a kind-defined
    /// boundary whose address is not yet known (for example SessionCreated).
    /// </summary>
    public EventAddress? RequiredAnchor { get; }
    public EventAddress CapturedHead { get; }
    /// <summary>
    /// The full bounded prefix length measured from <see cref="CapturedHead"/> to the header
    /// immediately before <see cref="NextAddress"/>.
    /// </summary>
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

    internal SessionCurrentLineagePrefix(
        EventAddress capturedHead,
        int maxHeaderCount,
        IReadOnlyList<SessionCurrentLineageHeader> headToOldest,
        SessionCurrentLineageContinuation? continuation,
        SessionCurrentLineageDiagnostics diagnostics
    ) : this(
        "<unbound-test-prefix>",
        capturedHead,
        maxHeaderCount,
        headToOldest,
        continuation,
        diagnostics,
        new object()
    ) {
    }

    internal SessionCurrentLineagePrefix(
        string ownerPath,
        EventAddress capturedHead,
        int maxHeaderCount,
        IReadOnlyList<SessionCurrentLineageHeader> headToOldest,
        SessionCurrentLineageContinuation? continuation,
        SessionCurrentLineageDiagnostics diagnostics,
        object state
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPath);
        ArgumentNullException.ThrowIfNull(state);
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
        if (entries[0] is null) {
            throw new ArgumentException(
                "A bounded lineage prefix cannot contain a null header.",
                nameof(headToOldest)
            );
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
        OwnerPath = ownerPath;
        State = state;
        _indexes = indexes;
    }

    internal string OwnerPath { get; }
    internal object State { get; }

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
/// Closed result of locating the canonical SessionCreated planning boundary inside one bounded
/// raw suffix. A BeyondPrefix result means only that the boundary was not found in this prefix;
/// it does not pretend that an as-yet unknown SessionCreated address was proven off-lineage.
/// </summary>
public abstract class SessionCreatedPlanningSeedReadResult {
    private SessionCreatedPlanningSeedReadResult() { }

    public sealed class Available : SessionCreatedPlanningSeedReadResult {
        internal Available(
            SessionHistoryPlanningSeed seed,
            int rawEventCountAfterStart,
            SessionCurrentLineageDiagnostics diagnostics
        ) {
            ArgumentNullException.ThrowIfNull(seed);
            ArgumentNullException.ThrowIfNull(diagnostics);
            if (rawEventCountAfterStart < 0) {
                throw new ArgumentOutOfRangeException(
                    nameof(rawEventCountAfterStart)
                );
            }
            Seed = seed;
            RawEventCountAfterStart = rawEventCountAfterStart;
            Diagnostics = diagnostics;
        }

        public SessionHistoryPlanningSeed Seed { get; }
        public int RawEventCountAfterStart { get; }
        public SessionCurrentLineageDiagnostics Diagnostics { get; }
    }

    public sealed class BeyondPrefix : SessionCreatedPlanningSeedReadResult {
        internal BeyondPrefix(
            EventAddress capturedHead,
            int headerCount,
            EventAddress nextAddress,
            SessionCurrentLineageDiagnostics diagnostics
        ) {
            if (capturedHead == default) {
                throw new ArgumentException(
                    "The captured lineage head cannot be default.",
                    nameof(capturedHead)
                );
            }
            if (headerCount <= 0) {
                throw new ArgumentOutOfRangeException(nameof(headerCount));
            }
            if (nextAddress == default) {
                throw new ArgumentException(
                    "The next lineage address cannot be default.",
                    nameof(nextAddress)
                );
            }
            ArgumentNullException.ThrowIfNull(diagnostics);
            CapturedHead = capturedHead;
            HeaderCount = headerCount;
            NextAddress = nextAddress;
            Diagnostics = diagnostics;
            ContinuationEvidence = new SessionCurrentLineageBeyondPrefix(
                requiredAnchor: null,
                capturedHead,
                headerCount,
                nextAddress
            );
        }

        public EventAddress CapturedHead { get; }
        public int HeaderCount { get; }
        public EventAddress NextAddress { get; }
        public SessionCurrentLineageDiagnostics Diagnostics { get; }
        public SessionCurrentLineageBeyondPrefix ContinuationEvidence {
            get;
        }
    }
}

/// <summary>
/// Opaque, repository-bound proof that one exact planning interval fits inside its raw-event
/// limit. Producing this token reads headers only; payload materialization is a separate action.
/// </summary>
public sealed class SessionHistoryPlanningWindowProof {
    internal SessionHistoryPlanningWindowProof(
        string ownerPath,
        EventAddress capturedHead,
        EventAddress startExclusive,
        int rawEventCount,
        SessionCurrentLineageDiagnostics diagnostics,
        SessionCurrentLineageLogicalCoverage logicalCoverage,
        object state
    ) {
        OwnerPath = ownerPath;
        CapturedHead = capturedHead;
        StartExclusive = startExclusive;
        RawEventCount = rawEventCount;
        Diagnostics = diagnostics;
        LogicalCoverage = logicalCoverage;
        State = state;
    }

    internal string OwnerPath { get; }
    internal object State { get; }

    public EventAddress CapturedHead { get; }
    public EventAddress StartExclusive { get; }
    public int RawEventCount { get; }
    public SessionCurrentLineageDiagnostics Diagnostics { get; }
    public SessionCurrentLineageLogicalCoverage LogicalCoverage { get; }
}

public abstract class SessionHistoryPlanningWindowProofResult {
    private SessionHistoryPlanningWindowProofResult() { }

    public sealed class Available : SessionHistoryPlanningWindowProofResult {
        internal Available(SessionHistoryPlanningWindowProof proof) {
            ArgumentNullException.ThrowIfNull(proof);
            Proof = proof;
        }

        public SessionHistoryPlanningWindowProof Proof { get; }
    }

    public sealed class BeyondPrefix : SessionHistoryPlanningWindowProofResult {
        internal BeyondPrefix(
            SessionCurrentLineageBeyondPrefix evidence,
            SessionCurrentLineageDiagnostics diagnostics,
            SessionCurrentLineageLogicalCoverage logicalCoverage
        ) {
            ArgumentNullException.ThrowIfNull(evidence);
            ArgumentNullException.ThrowIfNull(diagnostics);
            ArgumentNullException.ThrowIfNull(logicalCoverage);
            Evidence = evidence;
            Diagnostics = diagnostics;
            LogicalCoverage = logicalCoverage;
        }

        public SessionCurrentLineageBeyondPrefix Evidence { get; }
        public SessionCurrentLineageDiagnostics Diagnostics { get; }
        public SessionCurrentLineageLogicalCoverage LogicalCoverage { get; }
    }
}

/// <summary>
/// Opaque, repository-bound header proof that the first runtime-config and system-prompt setup
/// events governing one exact boundary are the expected durable addresses. Producing this token
/// never reads payloads; setup and boundary payload validation is deferred to materialization.
/// </summary>
public sealed class SessionGoverningSetupProof {
    internal SessionGoverningSetupProof(
        string ownerPath,
        EventAddress boundary,
        SessionContextAnchorSetupReferences expectedSetups,
        SessionCurrentLineageDiagnostics diagnostics,
        SessionCurrentLineageLogicalCoverage logicalCoverage,
        object state
    ) {
        OwnerPath = ownerPath;
        Boundary = boundary;
        ExpectedSetups = expectedSetups;
        Diagnostics = diagnostics;
        LogicalCoverage = logicalCoverage;
        State = state;
    }

    internal string OwnerPath { get; }
    internal object State { get; }

    public EventAddress Boundary { get; }
    public SessionContextAnchorSetupReferences ExpectedSetups { get; }
    public SessionCurrentLineageDiagnostics Diagnostics { get; }
    public SessionCurrentLineageLogicalCoverage LogicalCoverage { get; }
}

/// <summary>
/// Header-only evidence that a governing setup pair could not be fully proven inside one bounded
/// Parent prefix. The next address is an explicit continuation; no hidden paging was performed.
/// </summary>
public sealed class SessionGoverningSetupBeyondPrefix {
    internal SessionGoverningSetupBeyondPrefix(
        EventAddress boundary,
        SessionContextAnchorSetupReferences expectedSetups,
        EventAddress capturedHead,
        int headerCount,
        EventAddress nextAddress,
        EventAddress requiredAnchor
    ) {
        Boundary = boundary;
        ExpectedSetups = expectedSetups;
        HeaderCount = headerCount;
        NextAddress = nextAddress;
        RequiredAnchor = requiredAnchor;
        ContinuationEvidence = new SessionCurrentLineageBeyondPrefix(
            requiredAnchor,
            capturedHead,
            headerCount,
            nextAddress
        );
    }

    public EventAddress Boundary { get; }
    public SessionContextAnchorSetupReferences ExpectedSetups { get; }
    /// <summary>
    /// The full bounded prefix length measured from the continuation evidence captured head.
    /// For a boundary inside that prefix, the boundary-relative subset is reported separately as
    /// <see cref="SessionCurrentLineageLogicalCoverage"/> on the returned result.
    /// </summary>
    public int HeaderCount { get; }
    public EventAddress NextAddress { get; }
    public EventAddress RequiredAnchor { get; }
    public SessionCurrentLineageBeyondPrefix ContinuationEvidence {
        get;
    }
}

/// <summary>
/// Closed result of proving the exact governing setup addresses for one replay boundary.
/// </summary>
public abstract class SessionGoverningSetupProofResult {
    private SessionGoverningSetupProofResult() { }

    public sealed class Available : SessionGoverningSetupProofResult {
        internal Available(SessionGoverningSetupProof proof) {
            ArgumentNullException.ThrowIfNull(proof);
            Proof = proof;
        }

        public SessionGoverningSetupProof Proof { get; }
    }

    public sealed class BeyondPrefix : SessionGoverningSetupProofResult {
        internal BeyondPrefix(
            SessionGoverningSetupBeyondPrefix evidence,
            SessionCurrentLineageDiagnostics diagnostics,
            SessionCurrentLineageLogicalCoverage logicalCoverage
        ) {
            ArgumentNullException.ThrowIfNull(evidence);
            ArgumentNullException.ThrowIfNull(diagnostics);
            ArgumentNullException.ThrowIfNull(logicalCoverage);
            Evidence = evidence;
            Diagnostics = diagnostics;
            LogicalCoverage = logicalCoverage;
        }

        public SessionGoverningSetupBeyondPrefix Evidence { get; }
        public SessionCurrentLineageDiagnostics Diagnostics { get; }
        public SessionCurrentLineageLogicalCoverage LogicalCoverage { get; }
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
