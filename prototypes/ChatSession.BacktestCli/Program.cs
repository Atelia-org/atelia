using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Maintainers;
using SJ = Atelia.SessionJournal;

namespace ChatSessionBacktestCli;

internal static partial class Program {
    private const int DefaultThresholdTokens = 24_000;
    private const string DefaultLlmSmokeCallLogDir = "gitignore/backtest/llm-smoke-calls";
    private const string DefaultRollingSummaryCallLogDir = "gitignore/backtest/rolling-summary-calls";
    private const string DefaultSessionJournalRollingSummaryCallLogDir = "gitignore/backtest/session-journal-rolling-summary-calls";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Main(string[] args)
        => MainCore(args, new DefaultCompletionClientFactory());

    internal static int MainCore(string[] args, ICompletionClientFactory completionClientFactory) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        try {
            if (args.Length == 0 || args[0] is "-h" or "--help") {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            var command = args[0];
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            return command switch {
                "inspect" => RunInspect(options),
                "export-legacy-upgrade" => RunExportLegacyUpgrade(options),
                "export-legacy-upgrade-markdown" =>
                    RunExportLegacyUpgradeMarkdown(options),
                "import-session-journal" => RunImportSessionJournal(options),
                "validate-session-journal" => RunValidateSessionJournalAsync(options).GetAwaiter().GetResult(),
                "checkpoint-artifact-set-session-journal" =>
                    RunCheckpointArtifactSetSessionJournalAsync(options).GetAwaiter().GetResult(),
                "llm-smoke" => RunLlmSmokeAsync(options, completionClientFactory).GetAwaiter().GetResult(),
                "replay-pattern-count" => RunReplayPatternCount(options),
                "replay-rolling-summary" => RunReplayRollingSummaryAsync(options, completionClientFactory).GetAwaiter().GetResult(),
                "replay-rolling-summary-session-journal" => RunSessionJournalRollingSummaryAsync(options, completionClientFactory).GetAwaiter().GetResult(),
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

    private static int RunInspect(CliOptions options) {
        var inputPath = options.Require("input");
        var eventSource = ChatSessionLegacyEventSourceReader.Read(inputPath);
        var messageKindCounts = CountMessageKinds(eventSource.Events);

        Console.WriteLine($"schema: {eventSource.Schema}");
        Console.WriteLine($"branchName: {eventSource.BranchName ?? "(none)"}");
        Console.WriteLine($"eventCount: {eventSource.Events.Count}");
        Console.WriteLine("eventKinds:");
        foreach (var item in eventSource.Events.GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)) {
            Console.WriteLine($"  {item.Key}: {item.Count()}");
        }

        Console.WriteLine("messageKinds:");
        foreach (var item in messageKindCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            Console.WriteLine($"  {item.Key}: {item.Value}");
        }

        return 0;
    }

    private static int RunExportLegacyUpgrade(CliOptions options) {
        string inputPath = options.Require("input");
        string outputPath = options.Require("output");
        string branchName = options.Get("branch") ?? "main";
        var exportOptions = new ChatSessionLegacyUpgradeExportOptions(
            WriteIndented: !options.HasFlag("compact")
        );
        EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        EnsurePathChainHasNoReparsePoint(outputPath, "--output");
        EnsurePathIsOutsideRepository(inputPath, outputPath, "--output");

        string json = ChatSessionLegacyUpgradeExporter.ExportJson(
            inputPath,
            branchName,
            exportOptions
        );
        WriteTextAtomically(outputPath, json);

        Console.WriteLine($"input: {Path.GetFullPath(inputPath)}");
        Console.WriteLine($"branchName: {branchName}");
        Console.WriteLine($"output: {Path.GetFullPath(outputPath)}");
        return 0;
    }

    private static int RunExportLegacyUpgradeMarkdown(CliOptions options) {
        string inputPath = options.Require("input");
        string outputPath = options.Require("output");
        string branchName = options.Get("branch") ?? "main";
        var exportOptions = new ChatSessionLegacyUpgradeMarkdownExportOptions(
            IncludeWarnings: !options.HasFlag("exclude-warnings")
        );
        EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        EnsurePathChainHasNoReparsePoint(outputPath, "--output");
        EnsurePathIsOutsideRepository(inputPath, outputPath, "--output");

        string markdown =
            ChatSessionLegacyUpgradeMarkdownExporter.ExportMarkdown(
                inputPath,
                branchName,
                exportOptions
            );
        WriteTextAtomically(outputPath, markdown);

        Console.WriteLine($"input: {Path.GetFullPath(inputPath)}");
        Console.WriteLine($"branchName: {branchName}");
        Console.WriteLine($"output: {Path.GetFullPath(outputPath)}");
        return 0;
    }

    private static int RunReplayPatternCount(CliOptions options) {
        var inputPath = options.Require("input");
        var outputPath = options.Require("output");
        var reportPath = options.Get("report-md");
        var thresholdTokens = options.GetInt("threshold-tokens", DefaultThresholdTokens);
        var mode = options.HasFlag("respect-original-compaction")
            ? ChatSessionLegacyReplayMode.RespectOriginalCompaction
            : ChatSessionLegacyReplayMode.IgnoreOriginalCompaction;

        var eventSource = ChatSessionLegacyEventSourceReader.Read(inputPath);
        var cursor = new ChatSessionLegacyReplayCursor(eventSource, mode);
        var memoryPack = new MemoryPack();
        var lastRecord = default(PatternReplayRecord);
        var recordCount = 0;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);

        while (cursor.TryStep(out var step)) {
            if (step.Event.Kind != ChatSessionLegacyEventKinds.ModelTurn || step.MessageCount == 0) { continue; }

            var historyMessages = cursor.CurrentHistoryMessages;
            var estimatedTokens = BacktestTextUtil.EstimateTokens(historyMessages);
            if (estimatedTokens < thresholdTokens) { continue; }

            var oldBlock = memoryPack.Action.GetValueOrDefault(NotButPatternAnalyzer.BlockId);
            var oldCount = NotButPatternAnalyzer.ExtractCount(oldBlock?.Text);
            var analysis = NotButPatternAnalyzer.Analyze(historyMessages);
            var newBlock = NotButPatternAnalyzer.RenderBlock(analysis);
            var draft = new MemoryPackDraft(memoryPack);
            draft.UpsertBlock(new MemoryPackBlockPath(MemoryPackCarrier.Action, NotButPatternAnalyzer.BlockId), newBlock);
            memoryPack = draft.Build();

            var record = NotButPatternAnalyzer.CreateReplayRecord(
                step,
                estimatedTokens,
                oldBlock?.Text,
                newBlock,
                analysis,
                oldCount
            );
            writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
            lastRecord = record;
            recordCount++;
        }

        if (!string.IsNullOrWhiteSpace(reportPath)) { WriteMarkdownReport(reportPath, eventSource, mode, thresholdTokens, recordCount, lastRecord); }

        Console.WriteLine($"records: {recordCount}");
        Console.WriteLine($"output: {outputPath}");
        if (!string.IsNullOrWhiteSpace(reportPath)) { Console.WriteLine($"report: {reportPath}"); }
        return 0;
    }

    private static int RunImportSessionJournal(CliOptions options) {
        var inputPath = options.Require("input");
        var outputPath = options.Require("output");
        var reportPath = options.Get("report-md");
        bool force = options.HasFlag("force");

        var eventSource = ChatSessionLegacyEventSourceReader.Read(inputPath);
        var result = SessionJournalLegacyImporter.Import(eventSource, outputPath, force);
        SessionJournalLegacyImporter.VerifyImportedRepo(outputPath, result);

        Console.WriteLine($"schema: {eventSource.Schema}");
        Console.WriteLine($"branchName: {eventSource.BranchName ?? "(none)"}");
        Console.WriteLine($"output: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"sessionCreated: {result.SessionCreatedCount}");
        Console.WriteLine($"runtimeConfigSetups: {result.RuntimeConfigSetupCount}");
        Console.WriteLine($"systemPromptSetups: {result.SystemPromptSetupCount}");
        Console.WriteLine($"observations: {result.ObservationCount}");
        Console.WriteLine($"agentActions: {result.AgentActionCount}");
        Console.WriteLine($"skippedCompactions: {result.SkippedCompactionCount}");
        Console.WriteLine($"skippedRecaps: {result.SkippedRecapCount}");
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            SessionJournalLegacyImporter.WriteReport(reportPath, inputPath, outputPath, result);
            Console.WriteLine($"report: {Path.GetFullPath(reportPath)}");
        }

        return 0;
    }

    private static async Task<int> RunValidateSessionJournalAsync(
        CliOptions options
    ) {
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

        PrintSessionJournalValidation(report);
        if (!string.IsNullOrWhiteSpace(reportPath)) {
            Console.WriteLine($"report: {Path.GetFullPath(reportPath)}");
        }
        return 0;
    }

    private static async Task<int> RunCheckpointArtifactSetSessionJournalAsync(
        CliOptions options
    ) {
        string inputPath = options.Require("input");
        EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        IReadOnlyList<string?> memberValues = options.GetAll("member");
        if (memberValues.Count < 2) {
            throw new ArgumentException(
                "checkpoint-artifact-set-session-journal requires at least two repeated --member <role>=<artifact-id> options."
            );
        }
        var members = memberValues.Select(ParseArtifactSetMember).ToArray();

        SJ.SessionJournalOfflineValidationReport before =
            await SJ.SessionJournalOfflineValidator.ValidateAsync(
                inputPath,
                CancellationToken.None
            ).ConfigureAwait(false);
        using (var engine = SJ.SessionJournalEngine.Open(inputPath)) {
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
                "Artifact-set checkpoint did not produce exactly one usable ArtifactSetCommitted event."
            );
        }

        Console.WriteLine($"oldHead: {before.Head ?? "(none)"}");
        Console.WriteLine($"newHead: {after.Head ?? "(none)"}");
        Console.WriteLine(
            $"anchor: {after.ActiveArtifactSet.CommonAnchor}"
        );
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
                $"Invalid --member '{value}'; expected exactly <role>=<artifact-id>."
            );
        }
        return new SJ.SessionArtifactSetMemberSelection(
            value[..separator],
            value[(separator + 1)..]
        );
    }

    private static void PrintSessionJournalValidation(
        SJ.SessionJournalOfflineValidationReport report
    ) {
        Console.WriteLine($"head: {report.Head ?? "(none)"}");
        Console.WriteLine($"events: {report.EventCount}");
        Console.WriteLine($"logicalPayloadBytes: {report.LogicalPayloadBytes}");
        Console.WriteLine($"phase: {report.ExecutionPhase}");
        Console.WriteLine($"readiness: {report.Readiness}");
        Console.WriteLine(
            $"activeArtifactSet: {report.ActiveArtifactSet?.Address ?? "(none)"}"
        );
    }

    private static void WriteJsonAtomically<T>(string path, T value) {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        string temporaryPath =
            $"{fullPath}.tmp-{Guid.NewGuid():N}";
        try {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(value, JsonOptions),
                Encoding.UTF8
            );
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch {
            TryDeleteFile(temporaryPath);
            throw;
        }
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

    private static async Task<int> RunLlmSmokeAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        var connectionsPath = options.Require("connections");
        var requestedConnectionId = options.Get("connection");
        var callLogDir = options.Get("call-log-dir") ?? DefaultLlmSmokeCallLogDir;
        var message = options.Get("message") ?? "请用一句话回复：LLM smoke test ok。";

        var connections = CompletionConnectionConfigLoader.LoadFile(connectionsPath);
        using var registry = new CompletionConnectionRegistry(connections, completionClientFactory);

        if (!string.IsNullOrWhiteSpace(requestedConnectionId) && !registry.TryGet(requestedConnectionId, out _)) { throw new ArgumentException($"Unknown completion connection '{requestedConnectionId}'."); }

        var connection = registry.Resolve(requestedConnectionId);
        var client = registry.GetClient(connection.Id);
        var loggingClient = new LoggingCompletionClient(
            client,
            connection,
            callLogDir,
            new CompletionCallLogContext(Command: "llm-smoke")
        );

        var request = new CompletionRequest(
            ModelId: connection.ModelId,
            SystemPrompt: "You are a concise smoke-test assistant. Reply briefly.",
            Context: new IHistoryMessage[] { new ObservationMessage(message) },
            Tools: System.Collections.Immutable.ImmutableArray<ToolDefinition>.Empty
        );

        var result = await loggingClient.StreamCompletionAsync(request, observer: null, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"connection: {connection.Id}");
        Console.WriteLine($"provider: {loggingClient.Name}/{loggingClient.ApiSpecId}");
        Console.WriteLine($"callLogDir: {Path.GetFullPath(callLogDir)}");
        Console.WriteLine("response:");
        Console.WriteLine(result.Message.GetFlattenedText());
        if (result.Errors is { Count: > 0 }) {
            Console.WriteLine("errors:");
            foreach (var error in result.Errors) { Console.WriteLine($"- {error}"); }
        }

        return 0;
    }

    private static Task<int> RunReplayRollingSummaryAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) => RunRollingSummaryAsync(
        options,
        completionClientFactory,
        command: "replay-rolling-summary",
        defaultCallLogDir: DefaultRollingSummaryCallLogDir,
        sourceFactory: static inputPath => new LegacyRollingSummaryReplaySource(
            ChatSessionLegacyEventSourceReader.Read(inputPath)
        ),
        artifactWriterFactory: null,
        enforceSessionJournalPathBoundary: false
    );

    private static Task<int> RunSessionJournalRollingSummaryAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) => RunRollingSummaryAsync(
        options,
        completionClientFactory,
        command: "replay-rolling-summary-session-journal",
        defaultCallLogDir: DefaultSessionJournalRollingSummaryCallLogDir,
        sourceFactory: static inputPath => SessionJournalRollingSummaryReplaySource.Open(inputPath),
        artifactWriterFactory: static (inputPath, profile, client, connection) =>
            SessionJournalDerivedRecapWriter.Open(inputPath, profile, client, connection),
        enforceSessionJournalPathBoundary: true
    );

    private static async Task<int> RunRollingSummaryAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory,
        string command,
        string defaultCallLogDir,
        Func<string, IRollingSummaryReplaySource> sourceFactory,
        Func<
            string,
            ReplayMemoryMaintainerProfile,
            ICompletionClient,
            CompletionConnectionConfig,
            IRollingSummaryArtifactWriter
        >? artifactWriterFactory,
        bool enforceSessionJournalPathBoundary
    ) {
        var inputPath = options.Require("input");
        var outputPath = options.Require("output");
        var connectionsPath = options.Require("connections");
        var requestedConnectionId = options.Get("connection");
        var callLogDir = options.Get("call-log-dir") ?? defaultCallLogDir;
        var thresholdTokens = options.GetInt("threshold-tokens", DefaultThresholdTokens);
        var maxEpochs = options.GetInt("max-epochs", int.MaxValue);
        var preset = options.Get("preset") ?? "autobiographical-rewrite";

        if (enforceSessionJournalPathBoundary) {
            EnsurePathChainHasNoReparsePoint(inputPath, "--input");
            EnsurePathChainHasNoReparsePoint(outputPath, "--output");
            EnsurePathChainHasNoReparsePoint(callLogDir, "--call-log-dir");
            EnsurePathIsOutsideRepository(inputPath, outputPath, "--output");
            EnsurePathIsOutsideRepository(inputPath, callLogDir, "--call-log-dir");
        }

        var systemPromptOverride = ReadPromptOrNull(options.Get("system-prompt"));
        var userPromptOverride = ReadPromptOrNull(options.Get("prompt"));

        var connections = CompletionConnectionConfigLoader.LoadFile(connectionsPath);
        using var registry = new CompletionConnectionRegistry(connections, completionClientFactory);

        if (!string.IsNullOrWhiteSpace(requestedConnectionId) && !registry.TryGet(requestedConnectionId, out _)) { throw new ArgumentException($"Unknown completion connection '{requestedConnectionId}'."); }

        var connection = registry.Resolve(requestedConnectionId);
        var client = registry.GetClient(connection.Id);
        var profile = CreateReplayMaintainerProfile(preset, systemPromptOverride, userPromptOverride);
        IRollingSummaryReplaySource source = sourceFactory(inputPath);
        IRollingSummaryArtifactWriter? artifactWriter = artifactWriterFactory?.Invoke(
            inputPath,
            profile,
            client,
            connection
        );

        string fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? ".");
        Directory.CreateDirectory(callLogDir);

        var runner = new RollingSummaryReplayRunner(
            source,
            client,
            connection,
            profile,
            callLogDir,
            thresholdTokens,
            maxEpochs,
            artifactWriter,
            command
        );

        int recordCount = 0;
        (string temporaryOutputPath, FileStream temporaryOutput) = CreateTemporaryOutput(fullOutputPath);
        try {
            await using (temporaryOutput.ConfigureAwait(false)) {
                await using var writer = new StreamWriter(
                    temporaryOutput,
                    Encoding.UTF8,
                    bufferSize: 1024,
                    leaveOpen: true
                );
                await foreach (var record in runner.RunAsync(CancellationToken.None).ConfigureAwait(false)) {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions)).ConfigureAwait(false);
                    await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    recordCount++;
                }

                await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                await temporaryOutput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temporaryOutputPath, fullOutputPath, overwrite: true);
        }
        catch {
            TryDeleteFile(temporaryOutputPath);
            throw;
        }

        Console.WriteLine($"records: {recordCount}");
        Console.WriteLine($"connection: {connection.Id}");
        Console.WriteLine($"preset: {profile.PresetName}");
        Console.WriteLine($"output: {outputPath}");
        Console.WriteLine($"callLogDir: {Path.GetFullPath(callLogDir)}");
        return runner.HadFailure ? 1 : 0;
    }

    private static void EnsurePathChainHasNoReparsePoint(string path, string optionName) {
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
        string repositoryFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        string candidateFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string repositoryPrefix = Path.EndsInDirectorySeparator(repositoryFullPath)
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

    private static (string Path, FileStream Stream) CreateTemporaryOutput(string fullOutputPath) {
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
            // Best-effort cleanup must not hide the original replay failure.
        }
    }

    private static Dictionary<string, int> CountMessageKinds(IReadOnlyList<ChatSessionLegacyReplayEvent> events) {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var message in events.SelectMany(EnumerateMessages)) {
            counts.TryGetValue(message.Kind, out var count);
            counts[message.Kind] = count + 1;
        }

        return counts;
    }

    private static IEnumerable<ChatSessionLegacyMessageDto> EnumerateMessages(ChatSessionLegacyReplayEvent replayEvent) {
        if (replayEvent.Messages is not null) {
            foreach (var message in replayEvent.Messages) { yield return message; }
        }

        if (replayEvent.AppendedMessages is not null) {
            foreach (var message in replayEvent.AppendedMessages) { yield return message; }
        }

        if (replayEvent.RecapMessage is not null) { yield return replayEvent.RecapMessage; }
    }

    private static void WriteMarkdownReport(
        string reportPath,
        ChatSessionLegacyEventSource eventSource,
        ChatSessionLegacyReplayMode mode,
        int thresholdTokens,
        int recordCount,
        PatternReplayRecord? lastRecord
    ) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
        using var writer = new StreamWriter(reportPath, append: false, Encoding.UTF8);
        writer.WriteLine("# ChatSession Memory Backtest Report");
        writer.WriteLine();
        writer.WriteLine($"- schema: `{eventSource.Schema}`");
        writer.WriteLine($"- branchName: `{eventSource.BranchName ?? "(none)"}`");
        writer.WriteLine($"- replayMode: `{mode}`");
        writer.WriteLine($"- thresholdTokens: `{thresholdTokens}`");
        writer.WriteLine($"- records: `{recordCount}`");
        if (lastRecord is null) { return; }

        writer.WriteLine($"- finalCount: `{lastRecord.Count}`");
        writer.WriteLine();
        writer.WriteLine("## Final Block Tail");
        writer.WriteLine();
        writer.WriteLine("```text");
        writer.WriteLine(lastRecord.NewBlock.TailPreview);
        writer.WriteLine("```");
        writer.WriteLine();
        writer.WriteLine("## Last Delta Matches");
        foreach (var match in lastRecord.DeltaMatches) { writer.WriteLine($"- {match}"); }
    }

    private static int Fail(string message) {
        Console.Error.WriteLine($"error: {message}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp() {
        Console.WriteLine("ChatSession.BacktestCli");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  inspect --input <path>");
        Console.WriteLine("  export-legacy-upgrade --input <repo-dir> --output <json> [--branch <name>] [--compact]");
        Console.WriteLine("  export-legacy-upgrade-markdown --input <repo-dir> --output <md> [--branch <name>] [--exclude-warnings]");
        Console.WriteLine("  import-session-journal --input <path> --output <repo-dir> [--force] [--report-md <path>]");
        Console.WriteLine("  validate-session-journal --input <repo-dir> [--report-json <path-outside-repo>]");
        Console.WriteLine("  checkpoint-artifact-set-session-journal --input <repo-dir> --member <role>=<artifact-id> --member <role>=<artifact-id> [...]");
        Console.WriteLine("  llm-smoke --connections <path> [--connection <id>] [--call-log-dir <dir>] [--message <text>]");
        Console.WriteLine("  replay-pattern-count --input <path> --output <jsonl> [--report-md <path>] [--threshold-tokens <n>] [--respect-original-compaction]");
        Console.WriteLine("  replay-rolling-summary --input <path> --output <jsonl> --connections <path> [--preset autobiographical-rewrite|world-understanding-rewrite] [--connection <id>] [--call-log-dir <dir>] [--threshold-tokens <n>] [--max-epochs <n>] [--system-prompt <path>] [--prompt <path>]");
        Console.WriteLine("  replay-rolling-summary-session-journal --input <repo-dir> --output <jsonl> --connections <path> [--preset autobiographical-rewrite|world-understanding-rewrite] [--connection <id>] [--call-log-dir <dir>] [--threshold-tokens <n>] [--max-epochs <n>] [--system-prompt <path>] [--prompt <path>]");
    }

    private static string? ReadPromptOrNull(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : File.ReadAllText(path, Encoding.UTF8);

    private static ReplayMemoryMaintainerProfile CreateReplayMaintainerProfile(
        string preset,
        string? systemPromptOverride,
        string? userPromptOverride
    ) {
        switch (preset) {
            case "autobiographical-rewrite":
                return new ReplayMemoryMaintainerProfile(
                    preset,
                    ResolveRewriteProfile(
                        AutobiographicalRewriteProfiles.Default,
                        systemPromptOverride,
                        userPromptOverride
                    )
                );

            case "world-understanding-rewrite":
                return new ReplayMemoryMaintainerProfile(
                    preset,
                    ResolveRewriteProfile(
                        WorldUnderstandingRewriteProfiles.Default,
                        systemPromptOverride,
                        userPromptOverride
                    )
                );

            default:
                throw new ArgumentException($"Unsupported replay memory preset '{preset}'.");
        }
    }

    private static SJ.MemoryRewriteProfile ResolveRewriteProfile(
        SJ.MemoryRewriteProfile defaults,
        string? systemPromptOverride,
        string? userPromptOverride
    ) => new(
        defaults.Id,
        defaults.Target,
        systemPromptOverride ?? defaults.SystemPrompt,
        userPromptOverride ?? defaults.UserPrompt
    );

}

internal sealed class CliOptions {
    private readonly Dictionary<string, List<string?>> _values;

    private CliOptions(Dictionary<string, List<string?>> values) {
        _values = values;
    }

    public static CliOptions Parse(string[] args) {
        var values = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++) {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { throw new ArgumentException($"Unexpected argument '{arg}'."); }
            var key = arg[2..];
            if (string.IsNullOrWhiteSpace(key)) { throw new ArgumentException("Empty option name."); }
            if (!values.TryGetValue(key, out List<string?>? occurrences)) {
                occurrences = new List<string?>();
                values.Add(key, occurrences);
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) {
                occurrences.Add(null);
                continue;
            }

            occurrences.Add(args[++index]);
        }

        return new CliOptions(values);
    }

    public string Require(string key) {
        var value = Get(key);
        if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException($"Missing required option --{key}."); }
        return value;
    }

    public string? Get(string key)
        => _values.TryGetValue(key, out List<string?>? values)
            ? values[^1]
            : null;

    public IReadOnlyList<string?> GetAll(string key)
        => _values.TryGetValue(key, out List<string?>? values)
            ? values.AsReadOnly()
            : Array.AsReadOnly(Array.Empty<string?>());

    public int GetInt(string key, int defaultValue) {
        var value = Get(key);
        if (string.IsNullOrWhiteSpace(value)) { return defaultValue; }
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new ArgumentException($"--{key} must be a positive integer.");
    }

    public int RequirePositiveInt(string key) {
        var value = Require(key);
        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"--{key} must be a positive integer.");
    }

    public bool HasFlag(string key)
        => _values.ContainsKey(key);
}
