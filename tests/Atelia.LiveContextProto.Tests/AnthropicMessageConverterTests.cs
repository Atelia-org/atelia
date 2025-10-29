using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Provider.Anthropic;
using Atelia.LiveContextProto.State.History;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AnthropicMessageConverterTests {
    [Fact]
    public void ConvertToApiRequest_ParseErrorFallsBackToRawArguments() {
        var toolCall = new ToolCallRequest(
            ToolName: "search",
            ToolCallId: "call-1",
            RawArguments: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["message"] = "hello",
                ["count"] = "42",
                ["flag"] = "true",
                ["payload"] = "{\"nested\":1}"
            },
            Arguments: null,
            ParseError: "int32_invalid_literal",
            ParseWarning: null
        );

        var historyEntry = new ModelOutputEntry(
            Contents: string.Empty,
            ToolCalls: new[] { toolCall },
            Invocation: new ModelInvocationDescriptor("provider", "spec", "model")
        );

        var request = new LlmRequest(
            ModelId: "claude-3",
            SystemInstruction: string.Empty,
            Context: new IContextMessage[] { historyEntry },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        var toolUseBlock = Assert.IsType<AnthropicToolUseBlock>(assistantMessage.Content.Single(block => block is AnthropicToolUseBlock));
        var input = toolUseBlock.Input;

        Assert.Equal(JsonValueKind.Object, input.ValueKind);
        Assert.Equal("hello", input.GetProperty("message").GetString());
        Assert.Equal(42, input.GetProperty("count").GetInt32());
        Assert.True(input.GetProperty("flag").GetBoolean());

        var payload = input.GetProperty("payload");
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(1, payload.GetProperty("nested").GetInt32());
    }

    [Fact]
    public void ConvertToApiRequest_UsesParsedArgumentsWhenNoParseError() {
        var arguments = new Dictionary<string, object?> {
            ["count"] = 7
        };

        var toolCall = new ToolCallRequest(
            ToolName: "echo",
            ToolCallId: "call-2",
            RawArguments: null,
            Arguments: arguments,
            ParseError: null,
            ParseWarning: null
        );

        var historyEntry = new ModelOutputEntry(
            Contents: "call",
            ToolCalls: new[] { toolCall },
            Invocation: new ModelInvocationDescriptor("provider", "spec", "model")
        );

        var request = new LlmRequest(
            ModelId: "claude-3",
            SystemInstruction: string.Empty,
            Context: new IContextMessage[] { historyEntry },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        var toolUseBlock = Assert.IsType<AnthropicToolUseBlock>(assistantMessage.Content.Single(block => block is AnthropicToolUseBlock));

        Assert.Equal(7, toolUseBlock.Input.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ConvertToApiRequest_ParseErrorWithoutRawFallsBackToParsedValues() {
        var arguments = new Dictionary<string, object?> {
            ["count"] = 3
        };

        var toolCall = new ToolCallRequest(
            ToolName: "echo",
            ToolCallId: "call-3",
            RawArguments: null,
            Arguments: arguments,
            ParseError: "arguments_malformed",
            ParseWarning: null
        );

        var historyEntry = new ModelOutputEntry(
            Contents: "call",
            ToolCalls: new[] { toolCall },
            Invocation: new ModelInvocationDescriptor("provider", "spec", "model")
        );

        var request = new LlmRequest(
            ModelId: "claude-3",
            SystemInstruction: string.Empty,
            Context: new IContextMessage[] { historyEntry },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        var toolUseBlock = Assert.IsType<AnthropicToolUseBlock>(assistantMessage.Content.Single(block => block is AnthropicToolUseBlock));

        Assert.Equal(3, toolUseBlock.Input.GetProperty("count").GetInt32());
    }
}
