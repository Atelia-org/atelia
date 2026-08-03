using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Atelia.Testing;

namespace Atelia.SessionJournal.Cli.Tests;

internal sealed class CreateOnlyDirectorySnapshot : IDisposable {
    private const int AtCurrentWorkingDirectory = -100;
    private const uint RenameNoReplace = 1;
    private const int ErrorInvalidArgument = 22;
    private const int ErrorFunctionNotImplemented = 38;
    private const int ErrorOperationNotSupported = 95;
    private string? _temporaryPath;
    private readonly CreateOnlyDirectorySnapshotTestHooks? _testHooks;

    private CreateOnlyDirectorySnapshot(
        string destinationPath,
        string temporaryPath,
        CreateOnlyDirectorySnapshotTestHooks? testHooks
    ) {
        DestinationPath = destinationPath;
        _temporaryPath = temporaryPath;
        _testHooks = testHooks;
    }

    internal string DestinationPath { get; }

    internal string TemporaryPath => _temporaryPath
        ?? throw new InvalidOperationException(
            "The directory snapshot has already been published."
        );

    internal static async ValueTask<CreateOnlyDirectorySnapshot>
        PrepareAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyCollection<string> protectedPaths,
        Func<string, ValueTask> validateSnapshotAsync,
        CreateOnlyDirectorySnapshotTestHooks? testHooks = null
    ) {
        ArgumentNullException.ThrowIfNull(protectedPaths);
        ArgumentNullException.ThrowIfNull(validateSnapshotAsync);
        string source = TestDirectorySafety.Normalize(sourcePath);
        string destination = TestDirectorySafety.Normalize(destinationPath);
        if (!Directory.Exists(source)) {
            throw new DirectoryNotFoundException(source);
        }
        string? destinationParent = Path.GetDirectoryName(destination);
        if (destinationParent is null
            || !Directory.Exists(destinationParent)) {
            throw new DirectoryNotFoundException(
                "The scripted staging output parent must already exist: "
                + destinationParent
            );
        }
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(source);
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(
            destination
        );
        if (Path.Exists(destination)) {
            throw new IOException(
                "The scripted staging output already exists: "
                + destination
            );
        }

        string[] protectedFullPaths = [
            .. protectedPaths.Select(TestDirectorySafety.Normalize)
        ];
        foreach (string protectedPath in protectedFullPaths) {
            TestDirectorySafety
                .EnsureExistingPathChainHasNoReparsePoint(protectedPath);
        }
        string[] allPaths = [source, destination, .. protectedFullPaths];
        for (int left = 0; left < allPaths.Length; left++) {
            for (int right = left + 1;
                 right < allPaths.Length;
                 right++) {
                TestDirectorySafety.EnsureDisjoint(
                    allPaths[left],
                    allPaths[right]
                );
            }
        }

        string temporaryName = testHooks?.TemporaryNameFactory?.Invoke()
            ?? $".{Path.GetFileName(destination)}."
                + $"{Guid.NewGuid():N}.staging";
        if (string.IsNullOrWhiteSpace(temporaryName)
            || !string.Equals(
                temporaryName,
                Path.GetFileName(temporaryName),
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "The test temporary-name factory must return one file name."
            );
        }
        string temporary = Path.Combine(
            destinationParent,
            temporaryName
        );
        bool ownsTemporary = false;
        try {
            testHooks?.BeforeTemporaryDirectoryCreate?.Invoke(temporary);
            TestDirectorySafety.CreateDirectoryNew(temporary);
            ownsTemporary = true;
            DirectoryTreeFingerprint before = Fingerprint(source);
            TestDirectorySafety.CopyTreeIntoOwnedEmptyDirectory(
                source,
                temporary
            );
            DirectoryTreeFingerprint sourceAfter = Fingerprint(source);
            DirectoryTreeFingerprint copied = Fingerprint(temporary);
            if (before != sourceAfter || before != copied) {
                throw new InvalidDataException(
                    "The scripted staging source changed during copy or "
                    + "the copied fingerprint does not match its source."
                );
            }
            await validateSnapshotAsync(temporary).ConfigureAwait(false);
            if (copied != Fingerprint(temporary)) {
                throw new InvalidDataException(
                    "Scripted staging validation mutated the prepared "
                    + "snapshot."
                );
            }
            return new CreateOnlyDirectorySnapshot(
                destination,
                temporary,
                testHooks
            );
        }
        catch {
            if (ownsTemporary) {
                TestDirectorySafety.DeleteOwnedTreeNoFollow(temporary);
            }
            throw;
        }
    }

    internal void Publish() {
        string temporary = TemporaryPath;
        _testHooks?.BeforePublishRename?.Invoke(
            temporary,
            DestinationPath
        );
        MoveDirectoryCreateNewAtomic(temporary, DestinationPath);
        _temporaryPath = null;
    }

    public void Dispose() {
        if (_temporaryPath is { } temporary) {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(temporary);
            _temporaryPath = null;
        }
    }

    internal static DirectoryTreeFingerprint Fingerprint(string rootPath) {
        string root = TestDirectorySafety.Normalize(rootPath);
        TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(root);
        if (!Directory.Exists(root)) {
            throw new DirectoryNotFoundException(root);
        }
        using IncrementalHash aggregate =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int directoryCount = 0;
        int fileCount = 0;
        long totalBytes = 0;
        FingerprintDirectory(
            root,
            root,
            aggregate,
            ref directoryCount,
            ref fileCount,
            ref totalBytes
        );
        return new DirectoryTreeFingerprint(
            directoryCount,
            fileCount,
            totalBytes,
            Convert.ToHexStringLower(aggregate.GetHashAndReset())
        );
    }

    private static void FingerprintDirectory(
        string root,
        string directory,
        IncrementalHash aggregate,
        ref int directoryCount,
        ref int fileCount,
        ref long totalBytes
    ) {
        FileAttributes directoryAttributes =
            File.GetAttributes(directory);
        TestDirectorySafety.RejectReparsePoint(
            directory,
            directoryAttributes
        );
        AppendEntry(
            aggregate,
            (byte)'D',
            Path.GetRelativePath(root, directory),
            0,
            []
        );
        directoryCount++;
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly
                 ).Order(StringComparer.Ordinal)) {
            FileAttributes attributes = File.GetAttributes(entry);
            TestDirectorySafety.RejectReparsePoint(entry, attributes);
            if ((attributes & FileAttributes.Directory) != 0) {
                FingerprintDirectory(
                    root,
                    entry,
                    aggregate,
                    ref directoryCount,
                    ref fileCount,
                    ref totalBytes
                );
                continue;
            }
            byte[] bytes = File.ReadAllBytes(entry);
            AppendEntry(
                aggregate,
                (byte)'F',
                Path.GetRelativePath(root, entry),
                bytes.LongLength,
                SHA256.HashData(bytes)
            );
            fileCount++;
            totalBytes += bytes.LongLength;
        }
    }

    private static void AppendEntry(
        IncrementalHash aggregate,
        byte kind,
        string relativePath,
        long length,
        ReadOnlySpan<byte> payloadHash
    ) {
        aggregate.AppendData([kind]);
        aggregate.AppendData(Encoding.UTF8.GetBytes(
            relativePath.Replace(Path.DirectorySeparatorChar, '/')
        ));
        aggregate.AppendData([0]);
        aggregate.AppendData(BitConverter.GetBytes(length));
        aggregate.AppendData(payloadHash);
    }

    private static void MoveDirectoryCreateNewAtomic(
        string source,
        string destination
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "Scripted staging publication requires Linux renameat2 "
                + "with RENAME_NOREPLACE."
            );
        }
        try {
            if (NativeRenameAt2(
                    AtCurrentWorkingDirectory,
                    source,
                    AtCurrentWorkingDirectory,
                    destination,
                    RenameNoReplace
                ) == 0) {
                return;
            }
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorInvalidArgument
                or ErrorFunctionNotImplemented
                or ErrorOperationNotSupported) {
                throw new PlatformNotSupportedException(
                    "Scripted staging publication requires Linux renameat2 "
                    + "with RENAME_NOREPLACE."
                );
            }
            throw new IOException(
                "Atomic create-new scripted staging publication failed: "
                + $"{source} -> {destination}",
                new Win32Exception(error)
            );
        }
        catch (EntryPointNotFoundException exception) {
            throw new PlatformNotSupportedException(
                "Scripted staging publication requires Linux renameat2 "
                + "with RENAME_NOREPLACE.",
                exception
            );
        }
        catch (DllNotFoundException exception) {
            throw new PlatformNotSupportedException(
                "Scripted staging publication requires Linux renameat2 "
                + "with RENAME_NOREPLACE.",
                exception
            );
        }
    }

    [DllImport(
        "libc",
        EntryPoint = "renameat2",
        SetLastError = true,
        CharSet = CharSet.Ansi
    )]
    private static extern int NativeRenameAt2(
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath,
        uint flags
    );
}

internal sealed record CreateOnlyDirectorySnapshotTestHooks(
    Func<string>? TemporaryNameFactory = null,
    Action<string>? BeforeTemporaryDirectoryCreate = null,
    Action<string, string>? BeforePublishRename = null
);

internal sealed record DirectoryTreeFingerprint(
    int DirectoryCount,
    int FileCount,
    long TotalBytes,
    string Sha256
);
