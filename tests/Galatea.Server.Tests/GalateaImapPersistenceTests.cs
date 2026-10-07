using System.Text;
using System.Text.Json;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaImapPersistenceTests {
    private static readonly DateTimeOffset BaselineAt = DateTimeOffset.UnixEpoch.AddDays(1);
    private static string Reference(string character = "alice", char identity = 'a') =>
        $"imap:{character}:{new string(identity, 64)}";
    private static string Address(int value) => $"ej1:{value:x16}0000000100000000";

    [Fact]
    public void Baseline_IsDurableAndAccountNamespacesDoNotResetEachOther() {
        using var fixture = new Fixture();
        Assert.Null(fixture.Store.ReadImapCheckpoint(Reference()));
        GalateaImapCheckpointSnapshot first = fixture.Store.EstablishImapBaseline(Reference(), 41, 100, BaselineAt);
        Assert.Equal(100u, first.ScannedThroughUid);
        Assert.Equal(BaselineAt.ToUnixTimeMilliseconds(), first.BaselineAtUnixTimeMilliseconds);
        fixture.Reopen();
        Assert.Equal(first, fixture.Checkpoint);
        Assert.Equal(first, fixture.Store.EstablishImapBaseline(Reference(), 999, 900, BaselineAt.AddDays(5)));
        GalateaImapCheckpointSnapshot other = fixture.Store.EstablishImapBaseline(Reference(identity: 'b'), 52, 700, BaselineAt.AddDays(1));
        Assert.Equal(first, fixture.Checkpoint);
        Assert.Equal(700u, other.ScannedThroughUid);
        fixture.Reopen();
        Assert.Equal(first, fixture.Checkpoint);
        Assert.Equal(other, fixture.Store.ReadImapCheckpoint(Reference(identity: 'b')));
    }

    [Fact]
    public void UnknownSender_CommitsOnlyCursorAndLaterPermissionDoesNotReplayOldUid() {
        using var fixture = new Fixture();
        GalateaImapCheckpointSnapshot baseline = fixture.Baseline();
        Assert.False(fixture.Store.DecideImapAdmission(baseline, 1, "stranger@example.test", []));
        Assert.Equal(1u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Null(fixture.Store.ReadPendingExternalMail());
        Assert.Empty(fixture.Store.ReadUnsettledExternalMails());
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
        fixture.Reopen();
        GalateaImapCheckpointSnapshot denied = fixture.Checkpoint;
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.DecideImapAdmission(denied, 1, "stranger@example.test", ["stranger@example.test"]));
        Assert.True(fixture.Store.DecideImapAdmission(denied, 2, "stranger@example.test", ["stranger@example.test"]));
        Assert.Equal(denied, fixture.Checkpoint);
        var row = fixture.Accept(2, "stranger@example.test");
        Assert.Equal(2u, row.Uid);
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
    }

    [Theory]
    [InlineData("Alice@EXAMPLE.test", true)]
    [InlineData("alice@example.test", false)]
    [InlineData("Alice+tag@example.test", false)]
    [InlineData("Alice@example.other", false)]
    public void ConfiguredSender_UsesExactLocalPartAndAsciiInsensitiveDomain(string from, bool allowed) {
        using var fixture = new Fixture();
        GalateaImapCheckpointSnapshot baseline = fixture.Baseline();
        Assert.Equal(allowed, fixture.Store.DecideImapAdmission(baseline, 1, from, ["Alice@example.test"]));
        Assert.Equal(allowed ? 0u : 1u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
    }

    [Theory]
    [InlineData((int)GalateaSmtpMailState.Pending)]
    [InlineData((int)GalateaSmtpMailState.Attempting)]
    [InlineData((int)GalateaSmtpMailState.ProviderAccepted)]
    [InlineData((int)GalateaSmtpMailState.DefiniteFailure)]
    [InlineData((int)GalateaSmtpMailState.OutcomeUnknown)]
    public void NormalAcceptedOutboundContact_DoesNotDependOnSmtpState(int stateValue) {
        var state = (GalateaSmtpMailState)stateValue;
        using var fixture = new Fixture();
        fixture.CaptureContact("Alice@EXAMPLE.test", "smtp:alice:previous-binding");
        if (state != GalateaSmtpMailState.Pending) {
            GalateaSmtpMailOutboxSnapshot attempting = fixture.Store.ClaimPendingSmtpMail()!;
            if (state != GalateaSmtpMailState.Attempting) {
                fixture.Store.CompleteSmtpAttempt(attempting.DispatchId, attempting.Revision,
                    new GalateaSmtpSendResult(state, "TEST_RESULT"));
            }
        }
        GalateaImapCheckpointSnapshot baseline = fixture.Baseline();
        Assert.True(fixture.Store.DecideImapAdmission(baseline, 1, "Alice@example.test", []));
        Assert.Equal(baseline, fixture.Checkpoint);
        Assert.False(fixture.Store.DecideImapAdmission(baseline, 1, "alice@example.test", []));
        fixture.Reopen();
        Assert.True(fixture.Store.DecideImapAdmission(fixture.Checkpoint, 2, "Alice@example.test", []));
    }

    [Theory]
    [InlineData("offline:alice")]
    [InlineData("blocked:alice:SMTP_DISABLED")]
    [InlineData("blocked:alice:NO_SENDER_BINDING")]
    [InlineData("blocked:alice:SENDER_BINDING_DISABLED")]
    public void NonSendingCapture_DoesNotCreateContactPermission(string smtpReference) {
        using var fixture = new Fixture();
        fixture.CaptureContact("friend@example.test", smtpReference);
        Assert.False(fixture.Store.DecideImapAdmission(fixture.Baseline(), 1, "friend@example.test", []));
        fixture.Reopen();
        Assert.False(fixture.Store.DecideImapAdmission(fixture.Checkpoint, 2, "friend@example.test", []));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
    }

    [Fact]
    public void ContactsFollowRoleStoreAcrossMailboxChangeAndStayIsolatedFromOtherRoles() {
        using var alice = new Fixture();
        using var bob = new Fixture(character: "bob");
        alice.CaptureContact("friend@example.test", "smtp:alice:old-binding");
        bob.Baseline();
        Assert.False(bob.Store.DecideImapAdmission(bob.Checkpoint, 1, "friend@example.test", []));
        alice.Baseline();
        GalateaImapCheckpointSnapshot changedAccount = alice.Store.EstablishImapBaseline(Reference(identity: 'b'), 55, 80, BaselineAt);
        Assert.True(alice.Store.DecideImapAdmission(changedAccount, 81, "friend@EXAMPLE.test", []));
        alice.Reopen();
        Assert.True(alice.Store.DecideImapAdmission(alice.Store.ReadImapCheckpoint(Reference(identity: 'b'))!, 81,
            "friend@example.test", []));
    }

    [Fact]
    public void RolledBackCaptureDoesNotCreateContactAndDeniedUidStaysDeniedAfterLaterCapture() {
        bool rejectCapture = true;
        using var fixture = new Fixture(new(BeforeCommit: operation => {
            if (rejectCapture && operation == "capture-action-batch") { throw new IOException("injected rollback"); }
        }));
        fixture.Baseline();
        Assert.Throws<IOException>(() => fixture.CaptureContact("friend@example.test", "smtp:alice:binding"));
        Assert.False(fixture.Store.DecideImapAdmission(fixture.Checkpoint, 1, "friend@example.test", []));
        rejectCapture = false;
        fixture.CaptureContact("friend@example.test", "smtp:alice:binding");
        Assert.Throws<GalateaDelegationStoreConflictException>(() =>
            fixture.Store.DecideImapAdmission(fixture.Checkpoint, 1, "friend@example.test", []));
        Assert.True(fixture.Store.DecideImapAdmission(fixture.Checkpoint, 2, "friend@example.test", []));
        Assert.Equal(1u, fixture.Checkpoint.ScannedThroughUid);
    }

    [Fact]
    public void CheckpointCasFencesRevisionValidityAndAccountIdentity() {
        using var fixture = new Fixture();
        GalateaImapCheckpointSnapshot first = fixture.Baseline(scannedThrough: 10);
        GalateaImapCheckpointSnapshot advanced = fixture.Store.AdvanceImapCheckpoint(first, 12);
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.AdvanceImapCheckpoint(first, 13));
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.AdvanceImapCheckpoint(advanced with { UidValidity = 99 }, 13));
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.AdvanceImapCheckpoint(advanced with { AccountReference = Reference(identity: 'b') }, 13));
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.AdvanceImapCheckpoint(advanced, 12));
        Assert.Equal(advanced, fixture.Checkpoint);
    }

    [Fact]
    public void UidValidityChangeBlocksUntilExplicitRebaselineAndKeepsOldInboxFacts() {
        using var fixture = new Fixture();
        fixture.Baseline(scannedThrough: 10);
        GalateaExternalMailInboxSnapshot old = fixture.Accept(11);
        GalateaImapCheckpointSnapshot blocked = fixture.Store.BlockImapCheckpoint(fixture.Checkpoint, "IMAP_UIDVALIDITY_CHANGED");
        Assert.Equal(11u, blocked.ScannedThroughUid);
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.AdvanceImapCheckpoint(blocked, 12));
        fixture.Reopen();
        Assert.Equal(blocked, fixture.Checkpoint);
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.RebaselineImapCheckpoint(blocked with { Revision = 0 }, 42, 100, BaselineAt.AddDays(1)));
        GalateaImapCheckpointSnapshot rebased = fixture.Store.RebaselineImapCheckpoint(blocked, 42, 100, BaselineAt.AddDays(1));
        Assert.Null(rebased.BlockedCode);
        Assert.Equal(42u, rebased.UidValidity);
        Assert.Equal(100u, rebased.ScannedThroughUid);
        Assert.Equal(old, fixture.Store.ReadExternalMail(old.InboxId));
        GalateaExternalMailInboxSnapshot next = fixture.Accept(101);
        Assert.Equal(42u, next.UidValidity);
        Assert.NotEqual(old.MessageId, next.MessageId);
        fixture.Reopen();
        Assert.Equal(old, fixture.Store.ReadExternalMail(old.InboxId));
        Assert.Equal(next, fixture.Store.ReadExternalMail(next.InboxId));
    }

    [Fact]
    public void ImapMaintenanceRebaselineDoesNotRecoverUnrelatedSmtpAttempt() {
        using var fixture = new Fixture();
        fixture.CaptureContact("friend@example.test", "smtp:alice:binding");
        fixture.Baseline(scannedThrough: 10);
        GalateaImapCheckpointSnapshot blocked = fixture.Store.BlockImapCheckpoint(fixture.Checkpoint, "IMAP_UIDVALIDITY_CHANGED");
        GalateaSmtpMailOutboxSnapshot attempting = fixture.Store.ClaimPendingSmtpMail()!;
        Assert.Equal(GalateaSmtpMailState.Attempting, attempting.State);
        fixture.Store.Dispose();

        using (GalateaDelegationSqliteStore maintenance = GalateaDelegationSqliteStore.OpenExistingForImapMaintenance(
            fixture.StorePath, fixture.Owner, Fixture.Limits)) {
            Assert.Equal(attempting, Assert.Single(maintenance.ReadSnapshot().SmtpMailOutboxes));
            Assert.Equal(blocked, maintenance.ReadImapCheckpoint(Reference()));
            GalateaImapCheckpointSnapshot rebased = maintenance.RebaselineImapCheckpoint(blocked, 42, 100, BaselineAt.AddDays(1));
            Assert.Null(rebased.BlockedCode);
            Assert.Equal(42u, rebased.UidValidity);
            Assert.Equal(100u, rebased.ScannedThroughUid);
            Assert.Equal(attempting, Assert.Single(maintenance.ReadSnapshot().SmtpMailOutboxes));
        }

        // Ordinary runtime startup retains its existing crash-recovery rule.
        fixture.Reopen();
        GalateaSmtpMailOutboxSnapshot recovered = Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        Assert.Equal(GalateaSmtpMailState.OutcomeUnknown, recovered.State);
        Assert.Equal("PROCESS_RESTART", recovered.ResultCode);
        Assert.Equal(attempting.Revision + 1, recovered.Revision);
        Assert.Null(fixture.Checkpoint.BlockedCode);
        Assert.Equal(42u, fixture.Checkpoint.UidValidity);
    }

    [Fact]
    public void AcceptedAndRejectedMailAdvanceCursorAtomicallyAndRetainStableIdentity() {
        using var fixture = new Fixture();
        fixture.Baseline();
        GalateaExternalMailInboxSnapshot accepted = fixture.Accept(1, body: "frozen body", subject: "subject", attachments: 2);
        Assert.Matches("^[0-9a-f]{32}$", accepted.MessageId);
        Assert.Equal(GalateaExternalMailInboxState.Pending, accepted.State);
        Assert.Equal("alice", accepted.TargetCharacterId);
        Assert.Equal("fixture-repository", accepted.TargetSessionRepositoryId);
        Assert.Equal("Galatea", accepted.TargetCharacterName);
        GalateaExternalMailInboxSnapshot rejected = fixture.Store.RejectImapMail(fixture.Checkpoint, 2, "IMAP_UNSUPPORTED_BODY");
        Assert.Equal(GalateaExternalMailInboxState.Rejected, rejected.State);
        Assert.Null(rejected.From);
        Assert.Null(rejected.Subject);
        Assert.Null(rejected.Body);
        Assert.Equal(2u, fixture.Checkpoint.ScannedThroughUid);
        fixture.Reopen();
        Assert.Equal(accepted, fixture.Store.ReadExternalMail(accepted.InboxId));
        Assert.Equal(rejected, fixture.Store.ReadExternalMail(rejected.InboxId));
        Assert.Equal(accepted, fixture.Store.ReadPendingExternalMail());
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Accept(1));
    }

    [Theory]
    [InlineData("imap-admission")]
    [InlineData("imap-accept")]
    [InlineData("imap-reject")]
    public void BeforeCommit_RollsBackCursorAndInboxTogether(string operationName) {
        bool armed = false;
        using var fixture = new Fixture(new(BeforeCommit: operation => {
            if (armed && operation == operationName) { throw new IOException("injected before commit"); }
        }));
        GalateaImapCheckpointSnapshot baseline = fixture.Baseline();
        armed = true;
        Assert.Throws<IOException>(() => Mutate(fixture, operationName));
        Assert.Equal(baseline, fixture.Checkpoint);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
        fixture.Reopen();
        Assert.Equal(baseline, fixture.Checkpoint);
        Assert.Null(fixture.Store.ReadPendingExternalMail());
    }

    [Theory]
    [InlineData("imap-baseline")]
    [InlineData("imap-admission")]
    [InlineData("imap-advance")]
    [InlineData("imap-accept")]
    [InlineData("imap-reject")]
    public void AfterCommit_RecoversExactCheckpointOrInboxPostStateWithoutDuplicates(string operationName) {
        int fired = 0;
        using var fixture = new Fixture(new(AfterCommitBeforeReturn: operation => {
            if (operation == operationName && Interlocked.Exchange(ref fired, 1) == 0) {
                throw new IOException("injected after commit");
            }
        }));
        fixture.Baseline();
        if (operationName != "imap-baseline") { Mutate(fixture, operationName); }
        Assert.Equal(1, fired);
        GalateaImapCheckpointSnapshot confirmed = fixture.Checkpoint;
        GalateaExternalMailInboxSnapshot? row = fixture.Store.ReadPendingExternalMail();
        long count = fixture.Scalar("SELECT count(*) FROM external_mail_inbox;");
        fixture.Reopen();
        Assert.Equal(confirmed, fixture.Checkpoint);
        Assert.Equal(count, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
        if (row is not null) { Assert.Equal(row, fixture.Store.ReadExternalMail(row.InboxId)); }
    }

    [Fact]
    public void UncertainCursorCommitAcceptsOnlyExactPostState() {
        Fixture? fixture = null;
        var hooks = new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: operation => {
            if (operation != "imap-admission") { return; }
            fixture!.Execute("UPDATE imap_checkpoint SET revision=revision+1;");
            throw new IOException("injected conflicting post state");
        });
        using (fixture = new Fixture(hooks)) {
            fixture.Baseline();
            Assert.Throws<GalateaDelegationCommitOutcomeException>(() =>
                fixture.Store.DecideImapAdmission(fixture.Checkpoint, 1, "stranger@example.test", []));
            Assert.Equal(1u, fixture.Checkpoint.ScannedThroughUid);
            Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
        }
    }

    [Fact]
    public void UncertainDeniedCursorCommitDoesNotHydrateUnrelatedHistoricalBody() {
        Fixture? fixture = null;
        var hooks = new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: operation => {
            if (operation != "imap-admission") { return; }
            // Deliberate corruption exists only inside this disposable fixture.
            // A full SMTP/outbound snapshot cannot read this missing body.
            fixture!.Execute("UPDATE outbound_mail SET body=NULL;");
            throw new IOException("injected after commit");
        });
        using (fixture = new Fixture(hooks)) {
            fixture.CaptureContact("friend@example.test", "smtp:alice:binding");
            fixture.Baseline();
            Assert.False(fixture.Store.DecideImapAdmission(fixture.Checkpoint, 1, "stranger@example.test", []));
            Assert.ThrowsAny<Exception>(() => fixture.Store.ReadSnapshot());
            fixture.Execute("UPDATE outbound_mail SET body='outgoing body';");
            fixture.Reopen();
            Assert.Equal(1u, fixture.Checkpoint.ScannedThroughUid);
            Assert.Null(fixture.Store.ReadPendingExternalMail());
        }
    }

    [Fact]
    public void PendingCountBackpressureDoesNotAdvanceUidAndObservedMailReleasesCapacity() {
        using var fixture = new Fixture();
        fixture.Baseline();
        for (uint uid = 1; uid <= 128; uid++) { fixture.Accept(uid); }
        GalateaImapCheckpointSnapshot full = fixture.Checkpoint;
        Assert.Null(fixture.Store.AcceptImapMail(full, 129, "Galatea", "friend@example.test", null, "waiting", 0));
        Assert.Equal(full, fixture.Checkpoint);
        Assert.Equal(128, fixture.Store.ReadImapInboxStatus().PendingCount);
        GalateaExternalMailInboxSnapshot first = fixture.Store.ReadPendingExternalMail()!;
        var bound = fixture.Store.BindExternalMailObservation(first.InboxId, first.Revision, Address(90), Observation(first));
        Assert.Equal(127, fixture.Store.ReadImapInboxStatus().PendingCount);
        Assert.Equal(1, fixture.Store.ReadImapInboxStatus().BoundCount);
        Assert.Null(fixture.Store.AcceptImapMail(full, 129, "Galatea", "friend@example.test", null, "waiting", 0));
        var observed = fixture.Store.CompleteExternalMailObservation(bound.InboxId, bound.Revision, Address(91));
        Assert.Equal("body", observed.Body);
        Assert.NotNull(fixture.Store.AcceptImapMail(full, 129, "Galatea", "friend@example.test", null, "waiting", 0));
        Assert.Equal(129u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(128, fixture.Store.ReadImapInboxStatus().PendingCount);
    }

    [Fact]
    public void Utf8CapacityIncludesSubjectAndFromBeforeCountLimit() {
        using var fixture = new Fixture();
        fixture.Baseline();
        string body = new('x', 64 * 1024);
        string subject = new('s', 4 * 1024);
        uint uid = 1;
        while (fixture.Store.AcceptImapMail(fixture.Checkpoint, uid, "Galatea", "friend@example.test", subject, body, 0) is not null) {
            uid++;
            Assert.True(uid <= 129, "The fixed queue limit must stop this bounded fixture.");
        }
        int pendingCount = fixture.Store.ReadImapInboxStatus().PendingCount;
        Assert.True(pendingCount < 128);
        long bytesPerMail = Encoding.UTF8.GetByteCount(body) + Encoding.UTF8.GetByteCount(subject) + Encoding.UTF8.GetByteCount("friend@example.test");
        Assert.True(pendingCount * bytesPerMail <= 8 * 1024 * 1024);
        Assert.True((pendingCount + 1) * bytesPerMail > 8 * 1024 * 1024);
        Assert.Equal(uid - 1, fixture.Checkpoint.ScannedThroughUid);
        fixture.Reopen();
        Assert.Equal(pendingCount, fixture.Store.ReadImapInboxStatus().PendingCount);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("subject")]
    public void PayloadLimitCountsUtf8BytesAndDoesNotAdvanceCursor(string oversizedField) {
        using var fixture = new Fixture();
        GalateaImapCheckpointSnapshot baseline = fixture.Baseline();
        string body = oversizedField == "body" ? new string('汉', 22_000) : "body";
        string? subject = oversizedField == "subject" ? new string('汉', 1400) : null;
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Store.AcceptImapMail(baseline, 1, "Galatea", "friend@example.test", subject, body, 0));
        Assert.Equal(baseline, fixture.Checkpoint);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("subject")]
    [InlineData("from")]
    [InlineData("message-id")]
    [InlineData("to")]
    [InlineData("attachment-count")]
    [InlineData("sender")]
    public void ObservationBindingRejectsChangedFrozenFacts(string changedField) {
        using var fixture = new Fixture();
        fixture.Baseline();
        GalateaExternalMailInboxSnapshot pending = fixture.Accept(1, subject: "subject", attachments: 2);
        Assert.Throws<InvalidDataException>(() => fixture.Store.BindExternalMailObservation(pending.InboxId, pending.Revision,
            Address(90), Observation(pending, changedField)));
        Assert.Equal(pending, fixture.Store.ReadExternalMail(pending.InboxId));
        Assert.Empty(fixture.Store.ReadUnsettledExternalMails());
    }

    [Fact]
    public void BindingResetAndCompletionAreRevisionFencedAndPreserveExactContent() {
        using var fixture = new Fixture();
        fixture.Baseline();
        GalateaExternalMailInboxSnapshot pending = fixture.Accept(1);
        SessionInputContent content = Observation(pending);
        GalateaExternalMailInboxSnapshot bound = fixture.Store.BindExternalMailObservation(pending.InboxId, pending.Revision, Address(90), content);
        fixture.Reopen();
        Assert.Equal(bound, Assert.Single(fixture.Store.ReadUnsettledExternalMails()));
        Assert.Equal(content, bound.BoundInput);
        Assert.Equal(Address(90), bound.ExpectedSessionHead);
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.ResetExternalMailObservation(bound.InboxId, pending.Revision));
        GalateaExternalMailInboxSnapshot reset = fixture.Store.ResetExternalMailObservation(bound.InboxId, bound.Revision);
        Assert.Equal(GalateaExternalMailInboxState.Pending, reset.State);
        Assert.Null(reset.BoundInput);
        Assert.Null(reset.ExpectedSessionHead);
        Assert.Equal(pending.MessageId, reset.MessageId);
        bound = fixture.Store.BindExternalMailObservation(reset.InboxId, reset.Revision, Address(90), content);
        GalateaExternalMailInboxSnapshot completed = fixture.Store.CompleteExternalMailObservation(bound.InboxId, bound.Revision, Address(91));
        Assert.Equal(GalateaExternalMailInboxState.Observed, completed.State);
        Assert.Equal(Address(91), completed.ObservationAddress);
        Assert.Empty(fixture.Store.ReadUnsettledExternalMails());
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.ResetExternalMailObservation(completed.InboxId, completed.Revision));
        fixture.Reopen();
        Assert.Equal(completed, fixture.Store.ReadExternalMail(completed.InboxId));
        Assert.Equal(1u, fixture.Checkpoint.ScannedThroughUid);
    }

    [Theory]
    [InlineData("bind-external-mail-observation", false)]
    [InlineData("bind-external-mail-observation", true)]
    [InlineData("reset-external-mail-observation", false)]
    [InlineData("reset-external-mail-observation", true)]
    [InlineData("complete-external-mail-observation", false)]
    [InlineData("complete-external-mail-observation", true)]
    [InlineData("quarantine-external-mail-observation", false)]
    [InlineData("quarantine-external-mail-observation", true)]
    public void ProofTransitionCommitBoundaryRetainsExactState(string operationName, bool afterCommit) {
        bool armed = false;
        int fired = 0;
        void Fail(string operation) {
            if (armed && operation == operationName && Interlocked.Exchange(ref fired, 1) == 0) {
                throw new IOException("injected proof transition boundary");
            }
        }
        var hooks = afterCommit ? new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: Fail)
            : new GalateaDelegationStoreTestHooks(BeforeCommit: Fail);
        using var fixture = new Fixture(hooks);
        fixture.Baseline();
        GalateaExternalMailInboxSnapshot previous = fixture.Accept(1);
        SessionInputContent input = Observation(previous);
        if (operationName != "bind-external-mail-observation") {
            previous = fixture.Store.BindExternalMailObservation(previous.InboxId, previous.Revision, Address(90), input);
        }
        armed = true;
        GalateaExternalMailInboxSnapshot Transition() => operationName switch {
            "bind-external-mail-observation" => fixture.Store.BindExternalMailObservation(previous.InboxId, previous.Revision, Address(90), input),
            "reset-external-mail-observation" => fixture.Store.ResetExternalMailObservation(previous.InboxId, previous.Revision),
            "complete-external-mail-observation" => fixture.Store.CompleteExternalMailObservation(previous.InboxId, previous.Revision, Address(91)),
            "quarantine-external-mail-observation" => fixture.Store.QuarantineExternalMailObservation(previous.InboxId, previous.Revision, "JOURNAL_PROOF_CONFLICT"),
            _ => throw new ArgumentException("Unknown proof transition.", nameof(operationName))
        };
        GalateaExternalMailInboxSnapshot confirmed;
        if (afterCommit) { confirmed = Transition(); }
        else {
            Assert.Throws<IOException>(() => Transition());
            confirmed = previous;
        }
        Assert.Equal(1, fired);
        Assert.Equal(confirmed, fixture.Store.ReadExternalMail(previous.InboxId));
        fixture.Reopen();
        Assert.Equal(confirmed, fixture.Store.ReadExternalMail(previous.InboxId));
        Assert.Equal(1u, fixture.Checkpoint.ScannedThroughUid);
    }

    [Fact]
    public void ProofQuarantineStaysVisibleWithoutReenteringPending() {
        using var fixture = new Fixture();
        fixture.Baseline();
        GalateaExternalMailInboxSnapshot pending = fixture.Accept(1);
        GalateaExternalMailInboxSnapshot bound = fixture.Store.BindExternalMailObservation(pending.InboxId, pending.Revision, Address(90), Observation(pending));
        GalateaExternalMailInboxSnapshot quarantined = fixture.Store.QuarantineExternalMailObservation(bound.InboxId, bound.Revision, "JOURNAL_PROOF_CONFLICT");
        Assert.Equal(GalateaExternalMailInboxState.Quarantined, quarantined.State);
        Assert.Equal("JOURNAL_PROOF_CONFLICT", quarantined.Code);
        Assert.True(fixture.Store.ReadImapInboxStatus().HasQuarantined);
        Assert.Null(fixture.Store.ReadPendingExternalMail());
        fixture.Reopen();
        Assert.Equal(quarantined, Assert.Single(fixture.Store.ReadUnsettledExternalMails()));
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.ResetExternalMailObservation(quarantined.InboxId, quarantined.Revision));
    }

    [Fact]
    public void V7UpgradePreservesExistingOutboundFactsAndImmediatelyDerivesNormalContacts() {
        using var fixture = new Fixture();
        fixture.CaptureContact("friend@example.test", "smtp:alice:previous-binding");
        GalateaDelegationStateSnapshot before = fixture.Store.ReadSnapshot();
        fixture.Store.Dispose();
        fixture.Execute($"""
            DROP TABLE external_mail_inbox;
            DROP TABLE imap_checkpoint;
            DROP INDEX ix_smtp_auto_display_sender;
            PRAGMA writable_schema=ON;
            UPDATE sqlite_schema SET sql=replace(sql,'schema_version = {GalateaDelegationSqliteStore.SchemaVersion}','schema_version = 7')
                WHERE name='delegation_meta';
            PRAGMA writable_schema=OFF;
            PRAGMA schema_version=801;
            PRAGMA ignore_check_constraints=ON;
            UPDATE delegation_meta SET schema_version=7;
            PRAGMA ignore_check_constraints=OFF;
            PRAGMA user_version=7;
            """);
        byte[] oldDatabase = File.ReadAllBytes(fixture.DatabasePath);
        Assert.Equal("DryRunReady", fixture.Upgrade(apply: false).Outcome);
        Assert.Equal(oldDatabase, File.ReadAllBytes(fixture.DatabasePath));
        GalateaDelegationStoreUpgradeResult result = fixture.Upgrade(apply: true);
        Assert.Equal("Upgraded", result.Outcome);
        Assert.NotNull(result.BackupPath);
        fixture.Reopen();
        GalateaDelegationStateSnapshot after = fixture.Store.ReadSnapshot();
        Assert.Equal(before.Captures, after.Captures);
        Assert.Equal(before.Mails, after.Mails);
        Assert.Equal(before.SmtpMailOutboxes, after.SmtpMailOutboxes);
        Assert.Equal(before.MailReceipts, after.MailReceipts);
        Assert.True(fixture.Store.DecideImapAdmission(fixture.Baseline(), 1, "friend@EXAMPLE.test", []));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
        Assert.Equal("NOCASE", fixture.TextScalar("SELECT coll FROM pragma_index_xinfo('ix_smtp_auto_display_sender') WHERE key=1;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictOpenRejectsMissingOrWrongRecipientIndexCollation(bool replaceWithBinary) {
        using var fixture = new Fixture();
        fixture.Store.Dispose();
        fixture.Execute("DROP INDEX ix_smtp_auto_display_sender;" + (replaceWithBinary
            ? "CREATE INDEX ix_smtp_auto_display_sender ON smtp_mail_outbox(recipient COLLATE BINARY);" : ""));
        Assert.Throws<InvalidDataException>(() => fixture.Reopen());
    }

    [Fact]
    public void StrictOpenRejectsRecipientIndexOnWrongTable() {
        using var fixture = new Fixture();
        fixture.Store.Dispose();
        fixture.Execute("""
            DROP INDEX ix_smtp_auto_display_sender;
            CREATE INDEX ix_smtp_auto_display_sender ON outbound_mail(recipient COLLATE NOCASE);
            """);
        Assert.Throws<InvalidDataException>(() => fixture.Reopen());
    }

    [Fact]
    public void ReadOnlyOpenCanInspectImapButCannotAdvanceIt() {
        using var fixture = new Fixture();
        GalateaImapCheckpointSnapshot baseline = fixture.Baseline();
        fixture.Accept(1);
        fixture.Store.Dispose();
        using GalateaDelegationSqliteStore readOnly = GalateaDelegationSqliteStore.OpenExistingReadOnly(fixture.StorePath, fixture.Owner, Fixture.Limits);
        Assert.Equal(1u, readOnly.ReadImapCheckpoint(Reference())!.ScannedThroughUid);
        Assert.NotNull(readOnly.ReadPendingExternalMail());
        Assert.Throws<GalateaDelegationStoreReadOnlyException>(() => readOnly.AdvanceImapCheckpoint(baseline, 2));
    }

    private static void Mutate(Fixture fixture, string operation) {
        switch (operation) {
            case "imap-admission":
                Assert.False(fixture.Store.DecideImapAdmission(fixture.Checkpoint, 1, "stranger@example.test", []));
                break;
            case "imap-advance":
                _ = fixture.Store.AdvanceImapCheckpoint(fixture.Checkpoint, 1);
                break;
            case "imap-accept":
                fixture.Accept(1);
                break;
            case "imap-reject":
                fixture.Store.RejectImapMail(fixture.Checkpoint, 1, "IMAP_UNSUPPORTED_BODY");
                break;
            default:
                throw new ArgumentException("Unknown synthetic mutation.", nameof(operation));
        }
    }

    private static SessionInputContent Observation(GalateaExternalMailInboxSnapshot row, string? changedField = null) =>
        SessionInputContent.Structured(GalateaObservationContent.V5SchemaId, JsonSerializer.SerializeToElement(new {
            v = 1, kind = "email-inbound",
            sender = new { kind = changedField == "sender" ? "player" : "runtime", id = "galatea", name = "Galatea runtime" },
            externalLocalTimestamp = DateTimeOffset.UnixEpoch.ToString("O"),
            action = new {
                messageId = changedField == "message-id" ? new string('0', 32) : row.MessageId,
                from = changedField == "from" ? "changed@example.test" : row.From,
                to = changedField == "to" ? "Changed" : row.TargetCharacterName,
                subject = changedField == "subject" ? "changed" : row.Subject,
                body = changedField == "body" ? "changed" : row.Body,
                attachmentCount = changedField == "attachment-count" ? row.AttachmentCount + 1 : row.AttachmentCount
            },
            notices = Array.Empty<object>(), recalls = Array.Empty<object>(),
            connectionState = new {
                runtimeOverrideConnectionId = (string?)null, effectiveConnectionId = "fixture", turnConnectionId = "fixture",
                effectiveName = "", turnName = "", lastChange = (object?)null
            }
        }));

    private sealed class Fixture : IDisposable {
        internal static readonly GalateaDelegationStoreLimits Limits = new(32, 100_000, 1024, 16, 16 * 1024);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-imap-persistence-" + Guid.NewGuid().ToString("N"));
        private int _captureNumber = 100;
        internal GalateaDelegationStoreOwner Owner { get; }
        internal string StorePath => Path.Combine(_root, "store");
        internal string DatabasePath => Path.Combine(StorePath, GalateaDelegationSqliteStore.DatabaseFileName);
        internal GalateaDelegationSqliteStore Store { get; private set; }
        internal GalateaImapCheckpointSnapshot Checkpoint => Store.ReadImapCheckpoint(Reference(Owner.CharacterId))!;

        internal Fixture(GalateaDelegationStoreTestHooks? hooks = null, string character = "alice") {
            Owner = new(character, "fixture-repository");
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(_root);
            Directory.CreateDirectory(_root);
            Store = GalateaDelegationSqliteStore.CreateNew(StorePath, Owner,
                new(new EventJournalPhysicalAppendFrontier(1, 4), Address(90)), Limits, hooks);
        }

        internal GalateaImapCheckpointSnapshot Baseline(uint scannedThrough = 0) =>
            Store.EstablishImapBaseline(Reference(Owner.CharacterId), 41, scannedThrough, BaselineAt);

        internal GalateaExternalMailInboxSnapshot Accept(uint uid, string from = "friend@example.test", string body = "body",
            string? subject = null, int attachments = 0) =>
            Assert.IsType<GalateaExternalMailInboxSnapshot>(Store.AcceptImapMail(Checkpoint, uid, "Galatea", from, subject, body, attachments));

        internal void CaptureContact(string recipient, string smtpReference) =>
            Store.CaptureActionBatch(new(Address(_captureNumber++), new string('a', 64), 12, "imap-persistence-tests-v1",
                [new(recipient, null, "outgoing body", null, "sent")], new GalateaSenderSnapshot("character", Owner.CharacterId, "Galatea"),
                SmtpSenderAccountReference: smtpReference));

        internal void Reopen() {
            Store.Dispose();
            Store = GalateaDelegationSqliteStore.OpenExisting(StorePath, Owner, Limits);
        }

        internal GalateaDelegationStoreUpgradeResult Upgrade(bool apply) =>
            GalateaDelegationSqliteStore.UpgradeExisting(StorePath, Owner, Limits, apply);

        internal SqliteConnection OpenSql() {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        internal void Execute(string sql) {
            using SqliteConnection connection = OpenSql();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal long Scalar(string sql) {
            using SqliteConnection connection = OpenSql();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        internal string TextScalar(string sql) {
            using SqliteConnection connection = OpenSql();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)command.ExecuteScalar()!;
        }

        public void Dispose() {
            Store.Dispose();
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
        }
    }
}
