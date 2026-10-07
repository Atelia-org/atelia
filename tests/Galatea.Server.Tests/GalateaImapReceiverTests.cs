using System.Text;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaImapReceiverTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly string Reference = "imap:alice:" + new string('a', 64);

    [Fact]
    public async Task FirstPollBaselinesWithoutDownloadingHistory_ColdReopenContinuesNewMail() {
        using var fixture = new Fixture();
        var connection = new FakeConnection(41, 101);
        connection.Messages[100] = Plain("friend@example.test", "old history");
        var baseline = await fixture.Receive(connection, ["friend@example.test"]);
        Assert.Equal("IMAP_BASELINE_ESTABLISHED", baseline.Code);
        Assert.Empty(connection.Searches);
        Assert.Empty(connection.Reads);
        Assert.Equal(100u, fixture.Checkpoint.ScannedThroughUid);
        fixture.Reopen();
        connection.UidNext = 102;
        connection.Messages[101] = Plain("friend@example.test", "new mail");
        var result = await fixture.Receive(connection, ["friend@example.test"]);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(new uint[] { 101 }, connection.Reads);
        Assert.Equal(1, fixture.Store.ReadImapInboxStatus().PendingCount);
        var row = Assert.IsType<GalateaExternalMailInboxSnapshot>(fixture.Store.ReadPendingExternalMail());
        Assert.Equal("new mail", row.Body);
        fixture.Reopen();
        Assert.Equal(0, (await fixture.Receive(connection, ["friend@example.test"])).ImportedCount);
        Assert.Equal(1, fixture.Store.ReadImapInboxStatus().PendingCount);
    }

    [Fact]
    public async Task UnknownSenderInvalidBody_IsNeverDecodedOrPersisted_OnlyCursorMoves() {
        using var fixture = new Fixture();
        fixture.Baseline();
        var connection = new FakeConnection(41, 3);
        connection.Messages[1] = GalateaImapMimeTests.Mail("stranger@example.test",
            "Subject: UNKNOWN_HEADER_SENTINEL\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64", "/w==UNKNOWN_BODY_SENTINEL");
        connection.Messages[2] = Plain("friend@example.test", "allowed");
        var result = await fixture.Receive(connection, ["friend@example.test"]);
        Assert.Equal(1, result.FilteredCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(2u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
        Assert.DoesNotContain("UNKNOWN_", Encoding.UTF8.GetString(File.ReadAllBytes(fixture.DatabasePath)));
        fixture.Reopen();
        Assert.Equal(0, (await fixture.Receive(connection, ["stranger@example.test", "friend@example.test"])).ImportedCount);
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
    }

    [Fact]
    public async Task AcceptedOutboundFact_AppliesOnlyToFutureUnprocessedUidsAcrossReopen() {
        using var fixture = new Fixture();
        fixture.Baseline();
        var connection = new FakeConnection(41, 2);
        connection.Messages[1] = Plain("friend@example.test", "before choice");
        Assert.Equal(1, (await fixture.Receive(connection)).FilteredCount);
        fixture.Store.CaptureActionBatch(new("ej1:00000000000000640000000100000000", new string('b', 64), 12,
            "imap-receiver-tests-v1", [new("friend@example.test", null, "outgoing", null, "sent")],
            new GalateaSenderSnapshot("character", "alice", "Galatea"), SmtpSenderAccountReference: "smtp:alice:old-binding"));
        fixture.Reopen();
        connection.Messages[2] = Plain("friend@example.test", "after choice");
        connection.UidNext = 3;
        Assert.Equal(1, (await fixture.Receive(connection)).ImportedCount);
        Assert.Equal("after choice", Assert.IsType<GalateaExternalMailInboxSnapshot>(fixture.Store.ReadPendingExternalMail()).Body);
    }

    [Fact]
    public async Task BadEncodedSubjectAndHtmlOnly_AreRejectedThenLaterUidIsImported() {
        using var fixture = new Fixture();
        fixture.Baseline();
        var connection = new FakeConnection(41, 4);
        connection.Messages[1] = GalateaImapMimeTests.Mail("friend@example.test",
            "Subject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("line\nsecond")) + "?=\r\nContent-Type: text/plain", "body");
        connection.Messages[2] = GalateaImapMimeTests.Mail("friend@example.test", "Content-Type: text/html", "<p>html only</p>");
        connection.Messages[3] = Plain("friend@example.test", "valid later mail");
        var result = await fixture.Receive(connection, ["friend@example.test"]);
        Assert.Equal(2, result.RejectedCount);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(3u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox WHERE state = 'Rejected' AND body IS NULL;"));
        Assert.Equal("valid later mail", Assert.IsType<GalateaExternalMailInboxSnapshot>(fixture.Store.ReadPendingExternalMail()).Body);
    }

    [Fact]
    public async Task DuplicateRfcMessageIds_AreIndependentUidsWithStableHostIds() {
        using var fixture = new Fixture();
        fixture.Baseline();
        var connection = new FakeConnection(41, 3);
        const string headers = "Message-Id: <same@sender.example.test>\r\nContent-Type: text/plain";
        connection.Messages[1] = GalateaImapMimeTests.Mail("friend@example.test", headers, "first");
        connection.Messages[2] = GalateaImapMimeTests.Mail("friend@example.test", headers, "second");
        Assert.Equal(2, (await fixture.Receive(connection, ["friend@example.test"])).ImportedCount);
        string[] before = fixture.PendingMessageIds();
        Assert.Equal(2, before.Length);
        Assert.NotEqual(before[0], before[1]);
        fixture.Reopen();
        Assert.Equal(0, (await fixture.Receive(connection, ["friend@example.test"])).ImportedCount);
        Assert.Equal(before, fixture.PendingMessageIds());
    }

    [Fact]
    public async Task BoundedUidWindowAndPerPollLimit_DoNotSkipNextMail() {
        using var fixture = new Fixture();
        fixture.Baseline(100);
        var connection = new FakeConnection(41, 2000);
        for (uint uid = 101; uid <= 120; uid++) { connection.Messages[uid] = Plain("friend@example.test", "body " + uid); }
        var first = await fixture.Receive(connection, ["friend@example.test"]);
        Assert.Equal("IMAP_POLL_LIMIT", first.Code);
        Assert.Equal(16, first.ImportedCount);
        Assert.Equal((101u, 356u), Assert.Single(connection.Searches));
        Assert.Equal(116u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(16, connection.Reads.Count);
        var second = await fixture.Receive(connection, ["friend@example.test"]);
        Assert.Equal(4, second.ImportedCount);
        Assert.Equal(372u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(20, fixture.Store.ReadImapInboxStatus().PendingCount);
    }

    [Fact]
    public async Task EmptySearchWindow_AdvancesOnlyConfirmedNumericGap() {
        using var fixture = new Fixture();
        fixture.Baseline(100);
        var connection = new FakeConnection(41, 1000);
        Assert.Equal("IMAP_READY", (await fixture.Receive(connection)).Code);
        Assert.Equal(356u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Empty(connection.Reads);
        Assert.Equal((101u, 356u), Assert.Single(connection.Searches));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingFetch_RequiresExactUidAbsenceBeforeAdvancing(bool stillPresent) {
        using var fixture = new Fixture();
        fixture.Baseline();
        var connection = new FakeConnection(41, 2) { MissingRead = true, StillPresentAfterMissingRead = stillPresent };
        connection.Messages[1] = Plain("friend@example.test", "body");
        var result = await fixture.Receive(connection, ["friend@example.test"]);
        Assert.Equal(new[] { (1u, 1u), (1u, 1u) }, connection.Searches);
        Assert.Equal(stillPresent ? 0u : 1u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(stillPresent ? "IMAP_FETCH_DEFERRED" : "IMAP_READY", result.Code);
        Assert.Equal(stillPresent ? 0L : 1L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox WHERE code = 'IMAP_MESSAGE_VANISHED';"));
    }

    [Fact]
    public async Task FetchFailure_IsNotTreatedAsVanishedAndDoesNotAdvanceUid() {
        using var fixture = new Fixture();
        fixture.Baseline();
        var connection = new FakeConnection(41, 2) { FailRead = true };
        connection.Messages[1] = Plain("friend@example.test", "body");
        await Assert.ThrowsAsync<GalateaImapReadException>(() => fixture.Receive(connection, ["friend@example.test"]));
        Assert.Equal(0u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
    }

    [Fact]
    public async Task AcceptedMailSignalsRelay_EvenWhenNextUidFetchFails() {
        using var fixture = new Fixture();
        fixture.Baseline();
        var connection = new FakeConnection(41, 3) { FailReadUid = 2 };
        connection.Messages[1] = Plain("friend@example.test", "committed before failure");
        connection.Messages[2] = Plain("friend@example.test", "deferred on fetch");
        int signals = 0;
        await Assert.ThrowsAsync<GalateaImapReadException>(() => GalateaImapReceiver.ReceiveAsync(
            fixture.Store, Reference, "Galatea", ["friend@example.test"], connection, Now, default, () => signals++));
        Assert.Equal(1, signals);
        Assert.Equal(1u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(1, fixture.Store.ReadImapInboxStatus().PendingCount);
    }

    [Theory]
    [InlineData(42u, 11u, "IMAP_UIDVALIDITY_CHANGED")]
    [InlineData(41u, 10u, "IMAP_UIDNEXT_REGRESSED")]
    public async Task NamespaceChanges_BlockWithoutResettingHistory(uint validity, uint uidNext, string code) {
        using var fixture = new Fixture();
        fixture.Baseline(10);
        var connection = new FakeConnection(validity, uidNext);
        Assert.Equal(code, (await fixture.Receive(connection)).Code);
        Assert.Equal(code, fixture.Checkpoint.BlockedCode);
        Assert.Equal(10u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(41u, fixture.Checkpoint.UidValidity);
        Assert.Empty(connection.Searches);
        fixture.Reopen();
        Assert.Equal(code, (await fixture.Receive(new FakeConnection(41, 20))).Code);
    }

    [Fact]
    public async Task NearMaximumUintUid_UsesCheckedWideWindowMath() {
        using var fixture = new Fixture();
        fixture.Baseline(uint.MaxValue - 10);
        var connection = new FakeConnection(41, uint.MaxValue);
        await fixture.Receive(connection);
        Assert.Equal((uint.MaxValue - 9, uint.MaxValue - 1), Assert.Single(connection.Searches));
        Assert.Equal(uint.MaxValue - 1, fixture.Checkpoint.ScannedThroughUid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UidNextRegressesDuringPollEvenAboveCursor_BlocksBeforeAdvancing(bool afterRead) {
        using var fixture = new Fixture();
        fixture.Baseline(10);
        var connection = new FakeConnection(41, 101) {
            UidNextAfterSearch = afterRead ? null : 51,
            UidNextAfterRead = afterRead ? 51u : null
        };
        if (afterRead) { connection.Messages[11] = Plain("friend@example.test", "not admitted on inconsistent namespace"); }
        Assert.Equal("IMAP_UIDNEXT_REGRESSED", (await fixture.Receive(connection, ["friend@example.test"])).Code);
        Assert.Equal("IMAP_UIDNEXT_REGRESSED", fixture.Checkpoint.BlockedCode);
        Assert.Equal(10u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM external_mail_inbox;"));
    }

    [Fact]
    public async Task QueueFull_StopsAtFirstAllowedUidWithoutSkippingLaterUnknownMail() {
        using var fixture = new Fixture();
        fixture.Baseline();
        for (uint uid = 1; uid <= 128; uid++) {
            Assert.NotNull(fixture.Store.AcceptImapMail(fixture.Checkpoint, uid, "Galatea", "friend@example.test", null, "queued", 0));
        }
        var connection = new FakeConnection(41, 131);
        connection.Messages[129] = Plain("friend@example.test", "must wait");
        connection.Messages[130] = Plain("unknown@example.test", "must not skip");
        var result = await fixture.Receive(connection, ["friend@example.test"]);
        Assert.Equal("IMAP_BACKPRESSURE", result.Code);
        Assert.Equal(128u, fixture.Checkpoint.ScannedThroughUid);
        Assert.Equal(new uint[] { 129 }, connection.Reads);
        fixture.Reopen();
        Assert.Equal(128u, fixture.Checkpoint.ScannedThroughUid);
    }

    private static byte[] Plain(string from, string body) => GalateaImapMimeTests.Mail(from, "Content-Type: text/plain; charset=utf-8", body);

    private sealed class FakeConnection(uint validity, uint? next) : IGalateaImapConnection {
        public uint UidValidity { get; set; } = validity;
        public uint? UidNext { get; set; } = next;
        internal Dictionary<uint, byte[]> Messages { get; } = [];
        internal List<(uint First, uint Last)> Searches { get; } = [];
        internal List<uint> Reads { get; } = [];
        internal bool MissingRead { get; init; }
        internal bool StillPresentAfterMissingRead { get; init; }
        internal bool FailRead { get; init; }
        internal uint? FailReadUid { get; init; }
        internal uint? UidNextAfterSearch { get; init; }
        internal uint? UidNextAfterRead { get; init; }
        public Task<IReadOnlyList<uint>> SearchUidsAsync(uint first, uint last, CancellationToken cancellationToken) {
            Searches.Add((first, last));
            IReadOnlyList<uint> result = MissingRead && Reads.Count != 0 && !StillPresentAfterMissingRead
                ? [] : Messages.Keys.Where(uid => uid >= first && uid <= last).Order().ToArray();
            if (UidNextAfterSearch is { } next) { UidNext = next; }
            return Task.FromResult(result);
        }
        public Task<GalateaImapRawMessage> ReadRawAsync(uint uid, CancellationToken cancellationToken) {
            Reads.Add(uid);
            if (UidNextAfterRead is { } next) { UidNext = next; }
            if (FailRead || FailReadUid == uid) { throw new GalateaImapReadException("IMAP_FETCH_FAILED"); }
            return Task.FromResult(MissingRead ? new GalateaImapRawMessage(null, null, Missing: true) : new(Messages[uid], null));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fixture : IDisposable {
        private static readonly GalateaDelegationStoreOwner Owner = new("alice", "fixture-repository");
        private static readonly GalateaDelegationStoreLimits Limits = new(32, 100_000, 1024, 16, 16 * 1024);
        private readonly string _root = Directory.CreateTempSubdirectory("atelia-imap-receiver-").FullName;
        internal string StorePath => Path.Combine(_root, "store");
        internal string DatabasePath => Path.Combine(StorePath, GalateaDelegationSqliteStore.DatabaseFileName);
        internal GalateaDelegationSqliteStore Store { get; private set; }
        internal GalateaImapCheckpointSnapshot Checkpoint => Store.ReadImapCheckpoint(Reference)!;

        internal Fixture() {
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(_root);
            Store = GalateaDelegationSqliteStore.CreateNew(StorePath, Owner,
                new(new EventJournalPhysicalAppendFrontier(1, 4), "ej1:000000000000005a0000000100000000"), Limits);
        }
        internal void Baseline(uint uid = 0) => Store.EstablishImapBaseline(Reference, 41, uid, Now);
        internal Task<GalateaImapPollResult> Receive(FakeConnection connection, IReadOnlyCollection<string>? senders = null) =>
            GalateaImapReceiver.ReceiveAsync(Store, Reference, "Galatea", senders ?? [], connection, Now, default);
        internal void Reopen() {
            Store.Dispose();
            Store = GalateaDelegationSqliteStore.OpenExisting(StorePath, Owner, Limits);
        }
        internal long Scalar(string sql) {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        internal string[] PendingMessageIds() {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT message_id FROM external_mail_inbox WHERE state='Pending' ORDER BY inbox_id;";
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read()) { result.Add(reader.GetString(0)); }
            return result.ToArray();
        }
        public void Dispose() { Store.Dispose(); TestDirectorySafety.DeleteOwnedTreeNoFollow(_root); }
    }
}
