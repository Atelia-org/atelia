using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Server.Mailbox;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaImapHostStatusTests {
    private const string Secret = "SYNTHETIC_IMAP_STATUS_SECRET";

    [Theory]
    [InlineData(false, false, false, false, "Paused")]
    [InlineData(true, false, false, false, "Paused")]
    [InlineData(true, true, true, false, "Paused")]
    [InlineData(true, true, false, true, "Paused")]
    [InlineData(true, true, false, false, "BaselinePending")]
    public async Task ReadingStatusDoesNotBootstrapSessionOrEstablishBaseline(
        bool configured, bool enabled, bool maintenance, bool stopping, string expectedState
    ) {
        var factory = new NoCompletionFactory();
        await using var files = GalateaTestHost.Create(factory, null);
        Configure(files, configured, enabled, maintenance);
        await using var host = OpenHost(files, factory);
        var transport = new NoNetworkTransport();
        using var poller = new GalateaImapPoller(host, transport);
        if (stopping) { host.BeginShutdown(); }
        string[] sessionFiles = SessionFiles(files);

        for (int read = 0; read < 3; read++) {
            GalateaImapInboundStatusDto status = host.ReadImapInboundStatus("alice");
            Assert.Equal(expectedState, status.State);
            Assert.Equal(configured, status.Configured);
            Assert.False(status.BaselineEstablished);
            Assert.Null(status.LastPoll);
            Assert.DoesNotContain(Secret, JsonSerializer.Serialize(status), StringComparison.Ordinal);
        }

        Assert.Null(host.ReadAttachedSession("alice"));
        Assert.False(Directory.Exists(files.DelegationStateDirectory));
        Assert.Equal(sessionFiles, SessionFiles(files));
        Assert.Equal(0, transport.OpenCount);
        Assert.Equal(0, factory.CreateCount);
    }

    [Theory]
    [InlineData(true, false, "Ready")]
    [InlineData(false, false, "Paused")]
    [InlineData(true, true, "Paused")]
    public async Task ExistingBaselineAndQueueRemainVisibleWithoutAttachingSession(
        bool enabled, bool maintenance, string expectedState
    ) {
        var factory = new NoCompletionFactory();
        await using var files = GalateaTestHost.Create(factory, null);
        Configure(files, true, true, false);
        GalateaImapCheckpointSnapshot checkpoint;
        GalateaExternalMailInboxSnapshot mail;
        await using (var seed = OpenHost(files, factory)) {
            var session = await seed.GetSessionAsync("alice", CancellationToken.None);
            var store = session.DelegationHandle!.Store;
            checkpoint = store.EstablishImapBaseline(seed.Imap.ReferenceFor("alice")!, 41, 9, DateTimeOffset.UnixEpoch);
            mail = store.AcceptImapMail(checkpoint, 10, "Galatea", "friend@example.test", null, "body", 0)!;
            checkpoint = store.ReadImapCheckpoint(checkpoint.AccountReference)!;
        }
        Configure(files, true, enabled, maintenance);
        await using var host = OpenHost(files, factory);
        var transport = new NoNetworkTransport();
        using var poller = new GalateaImapPoller(host, transport);
        int clientsBefore = factory.CreateCount;
        string[] sessionFiles = SessionFiles(files);

        for (int read = 0; read < 3; read++) {
            var status = host.ReadImapInboundStatus("alice");
            Assert.Equal(expectedState, status.State);
            Assert.True(status.BaselineEstablished);
            Assert.Equal(checkpoint.ScannedThroughUid, status.ScannedThroughUid);
            Assert.Equal(1, status.PendingCount);
            Assert.Equal(0, status.BoundCount);
            Assert.Null(status.LastPoll);
        }

        Assert.Null(host.ReadAttachedSession("alice"));
        Assert.Equal(sessionFiles, SessionFiles(files));
        Assert.Equal(clientsBefore, factory.CreateCount);
        Assert.Equal(0, transport.OpenCount);
        // Reuse the host's lifetime owner, including its maintenance read-only
        // store, rather than opening a competing owner of the same store.
        Assert.True(host.DelegationSupervisor.TryGetObservableStore("alice", out var view));
        Assert.Equal(checkpoint, view.ReadImapCheckpoint(checkpoint.AccountReference));
        Assert.Equal(mail, view.ReadExternalMail(mail.InboxId));
    }

    [Fact]
    public async Task SenderListEditRetainsBaselineAndBlockedRecoveryStateAcrossRestart() {
        var factory = new NoCompletionFactory();
        await using var files = GalateaTestHost.Create(factory, null);
        Configure(files, true, true, false);
        GalateaImapCheckpointSnapshot blocked;
        GalateaExternalMailInboxSnapshot mail;
        await using (var seed = OpenHost(files, factory)) {
            var session = await seed.GetSessionAsync("alice", CancellationToken.None);
            var store = session.DelegationHandle!.Store;
            var baseline = store.EstablishImapBaseline(seed.Imap.ReferenceFor("alice")!, 41, 9, DateTimeOffset.UnixEpoch);
            mail = store.AcceptImapMail(baseline, 10, "Galatea", "friend@example.test", null, "retained", 0)!;
            blocked = store.BlockImapCheckpoint(store.ReadImapCheckpoint(baseline.AccountReference)!, "IMAP_UIDVALIDITY_CHANGED");
        }
        Configure(files, true, true, false, "new-friend@example.test");
        await using var host = OpenHost(files, factory);
        Assert.Equal(blocked.AccountReference, host.Imap.ReferenceFor("alice"));
        Assert.Equal("new-friend@example.test", Assert.Single(host.Imap.Accounts["alice"].Imap!.AutoDisplaySenders));
        var status = host.ReadImapInboundStatus("alice");
        Assert.Equal("Blocked", status.State);
        Assert.Equal("IMAP_UIDVALIDITY_CHANGED", status.BlockedCode);
        Assert.Equal(10u, status.ScannedThroughUid);
        Assert.Equal(1, status.PendingCount);
        Assert.Null(host.ReadAttachedSession("alice"));
        Assert.True(host.DelegationSupervisor.TryGetAttachedStore("alice", out var retained));
        Assert.Equal(blocked, retained.ReadImapCheckpoint(blocked.AccountReference));
        Assert.Equal(mail, retained.ReadExternalMail(mail.InboxId));
    }

    private static GalateaHostService OpenHost(GalateaTestHost files, NoCompletionFactory factory) =>
        new(GalateaConfigLoader.Load(files.ConfigPath), factory, new GalateaUserMessageNormalizerFactory());

    private static void Configure(GalateaTestHost files, bool configured, bool enabled, bool maintenance,
        string sender = "friend@example.test") {
        JsonNode root = JsonNode.Parse(File.ReadAllText(files.ConfigPath))!;
        root["runtime"]!["maintenanceMode"] = maintenance;
        root["runtime"]!["imap"] = new JsonObject { ["enabled"] = enabled };
        root["characters"]![0]!["email"] = configured ? new JsonObject {
            ["address"] = "alice@example.test", ["authorizationCode"] = Secret,
            ["smtpHost"] = "smtp.example.test", ["smtpPort"] = 465, ["tlsMode"] = "implicit",
            ["imap"] = new JsonObject {
                ["host"] = "imap.example.test", ["port"] = 993, ["tlsMode"] = "implicit",
                ["autoDisplaySenders"] = new JsonArray(sender)
            }
        } : null;
        File.WriteAllText(files.ConfigPath, root.ToJsonString());
    }

    private static string[] SessionFiles(GalateaTestHost files) => Directory
        .EnumerateFiles(files.SessionDirectory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(files.SessionDirectory, path) + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class NoCompletionFactory : ICompletionClientFactory {
        internal int CreateCount { get; private set; }
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CreateCount++;
            return new NoCompletionClient();
        }
    }

    private sealed class NoCompletionClient : ICompletionClient {
        public string Name => "imap-status-fixture";
        public string ApiSpecId => "fixture";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An IMAP status read must not dispatch a model request.");
    }

    private sealed class NoNetworkTransport : IGalateaImapTransport {
        internal int OpenCount { get; private set; }
        public Task<IGalateaImapConnection> OpenAsync(GalateaEmailAccount account, CancellationToken cancellationToken) {
            OpenCount++;
            throw new InvalidOperationException("A status read must not open an IMAP connection.");
        }
    }
}
