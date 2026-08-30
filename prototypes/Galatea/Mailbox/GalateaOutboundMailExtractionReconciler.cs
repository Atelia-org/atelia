using Atelia.EventJournal;
using Atelia.Galatea.Server;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.Mailbox;

internal enum GalateaOutboundMailExtractionReadFailureKind {
    LimitExceeded,
    UnsupportedSchema,
    Corruption
}

internal sealed class GalateaOutboundMailExtractionReadException
    : InvalidOperationException {
    internal GalateaOutboundMailExtractionReadException(
        GalateaOutboundMailExtractionReadFailureKind kind,
        EventAddress selectedHead,
        string detail
    ) : base(
        $"Outbound mail extraction could not read the latest completed turn at "
            + $"'{EventAddressTextCodec.Format(selectedHead)}': {detail}"
    ) {
        Kind = kind;
        SelectedHead = selectedHead;
        Detail = detail;
    }

    internal GalateaOutboundMailExtractionReadFailureKind Kind { get; }
    internal EventAddress SelectedHead { get; }
    internal string Detail { get; }
}

internal sealed class GalateaOutboundMailExtractionCaptureMismatchException
    : InvalidOperationException {
    internal GalateaOutboundMailExtractionCaptureMismatchException(
        EventAddress sourceAction
    ) : base(
        "The durable outbound mail extraction capture does not match the exact "
            + $"Action identity '{EventAddressTextCodec.Format(sourceAction)}'."
    ) {
        SourceAction = sourceAction;
    }

    internal EventAddress SourceAction { get; }
}

internal abstract record GalateaOutboundMailExtractionReconcileResult {
    private GalateaOutboundMailExtractionReconcileResult() { }

    internal sealed record NoSelectedHead
        : GalateaOutboundMailExtractionReconcileResult;

    internal sealed record BaselineCovered(EventAddress SelectedHead)
        : GalateaOutboundMailExtractionReconcileResult;

    internal sealed record NoTerminalActionAtHead(
        EventAddress SelectedHead,
        EventAddress? LatestTerminalAction
    ) : GalateaOutboundMailExtractionReconcileResult;

    internal sealed record AlreadyCaptured(
        EventAddress SourceAction,
        int ArtifactCount,
        long StoreRevision
    ) : GalateaOutboundMailExtractionReconcileResult;

    internal sealed record Captured(
        EventAddress SourceAction,
        int ArtifactCount,
        long StoreRevision,
        IReadOnlyList<string> DispatchIds
    ) : GalateaOutboundMailExtractionReconcileResult;

    internal sealed record SelectedHeadChanged(
        EventAddress ExpectedHead,
        EventAddress? ObservedHead
    ) : GalateaOutboundMailExtractionReconcileResult;
}

/// <summary>
/// Single-gap reconciler for one durable outbound mail extraction store. The caller
/// must hold the corresponding per-session TurnLock for the whole call. This
/// type deliberately reads only the latest completed turn at one exact
/// selected head and never scans complete history.
/// </summary>
internal sealed class GalateaOutboundMailExtractionReconciler {
    private readonly GalateaDelegationSqliteStore _store;
    private readonly IOutboundMailExtractor _extractor;

    internal GalateaOutboundMailExtractionReconciler(
        GalateaDelegationSqliteStore store,
        IOutboundMailExtractor extractor
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _extractor = extractor
            ?? throw new ArgumentNullException(nameof(extractor));
    }

    internal async ValueTask<GalateaOutboundMailExtractionReconcileResult>
        ReconcileAsync(
        SessionJournalEngine engine,
        CancellationToken cancellationToken = default
    ) {
        RequireWritableEngine(engine);
        cancellationToken.ThrowIfCancellationRequested();

        if (engine.ReadCurrentHead() is not { } selectedHead) {
            return new GalateaOutboundMailExtractionReconcileResult
                .NoSelectedHead();
        }
        if (_store.Baseline.CaptureFromPhysicalFrontier.Contains(
                selectedHead)) {
            return new GalateaOutboundMailExtractionReconcileResult
                .BaselineCovered(selectedHead);
        }

        GalateaTerminalActionExtractionReadResult read =
            GalateaTerminalActionExtractionTargetReader.ReadAt(
                engine,
                selectedHead,
                cancellationToken
            );
        switch (read) {
            case GalateaTerminalActionExtractionReadResult.Available available:
                return await ReconcileTargetAsync(
                        engine,
                        available.Target,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            case GalateaTerminalActionExtractionReadResult
                    .NoTerminalActionAtHead none:
                return new GalateaOutboundMailExtractionReconcileResult
                    .NoTerminalActionAtHead(
                        none.SelectedHead,
                        none.LatestTerminalAction
                    );
            case GalateaTerminalActionExtractionReadResult.Failed failure:
                throw CreateReadException(failure);
            default:
                throw new InvalidDataException(
                    "Unknown terminal Action extraction read result."
                );
        }
    }

    /// <summary>
    /// Reconciles outbound mail for one already projected exact terminal Action.
    /// The caller must hold the per-session TurnLock and must have obtained the
    /// target from the selected head. This overload does not project history.
    /// </summary>
    internal async ValueTask<GalateaOutboundMailExtractionReconcileResult>
        ReconcileTargetAsync(
        SessionJournalEngine engine,
        GalateaTerminalActionExtractionTarget target,
        CancellationToken cancellationToken = default
    ) {
        RequireWritableEngine(engine);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        EventAddress selectedHead = target.SourceAction;
        if (_store.Baseline.CaptureFromPhysicalFrontier.Contains(
                selectedHead)) {
            return new GalateaOutboundMailExtractionReconcileResult
                .BaselineCovered(selectedHead);
        }

        string sourceAction = EventAddressTextCodec.Format(selectedHead);
        GalateaDelegationStateSnapshot before = _store.ReadSnapshot();
        GalateaActionCaptureSnapshot? existing = before.Captures
            .SingleOrDefault(value => string.Equals(
                value.SourceActionAddress,
                sourceAction,
                StringComparison.Ordinal
            ));
        if (existing is not null) {
            ValidateExistingCapture(existing, target);
            return new GalateaOutboundMailExtractionReconcileResult
                .AlreadyCaptured(
                    selectedHead,
                    existing.ArtifactCount,
                    before.StoreRevision
                );
        }

        IReadOnlyList<SendMailIntent> intents;
        if (string.IsNullOrWhiteSpace(target.VisibleText)) {
            intents = Array.Empty<SendMailIntent>();
        }
        else {
            intents = await _extractor.ExtractAsync(
                    target.VisibleText,
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
            return new GalateaOutboundMailExtractionReconcileResult
                .SelectedHeadChanged(selectedHead, observedHead);
        }

        var request = new GalateaDelegationCaptureRequest(
            sourceAction,
            target.VisibleTextSha256,
            target.VisibleTextUtf8Bytes,
            _extractor.ContractId,
            intents
        );
        GalateaDelegationCaptureResult capture =
            _store.CaptureActionBatch(request);
        if (capture.Disposition
                == GalateaDelegationCaptureDisposition.Captured) {
            return new GalateaOutboundMailExtractionReconcileResult.Captured(
                selectedHead,
                intents.Count,
                capture.StoreRevision,
                capture.DispatchIds
            );
        }

        GalateaActionCaptureSnapshot settled = RequireExistingCapture(
            sourceAction,
            target
        );
        return new GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured(
            selectedHead,
            settled.ArtifactCount,
            capture.StoreRevision
        );
    }

    private GalateaActionCaptureSnapshot RequireExistingCapture(
        string sourceAction,
        GalateaTerminalActionExtractionTarget target
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
        ValidateExistingCapture(existing, target);
        return existing;
    }

    private static void ValidateExistingCapture(
        GalateaActionCaptureSnapshot existing,
        GalateaTerminalActionExtractionTarget target
    ) {
        if (!string.Equals(
                existing.VisibleActionSha256,
                target.VisibleTextSha256,
                StringComparison.Ordinal)
            || existing.VisibleActionUtf8Bytes
                != target.VisibleTextUtf8Bytes) {
            throw new GalateaOutboundMailExtractionCaptureMismatchException(
                target.SourceAction
            );
        }
    }

    private static void RequireWritableEngine(SessionJournalEngine engine) {
        ArgumentNullException.ThrowIfNull(engine);
        if (engine.IsReadOnly) {
            throw new ArgumentException(
                "Outbound mail extraction reconciliation requires a writable SessionJournalEngine.",
                nameof(engine)
            );
        }
    }

    private static GalateaOutboundMailExtractionReadException
        CreateReadException(
        GalateaTerminalActionExtractionReadResult.Failed failure
    ) => new(
        failure.Kind switch {
            GalateaTerminalActionExtractionReadFailureKind.LimitExceeded =>
                GalateaOutboundMailExtractionReadFailureKind.LimitExceeded,
            GalateaTerminalActionExtractionReadFailureKind
                    .UnsupportedSchema =>
                GalateaOutboundMailExtractionReadFailureKind
                    .UnsupportedSchema,
            GalateaTerminalActionExtractionReadFailureKind.Corruption =>
                GalateaOutboundMailExtractionReadFailureKind.Corruption,
            _ => throw new InvalidDataException(
                "Unknown terminal Action extraction read failure kind."
            )
        },
        failure.SelectedHead,
        failure.Detail
    );
}
