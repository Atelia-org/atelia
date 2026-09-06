using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json.Nodes;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaServerAgentRuntimeTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HostedLoop_WithoutBrowser_UsesDefaultAndDoesNotCatchUp() {
        var clock = new TimerClock();
        var completion = new CompletionClient();
        await using var fixture = GalateaTestHost.Create(
            completion, new Normalizer(), timeProvider: clock,
            connections: [Connection("other"), Connection("test")],
            selectableConnectionIds: ["other", "test"],
            serverAgentUserIds: ["alice"], enableServerAgentHostedService: true
        );
        JsonObject config = JsonNode.Parse(File.ReadAllText(fixture.ConfigPath))!.AsObject();
        JsonArray users = config["users"]!.AsArray();
        JsonObject bob = users[0]!.DeepClone().AsObject();
        bob["userId"] = "bob";
        bob["defaultConnectionId"] = "other";
        bob["sessionProvisioning"] = "create-if-missing";
        string bobDirectory = Path.Combine(fixture.RootDirectory, "bob-session");
        bob["sessionDir"] = bobDirectory;
        bob["delegationStateDir"] = Path.Combine(fixture.RootDirectory, "bob-delegation");
        bob["characterMemoryStateDir"] = Path.Combine(fixture.RootDirectory, "bob-memory");
        users.Add(bob);
        File.WriteAllText(fixture.ConfigPath, config.ToJsonString());
        IServiceProvider services = fixture.Factory.Services; // No HTTP client or Player input.
        var host = services.GetRequiredService<GalateaHostService>();
        var coordinator = services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        var loop = services.GetServices<IHostedService>().OfType<GalateaServerAgentHostedService>().Single();
        await UntilAsync(() => coordinator.ReadStatus("alice").State == "waiting");
        Assert.NotNull(host.ReadAttachedSession("alice"));
        Assert.Null(host.ReadAttachedSession("bob"));
        Assert.Equal(0, completion.Calls);
        long? due = coordinator.ReadStatus("alice").NextActivationAtUnixTimeMilliseconds;

        var pulsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.PulseCompletedForTest = _ => pulsed.TrySetResult();
        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        await pulsed.Task.WaitAsync(Deadline);
        Assert.Equal(0, completion.Calls);
        Assert.Equal(due, coordinator.ReadStatus("alice").NextActivationAtUnixTimeMilliseconds);

        clock.Advance(TimeSpan.FromSeconds(1));
        // The next cheap check is at 10m09s after the delayed previous pulse.
        clock.Advance(TimeSpan.FromSeconds(9));
        await UntilAsync(() => completion.Calls >= 1);
        await UntilAsync(() => coordinator.ReadStatus("alice").State != "running");
        Assert.Equal("waiting", coordinator.ReadStatus("alice").State);
        Assert.Equal("test", completion.LastConnectionId);
        Assert.Equal("test", coordinator.ReadStatus("alice").ConnectionId);

        clock.Advance(TimeSpan.FromHours(3));
        await UntilAsync(() => completion.Calls >= 2);
        await UntilAsync(() => coordinator.ReadStatus("alice").State != "running");
        Assert.Equal("waiting", coordinator.ReadStatus("alice").State);
        pulsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.PulseCompletedForTest = _ => pulsed.TrySetResult();
        clock.Advance(TimeSpan.FromSeconds(10));
        await pulsed.Task.WaitAsync(Deadline);
        Assert.Equal(2, completion.Calls);
        Assert.Null(host.ReadAttachedSession("bob"));
        Assert.False(Directory.Exists(bobDirectory));
    }

    [Fact]
    public async Task HostedStop_WithCancelledBudget_DrainsAttachAndDisposesResources() {
        await using var fixture = GalateaTestHost.Create(
            new CompletionClient(), new Normalizer(), serverAgentUserIds: ["alice"]
        );
        IServiceProvider services = fixture.Factory.Services;
        var host = services.GetRequiredService<GalateaHostService>();
        var coordinator = services.GetRequiredService<GalateaAutomaticTurnCoordinator>();
        using var loop = new GalateaServerAgentHostedService(host, coordinator, services.GetRequiredService<IHostApplicationLifetime>());
        var entered = new TaskCompletionSource<UserSessionHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionAttachedForTest = async session => {
            entered.SetResult(session);
            await release.Task;
        };
        await loop.StartAsync(CancellationToken.None);
        UserSessionHost session = await entered.Task.WaitAsync(Deadline);
        Task stopping = loop.StopAsync(new CancellationToken(canceled: true));
        Assert.True(host.IsStopping);
        Assert.False(stopping.IsCompleted);
        Assert.NotNull(session.Engine.ReadCurrentHead());
        release.SetResult();
        await stopping.WaitAsync(Deadline);
        Assert.Throws<ObjectDisposedException>(() => session.Engine.ReadCurrentHead());
        Assert.Same(host.DisposeAsync().AsTask(), host.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Shutdown_DrainsPendingAttachAndClosesNewRegistrations() {
        var fixture = GalateaTestHost.Create(new CompletionClient(), new Normalizer());
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var entered = new TaskCompletionSource<UserSessionHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionAttachedForTest = async session => {
            entered.TrySetResult(session);
            await release.Task;
        };
        Task<UserSessionHost> attaching = host.GetSessionAsync("alice", CancellationToken.None);
        UserSessionHost session = await entered.Task.WaitAsync(Deadline);
        Task disposal = host.DisposeAsync().AsTask();
        await UntilAsync(() => host.IsStopping);
        Assert.False(disposal.IsCompleted);
        Assert.NotNull(session.Engine.ReadCurrentHead());
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.GetSessionAsync("alice", CancellationToken.None));
        release.SetResult();
        _ = await attaching.WaitAsync(Deadline);
        await disposal.WaitAsync(Deadline);
        Assert.Throws<ObjectDisposedException>(() => session.Engine.ReadCurrentHead());
        Assert.Throws<OperationCanceledException>(host.RequireRunning);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_BeforeDelegationAttach_CancelsAdmissionAndDisposesCleanly() {
        var fixture = GalateaTestHost.Create(new CompletionClient(), new Normalizer());
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeDelegationAttachForTest = async () => {
            entered.SetResult();
            await release.Task;
        };
        Task<UserSessionHost> attaching = host.GetSessionAsync("alice", CancellationToken.None);
        await entered.Task.WaitAsync(Deadline);
        Task disposal = host.DisposeAsync().AsTask();
        await UntilAsync(() => host.IsStopping);
        Assert.False(disposal.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => attaching);
        await disposal.WaitAsync(Deadline);
        Assert.Null(host.ReadAttachedSession("alice"));
        await fixture.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_CancelsAndDrainsAcceptedWriterBeforeDisposal(bool throughHostedService) {
        var completion = new CompletionClient(block: true);
        var fixture = GalateaTestHost.Create(completion, new Normalizer(), enableServerAgentHostedService: throughHostedService);
        var host = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        var runner = fixture.Factory.Services.GetRequiredService<GalateaAcceptedTurnRunner>();
        UserSessionHost session = await host.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = host.StartTurn(session, "shutdown probe", new("test"));
        Task run = runner.Start(session, turn);
        await completion.Entered.Task.WaitAsync(Deadline);
        Task disposal = throughHostedService
            ? fixture.DisposeAsync().AsTask()
            : host.DisposeAsync().AsTask();
        await UntilAsync(() => host.IsStopping);
        Assert.False(disposal.IsCompleted);
        Assert.NotNull(session.Engine.ReadCurrentHead());
        Assert.False(session.TurnLock.Wait(0));
        completion.Release.SetResult();
        await run.WaitAsync(Deadline);
        await disposal.WaitAsync(Deadline);
        Assert.Equal("failed", turn.Status);
        Assert.Throws<ObjectDisposedException>(() => session.Engine.ReadCurrentHead());
        await fixture.DisposeAsync();
    }

    private static async Task UntilAsync(Func<bool> condition) {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!condition()) { await Task.Delay(5, timeout.Token); }
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id, "openai-chat", "model-a", "openai-chat/strict", "http://localhost:8000/", ApiKey: "test-key"
    );

    private sealed class Normalizer : IGalateaUserMessageNormalizer {
        public bool ShouldNormalize(string userMessage) => false;
        public ValueTask<string> NormalizeAsync(string userMessage, CancellationToken ct) => ValueTask.FromResult(userMessage);
    }

    private sealed class CompletionClient(bool block = false) : ICompletionClientFactory, ICompletionClient {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal string? LastConnectionId { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "server-loop-test";
        public string ApiSpecId => "openai-chat-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            LastConnectionId = connection.Id;
            return this;
        }
        public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            if (block) { await Release.Task; }
            cancellationToken.ThrowIfCancellationRequested();
            return new(new ActionMessage([new ActionBlock.Text("自主行动完成。")]), new CompletionDescriptor(Name, ApiSpecId, "model-a"));
        }
    }

    private sealed class TimerClock : TimeProvider {
        private readonly object _gate = new();
        private readonly List<ClockTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) { return _ticks; } }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            lock (_gate) {
                var timer = new ClockTimer(this, callback, state, _ticks + dueTime.Ticks, period);
                _timers.Add(timer);
                return timer;
            }
        }
        internal void Advance(TimeSpan duration) {
            ClockTimer[] due;
            lock (_gate) {
                _ticks += duration.Ticks;
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToArray();
                foreach (var timer in due) { timer.Due = _ticks + timer.Period.Ticks; }
            }
            foreach (var timer in due) { timer.Callback(timer.State); }
        }
        private sealed class ClockTimer(TimerClock clock, TimerCallback callback, object? state, long due, TimeSpan period) : ITimer {
            internal TimerCallback Callback => callback;
            internal object? State => state;
            internal long Due = due;
            internal TimeSpan Period = period;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan nextPeriod) {
                lock (clock._gate) {
                    if (Disposed) { return false; }
                    Due = clock._ticks + dueTime.Ticks;
                    Period = nextPeriod;
                    return true;
                }
            }
            public void Dispose() { lock (clock._gate) { Disposed = true; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
