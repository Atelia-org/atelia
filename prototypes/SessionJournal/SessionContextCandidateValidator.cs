using System.Collections.Immutable;
using System.Text;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Revalidates all raw-facing candidate assertions at a fixed completion boundary.
/// Derived providers are intentionally not trusted with raw lineage, governing setup, or target correctness.
/// </summary>
internal static class SessionContextCandidateValidator {
    private const int MaxContributionCount = 128;
    private const int MaxContributionUtf8Bytes = 256 * 1024;

    public static ValidatedSessionContextCandidate Validate(
        SessionJournalEventReader reader,
        EventAddress completionBoundary,
        SessionGoverningSetup anchorGoverningSetup,
        SessionContextCandidate candidate,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(anchorGoverningSetup);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(candidate.AnchorSetups);
        ArgumentNullException.ThrowIfNull(candidate.Contributions);

        if (completionBoundary == default) {
            throw new ArgumentException("Completion boundary cannot be the default EventAddress.", nameof(completionBoundary));
        }
        if (candidate.RawStartExclusive == default) {
            throw new InvalidDataException("Context candidate rawStartExclusive cannot be the default EventAddress.");
        }
        if (candidate.RawStartExclusive == completionBoundary) {
            throw new InvalidDataException("Context candidate rawStartExclusive must be a strict ancestor of completionBoundary.");
        }
        if (anchorGoverningSetup.Head != candidate.RawStartExclusive) {
            throw new InvalidDataException("Context candidate anchor governing setup must be resolved at rawStartExclusive.");
        }
        ImmutableArray<SessionContextContribution> contributions = SnapshotContributions(
            candidate.Contributions
        );
        if (contributions.IsDefaultOrEmpty) {
            throw new InvalidDataException($"Context candidate must contain 1 through {MaxContributionCount} contributions.");
        }

        var sourceHeads = new HashSet<EventAddress>();
        foreach (SessionContextContribution contribution in contributions) {
            if (contribution.SourceRawHead == default) {
                throw new InvalidDataException("Context candidate contribution sourceRawHead cannot be the default EventAddress.");
            }
            sourceHeads.Add(contribution.SourceRawHead);
        }
        ValidatedRawInterval interval = ValidateAnchorAncestryAndSourceHeads(
            reader,
            completionBoundary,
            candidate.RawStartExclusive,
            sourceHeads,
            cancellationToken
        );
        SessionTailContextProjection.ValidateReplaySafeBoundary(reader, candidate.RawStartExclusive);

        ValidateAnchorSetupReferences(reader, candidate.AnchorSetups, anchorGoverningSetup);

        return new ValidatedSessionContextCandidate(
            completionBoundary,
            candidate.RawStartExclusive,
            anchorGoverningSetup,
            NormalizeContributions(contributions),
            interval.SuffixAddresses,
            interval.HeaderVisitCount
        );
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

    private static ValidatedRawInterval ValidateAnchorAncestryAndSourceHeads(
        SessionJournalEventReader reader,
        EventAddress completionBoundary,
        EventAddress anchor,
        HashSet<EventAddress> sourceHeads,
        CancellationToken cancellationToken
    ) {
        var reverseSuffix = new List<EventAddress>();
        int headerVisitCount = 0;
        EventAddress? cursor = completionBoundary;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            EventFrameHeader header = reader.ReadEventHeaderPreview(address).Unwrap();
            headerVisitCount++;
            ValidateSessionHeader(address, header);
            sourceHeads.Remove(address);
            if (address == anchor) {
                if (sourceHeads.Count != 0) {
                    throw new InvalidDataException(
                        "At least one context contribution sourceRawHead is not on the authoritative interval from anchor through completionBoundary."
                    );
                }
                reverseSuffix.Reverse();
                return new ValidatedRawInterval(
                    [.. reverseSuffix],
                    headerVisitCount
                );
            }
            reverseSuffix.Add(address);
            cursor = header.Parent;
        }

        throw new InvalidDataException("Context candidate rawStartExclusive is not an ancestor of completionBoundary.");
    }

    private static void ValidateAnchorSetupReferences(
        SessionJournalEventReader reader,
        SessionContextAnchorSetupReferences references,
        SessionGoverningSetup anchorSetup
    ) {
        ArgumentNullException.ThrowIfNull(references.RuntimeConfig);
        ArgumentNullException.ThrowIfNull(references.SystemPrompt);
        ValidateSetupReference(
            reader,
            references.RuntimeConfig,
            anchorSetup.RuntimeConfigSetupAddress,
            SessionEventKind.RuntimeConfigSetup
        );
        ValidateSetupReference(
            reader,
            references.SystemPrompt,
            anchorSetup.SystemPromptSetupAddress,
            SessionEventKind.SystemPromptSetup
        );
    }

    private static void ValidateSetupReference(
        SessionJournalEventReader reader,
        SessionContextSetupReference reference,
        EventAddress expectedAddress,
        SessionEventKind expectedKind
    ) {
        if (reference.Address != expectedAddress) {
            throw new InvalidDataException("Context candidate anchor setup address does not match the governing setup.");
        }
        using SessionJournalEventFrame frame = reader.ReadEvent(reference.Address).Unwrap();
        ValidateSessionHeader(reference.Address, frame.Header);
        if ((SessionEventKind)frame.Header.OpaqueEventKind != expectedKind) {
            throw new InvalidDataException("Context candidate anchor setup reference has the wrong event kind.");
        }
        _ = SessionEventCodec.Decode(expectedKind, frame.Payload, out int schemaVersion);
        if (reference.BodySchemaVersion != schemaVersion) {
            throw new InvalidDataException("Context candidate anchor setup schema version does not match raw payload.");
        }
        if (!string.Equals(
                reference.PayloadSha256,
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload),
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException("Context candidate anchor setup payload hash does not match raw payload.");
        }
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

    private static void ValidateSessionHeader(EventAddress address, EventFrameHeader header) {
        if (!Enum.IsDefined(typeof(SessionEventKind), header.OpaqueEventKind)
            || header.Hint != default(AddressHint)) {
            throw new InvalidDataException($"Invalid SessionJournal event header at {address}.");
        }
    }

    private sealed record ValidatedRawInterval(
        ImmutableArray<EventAddress> SuffixAddresses,
        int HeaderVisitCount
    );
}
