using System.Collections.Immutable;
using System.Linq;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class OpenAIChatStreamParserTests {
    [Fact]
    public void ParseEvent_IgnoresReasoningContentAndAggregatesToolCallFragments() {
        var parser = new OpenAIChatStreamParser(
            ImmutableArray.Create(
                new ToolDefinition(
                    Name: "get_weather",
                    Description: "Get weather",
                    Parameters: ImmutableArray.Create(
                        new ToolParamSpec("city", "city", ToolParamType.String)
                    )
                )
            ),
            OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls
        );

        var events = new[] {
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":null,\"tool_calls\":null},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":\"The model is thinking\",\"tool_calls\":null},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":[{\"id\":\"call_123\",\"index\":0,\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"\"}}]},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":[{\"id\":null,\"index\":0,\"type\":\"function\",\"function\":{\"name\":null,\"arguments\":\"{\\\"city\\\": \\\"\"}}]},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":[{\"id\":null,\"index\":0,\"type\":\"function\",\"function\":{\"name\":null,\"arguments\":\"Paris\\\"}\"}}]},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":\"\\n\",\"reasoning_content\":null,\"tool_calls\":null},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":null},\"finish_reason\":\"tool_calls\"}],\"usage\":null}"
        };

        var chunks = events.SelectMany(parser.ParseEvent).ToArray();

        Assert.DoesNotContain(chunks, chunk => chunk.Kind == CompletionChunkKind.Content);

        var toolCallChunk = Assert.Single(chunks, chunk => chunk.Kind == CompletionChunkKind.ToolCall);
        var toolCall = toolCallChunk.ToolCall!;
        Assert.Equal("call_123", toolCall.ToolCallId);
        Assert.Equal("get_weather", toolCall.ToolName);
        Assert.Null(toolCall.ParseError);
        Assert.Equal("Paris", toolCall.Arguments!["city"]);
        Assert.Equal("Paris", toolCall.RawArguments!["city"]);
    }

    [Fact]
    public void ParseEvent_StrictModePreservesWhitespaceContentDuringToolCalls() {
        var parser = new OpenAIChatStreamParser(
            ImmutableArray.Create(
                new ToolDefinition(
                    Name: "get_weather",
                    Description: "Get weather",
                    Parameters: ImmutableArray.Create(
                        new ToolParamSpec("city", "city", ToolParamType.String)
                    )
                )
            ),
            OpenAIChatWhitespaceContentMode.Preserve
        );

        var events = new[] {
            "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"id\":\"call_123\",\"index\":0,\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Paris\\\"}\"}}]},\"finish_reason\":null}],\"usage\":null}",
            "{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\\n\"},\"finish_reason\":null}],\"usage\":null}",
            "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":null}"
        };

        var chunks = events.SelectMany(parser.ParseEvent).ToArray();

        var contentChunk = Assert.Single(chunks, chunk => chunk.Kind == CompletionChunkKind.Content);
        Assert.Equal("\n", contentChunk.Content);

        var toolCallChunk = Assert.Single(chunks, chunk => chunk.Kind == CompletionChunkKind.ToolCall);
        Assert.Equal("call_123", toolCallChunk.ToolCall!.ToolCallId);
    }

    [Fact]
    public void ParseEvent_SgLangModeIgnoresWhitespaceWhenContentAndToolCallsShareSameDelta() {
        var parser = new OpenAIChatStreamParser(
            ImmutableArray.Create(
                new ToolDefinition(
                    Name: "get_weather",
                    Description: "Get weather",
                    Parameters: ImmutableArray.Create(
                        new ToolParamSpec("city", "city", ToolParamType.String)
                    )
                )
            ),
            OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls
        );

        var events = new[] {
            """
            {"choices":[{"index":0,"delta":{"content":"\n","tool_calls":[{"id":"call_123","index":0,"type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}]},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":null}
            """
        };

        var chunks = events.SelectMany(parser.ParseEvent).ToArray();

        Assert.DoesNotContain(chunks, chunk => chunk.Kind == CompletionChunkKind.Content);

        var toolCallChunk = Assert.Single(chunks, chunk => chunk.Kind == CompletionChunkKind.ToolCall);
        Assert.Equal("call_123", toolCallChunk.ToolCall!.ToolCallId);
        Assert.Equal("Paris", toolCallChunk.ToolCall!.Arguments!["city"]);
    }

    [Fact]
    public void ParseEvent_InterleavedToolCallsAreAggregatedByIndex() {
        var parser = new OpenAIChatStreamParser(
            ImmutableArray.Create(
                new ToolDefinition(
                    Name: "alpha",
                    Description: "alpha",
                    Parameters: ImmutableArray.Create(new ToolParamSpec("value", "value", ToolParamType.String))
                ),
                new ToolDefinition(
                    Name: "beta",
                    Description: "beta",
                    Parameters: ImmutableArray.Create(new ToolParamSpec("count", "count", ToolParamType.Int32))
                )
            )
        );

        var events = new[] {
            """
            {"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_a","index":0,"type":"function","function":{"name":"alpha","arguments":"{\"value\": \"A\"}"}},{"id":"call_b","index":1,"type":"function","function":{"name":"beta","arguments":"{\"count\": "}}]},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"tool_calls":[{"id":null,"index":1,"type":"function","function":{"name":null,"arguments":"7}"}}]},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":null}
            """
        };

        var chunks = events.SelectMany(parser.ParseEvent).ToArray();

        var toolCallChunks = chunks.Where(chunk => chunk.Kind == CompletionChunkKind.ToolCall).ToArray();
        Assert.Equal(2, toolCallChunks.Length);
        Assert.Equal("alpha", toolCallChunks[0].ToolCall!.ToolName);
        Assert.Equal("A", toolCallChunks[0].ToolCall!.Arguments!["value"]);
        Assert.Equal("beta", toolCallChunks[1].ToolCall!.ToolName);
        Assert.Equal(7, toolCallChunks[1].ToolCall!.Arguments!["count"]);
        Assert.Equal("call_b", toolCallChunks[1].ToolCall!.ToolCallId);
    }
}
