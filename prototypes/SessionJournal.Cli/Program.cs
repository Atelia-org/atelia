using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedMemory;
using Atelia.SessionJournal.Maintainers;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class Program {
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
                "publish-derived-artifact-set" =>
                    DerivedMemoryCommands.PublishAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "list-derived-artifact-sets" =>
                    DerivedMemoryCommands.ListAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "validate-derived-memory" =>
                    DerivedMemoryCommands.ValidateAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "rebuild-derived-artifact-set-latest" =>
                    DerivedMemoryCommands.RebuildLatestAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "configure-derived-artifact-planner" =>
                    DerivedMemoryCommands.ConfigurePlannerAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "plan-derived-artifact-epoch" =>
                    DerivedMemoryCommands.PlanEpochAsync(options)
                        .GetAwaiter()
                        .GetResult(),
                "list-derived-artifact-epochs" =>
                    DerivedMemoryCommands.ListEpochsAsync(options)
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

    private static void PrintValidation(
        SJ.SessionJournalOfflineValidationReport report
    ) {
        Console.WriteLine($"head: {report.Head ?? "(none)"}");
        Console.WriteLine($"events: {report.EventCount}");
        Console.WriteLine(
            $"logicalPayloadBytes: {report.LogicalPayloadBytes}"
        );
        Console.WriteLine($"phase: {report.ExecutionPhase}");
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
        options.EnsureOnly(
            "input",
            "epoch",
            "profile",
            "output",
            "connections",
            "connection",
            "call-log-dir",
            "system-prompt",
            "prompt",
            "candidate-id",
            "attempt-id"
        );
        string inputPath = options.RequireSingle("input");
        string epochId = options.RequireSingle("epoch");
        string profileName = options.RequireSingle("profile");
        string outputPath = options.RequireSingle("output");
        string connectionsPath = options.RequireSingle("connections");
        string? requestedConnectionId =
            options.GetOptionalSingle("connection");
        string callLogDir =
            options.GetOptionalSingle("call-log-dir")
            ?? DefaultMaintainerCallLogDir;
        string attemptId =
            options.GetOptionalSingle("attempt-id") ?? "attempt-1";

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
        EnsurePathsDoNotNest(
            outputPath,
            callLogDir,
            "--output and --call-log-dir must be disjoint paths."
        );
        string fullOutputPath = Path.GetFullPath(outputPath);
        if (Directory.Exists(fullOutputPath)) {
            throw new ArgumentException(
                "--output must be a file path, not an existing directory."
            );
        }

        string? systemPromptOverride =
            ReadPromptOrNull(options.GetOptionalSingle("system-prompt"));
        string? userPromptOverride =
            ReadPromptOrNull(options.GetOptionalSingle("prompt"));

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
        MemoryMaintainerProfileDescriptor profile =
            MemoryMaintainerProfileCatalog.Resolve(profileName)
                .WithPromptOverrides(
                    systemPromptOverride,
                    userPromptOverride
                );
        string candidateId =
            options.GetOptionalSingle("candidate-id")
            ?? $"prompt-{profile.PromptFingerprint[7..23]}";

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullOutputPath) ?? "."
        );
        Directory.CreateDirectory(callLogDir);

        var loggingClient = new LoggingCompletionClient(
            client,
            connection,
            callLogDir,
            new CompletionCallLogContext(
                Command: "run-memory-maintainer",
                MaintainerId: profile.RewriteProfile.Id,
                TargetCarrier:
                    SJ.MemoryPackCarrierTokens.ToStorageToken(
                        profile.RewriteProfile.Target.Carrier
                    ),
                TargetBlockId: profile.RewriteProfile.Target.BlockKey
            )
        );
        SJ.IMemoryBlockMaintainer maintainer = profile.Create(
            loggingClient,
            connection.ModelId
        );
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(inputPath);
        var runner = new DerivedMemoryMaintainerRunner(repository);
        using var engine = SJ.SessionJournalEngine.Open(inputPath);
        DerivedMemoryMaintainerRunResult result = await runner.RunAsync(
                engine,
                new DerivedMemoryMaintainerRunRequest(
                    epochId,
                    profile.RoleId,
                    profile.RewriteProfile.Id,
                    MemoryMaintainerProducerIdentity.Producer,
                    MemoryMaintainerProducerIdentity
                        .ComputeProducerFingerprint(
                            profile,
                            client,
                            connection
                        ),
                    profile.PromptFingerprint,
                    MemoryMaintainerProducerIdentity
                        .ComputeModelFingerprint(client, connection),
                    candidateId,
                    attemptId
                ),
                maintainer,
                () => loggingClient.WrittenCallLogPaths,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        MemoryMaintainerRunRecord record =
            MemoryMaintainerRunRecord.FromResult(
                profile,
                result,
                repository.Artifacts.ArtifactsDirectory
            );
        WriteJsonAtomically(fullOutputPath, record);

        Console.WriteLine($"epoch: {record.EpochId}");
        Console.WriteLine($"artifact: {record.ArtifactId}");
        Console.WriteLine($"connection: {connection.Id}");
        Console.WriteLine($"profile: {profile.ProfileName}");
        Console.WriteLine($"output: {outputPath}");
        Console.WriteLine(
            $"callLogDir: {Path.GetFullPath(callLogDir)}"
        );
        return 0;
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

    private static string? ReadPromptOrNull(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : File.ReadAllText(path, Encoding.UTF8);

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

    private static void EnsurePathsDoNotNest(
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
            "  llm-smoke --connections <path> [--connection <id>] "
            + "[--call-log-dir <dir>] [--message <text>]"
        );
        Console.WriteLine(
            "  run-memory-maintainer --input <repo-dir> --epoch <epoch-id> "
            + "--profile <"
            + "autobiographical-rewrite"
            + "|world-understanding-rewrite> "
            + "--output <json> --connections <path> "
            + "[--connection <id>] [--call-log-dir <dir>] "
            + "[--candidate-id <token>] [--attempt-id <token>] "
            + "[--system-prompt <path>] [--prompt <path>]"
        );
        Console.WriteLine(
            "  publish-derived-artifact-set --input <repo-dir> "
            + "--lineage <key> --coherence-group <token> "
            + "--policy-id <token> --policy-fingerprint <token> "
            + "--required-role <role=carrier/block> "
            + "[--optional-role <role=carrier/block>] "
            + "--member <role=artifact-id> "
            + "--expected-previous <none|set-id> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  list-derived-artifact-sets --input <repo-dir> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  validate-derived-memory --input <repo-dir> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  rebuild-derived-artifact-set-latest --input <repo-dir> "
            + "--lineage <key> --coherence-group <token> "
            + "--policy-id <token> --policy-fingerprint <token> "
            + "--required-role <role=carrier/block> "
            + "[--optional-role <role=carrier/block>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  configure-derived-artifact-planner --input <repo-dir> "
            + "--lineage <key> --coherence-group <token> "
            + "--topology-version <token> "
            + "--minimum-recent-tokens <n> --epoch-trigger-tokens <n> "
            + "--scheduling-headroom-tokens <n> --hard-limit-tokens <n> "
            + "--expected-current <none|config-id> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  plan-derived-artifact-epoch --input <repo-dir> "
            + "--lineage <key> --coherence-group <token> "
            + "--expected-previous <none|epoch-id> "
            + "--input-set <none|set-id> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  list-derived-artifact-epochs --input <repo-dir> "
            + "[--report-json <path-outside-repo>]"
        );
    }
}
