using Atelia.EventJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.SessionJournal.HistoryTimeline;

internal sealed partial class SqliteHistoryTimelineLedger {
    internal static int ReadUpgradeSourceVersion(string path, HistoryTimelineStorageLimits limits) {
        RequireDatabaseBound(path, limits);
        using SqliteConnection source = OpenConnection(path, create: false, limits, readOnly: true);
        ConfigureReadOnlyDatabase(source, limits);
        using SqliteCommand query = source.CreateCommand();
        query.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(query.ExecuteScalar());
    }

    internal static TimelineHeadRef ConvertSchemaV2Copy(string sourcePath, string destinationPath,
        RefId refId, TimelineId timelineId, HistoryTimelineStorageLimits limits) {
        if (limits.MaximumPathPageRows < 1 || limits.MaximumPathPageUtf8Bytes < 1) {
            throw new HistoryTimelineStoreLimitException("UpgradePageBytes", "Upgrade page limits must be positive.");
        }
        RequireDatabaseBound(sourcePath, limits);
        using SqliteConnection source = OpenConnection(sourcePath, create: false, limits, readOnly: true);
        ConfigureReadOnlyDatabase(source, limits);
        using SqliteTransaction sourceTransaction = source.BeginTransaction(deferred: true);
        ValidateLegacySchemaV2(source, sourceTransaction);
        ValidateLegacyPhysicalBounds(source, sourceTransaction);
        TimelineHeadRef head = ReadLegacyHead(source, sourceTransaction, refId, timelineId);
        if (File.Exists(destinationPath)) { throw new IOException("The upgrade destination already exists."); }
        using SqliteConnection target = OpenConnection(destinationPath, create: true, limits);
        ConfigureCreatedDatabase(target, limits);
        CreateSchema(target);
        using SqliteTransaction targetTransaction = target.BeginTransaction(deferred: false);
        using (SqliteCommand defer = target.CreateCommand()) {
            defer.Transaction = targetTransaction;
            defer.CommandText = "PRAGMA defer_foreign_keys = ON;";
            defer.ExecuteNonQuery();
        }
        CopyLegacyTable(source, sourceTransaction, target, targetTransaction,
            "policies", ["policy_digest", "canonical"]);
        ConvertLegacyRows(source, sourceTransaction, target, targetTransaction, refId, timelineId, limits);
        CopyLegacyTable(source, sourceTransaction, target, targetTransaction,
            "current_selected_path", ["ordinal", "row_id", "previous_row_id", "end_address", "leaf_digest"]);
        CopyLegacyTable(source, sourceTransaction, target, targetTransaction,
            "current_selected_path_merkle", ["level", "node_index", "range_start", "range_end", "digest"]);
        // Construction triggers mark the private target dirty. Restore the validated
        // source guard only after both selected-path tables have been copied.
        using (SqliteCommand clear = target.CreateCommand()) {
            clear.Transaction = targetTransaction;
            clear.CommandText = "DELETE FROM current_selected_path_guard;";
            clear.ExecuteNonQuery();
        }
        CopyLegacyTable(source, sourceTransaction, target, targetTransaction,
            "current_selected_path_guard", ["singleton", "dirty"]);
        CopyLegacyTable(source, sourceTransaction, target, targetTransaction,
            "store_metadata", ["singleton", "schema_version", "timeline_id", "ref_id", "head_canonical", "head_sha256", "policy_count", "row_count"]);
        targetTransaction.Commit();
        sourceTransaction.Commit();
        return head;
    }

    private static void ValidateLegacySchemaV2(SqliteConnection source, SqliteTransaction transaction) {
        using SqliteCommand header = source.CreateCommand();
        header.Transaction = transaction;
        header.CommandText = "SELECT (SELECT user_version FROM pragma_user_version),(SELECT application_id FROM pragma_application_id);";
        using (SqliteDataReader reader = header.ExecuteReader()) {
            if (!reader.Read() || reader.GetInt64(0) != 2 || reader.GetInt64(1) != ApplicationId) {
                throw new InvalidDataException("The upgrade source is not a schema-2 Timeline database.");
            }
        }
        using SqliteCommand query = source.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;";
        using (SqliteDataReader reader = query.ExecuteReader()) {
            SchemaEntry[] expected = LegacySchemaEntriesV2.OrderBy(static item => item.Type, StringComparer.Ordinal)
                .ThenBy(static item => item.Name, StringComparer.Ordinal).ToArray();
            int index = 0;
            while (reader.Read()) {
                if (index >= expected.Length || reader.IsDBNull(3)) {
                    throw new InvalidDataException("The schema-2 Timeline source has an unexpected schema object.");
                }
                SchemaEntry entry = expected[index++];
                if (reader.GetString(0) != entry.Type || reader.GetString(1) != entry.Name
                    || reader.GetString(2) != entry.TableName || reader.GetString(3) != entry.Sql) {
                    throw new InvalidDataException("The Timeline source differs from the fixed schema-2 DDL.");
                }
            }
            if (index != expected.Length) { throw new InvalidDataException("The schema-2 Timeline source is incomplete."); }
        }
        query.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(query.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) {
            throw new InvalidDataException("The schema-2 Timeline source failed integrity_check.");
        }
        query.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader violations = query.ExecuteReader();
        if (violations.Read()) { throw new InvalidDataException("The schema-2 Timeline source has broken foreign keys."); }
    }

    private static void ValidateLegacyPhysicalBounds(SqliteConnection source, SqliteTransaction transaction) {
        using SqliteCommand query = source.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT (SELECT COUNT(*) FROM store_metadata),
                (SELECT COUNT(*) FROM current_selected_path_guard WHERE singleton=1 AND dirty=0);
            """;
        using (SqliteDataReader reader = query.ExecuteReader()) {
            if (!reader.Read() || reader.GetInt64(0) != 1 || reader.GetInt64(1) != 1) {
                throw new InvalidDataException("The source metadata or selected-path guard is invalid.");
            }
        }
        query.CommandText = $"""
            SELECT
              EXISTS(SELECT 1 FROM store_metadata WHERE singleton!=1 OR schema_version!=2 OR length(timeline_id)!=32
                OR length(ref_id)!=16 OR length(head_sha256)!=64 OR length(head_canonical) NOT BETWEEN 1 AND {HistoryTimelineStoreLimits.MaximumHeadUtf8Bytes})
              OR EXISTS(SELECT 1 FROM policies WHERE length(policy_digest)!=64 OR length(canonical) NOT BETWEEN 1 AND {HistoryTimelineCanonicalCodec.MaximumPolicyUtf8Bytes})
              OR EXISTS(SELECT 1 FROM rows WHERE length(row_id)!=64 OR length(previous_row_id)!=64 OR length(descriptor_digest)!=64
                OR length(end_address)!={EventAddressCodec.EventAddressLength} OR length(canonical) NOT BETWEEN 1 AND {HistoryTimelineCanonicalCodec.MaximumDescriptorUtf8Bytes})
              OR EXISTS(SELECT 1 FROM current_selected_path WHERE length(row_id)!=64 OR length(previous_row_id)!=64
                OR length(end_address)!={EventAddressCodec.EventAddressLength} OR length(leaf_digest)!=64)
              OR EXISTS(SELECT 1 FROM current_selected_path_merkle WHERE length(digest)!=64);
            """;
        if (Convert.ToInt64(query.ExecuteScalar()) != 0) {
            throw new InvalidDataException("A schema-2 Timeline source value exceeds its physical bounds.");
        }
    }

    private static TimelineHeadRef ReadLegacyHead(SqliteConnection source, SqliteTransaction transaction,
        RefId refId, TimelineId timelineId) {
        using SqliteCommand query = source.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT timeline_id,ref_id,head_canonical,head_sha256 FROM store_metadata WHERE singleton=1;";
        using SqliteDataReader reader = query.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != timelineId.Value || reader.GetString(1) != refId.ToHexString()) {
            throw new InvalidDataException("The old Timeline database does not match its explicit Ref/Timeline scope.");
        }
        byte[] bytes = reader.GetFieldValue<byte[]>(2);
        TimelineHeadRef head = HistoryTimelineCanonicalCodec.DecodeTimelineHead(bytes);
        if (head.RefId != refId || head.TimelineId != timelineId || reader.GetString(3) != ComputeHeadDigest(bytes)) {
            throw new InvalidDataException("The schema-2 Timeline head differs from its source metadata.");
        }
        return head;
    }

    private static void ConvertLegacyRows(SqliteConnection source, SqliteTransaction sourceTransaction,
        SqliteConnection target, SqliteTransaction targetTransaction, RefId refId, TimelineId timelineId,
        HistoryTimelineStorageLimits limits) {
        string? after = null;
        int pageSize = Math.Min(128, limits.MaximumPathPageRows);
        while (true) {
            using SqliteCommand query = source.CreateCommand();
            query.Transaction = sourceTransaction;
            query.CommandText = "SELECT row_id,previous_row_id,end_address,descriptor_digest,length(canonical),canonical FROM rows "
                + (after is null ? "" : "WHERE row_id>$after ") + "ORDER BY row_id LIMIT $limit;";
            if (after is not null) { query.Parameters.AddWithValue("$after", after); }
            query.Parameters.AddWithValue("$limit", pageSize);
            using SqliteDataReader reader = query.ExecuteReader();
            int count = 0;
            int pageBytes = 0;
            while (reader.Read()) {
                long length = reader.GetInt64(4);
                if (length is < 1 or > HistoryTimelineCanonicalCodec.MaximumDescriptorUtf8Bytes) {
                    throw new InvalidDataException("An old descriptor exceeds its canonical byte bound.");
                }
                pageBytes = checked(pageBytes + (int)length);
                if (pageBytes > limits.MaximumPathPageUtf8Bytes) {
                    throw new HistoryTimelineStoreLimitException("UpgradePageBytes", "An upgrade row page exceeds its byte budget.");
                }
                byte[] bytes = reader.GetFieldValue<byte[]>(5);
                HistoryTimelineCanonicalCodec.LegacyDescriptorValue legacy =
                    HistoryTimelineCanonicalCodec.DecodeLegacyDescriptorV1(bytes);
                HistorySegmentDescriptor row = legacy.Descriptor;
                byte[] end = new byte[EventAddressCodec.EventAddressLength];
                EventAddressCodec.Encode(row.EndInclusive, end);
                if (bytes.LongLength != length || row.RowId.Value != reader.GetString(0)
                    || row.PreviousRowId?.Value != (reader.IsDBNull(1) ? null : reader.GetString(1))
                    || !end.AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(2))
                    || legacy.DescriptorDigest != reader.GetString(3)
                    || row.RefId != refId || row.TimelineId != timelineId) {
                    throw new InvalidDataException("An old descriptor differs from its row index or scope.");
                }
                InsertRow(target, targetTransaction, row, row.ToCanonicalBytes());
                after = row.RowId.Value;
                count++;
            }
            if (count < pageSize) { return; }
        }
    }

    private static void CopyLegacyTable(SqliteConnection source, SqliteTransaction sourceTransaction,
        SqliteConnection target, SqliteTransaction targetTransaction, string table, string[] columns) {
        // Table and column names are fixed call-site literals, never operator input.
        using SqliteCommand read = source.CreateCommand();
        read.Transaction = sourceTransaction;
        read.CommandText = $"SELECT {string.Join(',', columns)} FROM {table};";
        using SqliteDataReader reader = read.ExecuteReader();
        using SqliteCommand write = target.CreateCommand();
        write.Transaction = targetTransaction;
        string[] names = Enumerable.Range(0, columns.Length).Select(static index => "$p" + index).ToArray();
        write.CommandText = $"INSERT INTO {table}({string.Join(',', columns)}) VALUES({string.Join(',', names)});";
        foreach (string name in names) { write.Parameters.Add(new SqliteParameter(name, DBNull.Value)); }
        while (reader.Read()) {
            for (int index = 0; index < columns.Length; index++) {
                write.Parameters[index].Value = table == "store_metadata" && columns[index] == "schema_version"
                    ? SchemaVersion : reader.GetValue(index);
            }
            write.ExecuteNonQuery();
        }
    }

    // Frozen from 3c1fd372; this source schema is not derived from the current writer.
    private static readonly SchemaEntry[] LegacySchemaEntriesV2 = [
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
}
