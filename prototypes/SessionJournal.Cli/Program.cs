using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Maintainers;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class Program {
    private const int DefaultThresholdTokens = 24_000;
    private const string DefaultLlmSmokeCallLogDir =
        "gitignore/session-journal/llm-smoke-calls";
    private const string DefaultMaintainerCallLogDir =
        "gitignore/session-journal/memory-maintainer-calls";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Main(string[] args)
        => MainCore(args, new DefaultCompletionClientFactory());

    internal static int MainCore(
        string[] args,
        ICompletionClientFactory completionClientFactory
    ) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        try {
            if (args.Length == 0 || args[0] is "-h" or "--help") {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            string command = args[0];
            CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
            return command switch {
                "import-legacy-json" => RunImportLegacyJson(options),
                "validate" => RunValidateAsync(options)
                    .GetAwaiter()
                    .GetResult(),
                "checkpoint-artifact-set" =>
                    RunCheckpointArtifactSetAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "llm-smoke" => RunLlmSmokeAsync(
                        options,
                        completionClientFactory
                    )
                    .GetAwaiter()
                    .GetResult(),
                "run-memory-maintainer" => RunMemoryMaintainerAsync(
                        options,
                        completionClientFactory
                    )
                    .GetAwaiter()
                    .GetResult(),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or JsonException
                or NotSupportedException
                or TaskCanceledException
                or UnauthorizedAccessException
        ) {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunImportLegacyJson(CliOptions options) {
        string inputPath = options.Require("input");
        string outputPath = options.Require("output");
        string? reportPath = options.Get("report-md");
        bool force = options.HasFlag("force");

        EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        EnsurePathChainHasNoReparsePoint(outputPath, "--output");
        EnsurePathsDoNotOverlap(inputPath, outputPath);
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            EnsurePathChainHasNoReparsePoint(reportPath, "--report-md");
            EnsurePathsAreDifferent(
                inputPath,
                reportPath,
                "--report-md must not overwrite --input."
            );
            EnsurePathIsOutsideRepository(
                outputPath,
                reportPath,
                "--report-md"
            );
            EnsureFilePathIsNotAncestorOfDirectory(
                reportPath,
                outputPath,
                "--report-md must not contain --output."
            );
        }

        LegacyChatSessionExport export =
            LegacyChatSessionExportReader.Read(inputPath);
        SessionJournalLegacyImportResult result =
            SessionJournalLegacyImporter.Import(export, outputPath, force);
        SessionJournalLegacyImporter.VerifyImportedRepo(outputPath, result);

        Console.WriteLine($"schema: {export.Schema}");
        Console.WriteLine($"branchName: {export.BranchName ?? "(none)"}");
        Console.WriteLine($"output: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"sessionCreated: {result.SessionCreatedCount}");
        Console.WriteLine(
            $"runtimeConfigSetups: {result.RuntimeConfigSetupCount}"
        );
        Console.WriteLine(
            $"systemPromptSetups: {result.SystemPromptSetupCount}"
        );
        Console.WriteLine($"observations: {result.ObservationCount}");
        Console.WriteLine($"agentActions: {result.AgentActionCount}");
        Console.WriteLine(
            $"skippedCompactions: {result.SkippedCompactionCount}"
        );
        Console.WriteLine($"skippedRecaps: {result.SkippedRecapCount}");
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            SessionJournalLegacyImporter.WriteReport(
                reportPath,
                inputPath,
                outputPath,
                result
            );
            Console.WriteLine($"report: {Path.GetFullPath(reportPath)}");
        }
        return 0;
    }

    private static async Task<int> RunValidateAsync(CliOptions options) {
        string inputPath = options.Require("input");
        string? reportPath = options.Get("report-json");
        EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            EnsurePathChainHasNoReparsePoint(reportPath, "--report-json");
            EnsurePathIsOutsideRepository(
                inputPath,
                reportPath,
                "--report-json"
            );
        }

        SJ.SessionJournalOfflineValidationReport report =
            await SJ.SessionJournalOfflineValidator.ValidateAsync(
                inputPath,
                CancellationToken.None
            ).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            WriteJsonAtomically(reportPath, report);
        }

        PrintValidation(report);
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            Console.WriteLine($"report: {Path.GetFullPath(reportPath)}");
        }
        return 0;
    }

    private static async Task<int> RunCheckpointArtifactSetAsync(
        CliOptions options
    ) {
        string inputPath = options.Require("input");
        EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        IReadOnlyList<string?> memberValues = options.GetAll("member");
        if (memberValues.Count < 2) {
            throw new ArgumentException(
                "checkpoint-artifact-set requires at least two repeated "
                + "--member <role>=<artifact-id> options."
            );
        }
        SJ.SessionArtifactSetMemberSelection[] members = memberValues
            .Select(ParseArtifactSetMember)
            .ToArray();

        SJ.SessionJournalOfflineValidationReport before =
            await SJ.SessionJournalOfflineValidator.ValidateAsync(
                inputPath,
                CancellationToken.None
            ).ConfigureAwait(false);
        using (SJ.SessionJournalEngine engine =
               SJ.SessionJournalEngine.Open(inputPath)) {
            _ = await engine.CommitArtifactSetAsync(
                members,
                CancellationToken.None
            ).ConfigureAwait(false);
        }
        SJ.SessionJournalOfflineValidationReport after =
            await SJ.SessionJournalOfflineValidator.ValidateAsync(
                inputPath,
                CancellationToken.None
            ).ConfigureAwait(false);
        if (after.EventCount != checked(before.EventCount + 1)
            || after.ActiveArtifactSet is null
            || !string.Equals(
                after.Readiness,
                SJ.SessionJournalOfflineReadiness.ActiveCoherent,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Artifact-set checkpoint did not produce exactly one usable "
                + "ArtifactSetCommitted event."
            );
        }

        Console.WriteLine($"oldHead: {before.Head ?? "(none)"}");
        Console.WriteLine($"newHead: {after.Head ?? "(none)"}");
        Console.WriteLine($"anchor: {after.ActiveArtifactSet.CommonAnchor}");
        Console.WriteLine(
            $"roles: {string.Join(",", after.ActiveArtifactSet.Members.Select(static member => member.RoleId))}"
        );
        Console.WriteLine($"readiness: {after.Readiness}");
        return 0;
    }

    private static SJ.SessionArtifactSetMemberSelection ParseArtifactSetMember(
        string? value
    ) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                "--member requires a non-empty <role>=<artifact-id> value."
            );
        }
        int separator = value.IndexOf('=');
        if (separator <= 0
            || separator == value.Length - 1
            || value.IndexOf('=', separator + 1) >= 0) {
            throw new ArgumentException(
                $"Invalid --member '{value}'; expected exactly "
                + "<role>=<artifact-id>."
            );
        }
        return new SJ.SessionArtifactSetMemberSelection(
            value[..separator],
            value[(separator + 1)..]
        );
    }

    private static void PrintValidation(
        SJ.SessionJournalOfflineValidationReport report
    ) {
        Console.WriteLine($"head: {report.Head ?? "(none)"}");
        Console.WriteLine($"events: {report.EventCount}");
        Console.WriteLine(
            $"logicalPayloadBytes: {report.LogicalPayloadBytes}"
        );
        Console.WriteLine($"phase: {report.ExecutionPhase}");
        Console.WriteLine($"readiness: {report.Readiness}");
        Console.WriteLine(
            $"activeArtifactSet: "
            + $"{report.ActiveArtifactSet?.Address ?? "(none)"}"
        );
    }

    private static async Task<int> RunLlmSmokeAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        string connectionsPath = options.Require("connections");
        string? requestedConnectionId = options.Get("connection");
        string callLogDir =
            options.Get("call-log-dir") ?? DefaultLlmSmokeCallLogDir;
        string message = options.Get("message")
            ?? "请用一句话回复：LLM smoke test ok。";

        CompletionConnectionsFileConfig connections =
            CompletionConnectionConfigLoader.LoadFile(connectionsPath);
        using var registry = new CompletionConnectionRegistry(
            connections,
            completionClientFactory
        );
        ValidateRequestedConnection(registry, requestedConnectionId);

        CompletionConnectionConfig connection =
            registry.Resolve(requestedConnectionId);
        ICompletionClient client = registry.GetClient(connection.Id);
        var loggingClient = new LoggingCompletionClient(
            client,
            connection,
            callLogDir,
            new CompletionCallLogContext(Command: "llm-smoke")
        );
        var request = new CompletionRequest(
            ModelId: connection.ModelId,
            SystemPrompt:
                "You are a concise smoke-test assistant. Reply briefly.",
            Context: [new ObservationMessage(message)],
            Tools: []
        );

        CompletionResult result =
            await loggingClient.StreamCompletionAsync(
                request,
                observer: null,
                CancellationToken.None
            ).ConfigureAwait(false);
        Console.WriteLine($"connection: {connection.Id}");
        Console.WriteLine(
            $"provider: {loggingClient.Name}/{loggingClient.ApiSpecId}"
        );
        Console.WriteLine($"callLogDir: {Path.GetFullPath(callLogDir)}");
        Console.WriteLine("response:");
        Console.WriteLine(result.Message.GetFlattenedText());
        if (result.Errors is { Count: > 0 }) {
            Console.WriteLine("errors:");
            foreach (string error in result.Errors) {
                Console.WriteLine($"- {error}");
            }
        }
        return 0;
    }

    private static async Task<int> RunMemoryMaintainerAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        string inputPath = options.Require("input");
        string outputPath = options.Require("output");
        string connectionsPath = options.Require("connections");
        string? requestedConnectionId = options.Get("connection");
        string callLogDir =
            options.Get("call-log-dir") ?? DefaultMaintainerCallLogDir;
        int thresholdTokens = options.GetInt(
            "threshold-tokens",
            DefaultThresholdTokens
        );
        int maxEpochs = options.GetInt("max-epochs", int.MaxValue);
        string profileName =
            options.Get("profile") ?? "autobiographical-rewrite";

        EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        EnsurePathChainHasNoReparsePoint(outputPath, "--output");
        EnsurePathChainHasNoReparsePoint(
            callLogDir,
            "--call-log-dir"
        );
        EnsurePathIsOutsideRepository(
            inputPath,
            outputPath,
            "--output"
        );
        EnsurePathIsOutsideRepository(
            inputPath,
            callLogDir,
            "--call-log-dir"
        );

        string? systemPromptOverride =
            ReadPromptOrNull(options.Get("system-prompt"));
        string? userPromptOverride =
            ReadPromptOrNull(options.Get("prompt"));

        CompletionConnectionsFileConfig connections =
            CompletionConnectionConfigLoader.LoadFile(connectionsPath);
        using var registry = new CompletionConnectionRegistry(
            connections,
            completionClientFactory
        );
        ValidateRequestedConnection(registry, requestedConnectionId);

        CompletionConnectionConfig connection =
            registry.Resolve(requestedConnectionId);
        ICompletionClient client = registry.GetClient(connection.Id);
        MemoryMaintainerRunProfile profile = CreateMaintainerProfile(
            profileName,
            systemPromptOverride,
            userPromptOverride
        );
        SessionJournalMemoryMaintainerReplaySource source =
            SessionJournalMemoryMaintainerReplaySource.Open(inputPath);
        SessionJournalDerivedRecapWriter artifactWriter =
            SessionJournalDerivedRecapWriter.Open(
                inputPath,
                profile,
                client,
                connection
            );

        string fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullOutputPath) ?? "."
        );
        Directory.CreateDirectory(callLogDir);

        var runner = new MemoryMaintainerReplayRunner(
            source,
            client,
            connection,
            profile,
            callLogDir,
            thresholdTokens,
            maxEpochs,
            artifactWriter,
            command: "run-memory-maintainer"
        );
        int recordCount = 0;
        (string temporaryOutputPath, FileStream temporaryOutput) =
            CreateTemporaryOutput(fullOutputPath);
        try {
            await using (temporaryOutput.ConfigureAwait(false)) {
                await using var writer = new StreamWriter(
                    temporaryOutput,
                    Encoding.UTF8,
                    bufferSize: 1024,
                    leaveOpen: true
                );
                await foreach (
                    MemoryMaintainerRunRecord record in runner.RunAsync(
                        CancellationToken.None
                    ).ConfigureAwait(false)
                ) {
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(record, JsonOptions)
                    ).ConfigureAwait(false);
                    await writer.FlushAsync(
                        CancellationToken.None
                    ).ConfigureAwait(false);
                    recordCount++;
                }
                await temporaryOutput.FlushAsync(
                    CancellationToken.None
                ).ConfigureAwait(false);
            }
            File.Move(
                temporaryOutputPath,
                fullOutputPath,
                overwrite: true
            );
        }
        catch {
            TryDeleteFile(temporaryOutputPath);
            throw;
        }

        Console.WriteLine($"records: {recordCount}");
        Console.WriteLine($"connection: {connection.Id}");
        Console.WriteLine($"profile: {profile.ProfileName}");
        Console.WriteLine($"output: {outputPath}");
        Console.WriteLine(
            $"callLogDir: {Path.GetFullPath(callLogDir)}"
        );
        return runner.HadFailure ? 1 : 0;
    }

    private static void ValidateRequestedConnection(
        CompletionConnectionRegistry registry,
        string? requestedConnectionId
    ) {
        if (!string.IsNullOrWhiteSpace(requestedConnectionId)
            && !registry.TryGet(requestedConnectionId, out _)) {
            throw new ArgumentException(
                $"Unknown completion connection "
                + $"'{requestedConnectionId}'."
            );
        }
    }

    private static MemoryMaintainerRunProfile CreateMaintainerProfile(
        string profileName,
        string? systemPromptOverride,
        string? userPromptOverride
    ) {
        SJ.MemoryRewriteProfile defaults = profileName switch {
            "autobiographical-rewrite" =>
                AutobiographicalRewriteProfiles.Default,
            "world-understanding-rewrite" =>
                WorldUnderstandingRewriteProfiles.Default,
            _ => throw new ArgumentException(
                $"Unsupported memory maintainer profile '{profileName}'."
            )
        };
        return new MemoryMaintainerRunProfile(
            profileName,
            new SJ.MemoryRewriteProfile(
                defaults.Id,
                defaults.Target,
                systemPromptOverride ?? defaults.SystemPrompt,
                userPromptOverride ?? defaults.UserPrompt
            )
        );
    }

    private static string? ReadPromptOrNull(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : File.ReadAllText(path, Encoding.UTF8);

    private static void EnsurePathChainHasNoReparsePoint(
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

    private static void EnsurePathsDoNotOverlap(
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

    private static void EnsurePathsAreDifferent(
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

    private static void EnsureFilePathIsNotAncestorOfDirectory(
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static void WriteJsonAtomically<T>(string path, T value) {
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

    private static int Fail(string message) {
        Console.Error.WriteLine($"error: {message}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp() {
        Console.WriteLine("SessionJournal.Cli");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine(
            "  import-legacy-json --input <json> --output <repo-dir> "
            + "[--force] [--report-md <path>]"
        );
        Console.WriteLine(
            "  validate --input <repo-dir> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  checkpoint-artifact-set --input <repo-dir> "
            + "--member <role>=<artifact-id> "
            + "--member <role>=<artifact-id> [...]"
        );
        Console.WriteLine(
            "  llm-smoke --connections <path> [--connection <id>] "
            + "[--call-log-dir <dir>] [--message <text>]"
        );
        Console.WriteLine(
            "  run-memory-maintainer --input <repo-dir> --output <jsonl> "
            + "--connections <path> "
            + "[--profile autobiographical-rewrite"
            + "|world-understanding-rewrite] "
            + "[--connection <id>] [--call-log-dir <dir>] "
            + "[--threshold-tokens <n>] [--max-epochs <n>] "
            + "[--system-prompt <path>] [--prompt <path>]"
        );
    }
}
