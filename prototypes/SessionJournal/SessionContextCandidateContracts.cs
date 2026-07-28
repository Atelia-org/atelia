using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Store-neutral source of coherent derived context candidates for one exact raw completion boundary.
/// The source owns derived discovery and coherence policy; SessionJournal revalidates every raw-facing
/// assertion before a candidate can influence request materialization.
/// </summary>
public interface ICoherentContextCandidateSource {
    ValueTask<SessionContextCandidateDiscovery> DiscoverAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    );

    ValueTask<SessionContextCandidate> MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Optional store-neutral online maintenance hook. Implementations may update rebuildable derived
/// state but must not mutate the raw SessionJournal; the engine verifies the exact raw head after
/// every callback.
/// </summary>
public interface ISessionMemoryLifecycleCoordinator {
    ValueTask<SessionMemoryLifecycleResult> PrepareAsync(
        SessionJournalEngine engine,
        SessionMemoryLifecycleRequest request,
        CancellationToken cancellationToken
    );
}

public sealed record SessionMemoryLifecycleRequest(
    EventAddress Boundary,
    SessionExecutionPhase Phase,
    string? PendingObservation = null
);

public enum SessionMemoryLifecycleStatus {
    Ready = 0,
    Backpressure = 1,
    Unavailable = 2,
}

public sealed record SessionMemoryLifecycleResult(
    SessionMemoryLifecycleStatus Status,
    string? Detail = null
) {
    public static SessionMemoryLifecycleResult Ready { get; } =
        new(SessionMemoryLifecycleStatus.Ready);
}

/// <summary>
/// Selection policy understood by the core contract. More modes require an explicit contract revision.
/// </summary>
public enum SessionContextSelectionMode {
    Latest = 0,
    NthPrevious = 1,
    Budgeted = 2,
}

/// <summary>
/// A bounded, provider-facing request for a context candidate. These hints are not raw correctness facts.
/// </summary>
public sealed record SessionContextSelectionRequest(
    EventAddress CompletionBoundary,
    SessionContextSelectionMode Mode,
    string CoherenceGroup,
    long? RawSuffixTokenBudget = null,
    long? TotalContextTokenBudget = null,
    int NthPreviousOrdinal = 0,
    int MaxCandidateCount = 32
) {
    public const int MaximumCandidateCount = 64;

    public void ValidateShape() {
        if (CompletionBoundary == default) {
            throw new ArgumentException("Completion boundary cannot be the default EventAddress.", nameof(CompletionBoundary));
        }
        if (!Enum.IsDefined(Mode)) {
            throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Unsupported context selection mode.");
        }
        if (string.IsNullOrWhiteSpace(CoherenceGroup)) {
            throw new ArgumentException("Coherence group cannot be empty.", nameof(CoherenceGroup));
        }
        if (RawSuffixTokenBudget is <= 0) {
            throw new ArgumentOutOfRangeException(nameof(RawSuffixTokenBudget), "Raw suffix token budget must be positive when specified.");
        }
        if (TotalContextTokenBudget is <= 0) {
            throw new ArgumentOutOfRangeException(nameof(TotalContextTokenBudget), "Total context token budget must be positive when specified.");
        }
        if (NthPreviousOrdinal < 0) {
            throw new ArgumentOutOfRangeException(nameof(NthPreviousOrdinal), "Nth-previous ordinal cannot be negative.");
        }
        if (Mode != SessionContextSelectionMode.NthPrevious
            && NthPreviousOrdinal != 0) {
            throw new ArgumentException(
                "A non-zero nth-previous ordinal requires NthPrevious mode.",
                nameof(NthPreviousOrdinal)
            );
        }
        if (Mode == SessionContextSelectionMode.Budgeted
            && RawSuffixTokenBudget is null
            && TotalContextTokenBudget is null) {
            throw new ArgumentException(
                "Budgeted selection requires a raw-suffix or total-context token budget."
            );
        }
        if (MaxCandidateCount is <= 0 or > MaximumCandidateCount) {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCandidateCount),
                $"Candidate count must be between 1 and {MaximumCandidateCount}."
            );
        }
        if (Mode == SessionContextSelectionMode.NthPrevious
            && NthPreviousOrdinal >= MaxCandidateCount) {
            throw new ArgumentException(
                "Nth-previous ordinal must be smaller than the candidate discovery bound.",
                nameof(NthPreviousOrdinal)
            );
        }
    }
}

public enum SessionContextCandidateDiscoveryStatus {
    Candidates = 0,
    EmptyLineage = 1,
}

/// <summary>
/// Lightweight, content-free discovery result. EmptyLineage is an authoritative derived-store
/// statement: missing/stale indexes must be rebuilt and corrupt lineage must fail instead.
/// </summary>
public sealed record SessionContextCandidateDiscovery(
    SessionContextCandidateDiscoveryStatus Status,
    IReadOnlyList<SessionContextCandidateDescriptor> Candidates
);

/// <summary>
/// Opaque exact handle plus raw-facing facts required for one bounded authority pass. Ordinal is
/// lineage position only; it is never interpreted as raw suffix cost.
/// </summary>
public sealed record SessionContextCandidateDescriptor(
    string Handle,
    int Ordinal,
    EventAddress RawStartExclusive,
    SessionContextAnchorSetupReferences AnchorSetups
);

/// <summary>
/// Exact byte identity of a setup event asserted at a candidate's raw anchor.
/// </summary>
public sealed record SessionContextSetupReference(
    EventAddress Address,
    int BodySchemaVersion,
    string PayloadSha256
);

/// <summary>
/// The governing setup pair at <see cref="SessionContextCandidate.RawStartExclusive"/>.
/// </summary>
public sealed record SessionContextAnchorSetupReferences(
    SessionContextSetupReference RuntimeConfig,
    SessionContextSetupReference SystemPrompt
);

/// <summary>
/// One exact derived text contribution. It deliberately carries no artifact, epoch, profile, or store identity.
/// </summary>
public sealed record SessionContextContribution(
    MemoryPackBlockPath Target,
    string ExactText,
    string ContentCodecId,
    string ContentSha256,
    EventAddress SourceRawHead
);

/// <summary>
/// A store-neutral candidate whose raw-facing assertions are verified by SessionJournal.
/// </summary>
public sealed record SessionContextCandidate(
    EventAddress RawStartExclusive,
    SessionContextAnchorSetupReferences AnchorSetups,
    IReadOnlyList<SessionContextContribution> Contributions
);

/// <summary>
/// Canonical content identity for <see cref="SessionContextContribution.ExactText"/>.
/// The codec id is part of the contract so a future textual encoding change is never silently accepted.
/// </summary>
public static class SessionContextContributionHasher {
    public const string CodecId = "atelia.session-journal.context-contribution-text-sha256.v1";

    private static readonly byte[] DomainPrefix =
        Encoding.UTF8.GetBytes("atelia.session-journal.context-contribution-text-sha256.v1\0");

    public static string ComputeSha256(string exactText) {
        ArgumentNullException.ThrowIfNull(exactText);
        byte[] text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetBytes(exactText);
        byte[] input = new byte[checked(DomainPrefix.Length + text.Length)];
        DomainPrefix.CopyTo(input, 0);
        text.CopyTo(input, DomainPrefix.Length);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
