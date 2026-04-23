using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AnthropicStreamParserTests {
    [Fact]
    public void ParseEvent_UsageAccumulatesAcrossMessageStartAndDeltas() {
        var parser = new AnthropicStreamParser(ImmutableArray<ToolDefinition>.Empty);

        var events = new[] {
            """
            {"type":"message_start","message":{"usage":{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":11}}}
            """,
            """
            {"type":"message_delta","delta":{"stop_reason":null},"usage":{"input_tokens":123,"output_tokens":45}}
            """,
            """
            {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":46,"cache_creation_input_tokens":7}}
            """
        };

        var chunks = events.SelectMany(parser.ParseEvent).ToArray();

        Assert.Empty(chunks);

        var usage = parser.GetFinalUsage();
        Assert.NotNull(usage);
        Assert.Equal(123, usage.PromptTokens);
        Assert.Equal(46, usage.CompletionTokens);
        Assert.Equal(18, usage.CachedPromptTokens);
    }

    [Fact]
    public void ParseEvent_AggregatesToolInputFragmentsIntoSingleToolCall() {
        var parser = new AnthropicStreamParser(
            ImmutableArray.Create(
                new ToolDefinition(
                    Name: "get_weather",
                    Description: "Get weather",
                    Parameters: ImmutableArray.Create(
                        new ToolParamSpec("city", "city", ToolParamType.String)
                    )
                )
            )
        );

        var events = new[] {
            """
            {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_123","name":"get_weather","input":{}}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"city\":\"Par"}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"is\"}"}}
            """,
            """
            {"type":"content_block_stop","index":0}
            """
        };

        var chunks = events.SelectMany(parser.ParseEvent).ToArray();

        var toolCallChunk = Assert.Single(chunks, chunk => chunk.Kind == CompletionChunkKind.ToolCall);
        var toolCall = toolCallChunk.ToolCall!;

        Assert.Equal("toolu_123", toolCall.ToolCallId);
        Assert.Equal("get_weather", toolCall.ToolName);
        Assert.Null(toolCall.ParseError);
        Assert.Equal("Paris", toolCall.Arguments!["city"]);
        Assert.Equal("Paris", toolCall.RawArguments!["city"]);
    }

    [Fact]
    public void ParseEvent_UnknownToolFallbackPreservesStringLiterals() {
        var parser = new AnthropicStreamParser(ImmutableArray<ToolDefinition>.Empty);

        var events = new[] {
            """
            {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_456","name":"unknown_tool","input":{}}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"flag\":\"true\",\"count\":\"42\",\"maybe\":\"null\",\"nested\":{\"enabled\":\"false\"}}"}}
            """,
            """
            {"type":"content_block_stop","index":0}
            """
        };

        var chunks = events.SelectMany(parser.ParseEvent).ToArray();

        var toolCallChunk = Assert.Single(chunks, chunk => chunk.Kind == CompletionChunkKind.ToolCall);
        var toolCall = toolCallChunk.ToolCall!;

        Assert.Equal("toolu_456", toolCall.ToolCallId);
        Assert.Equal("unknown_tool", toolCall.ToolName);
        Assert.Null(toolCall.ParseError);
        Assert.Equal("tool_definition_missing", toolCall.ParseWarning);
        Assert.Equal("true", toolCall.Arguments!["flag"]);
        Assert.Equal("42", toolCall.Arguments!["count"]);
        Assert.Equal("null", toolCall.Arguments!["maybe"]);

        var nested = Assert.IsType<Dictionary<string, object?>>(toolCall.Arguments!["nested"]);
        Assert.Equal("false", nested["enabled"]);

        Assert.Equal("true", toolCall.RawArguments!["flag"]);
        Assert.Equal("42", toolCall.RawArguments!["count"]);
        Assert.Equal("null", toolCall.RawArguments!["maybe"]);
    }
}
