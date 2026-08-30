using Atelia.EventJournal;
using Atelia.MemoPod;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

/// <summary>
/// Owns one per-user Character Memory store and reconciles its single V1
/// Default MemoPod. Callers still serialize SessionJournal work with TurnLock.
/// </summary>
internal sealed class CharacterNoteDefaultPodReconciler : IDisposable {
    private readonly CharacterMemorySqliteStore _store;
    private readonly ICharacterNoteExtractor _extractor;
    private readonly ICharacterNoteDefaultPodAccess _pods;
    private bool _disposed;

    private CharacterNoteDefaultPodReconciler(
        CharacterMemorySqliteStore store,
        ICharacterNoteExtractor extractor,
        ICharacterNoteDefaultPodAccess pods
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _extractor = extractor
            ?? throw new ArgumentNullException(nameof(extractor));
        _pods = pods ?? throw new ArgumentNullException(nameof(pods));
    }

    internal static ValueTask<CharacterNoteDefaultPodReconciler>
        CreateNewAsync(
        string storeDirectory,
        CharacterMemoryStoreOwner owner,
        CharacterMemoryStoreBaseline baseline,
        ICharacterNoteExtractor extractor
    ) {
        CharacterMemorySqliteStore store =
            CharacterMemorySqliteStore.CreateNew(
                storeDirectory,
                owner,
                baseline,
                CharacterNoteDefaultPodV1.EmptyStateIdentity
            );
        return AttachAsync(
            store,
            extractor,
            CharacterNoteMemoPodAccess.Instance
        );
    }

    internal static ValueTask<CharacterNoteDefaultPodReconciler>
        OpenExistingAsync(
        string storeDirectory,
        CharacterMemoryStoreOwner owner,
        ICharacterNoteExtractor extractor
    ) => AttachAsync(
        CharacterMemorySqliteStore.OpenExisting(storeDirectory, owner),
        extractor,
        CharacterNoteMemoPodAccess.Instance
    );

    internal static async ValueTask<CharacterNoteDefaultPodReconciler>
        AttachAsync(
        CharacterMemorySqliteStore store,
        ICharacterNoteExtractor extractor,
        ICharacterNoteDefaultPodAccess? pods = null
    ) {
        var reconciler = new CharacterNoteDefaultPodReconciler(
            store,
            extractor,
            pods ?? CharacterNoteMemoPodAccess.Instance
        );
        try {
            await reconciler.EnsureProvisionedAsync().ConfigureAwait(false);
            return reconciler;
        }
        catch {
            reconciler.Dispose();
            throw;
        }
    }

    internal CharacterMemoryStatusSnapshot ReadStatusSnapshot() {
        ThrowIfDisposed();
        return _store.ReadStatusSnapshot();
    }

    internal async ValueTask<CharacterNotePendingReconcileResult>
        ReconcilePendingAsync() {
        ThrowIfDisposed();
        CharacterMemoryStatusSnapshot status = _store.ReadStatusSnapshot();
        if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
            return new CharacterNotePendingReconcileResult.Reconciled(
                new CharacterNoteDefaultPodReconcileResult.Quarantined(
                    status.QuarantineCode!
                )
            );
        }
        if (status.StoreState is not CharacterMemoryStoreState.Ready) {
            throw new InvalidDataException(
                "An attached Character Memory store must be Ready or Quarantined."
            );
        }
        if (status.ActiveCapture is null) {
            return new CharacterNotePendingReconcileResult.NoPending();
        }
        return new CharacterNotePendingReconcileResult.Reconciled(
            await ReconcileCapturedBatchAsync(
                    status,
                    status.ActiveCapture
                )
                .ConfigureAwait(false)
        );
    }

    internal async ValueTask<CharacterNoteDefaultPodReconcileResult>
        ReconcileTargetAsync(
        SessionJournalEngine engine,
        GalateaTerminalActionExtractionTarget target,
        CancellationToken preCaptureCancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(target);

        CharacterNotePendingReconcileResult pending =
            await ReconcilePendingAsync().ConfigureAwait(false);
        if (pending is CharacterNotePendingReconcileResult.Reconciled done) {
            return done.Result;
        }

        string sourceAction = EventAddressTextCodec.Format(
            target.SourceAction
        );
        CharacterMemoryStatusSnapshot status = _store.ReadStatusSnapshot();
        CharacterMemoryCaptureSnapshot? existing =
            _store.ReadCaptureExact(sourceAction);
        if (existing is not null) {
            CharacterNoteDefaultPodReconcileResult? health =
                CheckCurrentTip(
                    status,
                    target.SourceAction,
                    captureExists: true
                );
            return health ?? ResultForTerminalCapture(existing);
        }

        CharacterNoteDefaultPodReconcileResult? current =
            CheckCurrentTip(
                status,
                target.SourceAction,
                captureExists: false
            );
        if (current is not null) { return current; }
        if (_store.Baseline.CaptureFromPhysicalFrontier.Contains(
                target.SourceAction)) {
            return new CharacterNoteDefaultPodReconcileResult
                .BaselineCovered(target.SourceAction);
        }

        preCaptureCancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CharacterNoteIntent> intents =
            string.IsNullOrWhiteSpace(target.VisibleText)
                ? Array.Empty<CharacterNoteIntent>()
                : await _extractor.ExtractAsync(
                        target.VisibleText,
                        preCaptureCancellationToken
                    )
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        "Character Note extractor returned a null batch."
                    );
        preCaptureCancellationToken.ThrowIfCancellationRequested();
        EventAddress? observedHead = engine.ReadCurrentHead();
        if (observedHead != target.SourceAction) {
            return new CharacterNoteDefaultPodReconcileResult
                .SelectedHeadChanged(target.SourceAction, observedHead);
        }

        CharacterMemoryCaptureResult captured = _store.CaptureNew(new(
            sourceAction,
            target.VisibleTextSha256,
            target.VisibleTextUtf8Bytes,
            _extractor.ContractId,
            intents.Select(static intent => intent.ExactText).ToArray()
        ));
        return captured.Disposition switch {
            CharacterMemoryCaptureDisposition.BaselineCovered =>
                new CharacterNoteDefaultPodReconcileResult.BaselineCovered(
                    target.SourceAction
                ),
            CharacterMemoryCaptureDisposition.ZeroCaptured =>
                new CharacterNoteDefaultPodReconcileResult.ZeroCaptured(
                    target.SourceAction
                ),
            CharacterMemoryCaptureDisposition.Captured
                or CharacterMemoryCaptureDisposition.AlreadyCaptured =>
                await ReconcileCapturedBatchAsync(
                        _store.ReadStatusSnapshot(),
                        captured.Capture ?? throw new InvalidDataException(
                            "A non-zero capture result has no capture snapshot."
                        )
                    )
                    .ConfigureAwait(false),
            _ => throw new InvalidDataException(
                "Unknown Character Memory capture disposition."
            )
        };
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _store.Dispose();
    }

    private async ValueTask EnsureProvisionedAsync() {
        CharacterMemoryStatusSnapshot status = _store.ReadStatusSnapshot();
        if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
            return;
        }
        if (!string.Equals(
                status.ProvisionTargetPodStateIdentity,
                CharacterNoteDefaultPodV1.EmptyStateIdentity,
                StringComparison.Ordinal)) {
            _ = Quarantine(
                status,
                CharacterNoteDefaultPodOutcomeCodes
                    .ProvisionTargetUnsupported,
                observedIdentity: null
            );
            return;
        }

        PodOpenResult observed = OpenDefaultPod();
        if (status.StoreState is CharacterMemoryStoreState.Ready) {
            if (observed is PodOpenResult.Available ready) {
                bool matchesSettled = string.Equals(
                    ready.Identity,
                    status.SettledDefaultPodStateIdentity,
                    StringComparison.Ordinal
                );
                bool matchesPlannedTarget = status.ActiveCapture is {
                        State: CharacterMemoryCaptureState.Planned,
                        TargetPodStateIdentity: { } plannedTarget
                    }
                    && string.Equals(
                        ready.Identity,
                        plannedTarget,
                        StringComparison.Ordinal
                    );
                if (matchesSettled || matchesPlannedTarget) { return; }
            }
            if (observed is PodOpenResult.Unavailable unavailable) {
                throw unavailable.Exception;
            }
            _ = Quarantine(
                status,
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                ObservedIdentity(observed)
            );
            return;
        }
        if (status.StoreState is not CharacterMemoryStoreState.Provisioning) {
            throw new InvalidDataException(
                "Unknown Character Memory provisioning state."
            );
        }

        if (observed is PodOpenResult.Available installed) {
            await CompleteProvisionFromObservedAsync(status, installed)
                .ConfigureAwait(false);
            return;
        }
        if (observed is PodOpenResult.Unavailable transient) {
            throw transient.Exception;
        }
        if (observed is PodOpenResult.Invalid) {
            _ = Quarantine(
                status,
                CharacterNoteDefaultPodOutcomeCodes.ProvisionStateMismatch,
                observedIdentity: null
            );
            return;
        }

        ICharacterNoteDefaultPodHandle candidate;
        try {
            candidate = _pods.Create(
                _store.StoreDirectory,
                CharacterNoteDefaultPodV1.PodId,
                CharacterNoteDefaultPodV1.Topic
            );
        }
        catch (CharacterNoteDefaultPodAccessException exception) {
            if (exception.Kind is CharacterNoteDefaultPodFailureKind.NotFound
                or CharacterNoteDefaultPodFailureKind.IoFailure) {
                throw;
            }
            _ = Quarantine(
                status,
                CharacterNoteDefaultPodOutcomeCodes
                    .ProvisionStateMismatch,
                observedIdentity: null
            );
            return;
        }
        string candidateIdentity = candidate.ComputeStateIdentity();
        if (!string.Equals(
                candidateIdentity,
                status.ProvisionTargetPodStateIdentity,
                StringComparison.Ordinal)) {
            _ = Quarantine(
                status,
                CharacterNoteDefaultPodOutcomeCodes
                    .ProvisionStateMismatch,
                candidateIdentity
            );
            return;
        }

        try {
            await candidate.FreezeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            _ = _store.RecordInitialDefaultPod(candidateIdentity);
        }
        catch (IOException exception) {
            await RecoverProvisionPublishFailureAsync(status, exception)
                .ConfigureAwait(false);
        }
    }

    private ValueTask CompleteProvisionFromObservedAsync(
        CharacterMemoryStatusSnapshot status,
        PodOpenResult.Available observed
    ) {
        if (!string.Equals(
                observed.Identity,
                status.ProvisionTargetPodStateIdentity,
                StringComparison.Ordinal)) {
            _ = Quarantine(
                status,
                CharacterNoteDefaultPodOutcomeCodes.ProvisionStateMismatch,
                observed.Identity
            );
            return ValueTask.CompletedTask;
        }
        try {
            observed.Pod.ConfirmCurrentDocumentDurability();
        }
        catch (CharacterNoteDefaultPodAccessException exception) {
            if (exception.Kind is CharacterNoteDefaultPodFailureKind.NotFound
                or CharacterNoteDefaultPodFailureKind.IoFailure) {
                throw;
            }
            _ = Quarantine(
                status,
                CharacterNoteDefaultPodOutcomeCodes
                    .ProvisionStateMismatch,
                observed.Identity
            );
            return ValueTask.CompletedTask;
        }
        _ = _store.RecordInitialDefaultPod(observed.Identity);
        return ValueTask.CompletedTask;
    }

    private async ValueTask RecoverProvisionPublishFailureAsync(
        CharacterMemoryStatusSnapshot status,
        IOException original
    ) {
        PodOpenResult observed = OpenDefaultPod();
        switch (observed) {
            case PodOpenResult.Absent:
                throw original;
            case PodOpenResult.Unavailable unavailable:
                throw unavailable.Exception;
            case PodOpenResult.Invalid:
                _ = Quarantine(
                    status,
                    CharacterNoteDefaultPodOutcomeCodes
                        .ProvisionStateMismatch,
                    observedIdentity: null
                );
                return;
            case PodOpenResult.Available available:
                await CompleteProvisionFromObservedAsync(status, available)
                    .ConfigureAwait(false);
                return;
            default:
                throw new InvalidDataException("Unknown Default Pod state.");
        }
    }

    internal async ValueTask<CharacterNoteDefaultPodReconcileResult>
        ReconcileCapturedBatchAsync(
        CharacterMemoryStatusSnapshot status,
        CharacterMemoryCaptureSnapshot capture
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            capture.SourceActionAddress
        );
        if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
            return new CharacterNoteDefaultPodReconcileResult.Quarantined(
                status.QuarantineCode!
            );
        }
        return capture.State switch {
            CharacterMemoryCaptureState.ZeroCaptured =>
                new CharacterNoteDefaultPodReconcileResult.ZeroCaptured(
                    source
                ),
            CharacterMemoryCaptureState.Applied =>
                CheckCurrentTip(status, source, captureExists: true)
                    ?? new CharacterNoteDefaultPodReconcileResult
                        .AlreadyApplied(source),
            CharacterMemoryCaptureState.Rejected =>
                CheckCurrentTip(status, source, captureExists: true)
                    ?? new CharacterNoteDefaultPodReconcileResult.Rejected(
                        source,
                        capture.RejectionCode!
                    ),
            CharacterMemoryCaptureState.Captured =>
                await PlanAndPublishAsync(status, capture)
                    .ConfigureAwait(false),
            CharacterMemoryCaptureState.Planned =>
                await ReconcilePlannedAsync(status, capture)
                    .ConfigureAwait(false),
            _ => throw new InvalidDataException(
                "Unknown Character Memory capture state."
            )
        };
    }

    private async ValueTask<CharacterNoteDefaultPodReconcileResult>
        PlanAndPublishAsync(
        CharacterMemoryStatusSnapshot status,
        CharacterMemoryCaptureSnapshot capture
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            capture.SourceActionAddress
        );
        PodOpenResult observed = OpenDefaultPod();
        if (observed is PodOpenResult.Unavailable) {
            return Deferred(source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable);
        }
        if (observed is not PodOpenResult.Available available) {
            return QuarantineResult(
                status,
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                ObservedIdentity(observed)
            );
        }
        if (!string.Equals(
                available.Identity,
                status.SettledDefaultPodStateIdentity,
                StringComparison.Ordinal)) {
            return QuarantineResult(
                status,
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                available.Identity
            );
        }
        if (!HasCapacity(available.Pod, capture.Notes)) {
            try {
                _ = _store.Reject(new CharacterMemoryRejectRequest(
                    capture.SourceActionAddress,
                    capture.ExtractionCommitment,
                    CharacterNoteDefaultPodOutcomeCodes.CapacityExceeded
                ));
            }
            catch (IOException) {
                return Deferred(source,
                    CharacterNoteDefaultPodOutcomeCodes.PodUnavailable);
            }
            return new CharacterNoteDefaultPodReconcileResult.Rejected(
                source,
                CharacterNoteDefaultPodOutcomeCodes.CapacityExceeded
            );
        }

        available.Pod.ResumeEditing();
        string[] memoIds = capture.Notes
            .Select(note => available.Pod.Append(note.ExactText).Value)
            .ToArray();
        string targetIdentity = available.Pod.ComputeStateIdentity();
        CharacterMemoryPlanResult plan;
        try {
            plan = _store.PlanApply(new CharacterMemoryPlanRequest(
                capture.SourceActionAddress,
                capture.ExtractionCommitment,
                available.Identity,
                targetIdentity,
                memoIds
            ));
        }
        catch (IOException) {
            return Deferred(source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable);
        }
        return await PublishPlannedCandidateAsync(
                plan.Capture,
                available.Pod,
                targetIdentity
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<CharacterNoteDefaultPodReconcileResult>
        ReconcilePlannedAsync(
        CharacterMemoryStatusSnapshot status,
        CharacterMemoryCaptureSnapshot capture
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            capture.SourceActionAddress
        );
        if (!string.Equals(
                capture.BasePodStateIdentity,
                status.SettledDefaultPodStateIdentity,
                StringComparison.Ordinal)) {
            return QuarantineResult(
                status,
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                capture.BasePodStateIdentity
            );
        }
        PodOpenResult observed = OpenDefaultPod();
        if (observed is PodOpenResult.Unavailable) {
            return Deferred(source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable);
        }
        if (observed is not PodOpenResult.Available available) {
            return QuarantineResult(
                status,
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                ObservedIdentity(observed)
            );
        }

        if (string.Equals(
                available.Identity,
                capture.TargetPodStateIdentity,
                StringComparison.Ordinal)) {
            try {
                available.Pod.ConfirmCurrentDocumentDurability();
            }
            catch (CharacterNoteDefaultPodAccessException exception) {
                if (exception.Kind is
                        CharacterNoteDefaultPodFailureKind.IoFailure) {
                    return Deferred(source,
                        CharacterNoteDefaultPodOutcomeCodes
                            .DurabilityUnconfirmed);
                }
                return QuarantineResult(
                    status,
                    CharacterNoteDefaultPodOutcomeCodes
                        .CurrentStateMismatch,
                    available.Identity
                );
            }
            return SettleApplied(capture);
        }
        if (!string.Equals(
                available.Identity,
                capture.BasePodStateIdentity,
                StringComparison.Ordinal)) {
            return QuarantineResult(
                status,
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                available.Identity
            );
        }

        available.Pod.ResumeEditing();
        for (int index = 0; index < capture.Notes.Count; index++) {
            string actual = available.Pod.Append(
                capture.Notes[index].ExactText
            ).Value;
            if (!string.Equals(
                    actual,
                    capture.Notes[index].MemoId,
                    StringComparison.Ordinal)) {
                return QuarantineResult(
                    status,
                    CharacterNoteDefaultPodOutcomeCodes
                        .PlannedMemoIdMismatch,
                    available.Identity
                );
            }
        }
        string candidateIdentity = available.Pod.ComputeStateIdentity();
        if (!string.Equals(
                candidateIdentity,
                capture.TargetPodStateIdentity,
                StringComparison.Ordinal)) {
            return QuarantineResult(
                status,
                CharacterNoteDefaultPodOutcomeCodes.PlannedTargetMismatch,
                candidateIdentity
            );
        }
        return await PublishPlannedCandidateAsync(
                capture,
                available.Pod,
                candidateIdentity
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<CharacterNoteDefaultPodReconcileResult>
        PublishPlannedCandidateAsync(
        CharacterMemoryCaptureSnapshot capture,
        ICharacterNoteDefaultPodHandle candidate,
        string targetIdentity
    ) {
        try {
            await candidate.FreezeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return SettleApplied(capture);
        }
        catch (IOException) {
            return ReconcileAfterPublishFailure(capture, targetIdentity);
        }
    }

    private CharacterNoteDefaultPodReconcileResult
        ReconcileAfterPublishFailure(
        CharacterMemoryCaptureSnapshot capture,
        string targetIdentity
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            capture.SourceActionAddress
        );
        CharacterMemoryStatusSnapshot status = _store.ReadStatusSnapshot();
        PodOpenResult observed = OpenDefaultPod();
        if (observed is PodOpenResult.Unavailable) {
            return Deferred(source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable);
        }
        if (observed is not PodOpenResult.Available available) {
            return QuarantineResult(
                status,
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                ObservedIdentity(observed)
            );
        }
        if (string.Equals(
                available.Identity,
                targetIdentity,
                StringComparison.Ordinal)) {
            try {
                available.Pod.ConfirmCurrentDocumentDurability();
            }
            catch (CharacterNoteDefaultPodAccessException exception) {
                if (exception.Kind is
                        CharacterNoteDefaultPodFailureKind.IoFailure) {
                    return Deferred(source,
                        CharacterNoteDefaultPodOutcomeCodes
                            .DurabilityUnconfirmed);
                }
                return QuarantineResult(
                    status,
                    CharacterNoteDefaultPodOutcomeCodes
                        .CurrentStateMismatch,
                    available.Identity
                );
            }
            return SettleApplied(capture);
        }
        if (string.Equals(
                available.Identity,
                capture.BasePodStateIdentity,
                StringComparison.Ordinal)) {
            return Deferred(source,
                CharacterNoteDefaultPodOutcomeCodes.PublishNotSettled);
        }
        return QuarantineResult(
            status,
            CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
            available.Identity
        );
    }

    private CharacterNoteDefaultPodReconcileResult SettleApplied(
        CharacterMemoryCaptureSnapshot capture
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            capture.SourceActionAddress
        );
        CharacterMemorySettleResult settled;
        try {
            settled = _store.SettleApplied(new(
                capture.SourceActionAddress,
                capture.ExtractionCommitment,
                capture.TargetPodStateIdentity!
            ));
        }
        catch (IOException) {
            return Deferred(source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable);
        }
        if (settled.Disposition is
                CharacterMemorySettleDisposition.AlreadyApplied) {
            return new CharacterNoteDefaultPodReconcileResult
                .AlreadyApplied(source);
        }
        CharacterMemoryCaptureSnapshot frozen =
            _store.ReadCaptureExact(capture.SourceActionAddress)
            ?? throw new InvalidDataException(
                "Settled Character Note capture is absent."
            );
        CharacterNoteAppliedMemo[] memos = frozen.Notes.Select(note =>
            new CharacterNoteAppliedMemo(
                frozen.SourceActionAddress,
                note.ArtifactOrdinal,
                CharacterNoteDefaultPodV1.PodId,
                MemoId.Parse(note.MemoId!),
                note.ExactText
            )
        ).ToArray();
        return new CharacterNoteDefaultPodReconcileResult.AppliedNow(
            source,
            Array.AsReadOnly(memos)
        );
    }

    private CharacterNoteDefaultPodReconcileResult? CheckCurrentTip(
        CharacterMemoryStatusSnapshot status,
        EventAddress source,
        bool captureExists
    ) {
        PodOpenResult observed = OpenDefaultPod();
        if (observed is PodOpenResult.Unavailable unavailable) {
            if (!captureExists) { throw unavailable.Exception; }
            return Deferred(source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable);
        }
        if (observed is PodOpenResult.Available available
            && string.Equals(
                available.Identity,
                status.SettledDefaultPodStateIdentity,
                StringComparison.Ordinal)) {
            return null;
        }
        return QuarantineResult(
            status,
            CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
            ObservedIdentity(observed)
        );
    }

    private CharacterNoteDefaultPodReconcileResult ResultForTerminalCapture(
        CharacterMemoryCaptureSnapshot capture
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            capture.SourceActionAddress
        );
        return capture.State switch {
            CharacterMemoryCaptureState.ZeroCaptured =>
                new CharacterNoteDefaultPodReconcileResult.ZeroCaptured(
                    source
                ),
            CharacterMemoryCaptureState.Applied =>
                new CharacterNoteDefaultPodReconcileResult.AlreadyApplied(
                    source
                ),
            CharacterMemoryCaptureState.Rejected =>
                new CharacterNoteDefaultPodReconcileResult.Rejected(
                    source,
                    capture.RejectionCode!
                ),
            _ => throw new InvalidDataException(
                "An active capture was absent but its capture state is nonterminal."
            )
        };
    }

    private CharacterNoteDefaultPodReconcileResult QuarantineResult(
        CharacterMemoryStatusSnapshot status,
        string code,
        string? observedIdentity
    ) {
        _ = Quarantine(status, code, observedIdentity);
        return new CharacterNoteDefaultPodReconcileResult.Quarantined(code);
    }

    private CharacterMemoryQuarantineResult Quarantine(
        CharacterMemoryStatusSnapshot status,
        string code,
        string? observedIdentity
    ) => _store.Quarantine(new CharacterMemoryQuarantineRequest(
        status.StoreRevision,
        code,
        observedIdentity
    ));

    private PodOpenResult OpenDefaultPod() {
        try {
            ICharacterNoteDefaultPodHandle pod = _pods.Open(
                _store.StoreDirectory,
                CharacterNoteDefaultPodV1.PodId
            );
            if (pod.Phase is not MemoPodPhase.Frozen
                || pod.PodId != CharacterNoteDefaultPodV1.PodId) {
                return new PodOpenResult.Invalid();
            }
            return new PodOpenResult.Available(
                pod,
                pod.ComputeStateIdentity()
            );
        }
        catch (CharacterNoteDefaultPodAccessException exception) {
            return exception.Kind switch {
                CharacterNoteDefaultPodFailureKind.NotFound =>
                    new PodOpenResult.Absent(),
                CharacterNoteDefaultPodFailureKind.IoFailure =>
                    new PodOpenResult.Unavailable(exception),
                _ => new PodOpenResult.Invalid(),
            };
        }
    }

    private static bool HasCapacity(
        ICharacterNoteDefaultPodHandle pod,
        IReadOnlyList<CharacterMemoryNoteSnapshot> notes
    ) {
        if (pod.ActiveMemoCount + notes.Count
                > MemoPodLimits.MaximumActiveMemoCount) {
            return false;
        }
        long exactTextBytes = pod.ActiveExactTextUtf8Bytes;
        foreach (CharacterMemoryNoteSnapshot note in notes) {
            exactTextBytes += TextExtractorUtf8.GetByteCount(note.ExactText);
            if (exactTextBytes
                    > MemoPodLimits.MaximumActiveExactTextUtf8Bytes) {
                return false;
            }
        }
        return true;
    }

    private static CharacterNoteDefaultPodReconcileResult
        Deferred(EventAddress source, string code) =>
        new CharacterNoteDefaultPodReconcileResult.DeferredAfterCapture(
            source,
            code
        );

    private static string? ObservedIdentity(PodOpenResult observed) =>
        observed is PodOpenResult.Available available
            ? available.Identity
            : null;

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private abstract record PodOpenResult {
        private PodOpenResult() { }
        internal sealed record Absent : PodOpenResult;
        internal sealed record Invalid : PodOpenResult;
        internal sealed record Unavailable(
            CharacterNoteDefaultPodAccessException Exception
        ) : PodOpenResult;
        internal sealed record Available(
            ICharacterNoteDefaultPodHandle Pod,
            string Identity
        ) : PodOpenResult;
    }
}
