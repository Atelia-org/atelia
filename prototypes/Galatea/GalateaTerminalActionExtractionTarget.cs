using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal enum GalateaTerminalActionExtractionReadFailureKind {
    LimitExceeded,
    UnsupportedSchema,
    Corruption
}

internal abstract record GalateaTerminalActionExtractionReadResult {
    private GalateaTerminalActionExtractionReadResult() { }

    internal sealed record Available(
        GalateaTerminalActionExtractionTarget Target
    ) : GalateaTerminalActionExtractionReadResult;

    internal sealed record NoTerminalActionAtHead(
        EventAddress SelectedHead,
        EventAddress? LatestTerminalAction
    ) : GalateaTerminalActionExtractionReadResult;

    internal sealed record Failed(
        GalateaTerminalActionExtractionReadFailureKind Kind,
        EventAddress SelectedHead,
        string Detail
    ) : GalateaTerminalActionExtractionReadResult;
}

/// <summary>
/// Immutable identity and visible text for one terminal Action selected at an
/// exact SessionJournal head. The constructor derives the byte identity from
/// the supplied text so callers cannot provide mismatched hash metadata.
/// </summary>
internal sealed record GalateaTerminalActionExtractionTarget {
    internal GalateaTerminalActionExtractionTarget(
        EventAddress sourceAction,
        string visibleText
    ) {
        if (sourceAction == default) {
            throw new ArgumentException(
                "Terminal Action extraction source cannot be the default EventAddress.",
                nameof(sourceAction)
            );
        }
        ArgumentNullException.ThrowIfNull(visibleText);

        GalateaVisibleActionFingerprint fingerprint =
            GalateaVisibleActionFingerprint.Derive(visibleText);
        SourceAction = sourceAction;
        VisibleText = visibleText;
        VisibleTextSha256 = fingerprint.Sha256;
        VisibleTextUtf8Bytes = fingerprint.Utf8Bytes;
    }

    internal EventAddress SourceAction { get; }
    internal string VisibleText { get; }
    internal string VisibleTextSha256 { get; }
    internal int VisibleTextUtf8Bytes { get; }
}

/// <summary>
/// Reads one latest completed-turn projection at a caller-selected exact head.
/// It deliberately does not read or compare the current selected head.
/// </summary>
internal static class GalateaTerminalActionExtractionTargetReader {
    internal static GalateaTerminalActionExtractionReadResult ReadAt(
        SessionJournalEngine engine,
        EventAddress selectedHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        if (selectedHead == default) {
            throw new ArgumentException(
                "Terminal Action extraction head cannot be the default EventAddress.",
                nameof(selectedHead)
            );
        }
        cancellationToken.ThrowIfCancellationRequested();

        SessionCompletedTurnsReadResult read = engine
            .ReadRecentCompletedTurnsAt(
                selectedHead,
                maximumCount: 1,
                cancellationToken
            );
        if (read is not SessionCompletedTurnsReadResult.Snapshot available) {
            return MapFailure(read, selectedHead);
        }

        SessionCompletedTurnsSnapshot completed = available.Value;
        if (completed.CapturedHead != selectedHead) {
            throw new InvalidDataException(
                "Latest-turn projection returned a different captured head."
            );
        }
        SessionCompletedTurnProjection? latest = completed.Turns
            .SingleOrDefault();
        if (latest is null
            || latest.TerminalAction.Address != selectedHead) {
            return new GalateaTerminalActionExtractionReadResult
                .NoTerminalActionAtHead(
                    selectedHead,
                    latest?.TerminalAction.Address
                );
        }

        string visibleText = GalateaVisibleActionTextRenderer.Render(
            latest.TerminalAction.Message
        );
        return new GalateaTerminalActionExtractionReadResult.Available(
            new GalateaTerminalActionExtractionTarget(
                selectedHead,
                visibleText
            )
        );
    }

    private static GalateaTerminalActionExtractionReadResult MapFailure(
        SessionCompletedTurnsReadResult result,
        EventAddress selectedHead
    ) => result switch {
        SessionCompletedTurnsReadResult.LimitExceeded limit =>
            new GalateaTerminalActionExtractionReadResult.Failed(
                GalateaTerminalActionExtractionReadFailureKind.LimitExceeded,
                selectedHead,
                limit.Limit.ToString()
            ),
        SessionCompletedTurnsReadResult.UnsupportedSchema unsupported =>
            new GalateaTerminalActionExtractionReadResult.Failed(
                GalateaTerminalActionExtractionReadFailureKind
                    .UnsupportedSchema,
                selectedHead,
                unsupported.Detail
            ),
        SessionCompletedTurnsReadResult.Corruption corruption =>
            new GalateaTerminalActionExtractionReadResult.Failed(
                GalateaTerminalActionExtractionReadFailureKind.Corruption,
                selectedHead,
                corruption.Detail
            ),
        _ => throw new InvalidDataException(
            "Unknown latest completed-turn read result."
        )
    };
}
