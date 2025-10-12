using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Provider.Stub;

internal sealed class StubProviderClient : IProviderClient {
    private const string DebugCategory = "Provider";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _scriptsDirectory;

    public StubProviderClient(string scriptsDirectory) {
        _scriptsDirectory = scriptsDirectory;
        DebugUtil.Print(DebugCategory, $"StubProviderClient initialized path={scriptsDirectory}");
    }

    public async IAsyncEnumerable<ModelOutputDelta> CallModelAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken
    ) {
        var scriptName = string.IsNullOrWhiteSpace(request.StubScriptName)
            ? "default"
            : request.StubScriptName;

        var script = await LoadScriptAsync(scriptName, cancellationToken);

        DebugUtil.Print(DebugCategory, $"[Stub] Executing script='{scriptName}' deltas={script.Deltas.Count}");

        foreach (var delta in script.Deltas) {
            cancellationToken.ThrowIfCancellationRequested();

            if (delta.DelayMilliseconds is { } delay and > 0) {
                await Task.Delay(delay, cancellationToken);
            }

            yield return ConvertDelta(delta, request);
        }
    }

    private async Task<StubScript> LoadScriptAsync(string scriptName, CancellationToken cancellationToken) {
        var path = ResolveScriptPath(scriptName);

        if (!File.Exists(path)) { throw new FileNotFoundException($"Stub script '{scriptName}' not found.", path); }

        await using var stream = File.OpenRead(path);
        var script = await JsonSerializer.DeserializeAsync<StubScript>(stream, SerializerOptions, cancellationToken);
        if (script is null) { throw new InvalidOperationException($"Failed to deserialize stub script '{scriptName}'."); }

        script.Normalize();
        return script;
    }

    private string ResolveScriptPath(string scriptName) {
        var basePath = Path.GetFullPath(_scriptsDirectory);
        var normalized = scriptName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fileName = normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".json";

        var fullPath = Path.GetFullPath(Path.Combine(basePath, fileName));
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException($"Script name '{scriptName}' escapes stub directory."); }

        return fullPath;
    }

    private static ModelOutputDelta ConvertDelta(StubDelta delta, ProviderRequest request) => delta.Type switch {
        "content" => ModelOutputDelta.Content(ApplyPlaceholders(delta.Content ?? string.Empty, request), delta.EndSegment),
        "toolCall" => ModelOutputDelta.ToolCall(CreateToolCall(delta.ToolCall ?? throw new InvalidOperationException("toolCall delta missing payload."))),
        "toolResult" => ModelOutputDelta.ToolResult(CreateToolResult(delta.ToolResult ?? throw new InvalidOperationException("toolResult delta missing payload."))),
        "executeError" => ModelOutputDelta.ExecutionError(delta.ExecuteError ?? "(unknown stub error)"),
        "tokenUsage" => ModelOutputDelta.Usage(CreateTokenUsage(delta.TokenUsage ?? throw new InvalidOperationException("tokenUsage delta missing payload."))),
        var other => throw new InvalidOperationException($"Unknown stub delta type '{other}'.")
    };

    private static ToolCallRequest CreateToolCall(StubToolCall stub) {
        var arguments = stub.Arguments is null
            ? null
            : new Dictionary<string, string>(stub.Arguments, StringComparer.OrdinalIgnoreCase);

        return new ToolCallRequest(
            stub.ToolName ?? throw new InvalidOperationException("toolCall.toolName is required."),
            stub.ToolCallId ?? throw new InvalidOperationException("toolCall.toolCallId is required."),
            stub.RawArguments ?? string.Empty,
            arguments,
            stub.ParseError
        );
    }

    private static ToolCallResult CreateToolResult(StubToolResult stub) {
        if (!Enum.TryParse<ToolExecutionStatus>(stub.Status, ignoreCase: true, out var status)) { throw new InvalidOperationException($"Unknown tool execution status '{stub.Status}'."); }

        TimeSpan? elapsed = null;
        if (stub.ElapsedMilliseconds is { } ms) {
            elapsed = TimeSpan.FromMilliseconds(ms);
        }

        return new ToolCallResult(
            stub.ToolName ?? throw new InvalidOperationException("toolResult.toolName is required."),
            stub.ToolCallId ?? throw new InvalidOperationException("toolResult.toolCallId is required."),
            status,
            stub.Result ?? string.Empty,
            elapsed
        );
    }

    private static TokenUsage CreateTokenUsage(StubTokenUsage stub) {
        if (stub.PromptTokens is null || stub.CompletionTokens is null) { throw new InvalidOperationException("tokenUsage requires promptTokens and completionTokens."); }

        return new TokenUsage(stub.PromptTokens.Value, stub.CompletionTokens.Value, stub.CachedPromptTokens);
    }

    private static string ApplyPlaceholders(string content, ProviderRequest request) {
        var result = content;
        var lastInput = ExtractLastUserInput(request.Context);

        if (!string.IsNullOrWhiteSpace(lastInput)) {
            result = result.Replace("{{last_user_input}}", lastInput, StringComparison.Ordinal);
        }

        return result;
    }

    private static string? ExtractLastUserInput(IReadOnlyList<IContextMessage> context) {
        for (var index = context.Count - 1; index >= 0; index--) {
            var message = context[index];
            if (message is ILiveScreenCarrier carrier) {
                message = carrier.InnerMessage;
            }

            if (message is IModelInputMessage input) { return string.Join(Environment.NewLine + Environment.NewLine, input.ContentSections.Select(section => section.Value)); }
        }

        return null;
    }

    private sealed class StubScript {
        public List<StubDelta> Deltas { get; set; } = new();

        public void Normalize() {
            foreach (var delta in Deltas) {
                delta.Type = delta.Type?.Trim() ?? string.Empty;
            }
        }
    }

    private sealed class StubDelta {
        public string Type { get; set; } = string.Empty;
        public string? Content { get; set; }
        public bool EndSegment { get; set; }
        public StubToolCall? ToolCall { get; set; }
        public StubToolResult? ToolResult { get; set; }
        public string? ExecuteError { get; set; }
        public int? DelayMilliseconds { get; set; }
        public StubTokenUsage? TokenUsage { get; set; }
    }

    private sealed class StubToolCall {
        public string? ToolName { get; set; }
        public string? ToolCallId { get; set; }
        public string? RawArguments { get; set; }
        public Dictionary<string, string>? Arguments { get; set; }
        public string? ParseError { get; set; }
    }

    private sealed class StubToolResult {
        public string? ToolName { get; set; }
        public string? ToolCallId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Result { get; set; }
        public int? ElapsedMilliseconds { get; set; }
    }

    private sealed class StubTokenUsage {
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? CachedPromptTokens { get; set; }
    }
}
