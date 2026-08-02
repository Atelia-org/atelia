using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public static class RecapPlannerConfigLoader {
    public const string ConfigDirectoryName = "config";
    public const string ConfigFileName =
        "recap-planner-config.json";

    public static string GetCanonicalPath(string repositoryRoot) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return Path.GetFullPath(Path.Combine(
            repositoryRoot,
            ConfigDirectoryName,
            ConfigFileName
        ));
    }

    public static RecapPlannerConfigLoadResult Load(
        string repositoryRoot
    ) {
        string path;
        try {
            path = GetCanonicalPath(repositoryRoot);
        }
        catch (Exception exception) when (IsPathException(exception)) {
            return new RecapPlannerConfigLoadResult.Unavailable(
                repositoryRoot ?? string.Empty,
                exception.Message
            );
        }

        try {
            string root = Path.GetFullPath(repositoryRoot);
            PathCheckResult rootCheck =
                RecapPlannerConfigPathSafety.ValidateRepositoryRoot(
                    root
                );
            if (rootCheck.Defect is { } rootDefect) {
                return Invalid(path, rootDefect);
            }
            if (rootCheck.UnavailableReason is { } rootUnavailable) {
                return new RecapPlannerConfigLoadResult.Unavailable(
                    path,
                    rootUnavailable
                );
            }

            string configDirectory =
                Path.GetDirectoryName(path)!;
            PathCheckResult directoryCheck =
                RecapPlannerConfigPathSafety.ValidateConfigDirectory(
                    root,
                    configDirectory,
                    allowMissing: true
                );
            if (directoryCheck.Defect is { } directoryDefect) {
                return Invalid(path, directoryDefect);
            }
            if (directoryCheck.UnavailableReason
                is { } directoryUnavailable) {
                return new RecapPlannerConfigLoadResult.Unavailable(
                    path,
                    directoryUnavailable
                );
            }
            if (!directoryCheck.Exists) {
                return new RecapPlannerConfigLoadResult.Missing(path);
            }

            PathCheckResult fileCheck =
                RecapPlannerConfigPathSafety.ValidateConfigFile(
                    root,
                    configDirectory,
                    path,
                    allowMissing: true
                );
            if (fileCheck.Defect is { } fileDefect) {
                return Invalid(path, fileDefect);
            }
            if (fileCheck.UnavailableReason is { } fileUnavailable) {
                return new RecapPlannerConfigLoadResult.Unavailable(
                    path,
                    fileUnavailable
                );
            }
            if (!fileCheck.Exists) {
                return new RecapPlannerConfigLoadResult.Missing(path);
            }

            byte[] bytes = ReadBoundedFromOneHandle(path);
            PathCheckResult afterRead =
                RecapPlannerConfigPathSafety.ValidateConfigFile(
                    root,
                    configDirectory,
                    path,
                    allowMissing: false
                );
            if (afterRead.Defect is { } afterReadDefect) {
                return Invalid(path, afterReadDefect);
            }
            if (afterRead.UnavailableReason
                is { } afterReadUnavailable) {
                return new RecapPlannerConfigLoadResult.Unavailable(
                    path,
                    afterReadUnavailable
                );
            }
            RecapPlannerConfigDecodeResult decoded =
                RecapPlannerConfigCodec.Decode(bytes);
            return decoded switch {
                RecapPlannerConfigDecodeResult.Valid valid =>
                    new RecapPlannerConfigLoadResult.Available(
                        path,
                        valid.Document,
                        valid.CanonicalBytes,
                        valid.ConfigSha256
                    ),
                RecapPlannerConfigDecodeResult.Invalid invalid =>
                    new RecapPlannerConfigLoadResult.Invalid(
                        path,
                        invalid.Defects
                    ),
                _ => throw new InvalidOperationException(
                    "Unknown planner config decode result."
                )
            };
        }
        catch (ConfigFileTooLargeException exception) {
            return Invalid(
                path,
                new RecapPlannerConfigDefect(
                    RecapPlannerConfigDefectCodes.SizeLimitExceeded,
                    exception.Message
                )
            );
        }
        catch (ConfigFileNotRegularException exception) {
            return Invalid(
                path,
                new RecapPlannerConfigDefect(
                    RecapPlannerConfigDefectCodes.UnsafePath,
                    exception.Message
                )
            );
        }
        catch (Exception exception) when (IsIoException(exception)) {
            return new RecapPlannerConfigLoadResult.Unavailable(
                path,
                exception.Message
            );
        }
    }

    private static byte[] ReadBoundedFromOneHandle(string path) {
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            FileOptions.RandomAccess
        );
        FileAttributes attributes = File.GetAttributes(handle);
        if ((attributes & (
                FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint
            )) != 0) {
            throw new ConfigFileNotRegularException(
                "Planner config opened handle must identify a "
                + "regular file."
            );
        }
        long length = RandomAccess.GetLength(handle);
        if (length
            > RecapPlannerConfigCodec.MaxDocumentUtf8Bytes) {
            throw new ConfigFileTooLargeException(
                $"Planner config exceeds "
                + $"{RecapPlannerConfigCodec.MaxDocumentUtf8Bytes} "
                + "UTF-8 bytes."
            );
        }

        byte[] buffer = new byte[
            RecapPlannerConfigCodec.MaxDocumentUtf8Bytes + 1
        ];
        int total = 0;
        while (total < buffer.Length) {
            int read = RandomAccess.Read(
                handle,
                buffer.AsSpan(total),
                fileOffset: total
            );
            if (read == 0) {
                break;
            }
            total += read;
        }
        if (total > RecapPlannerConfigCodec.MaxDocumentUtf8Bytes) {
            throw new ConfigFileTooLargeException(
                $"Planner config exceeds "
                + $"{RecapPlannerConfigCodec.MaxDocumentUtf8Bytes} "
                + "UTF-8 bytes."
            );
        }
        return buffer.AsSpan(0, total).ToArray();
    }

    private static RecapPlannerConfigLoadResult.Invalid Invalid(
        string path,
        RecapPlannerConfigDefect defect
    ) => new(path, Array.AsReadOnly([defect]));

    private static bool IsPathException(Exception exception)
        => exception is ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static bool IsIoException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException;

    private sealed class ConfigFileTooLargeException(string message)
        : Exception(message);

    private sealed class ConfigFileNotRegularException(string message)
        : Exception(message);
}

internal enum RecapPlannerConfigInitializeIoPoint {
    ConfigDirectoryCreated = 1,
    RepositoryRootBarrier = 2,
    TemporaryFileBarrier = 3,
    ConfigPublished = 4,
    ConfigDirectoryBarrier = 5
}

internal sealed record RecapPlannerConfigInitializerTestHooks(
    Action<RecapPlannerConfigInitializeIoPoint, string>? BeforeIo = null,
    Action<string>? AfterPublishCollision = null
);

public static class RecapPlannerConfigInitializer {
    public static RecapPlannerConfigInitializeResult Initialize(
        string repositoryRoot,
        RecapPlannerConfigDocument document
    ) => Initialize(
        repositoryRoot,
        document,
        new RecapPlannerConfigInitializerTestHooks()
    );

    internal static RecapPlannerConfigInitializeResult Initialize(
        string repositoryRoot,
        RecapPlannerConfigDocument document,
        RecapPlannerConfigInitializerTestHooks testHooks
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(testHooks);
        if (!OperatingSystem.IsLinux()) {
            return new RecapPlannerConfigInitializeResult.Unavailable(
                SafePath(repositoryRoot),
                "RecapPlannerConfig durable initialization requires "
                + "Linux directory fsync semantics."
            );
        }
        IReadOnlyList<RecapPlannerConfigDefect> defects =
            RecapPlannerConfigCodec.ValidateDocument(document);
        if (defects.Count != 0) {
            return new RecapPlannerConfigInitializeResult.Invalid(
                SafePath(repositoryRoot),
                defects
            );
        }
        byte[] canonical;
        try {
            canonical =
                RecapPlannerConfigCodec.EncodeCanonical(document);
        }
        catch (InvalidDataException exception) {
            return new RecapPlannerConfigInitializeResult.Invalid(
                SafePath(repositoryRoot),
                Array.AsReadOnly([
                    new RecapPlannerConfigDefect(
                        RecapPlannerConfigDefectCodes
                            .SizeLimitExceeded,
                        exception.Message
                    )
                ])
            );
        }
        string expectedSha256 =
            RecapPlannerConfigCodec.ComputeSha256(canonical);

        string path;
        try {
            path = RecapPlannerConfigLoader.GetCanonicalPath(
                repositoryRoot
            );
        }
        catch (Exception exception) when (IsPathException(exception)) {
            return new RecapPlannerConfigInitializeResult.Unavailable(
                repositoryRoot ?? string.Empty,
                exception.Message
            );
        }

        string? temporaryPath = null;
        try {
            string root = Path.GetFullPath(repositoryRoot);
            PathCheckResult rootCheck =
                RecapPlannerConfigPathSafety.ValidateRepositoryRoot(
                    root
                );
            if (rootCheck.Defect is { } rootDefect) {
                return Invalid(path, rootDefect);
            }
            if (rootCheck.UnavailableReason is { } rootUnavailable) {
                return new RecapPlannerConfigInitializeResult.Unavailable(
                    path,
                    rootUnavailable
                );
            }

            string configDirectory =
                Path.GetDirectoryName(path)!;
            PathCheckResult beforeCreate =
                RecapPlannerConfigPathSafety.ValidateConfigDirectory(
                    root,
                    configDirectory,
                    allowMissing: true
                );
            if (beforeCreate.Defect is { } beforeDefect) {
                return Invalid(path, beforeDefect);
            }
            if (beforeCreate.UnavailableReason
                is { } beforeUnavailable) {
                return new RecapPlannerConfigInitializeResult.Unavailable(
                    path,
                    beforeUnavailable
                );
            }
            if (!beforeCreate.Exists) {
                Directory.CreateDirectory(configDirectory);
                Observe(
                    testHooks,
                    RecapPlannerConfigInitializeIoPoint
                        .ConfigDirectoryCreated,
                    configDirectory
                );
            }

            PathCheckResult directoryCheck =
                RecapPlannerConfigPathSafety.ValidateConfigDirectory(
                    root,
                    configDirectory,
                    allowMissing: false
                );
            if (directoryCheck.Defect is { } directoryDefect) {
                return Invalid(path, directoryDefect);
            }
            if (directoryCheck.UnavailableReason
                is { } directoryUnavailable) {
                return new RecapPlannerConfigInitializeResult.Unavailable(
                    path,
                    directoryUnavailable
                );
            }

            PathCheckResult existingCheck =
                RecapPlannerConfigPathSafety.ValidateConfigFile(
                    root,
                    configDirectory,
                    path,
                    allowMissing: true
                );
            if (existingCheck.Defect is { } existingDefect) {
                return Invalid(path, existingDefect);
            }
            if (existingCheck.UnavailableReason
                is { } existingUnavailable) {
                return new RecapPlannerConfigInitializeResult.Unavailable(
                    path,
                    existingUnavailable
                );
            }
            // This also repairs a prior attempt that created config/ but
            // failed before its parent-directory barrier completed.
            FlushDirectory(
                root,
                RecapPlannerConfigInitializeIoPoint
                    .RepositoryRootBarrier,
                testHooks
            );
            if (existingCheck.Exists) {
                // This also repairs a prior attempt that renamed the file
                // but failed before its directory barrier completed.
                FlushDirectory(
                    configDirectory,
                    RecapPlannerConfigInitializeIoPoint
                        .ConfigDirectoryBarrier,
                    testHooks
                );
                return new RecapPlannerConfigInitializeResult
                    .AlreadyExists(path);
            }

            (temporaryPath, FileStream temporary) =
                CreateTemporary(configDirectory);
            using (temporary) {
                temporary.Write(canonical);
                Observe(
                    testHooks,
                    RecapPlannerConfigInitializeIoPoint
                        .TemporaryFileBarrier,
                    temporaryPath
                );
                temporary.Flush(flushToDisk: true);
            }

            PathCheckResult beforePublish =
                RecapPlannerConfigPathSafety.ValidateConfigDirectory(
                    root,
                    configDirectory,
                    allowMissing: false
                );
            if (beforePublish.Defect is { } publishDefect) {
                return Invalid(path, publishDefect);
            }
            if (beforePublish.UnavailableReason
                is { } publishUnavailable) {
                return new RecapPlannerConfigInitializeResult.Unavailable(
                    path,
                    publishUnavailable
                );
            }

            try {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (
                File.Exists(path) || Directory.Exists(path)
            ) {
                testHooks.AfterPublishCollision?.Invoke(path);
                PathCheckResult racedTarget =
                    RecapPlannerConfigPathSafety.ValidateConfigFile(
                        root,
                        configDirectory,
                        path,
                        allowMissing: false
                    );
                if (racedTarget.Defect is { } raceDefect) {
                    return Invalid(path, raceDefect);
                }
                if (racedTarget.UnavailableReason
                    is { } raceUnavailable) {
                    return new RecapPlannerConfigInitializeResult
                        .Unavailable(path, raceUnavailable);
                }
                if (!racedTarget.Exists) {
                    return new RecapPlannerConfigInitializeResult
                        .Unavailable(
                        path,
                        "Raced planner config target is unavailable."
                    );
                }
                FlushDirectory(
                    configDirectory,
                    RecapPlannerConfigInitializeIoPoint
                        .ConfigDirectoryBarrier,
                    testHooks
                );
                return new RecapPlannerConfigInitializeResult
                    .AlreadyExists(path);
            }
            temporaryPath = null;
            Observe(
                testHooks,
                RecapPlannerConfigInitializeIoPoint.ConfigPublished,
                path
            );
            FlushDirectory(
                configDirectory,
                RecapPlannerConfigInitializeIoPoint
                    .ConfigDirectoryBarrier,
                testHooks
            );

            RecapPlannerConfigLoadResult published =
                RecapPlannerConfigLoader.Load(root);
            return published switch {
                RecapPlannerConfigLoadResult.Available available
                    when string.Equals(
                        available.ConfigSha256,
                        expectedSha256,
                        StringComparison.Ordinal
                    ) =>
                    new RecapPlannerConfigInitializeResult.Initialized(
                        path,
                        expectedSha256
                    ),
                RecapPlannerConfigLoadResult.Available =>
                    new RecapPlannerConfigInitializeResult.Unavailable(
                        path,
                        "Published planner config changed before "
                        + "post-publication verification."
                    ),
                RecapPlannerConfigLoadResult.Invalid invalid =>
                    new RecapPlannerConfigInitializeResult.Invalid(
                        path,
                        invalid.Defects
                    ),
                RecapPlannerConfigLoadResult.Missing =>
                    new RecapPlannerConfigInitializeResult.Unavailable(
                        path,
                        "Published planner config disappeared before "
                        + "post-publication verification."
                    ),
                RecapPlannerConfigLoadResult.Unavailable unavailable =>
                    new RecapPlannerConfigInitializeResult.Unavailable(
                        path,
                        unavailable.Reason
                    ),
                _ => throw new InvalidOperationException(
                    "Unknown post-publication planner config "
                    + "load result."
                )
            };
        }
        catch (Exception exception) when (IsIoException(exception)) {
            return new RecapPlannerConfigInitializeResult.Unavailable(
                path,
                exception.Message
            );
        }
        finally {
            if (temporaryPath is not null) {
                try {
                    File.Delete(temporaryPath);
                }
                catch {
                    // Best effort for an unpublished temporary file.
                }
            }
        }
    }

    private static (
        string Path,
        FileStream Stream
    ) CreateTemporary(string directory) {
        while (true) {
            string path = Path.Combine(
                directory,
                $".{RecapPlannerConfigLoader.ConfigFileName}."
                + $"{Guid.NewGuid():N}.tmp"
            );
            try {
                return (
                    path,
                    new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.SequentialScan
                    )
                );
            }
            catch (IOException) when (File.Exists(path)) {
                // Try another unpredictable same-directory name.
            }
        }
    }

    private static void Observe(
        RecapPlannerConfigInitializerTestHooks hooks,
        RecapPlannerConfigInitializeIoPoint point,
        string path
    ) => hooks.BeforeIo?.Invoke(point, path);

    private static void FlushDirectory(
        string path,
        RecapPlannerConfigInitializeIoPoint point,
        RecapPlannerConfigInitializerTestHooks hooks
    ) {
        Observe(hooks, point, path);
        int descriptor = NativeOpen(
            path,
            OpenReadOnly | OpenDirectory | OpenCloseOnExec
        );
        if (descriptor < 0) {
            throw NativeIOException(
                $"Failed to open planner config directory for fsync: "
                + path
            );
        }
        try {
            if (NativeFsync(descriptor) != 0) {
                throw NativeIOException(
                    $"Failed to fsync planner config directory: {path}"
                );
            }
        }
        finally {
            _ = NativeClose(descriptor);
        }
    }

    private static IOException NativeIOException(string message)
        => new(
            message,
            new System.ComponentModel.Win32Exception(
                Marshal.GetLastPInvokeError()
            )
        );

    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenCloseOnExec = 0x80000;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen(
        string path,
        int flags
    );

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int NativeFsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int descriptor);

    private static RecapPlannerConfigInitializeResult.Invalid Invalid(
        string path,
        RecapPlannerConfigDefect defect
    ) => new(path, Array.AsReadOnly([defect]));

    private static string SafePath(string? repositoryRoot) {
        try {
            return RecapPlannerConfigLoader.GetCanonicalPath(
                repositoryRoot!
            );
        }
        catch {
            return repositoryRoot ?? string.Empty;
        }
    }

    private static bool IsPathException(Exception exception)
        => exception is ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static bool IsIoException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException;
}

internal sealed record PathCheckResult(
    bool Exists,
    RecapPlannerConfigDefect? Defect = null,
    string? UnavailableReason = null
);

internal static class RecapPlannerConfigPathSafety {
    internal static PathCheckResult ValidateRepositoryRoot(
        string root
    ) {
        PathCheckResult chain = ValidateExistingChain(root, root);
        if (chain.Defect is not null
            || chain.UnavailableReason is not null) {
            return chain;
        }
        if (!Directory.Exists(root)) {
            if (File.Exists(root)) {
                return Unsafe(
                    $"Planner config repository root must be a "
                    + $"directory: {root}"
                );
            }
            return new PathCheckResult(
                Exists: false,
                UnavailableReason:
                    $"SessionJournal repository root does not exist: {root}"
            );
        }
        return ValidateDirectory(root, "repository root");
    }

    internal static PathCheckResult ValidateConfigDirectory(
        string root,
        string directory,
        bool allowMissing
    ) {
        PathCheckResult chain =
            ValidateExistingChain(root, directory);
        if (chain.Defect is not null
            || chain.UnavailableReason is not null) {
            return chain;
        }
        if (!Directory.Exists(directory)) {
            if (File.Exists(directory)) {
                return Unsafe(
                    $"Planner config path component is not a "
                    + $"directory: {directory}"
                );
            }
            return allowMissing
                ? new PathCheckResult(Exists: false)
                : new PathCheckResult(
                    Exists: false,
                    UnavailableReason:
                        $"Planner config directory is missing: "
                        + directory
                );
        }
        return ValidateDirectory(directory, "config directory");
    }

    internal static PathCheckResult ValidateConfigFile(
        string root,
        string directory,
        string path,
        bool allowMissing
    ) {
        PathCheckResult chain = ValidateExistingChain(root, path);
        if (chain.Defect is not null
            || chain.UnavailableReason is not null) {
            return chain;
        }

        FileAttributes attributes;
        try {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) {
            return allowMissing
                ? new PathCheckResult(Exists: false)
                : new PathCheckResult(
                    Exists: false,
                    UnavailableReason:
                        $"Planner config is missing: {path}"
                );
        }
        catch (DirectoryNotFoundException) {
            return allowMissing
                ? new PathCheckResult(Exists: false)
                : new PathCheckResult(
                    Exists: false,
                    UnavailableReason:
                        $"Planner config directory is missing: "
                        + directory
                );
        }
        catch (Exception exception) when (IsIoException(exception)) {
            return new PathCheckResult(
                Exists: false,
                UnavailableReason: exception.Message
            );
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0) {
            return Unsafe(
                $"Planner config must not be a symbolic link or "
                + $"reparse point: {path}"
            );
        }
        if ((attributes & FileAttributes.Directory) != 0) {
            return Unsafe(
                $"Planner config must be a regular file: {path}"
            );
        }
        if ((attributes & FileAttributes.Device) != 0) {
            return Unsafe(
                $"Planner config must not be a device: {path}"
            );
        }
        return new PathCheckResult(Exists: true);
    }

    private static PathCheckResult ValidateDirectory(
        string path,
        string description
    ) {
        try {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) {
                return Unsafe(
                    $"Planner config {description} must not be a "
                    + $"symbolic link or reparse point: {path}"
                );
            }
            if ((attributes & FileAttributes.Directory) == 0) {
                return Unsafe(
                    $"Planner config {description} must be a "
                    + $"directory: {path}"
                );
            }
            return new PathCheckResult(Exists: true);
        }
        catch (Exception exception) when (IsIoException(exception)) {
            return new PathCheckResult(
                Exists: false,
                UnavailableReason: exception.Message
            );
        }
    }

    private static PathCheckResult ValidateExistingChain(
        string root,
        string leaf
    ) {
        string rootFull = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root)
        );
        string leafFull = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(leaf)
        );
        if (!IsSameOrDescendant(rootFull, leafFull)) {
            return Unsafe(
                $"Planner config path escapes repository root: "
                + leafFull
            );
        }

        string? current = leafFull;
        while (current is not null) {
            try {
                FileAttributes attributes =
                    File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    return Unsafe(
                        "Planner config path must not contain a "
                        + $"symbolic link or reparse point: {current}"
                    );
                }
            }
            catch (FileNotFoundException) {
                // Missing config descendants are permitted to init.
            }
            catch (DirectoryNotFoundException) {
                // Missing config descendants are permitted to init.
            }
            catch (Exception exception) when (
                IsIoException(exception)
            ) {
                return new PathCheckResult(
                    Exists: false,
                    UnavailableReason: exception.Message
                );
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null) {
                break;
            }
            current = parent;
        }
        return new PathCheckResult(Exists: true);
    }

    private static bool IsSameOrDescendant(
        string root,
        string candidate
    ) {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.Equals(root, comparison)
            || candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                comparison
            );
    }

    private static PathCheckResult Unsafe(string detail) => new(
        Exists: false,
        Defect: new RecapPlannerConfigDefect(
            RecapPlannerConfigDefectCodes.UnsafePath,
            detail
        )
    );

    private static bool IsIoException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException;
}
