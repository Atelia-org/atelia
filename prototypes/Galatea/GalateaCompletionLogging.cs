using Atelia.Completion;
using Atelia.Completion.Abstractions;

namespace Atelia.Galatea.Server;

/// <summary>
/// Opt-in Completion call logging owned by the Galatea composition root. The
/// wrapper preserves the inner client's Name and ApiSpecId, so durable
/// completion-target identity remains independent of logging configuration.
/// </summary>
internal static class GalateaCompletionLogging {
    internal static ICompletionClientFactory CreateOwnedFactory(
        ICompletionClientFactory inner,
        string? callLogDirectory
    ) {
        ArgumentNullException.ThrowIfNull(inner);
        return callLogDirectory is null
            ? inner
            : new OwnedLoggingCompletionClientFactory(
                inner,
                callLogDirectory
            );
    }

}

internal sealed class OwnedLoggingCompletionClientFactory(
    ICompletionClientFactory inner,
    string callLogDirectory
) : ICompletionClientFactory {
    public ICompletionClient Create(CompletionConnectionConfig connection) {
        ArgumentNullException.ThrowIfNull(connection);
        ICompletionClient created = inner.Create(connection);
        try {
            return new OwnedLoggingCompletionClient(
                created,
                connection,
                callLogDirectory
            );
        }
        catch {
            DisposeOwned(created);
            throw;
        }
    }

    private static void DisposeOwned(ICompletionClient client) {
        if (client is IAsyncDisposable asyncDisposable) {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else if (client is IDisposable disposable) {
            disposable.Dispose();
        }
    }
}

internal sealed class OwnedLoggingCompletionClient
    : ICompletionClient, IDisposable, IAsyncDisposable {
    private readonly ICompletionClient _owned;
    private readonly LoggingCompletionClient _logging;
    private int _disposed;

    internal OwnedLoggingCompletionClient(
        ICompletionClient owned,
        CompletionConnectionConfig connection,
        string callLogDirectory
    ) {
        _owned = owned ?? throw new ArgumentNullException(nameof(owned));
        _logging = new LoggingCompletionClient(
            owned,
            connection,
            Path.Combine(callLogDirectory, "completion"),
            new CompletionCallLogContext(Command: "galatea/completion-v9")
        );
    }

    public string Name => _logging.Name;
    public string ApiSpecId => _logging.ApiSpecId;

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => _logging.StreamCompletionAsync(
        request,
        observer,
        cancellationToken
    );

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => _logging.StreamCompletionAsync(
        request,
        invocationOptions,
        observer,
        cancellationToken
    );

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }
        if (_owned is IDisposable disposable) {
            disposable.Dispose();
        }
        else if (_owned is IAsyncDisposable asyncDisposable) {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }
        if (_owned is IAsyncDisposable asyncDisposable) {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_owned is IDisposable disposable) {
            disposable.Dispose();
        }
    }
}
