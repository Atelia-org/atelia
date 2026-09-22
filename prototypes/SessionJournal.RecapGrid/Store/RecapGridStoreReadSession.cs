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
/// connection. Only the connection is reused between calls: every read runs
/// in one short, per-read deferred snapshot and ends that snapshot before the
/// call returns. Each read re-checks the store instance identity on the cached
/// connection and latches the whole store invalid on mismatch. No transaction
/// crosses logical reads or awaits. Not thread-safe: a session must be
/// accessed sequentially by one operation and disposed by its owner; an async
/// continuation may resume that operation on a different thread only after
/// its prior read has returned.
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

    internal RecapGridStoreReadResult<RowWork> ReadRowWork(RowWorkKey key) {
        ArgumentNullException.ThrowIfNull(key);
        return Execute(
            connection => SqliteRecapGridStore.ReadRowWorkCore(
                connection,
                transaction: null,
                key
            )
        );
    }

    internal RecapGridMissingResult FindMissingAssignments(RowBuildSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);
        return Execute(
            connection => SqliteRecapGridStore.FindMissingCore(
                connection,
                transaction: null,
                spec
            )
        ) switch {
            RecapGridStoreReadResult<RecapGridMissingResult>.Found found
                => found.Value,
            RecapGridStoreReadResult<RecapGridMissingResult>.Busy
                => new RecapGridMissingResult.Busy(),
            RecapGridStoreReadResult<RecapGridMissingResult>.Disposed
                => new RecapGridMissingResult.Disposed(),
            RecapGridStoreReadResult<RecapGridMissingResult>.Invalid invalid
                => new RecapGridMissingResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => throw new InvalidOperationException(
                "The read session returned an unsupported missing outcome."
            )
        };
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
            T? value = ReadWithinShortSnapshot(connection, read);
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

    private T? ReadWithinShortSnapshot<T>(
        SqliteConnection connection,
        Func<SqliteConnection, T?> read
    ) where T : class {
        bool begun = false;
        try {
            SqliteRecapGridStore.ExecuteNativeControl(
                connection,
                "BEGIN DEFERRED;"
            );
            begun = true;
            // A constant SELECT can be optimized without acquiring a shared
            // lock. This exact-table read establishes it before any managed
            // provider command, whose zero command timeout otherwise retries
            // a busy prepare indefinitely.
            SqliteRecapGridStore.ExecuteNativeControl(
                connection,
                "SELECT singleton FROM store_metadata WHERE singleton = 1 LIMIT 1;"
            );
            RecapGridStoreIdentity fresh = SqliteRecapGridStore.ReadIdentity(
                connection,
                transaction: null
            );
            if (fresh != _identity) {
                throw new StoreException(
                    "GridStoreInstanceIdMismatch",
                    "Store identity changed under the read session."
                );
            }
            return read(connection);
        }
        finally {
            if (begun
                && SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0) {
                try {
                    SqliteRecapGridStore.ExecuteNativeControl(
                        connection,
                        "ROLLBACK;"
                    );
                }
                catch (Exception exception) when (
                    SqliteRecapGridStore.IsStoreFailure(exception)
                ) {
                    var failure = new StoreException(
                        "GridStoreReadSnapshotCleanupFailed",
                        "The Store read snapshot could not be ended.",
                        exception
                    );
                    _store.LatchInvalid(failure);
                    _connection = null;
                    try {
                        connection.Dispose();
                    }
                    catch (Exception disposal) when (
                        SqliteRecapGridStore.IsStoreFailure(disposal)
                    ) {
                        // The Store is already invalid and this connection
                        // detached; preserve the original cleanup failure.
                    }
                    throw failure;
                }
            }
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
