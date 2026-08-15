using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.MutableContextAgentProto.Protocol;

namespace Atelia.MutableContextAgentProto.Llm.ChatHistory;

public sealed class DeepSeekHistoryChatClient : IDisposable {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly DeepSeekOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public DeepSeekHistoryChatClient(DeepSeekOptions options, HttpClient? httpClient = null) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = options.Timeout;
    }

    public string? LastRawRequest { get; private set; }

    public string? LastRawResponse { get; private set; }

    public async Task<ChatHistoryResponse> SendAsync(ChatHistoryRequest historyRequest, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(historyRequest);
        if (historyRequest.Messages.Count == 0) { throw new ArgumentException("Chat history must contain at least one message.", nameof(historyRequest)); }

        var request = new ChatCompletionRequest(
            _options.Model,
            historyRequest.Messages.Select(ToWireMessage).ToArray(),
            Stream: false,
            Tools: historyRequest.Tools.Count == 0
                ? null
                : historyRequest.Tools.Select(ToWireTool).ToArray(),
            ToolChoice: historyRequest.Tools.Count == 0 ? null : ToWireToolChoice(historyRequest.ToolChoice)
        );

        LastRawRequest = JsonSerializer.Serialize(request, JsonOptions);
        LastRawResponse = null;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.ChatCompletionsEndpoint) {
            Content = new StringContent(LastRawRequest, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        LastRawResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException(
                $"DeepSeek chat completion failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Raw response: {LastRawResponse}"
            );
        }

        ChatCompletionResponse? parsed;
        try {
            parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(LastRawResponse, JsonOptions);
        }
        catch (JsonException ex) {
            throw new InvalidOperationException(
                $"DeepSeek chat completion response is not valid JSON. Raw response: {LastRawResponse}",
                ex
            );
        }

        var choice = parsed?.Choices?.FirstOrDefault();
        var message = choice?.Message;
        if (message is null) {
            throw new InvalidOperationException(
                $"DeepSeek chat completion response did not contain choices[0].message. Raw response: {LastRawResponse}"
            );
        }

        var assistantMessage = new AssistantChatHistoryMessage(
            message.Content,
            message.ReasoningContent,
            ParseToolCalls(message.ToolCalls, LastRawResponse)
        );

        return new ChatHistoryResponse(assistantMessage, choice?.FinishReason, LastRawResponse);
    }

    public void Dispose() {
        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }
    }

    private static WireChatMessage ToWireMessage(ChatHistoryMessage message)
        => message switch {
            SystemChatHistoryMessage systemMessage => new WireChatMessage("system", systemMessage.Content),
            UserChatHistoryMessage userMessage => new WireChatMessage("user", userMessage.Content),
            AssistantChatHistoryMessage assistantMessage => new WireChatMessage(
                "assistant",
                assistantMessage.Content,
                ReasoningContent: assistantMessage.ReasoningContent,
                ToolCalls: assistantMessage.ToolCalls?.Select(ToWireToolCall).ToArray()
            ),
            ToolChatHistoryMessage toolMessage => new WireChatMessage(
                "tool",
                toolMessage.Content,
                ToolCallId: toolMessage.ToolCallId,
                Name: toolMessage.Name
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message, "Unsupported chat history message type."),
        };

    private static ChatToolDefinition ToWireTool(ToolDefinition definition)
        => new(
            "function",
            new ChatFunctionDefinition(definition.Name, definition.Description, definition.ParametersJsonSchema)
        );

    private static WireOutboundToolCall ToWireToolCall(ToolCallRequest request)
        => new(
            request.Id,
            "function",
            new WireOutboundFunctionCall(request.Name, JsonSerializer.Serialize(request.Arguments, JsonOptions))
        );

    private static string ToWireToolChoice(ChatToolChoice choice)
        => choice switch {
            ChatToolChoice.None => "none",
            ChatToolChoice.Required => "required",
            _ => "auto",
        };

    private static IReadOnlyList<ToolCallRequest> ParseToolCalls(
        IReadOnlyList<WireToolCall>? wireToolCalls,
        string rawResponse
    ) {
        if (wireToolCalls is null || wireToolCalls.Count == 0) { return []; }

        var calls = new List<ToolCallRequest>(wireToolCalls.Count);
        for (var index = 0; index < wireToolCalls.Count; index++) {
            var wireCall = wireToolCalls[index];
            var function = wireCall.Function
                ?? throw new InvalidOperationException($"Tool call at index {index} did not contain a function payload. Raw response: {rawResponse}");

            if (string.IsNullOrWhiteSpace(function.Name)) { throw new InvalidOperationException($"Tool call at index {index} did not contain function.name. Raw response: {rawResponse}"); }

            var callId = string.IsNullOrWhiteSpace(wireCall.Id)
                ? $"call-{index + 1}"
                : wireCall.Id;

            calls.Add(new ToolCallRequest(callId, function.Name, ParseArguments(function.Arguments, rawResponse)));
        }

        return calls;
    }

    private static JsonElement ParseArguments(JsonElement arguments, string rawResponse) {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) { return EmptyArguments(); }

        if (arguments.ValueKind == JsonValueKind.Object) { return arguments.Clone(); }

        if (arguments.ValueKind != JsonValueKind.String) {
            throw new InvalidOperationException(
                $"Tool call function.arguments must be a JSON string or object, but was {arguments.ValueKind}. Raw response: {rawResponse}"
            );
        }

        var text = arguments.GetString();
        if (string.IsNullOrWhiteSpace(text)) { return EmptyArguments(); }

        try {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                throw new InvalidOperationException(
                    $"Tool call function.arguments must decode to a JSON object, but was {document.RootElement.ValueKind}. Raw response: {rawResponse}"
                );
            }

            return document.RootElement.Clone();
        }
        catch (JsonException ex) {
            throw new InvalidOperationException(
                $"Tool call function.arguments was not valid JSON. Raw response: {rawResponse}",
                ex
            );
        }
    }

    private static JsonElement EmptyArguments()
        => JsonSerializer.SerializeToElement(new { });

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<WireChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("tools")] IReadOnlyList<ChatToolDefinition>? Tools,
        [property: JsonPropertyName("tool_choice")] string? ToolChoice
    );

    private sealed record WireChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("reasoning_content")] string? ReasoningContent = null,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<WireOutboundToolCall>? ToolCalls = null,
        [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
        [property: JsonPropertyName("name")] string? Name = null
    );

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices
    );

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessageResponse? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason
    );

    private sealed record ChatMessageResponse(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("reasoning_content")] string? ReasoningContent,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<WireToolCall>? ToolCalls
    );

    private sealed record ChatToolDefinition(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] ChatFunctionDefinition Function
    );

    private sealed record ChatFunctionDefinition(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters
    );

    private sealed record WireToolCall(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("function")] WireFunctionCall? Function
    );

    private sealed record WireFunctionCall(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("arguments")] JsonElement Arguments
    );

    private sealed record WireOutboundToolCall(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] WireOutboundFunctionCall Function
    );

    private sealed record WireOutboundFunctionCall(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments
    );
}
