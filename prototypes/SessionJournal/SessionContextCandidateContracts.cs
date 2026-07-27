using System.Collections.Immutable;
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
    ValueTask<SessionContextCandidate?> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Selection policy understood by the core contract. More modes require an explicit contract revision.
/// </summary>
public enum SessionContextSelectionMode {
    Latest = 0,
}

/// <summary>
/// A bounded, provider-facing request for a context candidate. These hints are not raw correctness facts.
/// </summary>
public sealed record SessionContextSelectionRequest(
    EventAddress CompletionBoundary,
    SessionContextSelectionMode Mode,
    string CoherenceGroup,
    int? RawSuffixTokenBudget = null
) {
    public void ValidateShape() {
        if (CompletionBoundary == default) {
            throw new ArgumentException("Completion boundary cannot be the default EventAddress.", nameof(CompletionBoundary));
        }
        if (Mode != SessionContextSelectionMode.Latest) {
            throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Unsupported context selection mode.");
        }
        if (string.IsNullOrWhiteSpace(CoherenceGroup)) {
            throw new ArgumentException("Coherence group cannot be empty.", nameof(CoherenceGroup));
        }
        if (RawSuffixTokenBudget is <= 0) {
            throw new ArgumentOutOfRangeException(nameof(RawSuffixTokenBudget), "Raw suffix token budget must be positive when specified.");
        }
    }
}

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

/// <summary>
/// Core-normalized form of a candidate. It is intentionally internal: only SessionJournal needs the
/// authoritative anchor setup and canonical contribution order.
/// </summary>
internal sealed record ValidatedSessionContextCandidate(
    EventAddress CompletionBoundary,
    EventAddress RawStartExclusive,
    SessionGoverningSetup AnchorGoverningSetup,
    ImmutableArray<SessionContextContribution> CanonicalContributions,
    ImmutableArray<EventAddress> SuffixAddresses,
    int HeaderVisitCount
);
