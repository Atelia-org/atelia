using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.MutableContextAgentProto.Protocol;

namespace Atelia.MutableContextAgentProto.Llm;

public sealed class DeepSeekChatClient : IDisposable {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly DeepSeekOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public DeepSeekChatClient(DeepSeekOptions options, HttpClient? httpClient = null) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = options.Timeout;
    }

    public string? LastRawRequest { get; private set; }

    public string? LastRawResponse { get; private set; }

    public async Task<ChatTurnResponse> SendTurnAsync(ChatTurnRequest turnRequest, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(turnRequest);

        var userMessage = turnRequest.UserMessage;
        if (string.IsNullOrWhiteSpace(userMessage)) { throw new ArgumentException("User message must not be empty.", nameof(userMessage)); }

        var request = new ChatCompletionRequest(
            _options.Model,
            [new ChatMessage("user", userMessage)],
            Stream: false,
            Tools: turnRequest.Tools.Count == 0
                ? null
                : turnRequest.Tools.Select(ToWireTool).ToArray(),
            ToolChoice: turnRequest.Tools.Count == 0 ? null : ToWireToolChoice(turnRequest.ToolChoice)
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

        var toolCalls = ParseToolCalls(message.ToolCalls, LastRawResponse);
        return new ChatTurnResponse(message.Content, toolCalls, choice?.FinishReason, LastRawResponse);
    }

    public void Dispose() {
        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("tools")] IReadOnlyList<ChatToolDefinition>? Tools,
        [property: JsonPropertyName("tool_choice")] string? ToolChoice
    );

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
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

    private static ChatToolDefinition ToWireTool(ToolDefinition definition)
        => new(
            "function",
            new ChatFunctionDefinition(definition.Name, definition.Description, definition.ParametersJsonSchema)
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
}
