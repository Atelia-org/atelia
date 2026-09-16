using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaCompletionRetryTests {
    [Theory]
    [InlineData(408, null, true)]
    [InlineData(429, null, true)]
    [InlineData(500, null, true)]
    [InlineData(502, null, true)]
    [InlineData(503, null, true)]
    [InlineData(504, null, true)]
    [InlineData(401, "server_error", false)]
    [InlineData(403, null, false)]
    [InlineData(400, null, false)]
    [InlineData(429, "insufficient_quota", false)]
    [InlineData(429, "billing_hard_limit_reached", false)]
    [InlineData(503, "new_unknown_code", false)]
    [InlineData(429, "rate_limit_exceeded", true)]
    public void HttpPolicyUsesFacts(int status, string? code, bool expected) =>
        Assert.Equal(expected, GalateaCompletionRetryClient.IsRetryable(new(CompletionFailureKind.Http, status, code)));

    [Fact]
    public void DelayHasPositiveJitterCapAndRetryAfterFloor() {
        Assert.Equal(TimeSpan.FromSeconds(5), GalateaCompletionRetryClient.GetRetryDelay(1, null, 0));
        Assert.Equal(TimeSpan.FromSeconds(12), GalateaCompletionRetryClient.GetRetryDelay(2, null, 1));
        Assert.Equal(TimeSpan.FromSeconds(300), GalateaCompletionRetryClient.GetRetryDelay(int.MaxValue, null, 0));
        Assert.Equal(TimeSpan.FromDays(100), GalateaCompletionRetryClient.GetRetryDelay(1, TimeSpan.FromDays(100), 0));
        Assert.Equal(TimeSpan.FromSeconds(5), GalateaCompletionRetryClient.GetRetryDelay(1, TimeSpan.FromSeconds(-1), 0));
    }

    [Fact]
    public async Task RetryReusesRequestOptionsAndResetsObserverAfterCleanup() {
        var clock = new ManualClock();
        var request = Request();
        var options = new CompletionInvocationOptions { PromptCacheReuseHint = PromptCacheReuseHint.ReuseExpectedSoon };
        var result = Result();
        var seen = new List<CompletionStreamObserver?>();
        bool cleaned = false;
        var client = new ScriptedClient(async (call, actual, actualOptions, observer, token) => {
            Assert.Same(request, actual);
            Assert.Same(options, actualOptions);
            seen.Add(observer);
            if (call == 1) {
                try { await Task.Yield(); throw Failure(); }
                finally { cleaned = true; }
            }
            Assert.True(cleaned);
            return result;
        });
        int invocations = 0;
        var retry = new GalateaCompletionRetryClient(client, new() {
            TimeProvider = clock, RandomSample = () => 0,
            InvocationStarted = () => invocations++,
            CreateAttemptObserver = (_, _) => new(),
            RetryWaiting = _ => Assert.True(cleaned)
        });
        var task = retry.StreamCompletionAsync(request, options, null);
        await Until(() => clock.HasDue(TimeSpan.FromSeconds(5)));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Same(result, await task);
        Assert.Equal(1, invocations);
        Assert.Equal(2, seen.Count);
        Assert.NotSame(seen[0], seen[1]);
        Assert.Equal(client.Name, retry.Name);
        Assert.Equal(client.ApiSpecId, retry.ApiSpecId);
    }

    [Fact]
    public async Task DeadlineDoesNotAbandonNoncooperativeCall() {
        var clock = new ManualClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = Result();
        int timeoutNotices = 0;
        var client = new ScriptedClient(async (_, _, _, _, _) => { await release.Task; return result; });
        var retry = new GalateaCompletionRetryClient(client, new() {
            TimeProvider = clock, AttemptTimeout = TimeSpan.FromSeconds(10),
            AttemptTimedOut = _ => { timeoutNotices++; throw new InvalidOperationException("UI notification failed"); }
        });
        var task = retry.StreamCompletionAsync(Request(), null);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, timeoutNotices);
        Assert.Equal(1, client.Calls);
        Assert.False(task.IsCompleted);
        release.SetResult();
        Assert.Same(result, await task);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task DeadlineCancellationRetriesOnlyAfterCleanup() {
        var clock = new ManualClock();
        bool cleaned = false;
        var client = new ScriptedClient(async (call, _, _, _, token) => {
            if (call == 1) {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cleaned = true; }
            }
            Assert.True(cleaned);
            return Result();
        });
        var retry = new GalateaCompletionRetryClient(client, new() {
            TimeProvider = clock, AttemptTimeout = TimeSpan.FromSeconds(10), RandomSample = () => 0
        });
        var task = retry.StreamCompletionAsync(Request(), null);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Until(() => clock.HasDue(TimeSpan.FromSeconds(5)));
        Assert.True(cleaned);
        clock.Advance(TimeSpan.FromSeconds(5));
        await task;
        Assert.Equal(2, client.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopCancelsInvocationOrBackoffWithOriginalToken(bool duringBackoff) {
        var clock = new ManualClock();
        using var stop = new CancellationTokenSource();
        var client = new ScriptedClient(async (_, _, _, _, token) => {
            if (duringBackoff) { throw Failure(); }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Result();
        });
        var retry = new GalateaCompletionRetryClient(client, new() {
            TimeProvider = clock, UserStopToken = stop.Token, RandomSample = () => 0
        });
        var task = retry.StreamCompletionAsync(Request(), null);
        if (duringBackoff) { await Until(() => clock.HasDue(TimeSpan.FromSeconds(5))); }
        stop.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(stop.Token, error.CancellationToken);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task UnknownExceptionAndPermanentFailureAreNotRetried() {
        var error = new IOException("not a classified transport failure");
        var client = new ScriptedClient((_, _, _, _, _) => throw error);
        var retry = new GalateaCompletionRetryClient(client, new());
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => retry.StreamCompletionAsync(Request(), null)));
        Assert.Equal(1, client.Calls);
        var failed = Result() with { Termination = CompletionTermination.Failed(), Failure = new(CompletionFailureKind.Http, 429, "insufficient_quota") };
        var failedClient = new ScriptedClient((_, _, _, _, _) => Task.FromResult(failed));
        Assert.Same(failed, await new GalateaCompletionRetryClient(failedClient, new()).StreamCompletionAsync(Request(), null));
        Assert.Equal(1, failedClient.Calls);
    }

    [Fact]
    public async Task ProviderFailureResultRetriesButIncompleteIsReturnedUnchanged() {
        var clock = new ManualClock();
        var failed = Result() with {
            Termination = CompletionTermination.Failed(),
            Failure = new(CompletionFailureKind.Provider, ProviderCode: "overloaded_error", RetryAfter: TimeSpan.FromSeconds(40))
        };
        var incomplete = Result() with { Termination = CompletionTermination.Incomplete("max_tokens") };
        var client = new ScriptedClient((call, _, _, _, _) => Task.FromResult(call == 1 ? failed : incomplete));
        var retry = new GalateaCompletionRetryClient(client, new() { TimeProvider = clock, RandomSample = () => 0 });
        var task = retry.StreamCompletionAsync(Request(), null);
        await Until(() => clock.HasDue(TimeSpan.FromSeconds(40)));
        clock.Advance(TimeSpan.FromSeconds(39));
        Assert.Equal(1, client.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Same(incomplete, await task);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task CallerShutdownCancellationIsNotDeadlineRetry() {
        using var shutdown = new CancellationTokenSource();
        var client = new ScriptedClient(async (_, _, _, _, token) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Result();
        });
        var retry = new GalateaCompletionRetryClient(client, new());
        var task = retry.StreamCompletionAsync(Request(), null, shutdown.Token);
        shutdown.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(shutdown.Token, error.CancellationToken);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task UnrelatedCancellationCoincidingWithDeadlineIsNotRetried() {
        var clock = new ManualClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new OperationCanceledException("observer error, not the invocation token");
        var client = new ScriptedClient(async (_, _, _, _, _) => { await release.Task; throw error; });
        var retry = new GalateaCompletionRetryClient(client, new() { TimeProvider = clock, AttemptTimeout = TimeSpan.FromSeconds(10) });
        var task = retry.StreamCompletionAsync(Request(), null);
        clock.Advance(TimeSpan.FromSeconds(10));
        release.SetResult();
        Assert.Same(error, await Assert.ThrowsAsync<OperationCanceledException>(() => task));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task CompletedResultWithFailureMetadataIsNeverRegenerated() {
        var result = Result() with { Failure = new(CompletionFailureKind.Transport) };
        var client = new ScriptedClient((_, _, _, _, _) => Task.FromResult(result));
        var retry = new GalateaCompletionRetryClient(client, new());
        Assert.Same(result, await retry.StreamCompletionAsync(Request(), null));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task HugeRetryAfterIsWaitedInRepresentableChunksWithoutEarlyRetry() {
        var clock = new ManualClock();
        var maximumChunk = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        var requested = maximumChunk + TimeSpan.FromSeconds(42);
        var client = new ScriptedClient((call, _, _, _, _) => call == 1
            ? throw new CompletionFailureException(new(CompletionFailureKind.Http, 503, RetryAfter: requested), "unavailable")
            : Task.FromResult(Result()));
        var retry = new GalateaCompletionRetryClient(client, new() { TimeProvider = clock, RandomSample = () => 0 });
        var task = retry.StreamCompletionAsync(Request(), null);
        await Until(() => clock.HasDue(maximumChunk));
        clock.Advance(maximumChunk);
        await Until(() => clock.HasDue(TimeSpan.FromSeconds(42)));
        Assert.Equal(1, client.Calls);
        clock.Advance(TimeSpan.FromSeconds(41));
        Assert.Equal(1, client.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await task;
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task ObserverIOExceptionIsNotTransportRetry() {
        var error = new IOException("observer rendering error");
        var observer = new CompletionStreamObserver();
        observer.ReceivedTextDelta += _ => throw error;
        var client = new ScriptedClient((_, _, _, actualObserver, _) => {
            actualObserver!.OnTextDelta("hello");
            return Task.FromResult(Result());
        });
        var retry = new GalateaCompletionRetryClient(client, new());
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => retry.StreamCompletionAsync(Request(), observer)));
        Assert.Equal(1, client.Calls);
    }

    private static CompletionRequest Request() => new("model", new("system", new([], CompletionToolChoice.None), []), []);
    private static CompletionResult Result() => new(new([new ActionBlock.Text("done")]), new("test", "test-api", "model"));
    private static CompletionFailureException Failure() => new(new(CompletionFailureKind.Transport), "network");
    private static async Task Until(Func<bool> condition) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { await Task.Delay(1, timeout.Token); }
    }

    private sealed class ScriptedClient(Func<int, CompletionRequest, CompletionInvocationOptions?, CompletionStreamObserver?, CancellationToken, Task<CompletionResult>> invoke) : ICompletionClient {
        public int Calls;
        public string Name => "test";
        public string ApiSpecId => "test-api";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default)
            => invoke(Interlocked.Increment(ref Calls), request, null, observer, cancellationToken);
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionInvocationOptions options, CompletionStreamObserver? observer, CancellationToken cancellationToken = default)
            => invoke(Interlocked.Increment(ref Calls), request, options, observer, cancellationToken);
    }

    private sealed class ManualClock : TimeProvider {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private long _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            lock (_gate) { var timer = new Timer(this, callback, state); timer.Change(dueTime, period); _timers.Add(timer); return timer; }
        }
        internal bool HasDue(TimeSpan duration) { lock (_gate) { return _timers.Any(t => t.Due == _ticks + duration.Ticks); } }
        internal void Advance(TimeSpan amount) {
            Timer[] due;
            lock (_gate) {
                _ticks += amount.Ticks;
                due = _timers.Where(t => t.Due <= _ticks).ToArray();
                foreach (var timer in due) { timer.Due = long.MaxValue; }
            }
            foreach (var timer in due) { timer.Callback(timer.State); }
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer {
            internal TimerCallback Callback => callback;
            internal object? State => state;
            internal long Due;
            public bool Change(TimeSpan dueTime, TimeSpan period) {
                lock (clock._gate) { Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks; return true; }
            }
            public void Dispose() { lock (clock._gate) { clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
