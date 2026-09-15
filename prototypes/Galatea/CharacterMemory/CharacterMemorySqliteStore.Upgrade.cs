using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed record CharacterMemoryStoreUpgradeResult(
    string Outcome, int SourceVersion, int TargetVersion, string? BackupPath);

internal sealed partial class CharacterMemorySqliteStore {
    /// <summary>Offline maintenance. The same lifetime lock covers preflight, backup, writes and cold verification.</summary>
    internal static CharacterMemoryStoreUpgradeResult UpgradeExisting(
        string storeDirectory, CharacterMemoryStoreOwner owner, bool apply,
        CharacterMemoryStoreTestHooks? hooks = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        GalateaDelegationDurableFiles.RequireLinux();
        ValidateOwner(owner);
        hooks ??= CharacterMemoryStoreTestHooks.None;
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
        RequireExistingAncestorsNoReparse(directory);
        if (!Directory.Exists(directory)) { throw new DirectoryNotFoundException("The Character Memory store does not exist."); }
        RejectReparsePoint(directory, "Character Memory upgrade directory");
        RejectReparsePoint(Path.Combine(directory, LockFileName), "Character Memory lifetime lock");
        using FileStream lifetimeLock = AcquireLifetimeLock(directory, FileMode.Open);
        string database = Path.Combine(directory, DatabaseFileName);
        RejectReparsePoint(database, "Character Memory database");
        string? backupPath = null;
        bool backupValidated = false;
        string phase = "preflight";
        int version = 0;
        try {
            string originalAuthority;
            string expectedV4Authority;
            using (SqliteConnection source = OpenConnection(database, create: false, readOnly: true)) {
                ConfigureOpenedDatabase(source, readOnly: true);
                version = checked((int)ReadPragmaInteger(source, "user_version"));
                ValidateUpgradeSource(source, owner, version);
                if (version == SchemaVersion) { return new("AlreadyCurrent", version, SchemaVersion, null); }
                originalAuthority = ReadUpgradeAuthorityDigest(source, version);

                phase = "projection";
                using (var projected = new SqliteConnection("Data Source=:memory:;Cache=Private;Pooling=False")) {
                    projected.Open();
                    source.BackupDatabase(projected);
                    ConfigureUpgradeProjection(projected);
                    ValidateUpgradeSource(projected, owner, version);
                    RequireUpgradeAuthority(originalAuthority, ReadUpgradeAuthorityDigest(projected, version));
                    UpgradeConnection(projected, owner, CharacterMemoryStoreTestHooks.None);
                    _ = ValidateOpenedDatabase(projected, owner);
                    expectedV4Authority = ReadUpgradeAuthorityDigest(projected, SchemaVersion);
                }
                if (!apply) { return new("DryRunReady", version, SchemaVersion, null); }

                string operation = $"upgrade-character-memory-v{version}-to-v{SchemaVersion}";
                hooks.AfterValidationBeforeTransaction?.Invoke(operation);
                ValidateUpgradeSource(source, owner, version);
                RequireUpgradeAuthority(originalAuthority, ReadUpgradeAuthorityDigest(source, version));
                phase = "backup";
                backupPath = database + $".v{version}-backup-"
                    + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N") + ".sqlite3";
                hooks.BeforeUpgradeBackup?.Invoke(backupPath);
                CreateCharacterMemoryUpgradeBackup(source, backupPath, owner, version, originalAuthority);
                backupValidated = true;
                hooks.AfterUpgradeBackup?.Invoke(backupPath);
            }

            phase = "migration";
            RejectReparsePoint(database, "Character Memory database");
            using (SqliteConnection writable = OpenConnection(database, create: false)) {
                // Keep journal-mode validation read-only even on this writable
                // connection: a changed source must not be silently normalized.
                ConfigureOpenedDatabase(writable, readOnly: true);
                ValidateUpgradeSource(writable, owner, version);
                RequireUpgradeAuthority(originalAuthority, ReadUpgradeAuthorityDigest(writable, version));
                UpgradeConnection(writable, owner, hooks);
                _ = ValidateOpenedDatabase(writable, owner);
                RequireUpgradeAuthority(expectedV4Authority, ReadUpgradeAuthorityDigest(writable, SchemaVersion));
            }
            phase = "cold-reopen";
            using (SqliteConnection reopened = OpenConnection(database, create: false, readOnly: true)) {
                ConfigureOpenedDatabase(reopened, readOnly: true);
                _ = ValidateOpenedDatabase(reopened, owner);
                RequireUpgradeAuthority(expectedV4Authority, ReadUpgradeAuthorityDigest(reopened, SchemaVersion));
            }
            return new("Upgraded", version, SchemaVersion, backupPath);
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            // Body-free diagnostic even when an injected I/O exception includes
            // arbitrary data. Preserve partial progress and the backup artifact.
            string backup = backupPath is null ? "No backup was created."
                : $"Backup path: {backupPath}; created={File.Exists(backupPath)}; validated={backupValidated}.";
            throw new InvalidDataException(
                $"Character Memory upgrade failed during {phase} ({exception.GetType().Name}); source version at entry={version}. {backup}", exception);
        }
    }

    private static void UpgradeConnection(SqliteConnection connection, CharacterMemoryStoreOwner owner, CharacterMemoryStoreTestHooks hooks) {
        MigrateV1ToV2IfNeeded(connection, owner, hooks);
        MigrateV2ToV3IfNeeded(connection, owner, hooks);
        MigrateV3ToV4IfNeeded(connection, owner, hooks);
    }

    private static void ValidateUpgradeSource(SqliteConnection connection, CharacterMemoryStoreOwner owner, int version) {
        if (version == 1) { _ = ValidateV1Database(connection, owner, transaction: null); }
        else if (version is 2 or 3 or SchemaVersion) { _ = ValidateOpenedDatabase(connection, owner, expectedVersion: version); }
        else { throw Corrupt("Unsupported Character Memory upgrade source version."); }
    }

    private static void ConfigureUpgradeProjection(SqliteConnection connection) {
        ExecutePragma(connection, "PRAGMA foreign_keys = ON;");
        ExecutePragma(connection, "PRAGMA synchronous = EXTRA;");
        ExecutePragma(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};");
        ExecutePragma(connection, "PRAGMA temp_store = MEMORY;");
        ExecutePragma(connection, "PRAGMA trusted_schema = OFF;");
        RequirePragmaInteger(connection, "page_size", 4096);
        RequirePragmaText(connection, "journal_mode", "memory");
    }

    private static void CreateCharacterMemoryUpgradeBackup(SqliteConnection source, string path,
        CharacterMemoryStoreOwner owner, int version, string expectedAuthority) {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        RejectReparsePoint(path, "Character Memory backup");
        using (SqliteConnection backup = OpenConnection(path, create: false)) { source.BackupDatabase(backup); }
        RejectReparsePoint(path, "Character Memory backup");
        using (SqliteConnection backup = OpenConnection(path, create: false, readOnly: true)) {
            ConfigureOpenedDatabase(backup, readOnly: true);
            ValidateUpgradeSource(backup, owner, version);
            RequireUpgradeAuthority(expectedAuthority, ReadUpgradeAuthorityDigest(backup, version));
        }
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) { file.Flush(flushToDisk: true); }
        GalateaDelegationDurableFiles.FlushDirectory(Path.GetDirectoryName(path)!);
    }

    // Local comparison for preflight/copy/cold-reopen only. No new durable identity.
    private static string ReadUpgradeAuthorityDigest(SqliteConnection connection, int version) {
        var tables = new List<(string Table, string Order)> {
            ("character_memory_meta", "singleton"),
            ("note_action_capture", "source_action_address"),
            ("character_note", "source_action_address, artifact_ordinal")
        };
        if (version >= 2) { tables.Add(("derived_info_work", "source_action_address")); }
        if (version >= 3) { tables.Add(("note_receipt_delivery", "source_action_address")); }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach ((string table, string order) in tables) {
            byte[] tableBytes = StrictUtf8.GetBytes(table);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, tableBytes.Length);
            hash.AppendData(length);
            hash.AppendData(tableBytes);
            var columns = new List<string>();
            using (SqliteCommand schema = connection.CreateCommand()) {
                schema.CommandText = $"PRAGMA table_info({table});";
                using SqliteDataReader fields = schema.ExecuteReader();
                while (fields.Read()) { columns.Add(fields.GetString(1)); }
            }
            using SqliteCommand command = connection.CreateCommand();
            string projection = string.Join(", ", columns.Select(column => "CAST(\"" + column.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" AS BLOB)"));
            command.CommandText = $"SELECT {projection} FROM {table} ORDER BY {order};";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                for (int index = 0; index < reader.FieldCount; index++) {
                    byte[] bytes = reader.IsDBNull(index) ? [] : reader.GetFieldValue<byte[]>(index);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, reader.IsDBNull(index) ? -1 : bytes.Length);
                    hash.AppendData(length);
                    hash.AppendData(bytes);
                }
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void RequireUpgradeAuthority(string expected, string actual) {
        if (expected != actual) { throw Corrupt("Character Memory upgrade authority changed."); }
    }
}
