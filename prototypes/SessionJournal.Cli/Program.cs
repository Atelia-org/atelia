using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using SJ = Atelia.SessionJournal;
using SJO = Atelia.SessionJournal.Offline;

namespace Atelia.SessionJournal.Cli;

internal static class Program {
    private const string DefaultLlmSmokeCallLogDir =
        "gitignore/session-journal/llm-smoke-calls";

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
            if (string.Equals(
                    command,
                    "recap",
                    StringComparison.Ordinal
                )) {
                return RecapStoreCommands.RunAsync(
                        args.Skip(1).ToArray(),
                        completionClientFactory
                    )
                    .GetAwaiter()
                    .GetResult();
            }
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
                "run-online-turn" => OnlineTurnCommand.RunAsync(
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
        options.EnsureOnly(
            "input",
            "output",
            "force",
            "report-md",
            "report-json"
        );
        string inputPath = options.RequireSingle("input");
        string outputPath = options.RequireSingle("output");
        string? markdownReportPath =
            options.GetOptionalSingle("report-md");
        string? jsonReportPath =
            options.GetOptionalSingle("report-json");
        if (options.GetAll("force").Count > 1) {
            throw new ArgumentException(
                "Option --force must be specified at most once."
            );
        }
        bool force = options.HasFlag("force");

        CliIo.EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        CliIo.EnsurePathChainHasNoReparsePoint(outputPath, "--output");
        CliIo.EnsurePathsDoNotOverlap(inputPath, outputPath);
        foreach ((string? reportPath, string option) in new[] {
                     (markdownReportPath, "--report-md"),
                     (jsonReportPath, "--report-json")
                 }) {
            if (reportPath is null) {
                continue;
            }
            CliIo.ValidateFileOutputPath(
                outputPath,
                reportPath,
                option
            );
            CliIo.EnsurePathsDoNotNest(
                inputPath,
                reportPath,
                $"{option} and --input must be disjoint paths."
            );
        }
        if (markdownReportPath is not null
            && jsonReportPath is not null) {
            CliIo.EnsurePathsDoNotNest(
                markdownReportPath,
                jsonReportPath,
                "--report-md and --report-json must be disjoint paths."
            );
        }

        LegacyChatSessionExportDocument document =
            LegacyChatSessionExportReader.ReadDocument(inputPath);
        LegacyChatSessionExport export = document.Export;
        SessionJournalLegacyImportResult result =
            SessionJournalLegacyImporter.Import(export, outputPath, force);
        SessionJournalLegacyImporter.VerifyImportedRepo(outputPath, result);
        SessionJournalLegacyImportReport report =
            SessionJournalLegacyImporter.CreateReport(
                document,
                inputPath,
                outputPath,
                result
            );

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
        if (markdownReportPath is not null) {
            SessionJournalLegacyImporter.WriteMarkdownReport(
                markdownReportPath,
                report
            );
            Console.WriteLine(
                $"markdownReport: {Path.GetFullPath(markdownReportPath)}"
            );
        }
        if (jsonReportPath is not null) {
            CliIo.WriteJsonAtomically(jsonReportPath, report);
            Console.WriteLine(
                $"jsonReport: {Path.GetFullPath(jsonReportPath)}"
            );
        }
        return 0;
    }

    private static async Task<int> RunValidateAsync(CliOptions options) {
        options.EnsureOnly("input", "branch", "report-json");
        string inputPath = options.RequireSingle("input");
        string branchName = options.GetOptionalSingle("branch")
            ?? SJ.SessionJournalDefaults.MainBranchName;
        string? reportPath =
            options.GetOptionalSingle("report-json");
        CliIo.EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            CliIo.EnsurePathChainHasNoReparsePoint(
                reportPath,
                "--report-json"
            );
            CliIo.EnsurePathIsOutsideRepository(
                inputPath,
                reportPath,
                "--report-json"
            );
        }

        SJO.SessionJournalOfflineValidationReport report =
            await SJO.SessionJournalOfflineValidator.ValidateAsync(
                inputPath,
                branchName,
                CancellationToken.None
            ).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }

        PrintValidation(report);
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            Console.WriteLine($"report: {Path.GetFullPath(reportPath)}");
        }
        return 0;
    }

    private static void PrintValidation(
        SJO.SessionJournalOfflineValidationReport report
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
            "  recap planner-config init --input <repo-dir> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap planner-config inspect --input <repo-dir> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap history-load inspect --input <repo-dir> "
            + "[--branch <name>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap create --input <repo-dir> --branch <name> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap inspect --input <repo-dir> --branch <name> "
            + "--anchor <event-address> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap materialize-inspect --input <repo-dir> "
            + "--branch <name> [--nth-previous <zero-based>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap run --input <repo-dir> --branch <name> "
            + "--connections <path> [--connection <id>] "
            + "[--call-log-dir <dir>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap resume --input <repo-dir> --branch <name> "
            + "--anchor <event-address> --connections <path> "
            + "[--connection <id>] [--call-log-dir <dir>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap restore --input <repo-dir> --branch <name> "
            + "--anchor <event-address> "
            + "--expected-raw-head <event-address> "
            + "--connections <path> [--connection <id>] "
            + "[--call-log-dir <dir>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap abandon-building --input <repo-dir> "
            + "--branch <name> --anchor <event-address> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  recap reset --input <repo-dir> --branch <name> "
            + "--confirm-ref <exact-ref-id> "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  import-legacy-json --input <json> --output <repo-dir> "
            + "[--force] [--report-md <path>] "
            + "[--report-json <path>]"
        );
        Console.WriteLine(
            "  validate --input <repo-dir> [--branch <name>] "
            + "[--report-json <path-outside-repo>]"
        );
        Console.WriteLine(
            "  llm-smoke --connections <path> [--connection <id>] "
            + "[--call-log-dir <dir>] [--message <text>]"
        );
        Console.WriteLine(
            "  run-online-turn --input <repo-dir> --branch <name> "
            + "--connections <path> --output <json> "
            + "[--message <text>] [--connection <id>] "
            + "[--call-log-dir <dir>] "
            + "[--maximum-canonical-request-bytes <n>] "
            + "[--uncertain-recovery refuse|restart-new-attempt]"
        );
    }
}
