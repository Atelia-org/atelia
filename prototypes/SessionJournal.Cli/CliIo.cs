using System.Text.Encodings.Web;
using System.Text.Json;

namespace Atelia.SessionJournal.Cli;

internal static class CliIo {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static void EnsurePathChainHasNoReparsePoint(
        string path,
        string optionName
    ) {
        string currentPath = Path.GetFullPath(path);
        while (true) {
            try {
                FileAttributes attributes =
                    File.GetAttributes(currentPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new ArgumentException(
                        $"{optionName} must not contain a symbolic link or "
                        + $"reparse point: {currentPath}"
                    );
                }
            }
            catch (FileNotFoundException) {
                // A missing leaf is allowed; existing ancestors still matter.
            }
            catch (DirectoryNotFoundException) {
                // A missing leaf is allowed; existing ancestors still matter.
            }

            string? parentPath = Path.GetDirectoryName(currentPath);
            if (parentPath is null) { break; }
            currentPath = parentPath;
        }
    }

    internal static void ValidateFileOutputPath(
        string repositoryPath,
        string outputPath,
        string optionName
    ) {
        ValidateOutputPathShape(
            outputPath,
            optionName,
            expectDirectory: false
        );
        EnsurePathIsOutsideRepository(
            repositoryPath,
            outputPath,
            optionName
        );
        EnsureFilePathIsNotAncestorOfDirectory(
            outputPath,
            repositoryPath,
            $"{optionName} must not contain the input repository."
        );
    }

    internal static void ValidateDirectoryOutputPath(
        string repositoryPath,
        string outputPath,
        string optionName
    ) {
        ValidateOutputPathShape(
            outputPath,
            optionName,
            expectDirectory: true
        );
        EnsurePathsDoNotNest(
            outputPath,
            repositoryPath,
            $"{optionName} and the input repository must be disjoint."
        );
    }

    internal static void ValidateReadOnlyWritablePaths(
        IReadOnlyList<(string Path, string Option)> readOnlyPaths,
        IReadOnlyList<(string Path, string Option)> writablePaths
    ) {
        foreach ((string path, string option) in readOnlyPaths) {
            EnsurePathChainHasNoReparsePoint(path, option);
        }
        foreach ((string path, string option) in writablePaths) {
            EnsurePathChainHasNoReparsePoint(path, option);
        }
        foreach ((string writablePath, string writableOption) in
                 writablePaths) {
            foreach ((string readOnlyPath, string readOnlyOption) in
                     readOnlyPaths) {
                EnsurePathsDoNotNest(
                    writablePath,
                    readOnlyPath,
                    $"{writableOption} and {readOnlyOption} must be disjoint paths."
                );
            }
        }
    }

    internal static void EnsurePathIsOutsideRepository(
        string repositoryPath,
        string candidatePath,
        string optionName
    ) {
        string repositoryFullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath)
        );
        string candidateFullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidatePath)
        );
        StringComparison comparison = PathComparison;
        string repositoryPrefix =
            repositoryFullPath + Path.DirectorySeparatorChar;
        if (candidateFullPath.Equals(repositoryFullPath, comparison)
            || candidateFullPath.StartsWith(
                repositoryPrefix,
                comparison
            )) {
            throw new ArgumentException(
                $"{optionName} must be outside the input repository."
            );
        }
    }

    internal static void EnsurePathsDoNotOverlap(
        string inputFilePath,
        string outputDirectoryPath
    ) {
        string input = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(inputFilePath)
        );
        string output = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(outputDirectoryPath)
        );
        string outputPrefix = output + Path.DirectorySeparatorChar;
        if (input.Equals(output, PathComparison)
            || input.StartsWith(outputPrefix, PathComparison)) {
            throw new ArgumentException(
                "--output must not contain --input."
            );
        }
    }

    internal static void EnsurePathsAreDifferent(
        string firstPath,
        string secondPath,
        string errorMessage
    ) {
        string first = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(firstPath)
        );
        string second = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(secondPath)
        );
        if (first.Equals(second, PathComparison)) {
            throw new ArgumentException(errorMessage);
        }
    }

    internal static void EnsurePathsDoNotNest(
        string firstPath,
        string secondPath,
        string errorMessage
    ) {
        string first = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(firstPath)
        );
        string second = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(secondPath)
        );
        string firstPrefix = first + Path.DirectorySeparatorChar;
        string secondPrefix = second + Path.DirectorySeparatorChar;
        if (first.Equals(second, PathComparison)
            || first.StartsWith(secondPrefix, PathComparison)
            || second.StartsWith(firstPrefix, PathComparison)) {
            throw new ArgumentException(errorMessage);
        }
    }

    internal static void EnsureFilePathIsNotAncestorOfDirectory(
        string filePath,
        string directoryPath,
        string errorMessage
    ) {
        string file = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(filePath)
        );
        string directory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directoryPath)
        );
        string filePrefix = file + Path.DirectorySeparatorChar;
        if (directory.StartsWith(filePrefix, PathComparison)) {
            throw new ArgumentException(errorMessage);
        }
    }

    internal static void WriteJsonAtomically<T>(string path, T value) {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ?? "."
        );
        (string temporaryPath, FileStream temporaryStream) =
            CreateTemporaryOutput(fullPath);
        try {
            using (temporaryStream) {
                JsonSerializer.Serialize(
                    temporaryStream,
                    value,
                    JsonOptions
                );
                temporaryStream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static void ValidateOutputPathShape(
        string path,
        string optionName,
        bool expectDirectory
    ) {
        string fullPath = Path.GetFullPath(path);
        string currentPath = fullPath;
        bool isLeaf = true;
        while (true) {
            try {
                FileAttributes attributes =
                    File.GetAttributes(currentPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new ArgumentException(
                        $"{optionName} must not contain a symbolic link or "
                        + $"reparse point: {currentPath}"
                    );
                }
                bool isDirectory =
                    (attributes & FileAttributes.Directory) != 0;
                if (isLeaf && isDirectory != expectDirectory) {
                    string expected = expectDirectory
                        ? "a directory"
                        : "a file";
                    throw new ArgumentException(
                        $"{optionName} must identify {expected} output path."
                    );
                }
                if (!isLeaf && !isDirectory) {
                    throw new ArgumentException(
                        $"{optionName} has a non-directory ancestor: "
                        + currentPath
                    );
                }
            }
            catch (FileNotFoundException) {
                // Missing output components may be created by the command.
            }
            catch (DirectoryNotFoundException) {
                // Missing output components may be created by the command.
            }

            string? parentPath = Path.GetDirectoryName(currentPath);
            if (parentPath is null) {
                break;
            }
            currentPath = parentPath;
            isLeaf = false;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static (string Path, FileStream Stream) CreateTemporaryOutput(
        string fullOutputPath
    ) {
        string directory =
            Path.GetDirectoryName(fullOutputPath) ?? ".";
        string fileName = Path.GetFileName(fullOutputPath);
        while (true) {
            string temporaryPath = Path.Combine(
                directory,
                $".{fileName}.{Guid.NewGuid():N}.tmp"
            );
            try {
                return (
                    temporaryPath,
                    new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read
                    )
                );
            }
            catch (IOException) when (File.Exists(temporaryPath)) {
                // Reserve another unique path.
            }
        }
    }

    private static void TryDeleteFile(string path) {
        try {
            File.Delete(path);
        }
        catch {
            // Best-effort cleanup must not hide the original failure.
        }
    }
}
