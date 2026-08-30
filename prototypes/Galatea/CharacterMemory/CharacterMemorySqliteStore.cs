using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

/// <summary>
/// Per-user durable authority for Character Note extraction capture and the
/// single Default MemoPod apply protocol. The caller still owns the
/// SessionJournal TurnLock; this store additionally holds a process-lifetime
/// exclusive filesystem lock and serializes every operation on one handle.
/// </summary>
internal sealed partial class CharacterMemorySqliteStore : IDisposable {
    internal const int SchemaVersion = 1;
    internal const int ApplicationId = 0x47434D31; // "GCM1"
    internal const string DatabaseFileName = "character-memory.sqlite3";
    internal const string LockFileName = "character-memory.lock";

    private const int BusyTimeoutMilliseconds = 1_000;
    private const string CommitmentVersion =
        "atelia.galatea.character-memory.extraction-commitment.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly object _gate = new();
    private readonly string _storeDirectory;
    private readonly string _databasePath;
    private readonly CharacterMemoryStoreOwner _owner;
    private readonly CharacterMemoryStoreBaseline _baseline;
    private readonly CharacterMemoryStoreTestHooks _hooks;
    private readonly FileStream _lifetimeLock;
    private bool _disposed;

    private CharacterMemorySqliteStore(
        string storeDirectory,
        CharacterMemoryStoreOwner owner,
        CharacterMemoryStoreBaseline baseline,
        CharacterMemoryStoreTestHooks hooks,
        FileStream lifetimeLock
    ) {
        _storeDirectory = storeDirectory;
        _databasePath = Path.Combine(storeDirectory, DatabaseFileName);
        _owner = owner;
        _baseline = baseline;
        _hooks = hooks;
        _lifetimeLock = lifetimeLock;
    }

    internal string StoreDirectory => _storeDirectory;
    internal CharacterMemoryStoreBaseline Baseline => _baseline;

    internal static CharacterMemorySqliteStore CreateNew(
        string storeDirectory,
        CharacterMemoryStoreOwner owner,
        CharacterMemoryStoreBaseline baseline,
        string provisionTargetPodStateIdentity,
        CharacterMemoryStoreTestHooks? hooks = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        GalateaDelegationDurableFiles.RequireLinux();
        ValidateOwner(owner);
        ValidateBaseline(baseline);
        RequirePodStateIdentity(
            provisionTargetPodStateIdentity,
            nameof(provisionTargetPodStateIdentity)
        );

        string fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(storeDirectory)
        );
        RequireExistingAncestorsNoReparse(fullPath);
        if (Path.Exists(fullPath)) {
            throw new IOException(
                $"Character Memory store path already exists: {fullPath}"
            );
        }

        GalateaDelegationDurableFiles.CreateDirectoryNew(fullPath);
        FileStream? lifetimeLock = null;
        try {
            RejectReparsePoint(fullPath, "Character Memory store directory");
            lifetimeLock = AcquireLifetimeLock(fullPath, FileMode.CreateNew);
            GalateaDelegationDurableFiles.FlushDirectory(
                Path.GetDirectoryName(fullPath)
                    ?? throw new InvalidOperationException(
                        "Character Memory store has no parent directory."
                    )
            );
            string databasePath = Path.Combine(fullPath, DatabaseFileName);
            using SqliteConnection connection = OpenConnection(
                databasePath,
                create: true
            );
            ConfigureCreatedDatabase(connection);
            CreateSchema(connection);
            InsertInitialState(
                connection,
                owner,
                baseline,
                provisionTargetPodStateIdentity,
                hooks ?? CharacterMemoryStoreTestHooks.None
            );
            _ = ValidateOpenedDatabase(connection, owner);
            GalateaDelegationDurableFiles.FlushDirectory(fullPath);
            return new CharacterMemorySqliteStore(
                fullPath,
                owner,
                baseline,
                hooks ?? CharacterMemoryStoreTestHooks.None,
                lifetimeLock
            );
        }
        catch {
            lifetimeLock?.Dispose();
            throw;
        }
    }

    internal static CharacterMemorySqliteStore OpenExisting(
        string storeDirectory,
        CharacterMemoryStoreOwner owner,
        CharacterMemoryStoreTestHooks? hooks = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        GalateaDelegationDurableFiles.RequireLinux();
        ValidateOwner(owner);
        string fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(storeDirectory)
        );
        RequireExistingAncestorsNoReparse(fullPath);
        RejectReparsePoint(fullPath, "Character Memory store directory");
        if (!Directory.Exists(fullPath)) {
            throw new DirectoryNotFoundException(
                $"Character Memory store was not found: {fullPath}"
            );
        }
        RejectReparsePoint(
            Path.Combine(fullPath, LockFileName),
            "Character Memory lifetime lock"
        );
        FileStream lifetimeLock = AcquireLifetimeLock(
            fullPath,
            FileMode.Open
        );
        try {
            string databasePath = Path.Combine(fullPath, DatabaseFileName);
            RejectReparsePoint(databasePath, "Character Memory database");
            using SqliteConnection connection = OpenConnection(
                databasePath,
                create: false
            );
            ConfigureOpenedDatabase(connection);
            CharacterMemoryStatusSnapshot snapshot =
                ValidateOpenedDatabase(connection, owner);
            return new CharacterMemorySqliteStore(
                fullPath,
                owner,
                snapshot.Baseline,
                hooks ?? CharacterMemoryStoreTestHooks.None,
                lifetimeLock
            );
        }
        catch {
            lifetimeLock.Dispose();
            throw;
        }
    }

    internal CharacterMemoryStatusSnapshot ReadStatusSnapshot() {
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadStatusCore(connection, transaction: null);
        }
    }

    internal CharacterMemoryCaptureSnapshot? ReadCaptureExact(
        string sourceActionAddress
    ) {
        RequireEventAddress(sourceActionAddress, nameof(sourceActionAddress));
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadCaptureCore(
                connection,
                transaction: null,
                sourceActionAddress
            );
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) { return; }
            _disposed = true;
            _lifetimeLock.Dispose();
        }
    }

    private SqliteConnection OpenVerifiedConnection() {
        RejectReparsePoint(_databasePath, "Character Memory database");
        SqliteConnection connection = OpenConnection(
            _databasePath,
            create: false
        );
        try {
            ConfigureOpenedDatabase(connection);
            ValidateSchemaIdentity(connection);
            RequireOwner(connection, transaction: null, _owner);
            return connection;
        }
        catch {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenConnection(
        string databasePath,
        bool create
    ) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = create
                ? SqliteOpenMode.ReadWriteCreate
                : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void ConfigureCreatedDatabase(
        SqliteConnection connection
    ) {
        ExecutePragma(connection, "PRAGMA page_size = 4096;");
        ConfigureOpenedDatabase(connection);
        ExecutePragma(
            connection,
            $"PRAGMA application_id = {ApplicationId};"
        );
        ExecutePragma(
            connection,
            $"PRAGMA user_version = {SchemaVersion};"
        );
    }

    private static void ConfigureOpenedDatabase(
        SqliteConnection connection
    ) {
        ExecutePragma(connection, "PRAGMA foreign_keys = ON;");
        ExecutePragma(connection, "PRAGMA journal_mode = DELETE;");
        ExecutePragma(connection, "PRAGMA synchronous = EXTRA;");
        ExecutePragma(
            connection,
            $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};"
        );
        ExecutePragma(connection, "PRAGMA temp_store = MEMORY;");
        ExecutePragma(connection, "PRAGMA trusted_schema = OFF;");
        RequirePragmaInteger(connection, "page_size", 4096);
        RequirePragmaText(connection, "journal_mode", "delete");
        RequirePragmaInteger(connection, "synchronous", 3);
        RequirePragmaInteger(connection, "foreign_keys", 1);
        RequirePragmaInteger(
            connection,
            "busy_timeout",
            BusyTimeoutMilliseconds
        );
        RequirePragmaInteger(connection, "temp_store", 2);
        RequirePragmaInteger(connection, "trusted_schema", 0);
    }

    private static void CreateSchema(SqliteConnection connection) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE character_memory_meta (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                schema_version INTEGER NOT NULL CHECK(schema_version = 1),
                user_id TEXT NOT NULL,
                session_repository_id TEXT NOT NULL,
                capture_frontier_segment_number INTEGER NOT NULL
                    CHECK(capture_frontier_segment_number
                        BETWEEN 1 AND 4294967295),
                capture_frontier_tail_offset INTEGER NOT NULL
                    CHECK(capture_frontier_tail_offset >= 4
                        AND capture_frontier_tail_offset % 4 = 0),
                baseline_selected_head TEXT NULL,
                store_state TEXT NOT NULL CHECK(store_state IN (
                    'Provisioning', 'Ready', 'Quarantined'
                )),
                provision_target_pod_state_identity TEXT NOT NULL,
                settled_default_pod_state_identity TEXT NULL,
                active_source_action TEXT NULL,
                quarantine_code TEXT NULL,
                quarantine_observed_pod_state_identity TEXT NULL,
                store_revision INTEGER NOT NULL CHECK(store_revision >= 0),
                CHECK(
                    (store_state = 'Provisioning'
                        AND settled_default_pod_state_identity IS NULL
                        AND active_source_action IS NULL
                        AND quarantine_code IS NULL
                        AND quarantine_observed_pod_state_identity IS NULL)
                    OR (store_state = 'Ready'
                        AND settled_default_pod_state_identity IS NOT NULL
                        AND quarantine_code IS NULL
                        AND quarantine_observed_pod_state_identity IS NULL)
                    OR (store_state = 'Quarantined'
                        AND quarantine_code IS NOT NULL)
                )
            ) STRICT;

            CREATE TABLE note_action_capture (
                source_action_address TEXT NOT NULL PRIMARY KEY,
                visible_action_sha256 TEXT NOT NULL
                    CHECK(length(visible_action_sha256) = 64),
                visible_action_utf8_bytes INTEGER NOT NULL
                    CHECK(visible_action_utf8_bytes >= 0),
                extractor_contract_id TEXT NOT NULL,
                extraction_commitment TEXT NOT NULL
                    CHECK(length(extraction_commitment) = 64),
                artifact_count INTEGER NOT NULL
                    CHECK(artifact_count BETWEEN 0 AND 16),
                state TEXT NOT NULL CHECK(state IN (
                    'ZeroCaptured', 'Captured', 'Planned',
                    'Applied', 'Rejected'
                )),
                base_pod_state_identity TEXT NULL,
                target_pod_state_identity TEXT NULL,
                rejection_code TEXT NULL,
                state_revision INTEGER NOT NULL CHECK(state_revision >= 1),
                CHECK(
                    (state = 'ZeroCaptured' AND artifact_count = 0
                        AND base_pod_state_identity IS NULL
                        AND target_pod_state_identity IS NULL
                        AND rejection_code IS NULL)
                    OR (state = 'Captured' AND artifact_count > 0
                        AND base_pod_state_identity IS NULL
                        AND target_pod_state_identity IS NULL
                        AND rejection_code IS NULL)
                    OR (state IN ('Planned', 'Applied')
                        AND artifact_count > 0
                        AND base_pod_state_identity IS NOT NULL
                        AND target_pod_state_identity IS NOT NULL
                        AND rejection_code IS NULL)
                    OR (state = 'Rejected' AND artifact_count > 0
                        AND base_pod_state_identity IS NULL
                        AND target_pod_state_identity IS NULL
                        AND rejection_code IS NOT NULL)
                )
            ) STRICT;

            CREATE TABLE character_note (
                source_action_address TEXT NOT NULL
                    REFERENCES note_action_capture(source_action_address)
                    ON DELETE RESTRICT,
                artifact_ordinal INTEGER NOT NULL
                    CHECK(artifact_ordinal BETWEEN 0 AND 15),
                exact_text TEXT NOT NULL CHECK(length(exact_text) > 0),
                memo_id TEXT NULL,
                PRIMARY KEY(source_action_address, artifact_ordinal)
            ) STRICT;

            CREATE UNIQUE INDEX ux_character_note_memo_id
            ON character_note(memo_id) WHERE memo_id IS NOT NULL;

            CREATE UNIQUE INDEX ux_note_capture_single_active
            ON note_action_capture((1))
            WHERE state IN ('Captured', 'Planned');
            """;
        command.ExecuteNonQuery();
    }

    private static void InsertInitialState(
        SqliteConnection connection,
        CharacterMemoryStoreOwner owner,
        CharacterMemoryStoreBaseline baseline,
        string provisionTargetPodStateIdentity,
        CharacterMemoryStoreTestHooks hooks
    ) {
        using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO character_memory_meta(
                singleton, schema_version, user_id, session_repository_id,
                capture_frontier_segment_number,
                capture_frontier_tail_offset, baseline_selected_head,
                store_state, provision_target_pod_state_identity,
                settled_default_pod_state_identity, active_source_action,
                quarantine_code, quarantine_observed_pod_state_identity,
                store_revision
            ) VALUES (
                1, 1, $user, $repository, $segment, $tail, $head,
                'Provisioning', $target, NULL, NULL, NULL, NULL, 0
            );
            """;
        command.Parameters.AddWithValue("$user", owner.UserId);
        command.Parameters.AddWithValue(
            "$repository",
            owner.SessionRepositoryId
        );
        command.Parameters.AddWithValue(
            "$segment",
            baseline.CaptureFromPhysicalFrontier.SegmentNumber
        );
        command.Parameters.AddWithValue(
            "$tail",
            baseline.CaptureFromPhysicalFrontier.TailOffset
        );
        command.Parameters.AddWithValue(
            "$head",
            (object?)baseline.SelectedHead ?? DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$target",
            provisionTargetPodStateIdentity
        );
        _ = command.ExecuteNonQuery();
        hooks.BeforeCommit?.Invoke("create-initial-state");
        transaction.Commit();
        hooks.AfterCommitBeforeReturn?.Invoke("create-initial-state");
    }

    private static CharacterMemoryStatusSnapshot ValidateOpenedDatabase(
        SqliteConnection connection,
        CharacterMemoryStoreOwner owner
    ) {
        ValidateSchemaIdentity(connection);
        using (SqliteCommand integrity = connection.CreateCommand()) {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(
                    integrity.ExecuteScalar() as string,
                    "ok",
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "Character Memory SQLite integrity_check failed."
                );
            }
        }
        using (SqliteCommand foreignKeys = connection.CreateCommand()) {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = foreignKeys.ExecuteReader();
            if (reader.Read()) {
                throw new InvalidDataException(
                    "Character Memory SQLite foreign_key_check failed."
                );
            }
        }
        RequireOwner(connection, transaction: null, owner);
        CharacterMemoryStatusSnapshot status = ReadStatusCore(
            connection,
            transaction: null
        );
        ValidateAllCaptures(connection, status);
        return status;
    }

    private static void ValidateSchemaIdentity(SqliteConnection connection) {
        RequirePragmaInteger(connection, "application_id", ApplicationId);
        RequirePragmaInteger(connection, "user_version", SchemaVersion);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        using (SqliteCommand command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT type || ':' || name FROM sqlite_schema
                WHERE name NOT LIKE 'sqlite_%'
                  AND type IN ('table', 'index', 'trigger', 'view');
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) { actual.Add(reader.GetString(0)); }
        }
        string[] expected = [
            "table:character_memory_meta",
            "table:note_action_capture",
            "table:character_note",
            "index:ux_character_note_memo_id",
            "index:ux_note_capture_single_active",
        ];
        if (!actual.SetEquals(expected)) {
            throw new InvalidDataException(
                "Character Memory SQLite schema object set is not exact."
            );
        }
        RequireExactColumns(connection, "character_memory_meta", [
            "singleton", "schema_version", "user_id",
            "session_repository_id", "capture_frontier_segment_number",
            "capture_frontier_tail_offset", "baseline_selected_head",
            "store_state", "provision_target_pod_state_identity",
            "settled_default_pod_state_identity", "active_source_action",
            "quarantine_code", "quarantine_observed_pod_state_identity",
            "store_revision",
        ]);
        RequireExactColumns(connection, "note_action_capture", [
            "source_action_address", "visible_action_sha256",
            "visible_action_utf8_bytes", "extractor_contract_id",
            "extraction_commitment", "artifact_count", "state",
            "base_pod_state_identity", "target_pod_state_identity",
            "rejection_code", "state_revision",
        ]);
        RequireExactColumns(connection, "character_note", [
            "source_action_address", "artifact_ordinal", "exact_text",
            "memo_id",
        ]);
        foreach (string table in new[] {
            "character_memory_meta", "note_action_capture", "character_note"
        }) {
            RequireStrictTable(connection, table);
        }
        RequireExactForeignKeys(connection, "character_note", [
            "source_action_address->note_action_capture.source_action_address:RESTRICT"
        ]);
    }

    private static CharacterMemoryStatusSnapshot ReadStatusCore(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT user_id, session_repository_id,
                   capture_frontier_segment_number,
                   capture_frontier_tail_offset, baseline_selected_head,
                   store_state, provision_target_pod_state_identity,
                   settled_default_pod_state_identity,
                   active_source_action, quarantine_code,
                   quarantine_observed_pod_state_identity, store_revision
            FROM character_memory_meta WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw Corrupt("Character Memory meta row is absent.");
        }
        var owner = new CharacterMemoryStoreOwner(
            reader.GetString(0),
            reader.GetString(1)
        );
        var baseline = new CharacterMemoryStoreBaseline(
            new EventJournalPhysicalAppendFrontier(
                checked((uint)reader.GetInt64(2)),
                reader.GetInt64(3)
            ),
            reader.IsDBNull(4) ? null : reader.GetString(4)
        );
        CharacterMemoryStoreState state = ParseStoreState(
            reader.GetString(5)
        );
        string provisionTarget = reader.GetString(6);
        string? settled = reader.IsDBNull(7) ? null : reader.GetString(7);
        string? activeSource = reader.IsDBNull(8)
            ? null
            : reader.GetString(8);
        string? quarantineCode = reader.IsDBNull(9)
            ? null
            : reader.GetString(9);
        string? observed = reader.IsDBNull(10)
            ? null
            : reader.GetString(10);
        long revision = reader.GetInt64(11);
        if (reader.Read()) { throw Corrupt("Character Memory meta is not singleton."); }
        reader.Close();

        ValidateOwner(owner);
        ValidateBaseline(baseline);
        RequirePodStateIdentity(provisionTarget, "provision target identity");
        if (settled is not null) {
            RequirePodStateIdentity(settled, "settled pod identity");
        }
        if (activeSource is not null) {
            RequireEventAddress(activeSource, "active source Action");
        }
        if (quarantineCode is not null) {
            RequireCode(quarantineCode, "quarantine code");
        }
        if (observed is not null) {
            RequirePodStateIdentity(observed, "observed pod identity");
        }
        CharacterMemoryCaptureSnapshot? active = activeSource is null
            ? null
            : ReadCaptureCore(connection, transaction, activeSource)
                ?? throw Corrupt("Active source Action has no capture row.");
        ValidateStatusShape(
            state,
            settled,
            activeSource,
            quarantineCode,
            observed,
            active
        );
        return new CharacterMemoryStatusSnapshot(
            owner,
            baseline,
            state,
            provisionTarget,
            settled,
            activeSource,
            quarantineCode,
            observed,
            revision,
            active
        );
    }

    private static CharacterMemoryCaptureSnapshot? ReadCaptureCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sourceActionAddress
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT visible_action_sha256, visible_action_utf8_bytes,
                   extractor_contract_id, extraction_commitment,
                   artifact_count, state, base_pod_state_identity,
                   target_pod_state_identity, rejection_code, state_revision
            FROM note_action_capture
            WHERE source_action_address = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceActionAddress);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        string visibleHash = reader.GetString(0);
        int visibleBytes = reader.GetInt32(1);
        string contract = reader.GetString(2);
        string commitment = reader.GetString(3);
        int artifactCount = reader.GetInt32(4);
        CharacterMemoryCaptureState state = ParseCaptureState(
            reader.GetString(5)
        );
        string? baseIdentity = reader.IsDBNull(6) ? null : reader.GetString(6);
        string? targetIdentity = reader.IsDBNull(7) ? null : reader.GetString(7);
        string? rejectionCode = reader.IsDBNull(8) ? null : reader.GetString(8);
        long stateRevision = reader.GetInt64(9);
        if (reader.Read()) { throw Corrupt("Capture source is not unique."); }
        reader.Close();

        var notes = new List<CharacterMemoryNoteSnapshot>();
        using SqliteCommand children = connection.CreateCommand();
        children.Transaction = transaction;
        children.CommandText = """
            SELECT artifact_ordinal, exact_text, memo_id
            FROM character_note WHERE source_action_address = $source
            ORDER BY artifact_ordinal;
            """;
        children.Parameters.AddWithValue("$source", sourceActionAddress);
        using SqliteDataReader noteReader = children.ExecuteReader();
        while (noteReader.Read()) {
            int ordinal = noteReader.GetInt32(0);
            if (ordinal != notes.Count) {
                throw Corrupt("Character Note ordinals are not contiguous.");
            }
            notes.Add(new CharacterMemoryNoteSnapshot(
                ordinal,
                noteReader.GetString(1),
                noteReader.IsDBNull(2) ? null : noteReader.GetString(2)
            ));
        }
        var snapshot = new CharacterMemoryCaptureSnapshot(
            sourceActionAddress,
            visibleHash,
            visibleBytes,
            contract,
            commitment,
            artifactCount,
            state,
            baseIdentity,
            targetIdentity,
            rejectionCode,
            stateRevision,
            Array.AsReadOnly(notes.ToArray())
        );
        ValidateCaptureSnapshot(snapshot);
        return snapshot;
    }

    private static void ValidateAllCaptures(
        SqliteConnection connection,
        CharacterMemoryStatusSnapshot status
    ) {
        var sources = new List<string>();
        using (SqliteCommand command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT source_action_address FROM note_action_capture
                ORDER BY source_action_address;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) { sources.Add(reader.GetString(0)); }
        }
        int activeCount = 0;
        foreach (string source in sources) {
            CharacterMemoryCaptureSnapshot capture = ReadCaptureCore(
                connection,
                transaction: null,
                source
            ) ?? throw Corrupt("Enumerated capture disappeared.");
            if (capture.State is CharacterMemoryCaptureState.Captured
                    or CharacterMemoryCaptureState.Planned) {
                activeCount++;
                if (!string.Equals(
                        source,
                        status.ActiveSourceAction,
                        StringComparison.Ordinal)) {
                    throw Corrupt("Active capture does not match meta.");
                }
            }
        }
        if (activeCount != (status.ActiveSourceAction is null ? 0 : 1)) {
            throw Corrupt("Character Memory active capture count is invalid.");
        }
    }

    private static void ValidateCaptureSnapshot(
        CharacterMemoryCaptureSnapshot capture
    ) {
        RequireEventAddress(capture.SourceActionAddress, "capture source");
        RequireSha256(capture.VisibleActionSha256, "visible Action hash");
        if (capture.VisibleActionUtf8Bytes is < 0
            or > TextExtractorBounds.MaximumTargetTextUtf8Bytes) {
            throw Corrupt("Visible Action byte count is invalid.");
        }
        RequireBoundedText(capture.ExtractorContractId, "extractor contract");
        RequireSha256(capture.ExtractionCommitment, "extraction commitment");
        if (capture.ArtifactCount != capture.Notes.Count
            || capture.ArtifactCount is < 0
                or > CharacterNoteBounds.MaximumIntentCount) {
            throw Corrupt("Capture artifact count is invalid.");
        }
        int totalBytes = 0;
        foreach (CharacterMemoryNoteSnapshot note in capture.Notes) {
            int bytes = RequireExactText(note.ExactText, "stored exact text");
            totalBytes = checked(totalBytes + bytes);
            if (note.MemoId is not null) {
                RequireMemoId(note.MemoId, "stored MemoId");
            }
        }
        if (totalBytes > CharacterNoteBounds.MaximumTotalExactTextUtf8Bytes) {
            throw Corrupt("Capture exact-text total is invalid.");
        }
        string recomputed = ComputeExtractionCommitment(
            capture.SourceActionAddress,
            capture.VisibleActionSha256,
            capture.VisibleActionUtf8Bytes,
            capture.ExtractorContractId,
            capture.Notes.Select(static value => value.ExactText).ToArray()
        );
        if (!string.Equals(
                recomputed,
                capture.ExtractionCommitment,
                StringComparison.Ordinal)) {
            throw Corrupt("Capture extraction commitment does not match rows.");
        }
        bool allMemoIdsNull = capture.Notes.All(static note => note.MemoId is null);
        bool allMemoIdsPresent = capture.Notes.All(static note => note.MemoId is not null);
        switch (capture.State) {
            case CharacterMemoryCaptureState.ZeroCaptured
                when capture.ArtifactCount == 0
                    && capture.BasePodStateIdentity is null
                    && capture.TargetPodStateIdentity is null
                    && capture.RejectionCode is null:
            case CharacterMemoryCaptureState.Captured
                when capture.ArtifactCount > 0 && allMemoIdsNull
                    && capture.BasePodStateIdentity is null
                    && capture.TargetPodStateIdentity is null
                    && capture.RejectionCode is null:
            case CharacterMemoryCaptureState.Rejected
                when capture.ArtifactCount > 0 && allMemoIdsNull
                    && capture.BasePodStateIdentity is null
                    && capture.TargetPodStateIdentity is null
                    && capture.RejectionCode is not null:
                break;
            case CharacterMemoryCaptureState.Planned
                or CharacterMemoryCaptureState.Applied
                when capture.ArtifactCount > 0 && allMemoIdsPresent
                    && capture.BasePodStateIdentity is not null
                    && capture.TargetPodStateIdentity is not null
                    && capture.RejectionCode is null:
                RequirePodStateIdentity(capture.BasePodStateIdentity, "base identity");
                RequirePodStateIdentity(capture.TargetPodStateIdentity, "target identity");
                if (string.Equals(capture.BasePodStateIdentity,
                        capture.TargetPodStateIdentity, StringComparison.Ordinal)) {
                    throw Corrupt("Planned Pod identities must differ.");
                }
                break;
            default:
                throw Corrupt("Capture state shape is invalid.");
        }
        if (capture.RejectionCode is not null) {
            RequireCode(capture.RejectionCode, "rejection code");
        }
    }

    private static void ValidateStatusShape(
        CharacterMemoryStoreState state,
        string? settled,
        string? activeSource,
        string? quarantineCode,
        string? observed,
        CharacterMemoryCaptureSnapshot? active
    ) {
        bool valid = state switch {
            CharacterMemoryStoreState.Provisioning => settled is null
                && activeSource is null && quarantineCode is null
                && observed is null && active is null,
            CharacterMemoryStoreState.Ready => settled is not null
                && quarantineCode is null && observed is null
                && ((activeSource is null && active is null)
                    || (activeSource is not null && active is not null
                        && active.State is CharacterMemoryCaptureState.Captured
                            or CharacterMemoryCaptureState.Planned)),
            CharacterMemoryStoreState.Quarantined => quarantineCode is not null,
            _ => false,
        };
        if (!valid) { throw Corrupt("Character Memory meta state shape is invalid."); }
    }

    private static void RequireOwner(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CharacterMemoryStoreOwner expected
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT schema_version, user_id, session_repository_id
            FROM character_memory_meta WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()
            || reader.GetInt32(0) != SchemaVersion
            || !string.Equals(reader.GetString(1), expected.UserId,
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), expected.SessionRepositoryId,
                StringComparison.Ordinal)
            || reader.Read()) {
            throw new InvalidDataException(
                "Character Memory store owner identity does not match."
            );
        }
    }

    private static void ValidateOwner(CharacterMemoryStoreOwner owner) {
        ArgumentNullException.ThrowIfNull(owner);
        RequireBoundedText(owner.UserId, nameof(owner.UserId));
        RequireBoundedText(
            owner.SessionRepositoryId,
            nameof(owner.SessionRepositoryId)
        );
    }

    private static void ValidateBaseline(CharacterMemoryStoreBaseline baseline) {
        ArgumentNullException.ThrowIfNull(baseline);
        EventJournalPhysicalAppendFrontier frontier;
        try {
            frontier = new EventJournalPhysicalAppendFrontier(
                baseline.CaptureFromPhysicalFrontier.SegmentNumber,
                baseline.CaptureFromPhysicalFrontier.TailOffset
            );
        }
        catch (ArgumentOutOfRangeException exception) {
            throw new ArgumentException(
                "Character Memory capture frontier is invalid.",
                nameof(baseline),
                exception
            );
        }
        if (baseline.SelectedHead is { } selectedHead) {
            RequireEventAddress(selectedHead, nameof(baseline.SelectedHead));
            if (!frontier.Contains(EventAddressTextCodec.Parse(selectedHead))) {
                throw new ArgumentException(
                    "Baseline selected head must be contained by the capture frontier.",
                    nameof(baseline)
                );
            }
        }
    }

    internal static string ComputeExtractionCommitment(
        string sourceActionAddress,
        string visibleActionSha256,
        int visibleActionUtf8Bytes,
        string extractorContractId,
        IReadOnlyList<string> exactTexts
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendCommitmentPart(hash, CommitmentVersion);
        AppendCommitmentPart(hash, sourceActionAddress);
        AppendCommitmentPart(hash, visibleActionSha256);
        Span<byte> byteCount = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(byteCount, visibleActionUtf8Bytes);
        hash.AppendData(byteCount);
        AppendCommitmentPart(hash, extractorContractId);
        foreach (string exactText in exactTexts) {
            AppendCommitmentPart(hash, exactText);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendCommitmentPart(
        IncrementalHash hash,
        string value
    ) {
        byte[] bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static int RequireExactText(string? value, string parameter) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException("ExactText must not be blank.", parameter);
        }
        int bytes;
        try { bytes = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException("ExactText must be strict Unicode.", parameter, exception);
        }
        if (bytes > CharacterNoteBounds.MaximumExactTextUtf8Bytes) {
            throw new ArgumentOutOfRangeException(parameter);
        }
        return bytes;
    }

    private static void RequireBoundedText(string? value, string parameter) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException($"{parameter} must not be blank.", parameter);
        }
        try {
            if (StrictUtf8.GetByteCount(value)
                    > CharacterMemoryStoreBounds.MaximumIdentityUtf8Bytes) {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException($"{parameter} must be strict Unicode.", parameter, exception);
        }
    }

    private static void RequirePodStateIdentity(string? value, string parameter) {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl)) {
            throw new ArgumentException("Pod state identity is not canonical.", parameter);
        }
        try {
            if (StrictUtf8.GetByteCount(value)
                    > CharacterMemoryStoreBounds.MaximumPodStateIdentityUtf8Bytes) {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException("Pod state identity must be strict Unicode.", parameter, exception);
        }
    }

    private static void RequireCode(string? value, string parameter) {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl)) {
            throw new ArgumentException("Character Memory code is not canonical.", parameter);
        }
        try {
            if (StrictUtf8.GetByteCount(value)
                    > CharacterMemoryStoreBounds.MaximumCodeUtf8Bytes) {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException("Character Memory code must be strict Unicode.", parameter, exception);
        }
    }

    private static void RequireSha256(string? value, string parameter) {
        if (value is not { Length: 64 }
            || value.Any(static character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f'))) {
            throw new ArgumentException("SHA-256 must be canonical lowercase hex.", parameter);
        }
    }

    private static void RequireMemoId(string? value, string parameter) {
        if (value is null || value.Length != 11
            || !value.StartsWith("m1:", StringComparison.Ordinal)
            || !IsLowerHex(value.AsSpan(3))
            || IsAllZero(value.AsSpan(3))) {
            throw new ArgumentException("MemoId is not canonical.", parameter);
        }
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value) {
        foreach (char character in value) {
            if (character is >= '0' and <= '9'
                or >= 'a' and <= 'f') {
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<char> value) {
        foreach (char character in value) {
            if (character != '0') { return false; }
        }
        return true;
    }

    private static void RequireEventAddress(string value, string parameter) {
        if (!EventAddressTextCodec.TryParse(value, out _)) {
            throw new ArgumentException("EventAddress is not canonical.", parameter);
        }
    }

    private static CharacterMemoryStoreState ParseStoreState(string value) =>
        value switch {
            "Provisioning" => CharacterMemoryStoreState.Provisioning,
            "Ready" => CharacterMemoryStoreState.Ready,
            "Quarantined" => CharacterMemoryStoreState.Quarantined,
            _ => throw Corrupt("Unknown Character Memory store state."),
        };

    private static CharacterMemoryCaptureState ParseCaptureState(string value) =>
        value switch {
            "ZeroCaptured" => CharacterMemoryCaptureState.ZeroCaptured,
            "Captured" => CharacterMemoryCaptureState.Captured,
            "Planned" => CharacterMemoryCaptureState.Planned,
            "Applied" => CharacterMemoryCaptureState.Applied,
            "Rejected" => CharacterMemoryCaptureState.Rejected,
            _ => throw Corrupt("Unknown Character Memory capture state."),
        };

    private static void RequireExactColumns(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> expected
    ) {
        var actual = new List<string>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) { actual.Add(reader.GetString(1)); }
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) {
            throw Corrupt($"Table '{table}' columns are not exact.");
        }
    }

    private static void RequireStrictTable(
        SqliteConnection connection,
        string table
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, strict FROM pragma_table_list
            WHERE schema = 'main' AND name = $table;
            """;
        command.Parameters.AddWithValue("$table", table);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != "table"
            || reader.GetInt32(1) != 1 || reader.Read()) {
            throw Corrupt($"Table '{table}' is not exact STRICT schema.");
        }
    }

    private static void RequireExactForeignKeys(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> expected
    ) {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{table}');";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            actual.Add(reader.GetString(3) + "->" + reader.GetString(2)
                + "." + reader.GetString(4) + ":" + reader.GetString(6));
        }
        if (!actual.SetEquals(expected)) {
            throw Corrupt($"Table '{table}' foreign keys are not exact.");
        }
    }

    private static void ExecutePragma(SqliteConnection connection, string sql) {
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
        if (Convert.ToInt64(command.ExecuteScalar()) != expected) {
            throw Corrupt($"SQLite PRAGMA {name} is not exact.");
        }
    }

    private static void RequirePragmaText(
        SqliteConnection connection,
        string name,
        string expected
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        if (!string.Equals(command.ExecuteScalar() as string, expected,
                StringComparison.Ordinal)) {
            throw Corrupt($"SQLite PRAGMA {name} is not exact.");
        }
    }

    private static FileStream AcquireLifetimeLock(
        string storeDirectory,
        FileMode mode
    ) => new(
        Path.Combine(storeDirectory, LockFileName),
        mode,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 1,
        FileOptions.WriteThrough
    );

    private static void RequireExistingAncestorsNoReparse(string path) {
        string? current = path;
        while (current is not null) {
            if (Path.Exists(current)) {
                RejectReparsePoint(current, "Character Memory store path");
            }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void RejectReparsePoint(string path, string kind) {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) {
            throw new InvalidDataException(
                $"{kind} must not be a symbolic link or reparse point: {path}"
            );
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static InvalidDataException Corrupt(string message) => new(message);
}
