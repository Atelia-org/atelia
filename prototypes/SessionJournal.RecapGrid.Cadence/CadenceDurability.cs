using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.RecapGrid.Cadence;

internal sealed class CadencePaths {
    internal CadencePaths(string repositoryPath, RefId refId) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "RecapGrid Cadence V1 supports Linux durable files only.");
        }
        if (refId.IsDefault) {
            throw new ArgumentException("RefId must not be default.", nameof(refId));
        }
        RepositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath));
        RefId = refId;
        DirectoryPath = Path.Combine(RepositoryPath, "control", "recap-grid",
            "v1", "refs", refId.ToHexString());
        StatePath = Path.Combine(DirectoryPath, "cadence.json");
        LockPath = Path.Combine(DirectoryPath, "cadence.lock");
        LinuxCadenceFiles.RequireExistingDirectoryChain(RepositoryPath);
    }

    internal string RepositoryPath { get; }
    internal RefId RefId { get; }
    internal string DirectoryPath { get; }
    internal string StatePath { get; }
    internal string LockPath { get; }
}

internal static class LinuxCadenceFiles {
    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenNonBlocking = 0x800;
    private const int OpenDirectoryFlag = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int LockShared = 1;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int ErrorNoEntry = 2;
    private const int ErrorAlreadyExists = 17;
    private const int ErrorWouldBlock = 11;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFileType = 0x8000;
    private const uint DirectoryType = 0x4000;

    internal static void RequireExistingDirectoryChain(string path) {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture is not (
                Architecture.X64 or Architecture.Arm64)) {
            throw new PlatformNotSupportedException(
                "RecapGrid Cadence requires the supported Linux stat ABI.");
        }
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.GetPathRoot(full)!;
        string current = root;
        foreach (string part in full[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries)) {
            current = Path.Combine(current, part);
            using SafeFileHandle handle = OpenDirectory(current);
        }
    }

    internal static void EnsureSlots(CadencePaths paths) {
        string relative = Path.GetRelativePath(paths.RepositoryPath,
            paths.DirectoryPath);
        string current = paths.RepositoryPath;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries)) {
            string next = Path.Combine(current, part);
            if (!TryOpenDirectory(next, out SafeFileHandle? existing)) {
                Directory.CreateDirectory(next);
                using SafeFileHandle created = OpenDirectory(next);
                FlushDirectory(next);
                FlushDirectory(current);
            }
            else {
                existing!.Dispose();
            }
            current = next;
        }
        using (FileStream? created = TryCreateRegularFile(paths.LockPath)) {
            if (created is null) {
                // Another creator won the create-new race. Validate the exact
                // canonical slot; the subsequent flock decides Busy versus an
                // already-created Cadence state.
                _ = EntryExists(paths.LockPath);
                return;
            }
            created.Flush(flushToDisk: true);
            FlushDirectory(paths.DirectoryPath);
        }
    }

    internal static bool EntryExists(string path) {
        int descriptor = Open(path,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoEntry) {
                return false;
            }
            throw Io("CadenceSlotOpenInvalid", path, error);
        }
        try {
            FileIdentity identity = ReadIdentity(descriptor, path);
            if (identity.FileType != RegularFileType) {
                throw new CadenceStoreException(
                    "CadenceSlotShapeInvalid",
                    "A canonical Cadence slot is not a regular file.");
            }
            return true;
        }
        finally {
            _ = Close(descriptor);
        }
    }

    internal static FileStream AcquireLock(string path, bool exclusive) {
        int descriptor = Open(path,
            OpenReadWrite | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            throw Io("CadenceLockOpenInvalid", path,
                Marshal.GetLastPInvokeError());
        }
        try {
            if (ReadIdentity(descriptor, path).FileType != RegularFileType) {
                throw new CadenceStoreException(
                    "CadenceLockShapeInvalid",
                    "The canonical Cadence lock is not a regular file.");
            }
            if (Flock(descriptor,
                    (exclusive ? LockExclusive : LockShared) | LockNonBlocking)
                != 0) {
                int error = Marshal.GetLastPInvokeError();
                if (error == ErrorWouldBlock) {
                    throw new CadenceBusyException();
                }
                throw Io("CadenceLockInvalid", path, error);
            }
            var handle = new SafeFileHandle(new IntPtr(descriptor), true);
            descriptor = -1;
            return new FileStream(handle,
                exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                bufferSize: 1, isAsync: false);
        }
        finally {
            if (descriptor >= 0) {
                _ = Close(descriptor);
            }
        }
    }

    internal static byte[] ReadBounded(string path) {
        int descriptor = Open(path,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            throw Io("CadenceStateOpenInvalid", path,
                Marshal.GetLastPInvokeError());
        }
        try {
            if (ReadIdentity(descriptor, path).FileType != RegularFileType) {
                throw new CadenceStoreException(
                    "CadenceStateShapeInvalid",
                    "The canonical Cadence state is not a regular file.");
            }
            var handle = new SafeFileHandle(new IntPtr(descriptor), true);
            descriptor = -1;
            using var stream = new FileStream(handle, FileAccess.Read,
                bufferSize: 4096, isAsync: false);
            if (stream.Length is < 2
                or > RecapGridCadenceLimits.MaximumCanonicalUtf8Bytes) {
                throw new CadenceStoreException(
                    "CadenceStateLimitExceeded",
                    "Cadence state exceeds its code-owned byte bound.");
            }
            byte[] bytes = GC.AllocateUninitializedArray<byte>(
                checked((int)stream.Length));
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) {
                throw new CadenceStoreException(
                    "CadenceStateChangedDuringRead",
                    "Cadence state changed during a bounded read.");
            }
            return bytes;
        }
        finally {
            if (descriptor >= 0) {
                _ = Close(descriptor);
            }
        }
    }

    internal static void WriteAtomic(
        CadencePaths paths,
        ReadOnlySpan<byte> bytes,
        bool createNew,
        CadencePersistenceTestHooks hooks
    ) {
        if (bytes.Length > RecapGridCadenceLimits.MaximumCanonicalUtf8Bytes) {
            throw new CadenceLimitException("CadenceCanonicalBytes");
        }
        string temporary = Path.Combine(paths.DirectoryPath,
            $".cadence.json.{Guid.NewGuid():N}.tmp");
        FileIdentity? temporaryIdentity = null;
        FileStream? temporaryStream = null;
        bool published = false;
        try {
            temporaryStream = CreateRegularFile(temporary);
            temporaryStream.Write(bytes);
            temporaryStream.Flush(flushToDisk: true);
            temporaryIdentity = ReadIdentity(
                temporaryStream.SafeFileHandle.DangerousGetHandle().ToInt32(),
                temporary);
            hooks.BeforePublish?.Invoke(temporary);
            RequirePathIdentity(temporary, temporaryIdentity.Value);
            File.Move(temporary, paths.StatePath, overwrite: !createNew);
            published = true;
            hooks.AfterPublish?.Invoke(temporary);
            FlushDirectory(paths.DirectoryPath);
        }
        catch (Exception exception) when (published
            && !CadenceError.IsFatal(exception)) {
            throw new CadencePublishIndeterminateException(exception);
        }
        finally {
            try {
                if (!published
                    && temporaryIdentity is { } identity
                    && temporaryStream is not null) {
                    TryDeleteOwnedTemporary(
                        temporary, identity, temporaryStream);
                }
            }
            finally {
                temporaryStream?.Dispose();
            }
        }
    }

    internal static void FlushDirectory(string path) {
        using SafeFileHandle handle = OpenDirectory(path);
        if (Fsync(handle.DangerousGetHandle().ToInt32()) != 0) {
            throw Io("CadenceDirectoryFlushInvalid", path,
                Marshal.GetLastPInvokeError());
        }
    }

    private static FileStream CreateRegularFile(string path) {
        return TryCreateRegularFile(path)
            ?? throw new CadenceStoreException(
                "CadenceFileAlreadyExists",
                "A create-new Cadence durable file already exists.");
    }

    private static FileStream? TryCreateRegularFile(string path) {
        int descriptor = OpenWithMode(path,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow
                | OpenCloseOnExec,
            Convert.ToUInt32("600", 8));
        if (descriptor < 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorAlreadyExists) {
                return null;
            }
            throw Io("CadenceFileCreateInvalid", path, error);
        }
        if (ReadIdentity(descriptor, path).FileType != RegularFileType) {
            _ = Close(descriptor);
            throw new CadenceStoreException(
                "CadenceFileShapeInvalid",
                "A newly-created Cadence slot is not a regular file.");
        }
        return new FileStream(
            new SafeFileHandle(new IntPtr(descriptor), true),
            FileAccess.Write, bufferSize: 4096, isAsync: false);
    }

    private static bool TryOpenDirectory(
        string path,
        out SafeFileHandle? handle
    ) {
        int descriptor = Open(path,
            OpenReadOnly | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoEntry) {
                handle = null;
                return false;
            }
            throw Io("CadenceDirectoryOpenInvalid", path, error);
        }
        if (ReadIdentity(descriptor, path).FileType != DirectoryType) {
            _ = Close(descriptor);
            throw new CadenceStoreException(
                "CadenceDirectoryShapeInvalid",
                "A Cadence path component is not a directory.");
        }
        handle = new SafeFileHandle(new IntPtr(descriptor), true);
        return true;
    }

    private static SafeFileHandle OpenDirectory(string path) {
        if (!TryOpenDirectory(path, out SafeFileHandle? handle)) {
            throw new CadenceStoreException(
                "CadenceDirectoryAbsent",
                "A required Cadence directory is absent.");
        }
        return handle!;
    }

    private static void TryDeleteOwnedTemporary(
        string path,
        FileIdentity expected,
        FileStream heldStream
    ) {
        try {
            FileIdentity held = ReadIdentity(
                heldStream.SafeFileHandle.DangerousGetHandle().ToInt32(), path);
            if (held != expected || held.FileType != RegularFileType) {
                return;
            }
        }
        catch (CadenceStoreException) {
            return;
        }
        int descriptor = Open(path,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            return;
        }
        try {
            FileIdentity actual = ReadIdentity(descriptor, path);
            if (actual == expected && actual.FileType == RegularFileType) {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (CadenceStoreException) { }
        finally {
            _ = Close(descriptor);
        }
    }

    private static void RequirePathIdentity(
        string path,
        FileIdentity expected
    ) {
        int descriptor = Open(path,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            throw Io("CadenceTemporaryOpenInvalid", path,
                Marshal.GetLastPInvokeError());
        }
        try {
            FileIdentity actual = ReadIdentity(descriptor, path);
            if (actual != expected || actual.FileType != RegularFileType) {
                throw new CadenceStoreException(
                    "CadenceTemporaryIdentityChanged",
                    "The Cadence temporary path no longer names the file created by this operation.");
            }
        }
        finally {
            _ = Close(descriptor);
        }
    }

    private static FileIdentity ReadIdentity(int descriptor, string path) {
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (Fstat(descriptor, buffer) != 0) {
                throw Io("CadenceFileStatInvalid", path,
                    Marshal.GetLastPInvokeError());
            }
            int modeOffset = RuntimeInformation.ProcessArchitecture switch {
                Architecture.X64 => 24,
                Architecture.Arm64 => 16,
                _ => throw new PlatformNotSupportedException(
                    "Unsupported Linux stat ABI.")
            };
            return new FileIdentity(
                unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
                unchecked((uint)Marshal.ReadInt32(buffer, modeOffset))
                    & FileTypeMask);
        }
        finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static CadenceStoreException Io(
        string code,
        string path,
        int error
    ) => new(code,
        $"Cadence durable path operation failed for '{path}' (errno {error}).");

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenWithMode(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int descriptor, IntPtr value);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    private readonly record struct FileIdentity(
        ulong Device,
        ulong Inode,
        uint FileType);
}

internal sealed class CadenceBusyException : Exception;
internal sealed class CadenceLimitException(string limit) : Exception(limit);
internal sealed class CadenceUnsupportedSchemaException(int version)
    : Exception {
    internal int Version { get; } = version;
}
internal sealed class CadencePublishIndeterminateException(Exception inner)
    : Exception("Cadence publication durability is indeterminate.", inner);
internal sealed class CadenceStoreException(
    string code,
    string message,
    Exception? inner = null
) : Exception(message, inner) {
    internal string Code { get; } = code;
}

internal static class CadenceError {
    internal static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;
}
