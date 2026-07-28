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
    ValueTask<SessionContextCandidateSelection> SelectAsync(
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
/// An exact provider-facing request for one context candidate. The source owns lineage traversal;
/// token budgets remain runtime-local guards and never influence which candidate is selected.
/// </summary>
public sealed record SessionContextSelectionRequest(
    EventAddress CompletionBoundary,
    int NthPrevious
) {
    public void ValidateShape() {
        if (CompletionBoundary == default) {
            throw new ArgumentException("Completion boundary cannot be the default EventAddress.", nameof(CompletionBoundary));
        }
        if (NthPrevious < 0) {
            throw new ArgumentOutOfRangeException(nameof(NthPrevious), "Nth-previous ordinal cannot be negative.");
        }
    }
}

public enum SessionContextCandidateSelectionStatus {
    Selected = 0,
    EmptyLineage = 1,
    OrdinalUnavailable = 2,
}

/// <summary>
/// Lightweight, content-free exact selection result. EmptyLineage means no set exists at all;
/// OrdinalUnavailable means the lineage exists but is shorter than the requested ordinal.
/// </summary>
public sealed record SessionContextCandidateSelection(
    SessionContextCandidateSelectionStatus Status,
    SessionContextCandidateDescriptor? Candidate
);

/// <summary>
/// Opaque exact handle plus raw-facing facts required for one bounded authority pass.
/// </summary>
public sealed record SessionContextCandidateDescriptor(
    string Handle,
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
