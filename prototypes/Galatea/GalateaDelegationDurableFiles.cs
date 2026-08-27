using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Atelia.Galatea.Server;

internal static class GalateaDelegationDurableFiles {
    private const uint OwnerDirectoryMode = 0x1C0; // 0700
    private const int ErrorAlreadyExists = 17;
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;

    internal static void RequireLinux() {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Galatea delegation store durability is verified only on Linux."
            );
        }
    }

    internal static void FlushDirectory(string path) {
        RequireLinux();
        int descriptor = NativeOpen(
            path,
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw NativeFailure("open", path);
        }
        try {
            if (NativeFsync(descriptor) != 0) {
                throw NativeFailure("fsync", path);
            }
        }
        finally {
            _ = NativeClose(descriptor);
        }
    }

    internal static void CreateDirectoryNew(string path) {
        RequireLinux();
        if (NativeMkdir(path, OwnerDirectoryMode) == 0) { return; }
        int error = Marshal.GetLastPInvokeError();
        if (error == ErrorAlreadyExists) {
            throw new IOException(
                "Delegation store directory already exists: " + path
            );
        }
        throw new IOException(
            "Failed to create delegation store directory: " + path,
            new Win32Exception(error)
        );
    }

    private static IOException NativeFailure(string operation, string path) =>
        new(
            $"Failed to {operation} delegation directory '{path}'.",
            new Win32Exception(Marshal.GetLastPInvokeError())
        );

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int NativeFsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int descriptor);

    [DllImport(
        "libc",
        EntryPoint = "mkdir",
        SetLastError = true,
        CharSet = CharSet.Ansi
    )]
    private static extern int NativeMkdir(string path, uint mode);
}
