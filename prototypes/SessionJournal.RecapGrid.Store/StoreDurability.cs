using System.Runtime.InteropServices;

namespace Atelia.SessionJournal.RecapGrid.Store;

internal sealed record StoreStorageLimits(
    long MaximumDatabaseBytes,
    int MaximumCellCount,
    int MaximumRowViewCount,
    int MaximumRowViewMemberCount,
    int MaximumFulfilledViewCount,
    int MaximumCommitAttempts,
    int CommitRetryDelayMilliseconds
) {
    internal static StoreStorageLimits Production { get; } = new(
        RecapGridStoreLimits.MaximumDatabaseBytes,
        RecapGridStoreLimits.MaximumCellCount,
        RecapGridStoreLimits.MaximumRowViewCount,
        RecapGridStoreLimits.MaximumRowViewMemberCount,
        RecapGridStoreLimits.MaximumFulfilledViewCount,
        MaximumCommitAttempts: 4,
        CommitRetryDelayMilliseconds: 25
    );
}

internal sealed record StorePersistenceTestHooks(
    Action? BeforeCellBegin = null,
    Action? BeforeCellCommit = null,
    Action? AfterCellNativeCommitReturn = null,
    Action? AfterCellCommit = null,
    Action? BeforeRowViewBegin = null,
    Action? BeforeRowViewCommit = null,
    Action? AfterRowViewNativeCommitReturn = null,
    Action? AfterRowViewCommit = null,
    Action? BeforeFulfilledBegin = null,
    Action? BeforeFulfilledCommit = null,
    Action? AfterFulfilledNativeCommitReturn = null,
    Action? AfterFulfilledCommit = null,
    Action<string>? BeforeCreatePublish = null,
    Action<string>? AfterCreatePublish = null,
    Action<string>? BeforeResetPublish = null,
    Action<string>? AfterResetPublish = null,
    Action<int>? BeforeLocalCommitRetry = null
) {
    internal static StorePersistenceTestHooks None { get; } = new();
}

internal sealed class StorePaths {
    internal StorePaths(string repositoryPath) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "RecapGrid Store V1 supports Linux durable files only."
            );
        }
        CanonicalRepositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath)
        );
        RequireRepositoryChainSafe();
        RootPath = Path.Combine(
            CanonicalRepositoryPath,
            "derived",
            "recap-grid",
            "v1"
        );
        DatabasePath = Path.Combine(RootPath, "grid.sqlite");
        LifetimeLockPath = Path.Combine(RootPath, "lifetime.lock");
        RequireSafe(RootPath);
        RequireSafe(DatabasePath);
        RequireSafe(LifetimeLockPath);
    }

    internal string CanonicalRepositoryPath { get; }
    internal string RootPath { get; }
    internal string DatabasePath { get; }
    internal string LifetimeLockPath { get; }
    internal string JournalPath => DatabasePath + "-journal";
    internal string WalPath => DatabasePath + "-wal";
    internal string ShmPath => DatabasePath + "-shm";

    internal void RequireSafe(string path) {
        RequireRepositoryChainSafe();
        string full = Path.GetFullPath(path);
        string prefix = CanonicalRepositoryPath
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) {
            throw new StoreException(
                "GridStorePathEscapesRepository",
                "A RecapGrid Store path escapes its canonical repository."
            );
        }
        for (string? cursor = full;
             cursor is not null
                && !string.Equals(
                    cursor,
                    CanonicalRepositoryPath,
                    StringComparison.Ordinal);
             cursor = Path.GetDirectoryName(cursor)) {
            RequireExistingPathIsNotReparsePoint(cursor);
        }
    }

    private void RequireRepositoryChainSafe() {
        for (string? cursor = CanonicalRepositoryPath;
             cursor is not null;
             cursor = Path.GetDirectoryName(cursor)) {
            RequireExistingPathIsNotReparsePoint(cursor);
            string? parent = Path.GetDirectoryName(cursor);
            if (string.Equals(parent, cursor, StringComparison.Ordinal)) {
                break;
            }
        }
    }

    private static void RequireExistingPathIsNotReparsePoint(string path) {
        try {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) {
                throw new StoreException(
                    "GridStorePathReparsePoint",
                    "RecapGrid Store paths must not traverse links."
                );
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}

internal static class StoreDurableFiles {
    private const int LockShared = 1;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int ErrorWouldBlock = 11;
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenCloseOnExec = 0x80000;

    internal static FileStream AcquireShared(StorePaths paths)
        => Acquire(paths, LockShared, create: false);

    internal static FileStream AcquireExclusive(
        StorePaths paths,
        bool create
    ) => Acquire(paths, LockExclusive, create);

    internal static void EnsureSlots(StorePaths paths) {
        EnsureDirectory(paths, paths.RootPath);
        try {
            using var stream = new FileStream(
                paths.LifetimeLockPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.WriteThrough
            );
            stream.Flush(flushToDisk: true);
            FlushDirectory(paths.RootPath);
        }
        catch (IOException) when (RegularFileExists(
            paths,
            paths.LifetimeLockPath
        )) { }
    }

    internal static bool RegularFileExists(StorePaths paths, string path) {
        paths.RequireSafe(path);
        try {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory
                    | FileAttributes.ReparsePoint)) != 0) {
                throw new StoreException(
                    "GridStoreSlotInvalid",
                    "A RecapGrid Store exact slot is not a regular file."
                );
            }
            return true;
        }
        catch (FileNotFoundException) {
            return false;
        }
        catch (DirectoryNotFoundException) {
            return false;
        }
    }

    internal static void FlushDirectory(string path) {
        int descriptor = Open(
            path,
            OpenReadOnly | OpenDirectory | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw new IOException(
                "Failed to open a RecapGrid Store directory for fsync."
            );
        }
        try {
            if (Fsync(descriptor) != 0) {
                throw new IOException(
                    "Failed to fsync a RecapGrid Store directory."
                );
            }
        }
        finally {
            Close(descriptor);
        }
    }

    internal static RecapGridStorePhysicalWitness ComputeWitness(
        StorePaths paths,
        StoreStorageLimits limits
    ) {
        paths.RequireSafe(paths.DatabasePath);
        using var stream = new FileStream(
            paths.DatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan
        );
        if (stream.Length is < 1 || stream.Length > limits.MaximumDatabaseBytes) {
            throw new StoreLimitException("MaximumDatabaseBytes");
        }
        long length = stream.Length;
        string digest = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(stream)
        );
        if (stream.Length != length) {
            throw new StoreException(
                "GridStoreChangedDuringWitness",
                "The RecapGrid Store changed while its witness was captured."
            );
        }
        return new RecapGridStorePhysicalWitness(length, digest);
    }

    private static FileStream Acquire(
        StorePaths paths,
        int operation,
        bool create
    ) {
        if (create) {
            EnsureSlots(paths);
        }
        paths.RequireSafe(paths.LifetimeLockPath);
        FileStream stream;
        try {
            stream = new FileStream(
                paths.LifetimeLockPath,
                FileMode.Open,
                operation == LockExclusive
                    ? FileAccess.ReadWrite
                    : FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.None
            );
        }
        catch (IOException exception) {
            throw new StoreException(
                "GridStoreLockIoInvalid",
                "The RecapGrid Store lifetime lock could not be opened.",
                exception
            );
        }
        if (Flock(
                stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
                operation | LockNonBlocking
            ) == 0) {
            return stream;
        }
        int error = Marshal.GetLastPInvokeError();
        stream.Dispose();
        if (error == ErrorWouldBlock) {
            throw new StoreBusyException();
        }
        throw new StoreException(
            "GridStoreLockInvalid",
            $"The RecapGrid Store lifetime lock failed with errno {error}."
        );
    }

    private static void EnsureDirectory(StorePaths paths, string path) {
        if (Directory.Exists(path)) {
            paths.RequireSafe(path);
            return;
        }
        string? parent = Path.GetDirectoryName(path);
        if (parent is null) {
            throw new StoreException(
                "GridStoreDirectoryInvalid",
                "A RecapGrid Store directory has no parent."
            );
        }
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                paths.CanonicalRepositoryPath,
                StringComparison.Ordinal)
            && !Directory.Exists(parent)) {
            throw new StoreException(
                "GridStoreRepositoryAbsent",
                "The canonical repository directory is absent."
            );
        }
        if (!Directory.Exists(parent)) {
            EnsureDirectory(paths, parent);
        }
        Directory.CreateDirectory(path);
        paths.RequireSafe(path);
        FlushDirectory(path);
        FlushDirectory(parent);
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fileDescriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);
}

internal sealed class StoreLifetime : IDisposable {
    private readonly object _gate = new();
    private readonly IDisposable _lease;
    private int _activeOperations;
    private bool _closing;
    private bool _disposed;

    internal StoreLifetime(IDisposable lease) => _lease = lease;

    internal Operation? TryEnter() {
        lock (_gate) {
            if (_closing || _disposed) {
                return null;
            }
            _activeOperations = checked(_activeOperations + 1);
            return new Operation(this);
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _closing = true;
            while (_activeOperations != 0) {
                Monitor.Wait(_gate);
            }
            _lease.Dispose();
            _disposed = true;
        }
    }

    private void Exit() {
        lock (_gate) {
            _activeOperations--;
            if (_activeOperations == 0) {
                Monitor.PulseAll(_gate);
            }
        }
    }

    internal sealed class Operation : IDisposable {
        private StoreLifetime? _owner;
        internal Operation(StoreLifetime owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}

internal sealed class StoreBusyException : Exception;
internal sealed class StoreLimitException(string limit) : IOException(limit) {
    internal string Limit { get; } = limit;
}
internal sealed class StoreUnsupportedSchemaException(int schemaVersion)
    : IOException($"Unsupported RecapGrid Store schema {schemaVersion}.") {
    internal int SchemaVersion { get; } = schemaVersion;
}
internal sealed class StoreCommitIndeterminateException(Exception inner)
    : Exception("A RecapGrid Store commit completed but settlement failed.", inner);
internal sealed class StorePublishIndeterminateException(Exception inner)
    : Exception("A RecapGrid Store file was published but fsync failed.", inner);
internal sealed class StoreException(
    string code,
    string message,
    Exception? inner = null
) : IOException(message, inner) {
    internal string Code { get; } = code;
}
