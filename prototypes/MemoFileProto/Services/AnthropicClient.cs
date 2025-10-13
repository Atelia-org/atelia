using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MemoFileProto.Models;
using MemoFileProto.Models.Anthropic;

namespace MemoFileProto.Services;

public class AnthropicClient : ILLMClient {
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;
    private readonly string _defaultModel;
    private static readonly JsonSerializerOptions _serializerOptions = new() {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public AnthropicClient(HttpClient? httpClient = null, string baseUrl = "http://localhost:4000/anthropic/v1", string defaultModel = "vscode-lm-proxy") {
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
        // 转换：Universal → Anthropic
        var anthropicRequest = ConvertToAnthropicRequest(request);

        var json = JsonSerializer.Serialize(anthropicRequest, _serializerOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/messages") {
            Content = content
        };

        // Anthropic 需要额外的头部
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // Anthropic 流式响应累积器
        var toolCallAccumulator = new Dictionary<int, ToolCallBuilder>();
        string? currentTextDelta = null;

        while (!reader.EndOfStream) {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) { continue; }

            if (line.StartsWith("event: ") || line.StartsWith("data: ")) {
                var eventPrefix = line.StartsWith("event: ") ? "event: " : "data: ";
                var data = line.Substring(eventPrefix.Length);

                if (eventPrefix == "data: ") {
                    var streamEvent = JsonSerializer.Deserialize<AnthropicStreamEvent>(data, _serializerOptions);
                    if (streamEvent == null) { continue; }

                    // 处理不同的事件类型
                    switch (streamEvent.Type) {
                        case "content_block_start":
                            // 新的内容块开始
                            if (streamEvent.ContentBlock?.Type == "tool_use") {
                                var index = streamEvent.Index ?? 0;
                                toolCallAccumulator[index] = new ToolCallBuilder {
                                    Id = streamEvent.ContentBlock.Id ?? string.Empty,
                                    Name = streamEvent.ContentBlock.Name ?? string.Empty,
                                    ArgumentsBuilder = new StringBuilder()
                                };
                            }
                            break;

                        case "content_block_delta":
                            // 内容块增量
                            if (streamEvent.Delta?.Type == "text_delta") {
                                currentTextDelta = streamEvent.Delta.Text;
                                yield return new UniversalResponseDelta {
                                    Content = currentTextDelta
                                };
                            }
                            else if (streamEvent.Delta?.Type == "input_json_delta") {
                                // 工具调用参数增量
                                var index = streamEvent.Index ?? 0;
                                if (toolCallAccumulator.TryGetValue(index, out var builder)) {
                                    builder.ArgumentsBuilder.Append(streamEvent.Delta.PartialJson ?? string.Empty);
                                }
                            }
                            break;

                        case "content_block_stop":
                            // 内容块结束，如果是工具调用，yield 完整的工具调用
                            var stopIndex = streamEvent.Index ?? 0;
                            if (toolCallAccumulator.TryGetValue(stopIndex, out var completedBuilder)) {
                                yield return new UniversalResponseDelta {
                                    ToolCalls = new List<UniversalToolCall> {
                                        new UniversalToolCall {
                                            Id = completedBuilder.Id,
                                            Name = completedBuilder.Name,
                                            Arguments = completedBuilder.ArgumentsBuilder.ToString()
                                        }
                                    }
                                };
                                toolCallAccumulator.Remove(stopIndex);
                            }
                            break;

                        case "message_delta":
                            // 消息增量（通常包含 stop_reason）
                            if (streamEvent.Delta?.StopReason != null) {
                                yield return new UniversalResponseDelta {
                                    FinishReason = streamEvent.Delta.StopReason
                                };
                            }
                            break;

                        case "message_stop":
                            // 消息结束
                            yield break;
                    }
                }
            }
        }
    }

    private AnthropicRequest ConvertToAnthropicRequest(UniversalRequest request) {
        // Anthropic 需要将连续的 tool 消息聚合成一条 user 消息
        var aggregatedMessages = AggregateToolMessages(request.Messages);

        return new AnthropicRequest {
            Model = request.Model ?? _defaultModel,
            Stream = request.Stream,
            MaxTokens = request.MaxTokens ?? 4096,
            Temperature = request.Temperature,
            Messages = aggregatedMessages.Select(ConvertToAnthropicMessage).ToList(),
            Tools = request.Tools?.Select(ConvertToAnthropicTool).ToList()
        };
    }

    /// <summary>
    /// 将连续的 tool 消息聚合成单条消息（Anthropic 要求）
    /// </summary>
    private List<UniversalMessage> AggregateToolMessages(List<UniversalMessage> messages) {
        var result = new List<UniversalMessage>();
        List<UniversalToolResult>? pendingToolResults = null;

        foreach (var msg in messages) {
            if (msg.Role == "tool" && msg.ToolResults != null) {
                // 累积 tool 结果
                pendingToolResults ??= new List<UniversalToolResult>();
                pendingToolResults.AddRange(msg.ToolResults);
            }
            else {
                // 遇到非 tool 消息，先输出累积的 tool 结果
                if (pendingToolResults != null && pendingToolResults.Count > 0) {
                    result.Add(
                        new UniversalMessage {
                            Role = "user", // Anthropic 中工具结果是 user 消息
                            ToolResults = pendingToolResults
                        }
                    );
                    pendingToolResults = null;
                }

                result.Add(msg);
            }
        }

        // 处理最后的累积
        if (pendingToolResults != null && pendingToolResults.Count > 0) {
            result.Add(
                new UniversalMessage {
                    Role = "user",
                    ToolResults = pendingToolResults
                }
            );
        }

        return result;
    }

    private AnthropicMessage ConvertToAnthropicMessage(UniversalMessage message) {
        var contentBlocks = new List<AnthropicContentBlock>();

        // 文本内容
        if (!string.IsNullOrEmpty(message.Content)) {
            contentBlocks.Add(
                new AnthropicContentBlock {
                    Type = "text",
                    Text = message.Content
                }
            );
        }

        // 工具调用（assistant 消息）
        if (message.ToolCalls != null && message.ToolCalls.Count > 0) {
            foreach (var toolCall in message.ToolCalls) {
                // 将 JSON 字符串解析为对象
                object? input = null;
                try {
                    input = JsonSerializer.Deserialize<object>(toolCall.Arguments);
                }
                catch (Exception ex) {
                    // Log the exception for debugging purposes
                    Console.Error.WriteLine($"Failed to parse toolCall.Arguments as JSON: {ex.Message}\nArguments: {toolCall.Arguments}");
                    input = new { raw = toolCall.Arguments };
                }

                contentBlocks.Add(
                    new AnthropicContentBlock {
                        Type = "tool_use",
                        Id = toolCall.Id,
                        Name = toolCall.Name,
                        Input = input
                    }
                );
            }
        }

        // 工具结果（user 消息，Anthropic 支持聚合）
        if (message.ToolResults != null && message.ToolResults.Count > 0) {
            foreach (var result in message.ToolResults) {
                contentBlocks.Add(
                    new AnthropicContentBlock {
                        Type = "tool_result",
                        ToolUseId = result.ToolCallId,
                        ToolResultContent = result.Content,
                        IsError = result.IsError ? true : null
                    }
                );
            }
        }

        return new AnthropicMessage {
            Role = message.Role,
            Content = contentBlocks
        };
    }

    private AnthropicTool ConvertToAnthropicTool(UniversalTool tool) {
        return new AnthropicTool {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.Parameters
        };
    }

    public void Dispose() {
        if (_ownsHttpClient) {
            _httpClient.Dispose();
        }
    }

    // 辅助类：累积工具调用参数
    private class ToolCallBuilder {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required StringBuilder ArgumentsBuilder { get; init; }
    }
}
