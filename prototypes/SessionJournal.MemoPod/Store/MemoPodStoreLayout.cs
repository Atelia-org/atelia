using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Atelia.SessionJournal.MemoPod;

internal enum MemoPodStoreErrorCode {
    RootAbsent,
    PathShapeInvalid,
    PathLinkRejected,
    DocumentAbsent,
    DocumentChangedDuringRead,
    DocumentIdentityMismatch,
    DirectorySyncFailed,
    PathStatFailed,
}

internal sealed class MemoPodStoreException : IOException {
    internal MemoPodStoreException(
        MemoPodStoreErrorCode code,
        string message,
        Exception? innerException = null
    ) : base(message, innerException) {
        Code = code;
    }

    internal MemoPodStoreErrorCode Code { get; }
}

internal sealed record MemoPodStorePaths(
    string RootPath,
    string MemoPodsPath,
    string VersionPath,
    string PodsPath,
    string DocumentPath
);

internal static class MemoPodStoreLayout {
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int AtCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x00000001;
    private const int StatxModeOffset = 28;
    private const ushort FileTypeMask = 0xF000;
    private const ushort DirectoryFileType = 0x4000;
    private const ushort RegularFileType = 0x8000;
    private const ushort SymbolicLinkFileType = 0xA000;

    internal static MemoPodStorePaths Resolve(
        string rootPath,
        MemoPodId podId
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        MemoPodSyntax.RequirePodId(podId, nameof(podId));

        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootPath)
        );
        string memoPods = Path.Combine(root, "memo-pods");
        string version = Path.Combine(memoPods, "v1");
        string pods = Path.Combine(version, "pods");
        return new MemoPodStorePaths(
            root,
            memoPods,
            version,
            pods,
            Path.Combine(pods, $"{podId.Value}.json")
        );
    }

    internal static void RequireForRead(MemoPodStorePaths paths) {
        RequireRootAncestors(paths.RootPath);
        RequireExistingDirectory(paths.MemoPodsPath);
        RequireExistingDirectory(paths.VersionPath);
        RequireExistingDirectory(paths.PodsPath);
    }

    internal static void EnsureForPublish(MemoPodStorePaths paths) {
        RequireLinux();
        RequireRootAncestors(paths.RootPath);
        EnsureFixedDirectory(paths.RootPath, paths.MemoPodsPath);
        EnsureFixedDirectory(paths.MemoPodsPath, paths.VersionPath);
        EnsureFixedDirectory(paths.VersionPath, paths.PodsPath);
    }

    internal static bool DocumentEntryExists(MemoPodStorePaths paths) {
        if (!TryGetAttributes(paths.DocumentPath, out FileAttributes attributes)) {
            return false;
        }
        RequireRegularFileAttributes(paths.DocumentPath, attributes);
        return true;
    }

    internal static void RequireRegularFile(string path) {
        if (!TryGetAttributes(path, out FileAttributes attributes)) {
            throw new MemoPodStoreException(
                MemoPodStoreErrorCode.DocumentAbsent,
                $"MemoPod document is absent: {path}"
            );
        }
        RequireRegularFileAttributes(path, attributes);
    }

    internal static void FlushDirectory(string path) {
        RequireLinux();
        int descriptor = NativeOpen(
            path,
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw NativeSyncException(path);
        }
        try {
            if (NativeFsync(descriptor) != 0) {
                throw NativeSyncException(path);
            }
        }
        finally {
            _ = NativeClose(descriptor);
        }
    }

    internal static bool TryGetAttributes(
        string path,
        out FileAttributes attributes
    ) {
        try {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        // File.GetAttributes may report a dangling link as absent. LinkTarget
        // lets us reject that entry instead of treating it as a free slot.
        try {
            if (new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null) {
                attributes = FileAttributes.ReparsePoint;
                return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        attributes = default;
        return false;
    }

    internal static void RequireLinux() {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "MemoPod V1 durable publication is supported only on Linux."
            );
        }
    }

    private static void RequireRootAncestors(string rootPath) {
        string? cursor = rootPath;
        bool isRequestedRoot = true;
        while (cursor is not null) {
            if (!TryGetAttributes(cursor, out FileAttributes attributes)) {
                throw new MemoPodStoreException(
                    isRequestedRoot
                        ? MemoPodStoreErrorCode.RootAbsent
                        : MemoPodStoreErrorCode.PathShapeInvalid,
                    isRequestedRoot
                        ? $"MemoPod caller root must already exist: {rootPath}"
                        : $"MemoPod root ancestor is absent: {cursor}"
                );
            }
            RequireDirectoryAttributes(cursor, attributes);
            cursor = Directory.GetParent(cursor)?.FullName;
            isRequestedRoot = false;
        }
    }

    private static void EnsureFixedDirectory(
        string parentPath,
        string childPath
    ) {
        if (TryGetAttributes(childPath, out FileAttributes attributes)) {
            RequireDirectoryAttributes(childPath, attributes);
            return;
        }

        Directory.CreateDirectory(childPath);
        if (!TryGetAttributes(childPath, out attributes)) {
            throw new MemoPodStoreException(
                MemoPodStoreErrorCode.PathShapeInvalid,
                $"MemoPod fixed directory was not created: {childPath}"
            );
        }
        RequireDirectoryAttributes(childPath, attributes);
        FlushDirectory(parentPath);
    }

    private static void RequireExistingDirectory(string path) {
        if (!TryGetAttributes(path, out FileAttributes attributes)) {
            throw new MemoPodStoreException(
                MemoPodStoreErrorCode.DocumentAbsent,
                $"MemoPod fixed directory is absent: {path}"
            );
        }
        RequireDirectoryAttributes(path, attributes);
    }

    private static void RequireDirectoryAttributes(
        string path,
        FileAttributes attributes
    ) {
        RequireNotLink(path, attributes);
        RequireLinuxFileType(path, DirectoryFileType);
        if ((attributes & FileAttributes.Directory) == 0) {
            throw new MemoPodStoreException(
                MemoPodStoreErrorCode.PathShapeInvalid,
                $"MemoPod path component must be a directory: {path}"
            );
        }
    }

    private static void RequireRegularFileAttributes(
        string path,
        FileAttributes attributes
    ) {
        RequireNotLink(path, attributes);
        RequireLinuxFileType(path, RegularFileType);
        if ((attributes & FileAttributes.Directory) != 0) {
            throw new MemoPodStoreException(
                MemoPodStoreErrorCode.PathShapeInvalid,
                $"MemoPod document path must be a regular file: {path}"
            );
        }
    }

    private static void RequireNotLink(
        string path,
        FileAttributes attributes
    ) {
        if ((attributes & FileAttributes.ReparsePoint) != 0) {
            throw new MemoPodStoreException(
                MemoPodStoreErrorCode.PathLinkRejected,
                $"MemoPod paths must not contain symbolic links or reparse points: {path}"
            );
        }
    }

    private static void RequireLinuxFileType(
        string path,
        ushort expectedFileType
    ) {
        if (!OperatingSystem.IsLinux()) { return; }

        IntPtr buffer = Marshal.AllocHGlobal(256);
        try {
            if (NativeStatx(
                    AtCurrentWorkingDirectory,
                    path,
                    AtSymlinkNoFollow,
                    StatxType,
                    buffer
                ) != 0) {
                int error = Marshal.GetLastPInvokeError();
                throw new MemoPodStoreException(
                    MemoPodStoreErrorCode.PathStatFailed,
                    $"Failed to inspect MemoPod path shape: {path}",
                    new Win32Exception(error)
                );
            }

            ushort mode = unchecked((ushort)Marshal.ReadInt16(
                buffer,
                StatxModeOffset
            ));
            ushort actualFileType = unchecked((ushort)(mode & FileTypeMask));
            if (actualFileType == SymbolicLinkFileType) {
                throw new MemoPodStoreException(
                    MemoPodStoreErrorCode.PathLinkRejected,
                    $"MemoPod paths must not contain symbolic links or reparse points: {path}"
                );
            }
            if (actualFileType != expectedFileType) {
                throw new MemoPodStoreException(
                    MemoPodStoreErrorCode.PathShapeInvalid,
                    $"MemoPod path has an unsupported filesystem shape: {path}"
                );
            }
        }
        finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static MemoPodStoreException NativeSyncException(string path) {
        int error = Marshal.GetLastPInvokeError();
        return new MemoPodStoreException(
            MemoPodStoreErrorCode.DirectorySyncFailed,
            $"Failed to fsync MemoPod directory: {path}",
            new Win32Exception(error)
        );
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int NativeFsync(int descriptor);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int NativeStatx(
        int directoryDescriptor,
        string path,
        int flags,
        uint mask,
        IntPtr buffer
    );

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int descriptor);
}
