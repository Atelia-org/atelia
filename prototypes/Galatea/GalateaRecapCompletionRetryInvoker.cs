using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;
using Atelia.SessionJournal.RecapGrid.Runtime;

namespace Atelia.Galatea.Server;

/// <summary>Only Recap maintenance generation; it never owns the borrowed provider or cell settlement.</summary>
internal sealed class GalateaRecapCompletionRetryInvoker : IRecapCompletionAttemptDeadlineInvoker {
    private readonly GalateaCompletionRetryClient _client;

    internal GalateaRecapCompletionRetryInvoker(string connectionId, ICompletionClient inner,
        TimeSpan attemptTimeout, TimeProvider timeProvider) {
        if (attemptTimeout <= TimeSpan.Zero || attemptTimeout > TimeSpan.FromDays(1)) {
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        }
        AttemptTimeout = attemptTimeout;
        _client = new(inner, new() {
            AttemptTimeout = attemptTimeout,
            TimeProvider = timeProvider,
            RetryWaiting = notice => DebugUtil.Warning("Galatea.Completion",
                $"Recap maintenance generation retry: connection={connectionId}, attempt={notice.Attempt}, kind={notice.Failure.Kind}, delay={notice.Delay}."),
            AttemptTimedOut = attempt => DebugUtil.Warning("Galatea.Completion",
                $"Recap maintenance attempt deadline; awaiting cleanup: connection={connectionId}, attempt={attempt}."),
        });
    }

    public TimeSpan AttemptTimeout { get; }
    public string ProviderId => _client.Name;
    public string ApiSpecId => _client.ApiSpecId;
    public async ValueTask<CompletionResult> InvokeAsync(CompletionRequest request,
        CompletionInvocationOptions invocationOptions, CancellationToken cancellationToken) =>
        await _client.StreamCompletionAsync(request, invocationOptions, null, cancellationToken).ConfigureAwait(false);
}
