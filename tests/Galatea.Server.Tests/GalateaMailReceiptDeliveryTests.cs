using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaMailReceiptDeliveryTests {
    [Theory]
    [InlineData("pending")]
    [InlineData("bound")]
    [InlineData("delivered")]
    public void V6Upgrade_PreservesReceiptAndExactObservationWithoutBackfillingSmtp(string state) {
        using var fixture = new Fixture();
        _ = fixture.Store.CaptureActionBatch(Capture(100, [Mail("Codex", new string('a', 80))]));
        ActionReceiptDeliverySnapshot receipt = fixture.Store.ReadPendingReceiptDelivery()!;
        if (state != "pending") {
            receipt = fixture.Store.BindReceiptDelivery(receipt.SourceActionAddress, receipt.StateRevision,
                Address(90), Observation(receipt.FrozenBatch!));
        }
        if (state == "delivered") {
            receipt = fixture.Store.CompleteReceiptDelivery(receipt.SourceActionAddress, receipt.StateRevision, Address(91));
        }
        GalateaDelegationStateSnapshot before = fixture.Store.ReadSnapshot();
        fixture.Store.Dispose();
        // Mainline V6 has frozen receipts and no SMTP table. Preserve every
        // receipt byte and state while removing only the SMTP schema addition.
        fixture.Execute("""
            DROP TABLE smtp_mail_outbox;
            PRAGMA writable_schema = ON;
            UPDATE sqlite_schema SET sql=replace(sql, 'schema_version = 7', 'schema_version = 6')
                WHERE name='delegation_meta';
            PRAGMA writable_schema = OFF;
            PRAGMA schema_version = 402;
            PRAGMA ignore_check_constraints = ON;
            UPDATE delegation_meta SET schema_version=6;
            PRAGMA ignore_check_constraints = OFF;
            PRAGMA user_version=6;
            """, string.Empty);
        byte[] original = File.ReadAllBytes(fixture.DatabasePath);
        Assert.Throws<InvalidDataException>(fixture.Reopen);
        Assert.Equal("DryRunReady", fixture.Upgrade(apply: false).Outcome);
        Assert.Equal(original, File.ReadAllBytes(fixture.DatabasePath));
        GalateaDelegationStoreUpgradeResult upgraded = fixture.Upgrade(apply: true);
        Assert.Equal("Upgraded", upgraded.Outcome);
        Assert.NotNull(upgraded.BackupPath);
        fixture.Reopen();
        GalateaDelegationStateSnapshot after = fixture.Store.ReadSnapshot();
        Assert.Equal(before.Captures, after.Captures);
        Assert.Equal(before.Mails, after.Mails);
        Assert.Equal(before.StoreRevision, after.StoreRevision);
        Assert.Equal(receipt, Assert.Single(after.MailReceipts));
        Assert.Empty(after.SmtpMailOutboxes);
    }

    [Fact]
    public void Capture_FreezesOrderedRealRouteResultsAndShortPreviews() {
        using var fixture = new Fixture();
        string body = new string('a', 32) + "unique-middle-must-not-be-in-receipt" + new string('z', 16);
        GalateaDelegationCaptureRequest request = Capture(100, [
            Mail("Codex", body), Mail("peer", "peer-body"), Mail("unknown", "unrouted-body")
        ]) with { InternalTargets = [null, new("peer-id", "peer-repository", "sender"), null] };
        GalateaDelegationCaptureResult result = fixture.Store.CaptureActionBatch(request);
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        MailReceiptBatch batch = Assert.IsType<MailReceiptBatch>(pending.FrozenBatch);

        Assert.Equal(result.StoreRevision, pending.CreatedRevision);
        Assert.Equal(result.DispatchIds, batch.Items.Select(static item => item.DispatchId).ToArray());
        Assert.Equal(["accepted", "accepted", "unrouted"], batch.Items.Select(static item => item.Outcome).ToArray());
        Assert.Equal(ActionReceiptPreview.Create(body), batch.Items[0].Preview);
        Assert.DoesNotContain("unique-middle", ActionReceiptBatchCodec.SerializeFrozen(batch), StringComparison.Ordinal);
        GalateaOutboundMailSnapshot internalArtifact = fixture.Store.ReadSnapshot().Mails[1];
        Assert.Equal(GalateaDurableMailState.Unrouted, internalArtifact.State);
        Assert.Single(fixture.Store.ReadSnapshot().InternalMailOutboxes);
        Assert.Equal(body, fixture.Store.ReadSnapshot().Mails[0].Body);
        fixture.Reopen();
        Assert.Equal(pending, fixture.Store.ReadPendingReceiptDelivery());
    }

    [Fact]
    public void ZeroAndAlreadyCaptured_NeverCreateAnotherReceiptOrRetrofitRoutes() {
        using var fixture = new Fixture();
        _ = fixture.Store.CaptureActionBatch(Capture(100, []));
        Assert.Null(fixture.Store.ReadPendingReceiptDelivery());
        GalateaDelegationCaptureRequest request = Capture(101, [Mail("peer", "body")]);
        _ = fixture.Store.CaptureActionBatch(request);
        ActionReceiptDeliverySnapshot original = fixture.Store.ReadPendingReceiptDelivery()!;
        GalateaDelegationCaptureResult duplicate = fixture.Store.CaptureActionBatch(request with {
            InternalTargets = [new("peer-id", "peer-repository", "sender")]
        });
        Assert.Equal(GalateaDelegationCaptureDisposition.AlreadyCaptured, duplicate.Disposition);
        Assert.Equal(original, fixture.Store.ReadPendingReceiptDelivery());
        Assert.Empty(fixture.Store.ReadSnapshot().InternalMailOutboxes);
        Assert.Equal("unrouted", Assert.Single(Assert.IsType<MailReceiptBatch>(original.FrozenBatch).Items).Outcome);
        Assert.Single(fixture.Store.ReadSnapshot().MailReceipts);
    }

    [Fact]
    public void TerminalBodyCleanup_PreservesFrozenReceiptWithoutHydration() {
        using var fixture = new Fixture();
        string body = new string('a', 80) + "end";
        _ = fixture.Store.CaptureActionBatch(Capture(100, [Mail("Codex", body)]));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        GalateaDelegationStateSnapshot captured = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = Assert.Single(captured.Mails);
        _ = fixture.Store.FinishMailLocally(mail.DispatchId, mail.Revision, captured.Route.Revision, "LOCAL_FAILURE", resetBinding: false);
        Assert.Null(Assert.Single(fixture.Store.ReadSnapshot().Mails).Body);
        fixture.Reopen();
        Assert.Equal(pending, fixture.Store.ReadPendingReceiptDelivery());
        SessionInputContent input = Observation(pending.FrozenBatch!);
        ActionReceiptDeliverySnapshot bound = fixture.Store.BindReceiptDelivery(pending.SourceActionAddress,
            pending.StateRevision, Address(90), input);
        fixture.Reopen();
        Assert.Equal(input, fixture.Store.ReadBoundReceiptDelivery()!.BoundInput);
        Assert.Equal(bound.FrozenBatch, fixture.Store.ReadBoundReceiptDelivery()!.FrozenBatch);
    }

    [Fact]
    public void BindRollbackComplete_AreFifoRevisionFencedAndReleaseOnlyDeliveredPayload() {
        using var fixture = new Fixture();
        _ = fixture.Store.CaptureActionBatch(Capture(100, [Mail("Codex", "first")]));
        _ = fixture.Store.CaptureActionBatch(Capture(101, [Mail("Codex", "second")]));
        ActionReceiptDeliverySnapshot first = fixture.Store.ReadPendingReceiptDelivery()!;
        ActionReceiptDeliverySnapshot second = fixture.Store.ReadReceiptDeliveryExact(Address(101))!;
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.BindReceiptDelivery(
            second.SourceActionAddress, second.StateRevision, Address(90), Observation(second.FrozenBatch!)));
        SessionInputContent input = Observation(first.FrozenBatch!.Compact());
        ActionReceiptDeliverySnapshot bound = fixture.Store.BindReceiptDelivery(
            first.SourceActionAddress, first.StateRevision, Address(90), input);
        Assert.Equal(first.FrozenBatch, bound.FrozenBatch);
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.RollbackReceiptDelivery(
            bound.SourceActionAddress, first.StateRevision));
        Assert.Throws<GalateaDelegationStoreConflictException>(() => fixture.Store.BindReceiptDelivery(
            second.SourceActionAddress, second.StateRevision, Address(90), Observation(second.FrozenBatch!)));
        ActionReceiptDeliverySnapshot rolled = fixture.Store.RollbackReceiptDelivery(bound.SourceActionAddress, bound.StateRevision);
        Assert.Equal(first.FrozenBatch, rolled.FrozenBatch);
        Assert.Null(rolled.BoundInput);
        bound = fixture.Store.BindReceiptDelivery(rolled.SourceActionAddress, rolled.StateRevision, Address(90), input);
        fixture.Reopen();
        Assert.Equal(bound, fixture.Store.ReadBoundReceiptDelivery());
        ActionReceiptDeliverySnapshot delivered = fixture.Store.CompleteReceiptDelivery(bound.SourceActionAddress,
            bound.StateRevision, Address(91));
        Assert.Equal(ActionReceiptDeliveryState.Delivered, delivered.State);
        Assert.Null(delivered.FrozenBatch);
        Assert.Null(delivered.BoundInput);
        Assert.Equal(Address(90), delivered.ExpectedSessionHead);
        Assert.Equal(Address(91), delivered.ObservationAddress);
        Assert.Equal(second, fixture.Store.ReadPendingReceiptDelivery());
        fixture.Reopen();
        Assert.Equal(delivered, fixture.Store.ReadReceiptDeliveryExact(first.SourceActionAddress));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("source")]
    [InlineData("outcome")]
    [InlineData("preview")]
    [InlineData("recipient")]
    [InlineData("order")]
    [InlineData("partial")]
    public void BindRejectsUnrelatedOrAlteredBatch(string variant) {
        using var fixture = new Fixture();
        _ = fixture.Store.CaptureActionBatch(Capture(100, [Mail("Codex", "one"), Mail("Codex", "two")]));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        MailReceiptBatch batch = Assert.IsType<MailReceiptBatch>(pending.FrozenBatch);
        ActionReceiptBatch changed = variant switch {
            "source" => new MailReceiptBatch(Address(99), batch.Items),
            "outcome" => new MailReceiptBatch(batch.SourceActionAddress, [batch.Items[0] with { Outcome = "unrouted" }, batch.Items[1]]),
            "preview" => new MailReceiptBatch(batch.SourceActionAddress, [batch.Items[0] with { Preview = "changed" }, batch.Items[1]]),
            "recipient" => new MailReceiptBatch(batch.SourceActionAddress, [batch.Items[0] with { RecipientPreview = "peer" }, batch.Items[1]]),
            "order" => new MailReceiptBatch(batch.SourceActionAddress, batch.Items.Reverse().ToArray()),
            "partial" => new MailReceiptBatch(batch.SourceActionAddress, [batch.Items[0]]),
            _ => batch
        };
        SessionInputContent input = variant == "missing" ? Observation(null) : Observation(changed);
        Assert.Throws<InvalidDataException>(() => fixture.Store.BindReceiptDelivery(pending.SourceActionAddress,
            pending.StateRevision, Address(90), input));
        Assert.Equal(pending, fixture.Store.ReadPendingReceiptDelivery());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureCommitBoundary_CannotLoseOrFabricateFrozenReceipt(bool afterCommit) {
        int fired = 0;
        void Fail(string operation) {
            if (operation == "capture-action-batch" && Interlocked.Exchange(ref fired, 1) == 0) {
                throw new IOException("injected capture boundary");
            }
        }
        var hooks = afterCommit
            ? new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: Fail)
            : new GalateaDelegationStoreTestHooks(BeforeCommit: Fail);
        using var fixture = new Fixture(hooks);
        GalateaDelegationCaptureRequest request = Capture(100, [Mail("Codex", "body")]);
        if (afterCommit) { _ = fixture.Store.CaptureActionBatch(request); }
        else {
            Assert.Throws<IOException>(() => fixture.Store.CaptureActionBatch(request));
            Assert.Empty(fixture.Store.ReadSnapshot().Captures);
            Assert.Null(fixture.Store.ReadPendingReceiptDelivery());
            _ = fixture.Store.CaptureActionBatch(request);
        }
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        fixture.Reopen();
        Assert.Equal(pending, fixture.Store.ReadPendingReceiptDelivery());
        Assert.Single(fixture.Store.ReadSnapshot().MailReceipts);
    }

    [Fact]
    public void UnknownCaptureCommit_RequiresFrozenPayloadAndExactConfirmedArtifacts() {
        Fixture? fixture = null;
        var hooks = new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: operation => {
            if (operation != "capture-action-batch") { return; }
            ActionReceiptDeliverySnapshot row = fixture!.Store.ReadPendingReceiptDelivery()!;
            MailReceiptBatch batch = Assert.IsType<MailReceiptBatch>(row.FrozenBatch);
            var changed = new MailReceiptBatch(batch.SourceActionAddress, [batch.Items[0] with { Preview = "changed" }]);
            fixture.Execute("UPDATE outbound_mail SET body = 'changed'; UPDATE mail_receipt_delivery SET receipt_content = $content;",
                ActionReceiptBatchCodec.SerializeFrozen(changed));
            throw new IOException("uncertain commit with unrelated legal post-state");
        });
        using (fixture = new Fixture(hooks)) {
            Assert.Throws<GalateaDelegationCommitOutcomeException>(() => fixture.Store.CaptureActionBatch(
                Capture(100, [Mail("Codex", "original")])));
        }
    }

    [Fact]
    public void UnknownCaptureCommit_ExistingCaptureWithoutFrozenReceiptIsNotPublicationProof() {
        Fixture? fixture = null;
        var hooks = new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: operation => {
            if (operation != "capture-action-batch") { return; }
            fixture!.Execute("DELETE FROM mail_receipt_delivery;", Array.Empty<byte>());
            throw new IOException("uncertain commit without frozen receipt");
        });
        using (fixture = new Fixture(hooks)) {
            Assert.Throws<GalateaDelegationCommitOutcomeException>(() => fixture.Store.CaptureActionBatch(
                Capture(100, [Mail("Codex", "original")])));
            Assert.Single(fixture.Store.ReadSnapshot().Captures);
            Assert.Empty(fixture.Store.ReadSnapshot().MailReceipts);
        }
    }

    [Theory]
    [InlineData("bind", false)]
    [InlineData("bind", true)]
    [InlineData("rollback", false)]
    [InlineData("rollback", true)]
    [InlineData("complete", false)]
    [InlineData("complete", true)]
    public void ReceiptTransitionCommitBoundary_ReadsBackExactStateAndInput(string stage, bool afterCommit) {
        int attempts = 0;
        int targetAttempt = stage == "bind" ? 1 : 2;
        void Fail(string operation) {
            if (operation == "transition-mail-receipt-delivery"
                && Interlocked.Increment(ref attempts) == targetAttempt) {
                throw new IOException("injected receipt transition boundary");
            }
        }
        var hooks = afterCommit
            ? new GalateaDelegationStoreTestHooks(AfterCommitBeforeReturn: Fail)
            : new GalateaDelegationStoreTestHooks(BeforeCommit: Fail);
        using var fixture = new Fixture(hooks);
        _ = fixture.Store.CaptureActionBatch(Capture(100, [Mail("Codex", "original")]));
        ActionReceiptDeliverySnapshot before = fixture.Store.ReadPendingReceiptDelivery()!;
        SessionInputContent input = Observation(before.FrozenBatch!);
        if (stage != "bind") {
            before = fixture.Store.BindReceiptDelivery(before.SourceActionAddress, before.StateRevision, Address(90), input);
        }
        Func<ActionReceiptDeliverySnapshot> transition = stage switch {
            "bind" => () => fixture.Store.BindReceiptDelivery(before.SourceActionAddress, before.StateRevision, Address(90), input),
            "rollback" => () => fixture.Store.RollbackReceiptDelivery(before.SourceActionAddress, before.StateRevision),
            "complete" => () => fixture.Store.CompleteReceiptDelivery(before.SourceActionAddress, before.StateRevision, Address(91)),
            _ => throw new InvalidOperationException()
        };
        if (!afterCommit) {
            Assert.Throws<IOException>(transition);
            Assert.Equal(before, fixture.Store.ReadReceiptDeliveryExact(before.SourceActionAddress));
        }
        ActionReceiptDeliverySnapshot result = transition();
        Assert.Equal(result, fixture.Store.ReadReceiptDeliveryExact(before.SourceActionAddress));
        fixture.Reopen();
        Assert.Equal(result, fixture.Store.ReadReceiptDeliveryExact(before.SourceActionAddress));
        if (stage == "bind") { Assert.Equal(input, result.BoundInput); }
        if (stage == "rollback") { Assert.Equal(before.FrozenBatch, result.FrozenBatch); }
        if (stage == "complete") { Assert.Null(result.FrozenBatch); }
    }

    [Fact]
    public void BoundInputTampering_IsRejectedOnColdOpenEvenAfterBodyCleanup() {
        using var fixture = new Fixture();
        _ = fixture.Store.CaptureActionBatch(Capture(100, [Mail("Codex", "original")]));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        GalateaDelegationStateSnapshot captured = fixture.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = Assert.Single(captured.Mails);
        _ = fixture.Store.FinishMailLocally(mail.DispatchId, mail.Revision, captured.Route.Revision, "LOCAL_FAILURE", resetBinding: false);
        _ = fixture.Store.BindReceiptDelivery(pending.SourceActionAddress, pending.StateRevision, Address(90), Observation(pending.FrozenBatch!));
        MailReceiptBatch batch = Assert.IsType<MailReceiptBatch>(pending.FrozenBatch);
        var changed = new MailReceiptBatch(batch.SourceActionAddress, [batch.Items[0] with { Preview = "changed" }]);
        fixture.Execute("UPDATE mail_receipt_delivery SET bound_input = $content;", Observation(changed).ToUtf8Json());
        fixture.Store.Dispose();
        Assert.Throws<InvalidDataException>(() => fixture.Reopen());
    }

    private static SessionInputContent Observation(ActionReceiptBatch? batch) => GalateaObservationContent.Create(
        new GalateaFreshInput.PlayerAction("next", GalateaDelegateTestConfiguration.PlayerSender),
        new DateTimeOffset(2026, 10, 6, 1, 2, 3, TimeSpan.Zero), Sender,
        batch is null ? [] : [new PlayerTurnNotice.ActionReceipt(batch)]);
    private static readonly GalateaSenderSnapshot Sender = new("character", "user", "sender");
    private static string Address(int value) => $"ej1:{value:x16}0000000100000000";
    private static SendMailIntent Mail(string recipient, string body) => new(recipient, null, body, null, "sent");
    private static GalateaDelegationCaptureRequest Capture(int source, IReadOnlyList<SendMailIntent> intents) =>
        new(Address(source), new string('a', 64), 12, "receipt-tests-v1", intents, Sender);

    private sealed class Fixture : IDisposable {
        private static readonly GalateaDelegationStoreOwner Owner = new("user", "repository");
        private static readonly GalateaDelegationStoreLimits Limits = new(32, 100_000, 1024, 16, 16 * 1024);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "atelia-mail-receipt-" + Guid.NewGuid().ToString("N"));
        internal Fixture(GalateaDelegationStoreTestHooks? hooks = null) {
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(_directory);
            Store = GalateaDelegationSqliteStore.CreateNew(_directory, Owner,
                new(new EventJournalPhysicalAppendFrontier(1, 4), Address(90)), Limits, hooks);
        }
        internal GalateaDelegationSqliteStore Store { get; private set; }
        internal string DatabasePath => Path.Combine(_directory, GalateaDelegationSqliteStore.DatabaseFileName);
        internal GalateaDelegationStoreUpgradeResult Upgrade(bool apply) =>
            GalateaDelegationSqliteStore.UpgradeExisting(_directory, Owner, Limits, apply);
        internal void Reopen() {
            Store.Dispose();
            Store = GalateaDelegationSqliteStore.OpenExisting(_directory, Owner, Limits);
        }
        internal void Execute(string sql, string content) => Execute(sql, System.Text.Encoding.UTF8.GetBytes(content));
        internal void Execute(string sql, byte[] content) {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(_directory, GalateaDelegationSqliteStore.DatabaseFileName),
                Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$content", content);
            command.ExecuteNonQuery();
        }
        public void Dispose() {
            Store.Dispose();
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_directory);
        }
    }
}
