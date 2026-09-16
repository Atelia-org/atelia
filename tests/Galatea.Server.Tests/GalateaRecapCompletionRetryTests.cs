using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed partial class GalateaRecapGridCompositionTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerMaintenanceRetriesFrozenWorkPastOldDeadlineAndReusesSettledCell(bool stopDuringBackoff) {
        string path = NewPath();
        FamilyDefinition family;
        GridBuildRecipe recipe;
        using (var provisioner = SessionJournalEngine.Create(path, new("model-a", "test system prompt", "openai-chat/strict"))) {
            ProvisionTimelineAndControl(provisioner);
            (family, _, recipe) = ProvisionActiveEmptyRecipe(provisioner);
        }
        var connection = Connection();
        var profile = AgentProfile();
        var config = Config(path, connection) with {
            RecapGrid = new(
                new GalateaRecapGridMaintenanceConfig(
                    connection.Id,
                    1,
                    TimeSpan.FromMilliseconds(30)
                ),
                new RecapGridAgentControlProfileRegistry([profile])
            )
        };
        var clock = new GalateaLabClock();
        var provider = new RetryMaintenanceClient();
        await using var owner = new GalateaCompletionOwner(config, provider, clock);
        await using var service = new GalateaHostService(config, DisabledGalateaUserMessageNormalizer.Instance,
            owner.RecapGrid, DefaultPolicies(GalateaRecapGridDefaultPolicy.ForTarget(recipe.Target)), clock);
        var session = await service.GetSessionAsync("alice", default);
        await RunFreshAsync(service, session, connection.Id, "first clue");
        Assert.Equal(1, provider.MainCalls);
        Assert.Empty(provider.RecapRequests);
        var expected = GalateaRecapGridDefaultPolicy.ForTarget(recipe.Target);
        using var stop = new CancellationTokenSource();
        Task<GalateaRecapGridTurn> pending = owner.RecapGrid.OpenFreshAsync(session.Engine, connection.Id,
            "second clue", expected, stop.Token).AsTask();
        await provider.Failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The previous runtime-wide 30 ms deadline would terminate this logical
        // invocation during backoff. Only the per-attempt owner may time it out.
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        if (stopDuringBackoff) {
            stop.Cancel();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(stop.Token, canceled.CancellationToken);
            Assert.Single(provider.RecapRequests);
            // Manager may retain incomplete row debt, but not an unresumable
            // environment failure. The same head can continue maintenance.
            pending = owner.RecapGrid.OpenFreshAsync(session.Engine, connection.Id, "second clue", expected, default).AsTask();
        }
        else { clock.Advance(TimeSpan.FromSeconds(6)); }
        await using (var opened = await pending.WaitAsync(TimeSpan.FromSeconds(10))) {
            Assert.Same(provider, opened.Client); // Main generation still uses the raw registry binding.
        }
        Assert.Equal(2, provider.RecapRequests.Count);
        if (!stopDuringBackoff) { Assert.Same(provider.RecapRequests[0], provider.RecapRequests[1]); }
        int settledCalls = provider.RecapRequests.Count;
        await using (var reopened = await owner.RecapGrid.OpenFreshAsync(session.Engine, connection.Id,
            "second clue", expected, default)) {
            Assert.Same(provider, reopened.Client);
        }
        Assert.Equal(settledCalls, provider.RecapRequests.Count);
        Assert.Equal(1, provider.MainCalls);
        Assert.Equal("ready", GalateaRecapGridReadiness.Inspect(session.Engine.ReadView,
            session.Engine.ReadCurrentHead()!.Value, expected, default).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecapRetryInvokerCancellationDrainsCallOrBackoff(bool duringBackoff) {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MaintenanceCancellationClient(async token => {
            entered.TrySetResult();
            try {
                if (duringBackoff) { throw new CompletionFailureException(new(CompletionFailureKind.Http, 503), "temporary"); }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }
            finally { cleaned.TrySetResult(); }
        });
        var invoker = new GalateaRecapCompletionRetryInvoker("test", client, TimeSpan.FromSeconds(30), new GalateaLabClock());
        using var stop = new CancellationTokenSource();
        var request = new CompletionRequest("model", new("system", new([], CompletionToolChoice.None), []), []);
        var pending = invoker.InvokeAsync(request, CompletionInvocationOptions.Default, stop.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(stop.Token, canceled.CancellationToken);
        Assert.True(cleaned.Task.IsCompleted);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task RecapRetryInvokerAppliesRouteDeadlineToEachAttemptAndDrainsBeforeRetry() {
        var clock = new MaintenanceDeadlineClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cleaned = false;
        MaintenanceCancellationClient? client = null;
        client = new(async token => {
            if (client!.Calls == 1) {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cleaned = true; }
            }
            Assert.True(cleaned);
            return new CompletionResult(new ActionMessage([new ActionBlock.Text("result")]), new(client.Name, client.ApiSpecId, "model"));
        });
        var invoker = new GalateaRecapCompletionRetryInvoker("test", client, TimeSpan.FromSeconds(30), clock);
        var request = new CompletionRequest("model", new("system", new([], CompletionToolChoice.None), []), []);
        Task<CompletionResult> pending = invoker.InvokeAsync(request, CompletionInvocationOptions.Default, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(30));
        await clock.BackoffScheduled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(cleaned);
        Assert.Equal(1, client.Calls);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(10))).Termination.IsSuccess);
        Assert.Equal(2, client.Calls);
    }

    private sealed class MaintenanceDeadlineClock : TimeProvider {
        private readonly GalateaLabClock _inner = new();
        internal TaskCompletionSource BackoffScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Advance(TimeSpan amount) => _inner.Advance(amount);
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime >= TimeSpan.FromSeconds(5) && dueTime <= TimeSpan.FromSeconds(6)) { BackoffScheduled.TrySetResult(); }
            return timer;
        }
    }

    private sealed class MaintenanceCancellationClient(Func<CancellationToken, Task<CompletionResult>> run) : ICompletionClient {
        internal int Calls;
        public string Name => "maintenance-cancellation";
        public string ApiSpecId => "test-api";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) { Calls++; return run(cancellationToken); }
    }

    private sealed class RetryMaintenanceClient : ICompletionClient, ICompletionClientFactory {
        internal int MainCalls;
        internal List<CompletionRequest> RecapRequests { get; } = [];
        internal TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "maintenance-retry";
        public string ApiSpecId => "openai-chat-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            bool recap = request.TailMessages is [ObservationMessage { Content: { } text }]
                && text.Contains($"\"schema\":\"{RecapRewriterProtocolV3.InputProtocolId}\"", StringComparison.Ordinal);
            if (recap) {
                RecapRequests.Add(request);
                if (RecapRequests.Count == 1) {
                    Failed.TrySetResult();
                    throw new CompletionFailureException(new(CompletionFailureKind.Http, 503), "temporary");
                }
            }
            else { MainCalls++; }
            return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.Text("remember the clue")]), CompletionDescriptor.From(this, request)));
        }
    }
}
