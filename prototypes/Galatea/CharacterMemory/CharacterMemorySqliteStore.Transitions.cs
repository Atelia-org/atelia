using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterMemorySqliteStore {
    internal CharacterMemoryProvisionResult RecordInitialDefaultPod(
        string observedTargetPodStateIdentity
    ) {
        RequirePodStateIdentity(
            observedTargetPodStateIdentity,
            nameof(observedTargetPodStateIdentity)
        );
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite(
                "record-initial-default-pod",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = ReadStatusCore(
                        connection,
                        transaction
                    );
                    if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
                        throw new CharacterMemoryStoreQuarantinedException(
                            status.QuarantineCode!
                        );
                    }
                    if (!string.Equals(
                            observedTargetPodStateIdentity,
                            status.ProvisionTargetPodStateIdentity,
                            StringComparison.Ordinal)) {
                        throw new CharacterMemoryStoreConflictException(
                            "Initial Default Pod identity does not match the frozen provision target."
                        );
                    }
                    if (status.StoreState is CharacterMemoryStoreState.Ready) {
                        return new CharacterMemoryProvisionResult(
                            CharacterMemoryProvisionDisposition.AlreadyRecorded,
                            status.StoreRevision
                        );
                    }
                    long revision = IncrementStoreRevision(
                        connection,
                        transaction
                    );
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE character_memory_meta
                        SET store_state = 'Ready',
                            settled_default_pod_state_identity = $identity
                        WHERE singleton = 1 AND store_state = 'Provisioning';
                        """;
                    update.Parameters.AddWithValue(
                        "$identity",
                        observedTargetPodStateIdentity
                    );
                    RequireOne(update.ExecuteNonQuery(), "initial Pod settlement");
                    return new CharacterMemoryProvisionResult(
                        CharacterMemoryProvisionDisposition.Recorded,
                        revision
                    );
                },
                result => {
                    CharacterMemoryStatusSnapshot status = ReadStatusSnapshot();
                    return status.StoreState is CharacterMemoryStoreState.Ready
                        && string.Equals(
                            status.ProvisionTargetPodStateIdentity,
                            observedTargetPodStateIdentity,
                            StringComparison.Ordinal)
                        && (result.Disposition is
                                CharacterMemoryProvisionDisposition.AlreadyRecorded
                            || string.Equals(
                                status.SettledDefaultPodStateIdentity,
                                observedTargetPodStateIdentity,
                                StringComparison.Ordinal))
                        && status.StoreRevision == result.StoreRevision;
                }
            );
        }
    }

    internal CharacterMemoryCaptureResult CaptureNew(
        CharacterMemoryCaptureRequest request
    ) {
        ValidateCaptureRequest(request);
        Atelia.EventJournal.EventAddress source = EventAddressTextCodec.Parse(
            request.SourceActionAddress
        );
        lock (_gate) {
            ThrowIfDisposed();
            if (_baseline.CaptureFromPhysicalFrontier.Contains(source)) {
                using SqliteConnection connection = OpenVerifiedConnection();
                CharacterMemoryStatusSnapshot status = RequireReady(
                    connection,
                    transaction: null
                );
                return new CharacterMemoryCaptureResult(
                    CharacterMemoryCaptureDisposition.BaselineCovered,
                    status.StoreRevision,
                    Capture: null
                );
            }
            string commitment = ComputeExtractionCommitment(
                request.SourceActionAddress,
                request.VisibleActionSha256,
                request.VisibleActionUtf8Bytes,
                request.ExtractorContractId,
                request.ExactTexts
            );
            return ExecuteWrite(
                request.ExactTexts.Count == 0
                    ? "capture-note-action-zero"
                    : "capture-note-action",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = RequireReady(
                        connection,
                        transaction
                    );
                    CharacterMemoryCaptureSnapshot? existing =
                        ReadCaptureCore(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        );
                    if (existing is not null) {
                        RequireCaptureIdentity(existing, request, commitment);
                        return new CharacterMemoryCaptureResult(
                            CharacterMemoryCaptureDisposition.AlreadyCaptured,
                            status.StoreRevision,
                            existing
                        );
                    }
                    if (status.ActiveSourceAction is not null) {
                        throw new CharacterMemoryStoreConflictException(
                            "Another Character Note capture still requires settlement."
                        );
                    }

                    long revision = IncrementStoreRevision(
                        connection,
                        transaction
                    );
                    CharacterMemoryCaptureState state = request.ExactTexts.Count == 0
                        ? CharacterMemoryCaptureState.ZeroCaptured
                        : CharacterMemoryCaptureState.Captured;
                    InsertCapture(
                        connection,
                        transaction,
                        request,
                        commitment,
                        state,
                        revision
                    );
                    if (state is CharacterMemoryCaptureState.Captured) {
                        SetActiveSource(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        );
                    }
                    CharacterMemoryCaptureSnapshot captured = ReadCaptureCore(
                        connection,
                        transaction,
                        request.SourceActionAddress
                    ) ?? throw Corrupt("Inserted capture is absent.");
                    return new CharacterMemoryCaptureResult(
                        state is CharacterMemoryCaptureState.ZeroCaptured
                            ? CharacterMemoryCaptureDisposition.ZeroCaptured
                            : CharacterMemoryCaptureDisposition.Captured,
                        revision,
                        captured
                    );
                },
                result => {
                    CharacterMemoryCaptureSnapshot? capture = ReadCaptureExact(
                        request.SourceActionAddress
                    );
                    return capture is not null
                        && string.Equals(capture.ExtractionCommitment,
                            commitment, StringComparison.Ordinal)
                        && capture.State == result.Capture?.State
                        && ReadStatusSnapshot().StoreRevision
                            == result.StoreRevision;
                }
            );
        }
    }

    internal CharacterMemoryPlanResult PlanApply(
        CharacterMemoryPlanRequest request
    ) {
        ValidatePlanRequest(request);
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite(
                "plan-note-apply",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = RequireReady(
                        connection,
                        transaction
                    );
                    CharacterMemoryCaptureSnapshot capture = RequireCapture(
                        connection,
                        transaction,
                        request.SourceActionAddress
                    );
                    RequireCommitment(capture, request.ExtractionCommitment);
                    if (capture.State is CharacterMemoryCaptureState.Applied) {
                        RequireExactPlan(capture, request);
                        return new CharacterMemoryPlanResult(
                            CharacterMemoryPlanDisposition.AlreadyApplied,
                            status.StoreRevision,
                            capture
                        );
                    }
                    if (capture.State is CharacterMemoryCaptureState.Planned) {
                        RequireExactPlan(capture, request);
                        return new CharacterMemoryPlanResult(
                            CharacterMemoryPlanDisposition.AlreadyPlanned,
                            status.StoreRevision,
                            capture
                        );
                    }
                    if (capture.State is not CharacterMemoryCaptureState.Captured) {
                        throw new CharacterMemoryStoreConflictException(
                            "Only a non-empty Captured batch can be planned."
                        );
                    }
                    if (!string.Equals(
                            status.ActiveSourceAction,
                            request.SourceActionAddress,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            status.SettledDefaultPodStateIdentity,
                            request.BasePodStateIdentity,
                            StringComparison.Ordinal)) {
                        throw new CharacterMemoryStoreConflictException(
                            "Apply plan base does not match the active settled Default Pod."
                        );
                    }
                    if (request.MemoIds.Count != capture.ArtifactCount) {
                        throw new CharacterMemoryStoreConflictException(
                            "Apply plan local MemoId count changed."
                        );
                    }
                    long revision = IncrementStoreRevision(
                        connection,
                        transaction
                    );
                    using (SqliteCommand update = connection.CreateCommand()) {
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE note_action_capture
                            SET state = 'Planned',
                                base_pod_state_identity = $base,
                                target_pod_state_identity = $target,
                                state_revision = $revision
                            WHERE source_action_address = $source
                              AND state = 'Captured';
                            """;
                        update.Parameters.AddWithValue("$base", request.BasePodStateIdentity);
                        update.Parameters.AddWithValue("$target", request.TargetPodStateIdentity);
                        update.Parameters.AddWithValue("$revision", revision);
                        update.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        RequireOne(update.ExecuteNonQuery(), "apply plan transition");
                    }
                    for (int ordinal = 0; ordinal < request.MemoIds.Count; ordinal++) {
                        using SqliteCommand child = connection.CreateCommand();
                        child.Transaction = transaction;
                        child.CommandText = """
                            UPDATE character_note SET memo_id = $memo
                            WHERE source_action_address = $source
                              AND artifact_ordinal = $ordinal
                              AND memo_id IS NULL;
                            """;
                        child.Parameters.AddWithValue("$memo", request.MemoIds[ordinal]);
                        child.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        child.Parameters.AddWithValue("$ordinal", ordinal);
                        RequireOne(child.ExecuteNonQuery(), "planned local MemoId");
                    }
                    CharacterMemoryCaptureSnapshot planned = RequireCapture(
                        connection,
                        transaction,
                        request.SourceActionAddress
                    );
                    return new CharacterMemoryPlanResult(
                        CharacterMemoryPlanDisposition.Planned,
                        revision,
                        planned
                    );
                },
                result => {
                    CharacterMemoryCaptureSnapshot? capture = ReadCaptureExact(
                        request.SourceActionAddress
                    );
                    return capture is not null
                        && capture.State == result.Capture.State
                        && IsExactPlan(capture, request)
                        && ReadStatusSnapshot().StoreRevision
                            == result.StoreRevision;
                }
            );
        }
    }

    internal CharacterMemorySettleResult SettleApplied(
        CharacterMemorySettleRequest request
    ) {
        ValidateSettleRequest(request);
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite(
                "settle-note-apply",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = RequireReady(
                        connection,
                        transaction
                    );
                    CharacterMemoryCaptureSnapshot capture = RequireCapture(
                        connection,
                        transaction,
                        request.SourceActionAddress
                    );
                    RequireCommitment(capture, request.ExtractionCommitment);
                    if (!string.Equals(
                            capture.TargetPodStateIdentity,
                            request.TargetPodStateIdentity,
                            StringComparison.Ordinal)) {
                        throw new CharacterMemoryStoreConflictException(
                            "Applied target identity changed."
                        );
                    }
                    if (capture.State is CharacterMemoryCaptureState.Applied) {
                        return new CharacterMemorySettleResult(
                            CharacterMemorySettleDisposition.AlreadyApplied,
                            status.StoreRevision,
                            capture
                        );
                    }
                    if (capture.State is not CharacterMemoryCaptureState.Planned
                        || !string.Equals(status.ActiveSourceAction,
                            request.SourceActionAddress, StringComparison.Ordinal)
                        || !string.Equals(status.SettledDefaultPodStateIdentity,
                            capture.BasePodStateIdentity, StringComparison.Ordinal)) {
                        throw new CharacterMemoryStoreConflictException(
                            "Only the active Planned batch can settle."
                        );
                    }
                    long revision = IncrementStoreRevision(connection, transaction);
                    using (SqliteCommand update = connection.CreateCommand()) {
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE note_action_capture
                            SET state = 'Applied', state_revision = $revision
                            WHERE source_action_address = $source
                              AND state = 'Planned';
                            """;
                        update.Parameters.AddWithValue("$revision", revision);
                        update.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        RequireOne(update.ExecuteNonQuery(), "apply settlement");
                    }
                    using (SqliteCommand meta = connection.CreateCommand()) {
                        meta.Transaction = transaction;
                        meta.CommandText = """
                            UPDATE character_memory_meta
                            SET settled_default_pod_state_identity = $target,
                                active_source_action = NULL
                            WHERE singleton = 1
                              AND active_source_action = $source;
                            """;
                        meta.Parameters.AddWithValue("$target", request.TargetPodStateIdentity);
                        meta.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        RequireOne(meta.ExecuteNonQuery(), "settled Pod tip advancement");
                    }
                    return new CharacterMemorySettleResult(
                        CharacterMemorySettleDisposition.Applied,
                        revision,
                        RequireCapture(connection, transaction, request.SourceActionAddress)
                    );
                },
                result => {
                    CharacterMemoryCaptureSnapshot? capture = ReadCaptureExact(
                        request.SourceActionAddress
                    );
                    CharacterMemoryStatusSnapshot status = ReadStatusSnapshot();
                    return capture?.State is CharacterMemoryCaptureState.Applied
                        && string.Equals(capture.ExtractionCommitment,
                            request.ExtractionCommitment, StringComparison.Ordinal)
                        && (result.Disposition is
                                CharacterMemorySettleDisposition.AlreadyApplied
                            || string.Equals(
                                status.SettledDefaultPodStateIdentity,
                                request.TargetPodStateIdentity,
                                StringComparison.Ordinal))
                        && status.StoreRevision == result.StoreRevision;
                }
            );
        }
    }

    internal CharacterMemoryRejectResult Reject(
        CharacterMemoryRejectRequest request
    ) {
        ValidateRejectRequest(request);
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite(
                "reject-note-apply",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = RequireReady(
                        connection,
                        transaction
                    );
                    CharacterMemoryCaptureSnapshot capture = RequireCapture(
                        connection,
                        transaction,
                        request.SourceActionAddress
                    );
                    RequireCommitment(capture, request.ExtractionCommitment);
                    if (capture.State is CharacterMemoryCaptureState.Rejected) {
                        if (!string.Equals(capture.RejectionCode,
                                request.RejectionCode, StringComparison.Ordinal)) {
                            throw new CharacterMemoryStoreConflictException(
                                "Rejected outcome changed code."
                            );
                        }
                        return new CharacterMemoryRejectResult(
                            CharacterMemoryRejectDisposition.AlreadyRejected,
                            status.StoreRevision,
                            capture
                        );
                    }
                    if (capture.State is not CharacterMemoryCaptureState.Captured
                        || !string.Equals(status.ActiveSourceAction,
                            request.SourceActionAddress, StringComparison.Ordinal)) {
                        throw new CharacterMemoryStoreConflictException(
                            "Only the active Captured batch can be rejected."
                        );
                    }
                    long revision = IncrementStoreRevision(connection, transaction);
                    using (SqliteCommand update = connection.CreateCommand()) {
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE note_action_capture
                            SET state = 'Rejected', rejection_code = $code,
                                state_revision = $revision
                            WHERE source_action_address = $source
                              AND state = 'Captured';
                            """;
                        update.Parameters.AddWithValue("$code", request.RejectionCode);
                        update.Parameters.AddWithValue("$revision", revision);
                        update.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        RequireOne(update.ExecuteNonQuery(), "capture rejection");
                    }
                    ClearActiveSource(connection, transaction, request.SourceActionAddress);
                    return new CharacterMemoryRejectResult(
                        CharacterMemoryRejectDisposition.Rejected,
                        revision,
                        RequireCapture(connection, transaction, request.SourceActionAddress)
                    );
                },
                result => {
                    CharacterMemoryCaptureSnapshot? capture = ReadCaptureExact(
                        request.SourceActionAddress
                    );
                    return capture?.State is CharacterMemoryCaptureState.Rejected
                        && string.Equals(capture.RejectionCode,
                            request.RejectionCode, StringComparison.Ordinal)
                        && ReadStatusSnapshot().StoreRevision == result.StoreRevision;
                }
            );
        }
    }

    internal CharacterMemoryQuarantineResult Quarantine(
        CharacterMemoryQuarantineRequest request
    ) {
        ValidateQuarantineRequest(request);
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite(
                "quarantine-character-memory",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = ReadStatusCore(
                        connection,
                        transaction
                    );
                    if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
                        if (!string.Equals(status.QuarantineCode,
                                request.QuarantineCode, StringComparison.Ordinal)
                            || !string.Equals(
                                status.QuarantineObservedPodStateIdentity,
                                request.ObservedPodStateIdentity,
                                StringComparison.Ordinal)) {
                            throw new CharacterMemoryStoreConflictException(
                                "Quarantine outcome changed exact evidence."
                            );
                        }
                        return new CharacterMemoryQuarantineResult(
                            CharacterMemoryQuarantineDisposition.AlreadyQuarantined,
                            status.StoreRevision
                        );
                    }
                    if (status.StoreRevision != request.ExpectedStoreRevision) {
                        throw new CharacterMemoryStoreConflictException(
                            "Quarantine observer used a stale store revision."
                        );
                    }
                    long revision = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE character_memory_meta
                        SET store_state = 'Quarantined',
                            quarantine_code = $code,
                            quarantine_observed_pod_state_identity = $observed
                        WHERE singleton = 1 AND store_state != 'Quarantined';
                        """;
                    update.Parameters.AddWithValue("$code", request.QuarantineCode);
                    update.Parameters.AddWithValue(
                        "$observed",
                        (object?)request.ObservedPodStateIdentity ?? DBNull.Value
                    );
                    RequireOne(update.ExecuteNonQuery(), "store quarantine");
                    return new CharacterMemoryQuarantineResult(
                        CharacterMemoryQuarantineDisposition.Quarantined,
                        revision
                    );
                },
                result => {
                    CharacterMemoryStatusSnapshot status = ReadStatusSnapshot();
                    return status.StoreState is CharacterMemoryStoreState.Quarantined
                        && string.Equals(status.QuarantineCode,
                            request.QuarantineCode, StringComparison.Ordinal)
                        && string.Equals(status.QuarantineObservedPodStateIdentity,
                            request.ObservedPodStateIdentity, StringComparison.Ordinal)
                        && status.StoreRevision == result.StoreRevision;
                }
            );
        }
    }

    private T ExecuteWrite<T>(
        string operation,
        Func<SqliteConnection, SqliteTransaction, T> apply,
        Func<T, bool> isPublished
    ) {
        T result;
        Exception? uncertain = null;
        using (SqliteConnection connection = OpenVerifiedConnection())
        using (SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false)) {
            result = apply(connection, transaction);
            _hooks.BeforeCommit?.Invoke(operation);
            try {
                transaction.Commit();
                _hooks.AfterCommitBeforeReturn?.Invoke(operation);
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                uncertain = exception;
            }
        }
        if (uncertain is null) { return result; }
        if (isPublished(result)) { return result; }
        throw new CharacterMemoryStoreCommitOutcomeException(
            operation,
            uncertain
        );
    }

    private static CharacterMemoryStatusSnapshot RequireReady(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        CharacterMemoryStatusSnapshot status = ReadStatusCore(
            connection,
            transaction
        );
        if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
            throw new CharacterMemoryStoreQuarantinedException(
                status.QuarantineCode!
            );
        }
        if (status.StoreState is not CharacterMemoryStoreState.Ready) {
            throw new CharacterMemoryStoreConflictException(
                "Character Memory store is not provisioned."
            );
        }
        return status;
    }

    private static CharacterMemoryCaptureSnapshot RequireCapture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceActionAddress
    ) => ReadCaptureCore(connection, transaction, sourceActionAddress)
        ?? throw new CharacterMemoryStoreConflictException(
            "Character Note capture is absent."
        );

    private static void InsertCapture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CharacterMemoryCaptureRequest request,
        string commitment,
        CharacterMemoryCaptureState state,
        long revision
    ) {
        using (SqliteCommand command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO note_action_capture(
                    source_action_address, visible_action_sha256,
                    visible_action_utf8_bytes, extractor_contract_id,
                    extraction_commitment, artifact_count, state,
                    base_pod_state_identity, target_pod_state_identity,
                    rejection_code, state_revision
                ) VALUES (
                    $source, $hash, $bytes, $contract, $commitment,
                    $count, $state, NULL, NULL, NULL, $revision
                );
                """;
            command.Parameters.AddWithValue("$source", request.SourceActionAddress);
            command.Parameters.AddWithValue("$hash", request.VisibleActionSha256);
            command.Parameters.AddWithValue("$bytes", request.VisibleActionUtf8Bytes);
            command.Parameters.AddWithValue("$contract", request.ExtractorContractId);
            command.Parameters.AddWithValue("$commitment", commitment);
            command.Parameters.AddWithValue("$count", request.ExactTexts.Count);
            command.Parameters.AddWithValue("$state", state.ToString());
            command.Parameters.AddWithValue("$revision", revision);
            RequireOne(command.ExecuteNonQuery(), "Action capture");
        }
        for (int ordinal = 0; ordinal < request.ExactTexts.Count; ordinal++) {
            using SqliteCommand child = connection.CreateCommand();
            child.Transaction = transaction;
            child.CommandText = """
                INSERT INTO character_note(
                    source_action_address, artifact_ordinal, exact_text, memo_id
                ) VALUES ($source, $ordinal, $text, NULL);
                """;
            child.Parameters.AddWithValue("$source", request.SourceActionAddress);
            child.Parameters.AddWithValue("$ordinal", ordinal);
            child.Parameters.AddWithValue("$text", request.ExactTexts[ordinal]);
            RequireOne(child.ExecuteNonQuery(), "captured Character Note");
        }
    }

    private static void RequireCaptureIdentity(
        CharacterMemoryCaptureSnapshot existing,
        CharacterMemoryCaptureRequest request,
        string commitment
    ) {
        if (!string.Equals(existing.VisibleActionSha256,
                request.VisibleActionSha256, StringComparison.Ordinal)
            || existing.VisibleActionUtf8Bytes != request.VisibleActionUtf8Bytes
            || !string.Equals(existing.ExtractorContractId,
                request.ExtractorContractId, StringComparison.Ordinal)
            || !string.Equals(existing.ExtractionCommitment,
                commitment, StringComparison.Ordinal)) {
            throw new CharacterMemoryStoreConflictException(
                "Competing Character Note capture changed exact commitment."
            );
        }
    }

    private static void RequireExactPlan(
        CharacterMemoryCaptureSnapshot capture,
        CharacterMemoryPlanRequest request
    ) {
        if (!IsExactPlan(capture, request)) {
            throw new CharacterMemoryStoreConflictException(
                "Competing Character Note apply plan changed exact identity."
            );
        }
    }

    private static bool IsExactPlan(
        CharacterMemoryCaptureSnapshot capture,
        CharacterMemoryPlanRequest request
    ) => string.Equals(capture.ExtractionCommitment,
            request.ExtractionCommitment, StringComparison.Ordinal)
        && string.Equals(capture.BasePodStateIdentity,
            request.BasePodStateIdentity, StringComparison.Ordinal)
        && string.Equals(capture.TargetPodStateIdentity,
            request.TargetPodStateIdentity, StringComparison.Ordinal)
        && capture.Notes.Select(static note => note.MemoId)
            .SequenceEqual(request.MemoIds, StringComparer.Ordinal);

    private static void RequireCommitment(
        CharacterMemoryCaptureSnapshot capture,
        string expected
    ) {
        if (!string.Equals(capture.ExtractionCommitment, expected,
                StringComparison.Ordinal)) {
            throw new CharacterMemoryStoreConflictException(
                "Character Note extraction commitment changed."
            );
        }
    }

    private static long IncrementStoreRevision(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE character_memory_meta
            SET store_revision = store_revision + 1
            WHERE singleton = 1 RETURNING store_revision;
            """;
        return Convert.ToInt64(update.ExecuteScalar());
    }

    private static void SetActiveSource(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceActionAddress
    ) {
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE character_memory_meta SET active_source_action = $source
            WHERE singleton = 1 AND active_source_action IS NULL;
            """;
        update.Parameters.AddWithValue("$source", sourceActionAddress);
        RequireOne(update.ExecuteNonQuery(), "active capture binding");
    }

    private static void ClearActiveSource(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceActionAddress
    ) {
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE character_memory_meta SET active_source_action = NULL
            WHERE singleton = 1 AND active_source_action = $source;
            """;
        update.Parameters.AddWithValue("$source", sourceActionAddress);
        RequireOne(update.ExecuteNonQuery(), "active capture clearing");
    }

    private static void ValidateCaptureRequest(
        CharacterMemoryCaptureRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        RequireEventAddress(request.SourceActionAddress,
            nameof(request.SourceActionAddress));
        RequireSha256(request.VisibleActionSha256,
            nameof(request.VisibleActionSha256));
        if (request.VisibleActionUtf8Bytes is < 0
            or > TextExtractorBounds.MaximumTargetTextUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(request.VisibleActionUtf8Bytes)
            );
        }
        RequireBoundedText(request.ExtractorContractId,
            nameof(request.ExtractorContractId));
        ArgumentNullException.ThrowIfNull(request.ExactTexts);
        if (request.ExactTexts.Count > CharacterNoteBounds.MaximumIntentCount) {
            throw new ArgumentOutOfRangeException(nameof(request.ExactTexts));
        }
        int total = 0;
        foreach (string? exactText in request.ExactTexts) {
            total = checked(total + RequireExactText(
                exactText,
                nameof(request.ExactTexts)
            ));
            if (total > CharacterNoteBounds.MaximumTotalExactTextUtf8Bytes) {
                throw new ArgumentOutOfRangeException(nameof(request.ExactTexts));
            }
        }
    }

    private static void ValidatePlanRequest(CharacterMemoryPlanRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        RequireEventAddress(request.SourceActionAddress,
            nameof(request.SourceActionAddress));
        RequireSha256(request.ExtractionCommitment,
            nameof(request.ExtractionCommitment));
        RequirePodStateIdentity(request.BasePodStateIdentity,
            nameof(request.BasePodStateIdentity));
        RequirePodStateIdentity(request.TargetPodStateIdentity,
            nameof(request.TargetPodStateIdentity));
        if (string.Equals(request.BasePodStateIdentity,
                request.TargetPodStateIdentity, StringComparison.Ordinal)) {
            throw new ArgumentException("Plan target must differ from base.",
                nameof(request));
        }
        ArgumentNullException.ThrowIfNull(request.MemoIds);
        if (request.MemoIds.Count is < 1
            or > CharacterNoteBounds.MaximumIntentCount) {
            throw new ArgumentOutOfRangeException(nameof(request.MemoIds));
        }
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string memoId in request.MemoIds) {
            RequireMemoId(memoId, nameof(request.MemoIds));
            if (!unique.Add(memoId)) {
                throw new ArgumentException("Plan MemoIds must be unique.",
                    nameof(request.MemoIds));
            }
        }
    }

    private static void ValidateSettleRequest(
        CharacterMemorySettleRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        RequireEventAddress(request.SourceActionAddress,
            nameof(request.SourceActionAddress));
        RequireSha256(request.ExtractionCommitment,
            nameof(request.ExtractionCommitment));
        RequirePodStateIdentity(request.TargetPodStateIdentity,
            nameof(request.TargetPodStateIdentity));
    }

    private static void ValidateRejectRequest(
        CharacterMemoryRejectRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        RequireEventAddress(request.SourceActionAddress,
            nameof(request.SourceActionAddress));
        RequireSha256(request.ExtractionCommitment,
            nameof(request.ExtractionCommitment));
        RequireCode(request.RejectionCode, nameof(request.RejectionCode));
    }

    private static void ValidateQuarantineRequest(
        CharacterMemoryQuarantineRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedStoreRevision < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(request.ExpectedStoreRevision)
            );
        }
        RequireCode(request.QuarantineCode,
            nameof(request.QuarantineCode));
        if (request.ObservedPodStateIdentity is not null) {
            RequirePodStateIdentity(request.ObservedPodStateIdentity,
                nameof(request.ObservedPodStateIdentity));
        }
    }

    private static void RequireOne(int count, string operation) {
        if (count != 1) {
            throw Corrupt($"Character Memory {operation} affected {count} rows.");
        }
    }
}
