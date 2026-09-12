using Atelia.EventJournal;
using Atelia.Galatea.Server;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationSqliteStoreMigrationTests {
    [Theory]
    [InlineData("Queued")]
    [InlineData("Binding")]
    [InlineData("Started")]
    [InlineData("OutcomeUnknown")]
    [InlineData("Accepted")]
    [InlineData("TerminalCompleted")]
    [InlineData("TerminalFailed")]
    [InlineData("Quarantined")]
    [InlineData("Leased")]
    [InlineData("Consumed")]
    public void Upgrade_PreservesEveryBusinessColumnAndSnapshot(string state) {
        using var fixture = new MigrationFixture(state);
        Assert.Throws<InvalidDataException>(() => fixture.Open());
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExistingReadOnly(
                fixture.StoreDirectory, Owner, Limits));

        GalateaDelegationStoreUpgradeResult result = fixture.Upgrade(apply: true);

        Assert.Equal("Upgraded", result.Outcome);
        Assert.NotNull(result.BackupPath);
        Assert.Equal(1L, Scalar(result.BackupPath, "PRAGMA user_version;"));
        Assert.Equal(fixture.V1Rows, ReadBusinessRows(result.BackupPath));
        Assert.Equal(fixture.BusinessRows, ReadBusinessRows(fixture.DatabasePath));
        Assert.Equal(2L, Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
        using (GalateaDelegationSqliteStore store = fixture.Open()) {
            Assert.Equal(fixture.Snapshot, JsonSerializer.Serialize(store.ReadSnapshot()));
        }
        byte[] after = File.ReadAllBytes(fixture.DatabasePath);
        GalateaDelegationStoreUpgradeResult retry = fixture.Upgrade(apply: true);
        Assert.Equal("AlreadyCurrent", retry.Outcome);
        Assert.Null(retry.BackupPath);
        Assert.Equal(after, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Single(fixture.Backups());
    }

    [Fact]
    public void DryRun_IsBytePreservingAndCreatesNoBackup() {
        using var fixture = new MigrationFixture("Leased");
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        GalateaDelegationStoreUpgradeResult result = fixture.Upgrade(apply: false);
        Assert.Equal("DryRunReady", result.Outcome);
        Assert.Null(result.BackupPath);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Empty(fixture.Backups());
        Assert.Equal(1L, Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommitBoundary_RetryRecognizesWholeOldOrNewFormat(bool afterCommit) {
        using var fixture = new MigrationFixture("Consumed");
        Action<string> fail = _ => throw new IOException("injected upgrade failure");
        var hooks = afterCommit
            ? new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: fail)
            : new GalateaDelegationStoreTestHooks(BeforeCommit: fail);
        Assert.Throws<IOException>(() => fixture.Upgrade(apply: true, hooks));
        Assert.Equal(afterCommit ? 2L : 1L,
            Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal(afterCommit ? 0L : 1L, Scalar(fixture.DatabasePath,
            "SELECT count(*) FROM pragma_table_info('outbound_mail') WHERE name = 'frozen_route_policy_fingerprint';"));
        Assert.Equal(afterCommit ? fixture.BusinessRows : fixture.V1Rows,
            ReadBusinessRows(fixture.DatabasePath));
        GalateaDelegationStoreUpgradeResult retry = fixture.Upgrade(apply: true);
        Assert.Equal(afterCommit ? "AlreadyCurrent" : "Upgraded", retry.Outcome);
        using GalateaDelegationSqliteStore store = fixture.Open();
        Assert.Equal(fixture.Snapshot, JsonSerializer.Serialize(store.ReadSnapshot()));
    }

    [Fact]
    public void WrongOwnerOrLimits_RejectBeforeBackupOrMutation() {
        using var fixture = new MigrationFixture("Accepted");
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        foreach (GalateaDelegationStoreOwner wrong in new[] {
            Owner with { UserId = "other" },
            Owner with { SessionRepositoryId = "other-repository" }
        }) {
            Assert.Throws<InvalidDataException>(() =>
                GalateaDelegationSqliteStore.UpgradeExisting(
                    fixture.StoreDirectory, wrong, Limits, apply: true));
        }
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.UpgradeExisting(
                fixture.StoreDirectory, Owner,
                Limits with { MaximumInboxReplies = Limits.MaximumInboxReplies + 1 },
                apply: true));
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public void HeldLifetimeLock_RejectsUpgrade() {
        using var fixture = new MigrationFixture("Queued");
        using var held = new FileStream(
            Path.Combine(fixture.StoreDirectory, GalateaDelegationSqliteStore.LockFileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() => fixture.Upgrade(apply: true));
        Assert.Empty(fixture.Backups());
        Assert.Equal(1L, Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
    }

    [Fact]
    public void UnsupportedVersion_RejectsBeforeBackupOrMutation() {
        using var fixture = new MigrationFixture("Queued");
        using (SqliteConnection connection = Connect(fixture.DatabasePath, SqliteOpenMode.ReadWrite)) {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        Assert.Throws<InvalidDataException>(() => fixture.Upgrade(apply: true));
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Empty(fixture.Backups());
    }

    [Theory]
    [InlineData("Started")]
    [InlineData("OutcomeUnknown")]
    [InlineData("Accepted")]
    public async Task UpgradedActiveMail_InspectsOriginalIdentityWithoutStartingAgain(string state) {
        using var fixture = new MigrationFixture(state);
        _ = fixture.Upgrade(apply: true);
        using GalateaDelegationSqliteStore store = fixture.Open();
        GalateaOutboundMailSnapshot original = store.ReadSnapshot().Mails[0];
        await using var transport = new InspectOnlyTransport();
        var clock = new MigrationClock();
        var driver = new GalateaDurableDelegationDriver(store, transport, fixture.HomeDirectory, clock);
        _ = await driver.PulseAsync();
        if (state == "Started") {
            Assert.Empty(transport.Requests);
            clock.Now += TimeSpan.FromDays(1);
            _ = await driver.PulseAsync();
        }
        GalateaInspectDelegateDispatchRequest request = Assert.Single(transport.Requests);
        Assert.Equal(original.DispatchId, request.DispatchId);
        Assert.Equal(original.RequestedThreadId, request.ThreadId);
        Assert.Equal(original.Body, request.Task);
        Assert.Equal(original.AcceptedTurnId, request.ExpectedTurnId);
        Assert.Equal(0, transport.StartCalls);
        Assert.Equal(GalateaDurableMailState.TerminalCompleted, store.ReadSnapshot().Mails[0].State);
        Assert.Equal("reply after upgrade", Assert.Single(store.ReadSnapshot().Notices).Body);
    }

    private static readonly GalateaDelegationStoreOwner Owner = new("user", "repository-id");
    private static readonly GalateaDelegationStoreLimits Limits = new(32, 100_000, 1024, 16, 16 * 1024);
    private static readonly string[] Tables = [
        "delegation_meta", "action_capture", "outbound_mail", "route_binding",
        "reply_notice", "reply_lease", "reply_lease_item"
    ];
    private static string Address(int value) => $"ej1:{value:x16}0000000100000000";

    private sealed class MigrationFixture : IDisposable {
        private readonly string _root;
        internal MigrationFixture(string state) {
            _root = Path.Combine(Path.GetTempPath(), "galatea-upgrade-test-" + Guid.NewGuid().ToString("N"));
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(_root);
            TestDirectorySafety.CreateDirectoryNew(_root);
            HomeDirectory = Path.Combine(_root, "home");
            TestDirectorySafety.CreateDirectoryNew(HomeDirectory);
            string sourceDirectory = Path.Combine(_root, "v2-source");
            StoreDirectory = Path.Combine(_root, "v1-target");
            TestDirectorySafety.CreateDirectoryNew(StoreDirectory);
            using (File.Create(Path.Combine(StoreDirectory, GalateaDelegationSqliteStore.LockFileName))) { }
            EventAddress head = EventAddressTextCodec.Parse(Address(2));
            using (GalateaDelegationSqliteStore store = GalateaDelegationSqliteStore.CreateNew(
                sourceDirectory, Owner,
                new(new EventJournalPhysicalAppendFrontier(head.SegmentNumber, head.Ticket.EndOffsetExclusive), Address(2)),
                Limits)) {
                Populate(store, state);
                Snapshot = JsonSerializer.Serialize(store.ReadSnapshot());
            }
            string sourcePath = Path.Combine(sourceDirectory, GalateaDelegationSqliteStore.DatabaseFileName);
            BusinessRows = ReadBusinessRows(sourcePath);
            CreateV1(sourcePath, DatabasePath);
            V1Rows = ReadBusinessRows(DatabasePath);
        }
        internal string HomeDirectory { get; }
        internal string StoreDirectory { get; }
        internal string DatabasePath => Path.Combine(StoreDirectory, GalateaDelegationSqliteStore.DatabaseFileName);
        internal string Snapshot { get; }
        internal string BusinessRows { get; }
        internal string V1Rows { get; }
        internal string[] Backups() => Directory.GetFiles(StoreDirectory, "*.v1-backup-*");
        internal GalateaDelegationStoreUpgradeResult Upgrade(bool apply, GalateaDelegationStoreTestHooks? hooks = null) =>
            GalateaDelegationSqliteStore.UpgradeExisting(StoreDirectory, Owner, Limits, apply, hooks);
        internal GalateaDelegationSqliteStore Open() =>
            GalateaDelegationSqliteStore.OpenExisting(StoreDirectory, Owner, Limits);
        public void Dispose() => TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
    }

    private static void Populate(GalateaDelegationSqliteStore store, string state) {
        store.CaptureActionBatch(new(Address(100), new string('a', 64), 12, "extractor-v1", [
            new SendMailIntent("Codex", null, "original task", null, "sent"),
            new SendMailIntent("Codex", null, "next queued task", null, "sent"),
            new SendMailIntent("other", null, "unrouted task", null, "sent")
        ]));
        if (state == "Queued") { return; }
        GalateaRouteBindingSnapshot route = store.BeginThreadBinding("bind", store.ReadSnapshot().Route.Revision);
        if (state == "Binding") { return; }
        route = store.CompleteThreadBinding("bind", "original-thread", route.Revision);
        GalateaOutboundMailSnapshot mail = store.ReadSnapshot().Mails[0];
        mail = store.StartQueuedMail(mail.DispatchId, mail.Revision, route.Revision);
        if (state == "Started") { return; }
        if (state == "OutcomeUnknown") {
            store.MarkMailOutcomeUnknown(mail.DispatchId, mail.Revision, "TRANSPORT_LOST", 0);
            return;
        }
        if (state == "Quarantined") {
            store.QuarantineActiveMail(mail.DispatchId, mail.Revision, "DISPATCH_AMBIGUOUS");
            return;
        }
        mail = store.RecordMailAccepted(mail.DispatchId, mail.Revision, "original-thread", "original-turn");
        if (state == "Accepted") { return; }
        if (state == "TerminalFailed") {
            store.RecordFailedMail(mail.DispatchId, mail.Revision, "original-thread", "original-turn", "turn", "FAILED", "delivery failed");
            return;
        }
        GalateaReplyNoticeSnapshot notice = store.RecordCompletedMail(
            mail.DispatchId, mail.Revision, "original-thread", "original-turn", "reply");
        if (state == "TerminalCompleted") { return; }
        GalateaReplyLeaseSnapshot lease = store.BeginReplyLeaseMembership(
            "lease", "player", [new(notice.NoticeId, notice.Revision)]);
        string observation = PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation(
            "player", DateTimeOffset.UnixEpoch, [new PlayerTurnNotice.Reply("reply")]));
        lease = store.BindReplyLeaseObservationBase(lease.LeaseId, lease.Revision, Address(20), observation);
        if (state == "Leased") { return; }
        lease = store.RecordLeaseObservationCommitted(lease.LeaseId, lease.Revision, Address(21));
        store.ConsumeReplyLease(lease.LeaseId, lease.Revision, Address(22));
    }

    private static void CreateV1(string sourcePath, string targetPath) {
        using SqliteConnection target = Connect(targetPath, SqliteOpenMode.ReadWriteCreate);
        using (SqliteCommand ddl = target.CreateCommand()) {
            ddl.CommandText = GalateaDelegationV1Schema.Sql
                + $"PRAGMA application_id = {GalateaDelegationSqliteStore.ApplicationId}; PRAGMA user_version = 1;";
            ddl.ExecuteNonQuery();
        }
        using (SqliteCommand attach = target.CreateCommand()) {
            attach.CommandText = "ATTACH DATABASE $source AS source;";
            attach.Parameters.AddWithValue("$source", sourcePath);
            attach.ExecuteNonQuery();
        }
        foreach (string table in Tables) {
            List<string> columns = Columns(target, table);
            string[] values = columns.Select(column => column switch {
                "schema_version" => "1",
                "route_policy_fingerprint" or "policy_fingerprint" => "'gdrp1-" + new string('a', 64) + "'",
                "frozen_route_policy_fingerprint" => "CASE WHEN operation_id IS NOT NULL THEN 'gdrp1-" + new string('a', 64) + "' ELSE NULL END",
                _ => Quote(column)
            }).ToArray();
            using SqliteCommand copy = target.CreateCommand();
            copy.CommandText = $"INSERT INTO {Quote(table)} ({string.Join(',', columns.Select(Quote))}) SELECT {string.Join(',', values)} FROM source.{Quote(table)};";
            copy.ExecuteNonQuery();
        }
    }

    // Compare every retained database column independently of the snapshot projection.
    private static string ReadBusinessRows(string path) {
        using SqliteConnection connection = Connect(path, SqliteOpenMode.ReadOnly);
        var tables = new List<object>();
        foreach (string table in Tables) {
            string[] columns = Columns(connection, table).Where(column =>
                column is not ("route_policy_fingerprint" or "policy_fingerprint" or "frozen_route_policy_fingerprint" or "schema_version")).ToArray();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(',', columns.Select(Quote))} FROM {Quote(table)} ORDER BY {string.Join(',', columns.Select(Quote))};";
            using SqliteDataReader reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read()) {
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
            }
            tables.Add(new { table, columns, rows });
        }
        return JsonSerializer.Serialize(tables);
    }

    private static List<string> Columns(SqliteConnection connection, string table) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)});";
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) { result.Add(reader.GetString(1)); }
        return result;
    }
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static long Scalar(string path, string sql) {
        using SqliteConnection connection = Connect(path, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    private static SqliteConnection Connect(string path, SqliteOpenMode mode) {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = mode, Pooling = false, Cache = SqliteCacheMode.Private
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class InspectOnlyTransport : IGalateaDurableDelegateTransport {
        internal List<GalateaInspectDelegateDispatchRequest> Requests { get; } = [];
        internal int StartCalls { get; private set; }
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(GalateaEnsureDelegateBindingRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Existing binding must survive upgrade.");
        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(GalateaStartDelegateTurnRequest request, CancellationToken ct) {
            StartCalls++;
            throw new InvalidOperationException("An existing dispatch must never restart.");
        }
        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(GalateaInspectDelegateDispatchRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Task.FromResult<GalateaDelegateDispatchInspection>(new GalateaDelegateDispatchInspection.Completed(
                request.DispatchId, request.ThreadId, request.ExpectedTurnId ?? "original-turn", "reply after upgrade", GalateaDelegateInspectionSource.Persistent));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MigrationClock : TimeProvider {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddDays(10);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
