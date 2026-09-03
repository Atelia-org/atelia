using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Result of byte-exact historical Prepared v5 verification. It deliberately exposes neither a
/// current CompletionRequest nor the historical output ceiling.
/// </summary>
internal sealed record SessionPreparedRequestV5HistoricalVerification(
    EventAddress RawEndInclusive,
    EventAddress? SourcePreparedAddress
);

/// <summary>
/// Read-only verifier for historical Prepared v5 events. Verification preserves old append-only
/// commitments, but v5 is not an executable compatibility path and can never produce a provider
/// request.
/// </summary>
internal static class SessionPreparedRequestV5HistoricalVerifier {
    public static SessionPreparedRequestV5HistoricalVerification Verify(
        SessionJournalEventReader reader,
        EventAddress sourcePreparedAddress,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested();
        using SessionJournalEventFrame frame = reader.ReadEvent(
            sourcePreparedAddress
        ).Unwrap();
        ValidateSessionHeader(sourcePreparedAddress, frame.Header);
        var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (kind != SessionEventKind.CompletionRequestPrepared) {
            throw new InvalidDataException(
                $"Expected a completion-request-prepared event at {sourcePreparedAddress}, got '{kind}'."
            );
        }
        EventAddress rawEndInclusive = frame.Header.Parent
            ?? throw new InvalidDataException(
                $"CompletionRequestPrepared at {sourcePreparedAddress} must have a raw boundary parent."
            );
        object decoded = SessionEventCodec.Decode(
            kind,
            frame.Payload,
            out int bodySchemaVersion
        );
        var manifest = decoded as HistoricalCompletionRequestPreparedV5Body
            ?? throw new InvalidDataException(
                $"CompletionRequestPrepared at {sourcePreparedAddress} is body v{bodySchemaVersion}, not historical v5."
            );
        return Verify(
            reader,
            manifest,
            rawEndInclusive,
            cancellationToken
        ) with { SourcePreparedAddress = sourcePreparedAddress };
    }

    public static SessionPreparedRequestV5HistoricalVerification Verify(
        SessionJournalEventReader reader,
        HistoricalCompletionRequestPreparedV5Body manifest,
        EventAddress authoritativeRawEndInclusive,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        SessionRequestManifestCodec.ValidateHistoricalV5(manifest);
        SessionPreparedManifestView view = SessionPreparedManifestView.FromDecoded(
            SessionRequestManifestDefaults.HistoricalBodySchemaVersionV5,
            manifest
        );
        SessionPreparedRequestMaterialization materialization =
            SessionPreparedRequestReconstructor.Materialize(
                reader,
                view,
                authoritativeRawEndInclusive,
                cancellationToken
            );
        byte[] canonicalBytes = SessionRequestV5HistoricalCanonicalizer.Canonicalize(
            materialization.ModelId,
            materialization.PromptPrefix,
            materialization.TailMessages,
            manifest.Parameters.LegacyMaxTokens
        );
        SessionPreparedRequestReconstructor.ValidateCommitment(
            manifest.Commitment,
            canonicalBytes
        );
        return new SessionPreparedRequestV5HistoricalVerification(
            authoritativeRawEndInclusive,
            SourcePreparedAddress: null
        );
    }

    private static void ValidateSessionHeader(
        EventAddress address,
        EventFrameHeader header
    ) {
        if (!Enum.IsDefined(typeof(SessionEventKind), header.OpaqueEventKind)) {
            throw new InvalidDataException(
                $"Unknown SessionJournal event kind '{header.OpaqueEventKind}' at {address}."
            );
        }
        if (header.Hint != default(AddressHint)) {
            throw new InvalidDataException(
                $"SessionJournal trunk requires EventAddress hint 0, got '{header.Hint}' at {address}."
            );
        }
    }
}

/// <summary>
/// Routes read-only audit verification by Prepared body version. Only current v7 reconstruction
/// can return a dispatchable request; this facade intentionally discards that result.
/// </summary>
internal static class SessionPreparedRequestAuditVerifier {
    public static void Verify(
        SessionJournalEventReader reader,
        EventAddress sourcePreparedAddress,
        int bodySchemaVersion,
        CancellationToken cancellationToken = default
    ) {
        switch (bodySchemaVersion) {
            case SessionRequestManifestDefaults.CurrentBodySchemaVersion:
                _ = SessionPreparedRequestReconstructor.Reconstruct(
                    reader,
                    sourcePreparedAddress,
                    cancellationToken
                );
                return;
            case SessionRequestManifestDefaults.HistoricalBodySchemaVersionV5:
                _ = SessionPreparedRequestV5HistoricalVerifier.Verify(
                    reader,
                    sourcePreparedAddress,
                    cancellationToken
                );
                return;
            default:
                throw new NotSupportedException(
                    $"Prepared body v{bodySchemaVersion} is not readable for audit."
                );
        }
    }
}
