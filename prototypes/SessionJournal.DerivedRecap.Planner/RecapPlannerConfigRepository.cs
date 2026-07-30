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
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
        if (!stream.CanSeek) {
            throw new ConfigFileNotRegularException(
                "Planner config must be a regular, seekable file."
            );
        }
        if (stream.Length
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
            int read = stream.Read(buffer, total, buffer.Length - total);
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

public static class RecapPlannerConfigInitializer {
    public static RecapPlannerConfigInitializeResult Initialize(
        string repositoryRoot,
        RecapPlannerConfigDocument document
    ) {
        ArgumentNullException.ThrowIfNull(document);
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
            if (existingCheck.Exists) {
                return new RecapPlannerConfigInitializeResult
                    .AlreadyExists(path);
            }

            (temporaryPath, FileStream temporary) =
                CreateTemporary(configDirectory);
            using (temporary) {
                temporary.Write(canonical);
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
                temporaryPath = null;
            }
            catch (IOException) when (
                File.Exists(path) || Directory.Exists(path)
            ) {
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
                return new RecapPlannerConfigInitializeResult
                    .AlreadyExists(path);
            }

            return new RecapPlannerConfigInitializeResult.Initialized(
                path,
                RecapPlannerConfigCodec.ComputeSha256(canonical)
            );
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
