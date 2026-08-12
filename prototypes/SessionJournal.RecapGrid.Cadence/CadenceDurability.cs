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
        StatePath = Path.Combine(DirectoryPath, StateName);
        LockPath = Path.Combine(DirectoryPath, LockName);
    }

    internal const string StateName = "cadence.json";
    internal const string LockName = "cadence.lock";
    internal string RepositoryPath { get; }
    internal RefId RefId { get; }
    internal string DirectoryPath { get; }
    internal string StatePath { get; }
    internal string LockPath { get; }
}

internal sealed class CadenceDirectoryLease : IDisposable {
    internal CadenceDirectoryLease(
        SafeFileHandle handle,
        CadenceFileIdentity identity
    ) {
        Handle = handle;
        Identity = identity;
    }

    internal SafeFileHandle Handle { get; }
    internal CadenceFileIdentity Identity { get; }
    internal int Descriptor => Handle.DangerousGetHandle().ToInt32();
    public void Dispose() => Handle.Dispose();
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
    private const uint OwnerDirectoryMode = 0x1c0; // 0700
    private const uint OwnerFileMode = 0x180; // 0600
    private const uint RenameNoReplace = 1;

    internal static CadenceDirectoryLease OpenDirectory(
        CadencePaths paths,
        bool create,
        CadencePersistenceTestHooks hooks
    ) {
        RequireLinuxAbi();
        using SafeFileHandle repository = OpenExistingAbsoluteDirectoryChain(
            paths.RepositoryPath);
        SafeFileHandle current = Duplicate(repository);
        try {
            string[] components = [
                "control", "recap-grid", "v1", "refs",
                paths.RefId.ToHexString()
            ];
            foreach (string component in components) {
                SafeFileHandle? next = TryOpenDirectoryAt(
                    current, component);
                if (next is null) {
                    if (!create) {
                        throw new CadenceDirectoryAbsentException();
                    }
                    if (MkdirAt(current.DangerousGetHandle().ToInt32(),
                            component, OwnerDirectoryMode) != 0) {
                        int error = Marshal.GetLastPInvokeError();
                        if (error != ErrorAlreadyExists) {
                            throw Io("CadenceDirectoryCreateInvalid",
                                paths.DirectoryPath, error);
                        }
                    }
                    else {
                        FlushDirectory(current, paths.DirectoryPath);
                    }
                    next = TryOpenDirectoryAt(current, component)
                        ?? throw new CadenceStoreException(
                            "CadenceDirectoryAbsent",
                            "A newly-created Cadence directory is absent.");
                }
                current.Dispose();
                current = next;
            }
            CadenceFileIdentity identity = ReadIdentity(
                current.DangerousGetHandle().ToInt32(),
                paths.DirectoryPath);
            var result = new CadenceDirectoryLease(current, identity);
            current = null!;
            hooks.AfterDirectoryOpen?.Invoke(paths.DirectoryPath);
            return result;
        }
        finally {
            current?.Dispose();
        }
    }

    internal static void RequireCanonicalDirectoryIdentity(
        CadencePaths paths,
        CadenceDirectoryLease expected
    ) {
        using CadenceDirectoryLease actual = OpenDirectory(
            paths, create: false, CadencePersistenceTestHooks.None);
        if (actual.Identity != expected.Identity) {
            throw new CadenceStoreException(
                "CadenceDirectoryIdentityChanged",
                "The canonical Cadence directory changed during the operation.");
        }
    }

    internal static void EnsureSlots(
        CadencePaths paths,
        CadenceDirectoryLease directory
    ) {
        using FileStream? created = TryCreateRegularFile(
            directory, CadencePaths.LockName, paths.LockPath);
        if (created is null) {
            _ = EntryExists(directory, CadencePaths.LockName,
                paths.LockPath);
            return;
        }
        created.Flush(flushToDisk: true);
        FlushDirectory(directory.Handle, paths.DirectoryPath);
    }

    internal static bool EntryExists(
        CadenceDirectoryLease directory,
        string name,
        string diagnosticPath
    ) {
        int descriptor = OpenAt(directory.Descriptor, name,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoEntry) {
                return false;
            }
            throw Io("CadenceSlotOpenInvalid", diagnosticPath, error);
        }
        try {
            CadenceFileIdentity identity = ReadIdentity(
                descriptor, diagnosticPath);
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

    internal static FileStream AcquireLock(
        CadenceDirectoryLease directory,
        bool exclusive,
        string diagnosticPath
    ) {
        int descriptor = OpenAt(directory.Descriptor, CadencePaths.LockName,
            OpenReadWrite | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            throw Io("CadenceLockOpenInvalid", diagnosticPath,
                Marshal.GetLastPInvokeError());
        }
        try {
            if (ReadIdentity(descriptor, diagnosticPath).FileType
                != RegularFileType) {
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
                throw Io("CadenceLockInvalid", diagnosticPath, error);
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

    internal static byte[] ReadBounded(
        CadenceDirectoryLease directory,
        string diagnosticPath
    ) {
        int descriptor = OpenAt(directory.Descriptor, CadencePaths.StateName,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            throw Io("CadenceStateOpenInvalid", diagnosticPath,
                Marshal.GetLastPInvokeError());
        }
        try {
            if (ReadIdentity(descriptor, diagnosticPath).FileType
                != RegularFileType) {
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
        CadenceDirectoryLease directory,
        ReadOnlySpan<byte> bytes,
        bool createNew,
        CadencePersistenceTestHooks hooks
    ) {
        if (bytes.Length > RecapGridCadenceLimits.MaximumCanonicalUtf8Bytes) {
            throw new CadenceLimitException("CadenceCanonicalBytes");
        }
        string temporaryName = $".cadence.json.{Guid.NewGuid():N}.tmp";
        string temporaryPath = Path.Combine(
            paths.DirectoryPath, temporaryName);
        FileStream? temporaryStream = null;
        bool published = false;
        try {
            temporaryStream = CreateRegularFile(
                directory, temporaryName, temporaryPath);
            temporaryStream.Write(bytes);
            temporaryStream.Flush(flushToDisk: true);
            CadenceFileIdentity temporaryIdentity = ReadIdentity(
                temporaryStream.SafeFileHandle.DangerousGetHandle().ToInt32(),
                temporaryPath);
            hooks.BeforePublish?.Invoke(temporaryPath);
            RequireRelativeIdentity(directory, temporaryName,
                temporaryPath, temporaryIdentity);
            int renamed = createNew
                ? RenameAt2(directory.Descriptor, temporaryName,
                    directory.Descriptor, CadencePaths.StateName,
                    RenameNoReplace)
                : RenameAt(directory.Descriptor, temporaryName,
                    directory.Descriptor, CadencePaths.StateName);
            if (renamed != 0) {
                int error = Marshal.GetLastPInvokeError();
                if (createNew && error == ErrorAlreadyExists) {
                    throw new CadenceStoreException(
                        "CadenceFileAlreadyExists",
                        "A create-new Cadence durable file already exists.");
                }
                throw Io("CadenceStatePublishInvalid", paths.StatePath,
                    error);
            }
            published = true;
            hooks.AfterPublish?.Invoke(temporaryPath);
            FlushDirectory(directory.Handle, paths.DirectoryPath);
            RequireCanonicalDirectoryIdentity(paths, directory);
        }
        catch (Exception exception) when (published
            && !CadenceError.IsFatal(exception)) {
            throw new CadencePublishIndeterminateException(exception);
        }
        finally {
            // There is no atomic compare-and-unlink primitive. Before publish
            // an orphan is safer than deleting a path another owner may have
            // reoccupied; after publish the source name is never touched.
            temporaryStream?.Dispose();
        }
    }

    private static SafeFileHandle OpenExistingAbsoluteDirectoryChain(
        string fullPath
    ) {
        string canonical = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(fullPath));
        SafeFileHandle current = OpenAbsoluteDirectory(
            Path.GetPathRoot(canonical)!);
        try {
            string root = Path.GetPathRoot(canonical)!;
            foreach (string component in canonical[root.Length..].Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries)) {
                SafeFileHandle? next = TryOpenDirectoryAt(current, component);
                if (next is null) {
                    throw new CadenceDirectoryAbsentException();
                }
                current.Dispose();
                current = next;
            }
            SafeFileHandle result = current;
            current = null!;
            return result;
        }
        finally {
            current?.Dispose();
        }
    }

    private static SafeFileHandle OpenAbsoluteDirectory(string path) {
        int descriptor = Open(path,
            OpenReadOnly | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            throw Io("CadenceDirectoryOpenInvalid", path,
                Marshal.GetLastPInvokeError());
        }
        var handle = new SafeFileHandle(new IntPtr(descriptor), true);
        try {
            if (ReadIdentity(descriptor, path).FileType != DirectoryType) {
                throw new CadenceStoreException(
                    "CadenceDirectoryShapeInvalid",
                    "A Cadence path component is not a directory.");
            }
            return handle;
        }
        catch {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? TryOpenDirectoryAt(
        SafeFileHandle parent,
        string name
    ) {
        int descriptor = OpenAt(parent.DangerousGetHandle().ToInt32(), name,
            OpenReadOnly | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoEntry) {
                return null;
            }
            throw Io("CadenceDirectoryOpenInvalid", name, error);
        }
        var handle = new SafeFileHandle(new IntPtr(descriptor), true);
        try {
            if (ReadIdentity(descriptor, name).FileType != DirectoryType) {
                throw new CadenceStoreException(
                    "CadenceDirectoryShapeInvalid",
                    "A Cadence path component is not a directory.");
            }
            return handle;
        }
        catch {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle Duplicate(SafeFileHandle source) {
        int descriptor = Dup(source.DangerousGetHandle().ToInt32());
        if (descriptor < 0) {
            throw Io("CadenceDirectoryDuplicateInvalid", "directory",
                Marshal.GetLastPInvokeError());
        }
        return new SafeFileHandle(new IntPtr(descriptor), true);
    }

    private static FileStream CreateRegularFile(
        CadenceDirectoryLease directory,
        string name,
        string diagnosticPath
    ) => TryCreateRegularFile(directory, name, diagnosticPath)
        ?? throw new CadenceStoreException(
            "CadenceFileAlreadyExists",
            "A create-new Cadence durable file already exists.");

    private static FileStream? TryCreateRegularFile(
        CadenceDirectoryLease directory,
        string name,
        string diagnosticPath
    ) {
        int descriptor = OpenAtWithMode(directory.Descriptor, name,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow
                | OpenCloseOnExec,
            OwnerFileMode);
        if (descriptor < 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorAlreadyExists) {
                return null;
            }
            throw Io("CadenceFileCreateInvalid", diagnosticPath, error);
        }
        if (ReadIdentity(descriptor, diagnosticPath).FileType
            != RegularFileType) {
            _ = Close(descriptor);
            throw new CadenceStoreException(
                "CadenceFileShapeInvalid",
                "A newly-created Cadence slot is not a regular file.");
        }
        return new FileStream(
            new SafeFileHandle(new IntPtr(descriptor), true),
            FileAccess.Write, bufferSize: 4096, isAsync: false);
    }

    private static void RequireRelativeIdentity(
        CadenceDirectoryLease directory,
        string name,
        string diagnosticPath,
        CadenceFileIdentity expected
    ) {
        int descriptor = OpenAt(directory.Descriptor, name,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0) {
            throw Io("CadenceTemporaryOpenInvalid", diagnosticPath,
                Marshal.GetLastPInvokeError());
        }
        try {
            CadenceFileIdentity actual = ReadIdentity(
                descriptor, diagnosticPath);
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

    private static void FlushDirectory(
        SafeFileHandle handle,
        string diagnosticPath
    ) {
        if (Fsync(handle.DangerousGetHandle().ToInt32()) != 0) {
            throw Io("CadenceDirectoryFlushInvalid", diagnosticPath,
                Marshal.GetLastPInvokeError());
        }
    }

    private static void RequireLinuxAbi() {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture is not (
                Architecture.X64 or Architecture.Arm64)) {
            throw new PlatformNotSupportedException(
                "RecapGrid Cadence requires the supported Linux stat ABI.");
        }
    }

    private static CadenceFileIdentity ReadIdentity(
        int descriptor,
        string path
    ) {
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
            return new CadenceFileIdentity(
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
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtWithMode(
        int directory, string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(int directory, string path, uint mode);
    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameAt(
        int oldDirectory, string oldPath,
        int newDirectory, string newPath);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(
        int oldDirectory, string oldPath,
        int newDirectory, string newPath, uint flags);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int descriptor, IntPtr value);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}

internal readonly record struct CadenceFileIdentity(
    ulong Device,
    ulong Inode,
    uint FileType);

internal sealed class CadenceBusyException : Exception;
internal sealed class CadenceDirectoryAbsentException : Exception;
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
