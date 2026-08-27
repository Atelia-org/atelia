using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

/// <summary>
/// Dormant, explicitly constructed durable delegation authority. Production
/// composition intentionally has no reference to this type until hard cut.
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
    private readonly GalateaDelegationStoreIdentity _identity;
    private readonly GalateaDelegationStoreLimits _limits;
    private readonly GalateaDelegationStoreTestHooks _hooks;
    private readonly FileStream _lifetimeLock;
    private readonly object _gate = new();
    private bool _disposed;

    private GalateaDelegationSqliteStore(
        string storeDirectory,
        GalateaDelegationStoreIdentity identity,
        GalateaDelegationStoreLimits limits,
        GalateaDelegationStoreTestHooks hooks,
        FileStream lifetimeLock
    ) {
        _storeDirectory = storeDirectory;
        _databasePath = Path.Combine(storeDirectory, DatabaseFileName);
        _identity = identity;
        _limits = limits;
        _hooks = hooks;
        _lifetimeLock = lifetimeLock;
    }

    internal string StoreDirectory => _storeDirectory;

    internal static GalateaDelegationSqliteStore CreateNew(
        string storeDirectory,
        GalateaDelegationStoreIdentity identity,
        GalateaDelegationStoreLimits limits,
        GalateaDelegationStoreTestHooks? hooks = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        GalateaDelegationDurableFiles.RequireLinux();
        ValidateIdentity(identity);
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
            InsertInitialState(connection, identity, limits);
            ValidateOpenedDatabase(connection, identity, limits);
            GalateaDelegationDurableFiles.FlushDirectory(fullPath);
            return new GalateaDelegationSqliteStore(
                fullPath,
                identity,
                limits,
                hooks ?? GalateaDelegationStoreTestHooks.None,
                lifetimeLock
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
        GalateaDelegationStoreIdentity identity,
        GalateaDelegationStoreLimits limits,
        GalateaDelegationStoreTestHooks? hooks = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        GalateaDelegationDurableFiles.RequireLinux();
        ValidateIdentity(identity);
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
                create: false
            );
            ConfigureOpenedDatabase(connection);
            ValidateOpenedDatabase(connection, identity, limits);
            return new GalateaDelegationSqliteStore(
                fullPath,
                identity,
                limits,
                hooks ?? GalateaDelegationStoreTestHooks.None,
                lifetimeLock
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
            create: false
        );
        try {
            ConfigureOpenedDatabase(connection);
            ValidateSchemaIdentity(connection);
            RequireIdentity(
                connection,
                transaction: null,
                _identity,
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
        bool create
    ) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = create
                ? SqliteOpenMode.ReadWriteCreate
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

    private static void ValidateOpenedDatabase(
        SqliteConnection connection,
        GalateaDelegationStoreIdentity identity,
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
        RequireIdentity(connection, transaction: null, identity, limits);
        _ = ReadSnapshotCore(connection, transaction: null);
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
            "session_repository_id", "capture_frontier",
            "baseline_selected_head", "route_policy_fingerprint",
            "maximum_queued_mails", "maximum_reply_utf8_bytes",
            "maximum_inbox_replies", "maximum_inbox_utf8_bytes",
            "next_completion_sequence", "revision"
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
            "quarantine_code", "revision"
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

    private static void RequireIdentity(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        GalateaDelegationStoreIdentity expected,
        GalateaDelegationStoreLimits expectedLimits
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT schema_version, user_id, session_repository_id,
                   capture_frontier, baseline_selected_head,
                   route_policy_fingerprint, maximum_queued_mails,
                   maximum_reply_utf8_bytes, maximum_inbox_replies,
                   maximum_inbox_utf8_bytes
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
                expected.CaptureFromPhysicalFrontier,
                StringComparison.Ordinal)
            || !NullableTextEquals(reader, 4, expected.BaselineSelectedHead)
            || !string.Equals(reader.GetString(5),
                expected.RoutePolicyFingerprint,
                StringComparison.Ordinal)
            || reader.GetInt32(6) != expectedLimits.MaximumQueuedMails
            || reader.GetInt32(7) != expectedLimits.MaximumReplyUtf8Bytes
            || reader.GetInt32(8) != expectedLimits.MaximumInboxReplies
            || reader.GetInt32(9) != expectedLimits.MaximumInboxUtf8Bytes
            || reader.Read()) {
            throw new InvalidDataException(
                "Delegation store identity or baseline does not match."
            );
        }
    }

    private static bool NullableTextEquals(
        SqliteDataReader reader,
        int ordinal,
        string? expected
    ) => reader.IsDBNull(ordinal)
        ? expected is null
        : string.Equals(
            reader.GetString(ordinal),
            expected,
            StringComparison.Ordinal
        );

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

    private static void ValidateIdentity(
        GalateaDelegationStoreIdentity identity
    ) {
        ArgumentNullException.ThrowIfNull(identity);
        RequireBoundedText(identity.UserId, nameof(identity.UserId));
        RequireBoundedText(
            identity.SessionRepositoryId,
            nameof(identity.SessionRepositoryId)
        );
        RequireEventAddress(
            identity.CaptureFromPhysicalFrontier,
            nameof(identity.CaptureFromPhysicalFrontier)
        );
        if (identity.BaselineSelectedHead is { } baseline) {
            RequireEventAddress(baseline, nameof(identity.BaselineSelectedHead));
        }
        RequireBoundedText(
            identity.RoutePolicyFingerprint,
            nameof(identity.RoutePolicyFingerprint)
        );
    }

    private static void ValidateLimits(GalateaDelegationStoreLimits limits) {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumQueuedMails is < 1
                or > GalateaDelegationStateBounds.MaximumCandidateCount
            || limits.MaximumReplyUtf8Bytes is < 1
                or > GalateaPlayerObservationEnvelope.MaximumReplyUtf8Bytes
            || limits.MaximumInboxReplies is < 1
                or > GalateaDelegationStateBounds.MaximumCandidateCount
            || limits.MaximumInboxUtf8Bytes
                < Math.Max(
                    limits.MaximumReplyUtf8Bytes,
                    GalateaPlayerObservationEnvelope.MaximumFailureUtf8Bytes
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
}
