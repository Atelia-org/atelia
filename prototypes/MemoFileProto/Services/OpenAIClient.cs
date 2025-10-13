using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MemoFileProto.Models;
using MemoFileProto.Models.OpenAI;

namespace MemoFileProto.Services;

public class OpenAIClient : ILLMClient {
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;
    private readonly string _defaultModel;
    private static readonly JsonSerializerOptions _serializerOptions = new() {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAIClient(HttpClient? httpClient = null, string baseUrl = "http://localhost:4000/openai/v1", string defaultModel = "vscode-lm-proxy") {
        _httpClient = httpClient ?? new HttpClient {
            Timeout = TimeSpan.FromSeconds(100)
        };
        _ownsHttpClient = httpClient is null;
        _baseUrl = baseUrl;
        _defaultModel = defaultModel;
    }

    public async IAsyncEnumerable<UniversalResponseDelta> StreamChatCompletionAsync(
        UniversalRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    ) {
        // 转换：Universal → OpenAI
        var openAIRequest = ConvertToOpenAIRequest(request);

        var json = JsonSerializer.Serialize(openAIRequest, _serializerOptions);
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

                var openAIResponse = JsonSerializer.Deserialize<OpenAIResponse>(data, _serializerOptions);
                if (openAIResponse?.Choices != null && openAIResponse.Choices.Count > 0) {
                    var choice = openAIResponse.Choices[0];
                    var delta = choice.Delta;

                    if (delta is not null || !string.IsNullOrWhiteSpace(choice.FinishReason)) {
                        // 转换：OpenAI → Universal
                        yield return ConvertToUniversalDelta(delta, choice.FinishReason);
                    }
                }
            }
        }
    }

    private OpenAIRequest ConvertToOpenAIRequest(UniversalRequest request) {
        var messages = new List<OpenAIMessage>();
        foreach (var universalMessage in request.Messages) {
            messages.AddRange(ConvertToOpenAIMessages(universalMessage));
        }

        return new OpenAIRequest {
            Model = request.Model ?? _defaultModel,
            Stream = request.Stream,
            Messages = messages,
            Tools = request.Tools?.Select(ConvertToOpenAITool).ToList()
        };
    }

    private IEnumerable<OpenAIMessage> ConvertToOpenAIMessages(UniversalMessage message) {
        if (message.ToolResults != null) {
            foreach (var result in message.ToolResults) {
                yield return new OpenAIMessage {
                    Role = "tool",
                    Content = result.Content ?? string.Empty,
                    ToolCallId = result.ToolCallId,
                    Name = result.ToolName
                };
            }

            yield break;
        }

        yield return ConvertNonToolMessage(message);
    }

    private OpenAIMessage ConvertNonToolMessage(UniversalMessage message) {
        return new OpenAIMessage {
            Role = message.Role,
            Content = message.Content ?? string.Empty,
            ToolCalls = message.ToolCalls?.Select(
                tc => new OpenAIToolCall {
                    Id = tc.Id,
                    Type = "function",
                    Function = new OpenAIFunctionCall {
                        Name = tc.Name,
                        Arguments = tc.Arguments
                    }
                }
            ).ToList()
        };
    }

    private OpenAITool ConvertToOpenAITool(UniversalTool tool) {
        return new OpenAITool {
            Type = "function",
            Function = new OpenAIFunctionDefinition {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.Parameters
            }
        };
    }

    private UniversalResponseDelta ConvertToUniversalDelta(OpenAIMessage? delta, string? finishReason) {
        return new UniversalResponseDelta {
            Content = delta?.Content,
            ToolCalls = delta?.ToolCalls?.Select(
                tc => new UniversalToolCall {
                    Id = tc.Id,
                    Name = tc.Function.Name,
                    Arguments = tc.Function.Arguments
                }
            ).ToList(),
            FinishReason = finishReason
        };
    }

    public void Dispose() {
        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }
    }
}
