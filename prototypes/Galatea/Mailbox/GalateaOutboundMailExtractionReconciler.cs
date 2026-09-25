using System.Diagnostics;
using System.Text.Json;
using Atelia.Diagnostics;
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
#if DEBUG
    internal static bool CompilesCapturedMailDiagnostics => true;
#else
    internal static bool CompilesCapturedMailDiagnostics => false;
#endif

    private readonly GalateaDelegationSqliteStore _store;
    private readonly IOutboundMailExtractor _extractor;
    private readonly GalateaSenderSnapshot _sender;
    private readonly Func<SendMailIntent, GalateaInternalMailTarget?>?
        _resolveInternalTarget;

    internal Action<string>? CapturedMailDiagnosticSinkForTest { get; set; }
    internal Action<string>? ExtractionDiagnosticSinkForTest { get; set; }

    internal GalateaOutboundMailExtractionReconciler(
        GalateaDelegationSqliteStore store,
        IOutboundMailExtractor extractor,
        GalateaSenderSnapshot sender,
        Func<SendMailIntent, GalateaInternalMailTarget?>?
            resolveInternalTarget = null
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _extractor = extractor
            ?? throw new ArgumentNullException(nameof(extractor));
        _resolveInternalTarget = resolveInternalTarget;
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
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

        var extractionSource = TextExtractionSource.Create(
            _sender.Id, sourceAction
        );
        var captureTrace = TextExtractionTrace.Create(
            "outbound-mail", _extractor.ContractId, _sender.Name,
            extractionSource, target.VisibleText,
            ExtractionDiagnosticSinkForTest
        );
        IReadOnlyList<SendMailIntent> intents;
        if (string.IsNullOrWhiteSpace(target.VisibleText)) {
            intents = Array.Empty<SendMailIntent>();
            captureTrace.Emit("text-extraction-skipped", new {
                reasonCode = "empty-visible-action",
                artifactCount = 0,
            });
        }
        else {
            intents = await _extractor.ExtractAsync(
                    target.VisibleText,
                    cancellationToken,
                    extractionSource
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "Outbound mail extractor returned a null intent batch."
                );
        }
        cancellationToken.ThrowIfCancellationRequested();
        EventAddress? observedHead = engine.ReadCurrentHead();
        if (observedHead != selectedHead) {
            captureTrace.Emit("text-extraction-capture", new {
                outcome = "skipped",
                reasonCode = "selected-head-changed",
                extractedCount = intents.Count,
                capturedCount = (int?)null,
            });
            return new GalateaOutboundMailExtractionReconcileResult
                .SelectedHeadChanged(selectedHead, observedHead);
        }

        var request = new GalateaDelegationCaptureRequest(
            sourceAction,
            target.VisibleTextSha256,
            target.VisibleTextUtf8Bytes,
            _extractor.ContractId,
            intents,
            _sender,
            _resolveInternalTarget is null ? null :
                GalateaDelegationStateSnapshot.Freeze(
                    intents.Select(intent => _resolveInternalTarget(intent))
                )
        );
        GalateaDelegationCaptureResult capture;
        try {
            capture = _store.CaptureActionBatch(request);
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            captureTrace.Emit("text-extraction-capture", new {
                outcome = "failed",
                reasonCode = "capture-exception",
                exceptionType = exception.GetType().FullName,
                extractedCount = intents.Count,
                capturedCount = (int?)null,
            });
            throw;
        }
        if (capture.Disposition
                == GalateaDelegationCaptureDisposition.Captured) {
            captureTrace.Emit("text-extraction-capture", new {
                outcome = "captured",
                reasonCode = (string?)null,
                extractedCount = intents.Count,
                capturedCount = intents.Count,
                captureSequence = capture.StoreRevision,
                dispatchIds = capture.DispatchIds,
            });
            for (int ordinal = 0; ordinal < capture.DispatchIds.Count;
                 ordinal++) {
                LogCapturedMail(request, capture, ordinal);
            }
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
        captureTrace.Emit("text-extraction-capture", new {
            outcome = "already-captured",
            reasonCode = (string?)null,
            extractedCount = intents.Count,
            capturedCount = settled.ArtifactCount,
            captureSequence = capture.StoreRevision,
        });
        return new GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured(
            selectedHead,
            settled.ArtifactCount,
            capture.StoreRevision
        );
    }

    /// <summary>
    /// A best-effort, content-free diagnostic emitted only after the complete
    /// capture transaction has committed. The dispatch ID is the durable
    /// outbound_mail key and also identifies an internal outbox when present.
    /// resolvedRecipientKind is a diagnostic target category, not the
    /// outbound_mail route_class stored for the mail.
    /// </summary>
    [Conditional("DEBUG")]
    private void LogCapturedMail(
        GalateaDelegationCaptureRequest request,
        GalateaDelegationCaptureResult capture,
        int ordinal
    ) {
        try {
            GalateaInternalMailTarget? internalTarget =
                request.InternalTargets?[ordinal];
            bool isCodex = string.Equals(
                request.Intents[ordinal].Recipient,
                GalateaDelegateConfigReader.CanonicalRecipient,
                StringComparison.Ordinal
            );
            string resolvedRecipientKind = isCodex ? "Codex" :
                internalTarget is not null ? "Character" : "Unrouted";
            string? recipientId = isCodex
                ? GalateaDelegateConfigReader.CanonicalRecipient
                : internalTarget?.TargetCharacterId;
            string diagnostic = JsonSerializer.Serialize(new {
                @event = "outbound-mail-captured",
                characterId = _sender.Id,
                sourceAction = request.SourceActionAddress,
                artifactOrdinal = ordinal,
                resolvedRecipientKind,
                recipientId,
                dispatchId = capture.DispatchIds[ordinal],
                captureSequence = capture.StoreRevision,
            });
            CapturedMailDiagnosticSinkForTest?.Invoke(diagnostic);
            DebugUtil.Debug("Galatea.Delegation", diagnostic);
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            // A diagnostic failure must not turn a committed capture into a
            // retryable business failure or prevent its dispatch signal.
        }
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
