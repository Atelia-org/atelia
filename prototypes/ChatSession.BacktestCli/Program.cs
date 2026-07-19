using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;

namespace ChatSessionBacktestCli;

internal static partial class Program {
    private const int DefaultThresholdTokens = 12_000;
    private const string DefaultLlmSmokeCallLogDir = "gitignore/backtest/llm-smoke-calls";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Main(string[] args) {
        try {
            if (args.Length == 0 || args[0] is "-h" or "--help") {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            var command = args[0];
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            return command switch {
                "inspect" => RunInspect(options),
                "llm-smoke" => RunLlmSmokeAsync(options).GetAwaiter().GetResult(),
                "replay-pattern-count" => RunReplayPatternCount(options),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or IOException or NotSupportedException) {
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

    private static async Task<int> RunLlmSmokeAsync(CliOptions options) {
        var connectionsPath = options.Require("connections");
        var requestedConnectionId = options.Get("connection");
        var callLogDir = options.Get("call-log-dir") ?? DefaultLlmSmokeCallLogDir;
        var message = options.Get("message") ?? "请用一句话回复：LLM smoke test ok。";

        var connections = CompletionConnectionConfigLoader.LoadFile(connectionsPath);
        using var registry = new CompletionConnectionRegistry(connections, new DefaultCompletionClientFactory());

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
        Console.WriteLine("  llm-smoke --connections <path> [--connection <id>] [--call-log-dir <dir>] [--message <text>]");
        Console.WriteLine("  replay-pattern-count --input <path> --output <jsonl> [--report-md <path>] [--threshold-tokens <n>] [--respect-original-compaction]");
    }

}

internal sealed class CliOptions {
    private readonly Dictionary<string, string?> _values;

    private CliOptions(Dictionary<string, string?> values) {
        _values = values;
    }

    public static CliOptions Parse(string[] args) {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++) {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { throw new ArgumentException($"Unexpected argument '{arg}'."); }
            var key = arg[2..];
            if (string.IsNullOrWhiteSpace(key)) { throw new ArgumentException("Empty option name."); }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) {
                values[key] = null;
                continue;
            }

            values[key] = args[++index];
        }

        return new CliOptions(values);
    }

    public string Require(string key) {
        if (!_values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) { throw new ArgumentException($"Missing required option --{key}."); }
        return value;
    }

    public string? Get(string key)
        => _values.TryGetValue(key, out var value) ? value : null;

    public int GetInt(string key, int defaultValue) {
        var value = Get(key);
        if (string.IsNullOrWhiteSpace(value)) { return defaultValue; }
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new ArgumentException($"--{key} must be a positive integer.");
    }

    public bool HasFlag(string key)
        => _values.ContainsKey(key);
}
