using Atelia.Galatea.Server;
using Atelia.Galatea.Server.Mailbox;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationSqliteStoreTests {
    [Fact]
    public void MailboxStatus_IsAggregateOnlyReadAndPreservesRevision() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreLimits limits = Limits();
        using GalateaDelegationSqliteStore store =
            GalateaDelegationSqliteStore.CreateNew(
                directory.Path,
                owner,
                Baseline(),
                limits
            );

        GalateaDelegationStateSnapshot emptyBefore = store.ReadSnapshot();
        Assert.Equal(
            GalateaMailboxStatusProjection.NoMail,
            store.ReadMailboxStatus()
        );
        Assert.Equal(
            emptyBefore.StoreRevision,
            store.ReadSnapshot().StoreRevision
        );

        _ = store.CaptureActionBatch(Capture(
            Address(99),
            [Mail("Codex", "secret-one"), Mail("Codex", "secret-two")]
        ));
        long revisionBeforeRead = store.ReadSnapshot().StoreRevision;
        GalateaMailboxStatusProjection status = store.ReadMailboxStatus();

        Assert.Equal(GalateaMailboxStatusState.Queued, status.State);
        Assert.Equal(2, status.QueuedCount);
        Assert.Equal(0, status.ReadyNoticeCount);
        Assert.Equal(0, status.AttemptCount);
        Assert.Null(status.Code);
        Assert.Null(status.NextRetryAtUnixTimeMilliseconds);
        Assert.Equal(revisionBeforeRead, store.ReadSnapshot().StoreRevision);
    }

    [Fact]
    public void MailboxStatus_ReadOnlyStoreIsReadableAndBytePreserving() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreLimits limits = Limits();
        using (GalateaDelegationSqliteStore writable =
               GalateaDelegationSqliteStore.CreateNew(
                   directory.Path, owner, Baseline(), limits)) {
            _ = writable.CaptureActionBatch(Capture(
                Address(98),
                [Mail("Codex", "secret")]
            ));
        }
        string databasePath = System.IO.Path.Combine(
            directory.Path,
            GalateaDelegationSqliteStore.DatabaseFileName
        );
        byte[] before = File.ReadAllBytes(databasePath);

        using (GalateaDelegationSqliteStore readOnly =
               GalateaDelegationSqliteStore.OpenExistingReadOnly(
                   directory.Path, owner, limits)) {
            GalateaMailboxStatusProjection status =
                readOnly.ReadMailboxStatus();
            Assert.Equal(GalateaMailboxStatusState.Queued, status.State);
            Assert.Equal(1, status.QueuedCount);
        }

        Assert.Equal(before, File.ReadAllBytes(databasePath));
        Assert.False(File.Exists(databasePath + "-journal"));
    }

    [Fact]
    public void MailboxStatus_FailsClosedOnOrphanActiveStateRow() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreLimits limits = Limits();
        using GalateaDelegationSqliteStore store =
            GalateaDelegationSqliteStore.CreateNew(
                directory.Path, owner, Baseline(), limits
            );
        _ = store.CaptureActionBatch(Capture(
            Address(97),
            [Mail("Codex", "secret")]
        ));
        ExecuteSql(
            System.IO.Path.Combine(
                directory.Path,
                GalateaDelegationSqliteStore.DatabaseFileName
            ),
            """
            UPDATE outbound_mail
            SET state = 'Started', operation_id = dispatch_id,
                requested_thread_id = 'thread';
            """
        );

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            store.ReadMailboxStatus
        );
        Assert.Contains(
            "active-mail authority is inconsistent",
            failure.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void AutomaticWakeReason_IsDeferredReadOnlyAndRetainsActiveLeaseRecoveryEvidence() {
        using var fixture = new RoutedStore();
        GalateaOutboundMailSnapshot mail = StartEarliest(fixture.Store, "thread-a");
        GalateaReplyNoticeSnapshot notice = fixture.Store.RecordCompletedMail(
            mail.DispatchId, mail.Revision, "thread-a", "turn-a", "reply"
        );
        long readyRevision = fixture.Store.ReadSnapshot().StoreRevision;

        Assert.Equal(
            GalateaAutomaticWakeReason.ReadyNotice,
            fixture.Store.ReadAutomaticWakeReason()
        );
        Assert.Equal(readyRevision, fixture.Store.ReadSnapshot().StoreRevision);

        _ = fixture.Store.BeginReplyLeaseMembership(
            "wake-active-lease",
            "player",
            [new(notice.NoticeId, notice.Revision)]
        );
        long leaseRevision = fixture.Store.ReadSnapshot().StoreRevision;

        Assert.Equal(
            GalateaAutomaticWakeReason.ActiveReplyLease,
            fixture.Store.ReadAutomaticWakeReason()
        );
        Assert.Equal(leaseRevision, fixture.Store.ReadSnapshot().StoreRevision);
    }

    [Fact]
    public void CreateOpen_RequiresExactIdentityLimitsAndExclusiveOwner() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreBaseline baseline = Baseline();
        GalateaDelegationStoreLimits limits = Limits();
        using (GalateaDelegationSqliteStore store =
               GalateaDelegationSqliteStore.CreateNew(
                   directory.Path,
                   owner,
                   baseline,
                   limits)) {
            GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
            Assert.Equal(owner, snapshot.Owner);
            Assert.Equal(baseline, snapshot.Baseline);
            Assert.Equal(limits, snapshot.Limits);
            Assert.Empty(snapshot.Captures);
            Assert.Equal(GalateaDelegationRouteState.Unbound,
                snapshot.Route.State);

            Assert.ThrowsAny<IOException>(() =>
                GalateaDelegationSqliteStore.OpenExisting(
                    directory.Path,
                    owner,
                    limits));
        }

        using GalateaDelegationSqliteStore reopened =
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path,
                owner,
                limits);
        GalateaDelegationStateSnapshot reopenedSnapshot =
            reopened.ReadSnapshot();
        Assert.Equal(baseline, reopened.Baseline);
        Assert.Equal(owner, reopenedSnapshot.Owner);
        Assert.Equal(baseline, reopenedSnapshot.Baseline);
        reopened.Dispose();

        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path,
                owner with { CharacterId = "another-user" },
                limits));
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path,
                owner,
                limits with { MaximumInboxReplies = 15 }));
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path,
                owner,
                limits with {
                    MaximumTaskUtf8Bytes =
                        limits.MaximumTaskUtf8Bytes - 1
                }));

        ExecuteSql(
            System.IO.Path.Combine(
                directory.Path,
                GalateaDelegationSqliteStore.DatabaseFileName
            ),
            "UPDATE delegation_meta SET baseline_selected_head = '"
                + Address(2, segmentNumber: 2)
                + "' WHERE singleton = 1;"
        );
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path,
                owner,
                limits));
    }

    [Fact]
    public void OpenExistingReadOnly_IsStrictLockedAndBytePreserving() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreBaseline baseline = Baseline();
        GalateaDelegationStoreLimits limits = Limits();
        GalateaDelegationCaptureRequest capture = Capture(
            Address(9),
            [Mail("Codex", "body")]
        );
        using (GalateaDelegationSqliteStore writable =
               GalateaDelegationSqliteStore.CreateNew(
                   directory.Path,
                   owner,
                   baseline,
                   limits)) {
            _ = writable.CaptureActionBatch(capture);
        }
        string databasePath = System.IO.Path.Combine(
            directory.Path,
            GalateaDelegationSqliteStore.DatabaseFileName
        );
        byte[] before = SHA256.HashData(File.ReadAllBytes(databasePath));

        using (GalateaDelegationSqliteStore readOnly =
               GalateaDelegationSqliteStore.OpenExistingReadOnly(
                   directory.Path,
                   owner,
                   limits)) {
            GalateaDelegationStateSnapshot snapshot = readOnly.ReadSnapshot();
            Assert.Equal(owner, snapshot.Owner);
            Assert.Equal(baseline, snapshot.Baseline);
            Assert.Single(snapshot.Mails);
            Assert.Throws<GalateaDelegationStoreReadOnlyException>(() =>
                readOnly.CaptureActionBatch(capture));
            Assert.Throws<GalateaDelegationStoreReadOnlyException>(() =>
                readOnly.BeginThreadBinding(
                    "bind-read-only",
                    snapshot.Route.Revision, snapshot.Mails[0].DispatchId, snapshot.Mails[0].Revision
                ));
            Assert.ThrowsAny<IOException>(() =>
                GalateaDelegationSqliteStore.OpenExisting(
                    directory.Path,
                    owner,
                    limits));
        }

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(databasePath)));
        Assert.False(File.Exists(databasePath + "-journal"));
        using GalateaDelegationSqliteStore reopened =
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path,
                owner,
                limits
            );
        Assert.Single(reopened.ReadSnapshot().Mails);
    }

    [Fact]
    public void DispatchIdentity_IsGolden() {
        string dispatch = GalateaDelegationDurableContract.CreateDispatchId(
            "user",
            EventAddressTextCodec.Parse(Address(1)),
            artifactOrdinal: 0
        );
        Assert.Equal(
            "gd1-71025e8de66efde57f5cd47da74a3f3b"
                + "d5e711cf45c1995162d3c2c4a43a1692",
            dispatch
        );
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
                Owner(),
                Baseline(),
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
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreLimits limits = Limits();
        using (GalateaDelegationSqliteStore store =
               GalateaDelegationSqliteStore.CreateNew(
                   directory.Path, owner, Baseline(), limits)) { }
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
                    owner,
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
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreLimits limits = Limits();
        using var store = GalateaDelegationSqliteStore.CreateNew(
            directory.Path,
            owner,
            Baseline(),
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
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreLimits limits = Limits();
        using (var store = GalateaDelegationSqliteStore.CreateNew(
                   directory.Path, owner, Baseline(), limits, hooks)) {
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
            directory.Path, owner, limits);
        Assert.Equal([0, 1], reopened.ReadSnapshot().Mails
            .Select(static mail => mail.ArtifactOrdinal));
    }

    [Fact]
    public void EmptyCaptureTombstones_FailClosedAtExactLifetimeCapacity() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreLimits limits = Limits();
        using (GalateaDelegationSqliteStore store =
               GalateaDelegationSqliteStore.CreateNew(
                   directory.Path, owner, Baseline(), limits)) { }
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
            directory.Path, owner, limits);
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
            Owner(),
            Baseline(),
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
            "bind", snapshot.Route.Revision, snapshot.Mails[0].DispatchId, snapshot.Mails[0].Revision);
        GalateaRouteBindingSnapshot bound = fixture.Store.CompleteThreadBinding(
            "bind", "thread", binding.Revision);
        GalateaTaskCommitment expectedTask = GalateaDelegationTestInputs.Commitment(fixture.Store, snapshot.Mails[0].DispatchId);
        Assert.Throws<GalateaDelegationCommitOutcomeException>(() => fixture.Store.StartQueuedMail(
            snapshot.Mails[0].DispatchId, snapshot.Mails[0].Revision, bound.Revision, expectedTask));
        // An uncertain Start never returns permission to dispatch. Reopen the
        // actual durable state before independently proving the later outcome.
        fixture.Reopen(hooks);
        GalateaOutboundMailSnapshot started = fixture.Store.ReadSnapshot().Mails[0];
        Assert.Equal(GalateaDurableMailState.Started, started.State);
        Assert.Equal(expectedTask, GalateaTaskCommitment.FromStored(started));


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
        bool injectRunningConfirmationUncertain = true;
        using var fixture = new RoutedStore(
            maximumInboxReplies: 4,
            hooks: new GalateaDelegationStoreTestHooks(
                AfterCommitBeforeReturn: operation => {
                    if (injectRunningConfirmationUncertain
                        && operation == "confirm-accepted-mail-running") {
                        injectRunningConfirmationUncertain = false;
                        throw new IOException(
                            "injected after Running confirmation"
                        );
                    }
                }
            )
        );
        GalateaDelegationStateSnapshot initial = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = initial.Mails[0];
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.StartQueuedMail(
                mail.DispatchId, mail.Revision,
                initial.Route.Revision, GalateaDelegationTestInputs.Commitment(fixture.Store, mail.DispatchId)));

        GalateaRouteBindingSnapshot binding = fixture.Store.BeginThreadBinding(
            "bind-op", initial.Route.Revision, mail.DispatchId, mail.Revision);
        GalateaRouteBindingSnapshot bound = fixture.Store.CompleteThreadBinding(
            "bind-op", "thread-1", binding.Revision);
        GalateaOutboundMailSnapshot started = fixture.Store.StartQueuedMail(
            mail.DispatchId, mail.Revision, bound.Revision, GalateaDelegationTestInputs.Commitment(fixture.Store, mail.DispatchId));
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
        Assert.Equal(1, unknown.RecoveryFailureCount);
        Assert.Equal(2000,
            unknown.NextRetryAtUnixTimeMilliseconds);
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.RecordMailPollMiss(
                mail.DispatchId,
                unknown.Revision - 1,
                "NOT_FOUND",
                nowUnixTimeMilliseconds: 1999
            ));
        unknown = fixture.Store.RecordMailPollMiss(
            mail.DispatchId,
            unknown.Revision,
            "NOT_FOUND",
            nowUnixTimeMilliseconds: 2000
        );
        Assert.Equal(GalateaDurableMailState.OutcomeUnknown, unknown.State);
        Assert.Equal(2, unknown.RecoveryFailureCount);
        Assert.Equal("NOT_FOUND", unknown.RecoveryLastCode);
        Assert.Equal(4000,
            unknown.NextRetryAtUnixTimeMilliseconds);
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.StartQueuedMail(
                fixture.Store.ReadSnapshot().Mails[1].DispatchId,
                0,
                fixture.Store.ReadSnapshot().Route.Revision, GalateaDelegationTestInputs.Commitment(fixture.Store, fixture.Store.ReadSnapshot().Mails[1].DispatchId)));

        GalateaOutboundMailSnapshot accepted = fixture.Store.RecordMailAccepted(
            mail.DispatchId,
            unknown.Revision,
            "thread-1",
            "turn-1"
        );
        // An Accepted handle improves identity knowledge but does not prove live progress.
        Assert.Equal(2, accepted.RecoveryFailureCount);
        Assert.Equal("NOT_FOUND", accepted.RecoveryLastCode);
        accepted = fixture.Store.RecordMailPollMiss(
            mail.DispatchId,
            accepted.Revision,
            "POLL_UNAVAILABLE",
            nowUnixTimeMilliseconds: 10_000
        );
        Assert.Equal(GalateaDurableMailState.Accepted, accepted.State);
        Assert.Equal("thread-1", accepted.AcceptedThreadId);
        Assert.Equal("turn-1", accepted.AcceptedTurnId);
        Assert.Equal(3, accepted.RecoveryFailureCount);
        long missedStoreRevision = fixture.Store.ReadSnapshot().StoreRevision;
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.ConfirmAcceptedMailRunning(
                mail.DispatchId,
                accepted.Revision,
                "wrong-thread",
                "turn-1"
            ));
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.ConfirmAcceptedMailRunning(
                mail.DispatchId,
                accepted.Revision,
                "thread-1",
                "wrong-turn"
            ));
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.ConfirmAcceptedMailRunning(
                mail.DispatchId,
                accepted.Revision - 1,
                "thread-1",
                "turn-1"
            ));
        Assert.Equal(
            missedStoreRevision,
            fixture.Store.ReadSnapshot().StoreRevision
        );
        Assert.Equal(
            accepted,
            fixture.Store.ReadSnapshot().Mails[0]
        );
        accepted = fixture.Store.ConfirmAcceptedMailRunning(
            mail.DispatchId,
            accepted.Revision,
            "thread-1",
            "turn-1"
        );
        Assert.False(injectRunningConfirmationUncertain);
        Assert.Equal(0, accepted.RecoveryFailureCount);
        Assert.Null(accepted.RecoveryLastCode);
        Assert.Null(accepted.NextRetryAtUnixTimeMilliseconds);
        Assert.Equal(
            missedStoreRevision + 1,
            fixture.Store.ReadSnapshot().StoreRevision
        );
        fixture.Reopen();
        accepted = fixture.Store.ReadSnapshot().Mails[0];
        Assert.Equal(GalateaDurableMailState.Accepted, accepted.State);
        Assert.Equal("thread-1", accepted.AcceptedThreadId);
        Assert.Equal("turn-1", accepted.AcceptedTurnId);
        Assert.Equal(0, accepted.RecoveryFailureCount);
        Assert.Null(accepted.RecoveryLastCode);
        Assert.Null(accepted.NextRetryAtUnixTimeMilliseconds);
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
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.RecordFailedMail(
            mail.DispatchId, accepted.Revision, "thread-1", "turn-1",
            "terminal", "CONFLICTING_STATUS", "conflicting failure"));
        Assert.Equal(GalateaDelegationRouteState.Quarantined,
            fixture.Store.ReadSnapshot().Route.State);
    }

    [Fact]
    public void BindingIdentityConflict_CanDurablyQuarantineBeforeAnyMailStart() {
        using var fixture = new RoutedStore();
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = snapshot.Mails[0];
        GalateaRouteBindingSnapshot route = fixture.Store.BeginThreadBinding(
            "bind", snapshot.Route.Revision, mail.DispatchId, mail.Revision);
        mail = fixture.Store.RecordThreadBindingEnsureMiss(
            "bind", route.Revision, mail.DispatchId, mail.Revision,
            "ENSURE_OUTCOME_UNKNOWN", 1_000);
        route = fixture.Store.ReadSnapshot().Route;
        GalateaRouteBindingSnapshot quarantined = fixture.Store.QuarantineThreadBinding(
            "bind", route.Revision, "OWNERSHIP_CONFLICT");
        Assert.Equal(GalateaDelegationRouteState.Quarantined, quarantined.State);
        Assert.Null(quarantined.ThreadId);
        Assert.Equal(1, fixture.Store.ReadSnapshot().Mails[0].RecoveryFailureCount);
        Assert.All(fixture.Store.ReadSnapshot().Mails,
            static value => Assert.Equal(GalateaDurableMailState.Queued, value.State));
    }

    [Fact]
    public void BindingFailureBudget_IsMailOwned_DurableAndCheckedByRevisionInsteadOfWallClock() {
        using var fixture = new RoutedStore();
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = snapshot.Mails[0];
        GalateaRouteBindingSnapshot route = fixture.Store.BeginThreadBinding(
            "bind-op", snapshot.Route.Revision, mail.DispatchId, mail.Revision);
        mail = fixture.Store.RecordThreadBindingEnsureMiss("bind-op", route.Revision,
            mail.DispatchId, mail.Revision, "SIDECAR_UNAVAILABLE", 1_000);
        Assert.Equal(1, mail.RecoveryFailureCount);
        Assert.Equal(2_000, mail.NextRetryAtUnixTimeMilliseconds);
        fixture.Reopen();
        route = fixture.Store.ReadSnapshot().Route;
        mail = fixture.Store.ReadSnapshot().Mails[0];
        Assert.Equal("bind-op", route.BindingOperationId);
        Assert.Equal("SIDECAR_UNAVAILABLE", mail.RecoveryLastCode);
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.RecordThreadBindingEnsureMiss("bind-op", route.Revision,
                mail.DispatchId, mail.Revision - 1, "SIDECAR_UNAVAILABLE", 999));
        // The driver owns monotonic due scheduling. A wall-clock rollback must not reject its CAS.
        mail = fixture.Store.RecordThreadBindingEnsureMiss("bind-op", route.Revision,
            mail.DispatchId, mail.Revision, "SIDECAR_UNAVAILABLE", 999);
        Assert.Equal(2, mail.RecoveryFailureCount);
        Assert.Equal(2_999, mail.NextRetryAtUnixTimeMilliseconds);
        route = fixture.Store.ReadSnapshot().Route;
        route = fixture.Store.CompleteThreadBinding("bind-op", "thread-fixed", route.Revision);
        Assert.Equal(GalateaDelegationRouteState.Bound, route.State);
        Assert.Equal("thread-fixed", route.ThreadId);
        Assert.Equal(2, fixture.Store.ReadSnapshot().Mails[0].RecoveryFailureCount);
    }

    [Fact]
    public void UnknownRecovery_EighthFailureAtomicallySettlesNoticeAndReleasesReservation() {
        using var fixture = new RoutedStore(maximumInboxReplies: 1);
        GalateaOutboundMailSnapshot mail = StartEarliest(fixture.Store, "thread-a");
        mail = fixture.Store.MarkMailOutcomeUnknown(mail.DispatchId, mail.Revision, "START_OUTCOME_UNKNOWN", 1_000);
        for (int failure = 2; failure <= 8; failure++) {
            mail = fixture.Store.RecordMailPollMiss(mail.DispatchId, mail.Revision, "INSPECTION_UNAVAILABLE", failure * 100_000L);
            Assert.Equal(failure, mail.RecoveryFailureCount);
            if (failure < 8) {
                Assert.Empty(fixture.Store.ReadSnapshot().Notices);
                Assert.Equal(mail.DispatchId, fixture.Store.ReadSnapshot().Route.ActiveDispatchId);
            }
        }
        fixture.Reopen();
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(GalateaDurableMailState.TerminalFailed, snapshot.Mails[0].State);
        Assert.Equal("RESULT_UNCONFIRMED", snapshot.Mails[0].TerminalCode);
        Assert.Equal(8, snapshot.Mails[0].RecoveryFailureCount);
        Assert.Null(snapshot.Mails[0].NextRetryAtUnixTimeMilliseconds);
        Assert.Equal(GalateaDelegationRouteState.Unbound, snapshot.Route.State);
        Assert.Null(snapshot.Route.ActiveDispatchId);
        GalateaReplyNoticeSnapshot notice = Assert.Single(snapshot.Notices);
        Assert.Equal(GalateaReplyNoticeKind.DeliveryFailure, notice.Kind);
        Assert.Equal("RESULT_UNCONFIRMED", notice.Code);
        Assert.Equal(1, notice.CompletionSequence);
        Assert.Equal(2, Assert.Single(snapshot.Captures).ArtifactCount);
        Assert.Equal(2, snapshot.Mails.Count);
    }

    [Fact]
    public void NotDispatched_RequeuesSameDispatch_PreservesBudgetAndRejectsUnknownEvidence() {
        using var fixture = new RoutedStore();
        GalateaOutboundMailSnapshot started = StartEarliest(fixture.Store, "thread-a");
        GalateaRouteBindingSnapshot route = fixture.Store.ReadSnapshot().Route;
        Assert.Throws<ArgumentException>(() => fixture.Store.RequeueNotDispatchedMail(
            started.DispatchId, started.Revision, route.Revision,
            GalateaDelegateDispatchState.MayHaveDispatched, "THREAD_NOT_FOUND", true, 0));
        GalateaOutboundMailSnapshot queued = fixture.Store.RequeueNotDispatchedMail(
            started.DispatchId, started.Revision, route.Revision,
            GalateaDelegateDispatchState.NotDispatched, "THREAD_NOT_FOUND", true, 0);
        Assert.Equal(started.DispatchId, queued.DispatchId);
        Assert.Equal(started.Body, queued.Body);
        Assert.Equal(GalateaDurableMailState.Queued, queued.State);
        Assert.Equal(1, queued.RecoveryFailureCount);
        Assert.Null(queued.OperationId);
        Assert.Null(queued.RequestedThreadId);
        fixture.Reopen();
        Assert.Equal(GalateaDelegationRouteState.Unbound, fixture.Store.ReadSnapshot().Route.State);
        Assert.Empty(fixture.Store.ReadSnapshot().Notices);
        GalateaOutboundMailSnapshot retried = StartEarliest(fixture.Store, "thread-b");
        Assert.Equal(started.DispatchId, retried.DispatchId);
        Assert.Equal(started.DispatchId, retried.OperationId);
        Assert.Equal(1, retried.RecoveryFailureCount);
        Assert.Equal("thread-b", retried.RequestedThreadId);
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.RequeueNotDispatchedMail(
            started.DispatchId, started.Revision, route.Revision,
            GalateaDelegateDispatchState.NotDispatched, "THREAD_NOT_FOUND", true, 0));
    }

    [Fact]
    public void LocalTerminal_LateSuccessReturnsFirstNoticeWithoutQuarantiningNextTask() {
        using var fixture = new RoutedStore();
        GalateaOutboundMailSnapshot first = StartEarliest(fixture.Store, "thread-a");
        GalateaReplyNoticeSnapshot local = fixture.Store.FinishMailLocally(first.DispatchId,
            first.Revision, fixture.Store.ReadSnapshot().Route.Revision, "RESULT_UNCONFIRMED", true);
        GalateaOutboundMailSnapshot second = StartEarliest(fixture.Store, "thread-b");
        GalateaDelegationStateSnapshot beforeLate = fixture.Store.ReadSnapshot();
        GalateaReplyNoticeSnapshot late = fixture.Store.RecordCompletedMail(first.DispatchId,
            first.Revision, "thread-a", "turn-a", "late success");
        Assert.Equal(local, late);
        Assert.Equal(beforeLate.Route, fixture.Store.ReadSnapshot().Route);
        Assert.Equal(beforeLate.StoreRevision, fixture.Store.ReadSnapshot().StoreRevision);
        Assert.Equal(second.DispatchId, fixture.Store.ReadSnapshot().Route.ActiveDispatchId);
        Assert.Equal(GalateaDelegationRouteState.Bound, fixture.Store.ReadSnapshot().Route.State);
        fixture.Store.RecordCompletedMail(second.DispatchId, second.Revision, "thread-b", "turn-b", "second success");
        fixture.Reopen();
        Assert.Equal(2, fixture.Store.ReadSnapshot().Notices.Count);
        Assert.Equal("RESULT_UNCONFIRMED", fixture.Store.ReadSnapshot().Notices[0].Code);
        Assert.Equal("second success", fixture.Store.ReadSnapshot().Notices[1].Body);
    }

    [Fact]
    public void RemoteTerminal_LocalTimeoutReturnsFirstSuccessEvenAfterRouteAdvanced() {
        using var fixture = new RoutedStore();
        GalateaOutboundMailSnapshot first = StartEarliest(fixture.Store, "thread-a");
        long oldRouteRevision = fixture.Store.ReadSnapshot().Route.Revision;
        GalateaReplyNoticeSnapshot completed = fixture.Store.RecordCompletedMail(first.DispatchId,
            first.Revision, "thread-a", "turn-a", "actual success");
        GalateaOutboundMailSnapshot second = StartEarliest(fixture.Store, "thread-a");
        GalateaDelegationStateSnapshot before = fixture.Store.ReadSnapshot();
        Assert.Equal(completed, fixture.Store.FinishMailLocally(first.DispatchId, first.Revision,
            oldRouteRevision, "RESULT_UNCONFIRMED", true));
        Assert.Equal(before.StoreRevision, fixture.Store.ReadSnapshot().StoreRevision);
        Assert.Equal(second.DispatchId, fixture.Store.ReadSnapshot().Route.ActiveDispatchId);
        Assert.Single(fixture.Store.ReadSnapshot().Notices);
    }

    [Fact]
    public void QueuedLocalFailure_FullInboxPersistsExhaustedMailUntilCapacityIsConsumed() {
        using var fixture = new RoutedStore(maximumInboxReplies: 1);
        GalateaOutboundMailSnapshot first = StartEarliest(fixture.Store, "thread-a");
        GalateaReplyNoticeSnapshot reply = fixture.Store.RecordCompletedMail(first.DispatchId,
            first.Revision, "thread-a", "turn-a", "reply");
        GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot second = snapshot.Mails[1];
        Assert.Throws<GalateaDelegationInboxBackpressureException>(() => fixture.Store.FinishMailLocally(
            second.DispatchId, second.Revision, snapshot.Route.Revision, "INVALID_CWD", true));
        fixture.Reopen();
        snapshot = fixture.Store.ReadSnapshot();
        second = snapshot.Mails[1];
        Assert.Equal(GalateaDurableMailState.Queued, second.State);
        Assert.Equal(8, second.RecoveryFailureCount);
        Assert.Equal("INVALID_CWD", second.RecoveryLastCode);
        Assert.Equal("second", second.Body);
        Assert.Single(snapshot.Notices);
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.StartQueuedMail(
            second.DispatchId, second.Revision, snapshot.Route.Revision, GalateaDelegationTestInputs.Commitment(fixture.Store, second.DispatchId)));
        ConsumeReadyReply(fixture.Store, reply);
        GalateaReplyNoticeSnapshot failed = fixture.Store.FinishMailLocally(second.DispatchId,
            second.Revision, fixture.Store.ReadSnapshot().Route.Revision, "INVALID_CWD", true);
        Assert.Equal("INVALID_CWD", failed.Code);
        Assert.Equal(2, failed.CompletionSequence);
        fixture.Reopen();
        Assert.Equal(2, fixture.Store.ReadSnapshot().Notices.Count);
        Assert.Equal(GalateaReplyNoticeState.Consumed, fixture.Store.ReadSnapshot().Notices[0].State);
        Assert.Equal(GalateaDurableMailState.TerminalFailed, fixture.Store.ReadSnapshot().Mails[1].State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverySettlement_CommitFailureReopensAsWholeStateWithOneNotice(bool afterCommit) {
        bool armed = false;
        bool injected = false;
        void Fail(string operation) {
            if (armed && !injected && operation == "record-mail-poll-miss") {
                injected = true;
                throw new IOException("injected recovery settlement commit failure");
            }
        }
        var hooks = afterCommit
            ? new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: Fail)
            : new GalateaDelegationStoreTestHooks(BeforeCommit: Fail);
        using var fixture = new RoutedStore(hooks: hooks);
        GalateaOutboundMailSnapshot mail = StartEarliest(fixture.Store, "thread-a");
        mail = fixture.Store.MarkMailOutcomeUnknown(mail.DispatchId, mail.Revision, "START_OUTCOME_UNKNOWN", 0);
        for (int failure = 2; failure <= 7; failure++) {
            mail = fixture.Store.RecordMailPollMiss(mail.DispatchId, mail.Revision, "INSPECTION_UNAVAILABLE", failure * 100_000L);
        }
        armed = true;
        if (afterCommit) {
            mail = fixture.Store.RecordMailPollMiss(mail.DispatchId, mail.Revision, "INSPECTION_UNAVAILABLE", 800_000);
            Assert.Equal(GalateaDurableMailState.TerminalFailed, mail.State);
        }
        else {
            Assert.Throws<IOException>(() => fixture.Store.RecordMailPollMiss(
                mail.DispatchId, mail.Revision, "INSPECTION_UNAVAILABLE", 800_000));
        }
        Assert.True(injected);
        fixture.Reopen();
        mail = fixture.Store.ReadSnapshot().Mails[0];
        if (!afterCommit) {
            Assert.Equal(7, mail.RecoveryFailureCount);
            Assert.Empty(fixture.Store.ReadSnapshot().Notices);
            Assert.Equal(mail.DispatchId, fixture.Store.ReadSnapshot().Route.ActiveDispatchId);
            fixture.Store.RecordMailPollMiss(mail.DispatchId, mail.Revision, "INSPECTION_UNAVAILABLE", 800_000);
            fixture.Reopen();
        }
        GalateaDelegationStateSnapshot terminal = fixture.Store.ReadSnapshot();
        Assert.Equal(8, terminal.Mails[0].RecoveryFailureCount);
        Assert.Equal(GalateaDurableMailState.TerminalFailed, terminal.Mails[0].State);
        Assert.Null(terminal.Route.ActiveDispatchId);
        Assert.Equal(GalateaDelegationRouteState.Unbound, terminal.Route.State);
        Assert.Equal(1, Assert.Single(terminal.Notices).CompletionSequence);
    }

    private static GalateaOutboundMailSnapshot StartEarliest(GalateaDelegationSqliteStore store, string threadId) {
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = snapshot.Mails.First(value => value.State == GalateaDurableMailState.Queued);
        if (snapshot.Route.State == GalateaDelegationRouteState.Unbound) {
            GalateaRouteBindingSnapshot binding = store.BeginThreadBinding("bind-" + threadId,
                snapshot.Route.Revision, mail.DispatchId, mail.Revision);
            store.CompleteThreadBinding(binding.BindingOperationId!, threadId, binding.Revision);
            snapshot = store.ReadSnapshot();
        }
        return store.StartQueuedMail(mail.DispatchId, mail.Revision, snapshot.Route.Revision, GalateaDelegationTestInputs.Commitment(store, mail.DispatchId));
    }

    private static void ConsumeReadyReply(GalateaDelegationSqliteStore store, GalateaReplyNoticeSnapshot reply) {
        GalateaReplyLeaseSnapshot lease = store.BeginReplyLeaseMembership("consume-ready", "player",
            [new(reply.NoticeId, reply.Revision)]);
        lease = store.BindReplyLeaseObservationBase(lease.LeaseId, lease.Revision, Address(20), Observation(store, "player", reply.Body));
        lease = store.RecordLeaseObservationCommitted(lease.LeaseId, lease.Revision, Address(21));
        store.ConsumeReplyLease(lease.LeaseId, lease.Revision, Address(22));
    }

    [Fact]
    public void LegacyQueuedPreflightFailure_IsFifoTerminalAndCapacityBounded() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreLimits limits = Limits(
            maximumTaskUtf8Bytes: 3,
            maximumInboxReplies: 1,
            maximumInboxUtf8Bytes: 4 * 1024
        );
        bool injectUncertain = true;
        using var store = GalateaDelegationSqliteStore.CreateNew(
            directory.Path,
            Owner(),
            Baseline(),
            limits,
            new GalateaDelegationStoreTestHooks(
                AfterCommitBeforeReturn: operation => {
                    if (injectUncertain
                        && operation == "fail-queued-mail-preflight") {
                        injectUncertain = false;
                        throw new IOException("injected after preflight");
                    }
                }
            )
        );
        _ = store.CaptureActionBatch(Capture(
            Address(91),
            [Mail("Codex", "first"), Mail("Codex", "second")]
        ));
        GalateaDelegationTestInputs.ImportQueuedLegacyTasks(store);
        GalateaDelegationStateSnapshot initial = store.ReadSnapshot();
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            store.BeginThreadBinding(
                "bind-before-preflight",
                initial.Route.Revision, initial.Mails[0].DispatchId, initial.Mails[0].Revision
            ));
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            store.FailQueuedMailPreflight(
                initial.Mails[1].DispatchId,
                initial.Mails[1].Revision
            ));

        GalateaReplyNoticeSnapshot notice = store.FailQueuedMailPreflight(
            initial.Mails[0].DispatchId,
            initial.Mails[0].Revision
        );
        Assert.False(injectUncertain);
        Assert.Equal(GalateaReplyNoticeKind.DeliveryFailure, notice.Kind);
        Assert.Equal("preflight", notice.Stage);
        Assert.Equal("TASK_INVALID_OR_TOO_LARGE", notice.Code);
        GalateaDelegationStateSnapshot failed = store.ReadSnapshot();
        GalateaOutboundMailSnapshot failedMail = failed.Mails[0];
        Assert.Equal(GalateaDurableMailState.TerminalFailed, failedMail.State);
        Assert.Null(failedMail.OperationId);
        Assert.Null(failedMail.RequestedThreadId);
        Assert.Null(failedMail.AcceptedThreadId);
        Assert.Null(failedMail.AcceptedTurnId);
        Assert.Null(failedMail.Body);
        Assert.Equal(GalateaDelegationRouteState.Unbound, failed.Route.State);

        Assert.Throws<GalateaDelegationInboxBackpressureException>(() =>
            store.FailQueuedMailPreflight(
                failed.Mails[1].DispatchId,
                failed.Mails[1].Revision
            ));
    }

    [Fact]
    public void LegacyQueuedPreflightFailure_RejectsBodyWithinDurableTaskLimit() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreLimits limits = Limits(
            maximumTaskUtf8Bytes: 16
        );
        using var store = GalateaDelegationSqliteStore.CreateNew(
            directory.Path,
            Owner(),
            Baseline(),
            limits
        );
        _ = store.CaptureActionBatch(Capture(
            Address(92),
            [Mail("Codex", "short")]
        ));
        GalateaDelegationTestInputs.ImportQueuedLegacyTasks(store);
        GalateaOutboundMailSnapshot mail = store.ReadSnapshot().Mails.Single();

        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            store.FailQueuedMailPreflight(mail.DispatchId, mail.Revision));
        Assert.Empty(store.ReadSnapshot().Notices);
    }

    [Fact]
    public void LegacyQueuedPreflightFailure_CannotPassAnActiveFifoHead() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreLimits limits = Limits(
            maximumTaskUtf8Bytes: 3
        );
        using var store = GalateaDelegationSqliteStore.CreateNew(
            directory.Path,
            Owner(),
            Baseline(),
            limits
        );
        _ = store.CaptureActionBatch(Capture(
            Address(93),
            [Mail("Codex", "ok"), Mail("Codex", "oversized")]
        ));
        GalateaDelegationTestInputs.ImportQueuedLegacyTasks(store);
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        GalateaRouteBindingSnapshot binding = store.BeginThreadBinding(
            "bind",
            snapshot.Route.Revision, snapshot.Mails[0].DispatchId, snapshot.Mails[0].Revision
        );
        GalateaRouteBindingSnapshot bound = store.CompleteThreadBinding(
            "bind",
            "thread",
            binding.Revision
        );
        _ = store.StartQueuedMail(
            snapshot.Mails[0].DispatchId,
            snapshot.Mails[0].Revision,
            bound.Revision
        , GalateaDelegationTestInputs.Commitment(store, snapshot.Mails[0].DispatchId));
        snapshot = store.ReadSnapshot();

        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            store.FailQueuedMailPreflight(
                snapshot.Mails[1].DispatchId,
                snapshot.Mails[1].Revision
            ));
        Assert.Empty(store.ReadSnapshot().Notices);
        Assert.Equal(
            GalateaDurableMailState.Queued,
            store.ReadSnapshot().Mails[1].State
        );
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
            "bind", snapshot.Route.Revision, snapshot.Mails[0].DispatchId, snapshot.Mails[0].Revision);
        GalateaRouteBindingSnapshot bound = fixture.Store.CompleteThreadBinding(
            "bind", "thread", binding.Revision);
        GalateaOutboundMailSnapshot first = fixture.Store.StartQueuedMail(
            snapshot.Mails[0].DispatchId,
            snapshot.Mails[0].Revision,
            bound.Revision
        , GalateaDelegationTestInputs.Commitment(fixture.Store, snapshot.Mails[0].DispatchId));
        _ = fixture.Store.RecordCompletedMail(
            first.DispatchId,
            first.Revision,
            "thread",
            "turn-1",
            "reply"
        );
        snapshot = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot second = snapshot.Mails[1];
        GalateaDelegationInboxBackpressureException backpressure =
            Assert.Throws<GalateaDelegationInboxBackpressureException>(() =>
            fixture.Store.StartQueuedMail(
                second.DispatchId,
                second.Revision,
                snapshot.Route.Revision
            , GalateaDelegationTestInputs.Commitment(fixture.Store, second.DispatchId)));
        Assert.Equal(1, backpressure.CurrentCount);
        Assert.Equal(1, backpressure.ReservedCount);

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
            Observation(fixture.Store, "player", "reply")
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
            , GalateaDelegationTestInputs.Commitment(fixture.Store, second.DispatchId));
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
                Observation(fixture.Store, "changed player", "reply")
            ));
        first = fixture.Store.BindReplyLeaseObservationBase(
            first.LeaseId,
            first.Revision,
            Address(30),
            Observation(fixture.Store, "player", "reply")
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
            Observation(fixture.Store, "player", "reply")
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
    public void Lease_ExactAbandonRollbackIsDurableAndRearmsNotice() {
        bool injectUncertain = true;
        using var fixture = RoutedStore.WithOneReadyNotice(
            new GalateaDelegationStoreTestHooks(
                AfterCommitBeforeReturn: operation => {
                    if (injectUncertain
                        && operation
                            == "rollback-reply-lease-after-exact-abandon") {
                        injectUncertain = false;
                        throw new IOException("injected after exact abandon");
                    }
                }
            )
        );
        GalateaReplyLeaseSnapshot lease = BeginBoundCommittedLease(
            fixture.Store,
            "lease-abandoned",
            Address(70),
            Address(71)
        );

        fixture.Store.RollbackReplyLeaseAfterExactAbandon(
            lease.LeaseId,
            lease.Revision,
            Address(70),
            Address(71)
        );

        Assert.False(injectUncertain);
        fixture.Reopen();
        GalateaDelegationStateSnapshot reopened =
            fixture.Store.ReadSnapshot();
        Assert.Null(reopened.ActiveLease);
        GalateaReplyNoticeSnapshot notice = reopened.Notices.Single();
        Assert.Equal(GalateaReplyNoticeState.Ready, notice.State);
        Assert.Null(notice.ConsumedActionAddress);
        Assert.Equal(0, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_lease;"
        ));
        Assert.Equal(0, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_lease_item;"
        ));
    }

    [Fact]
    public void Lease_ExactAbandonRollbackRejectsWrongStateOrEvidence() {
        using var fixture = RoutedStore.WithOneReadyNotice();
        GalateaReplyNoticeSnapshot notice =
            fixture.Store.ReadSnapshot().Notices.Single();
        GalateaReplyLeaseSnapshot lease =
            fixture.Store.BeginReplyLeaseMembership(
                "lease-exact",
                "player",
                [new(notice.NoticeId, notice.Revision)]
            );
        AssertExactAbandonConflictWithoutMutation(
            fixture.Store,
            lease.LeaseId,
            lease.Revision,
            Address(80),
            Address(81)
        );

        lease = fixture.Store.BindReplyLeaseObservationBase(
            lease.LeaseId,
            lease.Revision,
            Address(80),
            Observation(fixture.Store, "player", "reply")
        );
        AssertExactAbandonConflictWithoutMutation(
            fixture.Store,
            lease.LeaseId,
            lease.Revision,
            Address(80),
            Address(81)
        );

        lease = fixture.Store.RecordLeaseObservationCommitted(
            lease.LeaseId,
            lease.Revision,
            Address(81)
        );
        GalateaDelegationStateSnapshot committed =
            fixture.Store.ReadSnapshot();
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.RollbackReplyLease(
                lease.LeaseId,
                lease.Revision
            ));
        AssertDelegationLeaseStateEqual(
            committed,
            fixture.Store.ReadSnapshot()
        );
        AssertExactAbandonConflictWithoutMutation(
            fixture.Store,
            "wrong-lease",
            lease.Revision,
            Address(80),
            Address(81)
        );
        AssertExactAbandonConflictWithoutMutation(
            fixture.Store,
            lease.LeaseId,
            checked(lease.Revision + 1),
            Address(80),
            Address(81)
        );
        AssertExactAbandonConflictWithoutMutation(
            fixture.Store,
            lease.LeaseId,
            lease.Revision,
            Address(82),
            Address(81)
        );
        AssertExactAbandonConflictWithoutMutation(
            fixture.Store,
            lease.LeaseId,
            lease.Revision,
            Address(80),
            Address(83)
        );

        fixture.Store.QuarantineReplyLease(
            lease.LeaseId,
            lease.Revision
        );
        lease = Assert.IsType<GalateaReplyLeaseSnapshot>(
            fixture.Store.ReadSnapshot().ActiveLease
        );
        AssertExactAbandonConflictWithoutMutation(
            fixture.Store,
            lease.LeaseId,
            lease.Revision,
            Address(80),
            Address(81)
        );
    }

    [Fact]
    public void Lease_ExactAbandonRollbackRejectsCorruptPersistedObservation() {
        using var fixture = RoutedStore.WithOneReadyNotice();
        GalateaReplyLeaseSnapshot lease = BeginBoundCommittedLease(
            fixture.Store,
            "lease-corrupt",
            Address(90),
            Address(91)
        );
        ExecuteSql(fixture.DatabasePath, """
            UPDATE reply_lease
            SET rendered_observation = 'corrupt';
            """);

        Assert.Throws<InvalidDataException>(() =>
            fixture.Store.RollbackReplyLeaseAfterExactAbandon(
                lease.LeaseId,
                lease.Revision,
                Address(90),
                Address(91)
            ));

        Assert.Equal(1, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_lease;"
        ));
        Assert.Equal(1, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_lease_item;"
        ));
        Assert.Equal(1, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_notice WHERE state = 'Leased';"
        ));
        Assert.Equal(0, ExecuteScalarLong(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM reply_notice WHERE state = 'Ready';"
        ));
    }

    [Fact]
    public void StrictOpen_RejectsUnknownSchemaAndCorruptCurrentState() {
        using var directory = new StoreDirectory();
        GalateaDelegationStoreOwner owner = Owner();
        GalateaDelegationStoreLimits limits = Limits();
        using (var store = GalateaDelegationSqliteStore.CreateNew(
                   directory.Path, owner, Baseline(), limits)) {
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
                directory.Path, owner, limits));
        ExecuteSql(database, "DROP TABLE unexpected;");
        ExecuteSql(database, """
            DROP INDEX ux_outbound_source_ordinal;
            CREATE UNIQUE INDEX ux_outbound_source_ordinal
            ON outbound_mail(dispatch_id);
            """);
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path, owner, limits));
        ExecuteSql(database, """
            DROP INDEX ux_outbound_source_ordinal;
            CREATE UNIQUE INDEX ux_outbound_source_ordinal
            ON outbound_mail(source_action_address, artifact_ordinal);
            """);
        ExecuteSql(database, "UPDATE outbound_mail SET body = NULL;");
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path, owner, limits));
        ExecuteSql(database, "UPDATE outbound_mail SET body = 'body';");
        ExecuteSql(database, """
            UPDATE outbound_mail
            SET state = 'Started', operation_id = NULL,
                requested_thread_id = NULL;
            """);
        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.OpenExisting(
                directory.Path, owner, limits));
    }

    [Fact]
    public void InternalMailOutbox_CaptureFreezesTargetAndRowCasLifecycle() {
        using var directory = new StoreDirectory();
        using GalateaDelegationSqliteStore store =
            GalateaDelegationSqliteStore.CreateNew(
                directory.Path, Owner(), Baseline(), Limits());
        GalateaDelegationCaptureResult capture = store.CaptureActionBatch(new(
            Address(760), Sha('a'), 12, "extractor-contract-v1", [
                Mail("peer", "message")
            ], GalateaDelegationTestInputs.Sender(store, "sender-name"), [
                new GalateaInternalMailTarget(
                    "peer-user", "peer-repository", "sender-name")
            ]
        ));

        GalateaDelegationStateSnapshot captured = store.ReadSnapshot();
        GalateaOutboundMailSnapshot artifact = Assert.Single(captured.Mails);
        Assert.False(artifact.IsCodexRouted);
        Assert.Equal(GalateaDurableMailState.Unrouted, artifact.State);
        GalateaInternalMailOutboxSnapshot pending = Assert.Single(
            store.ReadInternalMailOutboxesForTarget("peer-user"));
        Assert.Equal(capture.DispatchIds[0], pending.DispatchId);
        Assert.Equal(GalateaInternalMailState.Pending, pending.State);
        Assert.Matches("^[0-9a-f]{32}$", pending.MessageId);

        GalateaInternalMailOutboxSnapshot bound =
            store.BindInternalMailObservation(
                pending.DispatchId, pending.Revision, Address(2),
                GalateaDelegationTestInputs.InternalMailInput(store, pending));
        Assert.Equal(GalateaInternalMailState.ObservationBound, bound.State);
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            store.CompleteInternalMailObservation(
                bound.DispatchId, pending.Revision, Address(3)));
        GalateaInternalMailOutboxSnapshot delivered =
            store.CompleteInternalMailObservation(
                bound.DispatchId, bound.Revision, Address(3));
        Assert.Equal(GalateaInternalMailState.Delivered, delivered.State);
        Assert.Equal(Address(3), delivered.ObservationAddress);
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            store.ResetInternalMailObservation(
                delivered.DispatchId, delivered.Revision));
    }

    [Fact]
    public void InternalMailOutbox_DuplicateCaptureDoesNotBackfillOrReplaceTarget() {
        using var directory = new StoreDirectory();
        using GalateaDelegationSqliteStore store =
            GalateaDelegationSqliteStore.CreateNew(
                directory.Path, Owner(), Baseline(), Limits());
        GalateaDelegationCaptureRequest withoutTarget = Capture(
            Address(761), [Mail("peer", "message")]);
        _ = store.CaptureActionBatch(withoutTarget);
        GalateaDelegationCaptureResult existing = store.CaptureActionBatch(
            withoutTarget with {
                InternalTargets = [new GalateaInternalMailTarget(
                    "peer-user", "peer-repository", "sender-name")]
            });
        Assert.Equal(GalateaDelegationCaptureDisposition.AlreadyCaptured,
            existing.Disposition);
        Assert.Empty(store.ReadSnapshot().InternalMailOutboxes);
    }

    [Fact]
    public void InternalMailOutbox_TargetReadFailsClosedOnMalformedLogicalShape() {
        using var directory = new StoreDirectory();
        using GalateaDelegationSqliteStore store =
            GalateaDelegationSqliteStore.CreateNew(
                directory.Path, Owner(), Baseline(), Limits());
        _ = store.CaptureActionBatch(new(
            Address(762), Sha('a'), 12, "extractor-contract-v1", [
                Mail("peer", "message")
            ], GalateaDelegationTestInputs.Sender(store, "sender-name"), [
                new GalateaInternalMailTarget(
                    "peer-user", "peer-repository", "sender-name")
            ]
        ));
        string databasePath = System.IO.Path.Combine(directory.Path,
            GalateaDelegationSqliteStore.DatabaseFileName);
        ExecuteSql(databasePath, """
            UPDATE internal_mail_outbox
            SET expected_session_head = 'ej1:00000000000000030000000100000000'
            WHERE target_user_id = 'peer-user';
            """);

        Assert.Throws<InvalidDataException>(() =>
            store.ReadInternalMailOutboxesForTarget("peer-user"));
    }

    private static GalateaDelegationStoreOwner Owner() =>
        new("user", "repository-id");

    private static GalateaDelegationStoreBaseline Baseline() {
        string selectedHead = Address(2);
        EventAddress address = EventAddressTextCodec.Parse(selectedHead);
        return new(
            new EventJournalPhysicalAppendFrontier(
                address.SegmentNumber,
                address.Ticket.EndOffsetExclusive
            ),
            selectedHead
        );
    }

    private static GalateaDelegationStoreLimits Limits(
        int maximumTaskUtf8Bytes = 100_000,
        int maximumInboxReplies = 16,
        int maximumInboxUtf8Bytes = 16 * 1024,
        int maximumReplyUtf8Bytes = 1024
    ) => new(
        MaximumQueuedMails: 32,
        maximumTaskUtf8Bytes,
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
        intents,
        new GalateaSenderSnapshot("character", Owner().CharacterId, "sender-name")
    );

    private static SendMailIntent Mail(string recipient, string body) => new(
        recipient,
        Subject: null,
        body,
        InReplyToMessageId: null,
        EvidenceQuote: "sent it"
    );

    private static string Address(
        int value,
        uint segmentNumber = 1
    ) => $"ej1:{value:x16}{segmentNumber:x8}00000000";

    private static string Sha(char value) => new(value, 64);

    private static SessionInputContent Observation(GalateaDelegationSqliteStore store, string playerText, string reply) {
        GalateaReplyLeaseSnapshot lease = store.ReadSnapshot().ActiveLease!;
        PlayerTurnNotice.Reply source = Assert.IsType<PlayerTurnNotice.Reply>(
            Assert.Single(new GalateaDurableReplyLease(store, lease.LeaseId, lease.Revision).ReadNotices()));
        PlayerTurnNotice.Reply selected = source.IsLegacyDurable
            ? PlayerTurnNotice.Reply.FromLegacyDurable(reply)
            : new PlayerTurnNotice.Reply(reply, source.Sender!, source.DispatchId!, source.ThreadId, source.TurnId, source.NoticeId);
        return GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction(playerText, GalateaDelegateTestConfiguration.PlayerSender),
            new DateTimeOffset(2026, 8, 29, 14, 23, 5, TimeSpan.FromHours(8)),
            GalateaDelegationTestInputs.Sender(store, "sender-name"), [selected]);
    }

    private static GalateaReplyLeaseSnapshot BeginBoundCommittedLease(
        GalateaDelegationSqliteStore store,
        string leaseId,
        string baseHead,
        string observationAddress
    ) {
        GalateaReplyNoticeSnapshot notice =
            store.ReadSnapshot().Notices.Single();
        GalateaReplyLeaseSnapshot lease =
            store.BeginReplyLeaseMembership(
                leaseId,
                "player",
                [new(notice.NoticeId, notice.Revision)]
            );
        lease = store.BindReplyLeaseObservationBase(
            lease.LeaseId,
            lease.Revision,
            baseHead,
            Observation(store, "player", "reply")
        );
        return store.RecordLeaseObservationCommitted(
            lease.LeaseId,
            lease.Revision,
            observationAddress
        );
    }

    private static void AssertExactAbandonConflictWithoutMutation(
        GalateaDelegationSqliteStore store,
        string leaseId,
        long expectedRevision,
        string expectedBaseHead,
        string expectedObservationAddress
    ) {
        GalateaDelegationStateSnapshot before = store.ReadSnapshot();

        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            store.RollbackReplyLeaseAfterExactAbandon(
                leaseId,
                expectedRevision,
                expectedBaseHead,
                expectedObservationAddress
            ));

        AssertDelegationLeaseStateEqual(before, store.ReadSnapshot());
    }

    private static void AssertDelegationLeaseStateEqual(
        GalateaDelegationStateSnapshot expected,
        GalateaDelegationStateSnapshot actual
    ) {
        Assert.Equal(expected.StoreRevision, actual.StoreRevision);
        Assert.Equal(expected.ActiveLease?.LeaseId,
            actual.ActiveLease?.LeaseId);
        Assert.Equal(expected.ActiveLease?.State,
            actual.ActiveLease?.State);
        Assert.Equal(expected.ActiveLease?.PlayerText,
            actual.ActiveLease?.PlayerText);
        Assert.Equal(expected.ActiveLease?.ExpectedSessionHead,
            actual.ActiveLease?.ExpectedSessionHead);
        Assert.Equal(expected.ActiveLease?.BoundInput,
            actual.ActiveLease?.BoundInput);
        Assert.Equal(expected.ActiveLease?.RenderedObservation,
            actual.ActiveLease?.RenderedObservation);
        Assert.Equal(expected.ActiveLease?.ObservationUtf8Bytes,
            actual.ActiveLease?.ObservationUtf8Bytes);
        Assert.Equal(expected.ActiveLease?.ObservationSha256,
            actual.ActiveLease?.ObservationSha256);
        Assert.Equal(expected.ActiveLease?.ObservationAddress,
            actual.ActiveLease?.ObservationAddress);
        Assert.Equal(expected.ActiveLease?.Revision,
            actual.ActiveLease?.Revision);
        Assert.Equal(expected.ActiveLease?.NoticeIds,
            actual.ActiveLease?.NoticeIds);
        Assert.Equal(expected.Notices, actual.Notices);
    }

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
        private readonly GalateaDelegationStoreOwner _owner;
        private readonly GalateaDelegationStoreLimits _limits;

        internal RoutedStore(
            int maximumInboxReplies = 4,
            int maximumInboxUtf8Bytes = 16 * 1024,
            int maximumReplyUtf8Bytes = 1024,
            GalateaDelegationStoreTestHooks? hooks = null
        ) {
            _limits = Limits(
                maximumInboxReplies: maximumInboxReplies,
                maximumInboxUtf8Bytes: maximumInboxUtf8Bytes,
                maximumReplyUtf8Bytes: maximumReplyUtf8Bytes
            );
            _owner = Owner();
            Store = GalateaDelegationSqliteStore.CreateNew(
                _directory.Path,
                _owner,
                Baseline(),
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

        internal void Reopen(GalateaDelegationStoreTestHooks? hooks = null) {
            Store.Dispose();
            Store = GalateaDelegationSqliteStore.OpenExisting(
                _directory.Path,
                _owner,
                _limits,
                hooks
            );
        }

        internal static RoutedStore WithOneReadyNotice(
            GalateaDelegationStoreTestHooks? hooks = null
        ) {
            var fixture = new RoutedStore(hooks: hooks);
            GalateaDelegationStateSnapshot snapshot = fixture.Store.ReadSnapshot();
            GalateaRouteBindingSnapshot binding = fixture.Store.BeginThreadBinding(
                "bind", snapshot.Route.Revision, snapshot.Mails[0].DispatchId, snapshot.Mails[0].Revision);
            GalateaRouteBindingSnapshot bound = fixture.Store.CompleteThreadBinding(
                "bind", "thread", binding.Revision);
            GalateaOutboundMailSnapshot started = fixture.Store.StartQueuedMail(
                snapshot.Mails[0].DispatchId,
                snapshot.Mails[0].Revision,
                bound.Revision
            , GalateaDelegationTestInputs.Commitment(fixture.Store, snapshot.Mails[0].DispatchId));
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
