using System.Runtime.InteropServices;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Control;

internal sealed class ControlPaths {
    internal ControlPaths(
        string repositoryPath,
        RefId refId,
        TimelineId timelineId
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "RecapGrid Control V1 supports Linux durable files only."
            );
        }
        if (refId.IsDefault) {
            throw new ArgumentException("RefId must not be default.", nameof(refId));
        }
        CanonicalRepositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath)
        );
        RefId = refId;
        TimelineId = timelineId;
        DirectoryPath = Path.Combine(
            CanonicalRepositoryPath,
            "control",
            "recap-grid",
            "v1",
            "refs",
            refId.ToHexString(),
            "timelines",
            timelineId.Value
        );
        StatePath = Path.Combine(DirectoryPath, "control.json");
        LifetimeLockPath = Path.Combine(DirectoryPath, "lifetime.lock");
        WriterLockPath = Path.Combine(DirectoryPath, "writer.lock");
        RequireSafe(DirectoryPath);
        RequireSafe(StatePath);
        RequireSafe(LifetimeLockPath);
        RequireSafe(WriterLockPath);
    }

    internal string CanonicalRepositoryPath { get; }
    internal RefId RefId { get; }
    internal TimelineId TimelineId { get; }
    internal string DirectoryPath { get; }
    internal string StatePath { get; }
    internal string LifetimeLockPath { get; }
    internal string WriterLockPath { get; }

    internal void RequireSafe(string path) {
        string full = Path.GetFullPath(path);
        string prefix = CanonicalRepositoryPath
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) {
            throw new ControlStoreException(
                "ControlPathEscapesRepository",
                "A Control durable path escapes its canonical repository."
            );
        }
        for (string? cursor = full;
             cursor is not null
                && cursor.StartsWith(prefix, StringComparison.Ordinal);
             cursor = Path.GetDirectoryName(cursor)) {
            if ((File.Exists(cursor) || Directory.Exists(cursor))
                && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) {
                throw new ControlStoreException(
                    "ControlPathReparsePoint",
                    "Control durable paths must not traverse links."
                );
            }
        }
    }
}

internal static class ControlDurableFiles {
    private const int LockShared = 1;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int ErrorWouldBlock = 11;
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenCloseOnExec = 0x80000;

    internal static FileStream AcquireSharedLifetime(ControlPaths paths)
        => Acquire(paths, paths.LifetimeLockPath, LockShared, create: false);

    internal static FileStream AcquireExclusiveLifetime(
        ControlPaths paths,
        bool create
    ) => Acquire(paths, paths.LifetimeLockPath, LockExclusive, create);

    internal static FileStream AcquireWriter(
        ControlPaths paths,
        bool create
    ) => Acquire(paths, paths.WriterLockPath, LockExclusive, create);

    internal static void EnsureSlots(ControlPaths paths) {
        EnsureDirectory(paths, paths.DirectoryPath);
        EnsureLockFile(paths, paths.LifetimeLockPath);
        EnsureLockFile(paths, paths.WriterLockPath);
    }

    internal static bool StateExists(ControlPaths paths) {
        paths.RequireSafe(paths.StatePath);
        try {
            FileAttributes attributes = File.GetAttributes(paths.StatePath);
            if ((attributes & (FileAttributes.Directory
                    | FileAttributes.ReparsePoint)) != 0) {
                throw new ControlStoreException(
                    "ControlStateSlotInvalid",
                    "The canonical Control state slot is not a regular file."
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
        catch (ControlStoreException) {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException) {
            throw new ControlStoreException(
                "ControlStateSlotIoInvalid",
                "The canonical Control state slot could not be inspected.",
                exception
            );
        }
    }

    internal static byte[] ReadState(ControlPaths paths) {
        paths.RequireSafe(paths.StatePath);
        using var stream = new FileStream(
            paths.StatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
        if (stream.Length is < 2
            or > ControlStorageLimits.MaximumStateCanonicalUtf8Bytes) {
            throw new ControlStoreException(
                "ControlStateLimitExceeded",
                "The Control state length exceeds its code-owned cap."
            );
        }
        int length = checked((int)stream.Length);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) {
            throw new ControlStoreException(
                "ControlStateChangedDuringRead",
                "The Control state changed during a bounded read."
            );
        }
        return bytes;
    }

    internal static void WriteState(
        ControlPaths paths,
        ReadOnlySpan<byte> bytes,
        bool createNew,
        ControlPersistenceTestHooks? hooks = null
    ) {
        hooks ??= ControlPersistenceTestHooks.None;
        if (bytes.Length > ControlStorageLimits.MaximumStateCanonicalUtf8Bytes) {
            throw new ControlLimitException("ControlStateBytes");
        }
        paths.RequireSafe(paths.StatePath);
        string temporary = Path.Combine(
            paths.DirectoryPath,
            $".control.json.{Guid.NewGuid():N}.tmp"
        );
        paths.RequireSafe(temporary);
        bool published = false;
        try {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough)) {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            hooks.BeforeStatePublish?.Invoke();
            if (createNew) {
                File.Move(temporary, paths.StatePath);
            }
            else {
                File.Move(temporary, paths.StatePath, overwrite: true);
            }
            published = true;
            hooks.AfterStatePublish?.Invoke(temporary);
            FlushDirectory(paths.DirectoryPath);
        }
        catch (Exception exception) when (published
            && exception is not ControlStatePublishIndeterminateException) {
            throw new ControlStatePublishIndeterminateException(exception);
        }
        finally {
            if (!published) {
                try {
                    File.Delete(temporary);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static void FlushDirectory(string path) {
        int descriptor = Open(
            path,
            OpenReadOnly | OpenDirectory | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw new IOException(
                "Failed to open a Control directory for durable flush."
            );
        }
        try {
            if (Fsync(descriptor) != 0) {
                throw new IOException(
                    "Failed to durably flush a Control directory."
                );
            }
        }
        finally {
            Close(descriptor);
        }
    }

    private static FileStream Acquire(
        ControlPaths paths,
        string path,
        int operation,
        bool create
    ) {
        paths.RequireSafe(path);
        FileStream stream;
        try {
            stream = new FileStream(
                path,
                create ? FileMode.OpenOrCreate : FileMode.Open,
                create ? FileAccess.ReadWrite : FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1,
                create ? FileOptions.WriteThrough : FileOptions.None
            );
        }
        catch (IOException exception)
            when (exception.HResult == ErrorWouldBlock) {
            throw new ControlBusyException();
        }
        catch (IOException exception) {
            throw new ControlStoreException(
                "ControlLockIoInvalid",
                "A Control lock slot could not be opened.",
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
            throw new ControlBusyException();
        }
        throw new ControlStoreException(
            "ControlLockInvalid",
            $"The Control lock failed with errno {error}."
        );
    }

    private static void EnsureLockFile(
        ControlPaths paths,
        string path
    ) {
        paths.RequireSafe(path);
        try {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.WriteThrough
            );
            stream.Flush(flushToDisk: true);
            FlushDirectory(paths.DirectoryPath);
        }
        catch (IOException) when (File.Exists(path)) { }
    }

    private static void EnsureDirectory(
        ControlPaths paths,
        string path
    ) {
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                paths.CanonicalRepositoryPath,
                StringComparison.Ordinal)) {
            if (!Directory.Exists(paths.CanonicalRepositoryPath)) {
                throw new ControlStoreException(
                    "ControlRepositoryAbsent",
                    "The canonical repository directory is absent."
                );
            }
            return;
        }
        paths.RequireSafe(path);
        if (Directory.Exists(path)) {
            return;
        }
        string? parent = Path.GetDirectoryName(path);
        if (parent is null) {
            throw new ControlStoreException(
                "ControlDirectoryInvalid",
                "A Control directory has no parent."
            );
        }
        if (!Directory.Exists(parent)) {
            EnsureDirectory(paths, parent);
        }
        Directory.CreateDirectory(path);
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

internal sealed class ControlBusyException : Exception;

internal sealed class ControlStatePublishIndeterminateException
    : Exception {
    internal ControlStatePublishIndeterminateException(Exception inner)
        : base(
            "The Control state was published but its durability confirmation failed.",
            inner
        ) { }
}

internal sealed class ControlLifetime : IDisposable {
    [ThreadStatic]
    private static Dictionary<ControlLifetime, int>? _entered;
    private readonly object _gate = new();
    private readonly IDisposable _controlLease;
    private readonly HistoryTimelineReaderHandle _timelineHandle;
    private int _activeOperations;
    private bool _closing;
    private bool _disposed;

    internal ControlLifetime(
        IDisposable controlLease,
        HistoryTimelineReaderHandle timelineHandle
    ) {
        _controlLease = controlLease;
        _timelineHandle = timelineHandle;
    }

    internal Operation? TryEnter() {
        lock (_gate) {
            bool reentrant = _entered?.ContainsKey(this) == true;
            if (_closing && !reentrant) {
                return null;
            }
            _activeOperations = checked(_activeOperations + 1);
            Dictionary<ControlLifetime, int> entered = _entered ??= [];
            entered[this] = entered.TryGetValue(this, out int count)
                ? checked(count + 1)
                : 1;
            return new Operation(this);
        }
    }

    public void Dispose() {
        bool enteredHere = _entered?.ContainsKey(this) == true;
        lock (_gate) {
            if (_closing) {
                if (enteredHere) {
                    return;
                }
                while (!_disposed) {
                    Monitor.Wait(_gate);
                }
                return;
            }
            _closing = true;
            if (enteredHere) {
                return;
            }
            while (_activeOperations != 0) {
                Monitor.Wait(_gate);
            }
            CompleteUnderLock();
        }
    }

    private void Exit() {
        Dictionary<ControlLifetime, int>? entered = _entered;
        if (entered is null || !entered.TryGetValue(this, out int count)) {
            throw new InvalidOperationException(
                "A Control operation exited on another thread."
            );
        }
        if (count == 1) {
            entered.Remove(this);
        }
        else {
            entered[this] = count - 1;
        }
        lock (_gate) {
            _activeOperations--;
            if (_activeOperations == 0) {
                if (_closing) {
                    CompleteUnderLock();
                }
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void CompleteUnderLock() {
        if (_disposed) {
            return;
        }
        _controlLease.Dispose();
        _timelineHandle.Dispose();
        _disposed = true;
        Monitor.PulseAll(_gate);
    }

    internal sealed class Operation : IDisposable {
        private ControlLifetime? _owner;

        internal Operation(ControlLifetime owner) {
            _owner = owner;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}
