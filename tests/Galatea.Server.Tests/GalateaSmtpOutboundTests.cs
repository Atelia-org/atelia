using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.Galatea.Server.Mailbox;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

// Deterministic tool responses exercise the host, not the model's judgement.
public sealed class GalateaSmtpOutboundTests {
    [Theory]
    [InlineData("missing", "NO_SENDER_BINDING")]
    [InlineData("other-role", "NO_SENDER_BINDING")]
    [InlineData("global-disabled", "SMTP_DISABLED")]
    public async Task HostPolicyRejection_IsPersistedAtCaptureAndNeverRebound(string mode, string code) {
        var account = new GalateaEmailAccount("host@Example.test", "SECRET_MARKER", "not-contacted.example.invalid", 465, "implicit");
        var policy = mode switch {
            "missing" => new GalateaSmtpConfig(new(true), new Dictionary<string, GalateaEmailAccount>()),
            "other-role" => new(new(true), new Dictionary<string, GalateaEmailAccount> { ["bob"] = account }),
            _ => GalateaSmtpConfig.Disabled
        };
        using var fixture = new Fixture(smtpReference: policy.ReferenceFor("alice"));
        await fixture.CaptureEmail();
        var row = Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, row.State);
        Assert.Equal(code, row.ResultCode);
        Assert.Equal(GalateaSmtpConfig.BlockedReference("alice", code), row.SenderAccountReference);
        Assert.Single(fixture.Store.ReadSnapshot().Mails);
        var never = new ForbiddenSender();
        var consumer = new GalateaSmtpOutboxConsumer(never);
        Assert.False(await consumer.ConsumeOneAsync(fixture.Store, default));
        fixture.Reopen();
        var reconciler = new GalateaOutboundMailExtractionReconciler(fixture.Store, new NeverExtract(), Fixture.Sender,
            smtpSenderAccountReference: GalateaSmtpConfig.AccountReference("alice", account));
        Assert.IsType<GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured>(await reconciler.ReconcileAsync(fixture.Engine));
        var enabled = new GalateaSmtpOutboxConsumer(never);
        Assert.False(await enabled.ConsumeOneAsync(fixture.Store, default));
        Assert.Equal(row, Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes));
    }

    [Theory]
    [InlineData("SMTP_DISABLED")]
    [InlineData("NO_SENDER_BINDING")]
    [InlineData("SENDER_BINDING_DISABLED")]
    public async Task HistoricalBlockedReasons_RemainReadableAndCannotBeReplayed(string code) {
        using var fixture = new Fixture(smtpReference: GalateaSmtpConfig.BlockedReference("alice", code));
        await fixture.CaptureEmail();
        var before = Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, before.State);
        Assert.Equal(code, before.ResultCode);
        fixture.Reopen();
        Assert.Equal(before, Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes));
        Assert.False(await new GalateaSmtpOutboxConsumer(new ForbiddenSender()).ConsumeOneAsync(fixture.Store, default));
    }

    private sealed class ForbiddenSender : IGalateaSmtpSender {
        public Task<GalateaSmtpSendResult> SendAsync(GalateaSmtpSendRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Forbidden sender invoked by synthetic policy test.");
    }

    [Fact]
    public async Task RealCaptureBinding_IsFrozenAndNeverReboundByAlreadyCaptured() {
        using var fixture = new Fixture(smtpReference: "smtp:alice:account-v1");
        await fixture.CaptureEmail(); fixture.Reopen();
        Assert.Equal("smtp:alice:account-v1", Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes).SenderAccountReference);
        var reconciler = new GalateaOutboundMailExtractionReconciler(fixture.Store, new NeverExtract(), Fixture.Sender,
            smtpSenderAccountReference: "smtp:alice:account-v2");
        Assert.IsType<GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured>(await reconciler.ReconcileAsync(fixture.Engine));
        Assert.Equal("smtp:alice:account-v1", Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes).SenderAccountReference);
    }

    [Theory]
    [InlineData("accepted", (int)GalateaSmtpMailState.ProviderAccepted)]
    [InlineData("auth-rejected", (int)GalateaSmtpMailState.DefiniteFailure)]
    [InlineData("after-data-close", (int)GalateaSmtpMailState.OutcomeUnknown)]
    [InlineData("after-data-timeout", (int)GalateaSmtpMailState.OutcomeUnknown)]
    public async Task LoopbackConsumer_PersistsTerminalResultAndNeverResends(string mode, int state) {
        await using var transport = new GalateaNetworkSmtpTests.TransportFixture(mode);
        using var fixture = new Fixture(smtpReference: transport.Reference);
        await fixture.CaptureEmail();
        var consumer = new GalateaSmtpOutboxConsumer(new GalateaNetworkSmtpSender(transport.Config, transport.Server.Certificate));
        Assert.True(await consumer.ConsumeOneAsync(fixture.Store, default));
        Assert.Equal((GalateaSmtpMailState)state, Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes).State);
        Assert.False(await consumer.ConsumeOneAsync(fixture.Store, default));
        fixture.Reopen();
        Assert.False(await consumer.ConsumeOneAsync(fixture.Store, default));
        Assert.Equal(1, transport.Server.Connections);
        if (mode == "accepted") {
            string encoded = transport.Server.Message!.Split("\r\n\r\n", 2)[1];
            Assert.Equal("body", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
    }

    [Fact]
    public async Task OfflinePending_WithNewEnabledBindingNeverBecomesNetworkMail() {
        await using var transport = new GalateaNetworkSmtpTests.TransportFixture();
        using var fixture = new Fixture();
        await fixture.CaptureEmail();
        var consumer = new GalateaSmtpOutboxConsumer(new GalateaNetworkSmtpSender(transport.Config, transport.Server.Certificate));
        Assert.True(await consumer.ConsumeOneAsync(fixture.Store, default));
        var row = Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        Assert.Equal("offline:alice", row.SenderAccountReference); Assert.Equal("SMTP_OFFLINE_ISOLATED", row.ResultCode);
        Assert.Equal(0, transport.Server.Connections);
    }

    [Fact]
    public async Task GlobalDisabled_ClaimsOldPendingThenTerminatesWithoutConnecting() {
        await using var transport = new GalateaNetworkSmtpTests.TransportFixture();
        using var fixture = new Fixture(smtpReference: transport.Reference);
        await fixture.CaptureEmail();
        Assert.Equal(GalateaSmtpMailState.Pending, Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes).State);
        var disabled = GalateaNetworkSmtpTests.ConfigFor(transport.Account, enabled: false);
        var consumer = new GalateaSmtpOutboxConsumer(new GalateaNetworkSmtpSender(disabled, transport.Server.Certificate));
        Assert.True(await consumer.ConsumeOneAsync(fixture.Store, default));
        var before = Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, before.State);
        Assert.Equal("SMTP_BINDING_UNAVAILABLE", before.ResultCode);
        Assert.Equal(0, transport.Server.Connections);
        fixture.Reopen();
        Assert.Equal(before, Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes));
        Assert.False(await consumer.ConsumeOneAsync(fixture.Store, default));
    }

    [Fact]
    public async Task DefaultHost_RejectsEmailWithoutExplicitSendingMode() {
        var connection = new CompletionConnectionConfig("test", "openai-chat", "fixture", "openai-chat/strict", "http://offline.invalid/");
        var helper = connection with { Id = "helper" };
        var client = new ScriptClient(new([Range("a@Example.test", 2, 2, 3)]), new([]));
        await using var testHost = GalateaTestHost.Create(new FixedClientFactory(client), normalizer: null,
            connections: [connection, helper], connectionOptionIds: [connection.Id], outboundMailExtractorConnectionId: helper.Id);
        using var http = testHost.CreateClient();
        Assert.IsType<GalateaNetworkSmtpSender>(testHost.Factory.Services.GetRequiredService<IGalateaSmtpSender>());
        var host = testHost.Factory.Services.GetRequiredService<GalateaHostService>();
        var session = await host.GetSessionAsync("alice", CancellationToken.None);
        session.Engine.AppendObservation("offline fixture");
        session.Engine.AppendImportedAgentAction(new([new ActionBlock.Text("收件人：a@Example.test\nbody\n[Galatea] 我已寄出。")]),
            new CompletionDescriptor("fixture", "fixture", "fixture"));
        await session.OutboundMailExtractionReconciler!.ReconcileAsync(session.Engine);
        var store = session.DelegationHandle!.Store;
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (store.ReadSnapshot().SmtpMailOutboxes.Single().State == GalateaSmtpMailState.Pending) {
            Assert.True(DateTime.UtcNow < deadline, "Offline background consumer did not advance.");
            await Task.Delay(50);
        }
        // A claim may be visible before its result has committed.
        while (store.ReadSnapshot().SmtpMailOutboxes.Single().State == GalateaSmtpMailState.Attempting) {
            Assert.True(DateTime.UtcNow < deadline, "Offline background result did not commit.");
            await Task.Delay(50);
        }
        Assert.Equal("SMTP_DISABLED", store.ReadSnapshot().SmtpMailOutboxes.Single().ResultCode);
        await host.DisposeAsync(); // Direct disposal must drain the consumer before releasing stores.
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task EmailCandidate_MissingOrChangedSourceAddressIsRejected(int? recipientLine) {
        using var fixture = new Fixture();
        var call = new ActionBlock.ToolCall(new RawToolCall(OutboundMailExtractor.ToolName, "mismatch",
            JsonSerializer.Serialize(new { recipient = "a@example.test", recipientLine,
                bodyStartLine = 2, bodyEndLine = 2, evidenceStartLine = 3, evidenceEndLine = 3 })));
        await Assert.ThrowsAsync<TextExtractionException>(() => fixture.ExtractAndCapture(
            "收件人：a@Example.test\nbody\n[Galatea] 我已寄出。", call));
        Assert.Empty(fixture.Store.ReadSnapshot().Captures);
        Assert.Empty(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
    }

    [Fact]
    public async Task EmailCapture_PreservesSourceSliceCaseAndHostSender() {
        using var fixture = new Fixture();
        const string action = "收件人：RobirdLiu@Gmail.com\r\n[邮件正文开始]\r\n第一行  \r\n\r\n```csharp\r\nx();\r\n```\r\n[邮件正文结束]\r\n[Galatea] 我把信寄出了。";
        var result = await fixture.ExtractAndCapture(action, Range("RobirdLiu@Gmail.com", 3, 7, 9));
        Assert.IsType<GalateaOutboundMailExtractionReconcileResult.Captured>(result);
        var snapshot = fixture.Store.ReadSnapshot();
        var mail = Assert.Single(snapshot.Mails);
        var row = Assert.Single(snapshot.SmtpMailOutboxes);
        Assert.Equal(GalateaMailRecipientClass.Email, snapshot.RecipientClass(mail));
        Assert.Equal("RobirdLiu@Gmail.com", mail.Recipient);
        Assert.Equal(mail.Recipient, row.Recipient);
        Assert.Equal("第一行  \r\n\r\n```csharp\r\nx();\r\n```", mail.Body);
        Assert.Equal(mail.Body, row.Body);
        Assert.Equal(mail.SourceActionAddress, row.SourceActionAddress);
        Assert.Equal(snapshot.Captures.Single().CaptureSequence, row.CaptureSequence);
        Assert.Equal(0, row.ArtifactOrdinal);
        Assert.Equal("alice", row.FromCharacterId);
        Assert.Equal("offline:alice", row.SenderAccountReference);
        Assert.Equal(GalateaSmtpMailState.Pending, row.State);
        Assert.Empty(snapshot.InternalMailOutboxes);
    }

    [Fact]
    public async Task MixedAction_EmailCodexAndConfiguredPeerTakeSeparateRoutes() {
        using var fixture = new Fixture();
        const string action = "收件人：a@Example.test\nemail body\n[Galatea] 我已寄出这封信。\n收件人：Codex\ncodex body\n[Galatea] 我已寄出这封信。\n收件人：Bob\npeer body\n[Galatea] 我已寄出这封信。";
        await fixture.ExtractAndCapture(action, Range("a@Example.test", 2, 2, 3),
            Range("Codex", 5, 5, 6), Range("Bob", 8, 8, 9));
        var snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(3, snapshot.Captures.Single().ArtifactCount);
        Assert.Equal(new[] { GalateaMailRecipientClass.Email, GalateaMailRecipientClass.Codex,
            GalateaMailRecipientClass.Character }, snapshot.Mails.Select(snapshot.RecipientClass));
        Assert.Equal("email body", Assert.Single(snapshot.SmtpMailOutboxes).Body);
        Assert.Equal(GalateaDurableMailState.Queued, snapshot.Mails[1].State);
        Assert.Equal("bob", Assert.Single(snapshot.InternalMailOutboxes).TargetCharacterId);
        Assert.Equal(3, snapshot.Mails.Select(m => m.DispatchId).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotedIncomingMail_OnlyScriptedNewReplyCanBecomeAnArtifact(bool reply) {
        using var fixture = new Fixture();
        const string quote = "[旁白] 她展示来信原文，没有寄出。\n```text\n收件人：old@Example.test\n旧正文\n寄出了。\n```";
        string action = reply ? quote + "\n收件人：reply@Example.test\n新回信正文\n[Galatea] 我把新回信寄出了。" : quote;
        await fixture.ExtractAndCapture(action, reply ? [Range("reply@Example.test", 8, 8, 9, 7)] : []);
        var snapshot = fixture.Store.ReadSnapshot();
        Assert.Equal(reply ? 1 : 0, snapshot.Captures.Single().ArtifactCount);
        Assert.Equal(reply ? 1 : 0, snapshot.SmtpMailOutboxes.Count);
        if (reply) { Assert.Equal("新回信正文", snapshot.SmtpMailOutboxes.Single().Body); }
    }

    [Theory]
    [InlineData("Name <user@example.test>")]
    [InlineData("a@example.test,b@example.test")]
    [InlineData("a@example.test\nb@example.test")]
    [InlineData("ü@example.test")]
    [InlineData("codex")]
    public async Task InvalidRecipients_DoNotCreateSmtpOutboxes(string recipient) {
        Assert.Equal(GalateaMailRecipientClass.Unrouted, GalateaMailRecipientClassifier.Classify(recipient, null));
        using var fixture = new Fixture();
        if (recipient.Contains('\n')) {
            await Assert.ThrowsAsync<TextExtractionException>(() => fixture.ExtractAndCapture(
                "invalid recipient", Range(recipient, 1, 1, 1)));
        }
        else {
            await fixture.ExtractAndCapture("收件人：" + recipient + "\nbody\n[Galatea] 已寄出。", Range(recipient, 2, 2, 3));
        }
        Assert.Empty(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        Assert.All(fixture.Store.ReadSnapshot().Mails, m => Assert.Equal(GalateaDurableMailState.Unrouted, m.State));
    }

    [Fact]
    public void Classifier_ExactConfiguredPeerPrecedesAddressAndCodexIsCaseSensitive() {
        Assert.Equal(GalateaMailRecipientClass.Character, GalateaMailRecipientClassifier.Classify("peer@Example.test",
            new("bob", "repository", "Galatea")));
        Assert.Equal(GalateaMailRecipientClass.Codex, GalateaMailRecipientClassifier.Classify("Codex", null));
        Assert.Equal(GalateaMailRecipientClass.Unrouted, GalateaMailRecipientClassifier.Classify(" Codex ", null));
    }

    [Theory]
    [InlineData((int)GalateaOfflineSmtpBehavior.Accepted, (int)GalateaSmtpMailState.ProviderAccepted, "OFFLINE_ACCEPTED")]
    [InlineData((int)GalateaOfflineSmtpBehavior.DefiniteFailure, (int)GalateaSmtpMailState.DefiniteFailure, "OFFLINE_REJECTED")]
    [InlineData((int)GalateaOfflineSmtpBehavior.OutcomeUnknown, (int)GalateaSmtpMailState.OutcomeUnknown, "OFFLINE_UNKNOWN")]
    [InlineData((int)GalateaOfflineSmtpBehavior.Throw, (int)GalateaSmtpMailState.OutcomeUnknown, "SENDER_EXCEPTION")]
    public async Task OfflineSender_RecordsResultAndNeverConsumesTerminalRowAgain(
        int behavior, int expected, string code) {
        using var fixture = new Fixture();
        await fixture.CaptureEmail();
        var sender = new RecordingSender(new GalateaOfflineSmtpSender((GalateaOfflineSmtpBehavior)behavior));
        var consumer = new GalateaSmtpOutboxConsumer(sender);
        Assert.True(await consumer.ConsumeOneAsync(fixture.Store, CancellationToken.None));
        var row = Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        Assert.Equal((GalateaSmtpMailState)expected, row.State);
        Assert.Equal(code, row.ResultCode);
        Assert.False(await consumer.ConsumeOneAsync(fixture.Store, CancellationToken.None));
        fixture.Reopen();
        Assert.False(await consumer.ConsumeOneAsync(fixture.Store, CancellationToken.None));
        Assert.Single(sender.Requests);
        Assert.Equal(row, fixture.Store.ReadSnapshot().SmtpMailOutboxes.Single());
    }

    [Fact]
    public async Task Restart_RecoversAttemptingAsUnknownWithoutInvokingSender() {
        using var fixture = new Fixture();
        await fixture.CaptureEmail();
        var claimed = Assert.IsType<GalateaSmtpMailOutboxSnapshot>(fixture.Store.ClaimPendingSmtpMail());
        Assert.Equal(GalateaSmtpMailState.Attempting, claimed.State);
        fixture.Reopen();
        var row = Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        Assert.Equal(GalateaSmtpMailState.OutcomeUnknown, row.State);
        Assert.Equal("PROCESS_RESTART", row.ResultCode);
        var sender = new RecordingSender(new GalateaOfflineSmtpSender());
        Assert.False(await new GalateaSmtpOutboxConsumer(sender).ConsumeOneAsync(fixture.Store, CancellationToken.None));
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task UncertainClaimCommit_NeverCallsSender() {
        using var fixture = new Fixture(new(AfterCommitBeforeReturn: operation => {
            if (operation == "smtp-Attempting") { throw new IOException("injected commit uncertainty"); }
        }));
        await fixture.CaptureEmail();
        var sender = new RecordingSender(new GalateaOfflineSmtpSender(GalateaOfflineSmtpBehavior.Accepted));
        await Assert.ThrowsAsync<GalateaDelegationCommitOutcomeException>(() =>
            new GalateaSmtpOutboxConsumer(sender).ConsumeOneAsync(fixture.Store, CancellationToken.None));
        Assert.Empty(sender.Requests);
        Assert.Equal(GalateaSmtpMailState.Attempting, fixture.Store.ReadSnapshot().SmtpMailOutboxes.Single().State);
        fixture.Reopen();
        Assert.Equal(GalateaSmtpMailState.OutcomeUnknown, fixture.Store.ReadSnapshot().SmtpMailOutboxes.Single().State);
    }

    [Fact]
    public async Task CaptureRollback_PublishesNeitherMailNorSmtpRow() {
        using var fixture = new Fixture(new(BeforeCommit: operation => {
            if (operation == "capture-action-batch") { throw new IOException("injected rollback"); }
        }));
        await Assert.ThrowsAsync<IOException>(() => fixture.CaptureEmail());
        var snapshot = fixture.Store.ReadSnapshot();
        Assert.Empty(snapshot.Captures);
        Assert.Empty(snapshot.Mails);
        Assert.Empty(snapshot.SmtpMailOutboxes);
    }

    [Fact]
    public async Task RepeatedNewCapture_CreatesOnlyOneSmtpRow() {
        using var fixture = new Fixture();
        await fixture.CaptureEmail();
        var before = fixture.Store.ReadSnapshot();
        var reconciler = new GalateaOutboundMailExtractionReconciler(fixture.Store, new NeverExtract(), Fixture.Sender);
        Assert.IsType<GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured>(await reconciler.ReconcileAsync(fixture.Engine));
        var capture = Assert.Single(before.Captures);
        Assert.Equal(GalateaDelegationCaptureDisposition.AlreadyCaptured, fixture.Store.CaptureActionBatch(new(
            capture.SourceActionAddress, capture.VisibleActionSha256, capture.VisibleActionUtf8Bytes,
            capture.ExtractorContractId, [new("a@Example.test", null, "body", null, "sent")], Fixture.Sender)).Disposition);
        Assert.Equal(Assert.Single(before.SmtpMailOutboxes), Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes));
        Assert.Single(fixture.Store.ReadSnapshot().Captures);
        Assert.Single(fixture.Store.ReadSnapshot().Mails);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SmtpInsertFailure_RollsBackCaptureAndEveryOutbox(int failAtInsert) {
        int inserts = 0;
        using var fixture = new Fixture(new(BeforeSmtpOutboxInsert: () => {
            if (++inserts == failAtInsert) { throw new IOException("injected SMTP insert failure"); }
        }));
        const string action = "收件人：a@Example.test\nsynthetic first body\n[Galatea] 已寄出。\n收件人：b@Example.test\nsynthetic second body\n[Galatea] 已寄出。";
        await Assert.ThrowsAsync<IOException>(() => fixture.ExtractAndCapture(action,
            Range("a@Example.test", 2, 2, 3), Range("b@Example.test", 5, 5, 6, 4)));
        Assert.Equal(failAtInsert, inserts);
        fixture.Reopen(); // Check the committed database, including rollback of an earlier SMTP INSERT.
        var snapshot = fixture.Store.ReadSnapshot();
        Assert.Empty(snapshot.Captures);
        Assert.Empty(snapshot.Mails);
        Assert.Empty(snapshot.SmtpMailOutboxes);
        using var connection = fixture.OpenSql();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM action_capture) + (SELECT count(*) FROM outbound_mail) + (SELECT count(*) FROM smtp_mail_outbox);";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MixedRouteSmtpInsertFailure_RollsBackEveryTable(int failAtInsert) {
        const string action = "收件人：Bob\nsynthetic peer body\n[Galatea] 已寄出。\n收件人：Codex\nsynthetic Codex body\n[Galatea] 已寄出。\n收件人：a@Example.test\nsynthetic first SMTP body\n[Galatea] 已寄出。\n收件人：b@Example.test\nsynthetic second SMTP body\n[Galatea] 已寄出。";
        ActionBlock[] calls = [Range("Bob", 2, 2, 3), Range("Codex", 5, 5, 6, 4),
            Range("a@Example.test", 8, 8, 9, 7), Range("b@Example.test", 11, 11, 12, 10)];

        // A separate synthetic control proves that all four artifacts and routes exist without the fault.
        using (var control = new Fixture()) {
            await control.ExtractAndCapture(action, calls);
            var captured = control.Store.ReadSnapshot();
            Assert.Equal(4, Assert.Single(captured.Captures).ArtifactCount);
            Assert.Equal(new[] { GalateaMailRecipientClass.Character, GalateaMailRecipientClass.Codex,
                GalateaMailRecipientClass.Email, GalateaMailRecipientClass.Email }, captured.Mails.Select(captured.RecipientClass));
            Assert.Equal("bob", Assert.Single(captured.InternalMailOutboxes).TargetCharacterId);
            Assert.Equal(GalateaDurableMailState.Queued, captured.Mails.Single(m => m.Recipient == "Codex").State);
            Assert.Equal(2, captured.SmtpMailOutboxes.Count);
        }

        int inserts = 0;
        using var fixture = new Fixture(new(BeforeSmtpOutboxInsert: () => {
            if (++inserts == failAtInsert) { throw new IOException("injected mixed SMTP insert failure"); }
        }));
        string metaBefore;
        string routeBefore;
        using (var connection = fixture.OpenSql()) {
            metaBefore = ReadSingleRow(connection, "delegation_meta");
            routeBefore = ReadSingleRow(connection, "route_binding");
        }
        var failure = await Assert.ThrowsAsync<IOException>(() => fixture.ExtractAndCapture(action, calls));
        Assert.Equal("injected mixed SMTP insert failure", failure.Message);
        Assert.Equal(failAtInsert, inserts);
        // Reopen before querying independently: assertions cover durable rollback, including the earlier
        // internal outbox / Codex Queued row, and (in case 2) the first SMTP INSERT.
        fixture.Reopen();
        using var persisted = fixture.OpenSql();
        Assert.Equal(0L, CountRows(persisted, "action_capture"));
        Assert.Equal(0L, CountRows(persisted, "outbound_mail"));
        using (var queued = persisted.CreateCommand()) {
            queued.CommandText = "SELECT count(*) FROM outbound_mail WHERE recipient='Codex' AND state='Queued';";
            Assert.Equal(0L, queued.ExecuteScalar());
        }
        Assert.Equal(0L, CountRows(persisted, "internal_mail_outbox"));
        Assert.Equal(0L, CountRows(persisted, "smtp_mail_outbox"));
        // Capture increments delegation_meta.revision in the same transaction; preserve every column.
        Assert.Equal(1L, CountRows(persisted, "delegation_meta"));
        Assert.Equal(metaBefore, ReadSingleRow(persisted, "delegation_meta"));
        Assert.Equal(1L, CountRows(persisted, "route_binding"));
        Assert.Equal(routeBefore, ReadSingleRow(persisted, "route_binding"));
        Assert.Equal(0L, CountRows(persisted, "reply_notice"));
        Assert.Equal(0L, CountRows(persisted, "reply_lease"));
        Assert.Equal(0L, CountRows(persisted, "reply_lease_item"));

        static long CountRows(SqliteConnection connection, string table) {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table};"; // Test-owned, constant table names only.
            return (long)command.ExecuteScalar()!;
        }

        static string ReadSingleRow(SqliteConnection connection, string table) {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table};";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            object[] values = new object[reader.FieldCount];
            reader.GetValues(values);
            Assert.False(reader.Read());
            return JsonSerializer.Serialize(values);
        }
    }

    [Fact]
    public async Task SenderInvocation_SeesAttemptingCommittedThroughSeparateConnection() {
        using var fixture = new Fixture();
        await fixture.CaptureEmail();
        var sender = new CallbackSender(request => {
            using var connection = fixture.OpenSql();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM smtp_mail_outbox WHERE dispatch_id=$dispatch;";
            command.Parameters.AddWithValue("$dispatch", request.DispatchId);
            Assert.Equal("Attempting", command.ExecuteScalar());
        });
        Assert.True(await new GalateaSmtpOutboxConsumer(sender).ConsumeOneAsync(fixture.Store, CancellationToken.None));
        Assert.Equal(1, sender.Calls);
        Assert.Equal(GalateaSmtpMailState.ProviderAccepted, fixture.Store.ReadSnapshot().SmtpMailOutboxes.Single().State);
    }

    [Fact]
    public async Task InterruptedResultCommit_RestartMakesUnknownAndNeitherLoopNorRestartResends() {
        bool interruptedAfterSender = false;
        var sender = new CallbackSender(_ => { });
        using var fixture = new Fixture(new(BeforeCommit: operation => {
            if (operation == "smtp-ProviderAccepted") {
                Assert.Equal(1, sender.Calls);
                interruptedAfterSender = true;
                throw new IOException("interrupted after sender returned, before result commit");
            }
        }));
        await fixture.CaptureEmail();
        var consumer = new GalateaSmtpOutboxConsumer(sender);
        await Assert.ThrowsAsync<IOException>(() => consumer.ConsumeOneAsync(fixture.Store, CancellationToken.None));
        Assert.True(interruptedAfterSender);
        Assert.Equal(GalateaSmtpMailState.Attempting, fixture.Store.ReadSnapshot().SmtpMailOutboxes.Single().State);
        Assert.False(await consumer.ConsumeOneAsync(fixture.Store, CancellationToken.None));
        fixture.Reopen();
        var unknown = fixture.Store.ReadSnapshot().SmtpMailOutboxes.Single();
        Assert.Equal(GalateaSmtpMailState.OutcomeUnknown, unknown.State);
        Assert.Equal("PROCESS_RESTART", unknown.ResultCode);
        Assert.False(await consumer.ConsumeOneAsync(fixture.Store, CancellationToken.None));
        fixture.Reopen();
        Assert.Equal(unknown, fixture.Store.ReadSnapshot().SmtpMailOutboxes.Single());
        Assert.False(await consumer.ConsumeOneAsync(fixture.Store, CancellationToken.None));
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task EmailReceipt_FreezesShortCaptureAcceptanceAcrossLaterSmtpFailure() {
        using var fixture = new Fixture();
        string body = new string('a', 32) + "unique-middle-omitted-from-receipt" + new string('z', 16);
        await fixture.ExtractAndCapture($"收件人：a@Example.test\n{body}\n[Galatea] 我已寄出。",
            Range("a@Example.test", 2, 2, 3));
        ActionReceiptDeliverySnapshot receipt = fixture.Store.ReadPendingReceiptDelivery()!;
        MailReceiptItem item = Assert.Single(Assert.IsType<MailReceiptBatch>(receipt.FrozenBatch).Items);
        Assert.Equal("accepted", item.Outcome);
        Assert.Equal(ActionReceiptPreview.Create(body), item.Preview);
        Assert.DoesNotContain("unique-middle", item.Preview!);
        Assert.Equal(body, Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes).Body);
        var consumer = new GalateaSmtpOutboxConsumer(new GalateaOfflineSmtpSender(GalateaOfflineSmtpBehavior.DefiniteFailure));
        Assert.True(await consumer.ConsumeOneAsync(fixture.Store, default));
        fixture.Reopen();
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes).State);
        Assert.Equal(receipt, fixture.Store.ReadPendingReceiptDelivery());
    }

    [Fact]
    public async Task V5Migration_DoesNotBackfillAndExistingCaptureRemainsAlreadyCaptured() {
        using var fixture = new Fixture();
        await fixture.CaptureEmail();
        await fixture.CaptureEmail(); // Two distinct synthetic old captures; no runtime database is used.
        string captureBefore = JsonSerializer.Serialize(fixture.Store.ReadSnapshot().Captures);
        string mailsBefore = JsonSerializer.Serialize(fixture.Store.ReadSnapshot().Mails);
        fixture.Store.Dispose();
        // Make an exact V5 fixture: same business records, no SMTP table, V5 meta CHECK.
        using (var connection = fixture.OpenSql()) {
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT sql FROM sqlite_schema WHERE name='delegation_meta';";
            string meta = ((string)read.ExecuteScalar()!).Replace("CREATE TABLE delegation_meta", "CREATE TABLE old_meta")
                .Replace($"schema_version = {GalateaDelegationSqliteStore.SchemaVersion}", "schema_version = 5");
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE smtp_mail_outbox;DROP TABLE mail_receipt_delivery;" + meta
                + ";INSERT INTO old_meta SELECT singleton, 5, user_id, session_repository_id,"
                + "capture_frontier_segment_number,capture_frontier_tail_offset,baseline_selected_head,"
                + "maximum_queued_mails,maximum_task_utf8_bytes,maximum_reply_utf8_bytes,maximum_inbox_replies,"
                + "maximum_inbox_utf8_bytes,next_completion_sequence,revision FROM delegation_meta;"
                + "DROP TABLE delegation_meta;ALTER TABLE old_meta RENAME TO delegation_meta;PRAGMA user_version=5;";
            command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        Assert.Equal("DryRunReady", GalateaDelegationSqliteStore.UpgradeExisting(fixture.StorePath,
            Fixture.Owner, Fixture.Limits, apply: false).Outcome);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Equal("Upgraded", GalateaDelegationSqliteStore.UpgradeExisting(fixture.StorePath,
            Fixture.Owner, Fixture.Limits, apply: true).Outcome);
        fixture.Reopen();
        var snapshot = fixture.Store.ReadSnapshot();
        Assert.Empty(snapshot.SmtpMailOutboxes);
        Assert.Equal(captureBefore, JsonSerializer.Serialize(snapshot.Captures));
        Assert.Equal(mailsBefore, JsonSerializer.Serialize(snapshot.Mails));
        Assert.Equal(2, snapshot.Captures.Count);
        Assert.Equal(2, snapshot.Mails.Count);
        Assert.All(snapshot.Mails, mail => Assert.Equal(GalateaMailRecipientClass.Unrouted, snapshot.RecipientClass(mail)));
        var reconciler = new GalateaOutboundMailExtractionReconciler(fixture.Store, new NeverExtract(), Fixture.Sender);
        Assert.IsType<GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured>(await reconciler.ReconcileAsync(fixture.Engine));
        Assert.Empty(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        Assert.Equal(GalateaDelegationCaptureDisposition.AlreadyCaptured, fixture.Store.CaptureActionBatch(new(
            snapshot.Captures[0].SourceActionAddress, snapshot.Captures[0].VisibleActionSha256,
            snapshot.Captures[0].VisibleActionUtf8Bytes, snapshot.Captures[0].ExtractorContractId,
            [new("a@Example.test", null, "body", null, "sent")], Fixture.Sender)).Disposition);
        Assert.Empty(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
        // Only a distinct newly sent Action may create an outbox after upgrade.
        await fixture.CaptureEmail();
        Assert.Single(fixture.Store.ReadSnapshot().SmtpMailOutboxes);
    }

    private static ActionBlock Range(string recipient, int start, int end, int evidence, int recipientLine = 1) =>
        new ActionBlock.ToolCall(new RawToolCall(OutboundMailExtractor.ToolName, Guid.NewGuid().ToString("N"),
            JsonSerializer.Serialize(new { recipient, bodyStartLine = start, bodyEndLine = end,
                evidenceStartLine = evidence, evidenceEndLine = evidence, recipientLine })));

    private sealed class Fixture : IDisposable {
        internal static readonly GalateaDelegationStoreOwner Owner = new("alice", "fixture-repository");
        internal static readonly GalateaDelegationStoreLimits Limits = new(32, 100_000, 1024, 16, 16384);
        internal static readonly GalateaSenderSnapshot Sender = new("character", "alice", "Galatea");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "galatea-smtp-" + Guid.NewGuid().ToString("N"));
        internal string StorePath => Path.Combine(_root, "store");
        internal string DatabasePath => Path.Combine(StorePath, GalateaDelegationSqliteStore.DatabaseFileName);
        internal GalateaDelegationSqliteStore Store { get; private set; }
        internal SessionJournalEngine Engine { get; }
        private readonly string? _smtpReference;
        internal Fixture(GalateaDelegationStoreTestHooks? hooks = null, string? smtpReference = null) {
            _smtpReference = smtpReference ?? "offline:alice";
            Directory.CreateDirectory(_root);
            Engine = SessionJournalEngine.Create(Path.Combine(_root, "session"), new SessionCreateOptions("fixture", "system", "surface"));
            Store = GalateaDelegationSqliteStore.CreateNew(StorePath, Owner,
                new(Engine.ReadView.ReadPhysicalAppendFrontier(), EventAddressTextCodec.FormatNullable(Engine.ReadCurrentHead())), Limits, hooks);
        }
        internal async Task<GalateaOutboundMailExtractionReconcileResult> ExtractAndCapture(string action, params ActionBlock[] calls) {
            var client = new ScriptClient(new(calls), new([]));
            var extractor = new OutboundMailExtractor(new GalateaCharacterName("Galatea"),
                new CompletionConnectionConfig("fixture", "openai-chat", "fixture", "openai-chat/strict",
                    "http://offline.invalid/", ApiKey: null), () => client);
            Engine.AppendObservation("synthetic observation");
            Engine.AppendImportedAgentAction(new([new ActionBlock.Text(action)]), new CompletionDescriptor("fixture", "fixture", "fixture"));
            var reconciler = new GalateaOutboundMailExtractionReconciler(Store, extractor, Sender,
                intent => intent.Recipient == "Bob" ? new("bob", "bob-repository", "Galatea") : null,
                _smtpReference);
            return await reconciler.ReconcileAsync(Engine);
        }
        internal Task CaptureEmail() => ExtractAndCapture("收件人：a@Example.test\nbody\n[Galatea] 我已寄出。", Range("a@Example.test", 2, 2, 3));
        internal void Reopen() { Store.Dispose(); Store = GalateaDelegationSqliteStore.OpenExisting(StorePath, Owner, Limits); }
        internal SqliteConnection OpenSql() {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Dispose() { Store.Dispose(); Engine.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private sealed class ScriptClient(params ActionMessage[] messages) : ICompletionClient {
        private readonly Queue<ActionMessage> _messages = new(messages);
        public string Name => "offline-fixture";
        public string ApiSpecId => "fixture";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompletionResult(_messages.Dequeue(), CompletionDescriptor.From(this, request)));
    }

    private sealed class FixedClientFactory(ICompletionClient client) : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection) => client;
    }

    private sealed class RecordingSender(IGalateaSmtpSender inner) : IGalateaSmtpSender {
        internal List<GalateaSmtpSendRequest> Requests { get; } = [];
        public Task<GalateaSmtpSendResult> SendAsync(GalateaSmtpSendRequest request, CancellationToken cancellationToken) {
            Requests.Add(request); return inner.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CallbackSender(Action<GalateaSmtpSendRequest> onCall) : IGalateaSmtpSender {
        internal int Calls { get; private set; }
        public Task<GalateaSmtpSendResult> SendAsync(GalateaSmtpSendRequest request, CancellationToken cancellationToken) {
            Calls++;
            onCall(request);
            return Task.FromResult(new GalateaSmtpSendResult(GalateaSmtpMailState.ProviderAccepted, "OFFLINE_ACCEPTED"));
        }
    }

    private sealed class NeverExtract : IOutboundMailExtractor {
        public string ContractId => "not-used";
        public ValueTask<IReadOnlyList<SendMailIntent>> ExtractAsync(string visibleActionText,
            CancellationToken cancellationToken, TextExtractionSource? source = null) =>
            throw new InvalidOperationException("Already captured Action must not be extracted again.");
    }
}
