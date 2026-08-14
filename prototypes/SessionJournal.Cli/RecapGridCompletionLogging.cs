using Atelia.Completion;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.Cli;

/// <summary>
/// Opt-in call logging for recap-grid build. The registry owns every client
/// returned by this factory, so the wrapper must preserve ownership of the
/// injected factory's client as well as its completion identity.
/// </summary>
internal sealed class RecapGridLoggingCompletionClientFactory(
    ICompletionClientFactory inner,
    string callLogDirectory
) : ICompletionClientFactory {
    public ICompletionClient Create(CompletionConnectionConfig connection) {
        ArgumentNullException.ThrowIfNull(connection);
        ICompletionClient created = inner.Create(connection);
        try {
            return new OwnedRecapGridLoggingCompletionClient(
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

internal sealed class OwnedRecapGridLoggingCompletionClient
    : ICompletionClient, IDisposable, IAsyncDisposable {
    private readonly ICompletionClient _owned;
    private readonly LoggingCompletionClient _logging;
    private int _disposed;

    internal OwnedRecapGridLoggingCompletionClient(
        ICompletionClient owned,
        CompletionConnectionConfig connection,
        string callLogDirectory
    ) {
        _owned = owned ?? throw new ArgumentNullException(nameof(owned));
        _logging = new LoggingCompletionClient(
            owned,
            connection,
            callLogDirectory,
            new CompletionCallLogContext(Command: "recap-grid/build")
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
