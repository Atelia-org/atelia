using Atelia.EventJournal;
using Atelia.MemoPod;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterNoteDefaultPodReconciler {
    internal async ValueTask<CharacterNoteDerivedInfoReconcileResult>
        ReconcileNextDerivedInfoAsync(
        CharacterNoteDerivedInfoMaterializeCallback materialize,
        ICharacterNoteDerivedInfoEnricher enricher,
        CancellationToken providerCancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(materialize);
        ArgumentNullException.ThrowIfNull(enricher);
        await _derivedInfoDispatchGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try {
            return await ReconcileNextDerivedInfoCoreAsync(
                    materialize,
                    enricher,
                    providerCancellationToken
                )
                .ConfigureAwait(false);
        }
        finally {
            _derivedInfoDispatchGate.Release();
        }
    }

    private async ValueTask<CharacterNoteDerivedInfoReconcileResult>
        ReconcileNextDerivedInfoCoreAsync(
        CharacterNoteDerivedInfoMaterializeCallback materialize,
        ICharacterNoteDerivedInfoEnricher enricher,
        CancellationToken providerCancellationToken
    ) {
        CharacterMemoryStatusSnapshot status = _store.ReadStatusSnapshot();
        if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
            return new CharacterNoteDerivedInfoReconcileResult.Quarantined(
                status.QuarantineCode!
            );
        }
        CharacterMemoryDerivedInfoWorkSnapshot? work =
            _store.ReadNextDerivedInfoWork(_derivedInfoPendingCursor);
        if (work is null) {
            return new CharacterNoteDerivedInfoReconcileResult.NoWork();
        }
        if (work.State is CharacterMemoryDerivedInfoState.Planned) {
            return await ReconcileDerivedInfoUnderGateAsync(work)
                .ConfigureAwait(false);
        }
        if (work.State is CharacterMemoryDerivedInfoState.Prepared) {
            return await ReconcileDerivedInfoUnderGateAsync(work)
                .ConfigureAwait(false);
        }
        if (work.State is not CharacterMemoryDerivedInfoState.Pending) {
            return await QuarantineDerivedInfoUnderGateAsync(
                    CharacterNoteDefaultPodOutcomeCodes
                        .DerivedInfoTargetMismatch,
                    observedIdentity: null
                )
                .ConfigureAwait(false);
        }
        _derivedInfoPendingCursor = new(
            work.CreatedRevision,
            work.SourceActionAddress
        );

        EventAddress source = EventAddressTextCodec.Parse(
            work.SourceActionAddress
        );
        CharacterNoteDerivedInfoEnrichmentRequest request;
        try {
            request = await materialize(
                    work,
                    providerCancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "Character Note DerivedInfo materializer returned a null request."
                );
        }
        catch (OperationCanceledException) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes
                    .DerivedInfoContextUnavailable
            );
        }
        catch (InvalidDataException) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes
                    .DerivedInfoContextMismatch
            );
        }
        catch (IOException) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes
                    .DerivedInfoContextUnavailable
            );
        }

        IReadOnlyList<CharacterNoteDerivedInfo> output;
        try {
            output = await enricher.EnrichAsync(
                    request,
                    providerCancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "Character Note DerivedInfo enricher returned a null batch."
                );
        }
        catch (Exception exception) when (!IsFatalDerivedInfo(exception)) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes
                    .DerivedInfoProviderUnavailable
            );
        }

        try {
            CharacterNoteDerivedInfo[] outputItems = output.ToArray();
            if (outputItems.Any(static item => item is null)) {
                throw new InvalidDataException(
                    "Character Note DerivedInfo output contains a null item."
                );
            }
            CharacterMemoryDerivedInfoValue[] values = outputItems.Select(
                static item => new CharacterMemoryDerivedInfoValue(
                    item.ArtifactOrdinal,
                    item.Title,
                    item.Gist,
                    item.Summary
                )
            ).ToArray();
            work = _store.PrepareDerivedInfo(new(
                work.SourceActionAddress,
                work.ExtractionCommitment,
                enricher.ContractId,
                Array.AsReadOnly(values)
            )).Work;
        }
        catch (ArgumentException) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes
                    .DerivedInfoProviderUnavailable
            );
        }
        catch (InvalidDataException) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes
                    .DerivedInfoProviderUnavailable
            );
        }
        catch (IOException) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable
            );
        }
        catch (CharacterMemoryStoreConflictException) {
            CharacterMemoryDerivedInfoWorkSnapshot? current =
                _store.ReadDerivedInfoWorkExact(work.SourceActionAddress);
            if (current is null
                || current.State is CharacterMemoryDerivedInfoState.Pending) {
                return DeferredDerivedInfo(
                    source,
                    CharacterNoteDefaultPodOutcomeCodes
                        .DerivedInfoProviderUnavailable
                );
            }
            work = current;
        }

        return await ReconcileDerivedInfoUnderGateAsync(work)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Admission-only recovery for an already durable Planned DerivedInfo
    /// mutation. This path never materializes turn context or calls a model.
    /// </summary>
    internal async ValueTask<CharacterNoteDerivedInfoReconcileResult>
        ReconcileActiveDerivedInfoPlanAsync() {
        ThrowIfDisposed();
        CharacterMemoryStatusSnapshot status = _store.ReadStatusSnapshot();
        if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
            return new CharacterNoteDerivedInfoReconcileResult.Quarantined(
                status.QuarantineCode!
            );
        }
        if (status.ActiveDerivedInfoWork is null) {
            return new CharacterNoteDerivedInfoReconcileResult.NoWork();
        }
        return await ReconcileDerivedInfoUnderGateAsync(
                status.ActiveDerivedInfoWork
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<CharacterNoteDerivedInfoReconcileResult>
        ReconcileDerivedInfoUnderGateAsync(
        CharacterMemoryDerivedInfoWorkSnapshot work
    ) {
        await _podMutationGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try {
            CharacterMemoryStatusSnapshot status =
                _store.ReadStatusSnapshot();
            if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
                return new CharacterNoteDerivedInfoReconcileResult
                    .Quarantined(status.QuarantineCode!);
            }
            CharacterMemoryDerivedInfoWorkSnapshot current =
                _store.ReadDerivedInfoWorkExact(work.SourceActionAddress)
                ?? throw new InvalidDataException(
                    "Selected Character Note DerivedInfo work is absent."
                );
            return current.State switch {
                CharacterMemoryDerivedInfoState.Prepared =>
                    await PlanAndPublishDerivedInfoAsync(status, current)
                        .ConfigureAwait(false),
                CharacterMemoryDerivedInfoState.Planned =>
                    await ReconcilePlannedDerivedInfoAsync(status, current)
                        .ConfigureAwait(false),
                CharacterMemoryDerivedInfoState.Applied =>
                    new CharacterNoteDerivedInfoReconcileResult.Applied(
                        EventAddressTextCodec.Parse(
                            current.SourceActionAddress
                        )
                    ),
                CharacterMemoryDerivedInfoState.Rejected =>
                    new CharacterNoteDerivedInfoReconcileResult.Rejected(
                        EventAddressTextCodec.Parse(
                            current.SourceActionAddress
                        ),
                        current.RejectionCode!
                    ),
                CharacterMemoryDerivedInfoState.Pending =>
                    DeferredDerivedInfo(
                        EventAddressTextCodec.Parse(
                            current.SourceActionAddress
                        ),
                        CharacterNoteDefaultPodOutcomeCodes
                            .DerivedInfoProviderUnavailable
                    ),
                _ => throw new InvalidDataException(
                    "Unknown Character Note DerivedInfo state."
                ),
            };
        }
        finally {
            _podMutationGate.Release();
        }
    }

    private async ValueTask<CharacterNoteDerivedInfoReconcileResult>
        PlanAndPublishDerivedInfoAsync(
        CharacterMemoryStatusSnapshot status,
        CharacterMemoryDerivedInfoWorkSnapshot work
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            work.SourceActionAddress
        );
        if (status.ActiveCapture is not null
            || status.ActiveDerivedInfoWork is not null) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable
            );
        }

        PodOpenResult observed = OpenDefaultPod();
        if (observed is PodOpenResult.Unavailable) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable
            );
        }
        if (observed is not PodOpenResult.Available available
            || !string.Equals(
                available.Identity,
                status.SettledDefaultPodStateIdentity,
                StringComparison.Ordinal
            )) {
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                ObservedIdentity(observed)
            );
        }

        DerivedInfoCandidateResult candidate = ApplyDerivedInfo(
            available.Pod,
            work
        );
        if (candidate is DerivedInfoCandidateResult.CapacityExceeded) {
            try {
                CharacterMemoryRejectDerivedInfoResult rejected =
                    _store.RejectDerivedInfo(new(
                        work.SourceActionAddress,
                        work.ExtractionCommitment,
                        work.DerivedInfoCommitment!,
                        CharacterNoteDefaultPodOutcomeCodes
                            .DerivedInfoCapacityExceeded
                    ));
                return new CharacterNoteDerivedInfoReconcileResult.Rejected(
                    source,
                    rejected.Work.RejectionCode!
                );
            }
            catch (IOException) {
                return DeferredDerivedInfo(
                    source,
                    CharacterNoteDefaultPodOutcomeCodes.PodUnavailable
                );
            }
        }
        if (candidate is DerivedInfoCandidateResult.MemoMismatch) {
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.DerivedInfoMemoMismatch,
                available.Identity
            );
        }

        string targetIdentity = ((DerivedInfoCandidateResult.Ready)candidate)
            .TargetIdentity;
        if (string.Equals(
                targetIdentity,
                available.Identity,
                StringComparison.Ordinal
            )) {
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.DerivedInfoTargetMismatch,
                targetIdentity
            );
        }

        CharacterMemoryPlanDerivedInfoResult planned;
        try {
            planned = _store.PlanDerivedInfo(new(
                work.SourceActionAddress,
                work.ExtractionCommitment,
                work.DerivedInfoCommitment!,
                available.Identity,
                targetIdentity
            ));
        }
        catch (IOException) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable
            );
        }
        catch (CharacterMemoryStoreConflictException) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable
            );
        }
        if (planned.Disposition is
                CharacterMemoryPlanDerivedInfoDisposition.AlreadyApplied) {
            return new CharacterNoteDerivedInfoReconcileResult.Applied(source);
        }
        if (planned.Disposition is
                CharacterMemoryPlanDerivedInfoDisposition.AlreadyPlanned
            && !string.Equals(
                planned.Work.TargetPodStateIdentity,
                targetIdentity,
                StringComparison.Ordinal
            )) {
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.DerivedInfoTargetMismatch,
                targetIdentity
            );
        }
        return await PublishDerivedInfoCandidateAsync(
                planned.Work,
                available.Pod,
                targetIdentity
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<CharacterNoteDerivedInfoReconcileResult>
        ReconcilePlannedDerivedInfoAsync(
        CharacterMemoryStatusSnapshot status,
        CharacterMemoryDerivedInfoWorkSnapshot work
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            work.SourceActionAddress
        );
        if (!string.Equals(
                status.ActiveDerivedInfoSourceAction,
                work.SourceActionAddress,
                StringComparison.Ordinal
            )
            || status.ActiveCapture is not null
            || !string.Equals(
                status.SettledDefaultPodStateIdentity,
                work.BasePodStateIdentity,
                StringComparison.Ordinal
            )) {
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                work.BasePodStateIdentity
            );
        }

        PodOpenResult observed = OpenDefaultPod();
        if (observed is PodOpenResult.Unavailable) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable
            );
        }
        if (observed is not PodOpenResult.Available available) {
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                ObservedIdentity(observed)
            );
        }
        if (string.Equals(
                available.Identity,
                work.TargetPodStateIdentity,
                StringComparison.Ordinal
            )) {
            return ConfirmAndSettleDerivedInfo(work, available.Pod);
        }
        if (!string.Equals(
                available.Identity,
                work.BasePodStateIdentity,
                StringComparison.Ordinal
            )) {
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                available.Identity
            );
        }

        DerivedInfoCandidateResult candidate = ApplyDerivedInfo(
            available.Pod,
            work
        );
        if (candidate is not DerivedInfoCandidateResult.Ready ready) {
            return QuarantineDerivedInfo(
                candidate is DerivedInfoCandidateResult.MemoMismatch
                    ? CharacterNoteDefaultPodOutcomeCodes
                        .DerivedInfoMemoMismatch
                    : CharacterNoteDefaultPodOutcomeCodes
                        .DerivedInfoTargetMismatch,
                available.Identity
            );
        }
        if (!string.Equals(
                ready.TargetIdentity,
                work.TargetPodStateIdentity,
                StringComparison.Ordinal
            )) {
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.DerivedInfoTargetMismatch,
                ready.TargetIdentity
            );
        }
        return await PublishDerivedInfoCandidateAsync(
                work,
                available.Pod,
                ready.TargetIdentity
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<CharacterNoteDerivedInfoReconcileResult>
        PublishDerivedInfoCandidateAsync(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        ICharacterNoteDefaultPodHandle candidate,
        string targetIdentity
    ) {
        try {
            await candidate.FreezeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return SettleDerivedInfo(work);
        }
        catch (IOException) {
            return RecoverDerivedInfoPublishFailure(work, targetIdentity);
        }
    }

    private CharacterNoteDerivedInfoReconcileResult
        RecoverDerivedInfoPublishFailure(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        string targetIdentity
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            work.SourceActionAddress
        );
        PodOpenResult observed = OpenDefaultPod();
        if (observed is PodOpenResult.Unavailable) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable
            );
        }
        if (observed is not PodOpenResult.Available available) {
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                ObservedIdentity(observed)
            );
        }
        if (string.Equals(
                available.Identity,
                targetIdentity,
                StringComparison.Ordinal
            )) {
            return ConfirmAndSettleDerivedInfo(work, available.Pod);
        }
        if (string.Equals(
                available.Identity,
                work.BasePodStateIdentity,
                StringComparison.Ordinal
            )) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes.PublishNotSettled
            );
        }
        return QuarantineDerivedInfo(
            CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
            available.Identity
        );
    }

    private CharacterNoteDerivedInfoReconcileResult ConfirmAndSettleDerivedInfo(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        ICharacterNoteDefaultPodHandle pod
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            work.SourceActionAddress
        );
        try {
            pod.ConfirmCurrentDocumentDurability();
        }
        catch (CharacterNoteDefaultPodAccessException exception) {
            if (exception.Kind is CharacterNoteDefaultPodFailureKind.IoFailure) {
                return DeferredDerivedInfo(
                    source,
                    CharacterNoteDefaultPodOutcomeCodes
                        .DurabilityUnconfirmed
                );
            }
            return QuarantineDerivedInfo(
                CharacterNoteDefaultPodOutcomeCodes.CurrentStateMismatch,
                work.TargetPodStateIdentity
            );
        }
        return SettleDerivedInfo(work);
    }

    private CharacterNoteDerivedInfoReconcileResult SettleDerivedInfo(
        CharacterMemoryDerivedInfoWorkSnapshot work
    ) {
        EventAddress source = EventAddressTextCodec.Parse(
            work.SourceActionAddress
        );
        try {
            _ = _store.SettleDerivedInfoApplied(new(
                work.SourceActionAddress,
                work.ExtractionCommitment,
                work.DerivedInfoCommitment!,
                work.TargetPodStateIdentity!
            ));
            return new CharacterNoteDerivedInfoReconcileResult.Applied(source);
        }
        catch (IOException) {
            return DeferredDerivedInfo(
                source,
                CharacterNoteDefaultPodOutcomeCodes.PodUnavailable
            );
        }
    }

    private static DerivedInfoCandidateResult ApplyDerivedInfo(
        ICharacterNoteDefaultPodHandle pod,
        CharacterMemoryDerivedInfoWorkSnapshot work
    ) {
        long projectedBytes = pod.ActiveDerivedInfoUtf8Bytes;
        var updates = new List<(MemoId Id,
            CharacterMemoryDerivedInfoNoteSnapshot Note)>();
        foreach (CharacterMemoryDerivedInfoNoteSnapshot note in work.Notes) {
            MemoId id;
            Memo memo;
            try {
                id = MemoId.Parse(note.MemoId);
                memo = pod.Get(id);
            }
            catch (Exception exception) when (exception is
                ArgumentException or FormatException
                    or KeyNotFoundException) {
                return new DerivedInfoCandidateResult.MemoMismatch();
            }
            if (!string.Equals(
                    memo.ExactText,
                    note.ExactText,
                    StringComparison.Ordinal
                )) {
                return new DerivedInfoCandidateResult.MemoMismatch();
            }
            projectedBytes -= DerivedInfoUtf8Bytes(memo);
            projectedBytes += TextExtractorUtf8.GetByteCount(note.Title!)
                + TextExtractorUtf8.GetByteCount(note.Gist!)
                + TextExtractorUtf8.GetByteCount(note.Summary!);
            if (projectedBytes
                    > MemoPodLimits.MaximumActiveMemoDerivedInfoUtf8Bytes) {
                return new DerivedInfoCandidateResult.CapacityExceeded();
            }
            updates.Add((id, note));
        }

        pod.ResumeEditing();
        foreach ((MemoId id, CharacterMemoryDerivedInfoNoteSnapshot note)
                 in updates) {
            pod.UpdateDerivedInfo(
                id,
                note.Title!,
                note.Gist!,
                note.Summary!
            );
        }
        return new DerivedInfoCandidateResult.Ready(
            pod.ComputeStateIdentity()
        );
    }

    private static int DerivedInfoUtf8Bytes(Memo memo) =>
        OptionalUtf8Bytes(memo.Title)
        + OptionalUtf8Bytes(memo.Gist)
        + OptionalUtf8Bytes(memo.Summary);

    private static int OptionalUtf8Bytes(string? value) => value is null
        ? 0
        : TextExtractorUtf8.GetByteCount(value);

    private async ValueTask<CharacterNoteDerivedInfoReconcileResult>
        QuarantineDerivedInfoUnderGateAsync(
        string code,
        string? observedIdentity
    ) {
        await _podMutationGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try {
            return QuarantineDerivedInfo(code, observedIdentity);
        }
        finally {
            _podMutationGate.Release();
        }
    }

    private CharacterNoteDerivedInfoReconcileResult QuarantineDerivedInfo(
        string code,
        string? observedIdentity
    ) {
        CharacterMemoryStatusSnapshot status = _store.ReadStatusSnapshot();
        if (status.StoreState is not CharacterMemoryStoreState.Quarantined) {
            _ = Quarantine(status, code, observedIdentity);
        }
        return new CharacterNoteDerivedInfoReconcileResult.Quarantined(code);
    }

    private static CharacterNoteDerivedInfoReconcileResult DeferredDerivedInfo(
        EventAddress source,
        string code
    ) => new CharacterNoteDerivedInfoReconcileResult.Deferred(source, code);

    private static bool IsFatalDerivedInfo(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    private abstract record DerivedInfoCandidateResult {
        private DerivedInfoCandidateResult() { }

        internal sealed record Ready(string TargetIdentity)
            : DerivedInfoCandidateResult;

        internal sealed record CapacityExceeded
            : DerivedInfoCandidateResult;

        internal sealed record MemoMismatch
            : DerivedInfoCandidateResult;
    }
}
