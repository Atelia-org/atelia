using Atelia.Completion.Abstractions;

namespace Atelia.Galatea.Server;

internal sealed record GalateaCompletionRetryNotice(int Attempt, TimeSpan Delay, CompletionFailureInfo Failure);

internal sealed class GalateaCompletionRetryOptions {
    internal CancellationToken UserStopToken { get; init; }
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    internal TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMinutes(30);
    internal Action? InvocationStarted { get; init; }
    internal Func<int, CompletionStreamObserver?, CompletionStreamObserver?>? CreateAttemptObserver { get; init; }
    internal Action<GalateaCompletionRetryNotice>? RetryWaiting { get; init; }
    internal Action<int>? AttemptTimedOut { get; init; }
    internal Func<double> RandomSample { get; init; } = Random.Shared.NextDouble;
}

/// <summary>Retries only repeatable generation, never Journal commits or tool execution.</summary>
internal sealed class GalateaCompletionRetryClient : ICompletionClient {
    private readonly ICompletionClient _inner;
    private readonly GalateaCompletionRetryOptions _options;
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    internal GalateaCompletionRetryClient(ICompletionClient inner, GalateaCompletionRetryOptions options) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        ArgumentNullException.ThrowIfNull(options.RandomSample);
        if (options.AttemptTimeout <= TimeSpan.Zero || options.AttemptTimeout > MaximumTimerDelay) {
            throw new ArgumentOutOfRangeException(nameof(options), "Attempt timeout must be positive and representable by a timer.");
        }
    }

    public string Name => _inner.Name;
    public string ApiSpecId => _inner.ApiSpecId;

    public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default) => ExecuteAsync(request, null, observer, cancellationToken);

    public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(invocationOptions);
        return ExecuteAsync(request, invocationOptions, observer, cancellationToken);
    }

    private async Task<CompletionResult> ExecuteAsync(CompletionRequest request, CompletionInvocationOptions? invocationOptions,
        CompletionStreamObserver? observer, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfStopped(cancellationToken);
        _options.InvocationStarted?.Invoke();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _options.UserStopToken);
        int attempt = 0;
        while (true) {
            ThrowIfStopped(cancellationToken);
            attempt = attempt == int.MaxValue ? attempt : attempt + 1;
            var attemptObserver = _options.CreateAttemptObserver is { } create ? create(attempt, observer) : observer;
            CompletionFailureInfo failure;
            // Every await includes the underlying client's finally/disposal. A noncooperative
            // client retains ownership here: cancellation never permits a parallel replacement.
            using (var deadline = new CancellationTokenSource(_options.AttemptTimeout, _options.TimeProvider)) {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, deadline.Token);
                using var registration = deadline.Token.Register(() => {
                    if (!lifetime.IsCancellationRequested) {
                        try { _options.AttemptTimedOut?.Invoke(attempt); }
                        catch { /* A diagnostic callback cannot interrupt cancellation/draining. */ }
                    }
                });
                try {
                    var result = invocationOptions is null
                        ? await _inner.StreamCompletionAsync(request, attemptObserver, linked.Token).ConfigureAwait(false)
                        : await _inner.StreamCompletionAsync(request, invocationOptions, attemptObserver, linked.Token).ConfigureAwait(false);
                    ThrowIfStopped(cancellationToken);
                    if (result.Termination.Kind != CompletionTerminationKind.Failed || result.Failure is not { } resultFailure
                        || !IsRetryable(resultFailure)) {
                        return result;
                    }
                    failure = resultFailure;
                }
                catch (OperationCanceledException error) when (error.CancellationToken == linked.Token
                    && deadline.IsCancellationRequested && !lifetime.IsCancellationRequested) {
                    failure = new CompletionFailureInfo(CompletionFailureKind.Transport);
                }
                catch (CompletionFailureException error) when (IsRetryable(error.Failure)) {
                    failure = error.Failure;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
                    ThrowIfStopped(cancellationToken);
                    throw;
                }
            }
            ThrowIfStopped(cancellationToken);
            var delay = GetRetryDelay(attempt, failure.RetryAfter, _options.RandomSample());
            _options.RetryWaiting?.Invoke(new(attempt, delay, failure));
            try {
                await DelayAsync(delay, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
                ThrowIfStopped(cancellationToken);
                throw;
            }
        }
    }

    private void ThrowIfStopped(CancellationToken caller) {
        caller.ThrowIfCancellationRequested();
        _options.UserStopToken.ThrowIfCancellationRequested();
    }

    private async Task DelayAsync(TimeSpan remaining, CancellationToken cancellationToken) {
        // Retry-After is a lower bound, even when larger than a platform timer can accept.
        while (remaining > TimeSpan.Zero) {
            var chunk = remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
            await Task.Delay(chunk, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            remaining -= chunk;
        }
    }

    internal static bool IsRetryable(CompletionFailureInfo failure) {
        if (failure.HttpStatusCode is 401 or 403) {
            return false;
        }
        if (failure.ProviderCode is { Length: > 0 } code) {
            return code is "rate_limit_exceeded" or "rate_limit_error" or "overloaded_error" or "overloaded"
                or "server_error" or "internal_error" or "internal_server_error" or "temporarily_unavailable"
                or "UNAVAILABLE" or "INTERNAL";
        }
        return failure.Kind switch {
            CompletionFailureKind.Transport => true,
            CompletionFailureKind.Http => failure.HttpStatusCode is 408 or 429 or 500 or 502 or 503 or 504,
            _ => false
        };
    }

    internal static TimeSpan GetRetryDelay(int attempt, TimeSpan? retryAfter, double randomSample) {
        if (!double.IsFinite(randomSample) || randomSample < 0 || randomSample > 1) {
            throw new ArgumentOutOfRangeException(nameof(randomSample));
        }
        double seconds = Math.Min(300, 5 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 6)));
        var delay = TimeSpan.FromSeconds(seconds * (1 + 0.2 * randomSample));
        return retryAfter is { } minimum && minimum > delay ? minimum : delay;
    }
}
