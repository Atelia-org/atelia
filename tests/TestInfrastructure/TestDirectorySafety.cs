using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Atelia.Testing;

internal static class TestDirectorySafety {
    private const uint OwnerDirectoryMode = 0x1C0; // 0700
    private const int ErrorAlreadyExists = 17;

    internal static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static void EnsureDisjoint(string first, string second) {
        string left = Normalize(first);
        string right = Normalize(second);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(left, right, comparison)
            || IsAncestor(left, right)
            || IsAncestor(right, left)) {
            throw new ArgumentException(
                $"Test paths must be disjoint: '{left}' and '{right}'."
            );
        }
    }

    internal static bool IsAncestor(
        string ancestorPath,
        string descendantPath
    ) {
        string ancestor = Normalize(ancestorPath);
        string descendant = Normalize(descendantPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string prefix = Path.EndsInDirectorySeparator(ancestor)
            ? ancestor
            : ancestor + Path.DirectorySeparatorChar;
        return !string.Equals(ancestor, descendant, comparison)
            && descendant.StartsWith(prefix, comparison);
    }

    internal static void EnsureExistingPathChainHasNoReparsePoint(
        string path
    ) {
        string current = Normalize(path);
        while (true) {
            try {
                RejectReparsePoint(current);
            }
            catch (FileNotFoundException) {
                // Missing leaves are allowed; existing ancestors still matter.
            }
            catch (DirectoryNotFoundException) {
                // Missing leaves are allowed; existing ancestors still matter.
            }
            string? parent = Path.GetDirectoryName(current);
            if (parent is null) {
                return;
            }
            current = parent;
        }
    }

    internal static void RejectReparsePoint(string path) =>
        RejectReparsePoint(path, File.GetAttributes(path));

    internal static void RejectReparsePoint(
        string path,
        FileAttributes attributes
    ) {
        if ((attributes & FileAttributes.ReparsePoint) != 0) {
            throw new InvalidDataException(
                "Test paths must not contain a symbolic link or reparse "
                + "point: " + path
            );
        }
    }

    internal static void CreateDirectoryNew(string path) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Atomic test-directory ownership requires Linux mkdir."
            );
        }
        if (NativeMkdir(path, OwnerDirectoryMode) == 0) {
            return;
        }
        int error = Marshal.GetLastPInvokeError();
        if (error == ErrorAlreadyExists) {
            throw new IOException(
                "Create-new test directory already exists: " + path
            );
        }
        throw new IOException(
            "Failed to create-new test directory: " + path,
            new Win32Exception(error)
        );
    }

    internal static void CopyTreeIntoOwnedEmptyDirectory(
        string source,
        string destination
    ) {
        RejectReparsePoint(source);
        RequireOwnedEmptyDirectory(destination);
        CopyChildren(source, destination);
    }

    private static void CopyChildren(
        string source,
        string destination
    ) {
        RejectReparsePoint(source);
        RequireOwnedEmptyDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly
                 ).Order(StringComparer.Ordinal)) {
            FileAttributes attributes = File.GetAttributes(entry);
            RejectReparsePoint(entry, attributes);
            string target = Path.Combine(
                destination,
                Path.GetFileName(entry)
            );
            if ((attributes & FileAttributes.Directory) != 0) {
                CreateDirectoryNew(target);
                CopyChildren(entry, target);
            }
            else {
                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    internal static void RequireOwnedEmptyDirectory(string path) {
        FileAttributes attributes = File.GetAttributes(path);
        RejectReparsePoint(path, attributes);
        if ((attributes & FileAttributes.Directory) == 0) {
            throw new InvalidDataException(
                "Owned test path is not a directory: " + path
            );
        }
        if (Directory.EnumerateFileSystemEntries(path).Any()) {
            throw new InvalidDataException(
                "Owned test directory is not empty: " + path
            );
        }
    }

    internal static void DeleteOwnedTreeNoFollow(string path) {
        if (!Path.Exists(path)) {
            return;
        }
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) {
            if ((attributes & FileAttributes.Directory) != 0) {
                Directory.Delete(path);
            }
            else {
                File.Delete(path);
            }
            return;
        }
        if ((attributes & FileAttributes.Directory) == 0) {
            File.Delete(path);
            return;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     path,
                     "*",
                     SearchOption.TopDirectoryOnly
                 )) {
            DeleteOwnedTreeNoFollow(entry);
        }
        Directory.Delete(path);
    }

    [DllImport(
        "libc",
        EntryPoint = "mkdir",
        SetLastError = true,
        CharSet = CharSet.Ansi
    )]
    private static extern int NativeMkdir(string path, uint mode);
}
