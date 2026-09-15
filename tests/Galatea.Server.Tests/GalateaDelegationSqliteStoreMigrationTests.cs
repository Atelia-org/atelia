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
    public static TheoryData<int, string> LegacyStates {
        get {
            var cases = new TheoryData<int, string>();
            foreach (int version in new[] { 1, 2, 3, 4 }) {
                foreach (string state in new[] {
                    "Queued", "Binding", "Started", "OutcomeUnknown", "Accepted",
                    "TerminalCompleted", "TerminalFailed", "Quarantined", "Leased", "Consumed"
                }) {
                    cases.Add(version, state);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(LegacyStates))]
    public void Upgrade_PreservesEveryBusinessColumnAndSnapshot(int version, string state) {
        using var fixture = new MigrationFixture(state, version);
        Assert.Throws<InvalidDataException>(() => fixture.Open());
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExistingReadOnly(
                fixture.StoreDirectory, Owner, Limits));

        GalateaDelegationStoreUpgradeResult result = fixture.Upgrade(apply: true);

        Assert.Equal("Upgraded", result.Outcome);
        Assert.NotNull(result.BackupPath);
        Assert.Equal((long)version, Scalar(result.BackupPath, "PRAGMA user_version;"));
        Assert.Equal(fixture.LegacyRows, ReadBusinessRows(result.BackupPath, normalize: false));
        Assert.Equal(fixture.BusinessRows, ReadBusinessRows(fixture.DatabasePath));
        Assert.Equal(5L, Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DryRun_IsBytePreservingAndCreatesNoBackup(int version) {
        using var fixture = new MigrationFixture("Leased", version);
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        GalateaDelegationStoreUpgradeResult result = fixture.Upgrade(apply: false);
        Assert.Equal("DryRunReady", result.Outcome);
        Assert.Null(result.BackupPath);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Empty(fixture.Backups());
        Assert.Equal((long)fixture.LegacyVersion, Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
    }

    [Fact]
    public void V3Upgrade_RebuildsMetaCheckAndAddsOnlyEmptyInternalOutbox() {
        using var fixture = new MigrationFixture("Queued", version: 3);
        Assert.Throws<InvalidDataException>(() => fixture.Open());
        Assert.Equal("DryRunReady", fixture.Upgrade(apply: false).Outcome);
        Assert.Equal(3L, Scalar(fixture.DatabasePath, "PRAGMA user_version;"));

        GalateaDelegationStoreUpgradeResult result = fixture.Upgrade(apply: true);

        Assert.Equal("Upgraded", result.Outcome);
        Assert.Equal(5L, Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal(0L, Scalar(fixture.DatabasePath,
            "SELECT COUNT(*) FROM internal_mail_outbox;"));
        using GalateaDelegationSqliteStore store = fixture.Open();
        Assert.Empty(store.ReadSnapshot().InternalMailOutboxes);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void CommitBoundary_RetryRecognizesWholeOldOrNewFormat(int version, bool afterCommit) {
        using var fixture = new MigrationFixture("Consumed", version);
        Action<string> fail = _ => throw new IOException("injected upgrade failure");
        var hooks = afterCommit
            ? new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: fail)
            : new GalateaDelegationStoreTestHooks(BeforeCommit: fail);
        Assert.Throws<IOException>(() => fixture.Upgrade(apply: true, hooks));
        Assert.Equal(afterCommit ? 5L : version,
            Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal(afterCommit || version is 2 or 3 ? 0L : 1L, Scalar(fixture.DatabasePath,
            "SELECT count(*) FROM pragma_table_info('outbound_mail') WHERE name = 'frozen_route_policy_fingerprint';"));
        Assert.Equal(afterCommit ? fixture.BusinessRows : fixture.LegacyRows,
            ReadBusinessRows(fixture.DatabasePath, normalize: afterCommit));
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
            Owner with { CharacterId = "other" },
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
        Assert.Equal((long)fixture.LegacyVersion, Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
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
    [InlineData(1, "Started")]
    [InlineData(1, "OutcomeUnknown")]
    [InlineData(1, "Accepted")]
    [InlineData(2, "Started")]
    [InlineData(2, "OutcomeUnknown")]
    [InlineData(2, "Accepted")]
    [InlineData(3, "Accepted")]
    [InlineData(4, "Accepted")]
    public async Task UpgradedActiveMail_InspectsOriginalIdentityWithoutStartingAgain(int version, string state) {
        using var fixture = new MigrationFixture(state, version);
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
        Assert.Equal(GalateaTaskCommitment.FromStored(original), request.TaskCommitment);
        Assert.Equal(original.AcceptedTurnId, request.ExpectedTurnId);
        Assert.Equal(0, transport.StartCalls);
        Assert.Equal(GalateaDurableMailState.TerminalCompleted, store.ReadSnapshot().Mails[0].State);
        Assert.Equal("reply after upgrade", Assert.Single(store.ReadSnapshot().Notices).Body);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BindingRecoveryBudget_MovesOnlyToFifoOwnerAndSurvivesReopen(int version) {
        using var fixture = new MigrationFixture("Binding", version);
        Execute(fixture.DatabasePath, """
            UPDATE route_binding SET ensure_attempt_count = 7,
                ensure_last_code = 'BINDING_UNAVAILABLE', next_ensure_at_ms = 123456;
            """);
        string before = ReadBusinessRows(fixture.DatabasePath, normalize: false);
        byte[] bytes = File.ReadAllBytes(fixture.DatabasePath);
        Assert.Equal("DryRunReady", fixture.Upgrade(apply: false).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Empty(fixture.Backups());

        GalateaDelegationStoreUpgradeResult result = fixture.Upgrade(apply: true);

        Assert.Equal(before, ReadBusinessRows(result.BackupPath!, normalize: false));
        using GalateaDelegationSqliteStore store = fixture.Open();
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        GalateaOutboundMailSnapshot first = snapshot.Mails[0];
        Assert.Equal("original task", first.Body);
        Assert.Equal(7, first.RecoveryFailureCount);
        Assert.Equal("BINDING_UNAVAILABLE", first.RecoveryLastCode);
        Assert.Equal(123456L, first.NextRetryAtUnixTimeMilliseconds);
        Assert.Equal(GalateaDurableMailState.Queued, first.State);
        Assert.Null(first.OperationId);
        Assert.Null(first.RequestedThreadId);
        Assert.All(snapshot.Mails.Skip(1), mail => {
            Assert.Equal(0, mail.RecoveryFailureCount);
            Assert.Null(mail.RecoveryLastCode);
            Assert.Null(mail.NextRetryAtUnixTimeMilliseconds);
        });
        Assert.Equal(GalateaDelegationRouteState.Binding, snapshot.Route.State);
        Assert.Equal("bind", snapshot.Route.BindingOperationId);
        Assert.Null(snapshot.Route.ActiveDispatchId);
        Assert.Empty(snapshot.Notices);
        Assert.Equal(0L, Scalar(fixture.DatabasePath,
            "SELECT COUNT(*) FROM pragma_table_info('route_binding') WHERE name LIKE 'ensure_%' OR name = 'next_ensure_at_ms';"));
    }

    [Theory]
    [InlineData(1, "OutcomeUnknown", 8)]
    [InlineData(1, "Accepted", 11)]
    [InlineData(2, "OutcomeUnknown", 11)]
    [InlineData(2, "Accepted", 8)]
    public async Task ExhaustedLegacyRecovery_FirstPulseFinishesLocallyWithoutAnyRpc(
        int version, string state, int failures
    ) {
        using var fixture = new MigrationFixture(state, version);
        Execute(fixture.DatabasePath, $"""
            UPDATE outbound_mail SET reconcile_attempt_count = {failures},
                reconcile_last_code = 'INSPECTION_UNAVAILABLE', next_reconcile_at_ms = 0
            WHERE state = '{state}';
            """);
        _ = fixture.Upgrade(apply: true);
        string dispatchId;
        await using var transport = new InspectOnlyTransport();
        using (GalateaDelegationSqliteStore store = fixture.Open()) {
            GalateaOutboundMailSnapshot before = store.ReadSnapshot().Mails[0];
            dispatchId = before.DispatchId;
            Assert.Equal(failures, before.RecoveryFailureCount);
            Assert.Equal("INSPECTION_UNAVAILABLE", before.RecoveryLastCode);
            Assert.Equal("original-thread", before.RequestedThreadId);
            Assert.Equal(state == "Accepted" ? "original-turn" : null, before.AcceptedTurnId);
            Assert.Empty(store.ReadSnapshot().Notices);
            var driver = new GalateaDurableDelegationDriver(store, transport, fixture.HomeDirectory, new MigrationClock());

            _ = await driver.PulseAsync();

            Assert.Empty(transport.Requests);
            Assert.Equal(0, transport.StartCalls);
            Assert.Equal(0, transport.EnsureCalls);
        }
        using GalateaDelegationSqliteStore reopened = fixture.Open();
        GalateaDelegationStateSnapshot after = reopened.ReadSnapshot();
        GalateaOutboundMailSnapshot finished = after.Mails[0];
        Assert.Equal(dispatchId, finished.DispatchId);
        Assert.Equal(GalateaDurableMailState.TerminalFailed, finished.State);
        Assert.Equal("RESULT_UNCONFIRMED", finished.TerminalCode);
        Assert.Equal(failures, finished.RecoveryFailureCount);
        Assert.Equal("INSPECTION_UNAVAILABLE", finished.RecoveryLastCode);
        Assert.Null(finished.NextRetryAtUnixTimeMilliseconds);
        GalateaReplyNoticeSnapshot notice = Assert.Single(after.Notices);
        Assert.Equal(dispatchId, notice.DispatchId);
        Assert.Equal(GalateaReplyNoticeKind.DeliveryFailure, notice.Kind);
        Assert.Equal("RESULT_UNCONFIRMED", notice.Code);
        Assert.Null(after.Route.ActiveDispatchId);
        Assert.Equal(GalateaDurableMailState.Queued, after.Mails[1].State);
    }

    [Theory]
    [InlineData(1, "Queued", "UPDATE outbound_mail SET reconcile_attempt_count = 1, reconcile_last_code = 'BROKEN', next_reconcile_at_ms = 0 WHERE state = 'Queued';")]
    [InlineData(2, "Started", "UPDATE outbound_mail SET reconcile_attempt_count = 1, reconcile_last_code = 'BROKEN', next_reconcile_at_ms = 0 WHERE state = 'Started';")]
    [InlineData(1, "Binding", "UPDATE route_binding SET ensure_attempt_count = 1;")]
    [InlineData(2, "Binding", "UPDATE route_binding SET ensure_last_code = 'BROKEN';")]
    [InlineData(2, "Queued", "UPDATE route_binding SET ensure_attempt_count = 1, ensure_last_code = 'BROKEN', next_ensure_at_ms = 0;")]
    [InlineData(1, "TerminalFailed", "UPDATE outbound_mail SET terminal_stage = 'local-recovery', terminal_code = 'RESULT_UNCONFIRMED' WHERE state = 'TerminalFailed';")]
    [InlineData(2, "TerminalFailed", "UPDATE outbound_mail SET accepted_thread_id = NULL, accepted_turn_id = NULL WHERE state = 'TerminalFailed';")]
    [InlineData(2, "Binding", "UPDATE outbound_mail SET state = 'Unrouted', route_class = 'Unrouted';")]
    public void InvalidLegacyShape_RejectsBeforeBackupOrMutation(int version, string state, string mutation) {
        using var fixture = new MigrationFixture(state, version);
        Execute(fixture.DatabasePath, mutation);
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);

        Assert.Throws<InvalidDataException>(() => fixture.Upgrade(apply: false));
        Assert.Throws<InvalidDataException>(() => fixture.Upgrade(apply: true));

        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Empty(fixture.Backups());
        Assert.Equal((long)version, Scalar(fixture.DatabasePath, "PRAGMA user_version;"));
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
        internal MigrationFixture(string state, int version = 2) {
            LegacyVersion = version;
            _root = Path.Combine(Path.GetTempPath(), "galatea-upgrade-test-" + Guid.NewGuid().ToString("N"));
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(_root);
            TestDirectorySafety.CreateDirectoryNew(_root);
            HomeDirectory = Path.Combine(_root, "home");
            TestDirectorySafety.CreateDirectoryNew(HomeDirectory);
            string sourceDirectory = Path.Combine(_root, "v3-source");
            StoreDirectory = Path.Combine(_root, "legacy-target");
            TestDirectorySafety.CreateDirectoryNew(StoreDirectory);
            using (File.Create(Path.Combine(StoreDirectory, GalateaDelegationSqliteStore.LockFileName))) { }
            EventAddress head = EventAddressTextCodec.Parse(Address(2));
            using (GalateaDelegationSqliteStore store = GalateaDelegationSqliteStore.CreateNew(
                sourceDirectory, Owner,
                new(new EventJournalPhysicalAppendFrontier(head.SegmentNumber, head.Ticket.EndOffsetExclusive), Address(2)),
                Limits)) {
                Populate(store, state);
                // Populate the current state machine, then explicitly export
                // only the facts available to the historical fixture schema.
            }
            string sourcePath = Path.Combine(sourceDirectory, GalateaDelegationSqliteStore.DatabaseFileName);
            ConvertSourceToHistoricalContent(sourcePath);
            using (GalateaDelegationSqliteStore legacyView = GalateaDelegationSqliteStore.OpenExisting(sourceDirectory, Owner, Limits)) {
                Snapshot = JsonSerializer.Serialize(legacyView.ReadSnapshot());
            }
            BusinessRows = ReadBusinessRows(sourcePath);
            CreateLegacy(sourcePath, DatabasePath, version);
            LegacyRows = ReadBusinessRows(DatabasePath, normalize: false);
        }
        internal string HomeDirectory { get; }
        internal string StoreDirectory { get; }
        internal string DatabasePath => Path.Combine(StoreDirectory, GalateaDelegationSqliteStore.DatabaseFileName);
        internal string Snapshot { get; }
        internal string BusinessRows { get; }
        internal int LegacyVersion { get; }
        internal string LegacyRows { get; }
        internal string[] Backups() => Directory.GetFiles(StoreDirectory, $"*.v{LegacyVersion}-backup-*");
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
        ], GalateaDelegationTestInputs.Sender(store, "Galatea")));
        if (state == "Queued") { return; }
        GalateaOutboundMailSnapshot mail = store.ReadSnapshot().Mails[0];
        GalateaRouteBindingSnapshot route = store.BeginThreadBinding(
            "bind", store.ReadSnapshot().Route.Revision, mail.DispatchId, mail.Revision);
        if (state == "Binding") { return; }
        route = store.CompleteThreadBinding("bind", "original-thread", route.Revision);
        mail = store.ReadSnapshot().Mails[0];
        mail = store.StartQueuedMail(mail.DispatchId, mail.Revision, route.Revision, GalateaDelegationTestInputs.Commitment(store, mail.DispatchId));
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
        SessionInputContent observation = GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction("player", GalateaDelegateTestConfiguration.PlayerSender),
            DateTimeOffset.UnixEpoch, GalateaDelegationTestInputs.Sender(store, "Galatea"),
            [GalateaDurableNoticeContent.Project(notice)]);
        lease = store.BindReplyLeaseObservationBase(lease.LeaseId, lease.Revision, Address(20), observation);
        if (state == "Leased") { return; }
        lease = store.RecordLeaseObservationCommitted(lease.LeaseId, lease.Revision, Address(21));
        store.ConsumeReplyLease(lease.LeaseId, lease.Revision, Address(22));
    }

    private static void ConvertSourceToHistoricalContent(string databasePath) {
        using SqliteConnection connection = Connect(databasePath, SqliteOpenMode.ReadWrite);
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT bound_input FROM reply_lease WHERE bound_input IS NOT NULL;";
        string? inputJson = read.ExecuteScalar() as string;
        if (inputJson is not null) {
            SessionInputContent input = JsonSerializer.Deserialize<SessionInputContent>(inputJson)!;
            PlayerTurnObservation parsed = GalateaObservationContent.ReadPlayerTurn(input);
            PlayerTurnNotice[] oldNotices = parsed.Notices.Select(notice => notice switch {
                PlayerTurnNotice.Reply reply => (PlayerTurnNotice)new PlayerTurnNotice.Reply(reply.Body),
                PlayerTurnNotice.DeliveryFailure failure => new PlayerTurnNotice.DeliveryFailure(failure.Detail!),
                _ => throw new InvalidDataException("Unexpected historical receipt fixture.")
            }).ToArray();
            string oldText = PlayerTurnObservationEnvelope.Wrap(parsed.WithNotices(oldNotices));
            using SqliteCommand bind = connection.CreateCommand();
            bind.CommandText = "UPDATE reply_lease SET bound_input=NULL, rendered_observation=$body, observation_utf8_bytes=$bytes, observation_sha256=$sha;";
            bind.Parameters.AddWithValue("$body", oldText);
            bind.Parameters.AddWithValue("$bytes", GalateaTaskCommitment.FromTask(oldText).Utf8Bytes);
            bind.Parameters.AddWithValue("$sha", GalateaTaskCommitment.FromTask(oldText).Sha256);
            bind.ExecuteNonQuery();
        }
        using SqliteCommand convert = connection.CreateCommand();
        convert.CommandText = """
            UPDATE outbound_mail SET content_format='legacy-task', sender_name=NULL, task_sha256=NULL, task_utf8_bytes=NULL;
            UPDATE reply_notice SET body=CASE WHEN kind='DeliveryFailure' THEN COALESCE(detail, code) ELSE body END,
                notice_format='legacy-text', sender_kind=NULL, sender_id=NULL, sender_name=NULL, detail=NULL, thread_id=NULL, turn_id=NULL;
            """;
        convert.ExecuteNonQuery();
    }

    private static void CreateLegacy(string sourcePath, string targetPath, int version) {
        using SqliteConnection target = Connect(targetPath, SqliteOpenMode.ReadWriteCreate);
        if (version is 3 or 4) {
            using (SqliteConnection source = Connect(sourcePath, SqliteOpenMode.ReadOnly)) {
                source.BackupDatabase(target);
            }
            using SqliteCommand downgrade = target.CreateCommand();
            downgrade.CommandText = """
                ALTER TABLE outbound_mail DROP COLUMN content_format;
                ALTER TABLE outbound_mail DROP COLUMN sender_name;
                ALTER TABLE outbound_mail DROP COLUMN task_sha256;
                ALTER TABLE outbound_mail DROP COLUMN task_utf8_bytes;
                ALTER TABLE reply_notice DROP COLUMN notice_format;
                ALTER TABLE reply_notice DROP COLUMN sender_kind;
                ALTER TABLE reply_notice DROP COLUMN sender_id;
                ALTER TABLE reply_notice DROP COLUMN sender_name;
                ALTER TABLE reply_notice DROP COLUMN detail;
                ALTER TABLE reply_notice DROP COLUMN thread_id;
                ALTER TABLE reply_notice DROP COLUMN turn_id;
                ALTER TABLE reply_lease DROP COLUMN bound_input;
                ALTER TABLE internal_mail_outbox DROP COLUMN bound_input;
                DROP INDEX ix_internal_mail_target_state;
                DROP INDEX ux_internal_mail_message_id;
                DROP TABLE internal_mail_outbox;
                ALTER TABLE delegation_meta RENAME TO delegation_meta_v4;
                CREATE TABLE delegation_meta (
                    singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                    schema_version INTEGER NOT NULL CHECK(schema_version = 3),
                    user_id TEXT NOT NULL,
                    session_repository_id TEXT NOT NULL,
                    capture_frontier_segment_number INTEGER NOT NULL
                        CHECK(capture_frontier_segment_number BETWEEN 1 AND 4294967295),
                    capture_frontier_tail_offset INTEGER NOT NULL
                        CHECK(capture_frontier_tail_offset >= 4 AND capture_frontier_tail_offset % 4 = 0),
                    baseline_selected_head TEXT NULL,
                    maximum_queued_mails INTEGER NOT NULL CHECK(maximum_queued_mails >= 1),
                    maximum_task_utf8_bytes INTEGER NOT NULL CHECK(maximum_task_utf8_bytes >= 1),
                    maximum_reply_utf8_bytes INTEGER NOT NULL CHECK(maximum_reply_utf8_bytes >= 1),
                    maximum_inbox_replies INTEGER NOT NULL CHECK(maximum_inbox_replies >= 1),
                    maximum_inbox_utf8_bytes INTEGER NOT NULL
                        CHECK(maximum_inbox_utf8_bytes >= maximum_reply_utf8_bytes),
                    next_completion_sequence INTEGER NOT NULL CHECK(next_completion_sequence >= 1),
                    revision INTEGER NOT NULL CHECK(revision >= 0)
                ) STRICT;
                INSERT INTO delegation_meta (
                    singleton, schema_version, user_id, session_repository_id,
                    capture_frontier_segment_number, capture_frontier_tail_offset,
                    baseline_selected_head, maximum_queued_mails,
                    maximum_task_utf8_bytes, maximum_reply_utf8_bytes,
                    maximum_inbox_replies, maximum_inbox_utf8_bytes,
                    next_completion_sequence, revision
                ) SELECT singleton, 3, user_id, session_repository_id,
                    capture_frontier_segment_number, capture_frontier_tail_offset,
                    baseline_selected_head, maximum_queued_mails,
                    maximum_task_utf8_bytes, maximum_reply_utf8_bytes,
                    maximum_inbox_replies, maximum_inbox_utf8_bytes,
                    next_completion_sequence, revision
                FROM delegation_meta_v4;
                DROP TABLE delegation_meta_v4;
                PRAGMA user_version = 3;
                """;
            if (version == 4) {
                downgrade.CommandText = downgrade.CommandText
                    .Replace("DROP INDEX ix_internal_mail_target_state;", "", StringComparison.Ordinal)
                    .Replace("DROP INDEX ux_internal_mail_message_id;", "", StringComparison.Ordinal)
                    .Replace("DROP TABLE internal_mail_outbox;", "", StringComparison.Ordinal)
                    .Replace("schema_version = 3", "schema_version = 4", StringComparison.Ordinal)
                    .Replace("SELECT singleton, 3,", "SELECT singleton, 4,", StringComparison.Ordinal)
                    .Replace("PRAGMA user_version = 3", "PRAGMA user_version = 4", StringComparison.Ordinal);
            }
            downgrade.ExecuteNonQuery();
            return;
        }
        using (SqliteCommand ddl = target.CreateCommand()) {
            // V2 differs from the captured V1 DDL only in its version and
            // the three retired route-policy columns. Keep this legacy fixture
            // independent of the current production schema/migration SQL.
            string schema = GalateaDelegationV1Schema.Sql;
            if (version == 2) {
                schema = schema.Replace("schema_version = 1", "schema_version = 2", StringComparison.Ordinal)
                    .Replace("route_policy_fingerprint TEXT NOT NULL,", "", StringComparison.Ordinal)
                    .Replace("frozen_route_policy_fingerprint TEXT NULL,", "", StringComparison.Ordinal)
                    .Replace("policy_fingerprint TEXT NOT NULL,", "", StringComparison.Ordinal);
            }
            ddl.CommandText = schema
                + $"PRAGMA application_id = {GalateaDelegationSqliteStore.ApplicationId}; PRAGMA user_version = {version};";
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
                "schema_version" => version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "reconcile_attempt_count" => Quote("recovery_failure_count"),
                "reconcile_last_code" => Quote("recovery_last_code"),
                "next_reconcile_at_ms" => Quote("next_retry_at_ms"),
                "ensure_attempt_count" => "0",
                "ensure_last_code" or "next_ensure_at_ms" => "NULL",
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
    private static string ReadBusinessRows(string path, bool normalize = true) {
        using SqliteConnection connection = Connect(path, SqliteOpenMode.ReadOnly);
        var tables = new List<object>();
        foreach (string table in Tables) {
            string[] storedColumns = Columns(connection, table).Where(column => !normalize ||
                column is not ("route_policy_fingerprint" or "policy_fingerprint" or "frozen_route_policy_fingerprint"
                    or "schema_version" or "ensure_attempt_count" or "ensure_last_code" or "next_ensure_at_ms"
                    or "content_format" or "sender_name" or "task_sha256" or "task_utf8_bytes" or "bound_input"
                    or "notice_format" or "sender_kind" or "sender_id" or "detail")
                && !(table == "reply_notice" && column is "thread_id" or "turn_id")).ToArray();
            string[] columns = storedColumns.Select(column => normalize ? column switch {
                "reconcile_attempt_count" => "recovery_failure_count",
                "reconcile_last_code" => "recovery_last_code",
                "next_reconcile_at_ms" => "next_retry_at_ms",
                _ => column
            } : column).ToArray();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(',', storedColumns.Select(Quote))} FROM {Quote(table)} ORDER BY {string.Join(',', storedColumns.Select(Quote))};";
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
    private static void Execute(string path, string sql) {
        using SqliteConnection connection = Connect(path, SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
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
        internal int EnsureCalls { get; private set; }
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(GalateaEnsureDelegateBindingRequest request, CancellationToken ct) {
            EnsureCalls++;
            throw new InvalidOperationException("Existing binding must survive upgrade.");
        }
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
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;
    }
}
