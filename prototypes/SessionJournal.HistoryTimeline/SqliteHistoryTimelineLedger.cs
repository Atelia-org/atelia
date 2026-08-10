using Microsoft.Data.Sqlite;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

internal sealed class SqliteHistoryTimelineLedger
    : IHistoryTimelineLedgerPort {
    internal const int SchemaVersion = 1;
    internal const string HeadHashDomain =
        "atelia.history-timeline.head.v1";
    internal const string SelectedSnapshotHashDomain =
        "atelia.history-timeline.selected-path-snapshot.v1";
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
    internal const string VerifyTrieNodesFirstPageSql = """
        SELECT node_digest, length(canonical), canonical
        FROM selected_path_nodes
        ORDER BY node_digest
        LIMIT 128
        """;
    internal const string VerifyTrieNodesNextPageSql = """
        SELECT node_digest, length(canonical), canonical
        FROM selected_path_nodes
        WHERE node_digest > $after
        ORDER BY node_digest
        LIMIT 128
        """;
    internal const string VerifySnapshotsFirstPageSql = """
        SELECT head_row_id
        FROM selected_path_snapshots
        ORDER BY head_row_id
        LIMIT 128
        """;
    internal const string VerifySnapshotsNextPageSql = """
        SELECT head_row_id
        FROM selected_path_snapshots
        WHERE head_row_id > $after
        ORDER BY head_row_id
        LIMIT 128
        """;

    private readonly string _databasePath;
    private readonly TimelineId _timelineId;
    private readonly RefId _refId;
    private readonly HistoryTimelineStorageLimits _limits;
    private readonly HistoryTimelinePersistenceTestHooks _hooks;
    private readonly bool _readOnly;
    private readonly SqliteSelectedPathTrie _trie = new();
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
            using (SqliteCommand schema = connection.CreateCommand()) {
                schema.CommandText = SchemaSql;
                schema.ExecuteNonQuery();
            }
            var head = new TimelineHeadRef(
                initialPolicy.TimelineId,
                refId,
                headRowId: null,
                initialPolicy.PolicyDigest,
                selectedRawHeadAtCommit: null,
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
                        row_count,
                        trie_node_count
                    ) VALUES (
                        1, $schema, $timeline, $ref,
                        $head, $headDigest, 1, 0, 0
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
            VerifyCanonicalTrieNodes(connection, transaction);
            VerifyCanonicalSelectedSnapshots(connection, transaction);
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
            SelectedSnapshot snapshot = ReadSelectedSnapshot(
                connection,
                transaction,
                head.HeadRowId
            );
            if (head.HeadRowId is { } rowId) {
                if (_trie.LookupRow(
                        connection,
                        transaction,
                        snapshot.RowRootDigest,
                        rowId
                    ) != rowId) {
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
            if (counts.PolicyCount >= _limits.MaximumPolicyCount) {
                return new HistoryTimelinePolicyPutResult.LimitExceeded(
                    "MaximumPolicyCount"
                );
            }
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
                "MaximumDatabaseBytes"
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
            HistoryRowProposal proposal = candidate.Proposal;
            TimelineHeadRef actual = ReadHead(connection, transaction);
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
            if (existingRow is null
                && counts.RowCount >= _limits.MaximumRowCount) {
                return new HistoryTimelineCommitResult.LimitExceeded(
                    "MaximumRowCount"
                );
            }
            SelectedSnapshot current =
                ReadAndValidateSelectedSnapshot(
                connection,
                transaction,
                actual.HeadRowId
            );
            HistoryRowId? existingSelectedRow = _trie.LookupRow(
                connection,
                transaction,
                current.RowRootDigest,
                descriptor.RowId
            );
            HistoryRowId? boundaryOwner = _trie.LookupEnd(
                connection,
                transaction,
                current.EndRootDigest,
                descriptor.EndInclusive
            );
            if (existingSelectedRow is not null
                || boundaryOwner is not null) {
                return new HistoryTimelineCommitResult.Invalid(
                    "SelectedBoundaryCollision",
                    "The exact predecessor snapshot already contains the proposed row or raw boundary."
                );
            }

            string expectedRowRoot = _trie.ComputeRowExtension(
                connection,
                transaction,
                current.RowRootDigest,
                descriptor.RowId
            );
            string expectedEndRoot = _trie.ComputeEndExtension(
                connection,
                transaction,
                current.EndRootDigest,
                descriptor.EndInclusive,
                descriptor.RowId
            );
            SelectedSnapshot expectedSnapshot = CreateSelectedSnapshot(
                descriptor.RowId,
                expectedRowRoot,
                expectedEndRoot,
                checked(current.MemberCount + 1)
            );
            int insertedNodes = 0;
            if (TryReadSelectedSnapshot(
                    connection,
                    transaction,
                    descriptor.RowId,
                    out SelectedSnapshot existingSnapshot)) {
                if (existingRow is null
                    || !SelectedSnapshotsEqual(
                        existingSnapshot,
                        expectedSnapshot)) {
                    return new HistoryTimelineCommitResult.Invalid(
                        "SelectedPathSnapshotCollision",
                        "The row ID is bound to a snapshot that is not the exact predecessor extension."
                    );
                }
                ValidateSnapshotHeadMembership(
                    connection,
                    transaction,
                    existingSnapshot,
                    descriptor.RowId
                );
                if (_trie.LookupEnd(
                        connection,
                        transaction,
                        existingSnapshot.EndRootDigest,
                        descriptor.EndInclusive
                    ) != descriptor.RowId) {
                    return new HistoryTimelineCommitResult.Invalid(
                        "SelectedPathSnapshotCollision",
                        "The row ID snapshot does not contain its exact boundary."
                    );
                }
            }
            else {
                if (existingRow is not null) {
                    return new HistoryTimelineCommitResult.Invalid(
                        "SelectedPathSnapshotMissing",
                        "An existing row has no exact selected-path snapshot."
                    );
                }
                InsertRow(
                    connection,
                    transaction,
                    descriptor,
                    descriptorBytes
                );
                string rowRoot = _trie.InsertRow(
                    connection,
                    transaction,
                    current.RowRootDigest,
                    descriptor.RowId,
                    ref insertedNodes
                );
                string endRoot = _trie.InsertEnd(
                    connection,
                    transaction,
                    current.EndRootDigest,
                    descriptor.EndInclusive,
                    descriptor.RowId,
                    ref insertedNodes
                );
                if (!string.Equals(
                        rowRoot,
                        expectedRowRoot,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        endRoot,
                        expectedEndRoot,
                        StringComparison.Ordinal)) {
                    return new HistoryTimelineCommitResult.Invalid(
                        "SelectedPathSnapshotConstructionMismatch",
                        "Materialized trie roots differ from the deterministic predecessor extension."
                    );
                }
                if (checked(
                        counts.TrieNodeCount + insertedNodes)
                    > _limits.MaximumTrieNodeCount) {
                    return new HistoryTimelineCommitResult.LimitExceeded(
                        "MaximumTrieNodeCount"
                    );
                }
                InsertSelectedSnapshot(
                    connection,
                    transaction,
                    descriptor.RowId,
                    expectedSnapshot
                );
            }

            TimelineHeadRef next = new(
                actual.TimelineId,
                actual.RefId,
                descriptor.RowId,
                actual.ActivePartitionPolicyDigest,
                proposal.CapturedSelectedRawHead,
                checked(actual.Generation + 1)
            );
            WriteHead(connection, transaction, actual, next);
            UpdateCounts(
                connection,
                transaction,
                counts with {
                    RowCount = existingRow is null
                        ? checked(counts.RowCount + 1)
                        : counts.RowCount,
                    TrieNodeCount = checked(
                        counts.TrieNodeCount + insertedNodes
                    )
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
                "MaximumDatabaseBytes"
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
            SelectedSnapshot snapshot =
                ReadAndValidateSelectedSnapshot(
                connection,
                transaction,
                actual.HeadRowId
            );
            HistoryRowId? found = _trie.LookupRow(
                connection,
                transaction,
                snapshot.RowRootDigest,
                rowId
            );
            if (found != rowId) {
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
            SelectedSnapshot snapshot =
                ReadAndValidateSelectedSnapshot(
                connection,
                transaction: null,
                actual.HeadRowId
            );
            var probe = new SqliteBoundaryProbe(
                this,
                connection,
                _trie.OpenEndBoundaryProbe(
                    connection,
                    snapshot.EndRootDigest,
                    _hooks.BeforeBoundaryProbeLookupQuery
                ),
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
            SelectedSnapshot current =
                ReadAndValidateSelectedSnapshot(
                connection,
                transaction,
                actual.HeadRowId
            );
            SelectedSnapshot nextSnapshot = SelectedSnapshot.Empty;
            if (candidate.SelectedRowId is { } selectedRowId) {
                HistoryRowId? member = _trie.LookupRow(
                    connection,
                    transaction,
                    current.RowRootDigest,
                    selectedRowId
                );
                if (member != selectedRowId) {
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
                nextSnapshot = ReadAndValidateSelectedSnapshot(
                    connection,
                    transaction,
                    selectedRowId
                );
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
            _ = nextSnapshot;
            TimelineHeadRef next = new(
                actual.TimelineId,
                actual.RefId,
                candidate.SelectedRowId,
                actual.ActivePartitionPolicyDigest,
                selectedFence,
                checked(actual.Generation + 1)
            );
            WriteHead(connection, transaction, actual, next);
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
            SelectedSnapshot snapshot =
                ReadAndValidateSelectedSnapshot(
                connection,
                transaction,
                actual.HeadRowId
            );
            HistoryRowId? cursor = startAt ?? actual.HeadRowId;
            if (cursor is { } requested
                && _trie.LookupRow(
                    connection,
                    transaction,
                    snapshot.RowRootDigest,
                    requested
                ) != requested) {
                return new HistoryTimelineStorePathPageResult.Invalid(
                    "PathCursorNotSelected",
                    "The path cursor is not on the exact selected path."
                );
            }
            var rows = new List<HistorySegmentDescriptor>(maximumRows);
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

    private SelectedSnapshot ReadSelectedSnapshot(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryRowId? headRowId
    ) {
        if (headRowId is null) {
            return SelectedSnapshot.Empty;
        }
        if (!TryReadSelectedSnapshot(
                connection,
                transaction,
                headRowId.Value,
                out SelectedSnapshot snapshot)) {
            throw new InvalidDataException(
                "The selected head has no persistent path snapshot."
            );
        }
        return snapshot;
    }

    private static bool TryReadSelectedSnapshot(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryRowId rowId,
        out SelectedSnapshot snapshot
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                row_root_digest,
                end_root_digest,
                member_count,
                snapshot_digest,
                length(canonical),
                canonical
            FROM selected_path_snapshots
            WHERE head_row_id = $row;
            """;
        command.Parameters.AddWithValue("$row", rowId.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            snapshot = SelectedSnapshot.Empty;
            return false;
        }
        string rowRoot = reader.GetString(0);
        string endRoot = reader.GetString(1);
        int memberCount = reader.GetInt32(2);
        string storedDigest = reader.GetString(3);
        long canonicalLength = reader.GetInt64(4);
        if (canonicalLength is < 1
            or > HistoryTimelineStoreLimits.MaximumHeadUtf8Bytes) {
            throw new InvalidDataException(
                "Selected-path snapshot canonical bytes exceed their bound."
            );
        }
        byte[] canonical = reader.GetFieldValue<byte[]>(5);
        if (canonical.Length != canonicalLength) {
            throw new InvalidDataException(
                "Selected-path snapshot canonical length changed while reading."
            );
        }
        HistoryTimelineSelectedPathSnapshotBody body =
            HistoryTimelineCanonicalCodec.DecodeSelectedPathSnapshot(
                canonical
            );
        string actualDigest = HistoryTimelineHash.Compute(
            SelectedSnapshotHashDomain,
            canonical
        );
        if (body.HeadRowId != rowId
            || !string.Equals(
                body.RowRootDigest,
                rowRoot,
                StringComparison.Ordinal)
            || !string.Equals(
                body.EndRootDigest,
                endRoot,
                StringComparison.Ordinal)
            || body.MemberCount != memberCount
            || !string.Equals(
                actualDigest,
                storedDigest,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Selected-path snapshot canonical commitment is invalid."
            );
        }
        snapshot = new SelectedSnapshot(
            body.HeadRowId,
            body.RowRootDigest,
            body.EndRootDigest,
            body.MemberCount,
            actualDigest,
            canonical
        );
        return true;
    }

    private static void InsertSelectedSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HistoryRowId rowId,
        SelectedSnapshot snapshot
    ) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO selected_path_snapshots(
                head_row_id,
                row_root_digest,
                end_root_digest,
                member_count,
                snapshot_digest,
                canonical
            ) VALUES (
                $row,
                $rowRoot,
                $endRoot,
                $count,
                $digest,
                $canonical
            );
            """;
        command.Parameters.AddWithValue("$row", rowId.Value);
        command.Parameters.AddWithValue(
            "$rowRoot",
            snapshot.RowRootDigest!
        );
        command.Parameters.AddWithValue(
            "$endRoot",
            snapshot.EndRootDigest!
        );
        command.Parameters.AddWithValue("$count", snapshot.MemberCount);
        command.Parameters.AddWithValue(
            "$digest",
            snapshot.SnapshotDigest!
        );
        command.Parameters.AddWithValue(
            "$canonical",
            snapshot.Canonical!
        );
        command.ExecuteNonQuery();
    }

    private static SelectedSnapshot CreateSelectedSnapshot(
        HistoryRowId headRowId,
        string rowRootDigest,
        string endRootDigest,
        int memberCount
    ) {
        var body = new HistoryTimelineSelectedPathSnapshotBody(
            headRowId,
            rowRootDigest,
            endRootDigest,
            memberCount
        );
        byte[] canonical = HistoryTimelineCanonicalCodec.Encode(body);
        return new SelectedSnapshot(
            body.HeadRowId,
            body.RowRootDigest,
            body.EndRootDigest,
            body.MemberCount,
            HistoryTimelineHash.Compute(
                SelectedSnapshotHashDomain,
                canonical
            ),
            canonical
        );
    }

    private static bool SelectedSnapshotsEqual(
        SelectedSnapshot left,
        SelectedSnapshot right
    ) => left.HeadRowId == right.HeadRowId
        && string.Equals(
            left.RowRootDigest,
            right.RowRootDigest,
            StringComparison.Ordinal
        )
        && string.Equals(
            left.EndRootDigest,
            right.EndRootDigest,
            StringComparison.Ordinal
        )
        && left.MemberCount == right.MemberCount
        && string.Equals(
            left.SnapshotDigest,
            right.SnapshotDigest,
            StringComparison.Ordinal
        )
        && left.Canonical is not null
        && right.Canonical is not null
        && left.Canonical.AsSpan().SequenceEqual(right.Canonical);

    private void ValidateSnapshotHeadMembership(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SelectedSnapshot snapshot,
        HistoryRowId? expectedHeadRowId
    ) {
        if (expectedHeadRowId is null) {
            if (snapshot != SelectedSnapshot.Empty) {
                throw new InvalidDataException(
                    "The empty selected path has a non-empty snapshot."
                );
            }
            return;
        }
        if (snapshot.HeadRowId != expectedHeadRowId
            || snapshot.MemberCount < 1
            || _trie.LookupRow(
                connection,
                transaction,
                snapshot.RowRootDigest,
                expectedHeadRowId.Value
            ) != expectedHeadRowId.Value) {
            throw new InvalidDataException(
                "A selected-path snapshot does not contain its exact head row."
            );
        }
    }

    private SelectedSnapshot ReadAndValidateSelectedSnapshot(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HistoryRowId? headRowId
    ) {
        SelectedSnapshot snapshot = ReadSelectedSnapshot(
            connection,
            transaction,
            headRowId
        );
        ValidateSnapshotHeadMembership(
            connection,
            transaction,
            snapshot,
            headRowId
        );
        if (headRowId is null) {
            if (snapshot != SelectedSnapshot.Empty) {
                throw new InvalidDataException(
                    "The empty selected path has a non-empty snapshot."
                );
            }
            return snapshot;
        }

        HistorySegmentDescriptor descriptor = ReadRowCore(
            connection,
            transaction,
            headRowId.Value
        ) ?? throw new InvalidDataException(
            "The selected snapshot head row is missing."
        );
        SelectedSnapshot predecessor = ReadSelectedSnapshot(
            connection,
            transaction,
            descriptor.PreviousRowId
        );
        ValidateSnapshotHeadMembership(
            connection,
            transaction,
            predecessor,
            descriptor.PreviousRowId
        );
        if (_trie.LookupRow(
                connection,
                transaction,
                predecessor.RowRootDigest,
                descriptor.RowId
            ) is not null
            || _trie.LookupEnd(
                connection,
                transaction,
                predecessor.EndRootDigest,
                descriptor.EndInclusive
            ) is not null) {
            throw new InvalidDataException(
                "The selected snapshot is not a strict predecessor extension."
            );
        }
        SelectedSnapshot expected = CreateSelectedSnapshot(
            descriptor.RowId,
            _trie.ComputeRowExtension(
                connection,
                transaction,
                predecessor.RowRootDigest,
                descriptor.RowId
            ),
            _trie.ComputeEndExtension(
                connection,
                transaction,
                predecessor.EndRootDigest,
                descriptor.EndInclusive,
                descriptor.RowId
            ),
            checked(predecessor.MemberCount + 1)
        );
        if (!SelectedSnapshotsEqual(snapshot, expected)
            || _trie.LookupEnd(
                connection,
                transaction,
                snapshot.EndRootDigest,
                descriptor.EndInclusive
            ) != descriptor.RowId) {
            throw new InvalidDataException(
                "The selected snapshot differs from its exact predecessor recurrence."
            );
        }
        return snapshot;
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

    private static void VerifyCanonicalTrieNodes(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        string? afterDigest = null;
        while (true) {
            var page = new List<CanonicalTrieNodeRecord>(128);
            using (var command = connection.CreateCommand()) {
                command.Transaction = transaction;
                command.CommandText = afterDigest is null
                    ? VerifyTrieNodesFirstPageSql
                    : VerifyTrieNodesNextPageSql;
                if (afterDigest is not null) {
                    command.Parameters.AddWithValue(
                        "$after",
                        afterDigest
                    );
                }
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) {
                    page.Add(new CanonicalTrieNodeRecord(
                        reader.GetString(0),
                        reader.GetInt64(1),
                        reader.GetFieldValue<byte[]>(2)
                    ));
                }
            }
            if (page.Count == 0) {
                return;
            }
            foreach (CanonicalTrieNodeRecord node in page) {
                if (node.CanonicalLength != node.Canonical.Length) {
                    throw new InvalidDataException(
                        "A selected-path trie node length is invalid."
                    );
                }
                IReadOnlyList<string> children =
                    SqliteSelectedPathTrie.VerifyCanonicalNode(
                        node.Digest,
                        node.Canonical
                    );
                foreach (string child in children) {
                    if (!CanonicalKeyExists(
                            connection,
                            transaction,
                            "selected_path_nodes",
                            "node_digest",
                            child
                        )) {
                        throw new InvalidDataException(
                            "A selected-path trie node references a missing child."
                        );
                    }
                }
            }
            afterDigest = page[^1].Digest;
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

    private void VerifyCanonicalSelectedSnapshots(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        string? afterRowId = null;
        while (true) {
            var rowIds = new List<HistoryRowId>(128);
            using (var command = connection.CreateCommand()) {
                command.Transaction = transaction;
                command.CommandText = afterRowId is null
                    ? VerifySnapshotsFirstPageSql
                    : VerifySnapshotsNextPageSql;
                if (afterRowId is not null) {
                    command.Parameters.AddWithValue(
                        "$after",
                        afterRowId
                    );
                }
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) {
                    rowIds.Add(new HistoryRowId(reader.GetString(0)));
                }
            }
            if (rowIds.Count == 0) {
                return;
            }
            foreach (HistoryRowId rowId in rowIds) {
                SelectedSnapshot snapshot = ReadSelectedSnapshot(
                    connection,
                    transaction,
                    rowId
                );
                ValidateSnapshotHeadMembership(
                    connection,
                    transaction,
                    snapshot,
                    rowId
                );
                HistorySegmentDescriptor descriptor = ReadRowCore(
                    connection,
                    transaction,
                    rowId
                ) ?? throw new InvalidDataException(
                    "A selected-path snapshot references a missing row."
                );
                SelectedSnapshot predecessor = ReadSelectedSnapshot(
                    connection,
                    transaction,
                    descriptor.PreviousRowId
                );
                ValidateSnapshotHeadMembership(
                    connection,
                    transaction,
                    predecessor,
                    descriptor.PreviousRowId
                );
                if (_trie.LookupRow(
                        connection,
                        transaction,
                        predecessor.RowRootDigest,
                        rowId
                    ) is not null
                    || _trie.LookupEnd(
                        connection,
                        transaction,
                        predecessor.EndRootDigest,
                        descriptor.EndInclusive
                    ) is not null) {
                    throw new InvalidDataException(
                        "A selected-path snapshot is not a strict predecessor extension."
                    );
                }
                SelectedSnapshot expected = CreateSelectedSnapshot(
                    rowId,
                    _trie.ComputeRowExtension(
                        connection,
                        transaction,
                        predecessor.RowRootDigest,
                        rowId
                    ),
                    _trie.ComputeEndExtension(
                        connection,
                        transaction,
                        predecessor.EndRootDigest,
                        descriptor.EndInclusive,
                        rowId
                    ),
                    checked(predecessor.MemberCount + 1)
                );
                if (!SelectedSnapshotsEqual(snapshot, expected)
                    || _trie.LookupEnd(
                        connection,
                        transaction,
                        snapshot.EndRootDigest,
                        descriptor.EndInclusive
                    ) != rowId) {
                    throw new InvalidDataException(
                        "A selected-path snapshot differs from its exact predecessor recurrence."
                    );
                }
            }
            afterRowId = rowIds[^1].Value;
        }
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
            SELECT policy_count, row_count, trie_node_count
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
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2)
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
                row_count = $rows,
                trie_node_count = $nodes
            WHERE singleton = 1;
            """;
        command.Parameters.AddWithValue(
            "$policies",
            counts.PolicyCount
        );
        command.Parameters.AddWithValue("$rows", counts.RowCount);
        command.Parameters.AddWithValue(
            "$nodes",
            counts.TrieNodeCount
        );
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
        if (stored.PolicyCount < 1
            || stored.PolicyCount > _limits.MaximumPolicyCount
            || stored.RowCount < 0
            || stored.RowCount > _limits.MaximumRowCount
            || stored.TrieNodeCount < 0
            || stored.TrieNodeCount > _limits.MaximumTrieNodeCount) {
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
                (SELECT COUNT(*) FROM rows),
                (SELECT COUNT(*) FROM selected_path_nodes),
                (SELECT COUNT(*) FROM selected_path_snapshots);
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw new InvalidDataException(
                "Timeline SQLite physical counts are unavailable."
            );
        }
        long policies = reader.GetInt64(0);
        long rows = reader.GetInt64(1);
        long nodes = reader.GetInt64(2);
        long snapshots = reader.GetInt64(3);
        StoreCounts stored = ReadCounts(connection, transaction);
        if (stored.PolicyCount != policies
            || stored.RowCount != rows
            || stored.TrieNodeCount != nodes
            || snapshots != rows) {
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
        int index = 0;
        while (schemaReader.Read()) {
            if (index >= ExpectedSchemaEntries.Length) {
                throw new InvalidDataException(
                    "Timeline SQLite schema contains an unexpected object."
                );
            }
            SchemaEntry expected = ExpectedSchemaEntries[index++];
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
                    "Timeline SQLite schema shape differs from V1."
                );
            }
        }
        if (index != ExpectedSchemaEntries.Length) {
            throw new InvalidDataException(
                "Timeline SQLite schema is missing a required object."
            );
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
        long maximumPages = limits.MaximumDatabaseBytes / 4096;
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
        if (file.Length > limits.MaximumDatabaseBytes) {
            throw new HistoryTimelineStoreLimitException(
                "MaximumDatabaseBytes",
                "Timeline database exceeds its code-owned byte bound."
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

    private static readonly SchemaEntry[] ExpectedSchemaEntries = [
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
            "selected_path_nodes",
            "selected_path_nodes",
            """
            CREATE TABLE selected_path_nodes(
                node_digest TEXT PRIMARY KEY,
                canonical BLOB NOT NULL
            ) STRICT, WITHOUT ROWID
            """
        ),
        new(
            "table",
            "selected_path_snapshots",
            "selected_path_snapshots",
            """
            CREATE TABLE selected_path_snapshots(
                head_row_id TEXT PRIMARY KEY,
                row_root_digest TEXT NOT NULL,
                end_root_digest TEXT NOT NULL,
                member_count INTEGER NOT NULL CHECK(member_count >= 1),
                snapshot_digest TEXT NOT NULL,
                canonical BLOB NOT NULL,
                FOREIGN KEY(head_row_id) REFERENCES rows(row_id),
                FOREIGN KEY(row_root_digest)
                    REFERENCES selected_path_nodes(node_digest),
                FOREIGN KEY(end_root_digest)
                    REFERENCES selected_path_nodes(node_digest)
            ) STRICT, WITHOUT ROWID
            """
        ),
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
                row_count INTEGER NOT NULL CHECK(row_count >= 0),
                trie_node_count INTEGER NOT NULL CHECK(trie_node_count >= 0)
            ) STRICT
            """
        )
    ];

    private const string SchemaSql = """
        CREATE TABLE store_metadata(
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL,
            timeline_id TEXT NOT NULL,
            ref_id TEXT NOT NULL,
            head_canonical BLOB NOT NULL,
            head_sha256 TEXT NOT NULL,
            policy_count INTEGER NOT NULL CHECK(policy_count >= 0),
            row_count INTEGER NOT NULL CHECK(row_count >= 0),
            trie_node_count INTEGER NOT NULL CHECK(trie_node_count >= 0)
        ) STRICT;

        CREATE TABLE policies(
            policy_digest TEXT PRIMARY KEY,
            canonical BLOB NOT NULL
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE rows(
            row_id TEXT PRIMARY KEY,
            previous_row_id TEXT NULL,
            end_address BLOB NOT NULL,
            descriptor_digest TEXT NOT NULL,
            canonical BLOB NOT NULL,
            FOREIGN KEY(previous_row_id) REFERENCES rows(row_id)
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE selected_path_nodes(
            node_digest TEXT PRIMARY KEY,
            canonical BLOB NOT NULL
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE selected_path_snapshots(
            head_row_id TEXT PRIMARY KEY,
            row_root_digest TEXT NOT NULL,
            end_root_digest TEXT NOT NULL,
            member_count INTEGER NOT NULL CHECK(member_count >= 1),
            snapshot_digest TEXT NOT NULL,
            canonical BLOB NOT NULL,
            FOREIGN KEY(head_row_id) REFERENCES rows(row_id),
            FOREIGN KEY(row_root_digest)
                REFERENCES selected_path_nodes(node_digest),
            FOREIGN KEY(end_root_digest)
                REFERENCES selected_path_nodes(node_digest)
        ) STRICT, WITHOUT ROWID;

        """;

    private sealed record StoreCounts(
        int PolicyCount,
        int RowCount,
        int TrieNodeCount
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

    private sealed record CanonicalTrieNodeRecord(
        string Digest,
        long CanonicalLength,
        byte[] Canonical
    );

    private sealed class SqliteBoundaryProbe
        : IHistoryTimelineBoundaryProbe {
        private readonly SqliteHistoryTimelineLedger _owner;
        private readonly SqliteConnection _connection;
        private readonly SqliteSelectedPathTrie.EndBoundaryProbe
            _endProbe;
        private readonly SqliteCommand _readRow;
        private readonly SqliteParameter _rowId;
        private readonly Action? _beforeLookupQuery;
        private bool _disposed;

        internal SqliteBoundaryProbe(
            SqliteHistoryTimelineLedger owner,
            SqliteConnection connection,
            SqliteSelectedPathTrie.EndBoundaryProbe endProbe,
            Action? beforeLookupQuery
        ) {
            _owner = owner;
            _connection = connection;
            _endProbe = endProbe;
            _beforeLookupQuery = beforeLookupQuery;
            _readRow = connection.CreateCommand();
            _readRow.CommandText = """
                SELECT length(canonical), canonical
                FROM rows
                WHERE row_id = $row;
                """;
            _rowId = _readRow.Parameters.Add(
                "$row",
                SqliteType.Text
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
                HistoryRowId? rowId = _endProbe.Lookup(endInclusive);
                if (rowId is null) {
                    return new SelectedHistoryBoundaryResult.NotFound();
                }
                _beforeLookupQuery?.Invoke();
                _rowId.Value = rowId.Value.Value;
                using SqliteDataReader reader = _readRow.ExecuteReader();
                if (!reader.Read()) {
                    throw new InvalidDataException(
                        "The selected boundary references a missing row."
                    );
                }
                long length = reader.GetInt64(0);
                if (length is < 1
                    or > HistoryTimelineCanonicalCodec
                        .MaximumDescriptorUtf8Bytes) {
                    throw new InvalidDataException(
                        "The selected boundary row exceeds its canonical byte bound."
                    );
                }
                byte[] canonical = reader.GetFieldValue<byte[]>(1);
                if (canonical.Length != length) {
                    throw new InvalidDataException(
                        "The selected boundary row length changed while reading."
                    );
                }
                HistorySegmentDescriptor descriptor =
                    _owner.DecodeRowCanonical(rowId.Value, canonical);
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
            _endProbe.Dispose();
            _connection.Dispose();
        }
    }

    private sealed record SelectedSnapshot(
        HistoryRowId? HeadRowId,
        string? RowRootDigest,
        string? EndRootDigest,
        int MemberCount,
        string? SnapshotDigest,
        byte[]? Canonical
    ) {
        internal static SelectedSnapshot Empty { get; } = new(
            null,
            null,
            null,
            0,
            null,
            null
        );
    }

}
