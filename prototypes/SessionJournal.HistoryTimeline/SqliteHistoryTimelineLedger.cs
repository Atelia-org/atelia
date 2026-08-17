using Microsoft.Data.Sqlite;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

internal sealed class SqliteHistoryTimelineLedger
    : IHistoryTimelineLedgerPort {
    internal const int SchemaVersion = 2;
    internal const string HeadHashDomain =
        "atelia.history-timeline.head.v1";
    internal const string VerifyRowsFirstPageSql = """
        SELECT
            row_id,
            previous_row_id,
            end_address,
            descriptor_digest,
            length(canonical),
            canonical
        FROM rows
        ORDER BY row_id
        LIMIT 128
        """;
    internal const string VerifyRowsNextPageSql = """
        SELECT
            row_id,
            previous_row_id,
            end_address,
            descriptor_digest,
            length(canonical),
            canonical
        FROM rows
        WHERE row_id > $after
        ORDER BY row_id
        LIMIT 128
        """;

    private readonly string _databasePath;
    private readonly TimelineId _timelineId;
    private readonly RefId _refId;
    private readonly HistoryTimelineStorageLimits _limits;
    private readonly HistoryTimelinePersistenceTestHooks _hooks;
    private readonly bool _readOnly;
    private readonly object _invalidGate = new();
    private string? _invalidCode;
    private string? _invalidDetail;

    internal SqliteHistoryTimelineLedger(
        string databasePath,
        TimelineId timelineId,
        RefId refId,
        HistoryTimelineStorageLimits limits,
        HistoryTimelinePersistenceTestHooks? hooks = null,
        bool readOnly = false
    ) {
        _databasePath = databasePath;
        _timelineId = timelineId;
        _refId = refId;
        _limits = limits;
        _hooks = hooks ?? HistoryTimelinePersistenceTestHooks.None;
        _readOnly = readOnly;
    }

    internal static void CreateNew(
        string databasePath,
        RefId refId,
        PartitionPolicyRevision initialPolicy,
        HistoryTimelineStorageLimits limits
    ) {
        ArgumentNullException.ThrowIfNull(initialPolicy);
        if (File.Exists(databasePath)) {
            throw new IOException(
                $"Timeline database already exists: {databasePath}"
            );
        }
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try {
            using SqliteConnection connection = OpenConnection(
                databasePath,
                create: true,
                limits
            );
            ConfigureCreatedDatabase(connection, limits);
            CreateSchema(connection);
            var head = new TimelineHeadRef(
                initialPolicy.TimelineId,
                refId,
                headRowId: null,
                initialPolicy.PolicyDigest,
                selectedRawHeadAtCommit: null,
                selectedPathCount: 0,
                HistorySelectedPathCommitment.EmptyDigest,
                generation: 0
            );
            byte[] policyBytes = initialPolicy.ToCanonicalBytes();
            byte[] headBytes = head.ToCanonicalBytes();
            string headDigest = ComputeHeadDigest(headBytes);
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: false);
            using (SqliteCommand policy = connection.CreateCommand()) {
                policy.Transaction = transaction;
                policy.CommandText = """
                    INSERT INTO policies(policy_digest, canonical)
                    VALUES ($digest, $canonical);
                    """;
                policy.Parameters.AddWithValue(
                    "$digest",
                    initialPolicy.PolicyDigest
                );
                policy.Parameters.AddWithValue(
                    "$canonical",
                    policyBytes
                );
                policy.ExecuteNonQuery();
            }
            using (SqliteCommand metadata = connection.CreateCommand()) {
                metadata.Transaction = transaction;
                metadata.CommandText = """
                    INSERT INTO store_metadata(
                        singleton,
                        schema_version,
                        timeline_id,
                        ref_id,
                        head_canonical,
                        head_sha256,
                        policy_count,
                        row_count
                    ) VALUES (
                        1, $schema, $timeline, $ref,
                        $head, $headDigest, 1, 0
                    );
                    """;
                metadata.Parameters.AddWithValue(
                    "$schema",
                    SchemaVersion
                );
                metadata.Parameters.AddWithValue(
                    "$timeline",
                    initialPolicy.TimelineId.Value
                );
                metadata.Parameters.AddWithValue(
                    "$ref",
                    refId.ToHexString()
                );
                metadata.Parameters.AddWithValue("$head", headBytes);
                metadata.Parameters.AddWithValue(
                    "$headDigest",
                    headDigest
                );
                metadata.ExecuteNonQuery();
            }
            transaction.Commit();
            RequireDatabaseBound(databasePath, limits);
        }
        catch {
            TryDeleteDatabaseArtifacts(databasePath);
            throw;
        }
    }

    internal HistoryTimelineStoreReadResult<TimelineHeadRef>
        VerifyAndReadHead() => ReadSnapshot();

    /// <summary>
    /// Reads only the strict SQLite identity and canonical whole head. Restore
    /// intentionally uses this narrower gate so a backup can repair damaged
    /// row/index content while still requiring an exact readable active head.
    /// </summary>
    internal HistoryTimelineStoreReadResult<TimelineHeadRef>
        ReadHeadForRestoreConfirmation() {
        if (TryReadInvalid<TimelineHeadRef>(out var invalid)) {
            return invalid;
        }
        try {
            RequireDatabaseBound(_databasePath, _limits);
            using SqliteConnection connection = OpenConnection(
                _databasePath,
                create: false,
                _limits,
                readOnly: true
            );
            ConfigureReadOnlyDatabase(connection, _limits);
            ValidateCoreSchemaIdentity(connection);
            TimelineHeadRef head = ReadHead(connection, transaction: null);
            return new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .Found(head);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .Busy();
        }
        catch (HistoryTimelineUnsupportedSchemaException exception) {
            return new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .UnsupportedSchema(exception.SchemaVersion);
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            return LatchInvalid<TimelineHeadRef>(exception);
        }
    }

    internal HistoryTimelineStoreReadResult<TimelineHeadRef>
        VerifyFully() {
        if (TryReadInvalid<TimelineHeadRef>(out var invalid)) {
            return invalid;
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: true);
            using (SqliteCommand check = connection.CreateCommand()) {
                check.Transaction = transaction;
                check.CommandText = "PRAGMA integrity_check;";
                object? result = check.ExecuteScalar();
                if (!string.Equals(
                        result as string,
                        "ok",
                        StringComparison.Ordinal)) {
                    throw new InvalidDataException(
                        "Timeline SQLite integrity_check failed."
                    );
                }
            }
            using (SqliteCommand foreignKeys = connection.CreateCommand()) {
                foreignKeys.Transaction = transaction;
                foreignKeys.CommandText = "PRAGMA foreign_key_check;";
                using SqliteDataReader violations =
                    foreignKeys.ExecuteReader();
                if (violations.Read()) {
                    throw new InvalidDataException(
                        "Timeline SQLite foreign_key_check failed."
                    );
                }
            }
            TimelineHeadRef head = ReadHead(connection, transaction);
            VerifyCanonicalPolicies(connection, transaction);
            VerifyCanonicalRows(connection, transaction);
            VerifyCurrentSelectedPath(connection, transaction, head);
            VerifyPhysicalCounts(connection, transaction);
            PartitionPolicyRevision? policy = ReadPolicyCore(
                connection,
                transaction,
                head.ActivePartitionPolicyDigest
            );
            if (policy is null) {
                throw new InvalidDataException(
                    "The active Timeline policy is missing."
                );
            }
            if (head.HeadRowId is { } rowId) {
                if (!IsCurrentSelectedRow(
                        connection, transaction, rowId)) {
                    throw new InvalidDataException(
                        "The selected head is absent from its path index."
                    );
                }
                _ = ReadRowCore(connection, transaction, rowId)
                    ?? throw new InvalidDataException(
                        "The selected head row is missing."
                    );
            }
            transaction.Commit();
            return new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .Found(head);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .Busy();
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            return LatchInvalid<TimelineHeadRef>(exception);
        }
    }

    internal void BackupTo(string destinationPath) {
        if (File.Exists(destinationPath)) {
            throw new IOException(
                "Timeline backup destination already exists."
            );
        }
        using SqliteConnection source = OpenVerifiedConnection();
        var builder = new SqliteConnectionStringBuilder {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = checked((int)Math.Ceiling(
                _limits.BusyTimeoutMilliseconds / 1000d
            ))
        };
        using var destination = new SqliteConnection(
            builder.ToString()
        );
        destination.Open();
        source.BackupDatabase(destination);
        destination.Close();
        RequireDatabaseBound(destinationPath, _limits);
    }

    public HistoryTimelineStoreReadResult<TimelineHeadRef>
        ReadSnapshot() {
        if (TryReadInvalid<TimelineHeadRef>(out var invalid)) {
            return invalid;
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            TimelineHeadRef head = ReadHead(connection, transaction: null);
            return new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .Found(head);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .Busy();
        }
        catch (HistoryTimelineUnsupportedSchemaException exception) {
            return new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .UnsupportedSchema(exception.SchemaVersion);
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            return LatchInvalid<TimelineHeadRef>(exception);
        }
    }

    public HistoryTimelineStoreReadResult<PartitionPolicyRevision>
        ReadPolicy(string policyDigest) {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyDigest);
        if (TryReadInvalid<PartitionPolicyRevision>(out var invalid)) {
            return invalid;
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            PartitionPolicyRevision? policy = ReadPolicyCore(
                connection,
                transaction: null,
                policyDigest
            );
            return policy is null
                ? new HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Absent()
                : new HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Found(policy);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Busy();
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            return LatchInvalid<PartitionPolicyRevision>(exception);
        }
    }

    public HistoryTimelineStoreReadResult<HistorySegmentDescriptor>
        ReadRow(HistoryRowId rowId) {
        if (TryReadInvalid<HistorySegmentDescriptor>(out var invalid)) {
            return invalid;
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            HistorySegmentDescriptor? row = ReadRowCore(
                connection,
                transaction: null,
                rowId
            );
            return row is null
                ? new HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Absent()
                : new HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Found(row);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.Busy();
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            return LatchInvalid<HistorySegmentDescriptor>(exception);
        }
    }

    public HistoryTimelinePolicyPutResult PutPolicy(
        PartitionPolicyRevision policy
    ) {
        ArgumentNullException.ThrowIfNull(policy);
        if (TryWriteInvalid(out string? code, out string? detail)) {
            return new HistoryTimelinePolicyPutResult.Invalid(
                code,
                detail
            );
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: false);
            RequireSelectedPathGuardClean(connection, transaction);
            TimelineHeadRef head = ReadHead(connection, transaction);
            if (policy.TimelineId != head.TimelineId) {
                return new HistoryTimelinePolicyPutResult.Invalid(
                    "TimelineMismatch",
                    "A partition policy belongs to another Timeline."
                );
            }
            byte[] canonical = policy.ToCanonicalBytes();
            byte[]? existing = ReadCanonicalByKey(
                connection,
                transaction,
                "policies",
                "policy_digest",
                policy.PolicyDigest,
                HistoryTimelineCanonicalCodec.MaximumPolicyUtf8Bytes
            );
            if (existing is not null) {
                return existing.AsSpan().SequenceEqual(canonical)
                    ? new HistoryTimelinePolicyPutResult
                        .AlreadyPresent()
                    : new HistoryTimelinePolicyPutResult.Invalid(
                        "PolicyDigestCollision",
                        "The policy digest is already bound to different canonical bytes."
                    );
            }
            StoreCounts counts = ReadCounts(connection, transaction);
            using (SqliteCommand insert = connection.CreateCommand()) {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO policies(policy_digest, canonical)
                    VALUES ($digest, $canonical);
                    """;
                insert.Parameters.AddWithValue(
                    "$digest",
                    policy.PolicyDigest
                );
                insert.Parameters.AddWithValue(
                    "$canonical",
                    canonical
                );
                insert.ExecuteNonQuery();
            }
            UpdateCounts(
                connection,
                transaction,
                counts with {
                    PolicyCount = checked(counts.PolicyCount + 1)
                }
            );
            _hooks.BeforePutPolicyCommit?.Invoke();
            transaction.Commit();
            _hooks.AfterPutPolicyCommit?.Invoke();
            return new HistoryTimelinePolicyPutResult.Stored();
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelinePolicyPutResult.BackendBusy();
        }
        catch (SqliteException exception)
            when (IsFullOrTooBig(exception)) {
            return new HistoryTimelinePolicyPutResult.LimitExceeded(
                "SqliteFull"
            );
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            (string invalidCode, string invalidDetail) =
                LatchInvalid(exception);
            return new HistoryTimelinePolicyPutResult.Invalid(
                invalidCode,
                invalidDetail
            );
        }
    }

    public HistoryTimelinePolicyCasResult CompareExchangePolicy(
        TimelineHeadRef expectedWholeHead,
        string nextPolicyDigest
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextPolicyDigest);
        if (TryWriteInvalid(out string? code, out string? detail)) {
            return new HistoryTimelinePolicyCasResult.Invalid(code, detail);
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: false);
            RequireSelectedPathGuardClean(connection, transaction);
            TimelineHeadRef actual = ReadHead(connection, transaction);
            if (actual != expectedWholeHead) {
                return new HistoryTimelinePolicyCasResult
                    .StaleTimelineHead(actual);
            }
            PartitionPolicyRevision? policy = ReadPolicyCore(
                connection,
                transaction,
                nextPolicyDigest
            );
            if (policy is null || policy.TimelineId != _timelineId) {
                return new HistoryTimelinePolicyCasResult
                    .PartitionPolicyUnavailable(nextPolicyDigest);
            }
            TimelineHeadRef next = new(
                actual.TimelineId,
                actual.RefId,
                actual.HeadRowId,
                nextPolicyDigest,
                actual.SelectedRawHeadAtCommit,
                actual.SelectedPathCount,
                actual.SelectedPathDigest,
                checked(actual.Generation + 1)
            );
            WriteHead(connection, transaction, actual, next);
            _hooks.BeforePolicyCasCommit?.Invoke();
            transaction.Commit();
            _hooks.AfterPolicyCasCommit?.Invoke();
            return new HistoryTimelinePolicyCasResult.Applied(next);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelinePolicyCasResult.BackendBusy();
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            (string invalidCode, string invalidDetail) =
                LatchInvalid(exception);
            return new HistoryTimelinePolicyCasResult.Invalid(
                invalidCode,
                invalidDetail
            );
        }
    }

    public HistoryTimelineCommitResult CommitRow(
        HistoryRowCommitCandidate candidate
    ) {
        ArgumentNullException.ThrowIfNull(candidate);
        if (TryWriteInvalid(out string? code, out string? detail)) {
            return new HistoryTimelineCommitResult.Invalid(code, detail);
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: false);
            _hooks.AfterAppendWriterLockAcquired?.Invoke();
            RequireSelectedPathGuardClean(connection, transaction);
            HistoryRowProposal proposal = candidate.Proposal;
            TimelineHeadRef actual = ReadHead(connection, transaction);
            RequireCurrentSelectedHead(
                connection,
                transaction,
                actual
            );
            if (!candidate.ReserveProof.IsExactFor(
                    proposal,
                    candidate.RawFence)) {
                return new HistoryTimelineCommitResult.Invalid(
                    "RecentReserveProofInvalid",
                    "The commit candidate has no active exact recent-reserve proof.");
            }
            if (actual != proposal.ExpectedHead) {
                return new HistoryTimelineCommitResult
                    .StaleTimelineHead(actual);
            }
            HistorySegmentDescriptor descriptor = proposal.Descriptor;
            if (candidate.RawFence.RefId != actual.RefId
                || candidate.RawFence.CapturedHead
                    != proposal.CapturedSelectedRawHead
                || descriptor.TimelineId != actual.TimelineId
                || descriptor.RefId != actual.RefId
                || descriptor.PreviousRowId != actual.HeadRowId) {
                return new HistoryTimelineCommitResult.Invalid(
                    "CommitCandidateMismatch",
                    "The opaque candidate does not extend the exact ledger/raw scope."
                );
            }
            PartitionPolicyRevision? policy = ReadPolicyCore(
                connection,
                transaction,
                descriptor.PartitionPolicyDigestAtCreation
            );
            if (policy is null || policy.TimelineId != actual.TimelineId) {
                return new HistoryTimelineCommitResult
                    .PartitionPolicyUnavailable(
                        descriptor.PartitionPolicyDigestAtCreation
                    );
            }
            if (!string.Equals(
                    actual.ActivePartitionPolicyDigest,
                    descriptor.PartitionPolicyDigestAtCreation,
                    StringComparison.Ordinal)) {
                return new HistoryTimelineCommitResult
                    .StaleTimelineHead(actual);
            }

            EventAddress? observedRawHead =
                candidate.RawFence.ReadCurrentHead();
            if (observedRawHead
                != proposal.CapturedSelectedRawHead) {
                return new HistoryTimelineCommitResult.RawHeadChanged(
                    proposal.CapturedSelectedRawHead,
                    observedRawHead
                );
            }

            StoreCounts counts = ReadCounts(connection, transaction);
            byte[] descriptorBytes = proposal.CanonicalDescriptorBytes
                .ToArray();
            byte[]? existingRow = ReadCanonicalByKey(
                connection,
                transaction,
                "rows",
                "row_id",
                descriptor.RowId.Value,
                HistoryTimelineCanonicalCodec.MaximumDescriptorUtf8Bytes
            );
            if (existingRow is not null
                && !existingRow.AsSpan().SequenceEqual(descriptorBytes)) {
                return new HistoryTimelineCommitResult.Invalid(
                    "RowIdCollision",
                    "The row ID is already bound to different canonical bytes."
                );
            }
            CurrentSelectedTail current = ReadCurrentSelectedTail(
                connection,
                transaction
            );
            if (current.RowId != actual.HeadRowId) {
                return new HistoryTimelineCommitResult.Invalid(
                    "SelectedPathHeadMismatch",
                    "The current selected path does not match the whole head."
                );
            }
            if (IsCurrentSelectedRow(
                    connection, transaction, descriptor.RowId)
                || IsCurrentSelectedBoundary(
                    connection, transaction, descriptor.EndInclusive)) {
                return new HistoryTimelineCommitResult.Invalid(
                    "SelectedBoundaryCollision",
                    "The exact predecessor snapshot already contains the proposed row or raw boundary."
                );
            }
            if (existingRow is null) {
                InsertRow(
                    connection,
                    transaction,
                    descriptor,
                    descriptorBytes
                );
            }
            InsertCurrentSelectedRow(
                connection,
                transaction,
                checked(current.Ordinal + 1),
                descriptor.RowId,
                descriptor.PreviousRowId,
                descriptor.EndInclusive
            );

            TimelineHeadRef next = new(
                actual.TimelineId,
                actual.RefId,
                descriptor.RowId,
                actual.ActivePartitionPolicyDigest,
                proposal.CapturedSelectedRawHead,
                checked(current.Ordinal + 2),
                ReadCurrentSelectedRoot(
                    connection,
                    transaction,
                    checked(current.Ordinal + 2)),
                checked(actual.Generation + 1)
            );
            WriteHead(connection, transaction, actual, next);
            ClearSelectedPathGuard(connection, transaction);
            UpdateCounts(
                connection,
                transaction,
                counts with {
                    RowCount = existingRow is null
                        ? checked(counts.RowCount + 1)
                        : counts.RowCount,
                }
            );
            _hooks.BeforeAppendCommit?.Invoke();
            transaction.Commit();
            _hooks.AfterAppendCommit?.Invoke();
            return new HistoryTimelineCommitResult.Committed(next);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelineCommitResult.BackendBusy();
        }
        catch (SqliteException exception)
            when (IsFullOrTooBig(exception)) {
            return new HistoryTimelineCommitResult.LimitExceeded(
                "SqliteFull"
            );
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            (string invalidCode, string invalidDetail) =
                LatchInvalid(exception);
            return new HistoryTimelineCommitResult.Invalid(
                invalidCode,
                invalidDetail
            );
        }
    }

    public SelectedHistoryRowResult ReadSelectedRow(
        TimelineHeadRef expectedWholeHead,
        HistoryRowId rowId
    ) {
        if (TryWriteInvalid(out string? code, out string? detail)) {
            return new SelectedHistoryRowResult.Invalid(code, detail);
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: true);
            TimelineHeadRef actual = ReadHead(connection, transaction);
            if (actual != expectedWholeHead) {
                return new SelectedHistoryRowResult
                    .StaleTimelineHead(actual);
            }
            RequireCurrentSelectedHead(
                connection, transaction, actual);
            CurrentSelectedAssignment? assignment =
                ReadCurrentSelectedAssignment(
                    connection, transaction, rowId);
            if (assignment is null) {
                return new SelectedHistoryRowResult
                    .NotOnSelectedPath(rowId);
            }
            HistorySegmentDescriptor descriptor = ReadRowCore(
                connection,
                transaction,
                rowId
            ) ?? throw new InvalidDataException(
                "The selected path references a missing row."
            );
            RequireSelectedAssignment(
                connection, transaction, actual, assignment, descriptor);
            transaction.Commit();
            return new SelectedHistoryRowResult.Selected(descriptor);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new SelectedHistoryRowResult.BackendBusy();
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            (string invalidCode, string invalidDetail) =
                LatchInvalid(exception);
            return new SelectedHistoryRowResult.Invalid(
                invalidCode,
                invalidDetail
            );
        }
    }

    public SelectedHistoryBoundaryResult ReadSelectedRowAtBoundary(
        TimelineHeadRef expectedWholeHead,
        EventAddress endInclusive
    ) {
        HistoryTimelineBoundaryProbeOpenResult opened =
            OpenBoundaryProbe(expectedWholeHead);
        if (opened is HistoryTimelineBoundaryProbeOpenResult
                .StaleTimelineHead stale) {
            return new SelectedHistoryBoundaryResult
                .StaleTimelineHead(stale.Actual);
        }
        if (opened is HistoryTimelineBoundaryProbeOpenResult.Busy) {
            return new SelectedHistoryBoundaryResult.BackendBusy();
        }
        if (opened is HistoryTimelineBoundaryProbeOpenResult
                .Invalid invalid) {
            return new SelectedHistoryBoundaryResult.Invalid(
                invalid.Code,
                invalid.Detail
            );
        }
        if (opened is not HistoryTimelineBoundaryProbeOpenResult
                .Opened available) {
            return new SelectedHistoryBoundaryResult.Invalid(
                "BoundaryProbeOpenOutcomeInvalid",
                "The ledger returned an unknown boundary-probe open outcome."
            );
        }
        using (available.Probe) {
            return available.Probe.Probe(endInclusive);
        }
    }

    public HistoryTimelineBoundaryProbeOpenResult OpenBoundaryProbe(
        TimelineHeadRef expectedWholeHead
    ) {
        if (TryWriteInvalid(out string? code, out string? detail)) {
            return new HistoryTimelineBoundaryProbeOpenResult.Invalid(
                code,
                detail
            );
        }
        SqliteConnection? connection = null;
        try {
            connection = OpenVerifiedConnection();
            TimelineHeadRef actual = ReadHead(
                connection,
                transaction: null
            );
            if (actual != expectedWholeHead) {
                return new HistoryTimelineBoundaryProbeOpenResult
                    .StaleTimelineHead(actual);
            }
            RequireCurrentSelectedHead(
                connection, transaction: null, actual);
            var probe = new SqliteBoundaryProbe(
                this,
                connection,
                actual,
                _hooks.BeforeBoundaryProbeLookupQuery
            );
            connection = null;
            _hooks.AfterBoundaryProbeOpened?.Invoke();
            return new HistoryTimelineBoundaryProbeOpenResult.Opened(
                probe
            );
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelineBoundaryProbeOpenResult.Busy();
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            (string invalidCode, string invalidDetail) =
                LatchInvalid(exception);
            return new HistoryTimelineBoundaryProbeOpenResult.Invalid(
                invalidCode,
                invalidDetail
            );
        }
        finally {
            connection?.Dispose();
        }
    }

    public HistoryTimelineReconcileResult ReconcileSelectedPath(
        HistoryTimelineReconcileCandidate candidate
    ) {
        ArgumentNullException.ThrowIfNull(candidate);
        if (TryWriteInvalid(out string? code, out string? detail)) {
            return new HistoryTimelineReconcileResult.Invalid(code, detail);
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: false);
            RequireSelectedPathGuardClean(connection, transaction);
            TimelineHeadRef actual = ReadHead(connection, transaction);
            if (actual != candidate.ExpectedHead) {
                return new HistoryTimelineReconcileResult
                    .StaleTimelineHead(actual);
            }
            if (candidate.RawFence.RefId != actual.RefId) {
                return new HistoryTimelineReconcileResult.Invalid(
                    "ReconcileScopeMismatch",
                    "The reconciliation candidate belongs to another raw Ref."
                );
            }
            RequireCurrentSelectedHead(
                connection, transaction, actual);
            long targetOrdinal = -1;
            if (candidate.SelectedRowId is { } selectedRowId) {
                CurrentSelectedAssignment? assignment =
                    ReadCurrentSelectedAssignment(
                        connection, transaction, selectedRowId);
                if (assignment is null) {
                    return new HistoryTimelineReconcileResult.Invalid(
                        "ReconcileTargetNotSelected",
                        "The reconciliation target is not on the exact expected selected path."
                    );
                }
                if (candidate.RawFence.CapturedHead is null) {
                    return new HistoryTimelineReconcileResult.Invalid(
                        "ReconcileRawHeadMissing",
                        "A non-empty target requires a captured raw head."
                    );
                }
                HistorySegmentDescriptor descriptor = ReadRowCore(
                    connection, transaction, selectedRowId)
                    ?? throw new InvalidDataException(
                        "The reconciliation target row is missing.");
                RequireSelectedAssignment(
                    connection,
                    transaction,
                    actual,
                    assignment,
                    descriptor);
                targetOrdinal = assignment.Ordinal;
            }
            EventAddress? observedRawHead =
                candidate.RawFence.ReadCurrentHead();
            if (observedRawHead != candidate.RawFence.CapturedHead) {
                return new HistoryTimelineReconcileResult.RawHeadChanged(
                    candidate.RawFence.CapturedHead,
                    observedRawHead
                );
            }
            EventAddress? selectedFence = candidate.SelectedRowId is null
                ? null
                : candidate.RawFence.CapturedHead;
            if (actual.HeadRowId == candidate.SelectedRowId
                && actual.SelectedRawHeadAtCommit == selectedFence) {
                return new HistoryTimelineReconcileResult.Unchanged(
                    actual
                );
            }
            DeleteCurrentSelectedSuffix(
                connection, transaction, targetOrdinal);
            TimelineHeadRef next = new(
                actual.TimelineId,
                actual.RefId,
                candidate.SelectedRowId,
                actual.ActivePartitionPolicyDigest,
                selectedFence,
                targetOrdinal < 0 ? 0 : checked(targetOrdinal + 1),
                ReadCurrentSelectedRoot(
                    connection,
                    transaction,
                    targetOrdinal < 0 ? 0 : checked(targetOrdinal + 1)),
                checked(actual.Generation + 1)
            );
            WriteHead(connection, transaction, actual, next);
            ClearSelectedPathGuard(connection, transaction);
            _hooks.BeforeReconcileCommit?.Invoke();
            transaction.Commit();
            _hooks.AfterReconcileCommit?.Invoke();
            return new HistoryTimelineReconcileResult.Reconciled(next);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelineReconcileResult.BackendBusy();
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            (string invalidCode, string invalidDetail) =
                LatchInvalid(exception);
            return new HistoryTimelineReconcileResult.Invalid(
                invalidCode,
                invalidDetail
            );
        }
    }

    public HistoryTimelineStorePathPageResult ReadSelectedPathPage(
        TimelineHeadRef expectedWholeHead,
        HistoryRowId? startAt,
        int maximumRows
    ) {
        if (TryWriteInvalid(out string? code, out string? detail)) {
            return new HistoryTimelineStorePathPageResult.Invalid(
                code,
                detail
            );
        }
        if (maximumRows is < 1
            || maximumRows > _limits.MaximumPathPageRows) {
            return new HistoryTimelineStorePathPageResult.Invalid(
                "PathPageLimitInvalid",
                "Path pages must use the code-owned row bound."
            );
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: true);
            TimelineHeadRef actual = ReadHead(connection, transaction);
            if (actual != expectedWholeHead) {
                return new HistoryTimelineStorePathPageResult
                    .StaleTimelineHead(actual);
            }
            RequireCurrentSelectedHead(
                connection, transaction, actual);
            HistoryRowId? cursor = startAt ?? actual.HeadRowId;
            if (cursor is { } requested
                && ReadCurrentSelectedAssignment(
                    connection, transaction, requested) is null) {
                return new HistoryTimelineStorePathPageResult.Invalid(
                    "PathCursorNotSelected",
                    "The path cursor is not on the exact selected path."
                );
            }
            var rows = new List<HistorySegmentDescriptor>(maximumRows);
            var proofNodes = new Dictionary<(int Level, long NodeIndex),
                string>();
            int bytes = 0;
            while (cursor is { } rowId
                && rows.Count < maximumRows) {
                HistorySegmentDescriptor descriptor = ReadRowCore(
                    connection,
                    transaction,
                    rowId
                ) ?? throw new InvalidDataException(
                    "The selected path references a missing row."
                );
                CurrentSelectedAssignment assignment =
                    ReadCurrentSelectedAssignment(
                        connection, transaction, rowId)
                    ?? throw new InvalidDataException(
                        "The descriptor chain is absent from the selected path.");
                RequireSelectedAssignment(
                    connection,
                    transaction,
                    actual,
                    assignment,
                    descriptor,
                    proofNodes,
                    verifyWholeRoot: false);
                bytes = checked(
                    bytes + descriptor.ToCanonicalBytes().Length
                );
                if (bytes > _limits.MaximumPathPageUtf8Bytes) {
                    return new HistoryTimelineStorePathPageResult.Invalid(
                        "PathPageByteLimitExceeded",
                        "The selected path page exceeds its byte bound."
                    );
                }
                rows.Add(descriptor);
                cursor = descriptor.PreviousRowId;
            }
            transaction.Commit();
            return new HistoryTimelineStorePathPageResult.Page(
                rows.AsReadOnly(),
                cursor
            );
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new HistoryTimelineStorePathPageResult.Busy();
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            (string invalidCode, string invalidDetail) =
                LatchInvalid(exception);
            return new HistoryTimelineStorePathPageResult.Invalid(
                invalidCode,
                invalidDetail
            );
        }
    }

    private SqliteConnection OpenVerifiedConnection() {
        RequireDatabaseBound(_databasePath, _limits);
        SqliteConnection connection = OpenConnection(
            _databasePath,
            create: false,
            _limits,
            _readOnly
        );
        try {
            if (_readOnly) {
                ConfigureReadOnlyDatabase(connection, _limits);
            }
            else {
                ConfigureOpenedDatabase(connection, _limits);
            }
            ValidateSchemaIdentity(connection);
            RequireSelectedPathGuardClean(
                connection,
                transaction: null
            );
            return connection;
        }
        catch {
            connection.Dispose();
            throw;
        }
    }

    private TimelineHeadRef ReadHead(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                schema_version,
                timeline_id,
                ref_id,
                length(head_canonical),
                head_canonical,
                head_sha256
            FROM store_metadata
            WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != SchemaVersion) {
            throw new InvalidDataException(
                "Timeline metadata is missing or has an unknown schema."
            );
        }
        if (!string.Equals(
                reader.GetString(1),
                _timelineId.Value,
                StringComparison.Ordinal)
            || !string.Equals(
                reader.GetString(2),
                _refId.ToHexString(),
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Timeline database scope does not match its locator."
            );
        }
        long length = reader.GetInt64(3);
        if (length is < 1
            or > HistoryTimelineStoreLimits.MaximumHeadUtf8Bytes) {
            throw new InvalidDataException(
                "Timeline head exceeds its canonical byte bound."
            );
        }
        byte[] canonical = reader.GetFieldValue<byte[]>(4);
        string storedDigest = reader.GetString(5);
        if (canonical.Length != length
            || !string.Equals(
                ComputeHeadDigest(canonical),
                storedDigest,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Timeline head digest mismatch."
            );
        }
        TimelineHeadRef head =
            HistoryTimelineCanonicalCodec.DecodeTimelineHead(canonical);
        if (head.TimelineId != _timelineId || head.RefId != _refId) {
            throw new InvalidDataException(
                "Timeline head scope does not match its database."
            );
        }
        return head;
    }

    private PartitionPolicyRevision? ReadPolicyCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string policyDigest
    ) {
        byte[]? canonical = ReadCanonicalByKey(
            connection,
            transaction,
            "policies",
            "policy_digest",
            policyDigest,
            HistoryTimelineCanonicalCodec.MaximumPolicyUtf8Bytes
        );
        if (canonical is null) {
            return null;
        }
        PartitionPolicyRevision policy =
            HistoryTimelineCanonicalCodec.DecodePartitionPolicy(canonical);
        if (policy.TimelineId != _timelineId
            || !string.Equals(
                policy.PolicyDigest,
                policyDigest,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Partition policy locator differs from its canonical bytes."
            );
        }
        return policy;
    }

    private HistorySegmentDescriptor? ReadRowCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryRowId rowId
    ) {
        byte[]? canonical = ReadCanonicalByKey(
            connection,
            transaction,
            "rows",
            "row_id",
            rowId.Value,
            HistoryTimelineCanonicalCodec.MaximumDescriptorUtf8Bytes
        );
        if (canonical is null) {
            return null;
        }
        return DecodeRowCanonical(rowId, canonical);
    }

    private HistorySegmentDescriptor DecodeRowCanonical(
        HistoryRowId rowId,
        byte[] canonical
    ) {
        HistorySegmentDescriptor descriptor =
            HistoryTimelineCanonicalCodec
                .DecodeHistorySegmentDescriptor(canonical);
        if (descriptor.RowId != rowId
            || descriptor.TimelineId != _timelineId
            || descriptor.RefId != _refId) {
            throw new InvalidDataException(
                "History row locator differs from its canonical bytes."
            );
        }
        return descriptor;
    }

    private static byte[]? ReadCanonicalByKey(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string keyColumn,
        string key,
        int maximumBytes
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT length(canonical), canonical
            FROM {table}
            WHERE {keyColumn} = $key;
            """;
        command.Parameters.AddWithValue("$key", key);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            return null;
        }
        long length = reader.GetInt64(0);
        if (length is < 1 || length > maximumBytes) {
            throw new InvalidDataException(
                $"Canonical {table} value exceeds its byte bound."
            );
        }
        byte[] canonical = reader.GetFieldValue<byte[]>(1);
        if (canonical.Length != length) {
            throw new InvalidDataException(
                $"Canonical {table} value length changed while reading."
            );
        }
        return canonical;
    }

    private static void InsertRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HistorySegmentDescriptor descriptor,
        byte[] canonical
    ) {
        byte[] endAddress = new byte[EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(descriptor.EndInclusive, endAddress);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rows(
                row_id,
                previous_row_id,
                end_address,
                descriptor_digest,
                canonical
            ) VALUES (
                $row,
                $previous,
                $end,
                $descriptorDigest,
                $canonical
            );
            """;
        command.Parameters.AddWithValue("$row", descriptor.RowId.Value);
        command.Parameters.AddWithValue(
            "$previous",
            descriptor.PreviousRowId is { } previous
                ? previous.Value
                : DBNull.Value
        );
        command.Parameters.AddWithValue("$end", endAddress);
        command.Parameters.AddWithValue(
            "$descriptorDigest",
            descriptor.DescriptorDigest.Value
        );
        command.Parameters.AddWithValue("$canonical", canonical);
        command.ExecuteNonQuery();
    }

    private static CurrentSelectedTail ReadCurrentSelectedTail(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ordinal, row_id
            FROM current_selected_path
            ORDER BY ordinal DESC
            LIMIT 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        return !reader.Read()
            ? new CurrentSelectedTail(-1, null)
            : new CurrentSelectedTail(
                reader.GetInt64(0),
                new HistoryRowId(reader.GetString(1))
            );
    }

    private static void RequireCurrentSelectedHead(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TimelineHeadRef expectedHead
    ) {
        RequireSelectedPathGuardClean(connection, transaction);
        CurrentSelectedTail tail = ReadCurrentSelectedTail(
            connection, transaction);
        long count = tail.Ordinal < 0 ? 0 : checked(tail.Ordinal + 1);
        string root = ReadCurrentSelectedRoot(
            connection, transaction, count);
        if (tail.RowId != expectedHead.HeadRowId
            || count != expectedHead.SelectedPathCount
            || !string.Equals(
                root,
                expectedHead.SelectedPathDigest,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The current selected path commitment does not match the whole head."
            );
        }
    }

    private static void RequireSelectedPathGuardClean(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT dirty
            FROM current_selected_path_guard
            WHERE singleton = 1;
            """;
        object? value = command.ExecuteScalar();
        if (value is not long dirty || dirty != 0) {
            throw new InvalidDataException(
                "The selected path was changed outside its whole-head transaction.");
        }
    }

    private static void ClearSelectedPathGuard(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE current_selected_path_guard
            SET dirty = 0
            WHERE singleton = 1;
            """;
        if (command.ExecuteNonQuery() != 1) {
            throw new InvalidDataException(
                "The selected-path mutation guard is unavailable.");
        }
    }

    private static bool IsCurrentSelectedRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryRowId rowId
    ) => ReadCurrentSelectedOrdinal(
        connection, transaction, rowId) is not null;

    private static long? ReadCurrentSelectedOrdinal(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryRowId rowId
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ordinal
            FROM current_selected_path
            WHERE row_id = $row;
            """;
        command.Parameters.AddWithValue("$row", rowId.Value);
        object? value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value);
    }

    private static CurrentSelectedAssignment?
        ReadCurrentSelectedAssignment(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryRowId rowId
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ordinal, previous_row_id, end_address, leaf_digest
            FROM current_selected_path
            WHERE row_id = $row;
            """;
        command.Parameters.AddWithValue("$row", rowId.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            return null;
        }
        return new CurrentSelectedAssignment(
            reader.GetInt64(0),
            rowId,
            reader.IsDBNull(1)
                ? null
                : new HistoryRowId(reader.GetString(1)),
            reader.GetFieldValue<byte[]>(2),
            HistoryTimelineSyntax.RequireSha256(
                reader.GetString(3),
                "leaf_digest"));
    }

    private static void RequireSelectedAssignment(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TimelineHeadRef expectedHead,
        CurrentSelectedAssignment assignment,
        HistorySegmentDescriptor descriptor,
        Dictionary<(int Level, long NodeIndex), string>? nodeCache = null,
        bool verifyWholeRoot = true
    ) {
        if (assignment.Ordinal < 0
            || assignment.Ordinal >= expectedHead.SelectedPathCount
            || assignment.RowId != descriptor.RowId
            || assignment.PreviousRowId != descriptor.PreviousRowId
            || assignment.EndAddress.Length
                != EventAddressCodec.EventAddressLength) {
            throw new InvalidDataException(
                "A selected-path assignment differs from its immutable row.");
        }
        byte[] expectedEnd = new byte[EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(descriptor.EndInclusive, expectedEnd);
        string leaf = HistorySelectedPathCommitment.ComputeLeaf(
            assignment.Ordinal,
            descriptor.RowId,
            descriptor.PreviousRowId,
            descriptor.EndInclusive);
        if (!assignment.EndAddress.AsSpan().SequenceEqual(expectedEnd)
            || !string.Equals(
                assignment.LeafDigest, leaf, StringComparison.Ordinal)
            || !string.Equals(
                ReadSelectedPathNode(
                    connection, transaction, 0, assignment.Ordinal,
                    nodeCache),
                leaf,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "A selected-path leaf commitment differs from its assignment.");
        }

        IReadOnlyList<(int Level, long NodeIndex)> peaks =
            HistorySelectedPathCommitment.PeakKeys(
                expectedHead.SelectedPathCount);
        (int peakLevel, long peakNode) = FindContainingPeak(
            peaks, assignment.Ordinal);
        string computed = leaf;
        long nodeIndex = assignment.Ordinal;
        for (int level = 0; level < peakLevel; level++) {
            long sibling = nodeIndex ^ 1;
            string siblingDigest = ReadSelectedPathNode(
                connection, transaction, level, sibling, nodeCache);
            computed = (nodeIndex & 1) == 0
                ? HistorySelectedPathCommitment.Combine(
                    level + 1, computed, siblingDigest)
                : HistorySelectedPathCommitment.Combine(
                    level + 1, siblingDigest, computed);
            nodeIndex >>= 1;
        }
        if (nodeIndex != peakNode
            || !string.Equals(
                computed,
                ReadSelectedPathNode(
                    connection,
                    transaction,
                    peakLevel,
                    peakNode,
                    nodeCache),
                StringComparison.Ordinal)
            || (verifyWholeRoot && !string.Equals(
                ReadCurrentSelectedRoot(
                    connection,
                    transaction,
                    expectedHead.SelectedPathCount),
                expectedHead.SelectedPathDigest,
                StringComparison.Ordinal))) {
            throw new InvalidDataException(
                "A selected-path inclusion proof differs from the whole head.");
        }
    }

    private static (int Level, long NodeIndex) FindContainingPeak(
        IReadOnlyList<(int Level, long NodeIndex)> peaks,
        long ordinal
    ) {
        foreach ((int level, long nodeIndex) in peaks) {
            long start = checked(nodeIndex << level);
            long end = checked(start + (1L << level));
            if (ordinal >= start && ordinal < end) {
                return (level, nodeIndex);
            }
        }
        throw new InvalidDataException(
            "A selected row is outside the committed path peaks.");
    }

    private static bool IsCurrentSelectedBoundary(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EventAddress endInclusive
    ) {
        byte[] encoded = new byte[EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(endInclusive, encoded);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM current_selected_path
            WHERE end_address = $end;
            """;
        command.Parameters.AddWithValue("$end", encoded);
        return command.ExecuteScalar() is not null;
    }

    private static void InsertCurrentSelectedRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ordinal,
        HistoryRowId rowId,
        HistoryRowId? previousRowId,
        EventAddress endInclusive
    ) {
        byte[] encoded = new byte[EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(endInclusive, encoded);
        string leafDigest = HistorySelectedPathCommitment.ComputeLeaf(
            ordinal, rowId, previousRowId, endInclusive);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO current_selected_path(
                ordinal, row_id, previous_row_id, end_address, leaf_digest
            ) VALUES ($ordinal, $row, $previous, $end, $leaf);
            """;
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$row", rowId.Value);
        command.Parameters.AddWithValue(
            "$previous",
            previousRowId is null
                ? DBNull.Value
                : previousRowId.Value.Value);
        command.Parameters.AddWithValue("$end", encoded);
        command.Parameters.AddWithValue("$leaf", leafDigest);
        command.ExecuteNonQuery();

        UpsertSelectedPathNode(
            connection, transaction, level: 0, ordinal, leafDigest);
        long nodeIndex = ordinal;
        string digest = leafDigest;
        for (int level = 0; (nodeIndex & 1) == 1; level++) {
            string left = ReadSelectedPathNode(
                connection, transaction, level, nodeIndex - 1);
            digest = HistorySelectedPathCommitment.Combine(
                level + 1, left, digest);
            nodeIndex >>= 1;
            UpsertSelectedPathNode(
                connection,
                transaction,
                level + 1,
                nodeIndex,
                digest);
        }
    }

    private static void DeleteCurrentSelectedSuffix(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long targetOrdinal
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM current_selected_path
            WHERE ordinal > $target;
            """;
        command.Parameters.AddWithValue("$target", targetOrdinal);
        command.ExecuteNonQuery();
        using var deleteNodes = connection.CreateCommand();
        deleteNodes.Transaction = transaction;
        deleteNodes.CommandText = """
            DELETE FROM current_selected_path_merkle
            WHERE range_start >= $count OR range_end >= $count;
            """;
        deleteNodes.Parameters.AddWithValue(
            "$count",
            targetOrdinal < 0 ? 0 : checked(targetOrdinal + 1));
        deleteNodes.ExecuteNonQuery();
    }

    private static void UpsertSelectedPathNode(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int level,
        long nodeIndex,
        string digest
    ) {
        long rangeStart = checked(nodeIndex << level);
        long rangeEnd = checked(rangeStart + (1L << level) - 1);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO current_selected_path_merkle(
                level, node_index, range_start, range_end, digest
            ) VALUES ($level, $node, $start, $end, $digest)
            ON CONFLICT(level, node_index) DO UPDATE SET
                range_start = excluded.range_start,
                range_end = excluded.range_end,
                digest = excluded.digest;
            """;
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$node", nodeIndex);
        command.Parameters.AddWithValue("$start", rangeStart);
        command.Parameters.AddWithValue("$end", rangeEnd);
        command.Parameters.AddWithValue("$digest", digest);
        command.ExecuteNonQuery();
    }

    private static string ReadSelectedPathNode(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int level,
        long nodeIndex,
        Dictionary<(int Level, long NodeIndex), string>? cache = null
    ) {
        if (cache is not null
            && cache.TryGetValue((level, nodeIndex), out string? cached)) {
            return cached;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT digest
            FROM current_selected_path_merkle
            WHERE level = $level AND node_index = $node;
            """;
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$node", nodeIndex);
        object? value = command.ExecuteScalar();
        if (value is not string digest) {
            throw new InvalidDataException(
                "A selected-path commitment node is missing.");
        }
        string validated = HistoryTimelineSyntax.RequireSha256(
            digest, nameof(digest));
        cache?.Add((level, nodeIndex), validated);
        return validated;
    }

    private static string ReadCurrentSelectedRoot(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long count
    ) {
        IReadOnlyList<(int Level, long NodeIndex)> keys =
            HistorySelectedPathCommitment.PeakKeys(count);
        var peaks = new List<HistorySelectedPathPeak>(keys.Count);
        foreach ((int level, long nodeIndex) in keys) {
            peaks.Add(new HistorySelectedPathPeak(
                level,
                nodeIndex,
                ReadSelectedPathNode(
                    connection, transaction, level, nodeIndex)));
        }
        return HistorySelectedPathCommitment.ComputeRoot(count, peaks);
    }

    private void VerifyCanonicalPolicies(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT policy_digest, length(canonical), canonical
            FROM policies;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            string digest = reader.GetString(0);
            long length = reader.GetInt64(1);
            byte[] canonical = reader.GetFieldValue<byte[]>(2);
            if (length is < 1
                or > HistoryTimelineCanonicalCodec.MaximumPolicyUtf8Bytes
                || canonical.Length != length) {
                throw new InvalidDataException(
                    "A partition policy exceeds its canonical byte bound."
                );
            }
            PartitionPolicyRevision policy =
                HistoryTimelineCanonicalCodec.DecodePartitionPolicy(
                    canonical
                );
            if (policy.TimelineId != _timelineId
                || !string.Equals(
                    policy.PolicyDigest,
                    digest,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "A partition policy locator differs from its canonical bytes."
                );
            }
        }
    }

    private void VerifyCanonicalRows(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        string? afterRowId = null;
        while (true) {
            var page = new List<CanonicalRowRecord>(128);
            using (var command = connection.CreateCommand()) {
                command.Transaction = transaction;
                command.CommandText = afterRowId is null
                    ? VerifyRowsFirstPageSql
                    : VerifyRowsNextPageSql;
                if (afterRowId is not null) {
                    command.Parameters.AddWithValue(
                        "$after",
                        afterRowId
                    );
                }
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) {
                    page.Add(new CanonicalRowRecord(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetFieldValue<byte[]>(2),
                        reader.GetString(3),
                        reader.GetInt64(4),
                        reader.GetFieldValue<byte[]>(5)
                    ));
                }
            }
            if (page.Count == 0) {
                return;
            }
            foreach (CanonicalRowRecord stored in page) {
                if (stored.CanonicalLength is < 1
                    or > HistoryTimelineCanonicalCodec
                        .MaximumDescriptorUtf8Bytes
                    || stored.Canonical.Length
                        != stored.CanonicalLength
                    || stored.EndAddress.Length
                        != EventAddressCodec.EventAddressLength) {
                    throw new InvalidDataException(
                        "A history row exceeds its canonical byte bound."
                    );
                }
                HistorySegmentDescriptor descriptor =
                    HistoryTimelineCanonicalCodec
                        .DecodeHistorySegmentDescriptor(
                            stored.Canonical
                        );
                byte[] expectedEnd = new byte[
                    EventAddressCodec.EventAddressLength
                ];
                EventAddressCodec.Encode(
                    descriptor.EndInclusive,
                    expectedEnd
                );
                if (descriptor.TimelineId != _timelineId
                    || descriptor.RefId != _refId
                    || !string.Equals(
                        descriptor.RowId.Value,
                        stored.RowId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        descriptor.PreviousRowId?.Value,
                        stored.PreviousRowId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        descriptor.DescriptorDigest.Value,
                        stored.DescriptorDigest,
                        StringComparison.Ordinal)
                    || !expectedEnd.AsSpan().SequenceEqual(
                        stored.EndAddress
                    )) {
                    throw new InvalidDataException(
                        "A history row index differs from its canonical descriptor."
                    );
                }
                if (ReadPolicyCore(
                        connection,
                        transaction,
                        descriptor.PartitionPolicyDigestAtCreation
                    ) is null) {
                    throw new InvalidDataException(
                        "A history row references a missing creation policy."
                    );
                }
                if (descriptor.PreviousRowId is { } previous
                    && !CanonicalKeyExists(
                        connection,
                        transaction,
                        "rows",
                        "row_id",
                        previous.Value
                    )) {
                    throw new InvalidDataException(
                        "A history row references a missing predecessor."
                    );
                }
            }
            afterRowId = page[^1].RowId;
        }
    }

    private void VerifyCurrentSelectedPath(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimelineHeadRef head
    ) {
        RequireSelectedPathGuardClean(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.ordinal, p.row_id, p.end_address, r.previous_row_id,
                   length(r.canonical), r.canonical
            FROM current_selected_path AS p
            JOIN rows AS r ON r.row_id = p.row_id
            ORDER BY p.ordinal;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        long expectedOrdinal = 0;
        HistoryRowId? previous = null;
        HistoryRowId? last = null;
        while (reader.Read()) {
            long ordinal = reader.GetInt64(0);
            var rowId = new HistoryRowId(reader.GetString(1));
            byte[] indexedEnd = reader.GetFieldValue<byte[]>(2);
            HistoryRowId? storedPrevious = reader.IsDBNull(3)
                ? null
                : new HistoryRowId(reader.GetString(3));
            long canonicalLength = reader.GetInt64(4);
            byte[] canonical = reader.GetFieldValue<byte[]>(5);
            if (ordinal != expectedOrdinal
                || storedPrevious != previous
                || canonicalLength is < 1
                    or > HistoryTimelineCanonicalCodec
                        .MaximumDescriptorUtf8Bytes
                || canonical.Length != canonicalLength
                || indexedEnd.Length
                    != EventAddressCodec.EventAddressLength) {
                throw new InvalidDataException(
                    "The current selected path is not a contiguous exact lineage."
                );
            }
            HistorySegmentDescriptor descriptor =
                DecodeRowCanonical(rowId, canonical);
            byte[] expectedEnd = new byte[
                EventAddressCodec.EventAddressLength];
            EventAddressCodec.Encode(descriptor.EndInclusive, expectedEnd);
            if (descriptor.PreviousRowId != previous
                || !expectedEnd.AsSpan().SequenceEqual(indexedEnd)) {
                throw new InvalidDataException(
                    "The current selected path index differs from its row."
                );
            }
            previous = rowId;
            last = rowId;
            expectedOrdinal = checked(expectedOrdinal + 1);
        }
        if (last != head.HeadRowId
            || expectedOrdinal != head.SelectedPathCount
            || !string.Equals(
                ReadCurrentSelectedRoot(
                    connection, transaction, expectedOrdinal),
                head.SelectedPathDigest,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The current selected path commitment differs from the whole head."
            );
        }
    }

    private static bool CanonicalKeyExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string keyColumn,
        string key
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT 1 FROM {table} WHERE {keyColumn} = $key;
            """;
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is not null;
    }

    private void WriteHead(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimelineHeadRef expected,
        TimelineHeadRef next
    ) {
        byte[] expectedBytes = expected.ToCanonicalBytes();
        byte[] nextBytes = next.ToCanonicalBytes();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE store_metadata
            SET head_canonical = $next,
                head_sha256 = $nextDigest
            WHERE singleton = 1
              AND head_canonical = $expected
              AND head_sha256 = $expectedDigest;
            """;
        command.Parameters.AddWithValue("$next", nextBytes);
        command.Parameters.AddWithValue(
            "$nextDigest",
            ComputeHeadDigest(nextBytes)
        );
        command.Parameters.AddWithValue("$expected", expectedBytes);
        command.Parameters.AddWithValue(
            "$expectedDigest",
            ComputeHeadDigest(expectedBytes)
        );
        if (command.ExecuteNonQuery() != 1) {
            throw new InvalidDataException(
                "Timeline whole-head CAS update did not affect one row."
            );
        }
    }

    private static StoreCounts ReadCounts(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT policy_count, row_count
            FROM store_metadata
            WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw new InvalidDataException(
                "Timeline store counters are unavailable."
            );
        }
        return new StoreCounts(
            reader.GetInt64(0),
            reader.GetInt64(1)
        );
    }

    private static void UpdateCounts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoreCounts counts
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE store_metadata
            SET policy_count = $policies,
                row_count = $rows
            WHERE singleton = 1;
            """;
        command.Parameters.AddWithValue(
            "$policies",
            counts.PolicyCount
        );
        command.Parameters.AddWithValue("$rows", counts.RowCount);
        if (command.ExecuteNonQuery() != 1) {
            throw new InvalidDataException(
                "Timeline store counter update failed."
            );
        }
    }

    private void ValidateSchemaIdentity(SqliteConnection connection) {
        ValidateCoreSchemaIdentity(connection);
        StoreCounts stored = ReadCounts(
            connection,
            transaction: null
        );
        if (stored.PolicyCount < 1 || stored.RowCount < 0) {
            throw new InvalidDataException(
                "Timeline SQLite counters exceed their code-owned bounds."
            );
        }
    }

    private static void VerifyPhysicalCounts(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM policies),
                (SELECT COUNT(*) FROM rows);
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw new InvalidDataException(
                "Timeline SQLite physical counts are unavailable."
            );
        }
        long policies = reader.GetInt64(0);
        long rows = reader.GetInt64(1);
        StoreCounts stored = ReadCounts(connection, transaction);
        if (stored.PolicyCount != policies
            || stored.RowCount != rows) {
            throw new InvalidDataException(
                "Timeline SQLite counters differ from canonical tables."
            );
        }
    }

    private static void ValidateCoreSchemaIdentity(
        SqliteConnection connection
    ) {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT user_version FROM pragma_user_version),
                (SELECT application_id FROM pragma_application_id),
                (SELECT COUNT(*) FROM store_metadata);
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw new InvalidDataException(
                "Timeline SQLite schema identity is invalid."
            );
        }
        int schemaVersion = reader.GetInt32(0);
        if (schemaVersion != SchemaVersion) {
            throw new HistoryTimelineUnsupportedSchemaException(
                schemaVersion
            );
        }
        if (reader.GetInt32(1) != ApplicationId
            || reader.GetInt32(2) != 1) {
            throw new InvalidDataException(
                "Timeline SQLite schema identity is invalid."
            );
        }
        reader.Close();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;
        using SqliteDataReader schemaReader = schema.ExecuteReader();
        SchemaEntry[] expectedEntries = [..
            SchemaEntriesInCreationOrder
                .OrderBy(
                    static entry => entry.Type,
                    StringComparer.Ordinal
                )
                .ThenBy(
                    static entry => entry.Name,
                    StringComparer.Ordinal
                )
        ];
        int index = 0;
        while (schemaReader.Read()) {
            if (index >= expectedEntries.Length) {
                throw new InvalidDataException(
                    "Timeline SQLite schema contains an unexpected object."
                );
            }
            SchemaEntry expected = expectedEntries[index++];
            if (!string.Equals(
                    schemaReader.GetString(0),
                    expected.Type,
                    StringComparison.Ordinal)
                || !string.Equals(
                    schemaReader.GetString(1),
                    expected.Name,
                    StringComparison.Ordinal)
                || !string.Equals(
                    schemaReader.GetString(2),
                    expected.TableName,
                    StringComparison.Ordinal)
                || schemaReader.IsDBNull(3)
                || !string.Equals(
                    schemaReader.GetString(3),
                    expected.Sql,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "Timeline SQLite schema shape differs from V2."
                );
            }
        }
        if (index != expectedEntries.Length) {
            throw new InvalidDataException(
                "Timeline SQLite schema is missing a required object."
            );
        }
    }

    private static void CreateSchema(SqliteConnection connection) {
        using SqliteCommand command = connection.CreateCommand();
        foreach (SchemaEntry entry in SchemaEntriesInCreationOrder) {
            if (!string.Equals(
                    entry.Type,
                    "table",
                    StringComparison.Ordinal)) {
                continue;
            }
            command.CommandText = entry.Sql;
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            INSERT INTO current_selected_path_guard(singleton, dirty)
            VALUES (1, 0);
            """;
        command.ExecuteNonQuery();
        foreach (SchemaEntry entry in SchemaEntriesInCreationOrder) {
            if (!string.Equals(
                    entry.Type,
                    "trigger",
                    StringComparison.Ordinal)) {
                continue;
            }
            command.CommandText = entry.Sql;
            command.ExecuteNonQuery();
        }
    }

    private static SqliteConnection OpenConnection(
        string path,
        bool create,
        HistoryTimelineStorageLimits limits,
        bool readOnly = false
    ) {
        if (!create && !File.Exists(path)) {
            throw new FileNotFoundException(
                "Timeline database is absent.",
                path
            );
        }
        var builder = new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = readOnly
                ? SqliteOpenMode.ReadOnly
                : create
                ? SqliteOpenMode.ReadWriteCreate
                : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = checked((int)Math.Ceiling(
                limits.BusyTimeoutMilliseconds / 1000d
            ))
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void ConfigureCreatedDatabase(
        SqliteConnection connection,
        HistoryTimelineStorageLimits limits
    ) {
        ExecuteScalarPragma(connection, "PRAGMA page_size = 4096;");
        ConfigureOpenedDatabase(connection, limits);
        ExecuteScalarPragma(
            connection,
            $"PRAGMA application_id = {ApplicationId};"
        );
        ExecuteScalarPragma(
            connection,
            $"PRAGMA user_version = {SchemaVersion};"
        );
    }

    private static void ConfigureOpenedDatabase(
        SqliteConnection connection,
        HistoryTimelineStorageLimits limits
    ) {
        ExecuteScalarPragma(connection, "PRAGMA foreign_keys = ON;");
        ExecuteScalarPragma(connection, "PRAGMA journal_mode = DELETE;");
        ExecuteScalarPragma(connection, "PRAGMA synchronous = EXTRA;");
        ExecuteScalarPragma(
            connection,
            $"PRAGMA busy_timeout = {limits.BusyTimeoutMilliseconds};"
        );
        ExecuteScalarPragma(connection, "PRAGMA temp_store = MEMORY;");
        const long maximumPages = 4_294_967_294L;
        ExecuteScalarPragma(
            connection,
            $"PRAGMA max_page_count = {maximumPages};"
        );
        RequirePragmaInteger(connection, "page_size", 4096);
        RequirePragmaText(connection, "journal_mode", "delete");
        RequirePragmaInteger(connection, "synchronous", 3);
        RequirePragmaInteger(connection, "foreign_keys", 1);
        RequirePragmaInteger(
            connection,
            "busy_timeout",
            limits.BusyTimeoutMilliseconds
        );
        RequirePragmaInteger(connection, "temp_store", 2);
        RequirePragmaInteger(
            connection,
            "max_page_count",
            maximumPages
        );
    }

    private static void ConfigureReadOnlyDatabase(
        SqliteConnection connection,
        HistoryTimelineStorageLimits limits
    ) {
        ExecuteScalarPragma(connection, "PRAGMA foreign_keys = ON;");
        ExecuteScalarPragma(connection, "PRAGMA synchronous = EXTRA;");
        ExecuteScalarPragma(
            connection,
            $"PRAGMA busy_timeout = {limits.BusyTimeoutMilliseconds};"
        );
        ExecuteScalarPragma(connection, "PRAGMA temp_store = MEMORY;");
        ExecuteScalarPragma(connection, "PRAGMA query_only = ON;");
        RequirePragmaInteger(connection, "page_size", 4096);
        RequirePragmaText(connection, "journal_mode", "delete");
        RequirePragmaInteger(connection, "synchronous", 3);
        RequirePragmaInteger(connection, "foreign_keys", 1);
        RequirePragmaInteger(
            connection,
            "busy_timeout",
            limits.BusyTimeoutMilliseconds
        );
        RequirePragmaInteger(connection, "temp_store", 2);
        RequirePragmaInteger(connection, "query_only", 1);
    }

    private static void ExecuteScalarPragma(
        SqliteConnection connection,
        string sql
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteScalar();
    }

    private static void RequirePragmaInteger(
        SqliteConnection connection,
        string name,
        long expected
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        object? value = command.ExecuteScalar();
        if (value is null
            || Convert.ToInt64(value) != expected) {
            throw new InvalidDataException(
                $"Timeline SQLite PRAGMA {name} is not {expected}."
            );
        }
    }

    private static void RequirePragmaText(
        SqliteConnection connection,
        string name,
        string expected
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        object? value = command.ExecuteScalar();
        if (!string.Equals(
                Convert.ToString(value),
                expected,
                StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException(
                $"Timeline SQLite PRAGMA {name} is not {expected}."
            );
        }
    }

    private static void RequireDatabaseBound(
        string path,
        HistoryTimelineStorageLimits limits
    ) {
        var file = new FileInfo(path);
        if (!file.Exists) {
            throw new FileNotFoundException(
                "Timeline database exact slot is absent.",
                path
            );
        }
        if (file.Length is < 1) {
            throw new InvalidDataException(
                "Timeline database exact slot is empty."
            );
        }
    }

    private static string ComputeHeadDigest(ReadOnlySpan<byte> canonical)
        => HistoryTimelineHash.Compute(HeadHashDomain, canonical);

    private bool TryReadInvalid<T>(
        out HistoryTimelineStoreReadResult<T>.Invalid invalid
    ) {
        lock (_invalidGate) {
            if (_invalidCode is null) {
                invalid = null!;
                return false;
            }
            invalid = new HistoryTimelineStoreReadResult<T>.Invalid(
                _invalidCode,
                _invalidDetail!
            );
            return true;
        }
    }

    private bool TryWriteInvalid(
        out string code,
        out string detail
    ) {
        lock (_invalidGate) {
            code = _invalidCode!;
            detail = _invalidDetail!;
            return _invalidCode is not null;
        }
    }

    private HistoryTimelineStoreReadResult<T> LatchInvalid<T>(
        Exception exception
    ) {
        (string code, string detail) = LatchInvalid(exception);
        return new HistoryTimelineStoreReadResult<T>.Invalid(
            code,
            detail
        );
    }

    private (string Code, string Detail) LatchInvalid(
        Exception exception
    ) {
        string code = exception switch {
            InvalidDataException => "TimelineStoreInvalid",
            FileNotFoundException => "TimelineStoreSlotMissing",
            UnauthorizedAccessException => "TimelineStoreUnauthorized",
            HistoryTimelineUnsupportedSchemaException =>
                "TimelineStoreUnsupportedSchema",
            SqliteException sqlite =>
                $"TimelineStoreSqlite{sqlite.SqliteErrorCode}",
            IOException => "TimelineStoreIoInvalid",
            _ => "TimelineStoreInvalid"
        };
        string detail = exception.Message;
        lock (_invalidGate) {
            _invalidCode ??= code;
            _invalidDetail ??= detail;
            return (_invalidCode, _invalidDetail);
        }
    }

    private static bool IsBusy(SqliteException exception)
        => exception.SqliteErrorCode is 5 or 6;

    private static bool IsFullOrTooBig(SqliteException exception)
        => exception.SqliteErrorCode is 13 or 18;

    private static bool IsStoreFailure(Exception exception)
        => exception is InvalidDataException
            or FileNotFoundException
            or UnauthorizedAccessException
            or SqliteException
            or IOException
            or OverflowException;

    private static void TryDeleteDatabaseArtifacts(string path) {
        foreach (string candidate in new[] {
                     path,
                     path + "-journal",
                     path + "-wal",
                     path + "-shm"
                 }) {
            try {
                if (File.Exists(candidate)) {
                    File.Delete(candidate);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private const int ApplicationId = 0x41544854;

    private static readonly SchemaEntry[] SchemaEntriesInCreationOrder = [
        new(
            "table",
            "store_metadata",
            "store_metadata",
            """
            CREATE TABLE store_metadata(
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                schema_version INTEGER NOT NULL,
                timeline_id TEXT NOT NULL,
                ref_id TEXT NOT NULL,
                head_canonical BLOB NOT NULL,
                head_sha256 TEXT NOT NULL,
                policy_count INTEGER NOT NULL CHECK(policy_count >= 0),
                row_count INTEGER NOT NULL CHECK(row_count >= 0)
            ) STRICT
            """
        ),
        new(
            "table",
            "policies",
            "policies",
            """
            CREATE TABLE policies(
                policy_digest TEXT PRIMARY KEY,
                canonical BLOB NOT NULL
            ) STRICT, WITHOUT ROWID
            """
        ),
        new(
            "table",
            "rows",
            "rows",
            """
            CREATE TABLE rows(
                row_id TEXT PRIMARY KEY,
                previous_row_id TEXT NULL,
                end_address BLOB NOT NULL,
                descriptor_digest TEXT NOT NULL,
                canonical BLOB NOT NULL,
                FOREIGN KEY(previous_row_id) REFERENCES rows(row_id)
            ) STRICT, WITHOUT ROWID
            """
        ),
        new(
            "table",
            "current_selected_path",
            "current_selected_path",
            """
            CREATE TABLE current_selected_path(
                ordinal INTEGER PRIMARY KEY CHECK(ordinal >= 0),
                row_id TEXT NOT NULL UNIQUE,
                previous_row_id TEXT NULL,
                end_address BLOB NOT NULL UNIQUE,
                leaf_digest TEXT NOT NULL,
                FOREIGN KEY(row_id) REFERENCES rows(row_id)
            ) STRICT
            """
        ),
        new(
            "table",
            "current_selected_path_merkle",
            "current_selected_path_merkle",
            """
            CREATE TABLE current_selected_path_merkle(
                level INTEGER NOT NULL CHECK(level >= 0 AND level <= 62),
                node_index INTEGER NOT NULL CHECK(node_index >= 0),
                range_start INTEGER NOT NULL CHECK(range_start >= 0),
                range_end INTEGER NOT NULL CHECK(range_end >= range_start),
                digest TEXT NOT NULL,
                PRIMARY KEY(level, node_index)
            ) STRICT, WITHOUT ROWID
            """
        ),
        new(
            "table",
            "current_selected_path_guard",
            "current_selected_path_guard",
            """
            CREATE TABLE current_selected_path_guard(
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                dirty INTEGER NOT NULL CHECK(dirty IN (0, 1))
            ) STRICT
            """
        ),
        new(
            "trigger",
            "guard_selected_path_ad",
            "current_selected_path",
            """
            CREATE TRIGGER guard_selected_path_ad
            AFTER DELETE ON current_selected_path
            BEGIN
                UPDATE current_selected_path_guard SET dirty = 1 WHERE singleton = 1;
            END
            """
        ),
        new(
            "trigger",
            "guard_selected_path_ai",
            "current_selected_path",
            """
            CREATE TRIGGER guard_selected_path_ai
            AFTER INSERT ON current_selected_path
            BEGIN
                UPDATE current_selected_path_guard SET dirty = 1 WHERE singleton = 1;
            END
            """
        ),
        new(
            "trigger",
            "guard_selected_path_au",
            "current_selected_path",
            """
            CREATE TRIGGER guard_selected_path_au
            AFTER UPDATE ON current_selected_path
            BEGIN
                UPDATE current_selected_path_guard SET dirty = 1 WHERE singleton = 1;
            END
            """
        ),
        new(
            "trigger",
            "guard_selected_path_commitments_ad",
            "current_selected_path_merkle",
            """
            CREATE TRIGGER guard_selected_path_commitments_ad
            AFTER DELETE ON current_selected_path_merkle
            BEGIN
                UPDATE current_selected_path_guard SET dirty = 1 WHERE singleton = 1;
            END
            """
        ),
        new(
            "trigger",
            "guard_selected_path_commitments_ai",
            "current_selected_path_merkle",
            """
            CREATE TRIGGER guard_selected_path_commitments_ai
            AFTER INSERT ON current_selected_path_merkle
            BEGIN
                UPDATE current_selected_path_guard SET dirty = 1 WHERE singleton = 1;
            END
            """
        ),
        new(
            "trigger",
            "guard_selected_path_commitments_au",
            "current_selected_path_merkle",
            """
            CREATE TRIGGER guard_selected_path_commitments_au
            AFTER UPDATE ON current_selected_path_merkle
            BEGIN
                UPDATE current_selected_path_guard SET dirty = 1 WHERE singleton = 1;
            END
            """
        )
    ];

    private sealed record StoreCounts(
        long PolicyCount,
        long RowCount
    );

    private sealed record CurrentSelectedTail(
        long Ordinal,
        HistoryRowId? RowId
    );

    private sealed record CurrentSelectedAssignment(
        long Ordinal,
        HistoryRowId RowId,
        HistoryRowId? PreviousRowId,
        byte[] EndAddress,
        string LeafDigest
    );

    private sealed record SchemaEntry(
        string Type,
        string Name,
        string TableName,
        string Sql
    );

    private sealed record CanonicalRowRecord(
        string RowId,
        string? PreviousRowId,
        byte[] EndAddress,
        string DescriptorDigest,
        long CanonicalLength,
        byte[] Canonical
    );

    private sealed class SqliteBoundaryProbe
        : IHistoryTimelineBoundaryProbe {
        private readonly SqliteHistoryTimelineLedger _owner;
        private readonly SqliteConnection _connection;
        private readonly SqliteCommand _readRow;
        private readonly SqliteParameter _endAddress;
        private readonly TimelineHeadRef _expectedHead;
        private readonly Action? _beforeLookupQuery;
        private bool _disposed;

        internal SqliteBoundaryProbe(
            SqliteHistoryTimelineLedger owner,
            SqliteConnection connection,
            TimelineHeadRef expectedHead,
            Action? beforeLookupQuery
        ) {
            _owner = owner;
            _connection = connection;
            _expectedHead = expectedHead;
            _beforeLookupQuery = beforeLookupQuery;
            _readRow = connection.CreateCommand();
            _readRow.CommandText = """
                SELECT
                    p.row_id,
                    p.ordinal,
                    p.previous_row_id,
                    p.end_address,
                    p.leaf_digest,
                    length(r.canonical),
                    r.canonical
                FROM current_selected_path AS p
                JOIN rows AS r ON r.row_id = p.row_id
                WHERE p.end_address = $end;
                """;
            _endAddress = _readRow.Parameters.Add(
                "$end",
                SqliteType.Blob
            );
            _readRow.Prepare();
        }

        public SelectedHistoryBoundaryResult Probe(
            EventAddress endInclusive
        ) {
            if (_disposed) {
                return new SelectedHistoryBoundaryResult.Invalid(
                    "BoundaryProbeDisposed",
                    "The operation-scoped boundary probe is disposed."
                );
            }
            try {
                _beforeLookupQuery?.Invoke();
                byte[] encoded = new byte[
                    EventAddressCodec.EventAddressLength];
                EventAddressCodec.Encode(endInclusive, encoded);
                _endAddress.Value = encoded;
                using SqliteDataReader reader = _readRow.ExecuteReader();
                if (!reader.Read()) {
                    return new SelectedHistoryBoundaryResult.NotFound();
                }
                var rowId = new HistoryRowId(reader.GetString(0));
                var assignment = new CurrentSelectedAssignment(
                    reader.GetInt64(1),
                    rowId,
                    reader.IsDBNull(2)
                        ? null
                        : new HistoryRowId(reader.GetString(2)),
                    reader.GetFieldValue<byte[]>(3),
                    HistoryTimelineSyntax.RequireSha256(
                        reader.GetString(4),
                        "leaf_digest"));
                long length = reader.GetInt64(5);
                if (length is < 1
                    or > HistoryTimelineCanonicalCodec
                        .MaximumDescriptorUtf8Bytes) {
                    throw new InvalidDataException(
                        "The selected boundary row exceeds its canonical byte bound."
                    );
                }
                byte[] canonical = reader.GetFieldValue<byte[]>(6);
                if (canonical.Length != length) {
                    throw new InvalidDataException(
                        "The selected boundary row length changed while reading."
                    );
                }
                HistorySegmentDescriptor descriptor =
                    _owner.DecodeRowCanonical(rowId, canonical);
                RequireSelectedAssignment(
                    _connection,
                    transaction: null,
                    _expectedHead,
                    assignment,
                    descriptor);
                if (descriptor.EndInclusive != endInclusive) {
                    throw new InvalidDataException(
                        "The selected boundary index differs from its row."
                    );
                }
                return new SelectedHistoryBoundaryResult.Found(
                    descriptor
                );
            }
            catch (SqliteException exception) when (IsBusy(exception)) {
                return new SelectedHistoryBoundaryResult.BackendBusy();
            }
            catch (Exception exception) when (IsStoreFailure(exception)) {
                (string code, string detail) =
                    _owner.LatchInvalid(exception);
                return new SelectedHistoryBoundaryResult.Invalid(
                    code,
                    detail
                );
            }
        }

        public void Dispose() {
            if (_disposed) {
                return;
            }
            _disposed = true;
            _readRow.Dispose();
            _connection.Dispose();
        }
    }

}
