using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaSemanticDelegationTests {
    [Fact]
    public void FullTaskCommitmentUsesExactStrictUtf8WithoutNormalization() {
        GalateaTaskCommitment value = GalateaTaskCommitment.FromTask(" \n你好😀\r\n");
        Assert.Equal(14, value.Utf8Bytes);
        Assert.Equal("20cd4e3a5b4be5a82e833481e77d3b30315bab75d967ac1260c5ce5cdece9285", value.Sha256);
        Assert.Throws<EncoderFallbackException>(() => GalateaTaskCommitment.FromTask("bad\uD800"));
    }

    [Fact]
    public async Task CapturePreservesOriginalBodyAndSenderWhileStartAtomicallyStoresFullProjectedTaskEvidence() {
        using var fixture = new Fixture();
        const string body = "原始任务\n```json\n{\"nested\":\"text\"}\n```\n";
        fixture.Capture(body);
        fixture.Bind();
        var transport = new Transport();
        transport.Starting = request => {
            GalateaOutboundMailSnapshot durable = fixture.Mail;
            Assert.Equal(GalateaDurableMailState.Started, durable.State);
            Assert.Equal(body, durable.Body);
            Assert.Equal("Resident", durable.SenderName);
            Assert.Equal(GalateaTaskCommitment.FromTask(request.Task), GalateaTaskCommitment.FromStored(durable));
            Assert.NotEqual(body, request.Task);
            Assert.Contains(body, request.Task);
            Assert.Contains("character-a", request.Task);
        };

        GalateaDurableDelegationPulseResult result = await fixture.Driver(transport).PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.MailAccepted, result.Step);
        Assert.Equal(1, transport.StartCount);
        Assert.Equal("semantic-mail-v1", fixture.Mail.ContentFormat);
        Assert.Equal(body, fixture.Mail.Body);
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM pragma_table_info('outbound_mail') WHERE name IN ('task','rendered_task','frozen_task');"));
        fixture.Reopen();
        Assert.Equal(GalateaTaskCommitment.FromTask(transport.StartTask!), GalateaTaskCommitment.FromStored(fixture.Mail));
        Assert.Equal("Resident", fixture.Mail.SenderName);
    }

    [Fact]
    public async Task StartCommitOutcomeUnknownNeverInvokesTransportEvenWhenClaimWasPublished() {
        using var fixture = new Fixture(hooks: new(AfterCommitBeforeReturn: operation => {
            if (operation == "start-queued-mail") { throw new IOException("injected commit response loss"); }
        }));
        fixture.Capture("task");
        fixture.Bind();
        var transport = new Transport();

        await Assert.ThrowsAsync<GalateaDelegationCommitOutcomeException>(() => fixture.Driver(transport).PulseAsync());

        Assert.Equal(0, transport.StartCount);
        Assert.Equal(GalateaDurableMailState.Started, fixture.Mail.State);
        Assert.NotNull(fixture.Mail.TaskSha256);
        fixture.Reopen();
        GalateaDurableDelegationPulseResult recovered = await fixture.Driver(transport).PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.RecoveredStarted, recovered.Step);
        Assert.Equal(0, transport.StartCount);
    }

    [Fact]
    public async Task ProjectionOverCurrentSendLimitKeepsQueuedBodyAndCreatesNoFailureNotice() {
        using var fixture = new Fixture(maximumTaskBytes: 256);
        fixture.Capture(new string('x', 80));
        fixture.Bind();
        GalateaDelegationStateSnapshot before = fixture.Store.ReadSnapshot();
        var transport = new Transport();

        GalateaDurableDelegationPulseResult result = await fixture.Driver(transport).PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.LocalProjectionRejected, result.Step);
        Assert.Equal("TASK_PROJECTION_TOO_LARGE", result.Code);
        Assert.Equal(0, transport.StartCount);
        Assert.Equal(before.StoreRevision, fixture.Store.ReadSnapshot().StoreRevision);
        Assert.Equal(before.Mails.Single(), fixture.Mail);
        Assert.Empty(fixture.Store.ReadSnapshot().Notices);
    }

    [Fact]
    public async Task FailedDispatchPersistsCodeAndSourceFactsWithoutRenderedFailureBody() {
        using var fixture = new Fixture();
        fixture.Capture("task");
        fixture.Bind();
        _ = await fixture.Driver(new Transport()).PulseAsync();
        GalateaOutboundMailSnapshot accepted = fixture.Mail;

        GalateaReplyNoticeSnapshot notice = fixture.Store.RecordFailedMail(accepted.DispatchId, accepted.Revision,
            "thread-a", "turn-a", "inspect-dispatch", "TURN_FAILED");

        Assert.Equal("semantic-notice-v1", notice.NoticeFormat);
        Assert.Empty(notice.Body);
        var failure = Assert.IsType<PlayerTurnNotice.DeliveryFailure>(GalateaDurableNoticeContent.Project(notice));
        Assert.Equal("TURN_FAILED", failure.Code);
        Assert.Equal("inspect-dispatch", failure.Stage);
        Assert.Equal("turn-a", failure.TurnId);
        Assert.Equal(notice.NoticeId, failure.NoticeId);
        Assert.Throws<InvalidOperationException>(() => failure.Body);
        fixture.Reopen();
        Assert.Equal(notice, Assert.Single(fixture.Store.ReadSnapshot().Notices));
    }

    [Fact]
    public async Task TypedReplyLeaseReopensAndProvesExactRawAppendWithoutProjector() {
        using var fixture = new Fixture();
        fixture.Capture("task");
        fixture.Bind();
        _ = await fixture.Driver(new Transport()).PulseAsync();
        GalateaOutboundMailSnapshot accepted = fixture.Mail;
        _ = fixture.Store.RecordCompletedMail(accepted.DispatchId, accepted.Revision, "thread-a", "turn-a", "original remote reply");
        using SessionJournalEngine journal = SessionJournalEngine.Create(Path.Combine(fixture.Root, "journal"), new("model", "system", "test"));
        var created = Assert.IsType<GalateaDurableReplyLeaseBeginResult.Created>(new GalateaDurableReplyLeaseReconciler(fixture.Store).BeginCutoff("continue"));
        SessionInputContent input = GalateaObservationContent.Create(new GalateaFreshInput.PlayerAction("continue", new("player", "admin", "Operator")),
            Fixture.Timestamp, fixture.Sender, created.Lease.ReadNotices());
        EventAddress head = journal.ReadCurrentHead()!.Value;
        long beforeBindRevision = fixture.Store.ReadSnapshot().StoreRevision;
        Assert.Throws<ArgumentException>(() => fixture.Store.BindReplyLeaseObservationBase(
            created.Lease.LeaseId, fixture.Store.ReadSnapshot().ActiveLease!.Revision,
            EventAddressTextCodec.Format(head), SessionInputContent.Text("arbitrary")));
        Assert.Equal(beforeBindRevision, fixture.Store.ReadSnapshot().StoreRevision);
        GalateaReplyLeaseSnapshot bound = created.Lease.BindObservationBase(journal, head, input);
        Assert.Null(bound.RenderedObservation);
        Assert.Equal(input, bound.BoundInput);
        fixture.Reopen();
        Assert.Equal(input, fixture.Store.ReadSnapshot().ActiveLease!.BoundInput);
        EventAddress appended = journal.AppendObservation(input);

        var proof = Assert.IsType<SessionExpectedObservationTurnReadResult.InProgress>(journal.ReadView.ProveExpectedObservationTurnAtSelectedHead(new(appended, head, input)));

        Assert.Equal(appended, proof.Evidence.ObservationAddress);
        Assert.IsType<GalateaDurableReplyLeaseReconcileResult.Retained>(new GalateaDurableReplyLeaseReconciler(fixture.Store).ReconcileActiveLease(journal));
    }

    [Fact]
    public void InternalMailTypedBindingValidatesCapturedSenderAndSurvivesColdProof() {
        using var fixture = new Fixture();
        fixture.Capture("mail body", recipient: "Target", internalTarget: new("target-character", "target-repository", "Resident"));
        GalateaInternalMailOutboxSnapshot outbox = Assert.Single(fixture.Store.ReadSnapshot().InternalMailOutboxes);
        MailboxMessage message = MailboxMessage.FromCanonicalEnvelope(outbox.MessageId, "Resident", "Target", null, "mail body");
        using SessionJournalEngine journal = SessionJournalEngine.Create(Path.Combine(fixture.Root, "journal"), new("model", "system", "test"));
        var binding = new GalateaInternalMailDeliveryBinding(fixture.Store, outbox.DispatchId, outbox.Revision);
        var target = new GalateaSenderSnapshot("character", "target-character", "Target");
        SessionInputContent input = GalateaObservationContent.Create(new GalateaFreshInput.InboundMail(message, binding, Sender: fixture.Sender), Fixture.Timestamp, target);
        EventAddress head = journal.ReadCurrentHead()!.Value;
        long beforeBindRevision = fixture.Store.ReadSnapshot().StoreRevision;
        Assert.Throws<ArgumentException>(() => fixture.Store.BindInternalMailObservation(outbox.DispatchId,
            outbox.Revision, EventAddressTextCodec.Format(head), SessionInputContent.Text("arbitrary")));
        MailboxMessage changedMessage = MailboxMessage.FromCanonicalEnvelope(outbox.MessageId, "Resident", "Target", null, "substituted body");
        SessionInputContent changedInput = GalateaObservationContent.Create(
            new GalateaFreshInput.InboundMail(changedMessage, binding, Sender: fixture.Sender), Fixture.Timestamp, target);
        Assert.Throws<InvalidDataException>(() => fixture.Store.BindInternalMailObservation(outbox.DispatchId,
            outbox.Revision, EventAddressTextCodec.Format(head), changedInput));
        Assert.Equal(beforeBindRevision, fixture.Store.ReadSnapshot().StoreRevision);
        binding.BindObservationBase(journal, head, input);
        fixture.Reopen();
        outbox = Assert.Single(fixture.Store.ReadSnapshot().InternalMailOutboxes);
        Assert.Null(outbox.RenderedObservation);
        Assert.Equal(input, outbox.BoundInput);
        EventAddress appended = journal.AppendObservation(input);
        Assert.IsType<SessionExpectedObservationTurnReadResult.InProgress>(journal.ReadView.ProveExpectedObservationTurnAtSelectedHead(new(appended, head, outbox.ObservationContent!)));
        _ = fixture.Store.CompleteInternalMailObservation(outbox.DispatchId, outbox.Revision, EventAddressTextCodec.Format(appended));
        Assert.Equal(GalateaInternalMailState.Delivered, Assert.Single(fixture.Store.ReadSnapshot().InternalMailOutboxes).State);
    }

    [Fact]
    public async Task V4AcceptedMailUpgradesWithoutInventingSenderOrRerenderingItsOriginalTask() {
        using var fixture = new Fixture();
        const string oldTask = "旧的完整 task\r\n```\nverbatim\n```\n";
        fixture.Capture(oldTask);
        fixture.Bind();
        GalateaOutboundMailSnapshot mail = fixture.Mail;
        GalateaOutboundMailSnapshot started = fixture.Store.StartQueuedMail(mail.DispatchId, mail.Revision,
            fixture.Store.ReadSnapshot().Route.Revision, GalateaTaskCommitment.FromTask(oldTask));
        _ = fixture.Store.RecordMailAccepted(started.DispatchId, started.Revision, "thread-a", "turn-a");
        fixture.DowngradeFixtureToV4();

        GalateaDelegationStoreUpgradeResult upgraded = fixture.UpgradeFixture();

        Assert.Equal("Upgraded", upgraded.Outcome);
        Assert.True(File.Exists(upgraded.BackupPath));
        Assert.Equal("legacy-task", fixture.Mail.ContentFormat);
        Assert.Null(fixture.Mail.SenderName);
        Assert.Null(fixture.Mail.TaskSha256);
        Assert.Equal(oldTask, fixture.Mail.Body);
        var transport = new Transport();
        GalateaDurableDelegationPulseResult result = await fixture.Driver(transport).PulseAsync();
        Assert.Equal(GalateaDurableDelegationPulseStep.AcceptedRunning, result.Step);
        Assert.Equal(0, transport.StartCount);
        Assert.Equal(GalateaTaskCommitment.FromTask(oldTask), transport.LastInspection!.TaskCommitment);
    }

    [Fact]
    public void V4AlreadyBoundLeaseRetainsExactLegacyTextAndItsOriginalDigest() {
        using var fixture = new Fixture();
        fixture.Capture("old task");
        fixture.Bind();
        GalateaOutboundMailSnapshot mail = fixture.Mail;
        mail = fixture.Store.StartQueuedMail(mail.DispatchId, mail.Revision, fixture.Store.ReadSnapshot().Route.Revision,
            GalateaTaskCommitment.FromTask("old task"));
        _ = fixture.Store.RecordCompletedMail(mail.DispatchId, mail.Revision, "thread-a", "turn-a", "old reply");
        // Explicitly construct historical persisted facts. New APIs deliberately
        // cannot manufacture an old text-bound lease from new semantic notices.
        fixture.ExecuteSql("UPDATE reply_notice SET notice_format='legacy-text',sender_kind=NULL,sender_id=NULL,sender_name=NULL,detail=NULL,thread_id=NULL,turn_id=NULL;");
        GalateaReplyNoticeSnapshot notice = Assert.Single(fixture.Store.ReadSnapshot().Notices);
        _ = fixture.Store.BeginReplyLeaseMembership("lease-a", "player", [new(notice.NoticeId, notice.Revision)]);
        string oldObservation = PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("player", Fixture.Timestamp,
            [new PlayerTurnNotice.Reply("old reply")]));
        int bytes = Encoding.UTF8.GetByteCount(oldObservation);
        string sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(oldObservation)));
        fixture.ExecuteSql("""
            UPDATE reply_lease SET state='ObservationBound', expected_session_head=$head,
                rendered_observation=$body, observation_utf8_bytes=$bytes, observation_sha256=$sha;
            """, ("$head", "ej1:00000000000000020000000100000000"), ("$body", oldObservation), ("$bytes", bytes), ("$sha", sha));
        fixture.DowngradeFixtureToV4();

        _ = fixture.UpgradeFixture();

        GalateaReplyLeaseSnapshot bound = fixture.Store.ReadSnapshot().ActiveLease!;
        Assert.Equal(oldObservation, bound.RenderedObservation);
        Assert.Null(bound.BoundInput);
        Assert.Equal(bytes, bound.ObservationUtf8Bytes);
        Assert.Equal(sha, bound.ObservationSha256);
        Assert.Equal(SessionInputContent.Text(oldObservation), bound.ObservationContent);
        PlayerTurnNotice.Reply oldNotice = Assert.IsType<PlayerTurnNotice.Reply>(GalateaDurableNoticeContent.Project(Assert.Single(fixture.Store.ReadSnapshot().Notices)));
        Assert.Null(oldNotice.Sender);
        Assert.Equal(notice.DispatchId, oldNotice.DispatchId);
        Assert.Equal(notice.NoticeId, oldNotice.NoticeId);
        Assert.Equal("turn-a", oldNotice.TurnId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MinimumInboxQuotaSettlesFullReplyIncludingPreviouslyAcceptedV4Mail(bool upgradeAcceptedV4) {
        const int payloadBytes = PlayerTurnObservationEnvelope.MaximumFailureUtf8Bytes;
        using var fixture = new Fixture(maximumReplyBytes: payloadBytes, maximumInboxReplies: 1, maximumInboxBytes: payloadBytes);
        const string task = "original task";
        fixture.Capture(task);
        fixture.Bind();
        if (upgradeAcceptedV4) {
            GalateaOutboundMailSnapshot pending = fixture.Mail;
            GalateaOutboundMailSnapshot started = fixture.Store.StartQueuedMail(pending.DispatchId, pending.Revision,
                fixture.Store.ReadSnapshot().Route.Revision, GalateaTaskCommitment.FromTask(task));
            _ = fixture.Store.RecordMailAccepted(started.DispatchId, started.Revision, "thread-a", "turn-a");
            fixture.DowngradeFixtureToV4();
            _ = fixture.UpgradeFixture();
        }
        else {
            var transport = new Transport();
            Assert.Equal(GalateaDurableDelegationPulseStep.MailAccepted, (await fixture.Driver(transport).PulseAsync()).Step);
            Assert.Equal(1, transport.StartCount);
        }
        GalateaOutboundMailSnapshot accepted = fixture.Mail;
        string fullReply = new('r', payloadBytes);

        GalateaReplyNoticeSnapshot settled = fixture.Store.RecordCompletedMail(accepted.DispatchId,
            accepted.Revision, "thread-a", "turn-a", fullReply);

        Assert.Equal(fullReply, settled.Body);
        Assert.Equal("semantic-notice-v1", settled.NoticeFormat);
        Assert.Null(fixture.Store.ReadSnapshot().Route.ActiveDispatchId);
        fixture.Reopen();
        Assert.Equal(fullReply, Assert.Single(fixture.Store.ReadSnapshot().Notices).Body);
        Assert.Equal(payloadBytes, fixture.Scalar("SELECT maximum_inbox_utf8_bytes FROM delegation_meta;"));
        Assert.Equal(payloadBytes, fixture.Scalar("SELECT maximum_reply_utf8_bytes FROM delegation_meta;"));
    }

    [Fact]
    public async Task PayloadQuotaStillBackpressuresNextDispatchWithAFreeNoticeSlot() {
        const int payloadBytes = PlayerTurnObservationEnvelope.MaximumFailureUtf8Bytes;
        using var fixture = new Fixture(maximumReplyBytes: payloadBytes, maximumInboxReplies: 2, maximumInboxBytes: payloadBytes);
        fixture.Capture("first task");
        fixture.Bind();
        _ = await fixture.Driver(new Transport()).PulseAsync();
        GalateaOutboundMailSnapshot accepted = fixture.Mail;
        _ = fixture.Store.RecordCompletedMail(accepted.DispatchId, accepted.Revision, "thread-a", "turn-a", new string('r', payloadBytes));
        fixture.Capture("second task", sourceAddress: "ej1:00000000000000680000000100000000");
        var transport = new Transport();
        GalateaDelegationStateSnapshot before = fixture.Store.ReadSnapshot();

        GalateaDurableDelegationPulseResult result = await fixture.Driver(transport).PulseAsync();

        Assert.Equal(GalateaDurableDelegationPulseStep.InboxBackpressure, result.Step);
        Assert.Equal(0, transport.StartCount);
        Assert.Equal(before.StoreRevision, fixture.Store.ReadSnapshot().StoreRevision);
        Assert.Single(fixture.Store.ReadSnapshot().Mails, mail => mail.State == GalateaDurableMailState.Queued);
        fixture.Reopen();
        Assert.Single(fixture.Store.ReadSnapshot().Notices);
    }

    [Theory]
    [InlineData("sender_kind", true)]
    [InlineData("sender_id", true)]
    [InlineData("sender_name", true)]
    [InlineData("sender_kind", false)]
    [InlineData("sender_id", false)]
    [InlineData("sender_name", false)]
    [InlineData("detail", false)]
    public async Task NoticeReaderRejectsHiddenLegacyAndIncompleteSemanticSourceColumns(string column, bool legacy) {
        using var fixture = new Fixture();
        fixture.Capture("task");
        fixture.Bind();
        _ = await fixture.Driver(new Transport()).PulseAsync();
        GalateaOutboundMailSnapshot accepted = fixture.Mail;
        _ = fixture.Store.RecordCompletedMail(accepted.DispatchId, accepted.Revision, "thread-a", "turn-a", "reply");
        if (legacy) {
            fixture.ExecuteSql("UPDATE reply_notice SET notice_format='legacy-text',sender_kind=NULL,sender_id=NULL,sender_name=NULL,detail=NULL,thread_id=NULL,turn_id=NULL;");
        }
        string badValue = legacy || column == "detail" ? "'hidden'" : "NULL";
        fixture.ExecuteSql($"UPDATE reply_notice SET {column}={badValue};");

        Assert.Throws<InvalidDataException>(fixture.Reopen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewBoundColumnCannotIntroduceTextEvenWithMatchingMachineEnvelopeDigest(bool internalMail) {
        using var fixture = new Fixture();
        if (internalMail) {
            fixture.Capture("mail body", recipient: "Target", internalTarget: new("target-character", "target-repository", "Resident"));
        }
        else {
            fixture.Capture("task");
            fixture.Bind();
            _ = await fixture.Driver(new Transport()).PulseAsync();
            GalateaOutboundMailSnapshot accepted = fixture.Mail;
            _ = fixture.Store.RecordCompletedMail(accepted.DispatchId, accepted.Revision, "thread-a", "turn-a", "reply");
        }
        using SessionJournalEngine journal = SessionJournalEngine.Create(Path.Combine(fixture.Root, "journal"), new("model", "system", "test"));
        string head = EventAddressTextCodec.Format(journal.ReadCurrentHead()!.Value);
        byte[] bytes = SessionInputContent.Text("arbitrary").ToUtf8Json();
        if (internalMail) {
            fixture.ExecuteSql("UPDATE internal_mail_outbox SET state='ObservationBound',expected_session_head=$head,bound_input=$input;",
                ("$head", head), ("$input", Encoding.UTF8.GetString(bytes)));
        }
        else {
            var lease = Assert.IsType<GalateaDurableReplyLeaseBeginResult.Created>(new GalateaDurableReplyLeaseReconciler(fixture.Store).BeginCutoff("continue"));
            fixture.ExecuteSql("UPDATE reply_lease SET state='ObservationBound',expected_session_head=$head,bound_input=$input,observation_utf8_bytes=$bytes,observation_sha256=$sha;",
                ("$head", head), ("$input", Encoding.UTF8.GetString(bytes)), ("$bytes", bytes.Length), ("$sha", Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        Assert.Throws<InvalidDataException>(fixture.Reopen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyInternalBindingIsReadableOnlyForRealV4Mail(bool upgradeV4) {
        using var fixture = new Fixture();
        fixture.Capture("mail body", recipient: "Target", internalTarget: new("target-character", "target-repository", "Resident"));
        GalateaInternalMailOutboxSnapshot outbox = Assert.Single(fixture.Store.ReadSnapshot().InternalMailOutboxes);
        MailboxMessage message = MailboxMessage.FromCanonicalEnvelope(outbox.MessageId, "Resident", "Target", null, "mail body");
        string oldObservation = GalateaMailboxObservationEnvelope.Wrap(message);
        using SessionJournalEngine journal = SessionJournalEngine.Create(Path.Combine(fixture.Root, "journal"), new("model", "system", "test"));
        EventAddress head = journal.ReadCurrentHead()!.Value;
        fixture.ExecuteSql("UPDATE internal_mail_outbox SET state='ObservationBound',expected_session_head=$head,rendered_observation=$text;",
            ("$head", EventAddressTextCodec.Format(head)), ("$text", oldObservation));
        if (!upgradeV4) {
            InvalidDataException failure = Assert.Throws<InvalidDataException>(fixture.Reopen);
            Assert.Contains("Semantic mail cannot contain a legacy text binding", failure.Message);
            return;
        }
        // The only authorizer for this old column is an actual V4 source.
        fixture.DowngradeFixtureToV4();
        _ = fixture.UpgradeFixture();
        outbox = Assert.Single(fixture.Store.ReadSnapshot().InternalMailOutboxes);
        Assert.Equal("legacy-task", fixture.Mail.ContentFormat);
        Assert.Null(outbox.BoundInput);
        Assert.Equal(oldObservation, outbox.RenderedObservation);
        SessionInputContent content = SessionInputContent.Text(oldObservation);
        EventAddress appended = journal.AppendObservation(content);
        Assert.IsType<SessionExpectedObservationTurnReadResult.InProgress>(journal.ReadView.ProveExpectedObservationTurnAtSelectedHead(new(appended, head, outbox.ObservationContent!)));
        _ = fixture.Store.CompleteInternalMailObservation(outbox.DispatchId, outbox.Revision, EventAddressTextCodec.Format(appended));
        fixture.Reopen();
        Assert.Equal(GalateaInternalMailState.Delivered, Assert.Single(fixture.Store.ReadSnapshot().InternalMailOutboxes).State);
    }

    [Fact]
    public async Task SemanticReplyCannotUseLegacyBindingColumnEvenWhenOldTextAndDigestMatchItsBody() {
        using var fixture = new Fixture();
        fixture.Capture("task");
        fixture.Bind();
        _ = await fixture.Driver(new Transport()).PulseAsync();
        GalateaOutboundMailSnapshot mail = fixture.Mail;
        _ = fixture.Store.RecordCompletedMail(mail.DispatchId, mail.Revision, "thread-a", "turn-a", "reply");
        _ = new GalateaDurableReplyLeaseReconciler(fixture.Store).BeginCutoff("continue");
        string oldObservation = PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("continue", Fixture.Timestamp,
            [new PlayerTurnNotice.Reply("reply")]));
        byte[] bytes = Encoding.UTF8.GetBytes(oldObservation);
        fixture.ExecuteSql("UPDATE reply_lease SET state='ObservationBound',expected_session_head=$head,rendered_observation=$text,observation_utf8_bytes=$bytes,observation_sha256=$sha;",
            ("$head", "ej1:00000000000000020000000100000000"), ("$text", oldObservation),
            ("$bytes", bytes.Length), ("$sha", Convert.ToHexStringLower(SHA256.HashData(bytes))));

        InvalidDataException failure = Assert.Throws<InvalidDataException>(fixture.Reopen);

        Assert.Contains("A lease containing semantic notices cannot contain a legacy text binding", failure.Message);
    }

    [Fact]
    public async Task MixedLegacyAndSemanticPendingNoticesCanBindNewStructuredObservation() {
        using var fixture = new Fixture();
        fixture.Capture("old task");
        fixture.Bind();
        GalateaOutboundMailSnapshot mail = fixture.Mail;
        mail = fixture.Store.StartQueuedMail(mail.DispatchId, mail.Revision, fixture.Store.ReadSnapshot().Route.Revision,
            GalateaTaskCommitment.FromTask("old task"));
        _ = fixture.Store.RecordCompletedMail(mail.DispatchId, mail.Revision, "thread-a", "turn-a", "old reply");
        fixture.DowngradeFixtureToV4();
        _ = fixture.UpgradeFixture();
        fixture.Capture("new task", sourceAddress: "ej1:00000000000000680000000100000000");
        _ = await fixture.Driver(new Transport()).PulseAsync();
        mail = Assert.Single(fixture.Store.ReadSnapshot().Mails, candidate => candidate.State == GalateaDurableMailState.Accepted);
        _ = fixture.Store.RecordCompletedMail(mail.DispatchId, mail.Revision, "thread-a", "turn-a", "new reply");
        using SessionJournalEngine journal = SessionJournalEngine.Create(Path.Combine(fixture.Root, "journal"), new("model", "system", "test"));
        var cutoff = Assert.IsType<GalateaDurableReplyLeaseBeginResult.Created>(new GalateaDurableReplyLeaseReconciler(fixture.Store).BeginCutoff("continue"));
        PlayerTurnNotice[] notices = cutoff.Lease.ReadNotices().ToArray();
        Assert.Equal(2, notices.Length);
        Assert.Null(Assert.IsType<PlayerTurnNotice.Reply>(notices[0]).Sender);
        Assert.NotNull(Assert.IsType<PlayerTurnNotice.Reply>(notices[1]).Sender);
        SessionInputContent input = GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction("continue", new("player", "admin", "Operator")), Fixture.Timestamp, fixture.Sender, notices);

        _ = cutoff.Lease.BindObservationBase(journal, journal.ReadCurrentHead()!.Value, input);

        fixture.Reopen();
        Assert.Null(fixture.Store.ReadSnapshot().ActiveLease!.RenderedObservation);
        Assert.Equal(input, fixture.Store.ReadSnapshot().ActiveLease!.BoundInput);
    }

    private sealed class Fixture : IDisposable {
        internal static readonly DateTimeOffset Timestamp = new(2026, 9, 15, 9, 0, 0, TimeSpan.FromHours(8));
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "galatea-semantic-delegation", Guid.NewGuid().ToString("N"));
        internal GalateaSenderSnapshot Sender { get; } = new("character", "character-a", "Resident");
        private readonly GalateaDelegationStoreOwner _owner = new("character-a", "repository-a");
        private readonly GalateaDelegationStoreLimits _limits;
        internal GalateaDelegationSqliteStore Store { get; private set; }
        internal GalateaOutboundMailSnapshot Mail => Assert.Single(Store.ReadSnapshot().Mails);
        internal Fixture(int maximumTaskBytes = 100_000, GalateaDelegationStoreTestHooks? hooks = null,
            int maximumReplyBytes = 16 * 1024, int maximumInboxReplies = 16, int maximumInboxBytes = 256 * 1024) {
            Directory.CreateDirectory(Root);
            _limits = new(16, maximumTaskBytes, maximumReplyBytes, maximumInboxReplies, maximumInboxBytes);
            Store = GalateaDelegationSqliteStore.CreateNew(Path.Combine(Root, "store"), _owner,
                new(new EventJournalPhysicalAppendFrontier(1, 4), null), _limits, hooks);
        }
        internal void Capture(string body, string recipient = "Codex", GalateaInternalMailTarget? internalTarget = null,
            string sourceAddress = "ej1:00000000000000640000000100000000") => Store.CaptureActionBatch(new(
            sourceAddress, new string('a', 64), 12, "extractor-v1",
            [new(recipient, null, body, null, "sent it")], Sender,
            internalTarget is null ? null : [internalTarget]));
        internal void Bind() {
            GalateaRouteBindingSnapshot route = Store.ReadSnapshot().Route;
            const string operation = "11111111111111111111111111111111";
            route = Store.BeginThreadBinding(operation, route.Revision, Mail.DispatchId, Mail.Revision);
            _ = Store.CompleteThreadBinding(operation, "thread-a", route.Revision);
        }
        internal GalateaDurableDelegationDriver Driver(Transport transport) => new(Store, transport, Root);
        internal void Reopen() {
            Store.Dispose();
            Store = GalateaDelegationSqliteStore.OpenExisting(Path.Combine(Root, "store"), _owner, _limits);
        }
        internal long Scalar(string sql) {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "store", GalateaDelegationSqliteStore.DatabaseFileName)};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        internal void ExecuteSql(string sql, params (string Name, object Value)[] parameters) {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "store", GalateaDelegationSqliteStore.DatabaseFileName)};Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object value) in parameters) { command.Parameters.AddWithValue(name, value); }
            _ = command.ExecuteNonQuery();
        }
        internal void DowngradeFixtureToV4() {
            Store.Dispose();
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "store", GalateaDelegationSqliteStore.DatabaseFileName)};Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name='delegation_meta';";
            string metaSql = ((string)command.ExecuteScalar()!).Replace("schema_version = 5", "schema_version = 4", StringComparison.Ordinal);
            command.CommandText = "ALTER TABLE delegation_meta RENAME TO prior_meta;" + metaSql + ";" + """
                INSERT INTO delegation_meta SELECT singleton,4,user_id,session_repository_id,
                    capture_frontier_segment_number,capture_frontier_tail_offset,baseline_selected_head,
                    maximum_queued_mails,maximum_task_utf8_bytes,maximum_reply_utf8_bytes,maximum_inbox_replies,
                    maximum_inbox_utf8_bytes,next_completion_sequence,revision FROM prior_meta;
                DROP TABLE prior_meta;
                ALTER TABLE outbound_mail DROP COLUMN content_format;
                ALTER TABLE outbound_mail DROP COLUMN sender_name;
                ALTER TABLE outbound_mail DROP COLUMN task_sha256;
                ALTER TABLE outbound_mail DROP COLUMN task_utf8_bytes;
                ALTER TABLE internal_mail_outbox DROP COLUMN bound_input;
                ALTER TABLE reply_lease DROP COLUMN bound_input;
                ALTER TABLE reply_notice DROP COLUMN notice_format;
                ALTER TABLE reply_notice DROP COLUMN sender_kind;
                ALTER TABLE reply_notice DROP COLUMN sender_id;
                ALTER TABLE reply_notice DROP COLUMN sender_name;
                ALTER TABLE reply_notice DROP COLUMN detail;
                ALTER TABLE reply_notice DROP COLUMN thread_id;
                ALTER TABLE reply_notice DROP COLUMN turn_id;
                PRAGMA user_version=4;
                """;
            _ = command.ExecuteNonQuery();
        }
        internal GalateaDelegationStoreUpgradeResult UpgradeFixture() {
            GalateaDelegationStoreUpgradeResult result = GalateaDelegationSqliteStore.UpgradeExisting(Path.Combine(Root, "store"), _owner, _limits, true);
            Reopen();
            return result;
        }
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class Transport : IGalateaDurableDelegateTransport {
        internal int StartCount { get; private set; }
        internal string? StartTask { get; private set; }
        internal Action<GalateaStartDelegateTurnRequest>? Starting { get; set; }
        internal GalateaInspectDelegateDispatchRequest? LastInspection { get; private set; }
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(GalateaEnsureDelegateBindingRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Fixture binding is already established.");
        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(GalateaStartDelegateTurnRequest request, CancellationToken ct) {
            StartCount++;
            StartTask = request.Task;
            Starting?.Invoke(request);
            return Task.FromResult(new GalateaDelegateTurnAccepted(request.DispatchId, request.ThreadId, "turn-a"));
        }
        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(GalateaInspectDelegateDispatchRequest request, CancellationToken ct) {
            LastInspection = request;
            return Task.FromResult<GalateaDelegateDispatchInspection>(new GalateaDelegateDispatchInspection.Running(request.DispatchId, request.ThreadId,
                request.ExpectedTurnId ?? "turn-a", GalateaDelegateInspectionSource.Live));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
