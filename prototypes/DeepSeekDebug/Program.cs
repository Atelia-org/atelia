using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;

namespace Atelia.DeepSeekDebug;

internal static class Program {
    private const string BaseUrlEnvVar = "DEEPSEEK_BASE_URL";
    private const string ApiKeyEnvVar = "DEEPSEEK_API_KEY";
    private const string ModelEnvVar = "DEEPSEEK_MODEL";
    private const string DefaultModel = "deepseek-v4-pro";
    private const string FixedToday = "2026-04-19";

    private static readonly ImmutableArray<ToolDefinition> Tools = [
        new ToolDefinition(
            Name: "get_date",
            Description: "Get today's date in YYYY-MM-DD format.",
            Parameters: ImmutableArray<ToolParamSpec>.Empty
        ),
        new ToolDefinition(
            Name: "get_weather",
            Description: "Get weather of a location on a date.",
            Parameters: [
                new ToolParamSpec("location", "The city name.", ToolParamType.String),
                new ToolParamSpec("date", "The date in YYYY-MM-DD format.", ToolParamType.String)
            ]
        )
    ];

    private static async Task<int> Main() {
        Console.OutputEncoding = Encoding.UTF8;

        var baseUrl = RequireEnvironmentVariable(BaseUrlEnvVar);
        var apiKey = RequireEnvironmentVariable(ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(ModelEnvVar) ?? DefaultModel;

        var recordingHandler = new RecordingHandler(new HttpClientHandler());
        using var httpClient = new HttpClient(recordingHandler);
        var client = new DeepSeekV4ChatClient(
            apiKey: apiKey,
            httpClient: httpClient,
            baseAddress: new Uri(EnsureTrailingSlash(baseUrl)),
            options: new OpenAIChatClientOptions {
                ExtraBody = new JsonObject {
                    ["thinking"] = new JsonObject {
                        ["type"] = "enabled"
                    },
                    ["reasoning_effort"] = "high"
                }
            }
        );

        Console.WriteLine($"[startup] base={EnsureTrailingSlash(baseUrl)} model={model}");

        var context = new List<IHistoryMessage>();
        var totalToolCalls = 0;

        var turn1 = await RunUserTurnAsync(
            client,
            model,
            context,
            "How's the weather in Hangzhou tomorrow? Please use tools and be concise.",
            turnLabel: "Turn 1"
        );
        totalToolCalls += turn1.ToolCallCount;

        var turn2 = await RunUserTurnAsync(
            client,
            model,
            context,
            "What about Guangzhou tomorrow? Compare it briefly with Hangzhou.",
            turnLabel: "Turn 2"
        );
        totalToolCalls += turn2.ToolCallCount;

        ValidateRun(recordingHandler.RequestBodies, totalToolCalls);

        Console.WriteLine();
        Console.WriteLine("[summary] real API debug completed successfully");
        Console.WriteLine($"[summary] total requests={recordingHandler.RequestBodies.Count} total tool calls={totalToolCalls}");
        Console.WriteLine($"[summary] turn1 final={turn1.FinalText}");
        Console.WriteLine($"[summary] turn2 final={turn2.FinalText}");
        return 0;
    }

    private static async Task<TurnRunResult> RunUserTurnAsync(
        ICompletionClient client,
        string model,
        List<IHistoryMessage> context,
        string userMessage,
        string turnLabel
    ) {
        context.Add(new ObservationMessage(userMessage));
        Console.WriteLine();
        Console.WriteLine($"== {turnLabel} ==");
        Console.WriteLine($"[user] {userMessage}");

        var toolCallCount = 0;
        var subTurn = 1;
        string? finalText = null;

        while (true) {
            var request = new CompletionRequest(
                ModelId: model,
                SystemPrompt: BuildSystemPrompt(),
                Context: context.ToArray(),
                Tools: Tools
            );

            var result = await client.StreamCompletionAsync(request, observer: null);
            PrintAssistantResult(turnLabel, subTurn, result);
            context.Add(result.Message);

            if (result.Errors is { Count: > 0 }) {
                throw new InvalidOperationException(
                    $"{turnLabel}.{subTurn} returned provider errors: {string.Join(" | ", result.Errors)}"
                );
            }

            var toolCalls = result.Message.ToolCalls;
            if (toolCalls.Count == 0) {
                finalText = result.Message.GetFlattenedText();
                break;
            }

            toolCallCount += toolCalls.Count;
            var toolResults = new List<ToolResult>(toolCalls.Count);
            foreach (var toolCall in toolCalls) {
                var toolResult = ExecuteTool(toolCall);
                toolResults.Add(toolResult);
                Console.WriteLine($"[tool] {toolCall.ToolName}({FormatArguments(toolCall.Arguments)}) => {toolResult.Result}");
            }

            context.Add(new ToolResultsMessage(Content: null, Results: toolResults, ExecuteError: null));
            subTurn++;
        }

        return new TurnRunResult(finalText ?? string.Empty, toolCallCount);
    }

    private static void ValidateRun(IReadOnlyList<string> requestBodies, int totalToolCalls) {
        if (requestBodies.Count < 3) {
            throw new InvalidOperationException($"Expected at least 3 outbound requests, got {requestBodies.Count}.");
        }

        if (totalToolCalls < 1) {
            throw new InvalidOperationException("Expected at least one real tool call, but the model emitted none.");
        }

        var laterRequestHasReplay = false;
        for (var i = 1; i < requestBodies.Count; i++) {
            using var document = JsonDocument.Parse(requestBodies[i]);
            if (!document.RootElement.TryGetProperty("messages", out var messages)) {
                continue;
            }

            foreach (var message in messages.EnumerateArray()) {
                if (!message.TryGetProperty("role", out var roleProperty) || roleProperty.GetString() != "assistant") {
                    continue;
                }

                var hasToolCalls = message.TryGetProperty("tool_calls", out var toolCallsProperty)
                    && toolCallsProperty.ValueKind == JsonValueKind.Array
                    && toolCallsProperty.GetArrayLength() > 0;
                var hasReasoning = message.TryGetProperty("reasoning_content", out var reasoningProperty)
                    && reasoningProperty.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(reasoningProperty.GetString());

                if (hasToolCalls && hasReasoning) {
                    laterRequestHasReplay = true;
                    break;
                }
            }

            if (laterRequestHasReplay) {
                break;
            }
        }

        if (!laterRequestHasReplay) {
            throw new InvalidOperationException(
                "Expected a follow-up request to replay an assistant tool_call message with reasoning_content, but none was found."
            );
        }
    }

    private static ToolResult ExecuteTool(ParsedToolCall toolCall) {
        return toolCall.ToolName switch {
            "get_date" => new ToolResult(
                ToolName: toolCall.ToolName,
                ToolCallId: toolCall.ToolCallId,
                Status: ToolExecutionStatus.Success,
                Result: FixedToday
            ),
            "get_weather" => new ToolResult(
                ToolName: toolCall.ToolName,
                ToolCallId: toolCall.ToolCallId,
                Status: ToolExecutionStatus.Success,
                Result: BuildWeatherResult(toolCall)
            ),
            _ => throw new InvalidOperationException($"Unknown tool '{toolCall.ToolName}'.")
        };
    }

    private static string BuildWeatherResult(ParsedToolCall toolCall) {
        if (toolCall.Arguments is null) {
            throw new InvalidOperationException($"Tool '{toolCall.ToolName}' arguments are null. parseError={toolCall.ParseError}");
        }

        var location = GetRequiredStringArgument(toolCall.Arguments, "location");
        var date = GetRequiredStringArgument(toolCall.Arguments, "date");
        return $"{location}: Cloudy 7~13°C on {date}";
    }

    private static string GetRequiredStringArgument(IReadOnlyDictionary<string, object?> arguments, string name) {
        if (!arguments.TryGetValue(name, out var value) || value is not string text || string.IsNullOrWhiteSpace(text)) {
            throw new InvalidOperationException($"Missing required string argument '{name}'.");
        }

        return text;
    }

    private static void PrintAssistantResult(string turnLabel, int subTurn, CompletionResult result) {
        var flattenedText = result.Message.GetFlattenedText();
        var reasoningBlocks = result.Message.Blocks.OfType<OpenAIChatReasoningBlock>().ToArray();
        var toolCalls = result.Message.ToolCalls;

        Console.WriteLine($"[{turnLabel}.{subTurn}] text={FormatPreview(flattenedText)}");
        Console.WriteLine($"[{turnLabel}.{subTurn}] reasoning_blocks={reasoningBlocks.Length} tool_calls={toolCalls.Count}");

        if (reasoningBlocks.Length > 0) {
            Console.WriteLine($"[{turnLabel}.{subTurn}] reasoning_preview={FormatPreview(reasoningBlocks[0].Content)}");
        }
    }

    private static string FormatArguments(IReadOnlyDictionary<string, object?>? arguments) {
        if (arguments is null || arguments.Count == 0) {
            return string.Empty;
        }

        return string.Join(", ", arguments.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string FormatPreview(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return "<empty>";
        }

        var normalized = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160] + "...";
    }

    private static string BuildSystemPrompt() {
        return "You are a weather assistant using external tools. "
            + "When the user asks about weather or asks for tomorrow, you MUST use tools instead of guessing. "
            + "If the date is relative, first call get_date, then call get_weather. "
            + "After all tool results arrive, answer in 1-3 concise sentences.";
    }

    private static string RequireEnvironmentVariable(string name) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidOperationException($"Environment variable '{name}' is required.");
        }

        return value;
    }

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith('/') ? value : value + "/";

    private sealed record TurnRunResult(string FinalText, int ToolCallCount);

    private sealed class RecordingHandler : DelegatingHandler {
        public RecordingHandler(HttpMessageHandler innerHandler)
            : base(innerHandler) {
        }

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.Content is not null) {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                RequestBodies.Add(body);
                PrintRequestSummary(RequestBodies.Count, request.RequestUri, body);

                request.Content = new StringContent(body, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
                if (request.Content.Headers.ContentType is not null && request.Content.Headers.ContentType.CharSet is null) {
                    request.Content.Headers.ContentType.CharSet = "utf-8";
                }
            }
            else {
                RequestBodies.Add(string.Empty);
                PrintRequestSummary(RequestBodies.Count, request.RequestUri, string.Empty);
            }

            var response = await base.SendAsync(request, cancellationToken);
            Console.WriteLine($"[http] status={(int)response.StatusCode} uri={request.RequestUri}");
            return response;
        }

        private static void PrintRequestSummary(int index, Uri? uri, string body) {
            var assistantCount = 0;
            var assistantWithReasoningCount = 0;
            var assistantWithToolCallsCount = 0;
            var assistantToolCallsWithReasoningCount = 0;

            if (!string.IsNullOrWhiteSpace(body)) {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("messages", out var messages)) {
                    foreach (var message in messages.EnumerateArray()) {
                        if (!message.TryGetProperty("role", out var roleProperty) || roleProperty.GetString() != "assistant") {
                            continue;
                        }

                        assistantCount++;

                        var hasReasoning = message.TryGetProperty("reasoning_content", out var reasoningProperty)
                            && reasoningProperty.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(reasoningProperty.GetString());
                        var hasToolCalls = message.TryGetProperty("tool_calls", out var toolCallsProperty)
                            && toolCallsProperty.ValueKind == JsonValueKind.Array
                            && toolCallsProperty.GetArrayLength() > 0;

                        if (hasReasoning) {
                            assistantWithReasoningCount++;
                        }

                        if (hasToolCalls) {
                            assistantWithToolCallsCount++;
                        }

                        if (hasReasoning && hasToolCalls) {
                            assistantToolCallsWithReasoningCount++;
                        }
                    }
                }
            }

            Console.WriteLine(
                $"[http] request#{index} uri={uri} assistant={assistantCount} assistant_with_reasoning={assistantWithReasoningCount} "
                + $"assistant_with_tool_calls={assistantWithToolCallsCount} tool_call_assistant_with_reasoning={assistantToolCallsWithReasoningCount}"
            );
        }
    }
}
