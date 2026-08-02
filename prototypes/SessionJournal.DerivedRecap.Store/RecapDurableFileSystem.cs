using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Atelia.SessionJournal.DerivedRecap.Store;

internal enum RecapIoPoint {
    FileDataFlushed,
    FileInstalled,
    DirectoryBarrier,
    DirectoryPromoted,
}

internal sealed class RecapDurableFileSystem {
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenCloseOnExec = 0x80000;
    private const int AtCurrentWorkingDirectory = -100;
    private const uint RenameNoReplace = 1;
    private const int ErrorInvalidArgument = 22;
    private const int ErrorFunctionNotImplemented = 38;
    private const int ErrorOperationNotSupported = 95;

    private readonly Action<RecapIoPoint, string>? _observer;

    public RecapDurableFileSystem(
        string repositoryRoot,
        Action<RecapIoPoint, string>? observer = null
    ) {
        RepositoryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryRoot)
        );
        _observer = observer;
        EnsurePlatformSupported();
        EnsureExistingPathChainHasNoReparsePoint(RepositoryRoot);
    }

    public string RepositoryRoot { get; }

    public void EnsurePlatformSupported() {
        if (!OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException(
                "DerivedRecap Store durability is currently verified only on Linux."
            );
        }
    }

    public void EnsureDirectoryDurable(string path) {
        EnsureSafeDescendant(path);
        if (Directory.Exists(path)) {
            EnsureSafeDescendant(path);
            return;
        }
        string parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException(
                $"Directory has no parent: {path}"
            );
        if (!Directory.Exists(parent)) {
            EnsureDirectoryDurable(parent);
        }
        Directory.CreateDirectory(path);
        EnsureSafeDescendant(path);
        FlushDirectory(path);
        FlushDirectory(parent);
    }

    public async ValueTask WriteFileCreateNewAsync(
        string finalPath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken
    ) {
        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidDataException(
                $"File has no parent directory: {finalPath}"
            );
        EnsureDirectoryDurable(directory);
        EnsureSafeDescendant(finalPath);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(finalPath)}."
            + $"{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}.tmp"
        );
        try {
            await WriteNewFileAndFlushAsync(
                    temporaryPath,
                    bytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
            EnsureSafeDescendant(finalPath);
            File.Move(temporaryPath, finalPath, overwrite: false);
            _observer?.Invoke(RecapIoPoint.FileInstalled, finalPath);
            FlushDirectory(directory);
        }
        catch {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    public async ValueTask<string> WriteNamedTemporaryFileAsync(
        string directory,
        string baseName,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken
    ) {
        EnsureDirectoryDurable(directory);
        string path = Path.Combine(
            directory,
            $"{baseName}."
            + $"{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}.tmp"
        );
        await WriteNewFileAndFlushAsync(
                path,
                bytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        FlushDirectory(directory);
        return path;
    }

    public async ValueTask WriteFileAtomicReplaceAsync(
        string finalPath,
        ReadOnlyMemory<byte> bytes,
        Action? beforeReplace,
        Action? afterReplace,
        CancellationToken cancellationToken
    ) {
        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidDataException(
                $"File has no parent directory: {finalPath}"
            );
        EnsureDirectoryDurable(directory);
        EnsureSafeDescendant(finalPath);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(finalPath)}."
            + $"{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}.tmp"
        );
        try {
            await WriteNewFileAndFlushAsync(
                    temporaryPath,
                    bytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
            beforeReplace?.Invoke();
            InstallTemporaryFileReplace(
                temporaryPath,
                finalPath
            );
            afterReplace?.Invoke();
        }
        catch {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    public void InstallTemporaryFileCreateNew(
        string temporaryPath,
        string finalPath
    ) {
        EnsureSafeDescendant(temporaryPath);
        EnsureSafeDescendant(finalPath);
        string temporaryDirectory =
            Path.GetDirectoryName(temporaryPath)!;
        string finalDirectory = Path.GetDirectoryName(finalPath)!;
        if (!string.Equals(
                temporaryDirectory,
                finalDirectory,
                StringComparison.Ordinal
            )) {
            throw new InvalidOperationException(
                "Atomic file installation requires one directory."
            );
        }
        File.Move(temporaryPath, finalPath, overwrite: false);
        _observer?.Invoke(RecapIoPoint.FileInstalled, finalPath);
        FlushDirectory(finalDirectory);
    }

    public void InstallTemporaryFileReplace(
        string temporaryPath,
        string finalPath
    ) {
        EnsureSafeDescendant(temporaryPath);
        EnsureSafeDescendant(finalPath);
        string temporaryDirectory =
            Path.GetDirectoryName(temporaryPath)!;
        string finalDirectory = Path.GetDirectoryName(finalPath)!;
        if (!string.Equals(
                temporaryDirectory,
                finalDirectory,
                StringComparison.Ordinal
            )) {
            throw new InvalidOperationException(
                "Atomic file replacement requires one directory."
            );
        }
        if (NativeRenameAt2(
                AtCurrentWorkingDirectory,
                temporaryPath,
                AtCurrentWorkingDirectory,
                finalPath,
                flags: 0
            ) != 0) {
            throw NativeIOException(
                "Atomic file replacement failed: "
                + $"{temporaryPath} -> {finalPath}"
            );
        }
        _observer?.Invoke(RecapIoPoint.FileInstalled, finalPath);
        FlushDirectory(finalDirectory);
    }

    public async ValueTask<byte[]> ReadBoundedAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeDescendant(path);
        try {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous
                    | FileOptions.SequentialScan
            );
            if (stream.Length > maxBytes) {
                throw new InvalidDataException(
                    $"File '{path}' exceeds {maxBytes} bytes."
                );
            }
            byte[] bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken)
                .ConfigureAwait(false);
            EnsureSafeDescendant(path);
            return bytes;
        }
        catch (FileNotFoundException) {
            throw;
        }
        catch (DirectoryNotFoundException) {
            throw;
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException) {
            throw new InvalidDataException(
                $"Failed to read DerivedRecap file '{path}'.",
                exception
            );
        }
    }

    public string ComputeFileSha256(string path) {
        EnsureSafeDescendant(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan
        );
        string hash = Convert.ToHexString(
            SHA256.HashData(stream)
        ).ToLowerInvariant();
        EnsureSafeDescendant(path);
        return hash;
    }

    public void FlushFile(string path) {
        EnsureSafeDescendant(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.None
        );
        stream.Flush(flushToDisk: true);
        _observer?.Invoke(RecapIoPoint.FileDataFlushed, path);
    }

    public void FlushDirectory(string path) {
        EnsureSafeDescendant(path);
        int fd = NativeOpen(
            path,
            OpenReadOnly | OpenDirectory | OpenCloseOnExec,
            0
        );
        if (fd < 0) {
            throw NativeIOException(
                $"Failed to open directory for durability barrier: {path}"
            );
        }
        try {
            if (NativeFsync(fd) != 0) {
                throw NativeIOException(
                    $"Failed to fsync directory: {path}"
                );
            }
        }
        finally {
            _ = NativeClose(fd);
        }
        _observer?.Invoke(RecapIoPoint.DirectoryBarrier, path);
    }

    public void MoveDirectoryCreateNew(
        string source,
        string destination
    ) {
        EnsureSafeDescendant(source);
        EnsureSafeDescendant(destination);
        try {
            if (NativeRenameAt2(
                    AtCurrentWorkingDirectory,
                    source,
                    AtCurrentWorkingDirectory,
                    destination,
                    RenameNoReplace
                ) != 0) {
                int error = Marshal.GetLastPInvokeError();
                if (error is ErrorInvalidArgument
                    or ErrorFunctionNotImplemented
                    or ErrorOperationNotSupported) {
                    throw new PlatformNotSupportedException(
                        "DerivedRecap Store requires Linux renameat2 "
                        + "with RENAME_NOREPLACE."
                    );
                }
                throw NativeIOException(
                    "Atomic create-new directory rename failed: "
                    + $"{source} -> {destination}"
                );
            }
        }
        catch (EntryPointNotFoundException exception) {
            throw new PlatformNotSupportedException(
                "DerivedRecap Store requires Linux renameat2 "
                + "with RENAME_NOREPLACE.",
                exception
            );
        }
        _observer?.Invoke(
            RecapIoPoint.DirectoryPromoted,
            destination
        );
    }

    public async ValueTask<FileStream> AcquireExclusiveLockAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        const int maxAttempts = 200;
        EnsureDirectoryDurable(
            Path.GetDirectoryName(path)
                ?? throw new InvalidDataException(
                    $"Lock file has no parent: {path}"
                )
        );
        IOException? contention = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++) {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeDescendant(path);
            try {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous
                );
            }
            catch (UnauthorizedAccessException) {
                throw;
            }
            catch (IOException exception) {
                contention = exception;
                await Task.Delay(
                        TimeSpan.FromMilliseconds(25),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        throw new IOException(
            $"Timed out acquiring DerivedRecap lock '{path}'.",
            contention
        );
    }

    /// <summary>
    /// Opens an existing coordination lock for a pure read operation without
    /// provisioning either its parent directory or the lock file itself.
    /// </summary>
    public async ValueTask<FileStream>
        AcquireExistingExclusiveReadLockAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        const int maxAttempts = 200;
        EnsureSafeDescendant(path);
        IOException? contention = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++) {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeDescendant(path);
            try {
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous
                );
            }
            catch (FileNotFoundException) {
                throw;
            }
            catch (DirectoryNotFoundException) {
                throw;
            }
            catch (UnauthorizedAccessException) {
                throw;
            }
            catch (IOException exception) {
                contention = exception;
                await Task.Delay(
                        TimeSpan.FromMilliseconds(25),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        throw new IOException(
            $"Timed out acquiring existing DerivedRecap lock '{path}'.",
            contention
        );
    }

    public void EnsureSafeDescendant(string path) {
        string candidate = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string prefix = RepositoryRoot
            + Path.DirectorySeparatorChar;
        if (!candidate.Equals(RepositoryRoot, comparison)
            && !candidate.StartsWith(prefix, comparison)) {
            throw new InvalidDataException(
                $"DerivedRecap path escapes repository root: {candidate}"
            );
        }
        EnsureExistingPathChainHasNoReparsePoint(candidate);
    }

    internal static void EnsureExistingPathChainHasNoReparsePoint(
        string path
    ) {
        string? cursor = Path.GetFullPath(path);
        while (cursor is not null) {
            try {
                FileAttributes attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new InvalidDataException(
                        "DerivedRecap path contains a symbolic link "
                        + $"or reparse point: {cursor}"
                    );
                }
            }
            catch (FileNotFoundException) {
            }
            catch (DirectoryNotFoundException) {
            }
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    private async ValueTask WriteNewFileAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken
    ) {
        EnsureSafeDescendant(path);
        await using (var stream = new FileStream(
                         path,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         FileOptions.Asynchronous
                     )) {
            await stream.WriteAsync(bytes, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        _observer?.Invoke(RecapIoPoint.FileDataFlushed, path);
    }

    private static IOException NativeIOException(string message) {
        int error = Marshal.GetLastPInvokeError();
        return new IOException(
            $"{message} errno={error}",
            new Win32Exception(error)
        );
    }

    private static void TryDeleteFile(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch {
        }
    }

    [DllImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        CharSet = CharSet.Ansi
    )]
    private static extern int NativeOpen(
        string path,
        int flags,
        uint mode
    );

    [DllImport(
        "libc",
        EntryPoint = "renameat2",
        SetLastError = true,
        CharSet = CharSet.Ansi
    )]
    private static extern int NativeRenameAt2(
        int oldDirectoryFd,
        string oldPath,
        int newDirectoryFd,
        string newPath,
        uint flags
    );

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int NativeFsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int fd);
}
