using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

/// <summary>
/// Reconstructs the exact completed-turn context for durable DerivedInfo work.
/// The SessionJournal remains authoritative; context is never copied into the
/// Character Memory store.
/// </summary>
internal static class CharacterNoteDerivedInfoContextMaterializer {
    internal static CharacterNoteDerivedInfoEnrichmentRequest Materialize(
        SessionJournalEngine engine,
        CharacterMemoryDerivedInfoWorkSnapshot work,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        EventAddress source = EventAddressTextCodec.Parse(
            work.SourceActionAddress
        );
        SessionCompletedTurnsReadResult read = engine
            .ReadRecentCompletedTurnsAt(
                source,
                maximumCount: 1,
                cancellationToken
            );
        if (read is not SessionCompletedTurnsReadResult.Snapshot available) {
            throw Mismatch(read switch {
                SessionCompletedTurnsReadResult.LimitExceeded limit =>
                    $"completed-turn read exceeded {limit.Limit}",
                SessionCompletedTurnsReadResult.UnsupportedSchema unsupported =>
                    $"completed-turn schema is unsupported: {unsupported.Detail}",
                SessionCompletedTurnsReadResult.Corruption corruption =>
                    $"completed-turn lineage is corrupt: {corruption.Detail}",
                _ => "completed-turn read returned an unknown outcome",
            });
        }

        SessionCompletedTurnsSnapshot snapshot = available.Value;
        if (snapshot.CapturedHead != source) {
            throw Mismatch(
                "completed-turn projection returned a different captured head"
            );
        }
        SessionCompletedTurnProjection? turn = snapshot.Turns
            .SingleOrDefault();
        if (turn is null || turn.TerminalAction.Address != source) {
            throw Mismatch(
                "completed-turn projection did not end at the source Action"
            );
        }

        string visibleActionText = GalateaVisibleActionTextRenderer.Render(
            turn.TerminalAction.Message
        );
        GalateaVisibleActionFingerprint fingerprint =
            GalateaVisibleActionFingerprint.Derive(visibleActionText);
        if (!string.Equals(
                fingerprint.Sha256,
                work.VisibleActionSha256,
                StringComparison.Ordinal
            )
            || fingerprint.Utf8Bytes != work.VisibleActionUtf8Bytes) {
            throw Mismatch(
                "completed-turn visible Action does not match durable provenance"
            );
        }

        CharacterNoteDerivedInfoTarget[] targets = work.Notes.Select(
            static note => new CharacterNoteDerivedInfoTarget(
                note.ArtifactOrdinal,
                note.ExactText
            )
        ).ToArray();
        return new CharacterNoteDerivedInfoEnrichmentRequest(
            turn.ObservationContent,
            visibleActionText,
            Array.AsReadOnly(targets)
        );
    }

    private static InvalidDataException Mismatch(string detail) => new(
        "Character Note DerivedInfo context mismatch: " + detail + "."
    );
}
