using System.Collections.Immutable;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Revalidates materialized provider content against a descriptor and an
/// authoritative raw interval discovered by SessionJournal.
/// </summary>
internal static class SessionContextCandidateValidator {
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
        if (candidate.SetAdmissionAnchor
                != descriptor.SetAdmissionAnchor
            || candidate.AnchorSetups != descriptor.AnchorSetups) {
            throw new InvalidDataException(
                "Materialized context candidate does not match its discovered descriptor."
            );
        }
        ArgumentNullException.ThrowIfNull(candidate.Contributions);
        ImmutableArray<SessionContextContribution> contributions =
            SessionContextContributionContract.ValidateAndNormalize(
                candidate.Contributions,
                allowEmpty
            );
        foreach (SessionContextContribution contribution
                 in contributions) {
            if (!allowedSourceHeads.Contains(
                    contribution.AbsorbedThrough
                )) {
                throw new InvalidDataException(
                    "A materialized contribution absorbedThrough is outside its authoritative raw interval."
                );
            }
        }
        return contributions;
    }
}
