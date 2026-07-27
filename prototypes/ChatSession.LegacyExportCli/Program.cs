using System.Text;
using System.Text.Json;
using Atelia.ChatSession;

namespace Atelia.ChatSession.LegacyExportCli;

internal static class Program {
    public static int Main(string[] args)
        => MainCore(args);

    internal static int MainCore(string[] args) {
        ArgumentNullException.ThrowIfNull(args);

        try {
            if (args.Length == 0 || args[0] is "-h" or "--help") {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            string command = args[0];
            CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
            return command switch {
                "export-json" => RunExportJson(options),
                "export-markdown" => RunExportMarkdown(options),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or JsonException
                or NotSupportedException
                or UnauthorizedAccessException
        ) {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunExportJson(CliOptions options) {
        string inputPath = options.Require("input");
        string outputPath = options.Require("output");
        string branchName = options.Get("branch") ?? "main";
        var exportOptions = new ChatSessionLegacyUpgradeExportOptions(
            WriteIndented: !options.HasFlag("compact")
        );

        ValidateExportPaths(inputPath, outputPath);
        string json = ChatSessionLegacyUpgradeExporter.ExportJson(
            inputPath,
            branchName,
            exportOptions
        );
        WriteTextAtomically(outputPath, json);

        PrintResult(inputPath, branchName, outputPath);
        return 0;
    }

    private static int RunExportMarkdown(CliOptions options) {
        string inputPath = options.Require("input");
        string outputPath = options.Require("output");
        string branchName = options.Get("branch") ?? "main";
        var exportOptions = new ChatSessionLegacyUpgradeMarkdownExportOptions(
            IncludeWarnings: !options.HasFlag("exclude-warnings")
        );

        ValidateExportPaths(inputPath, outputPath);
        string markdown = ChatSessionLegacyUpgradeMarkdownExporter.ExportMarkdown(
            inputPath,
            branchName,
            exportOptions
        );
        WriteTextAtomically(outputPath, markdown);

        PrintResult(inputPath, branchName, outputPath);
        return 0;
    }

    private static void ValidateExportPaths(string inputPath, string outputPath) {
        EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        EnsurePathChainHasNoReparsePoint(outputPath, "--output");
        EnsurePathIsOutsideRepository(inputPath, outputPath, "--output");
    }

    private static void PrintResult(
        string inputPath,
        string branchName,
        string outputPath
    ) {
        Console.WriteLine($"input: {Path.GetFullPath(inputPath)}");
        Console.WriteLine($"branchName: {branchName}");
        Console.WriteLine($"output: {Path.GetFullPath(outputPath)}");
    }

    private static void WriteTextAtomically(string path, string content) {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        (string temporaryPath, FileStream temporaryStream) =
            CreateTemporaryOutput(fullPath);
        try {
            using (temporaryStream) {
                using var writer = new StreamWriter(
                    temporaryStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true
                );
                writer.Write(content);
                writer.Flush();
                temporaryStream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static void EnsurePathChainHasNoReparsePoint(
        string path,
        string optionName
    ) {
        string currentPath = Path.GetFullPath(path);
        while (true) {
            try {
                FileAttributes attributes = File.GetAttributes(currentPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new ArgumentException(
                        $"{optionName} must not contain a symbolic link or reparse point: {currentPath}"
                    );
                }
            }
            catch (FileNotFoundException) {
                // A candidate leaf may not exist yet; its existing ancestors still need checking.
            }
            catch (DirectoryNotFoundException) {
                // A candidate leaf may not exist yet; its existing ancestors still need checking.
            }

            string? parentPath = Path.GetDirectoryName(currentPath);
            if (parentPath is null) { break; }
            currentPath = parentPath;
        }
    }

    private static void EnsurePathIsOutsideRepository(
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
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string repositoryPrefix = Path.EndsInDirectorySeparator(
            repositoryFullPath
        )
            ? repositoryFullPath
            : repositoryFullPath + Path.DirectorySeparatorChar;

        if (
            candidateFullPath.Equals(repositoryFullPath, comparison)
            || candidateFullPath.StartsWith(repositoryPrefix, comparison)
        ) {
            throw new ArgumentException(
                $"{optionName} must be outside the input repository."
            );
        }
    }

    private static (string Path, FileStream Stream) CreateTemporaryOutput(
        string fullOutputPath
    ) {
        string directory = Path.GetDirectoryName(fullOutputPath) ?? ".";
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
                // An extremely unlikely name collision; reserve another unique path.
            }
        }
    }

    private static void TryDeleteFile(string path) {
        try {
            File.Delete(path);
        }
        catch {
            // Best-effort cleanup must not hide the original export failure.
        }
    }

    private static int Fail(string message) {
        Console.Error.WriteLine($"error: {message}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp() {
        Console.WriteLine("ChatSession.LegacyExportCli");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine(
            "  export-json --input <repo-dir> --output <json> [--branch <name>] [--compact]"
        );
        Console.WriteLine(
            "  export-markdown --input <repo-dir> --output <md> [--branch <name>] [--exclude-warnings]"
        );
    }
}

internal sealed class CliOptions {
    private readonly Dictionary<string, List<string?>> _values;

    private CliOptions(Dictionary<string, List<string?>> values) {
        _values = values;
    }

    public static CliOptions Parse(string[] args) {
        var values = new Dictionary<string, List<string?>>(
            StringComparer.Ordinal
        );
        for (int index = 0; index < args.Length; index++) {
            string arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            string key = arg[2..];
            if (string.IsNullOrWhiteSpace(key)) {
                throw new ArgumentException("Empty option name.");
            }

            if (!values.TryGetValue(key, out List<string?>? occurrences)) {
                occurrences = [];
                values.Add(key, occurrences);
            }

            if (
                index + 1 >= args.Length
                || args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ) {
                occurrences.Add(null);
                continue;
            }

            occurrences.Add(args[++index]);
        }

        return new CliOptions(values);
    }

    public string Require(string key) {
        string? value = Get(key);
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException($"Missing required option --{key}.");
        }

        return value;
    }

    public string? Get(string key)
        => _values.TryGetValue(key, out List<string?>? values)
            ? values[^1]
            : null;

    public bool HasFlag(string key)
        => _values.ContainsKey(key);
}
