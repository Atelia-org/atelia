using System.Collections.Immutable;
using System.Text;

namespace Atelia.SessionJournal;

/// <summary>
/// Version-owned, store-neutral validation and canonical ordering for exact
/// context contributions. Raw ancestry validation remains the caller's
/// responsibility because only SessionJournal owns that authority.
/// </summary>
public static class SessionContextContributionContract {
    public const int MaxContributionCount = 128;
    public const int MaxContributionUtf8Bytes = 256 * 1024;
    public const int MaxBlockKeyLength = 256;

    public static ImmutableArray<SessionContextContribution>
        ValidateAndNormalize(
        IReadOnlyList<SessionContextContribution> contributions,
        bool allowEmpty = false
    ) {
        ArgumentNullException.ThrowIfNull(contributions);
        ImmutableArray<SessionContextContribution> snapshot =
            SnapshotOnce(contributions);
        if (snapshot.IsDefaultOrEmpty) {
            if (allowEmpty) {
                return ImmutableArray<SessionContextContribution>.Empty;
            }
            throw new InvalidDataException(
                $"Context candidate must contain 1 through {MaxContributionCount} contributions."
            );
        }

        var targets =
            new HashSet<(ContextHeaderCarrier Carrier, string BlockKey)>();
        foreach (SessionContextContribution contribution in snapshot) {
            ValidateContribution(contribution);
            if (!targets.Add((
                    contribution.Target.Carrier,
                    contribution.Target.BlockKey
                ))) {
                throw new InvalidDataException(
                    "Context candidate contributions must have unique carrier/blockKey targets."
                );
            }
        }

        return [
            .. snapshot
                .OrderBy(
                    static contribution =>
                        GetCarrierRank(contribution.Target.Carrier)
                )
                .ThenBy(
                    static contribution =>
                        contribution.Target.BlockKey,
                    StringComparer.Ordinal
                )
        ];
    }

    private static ImmutableArray<SessionContextContribution> SnapshotOnce(
        IReadOnlyList<SessionContextContribution> contributions
    ) {
        var builder =
            ImmutableArray.CreateBuilder<SessionContextContribution>();
        foreach (SessionContextContribution contribution in contributions) {
            if (builder.Count == MaxContributionCount) {
                throw new InvalidDataException(
                    $"Context candidate must contain at most {MaxContributionCount} contributions."
                );
            }
            ArgumentNullException.ThrowIfNull(contribution);
            builder.Add(contribution);
        }
        return builder.ToImmutable();
    }

    private static void ValidateContribution(
        SessionContextContribution contribution
    ) {
        ArgumentNullException.ThrowIfNull(contribution.Target);
        if (!Enum.IsDefined(contribution.Target.Carrier)) {
            throw new InvalidDataException(
                "Context candidate contribution has an unsupported carrier."
            );
        }
        if (string.IsNullOrWhiteSpace(
                contribution.Target.BlockKey
            )
            || contribution.Target.BlockKey.Length > MaxBlockKeyLength
            || contribution.Target.BlockKey.Contains(
                '\0',
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Context candidate contribution has an invalid block key."
            );
        }
        if (string.IsNullOrEmpty(contribution.ExactText)
            || string.IsNullOrWhiteSpace(contribution.ContentSha256)
            || contribution.AbsorbedThrough == default) {
            throw new InvalidDataException(
                "Context candidate contribution has an empty text, hash, or absorbedThrough."
            );
        }

        int utf8Bytes;
        try {
            utf8Bytes = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true
            ).GetByteCount(contribution.ExactText);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException(
                "Context candidate contribution text is not valid UTF-8.",
                exception
            );
        }
        if (utf8Bytes > MaxContributionUtf8Bytes) {
            throw new InvalidDataException(
                $"Context candidate contribution exceeds {MaxContributionUtf8Bytes} UTF-8 bytes."
            );
        }
        if (!string.Equals(
                contribution.ContentCodecId,
                SessionContextContributionHasher.CodecId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                contribution.ContentSha256,
                SessionContextContributionHasher.ComputeSha256(
                    contribution.ExactText
                ),
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Context candidate contribution content codec or hash does not match exact text."
            );
        }
    }

    private static int GetCarrierRank(ContextHeaderCarrier carrier)
        => carrier switch {
            ContextHeaderCarrier.System => 0,
            ContextHeaderCarrier.Observation => 1,
            ContextHeaderCarrier.Action => 2,
            _ => throw new InvalidDataException(
                "Context candidate contribution has an unsupported carrier."
            )
        };
}
