using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Server.Mailbox;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaImapShutdownTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAndDrainBlockedOpen_CompleteBeforeHostReleasesStoreOwner(bool disposeHost) {
        var completion = new NeverSendFactory();
        var transport = new BlockingTransport();
        await using var files = CreateFiles(completion, enabled: true, maintenance: false);
        await using var web = files.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IGalateaImapTransport>();
            services.AddSingleton<IGalateaImapTransport>(transport);
        }));
        using var releaseOnExit = new ReleaseOnExit(transport);
        var services = web.Services;
        var host = services.GetRequiredService<GalateaHostService>();
        var poller = services.GetRequiredService<GalateaImapPoller>();
        await transport.Entered.Task.WaitAsync(Deadline);
        var store = await host.GetImapStoreAsync("alice", default);
        string reference = GalateaImapConfig.AccountReference("alice", host.Imap.Accounts["alice"]);
        Assert.Null(store.ReadImapCheckpoint(reference));
        int ownersReleased = 0;
        host.DisposeHooksForTest = new(
            AfterSessionDisposed: _ => Assert.True(transport.Finished.Task.IsCompleted),
            AfterDelegationSupervisorDisposed: () => {
                Assert.True(transport.Finished.Task.IsCompleted);
                Interlocked.Increment(ref ownersReleased);
            });
        Task drain;
        if (disposeHost) { drain = host.DisposeAsync().AsTask(); }
        else { poller.BeginShutdown(); drain = poller.DrainAsync(); }
        try {
            await transport.CancelObserved.Task.WaitAsync(Deadline);
            Assert.False(drain.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref ownersReleased));
            // Owner is still usable while the canceled network call drains.
            Assert.Null(store.ReadImapCheckpoint(reference));
            Assert.Equal(0, store.ReadImapInboxStatus().PendingCount);
            Assert.Equal(0, completion.SendCount);
        }
        finally { transport.Release.TrySetResult(); }
        await drain.WaitAsync(Deadline);
        Assert.True(transport.Finished.Task.IsCompleted);
        if (!disposeHost) {
            Assert.Null(store.ReadImapCheckpoint(reference));
            Assert.Equal(0, ownersReleased);
            await host.DisposeAsync();
        }
        Assert.Equal(1, ownersReleased);
        Assert.Throws<ObjectDisposedException>(() => store.ReadImapCheckpoint(reference));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledOrMaintenancePoll_DoesNotCreateSessionOrBaseline(bool enabled, bool maintenance) {
        var completion = new NeverSendFactory();
        var transport = new BlockingTransport();
        await using var files = CreateFiles(completion, enabled, maintenance);
        await using var web = files.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IGalateaImapTransport>();
            services.AddSingleton<IGalateaImapTransport>(transport);
        }));
        using var releaseOnExit = new ReleaseOnExit(transport);
        var host = web.Services.GetRequiredService<GalateaHostService>();
        var poller = web.Services.GetRequiredService<GalateaImapPoller>();
        Assert.Equal("IMAP_PAUSED", (await poller.PollCharacterAsync("alice", default).WaitAsync(Deadline)).Code);
        Assert.Equal(0, transport.OpenCount);
        Assert.False(host.DelegationSupervisor.TryGetObservableStore("alice", out _));
        var status = host.ReadImapInboundStatus("alice");
        Assert.Equal("Paused", status.State);
        Assert.False(status.BaselineEstablished);
        Assert.Equal(0, completion.CreateCount);
        Assert.Equal(0, completion.SendCount);
    }

    [Theory]
    [InlineData("IMAP_UIDVALIDITY_CHANGED")]
    [InlineData("IMAP_UIDNEXT_REGRESSED")]
    public async Task NamespaceFailureDuringOpen_BlocksExistingCheckpointAcrossReopen(string code) {
        var completion = new NeverSendFactory();
        var transport = new RejectingTransport("IMAP_TEST_DEFERRED");
        await using var files = CreateFiles(completion, enabled: true, maintenance: false);
        string reference;
        await using (var web = files.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IGalateaImapTransport>();
            services.AddSingleton<IGalateaImapTransport>(transport);
        }))) {
            var host = web.Services.GetRequiredService<GalateaHostService>();
            var poller = web.Services.GetRequiredService<GalateaImapPoller>();
            await WaitForFirstPollAsync(poller);
            var store = await host.GetImapStoreAsync("alice", default);
            reference = GalateaImapConfig.AccountReference("alice", host.Imap.Accounts["alice"]);
            _ = store.EstablishImapBaseline(reference, 41, 7, host.TimeProvider.GetUtcNow());
            transport.Code = code;
            Assert.Equal(code, (await poller.PollCharacterAsync("alice", default)).Code);
            var checkpoint = Assert.IsType<GalateaImapCheckpointSnapshot>(store.ReadImapCheckpoint(reference));
            Assert.Equal(code, checkpoint.BlockedCode);
            Assert.Equal(7u, checkpoint.ScannedThroughUid);
            Assert.Equal(0, store.ReadImapInboxStatus().PendingCount);
        }
        var reopenedTransport = new RejectingTransport("IMAP_TEST_SHOULD_NOT_CONNECT");
        await using var reopened = files.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IGalateaImapTransport>();
            services.AddSingleton<IGalateaImapTransport>(reopenedTransport);
        }));
        var reopenedHost = reopened.Services.GetRequiredService<GalateaHostService>();
        var reopenedPoller = reopened.Services.GetRequiredService<GalateaImapPoller>();
        await WaitForFirstPollAsync(reopenedPoller);
        Assert.Equal(code, (await reopenedPoller.PollCharacterAsync("alice", default)).Code);
        var reopenedStore = await reopenedHost.GetImapStoreAsync("alice", default);
        Assert.Equal(code, reopenedStore.ReadImapCheckpoint(reference)!.BlockedCode);
        Assert.Equal(0, reopenedTransport.OpenCount);
        Assert.Equal(0, completion.SendCount);
    }

    [Theory]
    [InlineData("IMAP_UIDVALIDITY_CHANGED")]
    [InlineData("IMAP_UIDNEXT_REGRESSED")]
    public async Task NamespaceFailureDuringFirstOpen_DoesNotEstablishBaseline(string code) {
        var completion = new NeverSendFactory();
        var transport = new RejectingTransport(code);
        await using var files = CreateFiles(completion, enabled: true, maintenance: false);
        await using var web = files.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IGalateaImapTransport>();
            services.AddSingleton<IGalateaImapTransport>(transport);
        }));
        var host = web.Services.GetRequiredService<GalateaHostService>();
        var poller = web.Services.GetRequiredService<GalateaImapPoller>();
        await WaitForFirstPollAsync(poller);
        Assert.Equal(code, (await poller.PollCharacterAsync("alice", default)).Code);
        var store = await host.GetImapStoreAsync("alice", default);
        string reference = GalateaImapConfig.AccountReference("alice", host.Imap.Accounts["alice"]);
        Assert.Null(store.ReadImapCheckpoint(reference));
        Assert.Equal(0, store.ReadImapInboxStatus().PendingCount);
        Assert.Equal(0, completion.SendCount);
    }

    [Fact]
    public async Task MissingOpenMetadata_DefersWithoutBlockingExistingCheckpoint() {
        var completion = new NeverSendFactory();
        var transport = new RejectingTransport("IMAP_TEST_DEFERRED");
        await using var files = CreateFiles(completion, enabled: true, maintenance: false);
        await using var web = files.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IGalateaImapTransport>();
            services.AddSingleton<IGalateaImapTransport>(transport);
        }));
        var host = web.Services.GetRequiredService<GalateaHostService>();
        var poller = web.Services.GetRequiredService<GalateaImapPoller>();
        await WaitForFirstPollAsync(poller);
        var store = await host.GetImapStoreAsync("alice", default);
        string reference = GalateaImapConfig.AccountReference("alice", host.Imap.Accounts["alice"]);
        var checkpoint = store.EstablishImapBaseline(reference, 41, 7, host.TimeProvider.GetUtcNow());
        transport.Code = "IMAP_INVALID_UID_METADATA";
        Assert.Equal("IMAP_INVALID_UID_METADATA", (await poller.PollCharacterAsync("alice", default)).Code);
        Assert.Equal(checkpoint, store.ReadImapCheckpoint(reference));
    }

    [Fact]
    public async Task CancellationCoincidingWithOpenNamespaceFailure_DoesNotWriteCheckpoint() {
        var completion = new NeverSendFactory();
        var transport = new RejectingTransport("IMAP_TEST_DEFERRED");
        await using var files = CreateFiles(completion, enabled: true, maintenance: false);
        await using var web = files.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IGalateaImapTransport>();
            services.AddSingleton<IGalateaImapTransport>(transport);
        }));
        var host = web.Services.GetRequiredService<GalateaHostService>();
        var poller = web.Services.GetRequiredService<GalateaImapPoller>();
        await WaitForFirstPollAsync(poller);
        var store = await host.GetImapStoreAsync("alice", default);
        string reference = GalateaImapConfig.AccountReference("alice", host.Imap.Accounts["alice"]);
        var checkpoint = store.EstablishImapBaseline(reference, 41, 7, host.TimeProvider.GetUtcNow());
        transport.Code = "IMAP_UIDVALIDITY_CHANGED";
        using var cancellation = new CancellationTokenSource();
        transport.BeforeFailure = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.PollCharacterAsync("alice", cancellation.Token));
        Assert.Equal(checkpoint, store.ReadImapCheckpoint(reference));
    }

    private static async Task WaitForFirstPollAsync(GalateaImapPoller poller) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (poller.ReadLastPoll("alice") is null) { await Task.Delay(10, deadline.Token); }
    }

    private static GalateaTestHost CreateFiles(NeverSendFactory completion, bool enabled, bool maintenance) {
        var files = GalateaTestHost.Create(completion, DisabledGalateaUserMessageNormalizer.Instance,
            maintenanceMode: maintenance, timeProvider: new GalateaLabClock());
        JsonNode config = JsonNode.Parse(File.ReadAllText(files.ConfigPath))!;
        config["characters"]![0]!["email"] = new JsonObject {
            ["address"] = "host@example.test", ["authorizationCode"] = "SYNTHETIC_SHUTDOWN_AUTH",
            ["smtpHost"] = "smtp.example.test", ["smtpPort"] = 465, ["tlsMode"] = "implicit",
            ["imap"] = new JsonObject { ["host"] = "imap.example.test", ["port"] = 993, ["tlsMode"] = "implicit" }
        };
        config["runtime"]!["imap"] = new JsonObject {
            ["enabled"] = enabled, ["pollIntervalSeconds"] = 60, ["timeoutSeconds"] = 300
        };
        File.WriteAllText(files.ConfigPath, config.ToJsonString());
        return files;
    }

    private sealed class BlockingTransport : IGalateaImapTransport {
        private int _openCount;
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IGalateaImapConnection> OpenAsync(GalateaEmailAccount account, CancellationToken cancellationToken) {
            Interlocked.Increment(ref _openCount);
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                CancelObserved.TrySetResult();
                // Simulate final transport cleanup that must complete while
                // the attached SQLite owner remains available.
                await Release.Task;
                throw new OperationCanceledException(cancellationToken);
            }
            finally { Finished.TrySetResult(); }
            throw new InvalidOperationException("Synthetic blocked transport cannot open.");
        }
    }

    private sealed class RejectingTransport(string code) : IGalateaImapTransport {
        private int _openCount;
        internal string Code { get; set; } = code;
        internal Action? BeforeFailure { get; set; }
        internal int OpenCount => Volatile.Read(ref _openCount);
        public Task<IGalateaImapConnection> OpenAsync(GalateaEmailAccount account, CancellationToken cancellationToken) {
            Interlocked.Increment(ref _openCount);
            BeforeFailure?.Invoke();
            return Task.FromException<IGalateaImapConnection>(new GalateaImapReadException(Code));
        }
    }

    private sealed class ReleaseOnExit(BlockingTransport transport) : IDisposable {
        public void Dispose() => transport.Release.TrySetResult();
    }

    private sealed class NeverSendFactory : ICompletionClientFactory {
        private int _createCount, _sendCount;
        internal int CreateCount => Volatile.Read(ref _createCount);
        internal int SendCount => Volatile.Read(ref _sendCount);
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Interlocked.Increment(ref _createCount);
            return new Client(this);
        }
        private sealed class Client(NeverSendFactory owner) : ICompletionClient {
            public string Name => "shutdown-test";
            public string ApiSpecId => "fixture";
            public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
                CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
                Interlocked.Increment(ref owner._sendCount);
                throw new InvalidOperationException("Shutdown/pause must not send a model request.");
            }
        }
    }
}
