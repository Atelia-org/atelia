using Atelia.Galatea.Server;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationSqliteStoreTests {
    [Fact]
    public void CreateOpen_RequiresExactIdentityLimitsAndExclusiveOwner() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreIdentity identity = Identity();
        GalateaDelegationStoreLimits limits = Limits();
        using (GalateaDelegationSqliteStore store =
               GalateaDelegationSqliteStore.CreateNew(
                   directory.Path,
                   identity,
                   limits)) {
            GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
            Assert.Equal(identity, snapshot.Identity);
            Assert.Equal(limits, snapshot.Limits);
            Assert.Empty(snapshot.Captures);
            Assert.Equal(GalateaDelegationRouteState.Unbound,
                snapshot.Route.State);

            Assert.ThrowsAny<IOException>(() =>
                GalateaDelegationSqliteStore.OpenExisting(
                    directory.Path,
                    identity,
                    limits));
        }

        using GalateaDelegationSqliteStore reopened =
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path,
                identity,
                limits);
        Assert.Equal(identity, reopened.ReadSnapshot().Identity);
        reopened.Dispose();

        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path,
                identity with { UserId = "another-user" },
                limits));
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path,
                identity,
                limits with { MaximumInboxReplies = 15 }));
    }

    [Fact]
    public void CreateNew_PreexistingLockOrDirectory_IsNeverDeleted() {
        using var directory = new StoreDirectory(createLeaf: true);
        string lockPath = System.IO.Path.Combine(
            directory.Path,
            GalateaDelegationSqliteStore.LockFileName
        );
        using var owner = new FileStream(
            lockPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None
        );

        Assert.Throws<IOException>(() =>
            GalateaDelegationSqliteStore.CreateNew(
                directory.Path,
                Identity(),
                Limits()));

        Assert.True(File.Exists(lockPath));
        Assert.False(File.Exists(System.IO.Path.Combine(
            directory.Path,
            GalateaDelegationSqliteStore.DatabaseFileName
        )));
    }

    [Fact]
    public void OpenExisting_RejectsSymlinkLifetimeLockWithoutFollowingIt() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreIdentity identity = Identity();
        GalateaDelegationStoreLimits limits = Limits();
        using (GalateaDelegationSqliteStore store =
               GalateaDelegationSqliteStore.CreateNew(
                   directory.Path, identity, limits)) { }
        string lockPath = System.IO.Path.Combine(
            directory.Path,
            GalateaDelegationSqliteStore.LockFileName
        );
        string target = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "atelia-galatea-lock-target-" + Guid.NewGuid().ToString("N")
        );
        using (File.Create(target)) { }
        try {
            File.Delete(lockPath);
            File.CreateSymbolicLink(lockPath, target);

            Assert.Throws<InvalidDataException>(() =>
                GalateaDelegationSqliteStore.OpenExisting(
                    directory.Path,
                    identity,
                    limits));
            Assert.True(File.Exists(target));
        }
        finally {
            File.Delete(lockPath);
            File.Delete(target);
        }
    }

    [Fact]
    public void Capture_EmptyTombstoneAndDuplicate_AreDurableAndZeroWrite() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreIdentity identity = Identity();
        GalateaDelegationStoreLimits limits = Limits();
        using var store = GalateaDelegationSqliteStore.CreateNew(
            directory.Path,
            identity,
            limits
        );
        GalateaDelegationCaptureRequest empty = Capture(
            Address(10),
            intents: []
        );

        GalateaDelegationCaptureResult first = store.CaptureActionBatch(empty);
        Assert.Equal(GalateaDelegationCaptureDisposition.Captured,
            first.Disposition);
        Assert.Empty(first.DispatchIds);
        Assert.Equal(1, first.StoreRevision);

        GalateaDelegationCaptureResult duplicate =
            store.CaptureActionBatch(empty with {
                Intents = [Mail("Codex", "nondeterministic later output")]
            });
        Assert.Equal(GalateaDelegationCaptureDisposition.AlreadyCaptured,
            duplicate.Disposition);
        Assert.Empty(duplicate.DispatchIds);
        Assert.Equal(first.StoreRevision, duplicate.StoreRevision);
        Assert.Equal(first.StoreRevision, store.ReadSnapshot().StoreRevision);

        Assert.Throws<InvalidDataException>(() =>
            store.CaptureActionBatch(empty with {
                VisibleActionSha256 = Sha('b')
            }));
    }

    [Fact]
    public void Capture_WholeBatchIsAtomicAndStableAcrossReopen() {
        using var directory = new StoreDirectory();
        bool failBeforeCommit = true;
        var hooks = new GalateaDelegationStoreTestHooks(
            BeforeCommit: operation => {
                if (failBeforeCommit
                    && operation == "capture-action-batch") {
                    throw new IOException("injected before commit");
                }
            }
        );
        GalateaDelegationStoreIdentity identity = Identity();
        GalateaDelegationStoreLimits limits = Limits();
        using (var store = GalateaDelegationSqliteStore.CreateNew(
                   directory.Path, identity, limits, hooks)) {
            GalateaDelegationCaptureRequest request = Capture(
                Address(11),
                [
                    Mail("Codex", "first"),
                    Mail("someone else", "second")
                ]
            );
            Assert.Throws<IOException>(() => store.CaptureActionBatch(request));
            Assert.Empty(store.ReadSnapshot().Captures);
            Assert.Empty(store.ReadSnapshot().Mails);

            failBeforeCommit = false;
            GalateaDelegationCaptureResult result =
                store.CaptureActionBatch(request);
            Assert.Equal(2, result.DispatchIds.Count);
            GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
            Assert.Single(snapshot.Captures);
            Assert.Equal(2, snapshot.Captures[0].ArtifactCount);
            Assert.Equal(
                [
                    GalateaDurableMailState.Queued,
                    GalateaDurableMailState.Unrouted
                ],
                snapshot.Mails.Select(static value => value.State)
            );
            Assert.Equal(result.DispatchIds, snapshot.Mails
                .Select(static value => value.DispatchId));
        }

        ExecuteSql(System.IO.Path.Combine(
            directory.Path,
            GalateaDelegationSqliteStore.DatabaseFileName
        ), "VACUUM;");
        using var reopened = GalateaDelegationSqliteStore.OpenExisting(
            directory.Path, identity, limits);
        Assert.Equal([0, 1], reopened.ReadSnapshot().Mails
            .Select(static mail => mail.ArtifactOrdinal));
    }

    [Fact]
    public void EmptyCaptureTombstones_FailClosedAtExactLifetimeCapacity() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreIdentity identity = Identity();
        GalateaDelegationStoreLimits limits = Limits();
        using (GalateaDelegationSqliteStore store =
               GalateaDelegationSqliteStore.CreateNew(
                   directory.Path, identity, limits)) { }
        string database = System.IO.Path.Combine(
            directory.Path,
            GalateaDelegationSqliteStore.DatabaseFileName
        );
        ExecuteSql(database, """
            WITH RECURSIVE sequence(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM sequence WHERE value < 4095
            )
            INSERT INTO action_capture(
                source_action_address, capture_sequence,
                visible_action_sha256,
                visible_action_utf8_bytes, extractor_contract_id,
                artifact_count, revision
            )
            SELECT printf('ej1:%016x0000000100000000', value), value,
                   'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                   12, 'extractor-contract-v1', 0, 0
            FROM sequence;
            UPDATE delegation_meta SET revision = 4095 WHERE singleton = 1;
            """);

        using var reopened = GalateaDelegationSqliteStore.OpenExisting(
            directory.Path, identity, limits);
        _ = reopened.CaptureActionBatch(Capture(Address(5000), []));
        Assert.Equal(4096, reopened.ReadSnapshot().Captures.Count);
        Assert.Throws<InvalidOperationException>(() =>
            reopened.CaptureActionBatch(Capture(Address(5001), [])));
        Assert.Equal(4096, reopened.ReadSnapshot().Captures.Count);
    }

    [Fact]
    public void CommitOutcomeUncertain_ReopensAndClassifiesExactPostState() {
        using var directory = new StoreDirectory();
        bool throwOnce = true;
        var hooks = new GalateaDelegationStoreTestHooks(
            AfterCommitBeforeReturn: operation => {
                if (throwOnce && operation == "capture-action-batch") {
                    throwOnce = false;
                    throw new IOException("injected after commit");
                }
            }
        );
        using var store = GalateaDelegationSqliteStore.CreateNew(
            directory.Path,
            Identity(),
            Limits(),
            hooks
        );

        GalateaDelegationCaptureResult result = store.CaptureActionBatch(
            Capture(Address(12), [Mail("Codex", "body")])
        );

        Assert.Equal(GalateaDelegationCaptureDisposition.Captured,
            result.Disposition);
        Assert.Single(store.ReadSnapshot().Mails);
    }

    [Fact]
    public void MultiRowTransitions_UncertainCommitClassifyTerminalAndLease() {
        var remaining = new HashSet<string>(StringComparer.Ordinal) {
            "start-queued-mail",
            "record-terminal-mail",
            "begin-reply-lease-membership",
            "rollback-reply-lease"
        };
        var hooks = new GalateaDelegationStoreTestHooks(
            AfterCommitBeforeReturn: operation => {
                if (remaining.Remove(operation)) {
                    throw new IOException("injected after " + operation);
                }
            }
        );
        using var fixture = new RoutedStore(hooks: hooks);
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        GalateaRouteBindingSnapshot binding = fixture.Store.BeginThreadBinding(
            "bind", snapshot.Route.Revision);
        GalateaRouteBindingSnapshot bound = fixture.Store.CompleteThreadBinding(
            "bind", "thread", binding.Revision);
        GalateaOutboundMailSnapshot started = fixture.Store.StartQueuedMail(
            snapshot.Mails[0].DispatchId,
            snapshot.Mails[0].Revision,
            bound.Revision
        );

        GalateaReplyNoticeSnapshot notice = fixture.Store.RecordCompletedMail(
            started.DispatchId,
            started.Revision,
            "thread",
            "turn",
            "reply"
        );
        Assert.Equal(GalateaReplyNoticeState.Ready, notice.State);
        GalateaReplyLeaseSnapshot lease =
            fixture.Store.BeginReplyLeaseMembership(
                "lease",
                "player",
                [new(notice.NoticeId, notice.Revision)]
            );
        Assert.Equal(GalateaReplyLeaseState.CutoffFrozen, lease.State);
        GalateaReplyLeaseSnapshot durable = Assert.IsType<
            GalateaReplyLeaseSnapshot>(fixture.Store.ReadSnapshot().ActiveLease);
        Assert.Equal(lease.LeaseId, durable.LeaseId);
        Assert.Equal(lease.State, durable.State);
        Assert.Equal(lease.NoticeIds, durable.NoticeIds);
        fixture.Store.RollbackReplyLease(lease.LeaseId, lease.Revision);
        Assert.Empty(remaining);
        Assert.Null(fixture.Store.ReadSnapshot().ActiveLease);
        Assert.Equal(GalateaReplyNoticeState.Ready,
            fixture.Store.ReadSnapshot().Notices.Single().State);
    }

    [Fact]
    public void MailStateMachine_BindsBeforeStart_AndOutcomeUnknownNeverRequeues() {
        using var fixture = new RoutedStore(maximumInboxReplies: 4);
        GalateaDelegationStateSnapshot initial = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = initial.Mails[0];
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.StartQueuedMail(
                mail.DispatchId, mail.Revision,
                initial.Route.Revision));

        GalateaRouteBindingSnapshot binding = fixture.Store.BeginThreadBinding(
            "bind-op", initial.Route.Revision);
        GalateaRouteBindingSnapshot bound = fixture.Store.CompleteThreadBinding(
            "bind-op", "thread-1", binding.Revision);
        GalateaOutboundMailSnapshot started = fixture.Store.StartQueuedMail(
            mail.DispatchId, mail.Revision, bound.Revision);
        Assert.Equal(GalateaDurableMailState.Started, started.State);
        Assert.Equal("thread-1", started.RequestedThreadId);
        Assert.Equal(started.DispatchId, started.OperationId);
        Assert.Equal(mail.DispatchId,
            fixture.Store.ReadSnapshot().Route.ActiveDispatchId);

        GalateaOutboundMailSnapshot unknown =
            fixture.Store.MarkMailOutcomeUnknown(
                mail.DispatchId,
                started.Revision,
                "SIDECAR_EOF",
                nowUnixTimeMilliseconds: 1000
            );
        Assert.Equal(GalateaDurableMailState.OutcomeUnknown, unknown.State);
        Assert.Equal(GalateaDelegationRouteState.Bound,
            fixture.Store.ReadSnapshot().Route.State);
        Assert.Equal(mail.DispatchId,
            fixture.Store.ReadSnapshot().Route.ActiveDispatchId);
        Assert.Equal(1, unknown.ReconcileAttemptCount);
        Assert.Equal(2000,
            unknown.NextReconcileAtUnixTimeMilliseconds);
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.RecordOutcomeUnknownReconcileMiss(
                mail.DispatchId,
                unknown.Revision,
                "NOT_FOUND",
                nowUnixTimeMilliseconds: 1999
            ));
        unknown = fixture.Store.RecordOutcomeUnknownReconcileMiss(
            mail.DispatchId,
            unknown.Revision,
            "NOT_FOUND",
            nowUnixTimeMilliseconds: 2000
        );
        Assert.Equal(GalateaDurableMailState.OutcomeUnknown, unknown.State);
        Assert.Equal(2, unknown.ReconcileAttemptCount);
        Assert.Equal("NOT_FOUND", unknown.ReconcileLastCode);
        Assert.Equal(4000,
            unknown.NextReconcileAtUnixTimeMilliseconds);
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.StartQueuedMail(
                fixture.Store.ReadSnapshot().Mails[1].DispatchId,
                0,
                fixture.Store.ReadSnapshot().Route.Revision));

        GalateaOutboundMailSnapshot accepted = fixture.Store.RecordMailAccepted(
            mail.DispatchId,
            unknown.Revision,
            "thread-1",
            "turn-1"
        );
        GalateaReplyNoticeSnapshot notice = fixture.Store.RecordCompletedMail(
            mail.DispatchId,
            accepted.Revision,
            "thread-1",
            "turn-1",
            "exact final"
        );
        GalateaDelegationStateSnapshot terminal = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaReplyNoticeState.Ready, notice.State);
        Assert.Null(terminal.Route.ActiveDispatchId);
        Assert.Equal(GalateaDurableMailState.TerminalCompleted,
            terminal.Mails[0].State);
        Assert.Null(terminal.Mails[0].Body);
        long revisionAfterTerminal = terminal.StoreRevision;
        GalateaReplyNoticeSnapshot repeated = fixture.Store.RecordCompletedMail(
            mail.DispatchId,
            accepted.Revision,
            "thread-1",
            "turn-1",
            "exact final"
        );
        Assert.Equal(notice, repeated);
        Assert.Equal(revisionAfterTerminal,
            fixture.Store.ReadSnapshot().StoreRevision);
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.RecordFailedMail(
                mail.DispatchId,
                accepted.Revision,
                "thread-1",
                "turn-1",
                "terminal",
                "CONFLICTING_STATUS",
                "conflicting failure"
            ));
        Assert.Equal(GalateaDelegationRouteState.Quarantined,
            fixture.Store.ReadSnapshot().Route.State);
    }

    [Fact]
    public void BindingIdentityConflict_CanDurablyQuarantineBeforeAnyMailStart() {
        using var fixture = new RoutedStore();
        GalateaRouteBindingSnapshot route = fixture.Store.ReadSnapshot().Route;
        route = fixture.Store.BeginThreadBinding("bind", route.Revision);

        GalateaRouteBindingSnapshot quarantined =
            fixture.Store.QuarantineThreadBinding(
                "bind",
                route.Revision,
                "OWNERSHIP_CONFLICT"
            );

        Assert.Equal(GalateaDelegationRouteState.Quarantined,
            quarantined.State);
        Assert.Null(quarantined.ThreadId);
        Assert.All(fixture.Store.ReadSnapshot().Mails,
            static mail => Assert.Equal(
                GalateaDurableMailState.Queued,
                mail.State));
    }

    [Fact]
    public void FullInbox_PreventsStart_ConsumeReleasesCapacity() {
        using var fixture = new RoutedStore(
            maximumInboxReplies: 1,
            maximumInboxUtf8Bytes: 4 * 1024,
            maximumReplyUtf8Bytes: 64
        );
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        GalateaRouteBindingSnapshot binding = fixture.Store.BeginThreadBinding(
            "bind", snapshot.Route.Revision);
        GalateaRouteBindingSnapshot bound = fixture.Store.CompleteThreadBinding(
            "bind", "thread", binding.Revision);
        GalateaOutboundMailSnapshot first = fixture.Store.StartQueuedMail(
            snapshot.Mails[0].DispatchId,
            snapshot.Mails[0].Revision,
            bound.Revision
        );
        _ = fixture.Store.RecordCompletedMail(
            first.DispatchId,
            first.Revision,
            "thread",
            "turn-1",
            "reply"
        );
        snapshot = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot second = snapshot.Mails[1];
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Store.StartQueuedMail(
                second.DispatchId,
                second.Revision,
                snapshot.Route.Revision
            ));

        GalateaReplyLeaseSnapshot lease =
            fixture.Store.BeginReplyLeaseMembership(
                "lease-1",
                "player",
                [new(snapshot.Notices[0].NoticeId,
                    snapshot.Notices[0].Revision)]
            );
        lease = fixture.Store.BindReplyLeaseObservationBase(
            lease.LeaseId,
            lease.Revision,
            Address(20),
            Observation("player", "reply")
        );
        lease = fixture.Store.RecordLeaseObservationCommitted(
            lease.LeaseId,
            lease.Revision,
            Address(21)
        );
        fixture.Store.ConsumeReplyLease(
            lease.LeaseId,
            lease.Revision,
            Address(22)
        );
        snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaReplyNoticeState.Consumed,
            snapshot.Notices[0].State);

        GalateaOutboundMailSnapshot newlyStarted =
            fixture.Store.StartQueuedMail(
                second.DispatchId,
                second.Revision,
                snapshot.Route.Revision
            );
        Assert.Equal(GalateaDurableMailState.Started, newlyStarted.State);
    }

    [Fact]
    public void Lease_RollbackAllowsSameNoticeToBeLeasedAgain() {
        using var fixture = RoutedStore.WithOneReadyNotice();
        GalateaReplyNoticeSnapshot notice =
            fixture.Store.ReadSnapshot().Notices.Single();
        GalateaReplyLeaseSnapshot first =
            fixture.Store.BeginReplyLeaseMembership(
                "lease-first",
                "player",
                [new(notice.NoticeId, notice.Revision)]
            );
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.BindReplyLeaseObservationBase(
                first.LeaseId,
                first.Revision,
                Address(30),
                Observation("changed player", "reply")
            ));
        first = fixture.Store.BindReplyLeaseObservationBase(
            first.LeaseId,
            first.Revision,
            Address(30),
            Observation("player", "reply")
        );
        fixture.Store.RollbackReplyLease(first.LeaseId, first.Revision);

        notice = fixture.Store.ReadSnapshot().Notices.Single();
        Assert.Equal(GalateaReplyNoticeState.Ready, notice.State);
        GalateaReplyLeaseSnapshot second =
            fixture.Store.BeginReplyLeaseMembership(
                "lease-second",
                "player again",
                [new(notice.NoticeId, notice.Revision)]
            );
        Assert.Equal(GalateaReplyLeaseState.CutoffFrozen, second.State);
        Assert.Equal(notice.NoticeId, second.NoticeIds.Single());
    }

    [Fact]
    public void Lease_ConsumeIsDurableAndConsumedNoticeCannotRearm() {
        bool injectConsumeUncertain = true;
        using var fixture = RoutedStore.WithOneReadyNotice(
            new GalateaDelegationStoreTestHooks(
                AfterCommitBeforeReturn: operation => {
                    if (injectConsumeUncertain
                        && operation == "consume-reply-lease") {
                        injectConsumeUncertain = false;
                        throw new IOException("injected after consume");
                    }
                }
            )
        );
        GalateaReplyNoticeSnapshot notice =
            fixture.Store.ReadSnapshot().Notices.Single();
        GalateaReplyLeaseSnapshot lease =
            fixture.Store.BeginReplyLeaseMembership(
                "lease",
                "player",
                [new(notice.NoticeId, notice.Revision)]
            );
        Assert.Null(lease.ExpectedSessionHead);
        Assert.Equal(GalateaReplyLeaseState.CutoffFrozen, lease.State);
        lease = fixture.Store.BindReplyLeaseObservationBase(
            lease.LeaseId,
            lease.Revision,
            Address(40),
            Observation("player", "reply")
        );
        Assert.Equal(GalateaReplyLeaseState.ObservationBound, lease.State);
        lease = fixture.Store.RecordLeaseObservationCommitted(
            lease.LeaseId,
            lease.Revision,
            Address(41)
        );
        fixture.Store.ConsumeReplyLease(
            lease.LeaseId,
            lease.Revision,
            Address(42)
        );
        Assert.False(injectConsumeUncertain);
        fixture.Reopen();
        GalateaDelegationStateSnapshot settled = fixture.Store.ReadSnapshot();
        Assert.Null(settled.ActiveLease);
        Assert.Equal(GalateaReplyNoticeState.Consumed,
            settled.Notices.Single().State);
        Assert.Equal(Address(42),
            settled.Notices.Single().ConsumedActionAddress);
        Assert.Equal(0, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_lease;"
        ));
        Assert.Equal(0, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_lease_item;"
        ));
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.BeginReplyLeaseMembership(
                "lease-again",
                "player again",
                [new(notice.NoticeId,
                    settled.Notices.Single().Revision)]
            ));
    }

    [Fact]
    public void StrictOpen_RejectsUnknownSchemaAndCorruptCurrentState() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreIdentity identity = Identity();
        GalateaDelegationStoreLimits limits = Limits();
        using (var store = GalateaDelegationSqliteStore.CreateNew(
                   directory.Path, identity, limits)) {
            _ = store.CaptureActionBatch(
                Capture(Address(50), [Mail("Codex", "body")])
            );
        }
        string database = System.IO.Path.Combine(
            directory.Path,
            GalateaDelegationSqliteStore.DatabaseFileName
        );
        ExecuteSql(database, "CREATE TABLE unexpected(value TEXT) STRICT;");
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path, identity, limits));
        ExecuteSql(database, "DROP TABLE unexpected;");
        ExecuteSql(database, """
            DROP INDEX ux_outbound_source_ordinal;
            CREATE UNIQUE INDEX ux_outbound_source_ordinal
            ON outbound_mail(dispatch_id);
            """);
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path, identity, limits));
        ExecuteSql(database, """
            DROP INDEX ux_outbound_source_ordinal;
            CREATE UNIQUE INDEX ux_outbound_source_ordinal
            ON outbound_mail(source_action_address, artifact_ordinal);
            """);
        ExecuteSql(database, "UPDATE outbound_mail SET body = NULL;");
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path, identity, limits));
        ExecuteSql(database, "UPDATE outbound_mail SET body = 'body';");
        ExecuteSql(database, """
            UPDATE outbound_mail
            SET state = 'Started', operation_id = NULL,
                requested_thread_id = NULL;
            """);
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path, identity, limits));
    }

    private static GalateaDelegationStoreIdentity Identity() => new(
        "user",
        "repository-id",
        Address(1),
        Address(2),
        "route-policy-v1"
    );

    private static GalateaDelegationStoreLimits Limits(
        int maximumInboxReplies = 16,
        int maximumInboxUtf8Bytes = 16 * 1024,
        int maximumReplyUtf8Bytes = 1024
    ) => new(
        MaximumQueuedMails: 32,
        maximumReplyUtf8Bytes,
        maximumInboxReplies,
        maximumInboxUtf8Bytes
    );

    private static GalateaDelegationCaptureRequest Capture(
        string source,
        IReadOnlyList<SendMailIntent> intents
    ) => new(
        source,
        Sha('a'),
        VisibleActionUtf8Bytes: 12,
        "extractor-contract-v1",
        intents
    );

    private static SendMailIntent Mail(string recipient, string body) => new(
        recipient,
        Subject: null,
        body,
        InReplyToMessageId: null,
        EvidenceQuote: "sent it"
    );

    private static string Address(int value) =>
        $"ej1:{value:x16}0000000100000000";

    private static string Sha(char value) => new(value, 64);

    private static string Observation(string playerText, string reply) =>
        GalateaPlayerObservationEnvelope.Wrap(new GalateaPlayerObservation(
            playerText,
            [new GalateaReadyNotice.Reply(reply)]
        ));

    private static void ExecuteSql(string databasePath, string sql) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ExecuteScalarLong(string databasePath, string sql) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed class StoreDirectory : IDisposable {
        internal StoreDirectory(bool createLeaf = false) {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-galatea-delegation-store-"
                    + Guid.NewGuid().ToString("N")
            );
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(Path);
            if (createLeaf) {
                TestDirectorySafety.CreateDirectoryNew(Path);
            }
        }

        internal string Path { get; }

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(Path);
    }

    private sealed class RoutedStore : IDisposable {
        private readonly StoreDirectory _directory = new();
        private readonly GalateaDelegationStoreIdentity _identity = Identity();
        private readonly GalateaDelegationStoreLimits _limits;

        internal RoutedStore(
            int maximumInboxReplies = 4,
            int maximumInboxUtf8Bytes = 16 * 1024,
            int maximumReplyUtf8Bytes = 1024,
            GalateaDelegationStoreTestHooks? hooks = null
        ) {
            _limits = Limits(
                maximumInboxReplies,
                maximumInboxUtf8Bytes,
                maximumReplyUtf8Bytes
            );
            Store = GalateaDelegationSqliteStore.CreateNew(
                _directory.Path,
                _identity,
                _limits,
                hooks
            );
            _ = Store.CaptureActionBatch(Capture(
                Address(100),
                [Mail("Codex", "first"), Mail("Codex", "second")]
            ));
        }

        internal GalateaDelegationSqliteStore Store { get; private set; }
        internal string DatabasePath => System.IO.Path.Combine(
            _directory.Path,
            GalateaDelegationSqliteStore.DatabaseFileName
        );

        internal void Reopen() {
            Store.Dispose();
            Store = GalateaDelegationSqliteStore.OpenExisting(
                _directory.Path,
                _identity,
                _limits
            );
        }

        internal static RoutedStore WithOneReadyNotice(
            GalateaDelegationStoreTestHooks? hooks = null
        ) {
            var fixture = new RoutedStore(hooks: hooks);
            GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
            GalateaRouteBindingSnapshot binding = fixture.Store.BeginThreadBinding(
                "bind", snapshot.Route.Revision);
            GalateaRouteBindingSnapshot bound = fixture.Store.CompleteThreadBinding(
                "bind", "thread", binding.Revision);
            GalateaOutboundMailSnapshot started = fixture.Store.StartQueuedMail(
                snapshot.Mails[0].DispatchId,
                snapshot.Mails[0].Revision,
                bound.Revision
            );
            _ = fixture.Store.RecordCompletedMail(
                started.DispatchId,
                started.Revision,
                "thread",
                "turn",
                "reply"
            );
            return fixture;
        }

        public void Dispose() {
            Store.Dispose();
            _directory.Dispose();
        }
    }
}
