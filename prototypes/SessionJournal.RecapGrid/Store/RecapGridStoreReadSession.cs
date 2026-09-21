using Microsoft.Data.Sqlite;

namespace Atelia.SessionJournal.RecapGrid.Store;

internal abstract record RecapGridStoreSessionOpenResult {
    private RecapGridStoreSessionOpenResult() { }
    public sealed record Opened(RecapGridStoreReadSession Session)
        : RecapGridStoreSessionOpenResult;
    public sealed record Busy : RecapGridStoreSessionOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridStoreSessionOpenResult;
    public sealed record Disposed : RecapGridStoreSessionOpenResult;
}

/// <summary>
/// Operation-scoped verified read session over a single RecapGrid Store
/// connection. Connection reuse only — never a transaction: every read runs
/// in autocommit mode exactly like a fresh-open read. Each read re-checks the
/// store instance identity on the cached connection and latches the whole
/// store invalid on mismatch. Not thread-safe: a session must be used from a
/// single operation thread and disposed by its owner.
/// </summary>
internal sealed class RecapGridStoreReadSession : IDisposable {
    private readonly SqliteRecapGridStore _store;
    private readonly StoreLifetime _lifetime;
    private readonly RecapGridStoreIdentity _identity;
    private SqliteConnection? _connection;
    private bool _disposed;

    private RecapGridStoreReadSession(
        SqliteRecapGridStore store,
        StoreLifetime lifetime,
        SqliteConnection connection,
        RecapGridStoreIdentity identity
    ) {
        _store = store;
        _lifetime = lifetime;
        _connection = connection;
        _identity = identity;
    }

    internal static RecapGridStoreSessionOpenResult Open(
        SqliteRecapGridStore store,
        StoreLifetime lifetime
    ) {
        using StoreLifetime.Operation? operation = lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridStoreSessionOpenResult.Disposed();
        }
        if (store.TryInvalid(out string code, out string detail)) {
            return new RecapGridStoreSessionOpenResult.Invalid(code, detail);
        }
        try {
            (
                SqliteConnection connection,
                RecapGridStoreIdentity identity
            ) = store.OpenVerifiedConnectionCore();
            return new RecapGridStoreSessionOpenResult.Opened(
                new RecapGridStoreReadSession(
                    store,
                    lifetime,
                    connection,
                    identity
                )
            );
        }
        catch (SqliteException exception)
            when (SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreSessionOpenResult.Busy();
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)
        ) {
            (code, detail) = store.LatchInvalid(exception);
            return new RecapGridStoreSessionOpenResult.Invalid(code, detail);
        }
    }

    internal RecapGridStoreReadResult<RecapRowView> ReadViewAt(
        RowViewAssignmentKey key
    ) {
        ArgumentNullException.ThrowIfNull(key);
        return Execute(
            connection => SqliteRecapGridStore.ReadRowViewAtCore(
                connection,
                transaction: null,
                key
            )
        );
    }

    private RecapGridStoreReadResult<T> Execute<T>(
        Func<SqliteConnection, T?> read
    ) where T : class {
        using StoreLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null || _disposed) {
            return new RecapGridStoreReadResult<T>.Disposed();
        }
        if (_store.TryInvalid(out string code, out string detail)) {
            return new RecapGridStoreReadResult<T>.Invalid(code, detail);
        }
        try {
            SqliteConnection connection = _connection!;
            RecapGridStoreIdentity fresh = SqliteRecapGridStore.ReadIdentity(
                connection,
                transaction: null
            );
            if (fresh != _identity) {
                (code, detail) = _store.LatchInvalid(
                    new StoreException(
                        "GridStoreInstanceIdMismatch",
                        "Store identity changed under the read session."
                    )
                );
                return new RecapGridStoreReadResult<T>.Invalid(code, detail);
            }
            T? value = read(connection);
            return value is null
                ? new RecapGridStoreReadResult<T>.Missing()
                : new RecapGridStoreReadResult<T>.Found(value);
        }
        catch (SqliteException exception)
            when (SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreReadResult<T>.Busy();
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)
        ) {
            (code, detail) = _store.LatchInvalid(exception);
            return new RecapGridStoreReadResult<T>.Invalid(code, detail);
        }
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        _connection?.Dispose();
        _connection = null;
    }
}
