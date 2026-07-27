using System.Globalization;
using System.Text;

namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Repository-local owner for rebuildable derived-memory state. It never owns or mutates the raw
/// SessionJournal event sequence.
/// </summary>
public sealed class DerivedMemoryRepository {
    private const int WriteLockMaxAttempts = 200;
    private static readonly TimeSpan WriteLockRetryDelay =
        TimeSpan.FromMilliseconds(25);

    private DerivedMemoryRepository(string sessionJournalRepositoryPath) {
        SessionJournalRepositoryPath = sessionJournalRepositoryPath;
        DerivedRoot = Path.Combine(SessionJournalRepositoryPath, "derived");
        MemoryRoot = Path.Combine(DerivedRoot, "memory", "v1");
        WriteLockPath = Path.Combine(DerivedRoot, ".derived-memory.lock");
        Recaps = new DerivedRecapStore(this);
        ArtifactSets = new DerivedArtifactSetStore(this);
    }

    public string SessionJournalRepositoryPath { get; }

    public string DerivedRoot { get; }

    public string MemoryRoot { get; }

    public DerivedRecapStore Recaps { get; }

    public DerivedArtifactSetStore ArtifactSets { get; }

    internal string WriteLockPath { get; }

    public static DerivedMemoryRepository Open(
        string sessionJournalRepositoryPath
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            sessionJournalRepositoryPath
        );
        string fullPath = Path.GetFullPath(sessionJournalRepositoryPath);
        DerivedMemoryPathGuard.EnsureExistingPathChainHasNoReparsePoint(
            fullPath
        );
        if (!Directory.Exists(fullPath)) {
            throw new DirectoryNotFoundException(
                $"SessionJournal repository does not exist: {fullPath}"
            );
        }
        return new DerivedMemoryRepository(fullPath);
    }

    internal async ValueTask<FileStream> AcquireWriteLockAsync(
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory(DerivedRoot);
        IOException? lastContention = null;
        for (int attempt = 0; attempt < WriteLockMaxAttempts; attempt++) {
            cancellationToken.ThrowIfCancellationRequested();
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                SessionJournalRepositoryPath,
                WriteLockPath
            );
            try {
                return new FileStream(
                    WriteLockPath,
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
                lastContention = exception;
                await Task.Delay(
                        WriteLockRetryDelay,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }

        throw new IOException(
            $"Timed out acquiring the derived-memory repository lock '{WriteLockPath}'.",
            lastContention
        );
    }

    internal void EnsureDirectory(string path) {
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            SessionJournalRepositoryPath,
            path
        );
        Directory.CreateDirectory(path);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            SessionJournalRepositoryPath,
            path
        );
    }

    internal async ValueTask WriteFileAtomicallyAsync(
        string finalPath,
        string content,
        bool overwrite,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException(
                "Derived-memory file requires a parent directory.",
                nameof(finalPath)
            );
        EnsureDirectory(directory);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            SessionJournalRepositoryPath,
            finalPath
        );

        string fileName = Path.GetFileName(finalPath);
        string temporaryPath;
        FileStream temporaryStream;
        while (true) {
            temporaryPath = Path.Combine(
                directory,
                $".{fileName}.{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}.tmp"
            );
            try {
                temporaryStream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous
                );
                break;
            }
            catch (IOException) when (File.Exists(temporaryPath)) {
                // Retry only an actual generated-name collision.
            }
        }

        try {
            await using (temporaryStream.ConfigureAwait(false)) {
                byte[] bytes = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true
                ).GetBytes(content);
                await temporaryStream
                    .WriteAsync(bytes, cancellationToken)
                    .ConfigureAwait(false);
                await temporaryStream
                    .FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                temporaryStream.Flush(flushToDisk: true);
            }
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                SessionJournalRepositoryPath,
                finalPath
            );
            File.Move(temporaryPath, finalPath, overwrite);
        }
        catch {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private static void TryDeleteTemporaryFile(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch {
            // Best-effort cleanup of a file created by this operation only.
        }
    }
}

internal static class DerivedMemoryPathGuard {
    public static void EnsureSafeDescendant(string rootPath, string path) {
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootPath)
        );
        string candidate = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, comparison)
            && !candidate.StartsWith(prefix, comparison)) {
            throw new InvalidDataException(
                $"Derived-memory path escapes its repository root: {candidate}"
            );
        }
        EnsureExistingPathChainHasNoReparsePoint(candidate);
    }

    public static void EnsureExistingPathChainHasNoReparsePoint(string path) {
        string? cursor = Path.GetFullPath(path);
        while (cursor is not null) {
            try {
                FileAttributes attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new InvalidDataException(
                        $"Derived-memory path contains a symbolic link or reparse point: {cursor}"
                    );
                }
            }
            catch (FileNotFoundException) {
                // Missing descendants are allowed; existing ancestors are still checked.
            }
            catch (DirectoryNotFoundException) {
                // Missing descendants are allowed; existing ancestors are still checked.
            }
            cursor = Path.GetDirectoryName(cursor);
        }
    }
}
