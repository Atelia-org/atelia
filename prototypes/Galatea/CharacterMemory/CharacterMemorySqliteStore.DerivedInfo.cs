using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Atelia.MemoPod;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterMemorySqliteStore {
    private const string DerivedInfoCommitmentVersion =
        "atelia.galatea.character-memory.derived-info-commitment.v1";

    internal CharacterMemoryDerivedInfoWorkSnapshot? ReadDerivedInfoWorkExact(
        string sourceActionAddress
    ) {
        RequireEventAddress(sourceActionAddress, nameof(sourceActionAddress));
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadDerivedInfoWorkCore(
                connection,
                transaction: null,
                sourceActionAddress
            );
        }
    }

    internal CharacterMemoryDerivedInfoWorkSnapshot? ReadNextDerivedInfoWork() {
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            _ = RequireReady(connection, transaction: null);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_action_address
                FROM derived_info_work
                WHERE state IN ('Planned', 'Prepared', 'Pending')
                ORDER BY CASE state
                    WHEN 'Planned' THEN 0
                    WHEN 'Prepared' THEN 1
                    ELSE 2
                END, created_revision, source_action_address
                LIMIT 1;
                """;
            string? source = command.ExecuteScalar() as string;
            return source is null
                ? null
                : ReadDerivedInfoWorkCore(
                    connection,
                    transaction: null,
                    source
                ) ?? throw Corrupt(
                    "Selected derived-info work is absent."
                );
        }
    }

    internal CharacterMemoryPrepareDerivedInfoResult PrepareDerivedInfo(
        CharacterMemoryPrepareDerivedInfoRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Values);
        CharacterMemoryDerivedInfoValue[] values = request.Values.ToArray();
        request = request with {
            Values = Array.AsReadOnly(values)
        };
        ValidatePrepareDerivedInfoRequest(request);
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite(
                "prepare-derived-info",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = RequireReady(
                        connection,
                        transaction
                    );
                    CharacterMemoryDerivedInfoWorkSnapshot work =
                        RequireDerivedInfoWork(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        );
                    RequireDerivedInfoExtractionIdentity(work, request.ExtractionCommitment);
                    string commitment = ComputeDerivedInfoCommitment(
                        work.SourceActionAddress,
                        work.ExtractionCommitment,
                        request.EnricherContractId,
                        values
                    );
                    if (work.State is CharacterMemoryDerivedInfoState.Prepared
                        or CharacterMemoryDerivedInfoState.Planned
                        or CharacterMemoryDerivedInfoState.Applied) {
                        RequireExactPreparedDerivedInfo(
                            work,
                            request.EnricherContractId,
                            commitment,
                            values
                        );
                        return new CharacterMemoryPrepareDerivedInfoResult(
                            CharacterMemoryPrepareDerivedInfoDisposition.AlreadyPrepared,
                            status.StoreRevision,
                            work
                        );
                    }
                    if (work.State is not CharacterMemoryDerivedInfoState.Pending) {
                        throw new CharacterMemoryStoreConflictException(
                            "Only Pending derived-info work can be prepared."
                        );
                    }
                    RequireValuesMatchWork(work, values);
                    long revision = IncrementStoreRevision(connection, transaction);
                    foreach (CharacterMemoryDerivedInfoValue value in values) {
                        using SqliteCommand note = connection.CreateCommand();
                        note.Transaction = transaction;
                        note.CommandText = """
                            UPDATE character_note
                            SET derived_title = $title,
                                derived_gist = $gist,
                                derived_summary = $summary
                            WHERE source_action_address = $source
                              AND artifact_ordinal = $ordinal
                              AND derived_title IS NULL
                              AND derived_gist IS NULL
                              AND derived_summary IS NULL;
                            """;
                        note.Parameters.AddWithValue("$title", value.Title);
                        note.Parameters.AddWithValue("$gist", value.Gist);
                        note.Parameters.AddWithValue("$summary", value.Summary);
                        note.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        note.Parameters.AddWithValue("$ordinal", value.ArtifactOrdinal);
                        RequireOne(note.ExecuteNonQuery(), "derived-info note preparation");
                    }
                    using (SqliteCommand update = connection.CreateCommand()) {
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE derived_info_work
                            SET state = 'Prepared',
                                enricher_contract_id = $contract,
                                derived_info_commitment = $commitment,
                                state_revision = $revision
                            WHERE source_action_address = $source
                              AND state = 'Pending';
                            """;
                        update.Parameters.AddWithValue("$contract", request.EnricherContractId);
                        update.Parameters.AddWithValue("$commitment", commitment);
                        update.Parameters.AddWithValue("$revision", revision);
                        update.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        RequireOne(update.ExecuteNonQuery(), "derived-info preparation");
                    }
                    return new CharacterMemoryPrepareDerivedInfoResult(
                        CharacterMemoryPrepareDerivedInfoDisposition.Prepared,
                        revision,
                        RequireDerivedInfoWork(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        )
                    );
                },
                result => IsExactDerivedInfoResultPublished(
                    request.SourceActionAddress,
                    result.StoreRevision,
                    work => work.State == result.Work.State
                        && string.Equals(
                            work.EnricherContractId,
                            result.Work.EnricherContractId,
                            StringComparison.Ordinal
                        )
                        && string.Equals(
                            work.DerivedInfoCommitment,
                            result.Work.DerivedInfoCommitment,
                            StringComparison.Ordinal
                        )
                )
            );
        }
    }

    internal CharacterMemoryPlanDerivedInfoResult PlanDerivedInfo(
        CharacterMemoryPlanDerivedInfoRequest request
    ) {
        ValidatePlanDerivedInfoRequest(request);
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite(
                "plan-derived-info",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = RequireReady(
                        connection,
                        transaction
                    );
                    CharacterMemoryDerivedInfoWorkSnapshot work =
                        RequireDerivedInfoWork(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        );
                    RequireExactDerivedInfoPlan(work, request);
                    if (work.State is CharacterMemoryDerivedInfoState.Applied) {
                        return new CharacterMemoryPlanDerivedInfoResult(
                            CharacterMemoryPlanDerivedInfoDisposition.AlreadyApplied,
                            status.StoreRevision,
                            work
                        );
                    }
                    if (work.State is CharacterMemoryDerivedInfoState.Planned) {
                        return new CharacterMemoryPlanDerivedInfoResult(
                            CharacterMemoryPlanDerivedInfoDisposition.AlreadyPlanned,
                            status.StoreRevision,
                            work
                        );
                    }
                    if (work.State is not CharacterMemoryDerivedInfoState.Prepared) {
                        throw new CharacterMemoryStoreConflictException(
                            "Only Prepared derived-info work can be planned."
                        );
                    }
                    if (status.ActiveSourceAction is not null
                        || status.ActiveDerivedInfoSourceAction is not null
                        || !string.Equals(
                            status.SettledDefaultPodStateIdentity,
                            request.BasePodStateIdentity,
                            StringComparison.Ordinal
                        )) {
                        throw new CharacterMemoryStoreConflictException(
                            "DerivedInfo plan base does not own the settled Default Pod mutation slot."
                        );
                    }
                    long revision = IncrementStoreRevision(connection, transaction);
                    using (SqliteCommand update = connection.CreateCommand()) {
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE derived_info_work
                            SET state = 'Planned',
                                base_pod_state_identity = $base,
                                target_pod_state_identity = $target,
                                state_revision = $revision
                            WHERE source_action_address = $source
                              AND state = 'Prepared';
                            """;
                        update.Parameters.AddWithValue("$base", request.BasePodStateIdentity);
                        update.Parameters.AddWithValue("$target", request.TargetPodStateIdentity);
                        update.Parameters.AddWithValue("$revision", revision);
                        update.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        RequireOne(update.ExecuteNonQuery(), "derived-info plan transition");
                    }
                    using (SqliteCommand meta = connection.CreateCommand()) {
                        meta.Transaction = transaction;
                        meta.CommandText = """
                            UPDATE character_memory_meta
                            SET active_derived_info_source_action = $source
                            WHERE singleton = 1
                              AND active_source_action IS NULL
                              AND active_derived_info_source_action IS NULL;
                            """;
                        meta.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        RequireOne(meta.ExecuteNonQuery(), "active derived-info binding");
                    }
                    return new CharacterMemoryPlanDerivedInfoResult(
                        CharacterMemoryPlanDerivedInfoDisposition.Planned,
                        revision,
                        RequireDerivedInfoWork(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        )
                    );
                },
                result => IsExactDerivedInfoResultPublished(
                    request.SourceActionAddress,
                    result.StoreRevision,
                    work => work.State == result.Work.State
                        && IsExactDerivedInfoPlan(work, request)
                )
            );
        }
    }

    internal CharacterMemorySettleDerivedInfoResult SettleDerivedInfoApplied(
        CharacterMemorySettleDerivedInfoRequest request
    ) {
        ValidateSettleDerivedInfoRequest(request);
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite(
                "settle-derived-info",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = RequireReady(
                        connection,
                        transaction
                    );
                    CharacterMemoryDerivedInfoWorkSnapshot work =
                        RequireDerivedInfoWork(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        );
                    RequireDerivedInfoExtractionIdentity(work, request.ExtractionCommitment);
                    RequireDerivedInfoCommitment(work, request.DerivedInfoCommitment);
                    if (!string.Equals(
                        work.TargetPodStateIdentity,
                        request.TargetPodStateIdentity,
                        StringComparison.Ordinal
                    )) {
                        throw new CharacterMemoryStoreConflictException(
                            "Applied DerivedInfo target identity changed."
                        );
                    }
                    if (work.State is CharacterMemoryDerivedInfoState.Applied) {
                        return new CharacterMemorySettleDerivedInfoResult(
                            CharacterMemorySettleDerivedInfoDisposition.AlreadyApplied,
                            status.StoreRevision,
                            work
                        );
                    }
                    if (work.State is not CharacterMemoryDerivedInfoState.Planned
                        || status.ActiveSourceAction is not null
                        || !string.Equals(
                            status.ActiveDerivedInfoSourceAction,
                            request.SourceActionAddress,
                            StringComparison.Ordinal
                        )
                        || !string.Equals(
                            status.SettledDefaultPodStateIdentity,
                            work.BasePodStateIdentity,
                            StringComparison.Ordinal
                        )) {
                        throw new CharacterMemoryStoreConflictException(
                            "Only the active DerivedInfo Planned batch can settle."
                        );
                    }
                    long revision = IncrementStoreRevision(connection, transaction);
                    using (SqliteCommand update = connection.CreateCommand()) {
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE derived_info_work
                            SET state = 'Applied', state_revision = $revision
                            WHERE source_action_address = $source
                              AND state = 'Planned';
                            """;
                        update.Parameters.AddWithValue("$revision", revision);
                        update.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        RequireOne(update.ExecuteNonQuery(), "derived-info settlement");
                    }
                    using (SqliteCommand meta = connection.CreateCommand()) {
                        meta.Transaction = transaction;
                        meta.CommandText = """
                            UPDATE character_memory_meta
                            SET settled_default_pod_state_identity = $target,
                                active_derived_info_source_action = NULL
                            WHERE singleton = 1
                              AND active_source_action IS NULL
                              AND active_derived_info_source_action = $source;
                            """;
                        meta.Parameters.AddWithValue("$target", request.TargetPodStateIdentity);
                        meta.Parameters.AddWithValue("$source", request.SourceActionAddress);
                        RequireOne(meta.ExecuteNonQuery(), "settled DerivedInfo Pod advancement");
                    }
                    return new CharacterMemorySettleDerivedInfoResult(
                        CharacterMemorySettleDerivedInfoDisposition.Applied,
                        revision,
                        RequireDerivedInfoWork(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        )
                    );
                },
                result => IsExactDerivedInfoResultPublished(
                    request.SourceActionAddress,
                    result.StoreRevision,
                    work => work.State == result.Work.State
                        && string.Equals(
                            work.TargetPodStateIdentity,
                            request.TargetPodStateIdentity,
                            StringComparison.Ordinal
                        )
                )
            );
        }
    }

    internal CharacterMemoryRejectDerivedInfoResult RejectDerivedInfo(
        CharacterMemoryRejectDerivedInfoRequest request
    ) {
        ValidateRejectDerivedInfoRequest(request);
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite(
                "reject-derived-info",
                (connection, transaction) => {
                    CharacterMemoryStatusSnapshot status = RequireReady(
                        connection,
                        transaction
                    );
                    CharacterMemoryDerivedInfoWorkSnapshot work =
                        RequireDerivedInfoWork(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        );
                    RequireDerivedInfoExtractionIdentity(work, request.ExtractionCommitment);
                    if (work.State is CharacterMemoryDerivedInfoState.Rejected) {
                        if (!string.Equals(
                            work.RejectionCode,
                            request.RejectionCode,
                            StringComparison.Ordinal
                        )) {
                            throw new CharacterMemoryStoreConflictException(
                                "Rejected DerivedInfo outcome changed code."
                            );
                        }
                        return new CharacterMemoryRejectDerivedInfoResult(
                            CharacterMemoryRejectDerivedInfoDisposition.AlreadyRejected,
                            status.StoreRevision,
                            work
                        );
                    }
                    if (work.State is not (
                        CharacterMemoryDerivedInfoState.Pending
                        or CharacterMemoryDerivedInfoState.Prepared
                    )) {
                        throw new CharacterMemoryStoreConflictException(
                            "Only Pending or Prepared derived-info work can be rejected."
                        );
                    }
                    long revision = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE derived_info_work
                        SET state = 'Rejected', rejection_code = $code,
                            state_revision = $revision
                        WHERE source_action_address = $source
                          AND state IN ('Pending', 'Prepared');
                        """;
                    update.Parameters.AddWithValue("$code", request.RejectionCode);
                    update.Parameters.AddWithValue("$revision", revision);
                    update.Parameters.AddWithValue("$source", request.SourceActionAddress);
                    RequireOne(update.ExecuteNonQuery(), "derived-info rejection");
                    return new CharacterMemoryRejectDerivedInfoResult(
                        CharacterMemoryRejectDerivedInfoDisposition.Rejected,
                        revision,
                        RequireDerivedInfoWork(
                            connection,
                            transaction,
                            request.SourceActionAddress
                        )
                    );
                },
                result => IsExactDerivedInfoResultPublished(
                    request.SourceActionAddress,
                    result.StoreRevision,
                    work => work.State is CharacterMemoryDerivedInfoState.Rejected
                        && string.Equals(
                            work.RejectionCode,
                            request.RejectionCode,
                            StringComparison.Ordinal
                        )
                )
            );
        }
    }

    private bool IsExactDerivedInfoResultPublished(
        string sourceActionAddress,
        long revision,
        Func<CharacterMemoryDerivedInfoWorkSnapshot, bool> predicate
    ) {
        CharacterMemoryDerivedInfoWorkSnapshot? work =
            ReadDerivedInfoWorkExact(sourceActionAddress);
        return work is not null
            && predicate(work)
            && ReadStatusSnapshot().StoreRevision == revision;
    }

    private static CharacterMemoryDerivedInfoWorkSnapshot RequireDerivedInfoWork(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceActionAddress
    ) => ReadDerivedInfoWorkCore(connection, transaction, sourceActionAddress)
        ?? throw new CharacterMemoryStoreConflictException(
            "Character Note derived-info work is absent."
        );

    private static CharacterMemoryDerivedInfoWorkSnapshot? ReadDerivedInfoWorkCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sourceActionAddress
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT capture.visible_action_sha256,
                   capture.visible_action_utf8_bytes,
                   capture.extractor_contract_id,
                   capture.extraction_commitment,
                   work.state, work.enricher_contract_id,
                   work.derived_info_commitment,
                   work.base_pod_state_identity,
                   work.target_pod_state_identity,
                   work.rejection_code, work.created_revision,
                   work.state_revision
            FROM derived_info_work AS work
            JOIN note_action_capture AS capture USING(source_action_address)
            WHERE work.source_action_address = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceActionAddress);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        string visibleActionSha256 = reader.GetString(0);
        int visibleActionUtf8Bytes = reader.GetInt32(1);
        string extractorContractId = reader.GetString(2);
        string extractionCommitment = reader.GetString(3);
        CharacterMemoryDerivedInfoState state = ParseDerivedInfoState(reader.GetString(4));
        string? enricherContractId = reader.IsDBNull(5) ? null : reader.GetString(5);
        string? derivedInfoCommitment = reader.IsDBNull(6) ? null : reader.GetString(6);
        string? baseIdentity = reader.IsDBNull(7) ? null : reader.GetString(7);
        string? targetIdentity = reader.IsDBNull(8) ? null : reader.GetString(8);
        string? rejectionCode = reader.IsDBNull(9) ? null : reader.GetString(9);
        long createdRevision = reader.GetInt64(10);
        long stateRevision = reader.GetInt64(11);
        if (reader.Read()) { throw Corrupt("Derived-info source is not unique."); }
        reader.Close();

        var notes = new List<CharacterMemoryDerivedInfoNoteSnapshot>();
        using SqliteCommand children = connection.CreateCommand();
        children.Transaction = transaction;
        children.CommandText = """
            SELECT artifact_ordinal, exact_text, memo_id,
                   derived_title, derived_gist, derived_summary
            FROM character_note
            WHERE source_action_address = $source
            ORDER BY artifact_ordinal;
            """;
        children.Parameters.AddWithValue("$source", sourceActionAddress);
        using SqliteDataReader noteReader = children.ExecuteReader();
        while (noteReader.Read()) {
            int ordinal = noteReader.GetInt32(0);
            if (ordinal != notes.Count) {
                throw Corrupt("Derived-info note ordinals are not contiguous.");
            }
            if (noteReader.IsDBNull(2)) {
                throw Corrupt("Derived-info work note has no MemoId.");
            }
            notes.Add(new CharacterMemoryDerivedInfoNoteSnapshot(
                ordinal,
                noteReader.GetString(1),
                noteReader.GetString(2),
                noteReader.IsDBNull(3) ? null : noteReader.GetString(3),
                noteReader.IsDBNull(4) ? null : noteReader.GetString(4),
                noteReader.IsDBNull(5) ? null : noteReader.GetString(5)
            ));
        }
        var snapshot = new CharacterMemoryDerivedInfoWorkSnapshot(
            sourceActionAddress,
            visibleActionSha256,
            visibleActionUtf8Bytes,
            extractorContractId,
            extractionCommitment,
            state,
            enricherContractId,
            derivedInfoCommitment,
            baseIdentity,
            targetIdentity,
            rejectionCode,
            createdRevision,
            stateRevision,
            Array.AsReadOnly(notes.ToArray())
        );
        ValidateDerivedInfoWorkSnapshot(snapshot);
        return snapshot;
    }

    private static void ValidateDerivedInfoWorkSnapshot(
        CharacterMemoryDerivedInfoWorkSnapshot work
    ) {
        RequireEventAddress(work.SourceActionAddress, "derived-info source");
        RequireSha256(work.VisibleActionSha256, "derived-info visible Action hash");
        if (work.VisibleActionUtf8Bytes is < 0
            or > TextExtractorBounds.MaximumTargetTextUtf8Bytes) {
            throw Corrupt("Derived-info visible Action byte count is invalid.");
        }
        RequireBoundedText(work.ExtractorContractId, "derived-info extractor contract");
        RequireSha256(work.ExtractionCommitment, "derived-info extraction commitment");
        if (work.Notes.Count is < 1 or > CharacterNoteBounds.MaximumIntentCount
            || work.CreatedRevision < 1
            || work.StateRevision < work.CreatedRevision) {
            throw Corrupt("Derived-info work revision or note count is invalid.");
        }
        int totalExactTextBytes = 0;
        foreach (CharacterMemoryDerivedInfoNoteSnapshot note in work.Notes) {
            totalExactTextBytes = checked(
                totalExactTextBytes
                    + RequireExactText(note.ExactText, "derived-info exact text")
            );
            RequireMemoId(note.MemoId, "derived-info MemoId");
        }
        if (totalExactTextBytes
                > CharacterNoteBounds.MaximumTotalExactTextUtf8Bytes) {
            throw Corrupt("Derived-info exact-text total is invalid.");
        }
        string extractionCommitment = ComputeExtractionCommitment(
            work.SourceActionAddress,
            work.VisibleActionSha256,
            work.VisibleActionUtf8Bytes,
            work.ExtractorContractId,
            work.Notes.Select(static note => note.ExactText).ToArray()
        );
        if (!string.Equals(
            extractionCommitment,
            work.ExtractionCommitment,
            StringComparison.Ordinal
        )) {
            throw Corrupt(
                "Derived-info source extraction commitment does not match rows."
            );
        }
        bool allFieldsNull = work.Notes.All(static note =>
            note.Title is null && note.Gist is null && note.Summary is null);
        bool allFieldsPresent = work.Notes.All(static note =>
            note.Title is not null && note.Gist is not null && note.Summary is not null);
        if (!allFieldsNull && !allFieldsPresent) {
            throw Corrupt("Derived-info fields are not an exact batch.");
        }
        if (allFieldsPresent) {
            foreach (CharacterMemoryDerivedInfoNoteSnapshot note in work.Notes) {
                RequireDerivedInfoText(note.Title!, MemoPodLimits.MaximumMemoTitleUtf8Bytes, "stored title");
                RequireDerivedInfoText(note.Gist!, MemoPodLimits.MaximumMemoGistUtf8Bytes, "stored gist");
                RequireDerivedInfoText(note.Summary!, MemoPodLimits.MaximumMemoSummaryUtf8Bytes, "stored summary");
            }
        }
        bool preparedIdentityPresent = work.EnricherContractId is not null
            && work.DerivedInfoCommitment is not null;
        bool planPresent = work.BasePodStateIdentity is not null
            && work.TargetPodStateIdentity is not null;
        switch (work.State) {
            case CharacterMemoryDerivedInfoState.Pending
                when allFieldsNull && !preparedIdentityPresent && !planPresent
                    && work.RejectionCode is null:
                break;
            case CharacterMemoryDerivedInfoState.Prepared
                when allFieldsPresent && preparedIdentityPresent && !planPresent
                    && work.RejectionCode is null:
                break;
            case CharacterMemoryDerivedInfoState.Planned
                or CharacterMemoryDerivedInfoState.Applied
                when allFieldsPresent && preparedIdentityPresent && planPresent
                    && work.RejectionCode is null:
                break;
            case CharacterMemoryDerivedInfoState.Rejected
                when (allFieldsNull || allFieldsPresent)
                    && preparedIdentityPresent == allFieldsPresent
                    && (!planPresent || preparedIdentityPresent)
                    && work.RejectionCode is not null:
                break;
            default:
                throw Corrupt("Derived-info work state shape is invalid.");
        }
        if (work.EnricherContractId is not null) {
            RequireBoundedText(work.EnricherContractId, "enricher contract");
            RequireSha256(work.DerivedInfoCommitment, "derived-info commitment");
            CharacterMemoryDerivedInfoValue[] values = work.Notes.Select(static note =>
                new CharacterMemoryDerivedInfoValue(
                    note.ArtifactOrdinal,
                    note.Title!,
                    note.Gist!,
                    note.Summary!
                )).ToArray();
            string recomputed = ComputeDerivedInfoCommitment(
                work.SourceActionAddress,
                work.ExtractionCommitment,
                work.EnricherContractId,
                values
            );
            if (!string.Equals(recomputed, work.DerivedInfoCommitment, StringComparison.Ordinal)) {
                throw Corrupt("Derived-info commitment does not match rows.");
            }
        }
        if (planPresent) {
            RequirePodStateIdentity(work.BasePodStateIdentity, "derived-info base identity");
            RequirePodStateIdentity(work.TargetPodStateIdentity, "derived-info target identity");
            if (string.Equals(work.BasePodStateIdentity, work.TargetPodStateIdentity, StringComparison.Ordinal)) {
                throw Corrupt("DerivedInfo Planned identities must differ.");
            }
        }
        if (work.RejectionCode is not null) {
            RequireCode(work.RejectionCode, "derived-info rejection code");
        }
    }

    internal static string ComputeDerivedInfoCommitment(
        string sourceActionAddress,
        string extractionCommitment,
        string enricherContractId,
        IReadOnlyList<CharacterMemoryDerivedInfoValue> values
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCommitmentPart(hash, DerivedInfoCommitmentVersion);
        AppendCommitmentPart(hash, sourceActionAddress);
        AppendCommitmentPart(hash, extractionCommitment);
        AppendCommitmentPart(hash, enricherContractId);
        Span<byte> count = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(count, values.Count);
        hash.AppendData(count);
        Span<byte> ordinal = stackalloc byte[sizeof(int)];
        foreach (CharacterMemoryDerivedInfoValue value in values) {
            BinaryPrimitives.WriteInt32BigEndian(ordinal, value.ArtifactOrdinal);
            hash.AppendData(ordinal);
            AppendCommitmentPart(hash, value.Title);
            AppendCommitmentPart(hash, value.Gist);
            AppendCommitmentPart(hash, value.Summary);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidatePrepareDerivedInfoRequest(
        CharacterMemoryPrepareDerivedInfoRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        RequireEventAddress(request.SourceActionAddress, nameof(request.SourceActionAddress));
        RequireSha256(request.ExtractionCommitment, nameof(request.ExtractionCommitment));
        RequireBoundedText(request.EnricherContractId, nameof(request.EnricherContractId));
        ArgumentNullException.ThrowIfNull(request.Values);
        if (request.Values.Count is < 1 or > CharacterNoteBounds.MaximumIntentCount) {
            throw new ArgumentOutOfRangeException(nameof(request.Values));
        }
        for (int ordinal = 0; ordinal < request.Values.Count; ordinal++) {
            CharacterMemoryDerivedInfoValue value = request.Values[ordinal]
                ?? throw new ArgumentException("DerivedInfo values must not contain null.", nameof(request.Values));
            if (value.ArtifactOrdinal != ordinal) {
                throw new ArgumentException("DerivedInfo values must preserve exact contiguous order.", nameof(request.Values));
            }
            RequireDerivedInfoText(value.Title, MemoPodLimits.MaximumMemoTitleUtf8Bytes, nameof(value.Title));
            RequireDerivedInfoText(value.Gist, MemoPodLimits.MaximumMemoGistUtf8Bytes, nameof(value.Gist));
            RequireDerivedInfoText(value.Summary, MemoPodLimits.MaximumMemoSummaryUtf8Bytes, nameof(value.Summary));
        }
    }

    private static void ValidatePlanDerivedInfoRequest(
        CharacterMemoryPlanDerivedInfoRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        RequireEventAddress(request.SourceActionAddress, nameof(request.SourceActionAddress));
        RequireSha256(request.ExtractionCommitment, nameof(request.ExtractionCommitment));
        RequireSha256(request.DerivedInfoCommitment, nameof(request.DerivedInfoCommitment));
        RequirePodStateIdentity(request.BasePodStateIdentity, nameof(request.BasePodStateIdentity));
        RequirePodStateIdentity(request.TargetPodStateIdentity, nameof(request.TargetPodStateIdentity));
        if (string.Equals(request.BasePodStateIdentity, request.TargetPodStateIdentity, StringComparison.Ordinal)) {
            throw new ArgumentException("DerivedInfo plan target must differ from base.", nameof(request));
        }
    }

    private static void ValidateSettleDerivedInfoRequest(
        CharacterMemorySettleDerivedInfoRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        RequireEventAddress(request.SourceActionAddress, nameof(request.SourceActionAddress));
        RequireSha256(request.ExtractionCommitment, nameof(request.ExtractionCommitment));
        RequireSha256(request.DerivedInfoCommitment, nameof(request.DerivedInfoCommitment));
        RequirePodStateIdentity(request.TargetPodStateIdentity, nameof(request.TargetPodStateIdentity));
    }

    private static void ValidateRejectDerivedInfoRequest(
        CharacterMemoryRejectDerivedInfoRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        RequireEventAddress(request.SourceActionAddress, nameof(request.SourceActionAddress));
        RequireSha256(request.ExtractionCommitment, nameof(request.ExtractionCommitment));
        RequireCode(request.RejectionCode, nameof(request.RejectionCode));
    }

    private static void RequireDerivedInfoExtractionIdentity(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        string extractionCommitment
    ) {
        if (!string.Equals(work.ExtractionCommitment, extractionCommitment, StringComparison.Ordinal)) {
            throw new CharacterMemoryStoreConflictException(
                "Character Note derived-info extraction identity changed."
            );
        }
    }

    private static void RequireDerivedInfoCommitment(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        string derivedInfoCommitment
    ) {
        if (!string.Equals(work.DerivedInfoCommitment, derivedInfoCommitment, StringComparison.Ordinal)) {
            throw new CharacterMemoryStoreConflictException(
                "Character Note derived-info commitment changed."
            );
        }
    }

    private static void RequireExactPreparedDerivedInfo(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        string enricherContractId,
        string commitment,
        IReadOnlyList<CharacterMemoryDerivedInfoValue> values
    ) {
        if (!string.Equals(work.EnricherContractId, enricherContractId, StringComparison.Ordinal)
            || !string.Equals(work.DerivedInfoCommitment, commitment, StringComparison.Ordinal)
            || work.Notes.Count != values.Count
            || !work.Notes.Zip(values).All(static pair =>
                pair.First.ArtifactOrdinal == pair.Second.ArtifactOrdinal
                && string.Equals(pair.First.Title, pair.Second.Title, StringComparison.Ordinal)
                && string.Equals(pair.First.Gist, pair.Second.Gist, StringComparison.Ordinal)
                && string.Equals(pair.First.Summary, pair.Second.Summary, StringComparison.Ordinal))) {
            throw new CharacterMemoryStoreConflictException(
                "Competing DerivedInfo preparation changed exact output."
            );
        }
    }

    private static void RequireValuesMatchWork(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        IReadOnlyList<CharacterMemoryDerivedInfoValue> values
    ) {
        if (work.Notes.Count != values.Count
            || !work.Notes.Select(static note => note.ArtifactOrdinal)
                .SequenceEqual(values.Select(static value => value.ArtifactOrdinal))) {
            throw new CharacterMemoryStoreConflictException(
                "DerivedInfo preparation does not exactly cover the source batch."
            );
        }
    }

    private static void RequireExactDerivedInfoPlan(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        CharacterMemoryPlanDerivedInfoRequest request
    ) {
        RequireDerivedInfoExtractionIdentity(work, request.ExtractionCommitment);
        RequireDerivedInfoCommitment(work, request.DerivedInfoCommitment);
        if ((work.State is CharacterMemoryDerivedInfoState.Planned
                or CharacterMemoryDerivedInfoState.Applied)
            && !IsExactDerivedInfoPlan(work, request)) {
            throw new CharacterMemoryStoreConflictException(
                "Competing DerivedInfo plan changed exact identity."
            );
        }
    }

    private static bool IsExactDerivedInfoPlan(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        CharacterMemoryPlanDerivedInfoRequest request
    ) => string.Equals(work.ExtractionCommitment, request.ExtractionCommitment, StringComparison.Ordinal)
        && string.Equals(work.DerivedInfoCommitment, request.DerivedInfoCommitment, StringComparison.Ordinal)
        && string.Equals(work.BasePodStateIdentity, request.BasePodStateIdentity, StringComparison.Ordinal)
        && string.Equals(work.TargetPodStateIdentity, request.TargetPodStateIdentity, StringComparison.Ordinal);

    private static void RequireDerivedInfoText(
        string? value,
        int maximumUtf8Bytes,
        string parameter
    ) {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl)) {
            throw new ArgumentException("DerivedInfo text is not canonical.", parameter);
        }
        try {
            if (StrictUtf8.GetByteCount(value) > maximumUtf8Bytes) {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException("DerivedInfo text must be strict Unicode.", parameter, exception);
        }
    }

    private static CharacterMemoryDerivedInfoState ParseDerivedInfoState(
        string value
    ) => value switch {
        "Pending" => CharacterMemoryDerivedInfoState.Pending,
        "Prepared" => CharacterMemoryDerivedInfoState.Prepared,
        "Planned" => CharacterMemoryDerivedInfoState.Planned,
        "Applied" => CharacterMemoryDerivedInfoState.Applied,
        "Rejected" => CharacterMemoryDerivedInfoState.Rejected,
        _ => throw Corrupt("Unknown Character Memory derived-info state."),
    };
}
