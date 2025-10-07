using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MemoFileProto.Models;

namespace MemoFileProto.Services;

public class OpenAIClient : IDisposable {
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;
    private readonly string _model;
    private static readonly JsonSerializerOptions _serializerOptions = new() {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAIClient(HttpClient? httpClient = null, string baseUrl = "http://localhost:4000/openai/v1", string model = "gpt-4.1") {
        _httpClient = httpClient ?? new HttpClient {
            Timeout = TimeSpan.FromSeconds(100)
        };
        _ownsHttpClient = httpClient is null;
        _baseUrl = baseUrl;
        _model = model;
    }

    public async IAsyncEnumerable<ChatResponseDelta> StreamChatCompletionAsync(
        List<ChatMessage> messages,
        List<Tool>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    ) {
        var request = new ChatRequest {
            Model = _model,
            Messages = messages,
            Stream = true,
            Tools = tools
        };

        var json = JsonSerializer.Serialize(request, _serializerOptions);

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions") {
            Content = content
        };

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream) {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) { continue; }

            if (line.StartsWith("data: ")) {
                var data = line.Substring(6);
                if (data == "[DONE]") { break; }

                var chatResponse = JsonSerializer.Deserialize<ChatResponse>(data, _serializerOptions);
                if (chatResponse?.Choices != null && chatResponse.Choices.Count > 0) {
                    var choice = chatResponse.Choices[0];
                    var delta = choice.Delta;

                    if (delta is not null || !string.IsNullOrWhiteSpace(choice.FinishReason)) {
                        yield return new ChatResponseDelta {
                            Content = delta?.Content,
                            ToolCalls = delta?.ToolCalls,
                            FinishReason = choice.FinishReason
                        };
                    }
                }
            }
        }
    }

    public void Dispose() {
        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }
    }
}
