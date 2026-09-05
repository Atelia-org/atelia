using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

/// <summary>
/// Explicitly constructed durable delegation current-state authority.
/// </summary>
internal sealed partial class GalateaDelegationSqliteStore : IDisposable {
    internal const int SchemaVersion = 1;
    internal const int ApplicationId = 0x47444C47; // "GDLG"
    internal const string DatabaseFileName = "delegation-state.sqlite3";
    internal const string LockFileName = "delegation-state.lock";

    private const int BusyTimeoutMilliseconds = 1_000;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly string _storeDirectory;
    private readonly string _databasePath;
    private readonly GalateaDelegationStoreOwner _owner;
    private readonly GalateaDelegationStoreBaseline _baseline;
    private readonly GalateaDelegationStoreLimits _limits;
    private readonly GalateaDelegationStoreTestHooks _hooks;
    private readonly FileStream _lifetimeLock;
    private readonly bool _readOnly;
    private readonly object _gate = new();
    private bool _disposed;

    private GalateaDelegationSqliteStore(
        string storeDirectory,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreBaseline baseline,
        GalateaDelegationStoreLimits limits,
        GalateaDelegationStoreTestHooks hooks,
        FileStream lifetimeLock,
        bool readOnly
    ) {
        _storeDirectory = storeDirectory;
        _databasePath = Path.Combine(storeDirectory, DatabaseFileName);
        _owner = owner;
        _baseline = baseline;
        _limits = limits;
        _hooks = hooks;
        _lifetimeLock = lifetimeLock;
        _readOnly = readOnly;
    }

    internal string StoreDirectory => _storeDirectory;
    internal GalateaDelegationStoreBaseline Baseline => _baseline;

    internal static GalateaDelegationSqliteStore CreateNew(
        string storeDirectory,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreBaseline baseline,
        GalateaDelegationStoreLimits limits,
        GalateaDelegationStoreTestHooks? hooks = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        GalateaDelegationDurableFiles.RequireLinux();
        ValidateOwner(owner);
        ValidateBaseline(baseline);
        ValidateLimits(limits);
        string fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(storeDirectory)
        );
        RequireExistingAncestorsNoReparse(fullPath);
        if (Path.Exists(fullPath)) {
            throw new IOException(
                $"Delegation store path already exists: {fullPath}"
            );
        }

        GalateaDelegationDurableFiles.CreateDirectoryNew(fullPath);
        FileStream? lifetimeLock = null;
        try {
            RejectReparsePoint(fullPath, "delegation store directory");
            lifetimeLock = AcquireLifetimeLock(fullPath, FileMode.CreateNew);
            GalateaDelegationDurableFiles.FlushDirectory(
                Path.GetDirectoryName(fullPath)
                    ?? throw new InvalidOperationException(
                        "Delegation store has no parent directory."
                    )
            );
            string databasePath = Path.Combine(fullPath, DatabaseFileName);
            using SqliteConnection connection = OpenConnection(
                databasePath,
                create: true
            );
            ConfigureCreatedDatabase(connection);
            CreateSchema(connection);
            InsertInitialState(connection, owner, baseline, limits);
            _ = ValidateOpenedDatabase(connection, owner, limits);
            GalateaDelegationDurableFiles.FlushDirectory(fullPath);
            return new GalateaDelegationSqliteStore(
                fullPath,
                owner,
                baseline,
                limits,
                hooks ?? GalateaDelegationStoreTestHooks.None,
                lifetimeLock,
                readOnly: false
            );
        }
        catch {
            lifetimeLock?.Dispose();
            // A failed baseline create leaves its owned candidate directory
            // for explicit inspection. Deleting after releasing the lifetime
            // lock would race a strict opener that acquired the same path.
            throw;
        }
    }

    internal static GalateaDelegationSqliteStore OpenExisting(
        string storeDirectory,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits,
        GalateaDelegationStoreTestHooks? hooks = null
    ) => OpenExistingCore(
        storeDirectory,
        owner,
        limits,
        readOnly: false,
        hooks
    );

    internal static GalateaDelegationSqliteStore OpenExistingReadOnly(
        string storeDirectory,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits
    ) => OpenExistingCore(
        storeDirectory,
        owner,
        limits,
        readOnly: true,
        hooks: null
    );

    private static GalateaDelegationSqliteStore OpenExistingCore(
        string storeDirectory,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits,
        bool readOnly,
        GalateaDelegationStoreTestHooks? hooks
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        GalateaDelegationDurableFiles.RequireLinux();
        ValidateOwner(owner);
        ValidateLimits(limits);
        string fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(storeDirectory)
        );
        RequireExistingAncestorsNoReparse(fullPath);
        RejectReparsePoint(fullPath, "delegation store directory");
        if (!Directory.Exists(fullPath)) {
            throw new DirectoryNotFoundException(
                $"Delegation store directory was not found: {fullPath}"
            );
        }

        RejectReparsePoint(
            Path.Combine(fullPath, LockFileName),
            "delegation lifetime lock"
        );
        FileStream lifetimeLock = AcquireLifetimeLock(
            fullPath,
            FileMode.Open
        );
        try {
            string databasePath = Path.Combine(fullPath, DatabaseFileName);
            RejectReparsePoint(databasePath, "delegation database");
            using SqliteConnection connection = OpenConnection(
                databasePath,
                create: false,
                readOnly
            );
            ConfigureOpenedDatabase(connection, readOnly);
            GalateaDelegationStateSnapshot snapshot =
                ValidateOpenedDatabase(connection, owner, limits);
            return new GalateaDelegationSqliteStore(
                fullPath,
                owner,
                snapshot.Baseline,
                limits,
                hooks ?? GalateaDelegationStoreTestHooks.None,
                lifetimeLock,
                readOnly
            );
        }
        catch {
            lifetimeLock.Dispose();
            throw;
        }
    }

    internal GalateaDelegationStateSnapshot ReadSnapshot() {
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: true);
            GalateaDelegationStateSnapshot snapshot = ReadSnapshotCore(
                connection,
                transaction
            );
            transaction.Commit();
            return snapshot;
        }
    }

    /// <summary>
    /// Reads the public mailbox progress projection in one deferred read
    /// transaction. The query never selects message content, routing
    /// identities, operation ids, thread ids, turn ids, or hashes.
    /// </summary>
    internal GalateaMailboxStatusProjection ReadMailboxStatus() {
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: true);
            GalateaMailboxStatusAggregate aggregate =
                ReadMailboxStatusAggregate(connection, transaction);
            GalateaMailboxStatusProjection projection =
                ProjectMailboxStatus(aggregate);
            transaction.Commit();
            return projection;
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
        RejectReparsePoint(_databasePath, "delegation database");
        SqliteConnection connection = OpenConnection(
            _databasePath,
            create: false,
            _readOnly
        );
        try {
            ConfigureOpenedDatabase(connection, _readOnly);
            ValidateSchemaIdentity(connection);
            RequireOwner(
                connection,
                transaction: null,
                _owner,
                _limits
            );
            return connection;
        }
        catch {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenConnection(
        string databasePath,
        bool create,
        bool readOnly = false
    ) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = create
                ? SqliteOpenMode.ReadWriteCreate
                : readOnly
                    ? SqliteOpenMode.ReadOnly
                    : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void ConfigureCreatedDatabase(
        SqliteConnection connection
    ) {
        ExecutePragma(connection, "PRAGMA page_size = 4096;");
        ConfigureOpenedDatabase(connection, readOnly: false);
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
        SqliteConnection connection,
        bool readOnly
    ) {
        ExecutePragma(connection, "PRAGMA foreign_keys = ON;");
        if (!readOnly) {
            ExecutePragma(connection, "PRAGMA journal_mode = DELETE;");
            ExecutePragma(connection, "PRAGMA synchronous = EXTRA;");
        }
        ExecutePragma(
            connection,
            $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};"
        );
        ExecutePragma(connection, "PRAGMA temp_store = MEMORY;");
        ExecutePragma(connection, "PRAGMA trusted_schema = OFF;");
        if (readOnly) {
            ExecutePragma(connection, "PRAGMA query_only = ON;");
        }
        RequirePragmaInteger(connection, "page_size", 4096);
        RequirePragmaText(connection, "journal_mode", "delete");
        if (!readOnly) {
            RequirePragmaInteger(connection, "synchronous", 3);
        }
        RequirePragmaInteger(connection, "foreign_keys", 1);
        RequirePragmaInteger(
            connection,
            "busy_timeout",
            BusyTimeoutMilliseconds
        );
        RequirePragmaInteger(connection, "temp_store", 2);
        RequirePragmaInteger(connection, "trusted_schema", 0);
        RequirePragmaInteger(connection, "query_only", readOnly ? 1 : 0);
    }

    private static GalateaDelegationStateSnapshot ValidateOpenedDatabase(
        SqliteConnection connection,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits
    ) {
        ValidateSchemaIdentity(connection);
        using (SqliteCommand integrity = connection.CreateCommand()) {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(
                    integrity.ExecuteScalar() as string,
                    "ok",
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "Delegation SQLite integrity_check failed."
                );
            }
        }
        using (SqliteCommand foreignKeys = connection.CreateCommand()) {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = foreignKeys.ExecuteReader();
            if (reader.Read()) {
                throw new InvalidDataException(
                    "Delegation SQLite foreign_key_check failed."
                );
            }
        }
        RequireOwner(connection, transaction: null, owner, limits);
        return ReadSnapshotCore(connection, transaction: null);
    }

    private static void ValidateSchemaIdentity(SqliteConnection connection) {
        RequirePragmaInteger(connection, "application_id", ApplicationId);
        RequirePragmaInteger(connection, "user_version", SchemaVersion);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        using (SqliteCommand command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT type || ':' || name
                FROM sqlite_schema
                WHERE name NOT LIKE 'sqlite_%'
                  AND type IN ('table', 'index', 'trigger', 'view')
                ORDER BY type, name;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                actual.Add(reader.GetString(0));
            }
        }
        string[] expected = [
            "table:delegation_meta",
            "table:action_capture",
            "table:outbound_mail",
            "table:route_binding",
            "table:reply_notice",
            "table:reply_lease",
            "table:reply_lease_item",
            "index:ux_action_capture_sequence",
            "index:ux_outbound_source_ordinal",
            "index:ux_reply_notice_completion",
            "index:ux_reply_lease_one_active",
            "index:ux_reply_lease_item_notice"
        ];
        if (!actual.SetEquals(expected)) {
            throw new InvalidDataException(
                "Delegation SQLite schema object set is not exact."
            );
        }
        RequireExactColumns(connection, "delegation_meta", [
            "singleton", "schema_version", "user_id",
            "session_repository_id", "capture_frontier_segment_number",
            "capture_frontier_tail_offset",
            "baseline_selected_head", "route_policy_fingerprint",
            "maximum_queued_mails", "maximum_task_utf8_bytes",
            "maximum_reply_utf8_bytes", "maximum_inbox_replies",
            "maximum_inbox_utf8_bytes", "next_completion_sequence",
            "revision"
        ]);
        RequireExactColumns(connection, "action_capture", [
            "source_action_address", "capture_sequence",
            "visible_action_sha256",
            "visible_action_utf8_bytes", "extractor_contract_id",
            "artifact_count", "revision"
        ]);
        RequireExactColumns(connection, "outbound_mail", [
            "dispatch_id", "source_action_address", "artifact_ordinal",
            "recipient", "subject", "body", "in_reply_to_message_id",
            "evidence_quote", "route_class",
            "frozen_route_policy_fingerprint", "state", "operation_id",
            "requested_thread_id", "accepted_thread_id",
            "accepted_turn_id", "terminal_final_sha256", "terminal_stage",
            "terminal_code", "reconcile_attempt_count",
            "reconcile_last_code", "next_reconcile_at_ms", "revision"
        ]);
        RequireExactColumns(connection, "route_binding", [
            "singleton", "state", "binding_operation_id", "thread_id",
            "policy_fingerprint", "active_dispatch_id",
            "quarantine_code", "ensure_attempt_count", "ensure_last_code",
            "next_ensure_at_ms", "revision"
        ]);
        RequireExactColumns(connection, "reply_notice", [
            "notice_id", "dispatch_id", "kind", "body", "stage", "code",
            "completion_sequence", "state", "consumed_action_address",
            "revision"
        ]);
        RequireExactColumns(connection, "reply_lease", [
            "lease_id", "state", "active_slot", "player_text",
            "expected_session_head", "rendered_observation",
            "observation_utf8_bytes", "observation_sha256",
            "completion_frontier", "observation_address",
            "revision"
        ]);
        RequireExactColumns(connection, "reply_lease_item", [
            "lease_id", "ordinal", "notice_id"
        ]);
        foreach (string table in new[] {
            "delegation_meta", "action_capture", "outbound_mail",
            "route_binding", "reply_notice", "reply_lease",
            "reply_lease_item"
        }) {
            RequireStrictTable(connection, table);
        }
        RequireExactIndexColumns(connection, "ux_action_capture_sequence",
            ["capture_sequence"]);
        RequireExactIndexColumns(connection, "ux_outbound_source_ordinal",
            ["source_action_address", "artifact_ordinal"]);
        RequireExactIndexColumns(connection, "ux_reply_notice_completion",
            ["completion_sequence"]);
        RequireExactIndexColumns(connection, "ux_reply_lease_one_active",
            ["active_slot"]);
        RequireExactIndexColumns(connection, "ux_reply_lease_item_notice",
            ["notice_id"]);
        RequireExactForeignKeys(connection, "outbound_mail", [
            "source_action_address->action_capture.source_action_address:RESTRICT"
        ]);
        RequireExactForeignKeys(connection, "route_binding", [
            "active_dispatch_id->outbound_mail.dispatch_id:RESTRICT"
        ]);
        RequireExactForeignKeys(connection, "reply_notice", [
            "dispatch_id->outbound_mail.dispatch_id:RESTRICT"
        ]);
        RequireExactForeignKeys(connection, "reply_lease", []);
        RequireExactForeignKeys(connection, "reply_lease_item", [
            "lease_id->reply_lease.lease_id:RESTRICT",
            "notice_id->reply_notice.notice_id:RESTRICT"
        ]);
    }

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
            throw new InvalidDataException(
                $"Delegation SQLite table '{table}' columns are not exact."
            );
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
        if (!reader.Read()
            || !string.Equals(reader.GetString(0), "table",
                StringComparison.Ordinal)
            || reader.GetInt32(1) != 1
            || reader.Read()) {
            throw new InvalidDataException(
                $"Delegation SQLite table '{table}' is not exact STRICT schema."
            );
        }
    }

    private static void RequireExactIndexColumns(
        SqliteConnection connection,
        string index,
        IReadOnlyList<string> expected
    ) {
        var actual = new List<string>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info('{index}');";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) { actual.Add(reader.GetString(2)); }
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) {
            throw new InvalidDataException(
                $"Delegation SQLite index '{index}' columns are not exact."
            );
        }
        using SqliteCommand definition = connection.CreateCommand();
        definition.CommandText = """
            SELECT sql FROM sqlite_schema
            WHERE type = 'index' AND name = $index;
            """;
        definition.Parameters.AddWithValue("$index", index);
        string? sql = definition.ExecuteScalar() as string;
        if (sql is null
            || !sql.StartsWith("CREATE UNIQUE INDEX ",
                StringComparison.Ordinal)
            || (string.Equals(index, "ux_reply_lease_one_active",
                    StringComparison.Ordinal)
                && !sql.EndsWith("WHERE active_slot IS NOT NULL",
                    StringComparison.Ordinal))) {
            throw new InvalidDataException(
                $"Delegation SQLite index '{index}' definition is not exact."
            );
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
            actual.Add(
                reader.GetString(3) + "->" + reader.GetString(2) + "."
                + reader.GetString(4) + ":" + reader.GetString(6)
            );
        }
        if (!actual.SetEquals(expected)) {
            throw new InvalidDataException(
                $"Delegation SQLite table '{table}' foreign keys are not exact."
            );
        }
    }

    private static void RequireOwner(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        GalateaDelegationStoreOwner expected,
        GalateaDelegationStoreLimits expectedLimits
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT schema_version, user_id, session_repository_id,
                   route_policy_fingerprint, maximum_queued_mails,
                   maximum_task_utf8_bytes, maximum_reply_utf8_bytes,
                   maximum_inbox_replies, maximum_inbox_utf8_bytes
            FROM delegation_meta
            WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()
            || reader.GetInt32(0) != SchemaVersion
            || !string.Equals(reader.GetString(1), expected.UserId,
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2),
                expected.SessionRepositoryId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3),
                expected.RoutePolicyFingerprint,
                StringComparison.Ordinal)
            || reader.GetInt32(4) != expectedLimits.MaximumQueuedMails
            || reader.GetInt32(5) != expectedLimits.MaximumTaskUtf8Bytes
            || reader.GetInt32(6) != expectedLimits.MaximumReplyUtf8Bytes
            || reader.GetInt32(7) != expectedLimits.MaximumInboxReplies
            || reader.GetInt32(8) != expectedLimits.MaximumInboxUtf8Bytes
            || reader.Read()) {
            throw new InvalidDataException(
                "Delegation store owner identity or limits do not match."
            );
        }
    }

    private static void ExecutePragma(
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
        long actual = Convert.ToInt64(command.ExecuteScalar());
        if (actual != expected) {
            throw new InvalidDataException(
                $"Delegation SQLite PRAGMA {name} was {actual}, expected {expected}."
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
        string? actual = command.ExecuteScalar() as string;
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"Delegation SQLite PRAGMA {name} was '{actual}', expected '{expected}'."
            );
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

    private static void ValidateOwner(
        GalateaDelegationStoreOwner owner
    ) {
        ArgumentNullException.ThrowIfNull(owner);
        RequireBoundedText(owner.UserId, nameof(owner.UserId));
        RequireBoundedText(
            owner.SessionRepositoryId,
            nameof(owner.SessionRepositoryId)
        );
        RequireRoutePolicyFingerprint(
            owner.RoutePolicyFingerprint,
            nameof(owner.RoutePolicyFingerprint)
        );
    }

    private static void ValidateBaseline(
        GalateaDelegationStoreBaseline baseline
    ) {
        ArgumentNullException.ThrowIfNull(baseline);
        Atelia.EventJournal.EventJournalPhysicalAppendFrontier frontier;
        try {
            frontier = new(
                baseline.CaptureFromPhysicalFrontier.SegmentNumber,
                baseline.CaptureFromPhysicalFrontier.TailOffset
            );
        }
        catch (ArgumentOutOfRangeException exception) {
            throw new ArgumentException(
                "CaptureFromPhysicalFrontier is invalid.",
                nameof(baseline),
                exception
            );
        }
        if (baseline.SelectedHead is { } selectedHead) {
            RequireEventAddress(selectedHead, nameof(baseline.SelectedHead));
            Atelia.EventJournal.EventAddress address =
                Atelia.SessionJournal.EventAddressTextCodec.Parse(
                    selectedHead
                );
            if (!frontier.Contains(address)) {
                throw new ArgumentException(
                    "SelectedHead must be physically contained by the capture frontier.",
                    nameof(baseline)
                );
            }
        }
    }

    private static void ValidateLimits(GalateaDelegationStoreLimits limits) {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumQueuedMails is < 1
                or > GalateaDelegationStateBounds.MaximumCandidateCount
            || limits.MaximumTaskUtf8Bytes is < 1
                or > GalateaDelegationStateBounds.MaximumTaskUtf8Bytes
            || limits.MaximumReplyUtf8Bytes is < 1
                or > PlayerTurnObservationEnvelope.MaximumReplyUtf8Bytes
            || limits.MaximumInboxReplies is < 1
                or > GalateaDelegationStateBounds.MaximumCandidateCount
            || limits.MaximumInboxUtf8Bytes
                < Math.Max(
                    limits.MaximumReplyUtf8Bytes,
                    PlayerTurnObservationEnvelope.MaximumFailureUtf8Bytes
                )) {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Delegation store capacity limits are invalid."
            );
        }
    }

    private static void RequireBoundedText(string? value, string parameter) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                $"{parameter} must not be blank.",
                parameter
            );
        }
        try {
            if (StrictUtf8.GetByteCount(value)
                    > GalateaDelegationStateBounds.MaximumIdentityUtf8Bytes) {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                $"{parameter} must be strict Unicode.",
                parameter,
                exception
            );
        }
    }

    private static void RequireEventAddress(string value, string parameter) {
        if (!Atelia.SessionJournal.EventAddressTextCodec.TryParse(
                value,
                out _)) {
            throw new ArgumentException(
                $"{parameter} must be a canonical EventAddress.",
                parameter
            );
        }
    }

    private static void RequireRoutePolicyFingerprint(
        string value,
        string parameter
    ) {
        RequireBoundedText(value, parameter);
        if (!value.StartsWith("gdrp1-", StringComparison.Ordinal)
            || value.Length != 70
            || !IsLowerHexSha256(value[6..])) {
            throw new ArgumentException(
                $"{parameter} must be a canonical route policy fingerprint.",
                parameter
            );
        }
    }

    private static string ComputeSha256(string text) =>
        Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(text)))
            .ToLowerInvariant();

    private static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static void RequireExistingAncestorsNoReparse(string path) {
        string? current = path;
        while (current is not null) {
            if (Path.Exists(current)) {
                RejectReparsePoint(current, "delegation store path");
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

    private void ThrowIfNotWritable() {
        ThrowIfDisposed();
        if (_readOnly) {
            throw new GalateaDelegationStoreReadOnlyException();
        }
    }
}
