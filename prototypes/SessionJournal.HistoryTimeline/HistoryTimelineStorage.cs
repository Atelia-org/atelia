using System.ComponentModel;
using System.Runtime.InteropServices;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

internal sealed record HistoryTimelineStorageLimits(
    long MaximumDatabaseBytes,
    long MaximumRestoreCopyBytes,
    int MaximumPolicyCount,
    int MaximumRowCount,
    int MaximumTrieNodeCount,
    int MaximumPathPageRows,
    int MaximumPathPageUtf8Bytes,
    int BusyTimeoutMilliseconds
) {
    internal static HistoryTimelineStorageLimits Production { get; } = new(
        HistoryTimelineStoreLimits.MaximumDatabaseBytes,
        HistoryTimelineStoreLimits.MaximumRestoreCopyBytes,
        HistoryTimelineStoreLimits.MaximumPolicyCount,
        HistoryTimelineStoreLimits.MaximumRowCount,
        HistoryTimelineStoreLimits.MaximumTrieNodeCount,
        HistoryTimelineStoreLimits.MaximumPathPageRows,
        HistoryTimelineStoreLimits.MaximumPathPageUtf8Bytes,
        BusyTimeoutMilliseconds: 5_000
    );
}

internal sealed class HistoryTimelineStoreLimitException(
    string limit,
    string message
) : IOException(message) {
    internal string Limit { get; } = limit;
}

internal sealed class HistoryTimelineUnsupportedSchemaException(
    int schemaVersion
) : IOException(
    $"Unsupported HistoryTimeline schema version {schemaVersion}."
) {
    internal int SchemaVersion { get; } = schemaVersion;
}

internal sealed class HistoryTimelinePaths {
    internal HistoryTimelinePaths(string repositoryPath, RefId refId) {
        RepositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath)
        );
        RefId = HistoryTimelineSyntax.RequireRefId(refId);
        RootPath = Path.Combine(
            RepositoryPath,
            "derived",
            "history-timeline",
            "v1"
        );
        RefPath = Path.Combine(
            RootPath,
            "refs",
            refId.ToHexString()
        );
        TimelineRootPath = Path.Combine(RefPath, "timelines");
        LocatorPath = Path.Combine(RefPath, "locator.json");
        LockPath = Path.Combine(
            RootPath,
            "locks",
            $"{refId.ToHexString()}.lock"
        );
        HistoryTimelineDurableFiles.RequireSafePath(
            RepositoryPath,
            RepositoryPath
        );
    }

    internal string RepositoryPath { get; }
    internal RefId RefId { get; }
    internal string RootPath { get; }
    internal string RefPath { get; }
    internal string TimelineRootPath { get; }
    internal string LocatorPath { get; }
    internal string LockPath { get; }

    internal string TimelineDatabasePath(TimelineId timelineId)
        => Path.Combine(
            TimelineRootPath,
            $"{timelineId.Value}.sqlite"
        );

    internal void RequireAllSafe() {
        foreach (string path in new[] {
                     RootPath,
                     RefPath,
                     TimelineRootPath,
                     LocatorPath,
                     LockPath
                 }) {
            HistoryTimelineDurableFiles.RequireSafePath(
                RepositoryPath,
                path
            );
        }
    }
}

internal static class HistoryTimelineDurableFiles {
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenCloseOnExec = 0x80000;
    private const int LockShared = 1;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int ErrorWouldBlock = 11;
    private const int ErrorAlreadyExists = 17;

    internal static FileStream AcquireSharedExisting(
        HistoryTimelinePaths paths
    ) {
        paths.RequireAllSafe();
        FileStream stream;
        try {
            stream = new FileStream(
                paths.LockPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.None
            );
        }
        catch (IOException exception)
            when (IsNativeSharingContention(exception)) {
            throw new HistoryTimelineLeaseBusyException();
        }
        try {
            AcquireFlock(stream, LockShared);
            return stream;
        }
        catch {
            stream.Dispose();
            throw;
        }
    }

    internal static FileStream AcquireExclusive(
        HistoryTimelinePaths paths,
        bool create
    ) {
        paths.RequireAllSafe();
        if (create) {
            EnsureDirectoryDurable(
                paths.RepositoryPath,
                Path.GetDirectoryName(paths.LockPath)!
            );
        }
        FileStream stream;
        bool created = false;
        try {
            if (create) {
                try {
                    stream = new FileStream(
                        paths.LockPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite,
                        bufferSize: 1,
                        FileOptions.WriteThrough
                    );
                    created = true;
                }
                catch (IOException exception)
                    when (IsAlreadyExists(exception)) {
                    stream = OpenExistingLock(paths.LockPath);
                }
            }
            else {
                stream = OpenExistingLock(paths.LockPath);
            }
        }
        catch (IOException exception)
            when (IsNativeSharingContention(exception)) {
            throw new HistoryTimelineLeaseBusyException();
        }
        try {
            if (created) {
                stream.Flush(flushToDisk: true);
                FlushDirectory(Path.GetDirectoryName(paths.LockPath)!);
            }
            AcquireFlock(stream, LockExclusive);
            return stream;
        }
        catch {
            stream.Dispose();
            throw;
        }
    }

    private static FileStream OpenExistingLock(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize: 1,
            FileOptions.None
        );

    internal static void EnsureDirectoryDurable(
        string repositoryPath,
        string path
    ) {
        RequireSafePath(repositoryPath, path);
        if (Directory.Exists(path)) {
            RequireSafePath(repositoryPath, path);
            return;
        }
        string parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException(
                $"Directory has no parent: {path}"
            );
        if (!Directory.Exists(parent)) {
            EnsureDirectoryDurable(repositoryPath, parent);
        }
        Directory.CreateDirectory(path);
        RequireSafePath(repositoryPath, path);
        FlushDirectory(path);
        FlushDirectory(parent);
    }

    internal static void WriteCreateNew(
        string repositoryPath,
        string finalPath,
        ReadOnlySpan<byte> bytes,
        Action? beforePublish = null,
        Action? afterPublish = null
    ) {
        string directory = Path.GetDirectoryName(finalPath)!;
        EnsureDirectoryDurable(repositoryPath, directory);
        RequireSafePath(repositoryPath, finalPath);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp"
        );
        try {
            WriteTemporary(repositoryPath, temporaryPath, bytes);
            beforePublish?.Invoke();
            File.Move(temporaryPath, finalPath, overwrite: false);
            FlushDirectory(directory);
            afterPublish?.Invoke();
        }
        finally {
            TryDelete(temporaryPath);
        }
    }

    internal static void WriteAtomicReplace(
        string repositoryPath,
        string finalPath,
        ReadOnlySpan<byte> bytes,
        Action? beforePublish = null,
        Action? afterPublish = null
    ) {
        string directory = Path.GetDirectoryName(finalPath)!;
        EnsureDirectoryDurable(repositoryPath, directory);
        RequireSafePath(repositoryPath, finalPath);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp"
        );
        try {
            WriteTemporary(repositoryPath, temporaryPath, bytes);
            beforePublish?.Invoke();
            File.Move(temporaryPath, finalPath, overwrite: true);
            FlushDirectory(directory);
            afterPublish?.Invoke();
        }
        finally {
            TryDelete(temporaryPath);
        }
    }

    internal static byte[] ReadBounded(
        string repositoryPath,
        string path,
        int maximumBytes
    ) {
        RequireSafePath(repositoryPath, path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
        if (stream.Length is < 1 || stream.Length > maximumBytes) {
            throw new InvalidDataException(
                $"File '{path}' exceeds its canonical byte bound."
            );
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        RequireSafePath(repositoryPath, path);
        return bytes;
    }

    internal static bool ExistsExact(
        string repositoryPath,
        string path
    ) {
        RequireSafePath(repositoryPath, path);
        try {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) {
                throw new InvalidDataException(
                    $"Expected a file but found a directory: {path}"
                );
            }
            RequireSafePath(repositoryPath, path);
            return true;
        }
        catch (FileNotFoundException) {
            return false;
        }
        catch (DirectoryNotFoundException) {
            RequireMissingParentsAreDirectories(repositoryPath, path);
            return false;
        }
    }

    private static void RequireMissingParentsAreDirectories(
        string repositoryPath,
        string path
    ) {
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath)
        );
        string? cursor = Path.GetDirectoryName(Path.GetFullPath(path));
        while (cursor is not null) {
            try {
                FileAttributes attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.Directory) == 0) {
                    throw new InvalidDataException(
                        $"HistoryTimeline path parent is not a directory: {cursor}"
                    );
                }
                return;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (string.Equals(
                    cursor,
                    root,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)) {
                return;
            }
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    internal static void RequireSafePath(
        string repositoryPath,
        string path
    ) {
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath)
        );
        string candidate = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, comparison)
            && !candidate.StartsWith(prefix, comparison)) {
            throw new InvalidDataException(
                $"HistoryTimeline path escapes repository: {candidate}"
            );
        }
        string? cursor = candidate;
        while (cursor is not null) {
            if (File.Exists(cursor) || Directory.Exists(cursor)) {
                FileAttributes attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new InvalidDataException(
                        $"HistoryTimeline path contains a reparse point: {cursor}"
                    );
                }
            }
            if (string.Equals(cursor, root, comparison)) {
                break;
            }
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    internal static void FlushDirectory(string path) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "HistoryTimeline durability is currently verified only on Linux."
            );
        }
        int descriptor = NativeOpen(
            path,
            OpenReadOnly | OpenDirectory | OpenCloseOnExec,
            0
        );
        if (descriptor < 0) {
            throw NativeIOException(
                $"Failed to open directory for fsync: {path}"
            );
        }
        try {
            if (NativeFsync(descriptor) != 0) {
                throw NativeIOException(
                    $"Failed to fsync directory: {path}"
                );
            }
        }
        finally {
            _ = NativeClose(descriptor);
        }
    }

    private static void WriteTemporary(
        string repositoryPath,
        string temporaryPath,
        ReadOnlySpan<byte> bytes
    ) {
        RequireSafePath(repositoryPath, temporaryPath);
        using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough
        );
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IOException NativeIOException(string message)
        => new(
            message,
            new Win32Exception(Marshal.GetLastPInvokeError())
        );

    private static void AcquireFlock(FileStream stream, int operation) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "HistoryTimeline leases are currently verified only on Linux."
            );
        }
        int descriptor = checked((int)stream.SafeFileHandle
            .DangerousGetHandle());
        if (NativeFlock(
                descriptor,
                operation | LockNonBlocking
            ) == 0) {
            return;
        }
        int error = Marshal.GetLastPInvokeError();
        if (error == ErrorWouldBlock) {
            throw new HistoryTimelineLeaseBusyException();
        }
        throw NativeIOException(
            "Failed to acquire the HistoryTimeline lifecycle lease."
        );
    }

    private static bool IsNativeSharingContention(IOException exception) {
        int nativeCode = exception.HResult & 0xFFFF;
        return nativeCode is ErrorWouldBlock or 32 or 33;
    }

    private static bool IsAlreadyExists(IOException exception) {
        int nativeCode = exception.HResult & 0xFFFF;
        return nativeCode is ErrorAlreadyExists or 80 or 183;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen(
        string path,
        int flags,
        int mode
    );

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int NativeFsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int descriptor);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int NativeFlock(
        int descriptor,
        int operation
    );
}

internal sealed class HistoryTimelineLeaseBusyException : IOException { }
