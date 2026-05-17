using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public async Task<string> SendUserMessageAsync(string userMessage, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(userMessage)) { throw new ArgumentException("User message must not be empty.", nameof(userMessage)); }

        var request = new ChatCompletionRequest(
            _options.Model,
            [new ChatMessage("user", userMessage)],
            Stream: false
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

        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (content is null) {
            throw new InvalidOperationException(
                $"DeepSeek chat completion response did not contain choices[0].message.content. Raw response: {LastRawResponse}"
            );
        }

        return content;
    }

    public void Dispose() {
        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream
    );

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices
    );

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessageResponse? Message
    );

    private sealed record ChatMessageResponse(
        [property: JsonPropertyName("content")] string? Content
    );
}
