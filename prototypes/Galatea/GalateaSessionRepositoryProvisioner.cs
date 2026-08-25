using System.ComponentModel;
using System.Runtime.InteropServices;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal static class GalateaSessionRepositoryProvisioner {
    private const int AtCurrentWorkingDirectory = -100;
    private const uint RenameNoReplace = 1;
    private const int ErrorInvalidArgument = 22;
    private const int ErrorFunctionNotImplemented = 38;
    private const int ErrorOperationNotSupported = 95;

    internal static SessionJournalEngine CreateAndPublish(
        string finalPath,
        SessionCreateOptions options,
        GalateaSessionProvisioningTestHooks? hooks = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(options);

        string normalizedFinalPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(finalPath)
        );
        string parentPath = Path.GetDirectoryName(normalizedFinalPath)
            ?? throw new InvalidOperationException(
                $"Cannot determine SessionJournal parent path: "
                + normalizedFinalPath
            );
        Directory.CreateDirectory(parentPath);
        string stagingPath = Path.Combine(
            parentPath,
            $".galatea-session-{Guid.NewGuid():N}.staging"
        );
        bool candidateClosed = false;
        bool published = false;
        try {
            SessionJournalEngine candidate =
                SessionJournalEngine.Create(stagingPath, options);
            candidate.Dispose();
            candidateClosed = true;

            hooks?.BeforeSessionRepositoryPublish?.Invoke(
                stagingPath,
                normalizedFinalPath
            );
            PublishNoReplace(stagingPath, normalizedFinalPath);
            published = true;
            return SessionJournalEngine.Open(normalizedFinalPath);
        }
        finally {
            if (candidateClosed && !published) {
                TryDeleteOwnedCandidate(stagingPath);
            }
        }
    }

    private static void PublishNoReplace(
        string stagingPath,
        string finalPath
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw UnsupportedPlatform();
        }
        try {
            if (RenameAt2(
                    AtCurrentWorkingDirectory,
                    stagingPath,
                    AtCurrentWorkingDirectory,
                    finalPath,
                    RenameNoReplace
                ) == 0) {
                return;
            }
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorInvalidArgument
                or ErrorFunctionNotImplemented
                or ErrorOperationNotSupported) {
                throw UnsupportedPlatform();
            }
            throw new IOException(
                "Atomic create-only SessionJournal publication failed: "
                + $"'{stagingPath}' -> '{finalPath}'.",
                new Win32Exception(error)
            );
        }
        catch (EntryPointNotFoundException exception) {
            throw UnsupportedPlatform(exception);
        }
        catch (DllNotFoundException exception) {
            throw UnsupportedPlatform(exception);
        }
    }

    private static PlatformNotSupportedException UnsupportedPlatform(
        Exception? innerException = null
    ) => new(
        "Galatea create-if-missing requires Linux renameat2 with "
        + "RENAME_NOREPLACE.",
        innerException
    );

    private static void TryDeleteOwnedCandidate(string stagingPath) {
        try {
            if (Directory.Exists(stagingPath)) {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
        }
    }

    [DllImport(
        "libc",
        EntryPoint = "renameat2",
        SetLastError = true,
        CharSet = CharSet.Ansi
    )]
    private static extern int RenameAt2(
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath,
        uint flags
    );
}

internal sealed record GalateaSessionProvisioningTestHooks(
    Action<string, string>? BeforeSessionRepositoryPublish = null
);
