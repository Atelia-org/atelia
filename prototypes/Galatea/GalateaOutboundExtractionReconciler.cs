using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal enum GalateaOutboundExtractionReadFailureKind {
    LimitExceeded,
    UnsupportedSchema,
    Corruption
}

internal sealed class GalateaOutboundExtractionReadException
    : InvalidOperationException {
    internal GalateaOutboundExtractionReadException(
        GalateaOutboundExtractionReadFailureKind kind,
        EventAddress selectedHead,
        string detail
    ) : base(
        $"Outbound extraction could not read the latest completed turn at "
            + $"'{EventAddressTextCodec.Format(selectedHead)}': {detail}"
    ) {
        Kind = kind;
        SelectedHead = selectedHead;
        Detail = detail;
    }

    internal GalateaOutboundExtractionReadFailureKind Kind { get; }
    internal EventAddress SelectedHead { get; }
    internal string Detail { get; }
}

internal sealed class GalateaOutboundExtractionCaptureMismatchException
    : InvalidOperationException {
    internal GalateaOutboundExtractionCaptureMismatchException(
        EventAddress sourceAction
    ) : base(
        "The durable outbound extraction capture does not match the exact "
            + $"Action identity '{EventAddressTextCodec.Format(sourceAction)}'."
    ) {
        SourceAction = sourceAction;
    }

    internal EventAddress SourceAction { get; }
}

internal abstract record GalateaOutboundExtractionReconcileResult {
    private GalateaOutboundExtractionReconcileResult() { }

    internal sealed record NoSelectedHead
        : GalateaOutboundExtractionReconcileResult;

    internal sealed record BaselineCovered(EventAddress SelectedHead)
        : GalateaOutboundExtractionReconcileResult;

    internal sealed record NoTerminalActionAtHead(
        EventAddress SelectedHead,
        EventAddress? LatestTerminalAction
    ) : GalateaOutboundExtractionReconcileResult;

    internal sealed record AlreadyCaptured(
        EventAddress SourceAction,
        int ArtifactCount,
        long StoreRevision
    ) : GalateaOutboundExtractionReconcileResult;

    internal sealed record Captured(
        EventAddress SourceAction,
        int ArtifactCount,
        long StoreRevision,
        IReadOnlyList<string> DispatchIds
    ) : GalateaOutboundExtractionReconcileResult;

    internal sealed record SelectedHeadChanged(
        EventAddress ExpectedHead,
        EventAddress? ObservedHead
    ) : GalateaOutboundExtractionReconcileResult;
}

/// <summary>
/// Single-gap reconciler for one durable outbound extraction store. The caller
/// must hold the corresponding per-session TurnLock for the whole call. This
/// type deliberately reads only the latest completed turn at one exact
/// selected head and never scans complete history.
/// </summary>
internal sealed class GalateaOutboundExtractionReconciler {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly GalateaDelegationSqliteStore _store;
    private readonly IOutboundMailExtractor _extractor;

    internal GalateaOutboundExtractionReconciler(
        GalateaDelegationSqliteStore store,
        IOutboundMailExtractor extractor
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _extractor = extractor
            ?? throw new ArgumentNullException(nameof(extractor));
    }

    internal async ValueTask<GalateaOutboundExtractionReconcileResult>
        ReconcileAsync(
        SessionJournalEngine engine,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        if (engine.IsReadOnly) {
            throw new ArgumentException(
                "Outbound extraction reconciliation requires a writable SessionJournalEngine.",
                nameof(engine)
            );
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (engine.ReadCurrentHead() is not { } selectedHead) {
            return new GalateaOutboundExtractionReconcileResult
                .NoSelectedHead();
        }
        if (_store.Baseline.CaptureFromPhysicalFrontier.Contains(
                selectedHead)) {
            return new GalateaOutboundExtractionReconcileResult
                .BaselineCovered(selectedHead);
        }

        SessionCompletedTurnsSnapshot completed = RequireSnapshot(
            engine.ReadRecentCompletedTurnsAt(
                selectedHead,
                maximumCount: 1,
                cancellationToken
            ),
            selectedHead
        );
        if (completed.CapturedHead != selectedHead) {
            throw new InvalidDataException(
                "Latest-turn projection returned a different captured head."
            );
        }
        SessionCompletedTurnProjection? latest = completed.Turns
            .SingleOrDefault();
        if (latest is null
            || latest.TerminalAction.Address != selectedHead) {
            return new GalateaOutboundExtractionReconcileResult
                .NoTerminalActionAtHead(
                    selectedHead,
                    latest?.TerminalAction.Address
                );
        }

        string target = GalateaVisibleActionTextRenderer.Render(
            latest.TerminalAction.Message
        );
        VisibleActionIdentity identity = CreateIdentity(target);
        string sourceAction = EventAddressTextCodec.Format(selectedHead);
        GalateaDelegationStateSnapshot before = _store.ReadSnapshot();
        GalateaActionCaptureSnapshot? existing = before.Captures
            .SingleOrDefault(value => string.Equals(
                value.SourceActionAddress,
                sourceAction,
                StringComparison.Ordinal
            ));
        if (existing is not null) {
            ValidateExistingCapture(existing, identity, selectedHead);
            return new GalateaOutboundExtractionReconcileResult
                .AlreadyCaptured(
                    selectedHead,
                    existing.ArtifactCount,
                    before.StoreRevision
                );
        }

        IReadOnlyList<SendMailIntent> intents;
        if (string.IsNullOrWhiteSpace(target)) {
            intents = Array.Empty<SendMailIntent>();
        }
        else {
            intents = await _extractor.ExtractAsync(
                    target,
                    cancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "Outbound mail extractor returned a null intent batch."
                );
        }
        cancellationToken.ThrowIfCancellationRequested();
        EventAddress? observedHead = engine.ReadCurrentHead();
        if (observedHead != selectedHead) {
            return new GalateaOutboundExtractionReconcileResult
                .SelectedHeadChanged(selectedHead, observedHead);
        }

        var request = new GalateaDelegationCaptureRequest(
            sourceAction,
            identity.Sha256,
            identity.Utf8Bytes,
            _extractor.ContractId,
            intents
        );
        GalateaDelegationCaptureResult capture =
            _store.CaptureActionBatch(request);
        if (capture.Disposition
                == GalateaDelegationCaptureDisposition.Captured) {
            return new GalateaOutboundExtractionReconcileResult.Captured(
                selectedHead,
                intents.Count,
                capture.StoreRevision,
                capture.DispatchIds
            );
        }

        GalateaActionCaptureSnapshot settled = RequireExistingCapture(
            sourceAction,
            selectedHead,
            identity
        );
        return new GalateaOutboundExtractionReconcileResult.AlreadyCaptured(
            selectedHead,
            settled.ArtifactCount,
            capture.StoreRevision
        );
    }

    private GalateaActionCaptureSnapshot RequireExistingCapture(
        string sourceAction,
        EventAddress sourceAddress,
        VisibleActionIdentity identity
    ) {
        GalateaActionCaptureSnapshot existing = _store.ReadSnapshot()
            .Captures
            .SingleOrDefault(value => string.Equals(
                value.SourceActionAddress,
                sourceAction,
                StringComparison.Ordinal
            ))
            ?? throw new InvalidDataException(
                "An AlreadyCaptured result has no durable Action capture."
            );
        ValidateExistingCapture(existing, identity, sourceAddress);
        return existing;
    }

    private static void ValidateExistingCapture(
        GalateaActionCaptureSnapshot existing,
        VisibleActionIdentity identity,
        EventAddress sourceAction
    ) {
        if (!string.Equals(
                existing.VisibleActionSha256,
                identity.Sha256,
                StringComparison.Ordinal)
            || existing.VisibleActionUtf8Bytes != identity.Utf8Bytes) {
            throw new GalateaOutboundExtractionCaptureMismatchException(
                sourceAction
            );
        }
    }

    private static VisibleActionIdentity CreateIdentity(string target) {
        byte[] utf8 = StrictUtf8.GetBytes(target);
        return new VisibleActionIdentity(
            Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant(),
            utf8.Length
        );
    }

    private static SessionCompletedTurnsSnapshot RequireSnapshot(
        SessionCompletedTurnsReadResult result,
        EventAddress selectedHead
    ) => result switch {
        SessionCompletedTurnsReadResult.Snapshot snapshot => snapshot.Value,
        SessionCompletedTurnsReadResult.LimitExceeded limit =>
            throw new GalateaOutboundExtractionReadException(
                GalateaOutboundExtractionReadFailureKind.LimitExceeded,
                selectedHead,
                limit.Limit.ToString()
            ),
        SessionCompletedTurnsReadResult.UnsupportedSchema unsupported =>
            throw new GalateaOutboundExtractionReadException(
                GalateaOutboundExtractionReadFailureKind.UnsupportedSchema,
                selectedHead,
                unsupported.Detail
            ),
        SessionCompletedTurnsReadResult.Corruption corruption =>
            throw new GalateaOutboundExtractionReadException(
                GalateaOutboundExtractionReadFailureKind.Corruption,
                selectedHead,
                corruption.Detail
            ),
        _ => throw new InvalidDataException(
            "Unknown latest completed-turn read result."
        )
    };

    private sealed record VisibleActionIdentity(
        string Sha256,
        int Utf8Bytes
    );
}
