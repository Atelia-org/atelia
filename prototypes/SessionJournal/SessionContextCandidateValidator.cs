using System.Collections.Immutable;
using System.Text;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Revalidates materialized provider content against a descriptor and an
/// authoritative raw interval discovered by SessionJournal.
/// </summary>
internal static class SessionContextCandidateValidator {
    private const int MaxContributionCount = 128;
    private const int MaxContributionUtf8Bytes = 256 * 1024;

    internal static ImmutableArray<SessionContextContribution>
        ValidateMaterializedCandidate(
        SessionContextCandidateDescriptor descriptor,
        SessionContextCandidate candidate,
        IReadOnlySet<EventAddress> allowedSourceHeads,
        bool allowEmpty
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(allowedSourceHeads);
        if (candidate.RawStartExclusive != descriptor.RawStartExclusive
            || candidate.AnchorSetups != descriptor.AnchorSetups) {
            throw new InvalidDataException(
                "Materialized context candidate does not match its discovered descriptor."
            );
        }
        ArgumentNullException.ThrowIfNull(candidate.Contributions);
        ImmutableArray<SessionContextContribution> contributions =
            SnapshotContributions(candidate.Contributions);
        if (contributions.IsDefaultOrEmpty) {
            if (allowEmpty) {
                return ImmutableArray<SessionContextContribution>.Empty;
            }
            throw new InvalidDataException(
                $"Context candidate must contain 1 through {MaxContributionCount} contributions."
            );
        }
        foreach (SessionContextContribution contribution
                 in contributions) {
            if (!allowedSourceHeads.Contains(
                    contribution.SourceRawHead
                )) {
                throw new InvalidDataException(
                    "A materialized contribution sourceRawHead is outside its authoritative raw interval."
                );
            }
        }
        return NormalizeContributions(contributions);
    }

    /// <summary>
    /// Establishes the contract trust boundary. Provider collections may be lazy or mutable; no raw
    /// validation may observe them more than once. Count is intentionally not consulted because an
    /// adversarial IReadOnlyList can make it disagree with enumeration.
    /// </summary>
    private static ImmutableArray<SessionContextContribution> SnapshotContributions(
        IReadOnlyList<SessionContextContribution> contributions
    ) {
        var builder = ImmutableArray.CreateBuilder<SessionContextContribution>();
        foreach (SessionContextContribution contribution in contributions) {
            if (builder.Count == MaxContributionCount) {
                throw new InvalidDataException($"Context candidate must contain at most {MaxContributionCount} contributions.");
            }
            ArgumentNullException.ThrowIfNull(contribution);
            builder.Add(contribution);
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<SessionContextContribution> NormalizeContributions(
        ImmutableArray<SessionContextContribution> contributions
    ) {
        var targets = new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        foreach (SessionContextContribution contribution in contributions) {
            ArgumentNullException.ThrowIfNull(contribution.Target);
            if (!Enum.IsDefined(contribution.Target.Carrier)) {
                throw new InvalidDataException("Context candidate contribution has an unsupported carrier.");
            }
            if (string.IsNullOrWhiteSpace(contribution.Target.BlockKey)
                || string.IsNullOrEmpty(contribution.ExactText)
                || string.IsNullOrWhiteSpace(contribution.ContentSha256)
                || contribution.SourceRawHead == default) {
                throw new InvalidDataException("Context candidate contribution has an empty target, text, hash, or sourceRawHead.");
            }
            int utf8Bytes;
            try {
                utf8Bytes = new UTF8Encoding(false, true).GetByteCount(contribution.ExactText);
            }
            catch (EncoderFallbackException exception) {
                throw new InvalidDataException("Context candidate contribution text is not valid UTF-8.", exception);
            }
            if (utf8Bytes > MaxContributionUtf8Bytes) {
                throw new InvalidDataException($"Context candidate contribution exceeds {MaxContributionUtf8Bytes} UTF-8 bytes.");
            }
            if (!string.Equals(contribution.ContentCodecId, SessionContextContributionHasher.CodecId, StringComparison.Ordinal)
                || !string.Equals(
                    contribution.ContentSha256,
                    SessionContextContributionHasher.ComputeSha256(contribution.ExactText),
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException("Context candidate contribution content codec or hash does not match exact text.");
            }
            if (!targets.Add((contribution.Target.Carrier, contribution.Target.BlockKey))) {
                throw new InvalidDataException("Context candidate contributions must have unique carrier/blockKey targets.");
            }
        }

        return [
            .. contributions
                .OrderBy(static contribution => GetCarrierRank(contribution.Target.Carrier))
                .ThenBy(static contribution => contribution.Target.BlockKey, StringComparer.Ordinal)
        ];
    }

    private static int GetCarrierRank(MemoryPackCarrier carrier)
        => carrier switch {
            MemoryPackCarrier.System => 0,
            MemoryPackCarrier.Observation => 1,
            MemoryPackCarrier.Action => 2,
            _ => throw new InvalidDataException("Context candidate contribution has an unsupported carrier.")
        };
}
