using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Agent.Core.History;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AnthropicMessageConverterTests {
    [Fact]
    public void ConvertToApiRequest_ParseErrorFallsBackToRawArguments() {
        var toolCall = new ParsedToolCall(
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

        var historyEntry = new ActionEntry(
            Content: string.Empty,
            ToolCalls: new[] { toolCall },
            Invocation: new CompletionDescriptor("provider", "spec", "model")
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                historyEntry,
                new ToolResultsMessage(
                    Content: null,
                    Results: new[] {
                        new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
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

        var toolCall = new ParsedToolCall(
            ToolName: "echo",
            ToolCallId: "call-2",
            RawArguments: null,
            Arguments: arguments,
            ParseError: null,
            ParseWarning: null
        );

        var historyEntry = new ActionEntry(
            Content: "call",
            ToolCalls: new[] { toolCall },
            Invocation: new CompletionDescriptor("provider", "spec", "model")
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                historyEntry,
                new ToolResultsMessage(
                    Content: null,
                    Results: new[] {
                        new ToolResult("echo", "call-2", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
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

        var toolCall = new ParsedToolCall(
            ToolName: "echo",
            ToolCallId: "call-3",
            RawArguments: null,
            Arguments: arguments,
            ParseError: "arguments_malformed",
            ParseWarning: null
        );

        var historyEntry = new ActionEntry(
            Content: "call",
            ToolCalls: new[] { toolCall },
            Invocation: new CompletionDescriptor("provider", "spec", "model")
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                historyEntry,
                new ToolResultsMessage(
                    Content: null,
                    Results: new[] {
                        new ToolResult("echo", "call-3", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        var toolUseBlock = Assert.IsType<AnthropicToolUseBlock>(assistantMessage.Content.Single(block => block is AnthropicToolUseBlock));

        Assert.Equal(3, toolUseBlock.Input.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ConvertToApiRequest_ToolResultsFollowPendingAssistantToolCallOrder() {
        var actionEntry = new ActionEntry(
            Content: string.Empty,
            ToolCalls: new[] {
                new ParsedToolCall("search", "call-1", new Dictionary<string, string>(), new Dictionary<string, object?>(), null, null),
                new ParsedToolCall("lookup", "call-2", new Dictionary<string, string>(), new Dictionary<string, object?>(), null, null)
            },
            Invocation: new CompletionDescriptor("provider", "spec", "model")
        );

        var toolResults = new ToolResultsMessage(
            Content: "Observed external state.",
            Results: new[] {
                new ToolResult("lookup", "call-2", ToolExecutionStatus.Failed, "bad"),
                new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
            },
            ExecuteError: "runner_failed"
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { new ObservationMessage("hi"), actionEntry, toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        Assert.Collection(
            apiRequest.Messages,
            message => {
                Assert.Equal("user", message.Role);
                Assert.Equal("hi", Assert.IsType<AnthropicTextBlock>(Assert.Single(message.Content)).Text);
            },
            message => {
                Assert.Equal("assistant", message.Role);
                Assert.Equal(2, message.Content.Count(block => block is AnthropicToolUseBlock));
            },
            message => {
                Assert.Equal("user", message.Role);
                Assert.Collection(
                    message.Content,
                    block => {
                        var toolResult = Assert.IsType<AnthropicToolResultBlock>(block);
                        Assert.Equal("call-1", toolResult.ToolUseId);
                        Assert.Equal("ok", toolResult.Content);
                        Assert.Null(toolResult.IsError);
                    },
                    block => {
                        var toolResult = Assert.IsType<AnthropicToolResultBlock>(block);
                        Assert.Equal("call-2", toolResult.ToolUseId);
                        Assert.Equal("bad", toolResult.Content);
                        Assert.True(toolResult.IsError);
                    },
                    block => Assert.Equal("Observed external state.", Assert.IsType<AnthropicTextBlock>(block).Text),
                    block => Assert.Equal("[Execution Error]: runner_failed", Assert.IsType<AnthropicTextBlock>(block).Text)
                );
            }
        );
    }

    [Fact]
    public void ConvertToApiRequest_ExecuteErrorOnlyBackfillsPendingToolCalls() {
        var actionEntry = new ActionEntry(
            Content: string.Empty,
            ToolCalls: new[] {
                new ParsedToolCall("search", "call-1", new Dictionary<string, string>(), new Dictionary<string, object?>(), null, null),
                new ParsedToolCall("lookup", "call-2", new Dictionary<string, string>(), new Dictionary<string, object?>(), null, null)
            },
            Invocation: new CompletionDescriptor("provider", "spec", "model")
        );

        var toolResults = new ToolResultsMessage(
            Content: "Observed external state.",
            Results: Array.Empty<ToolResult>(),
            ExecuteError: "runner_failed"
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { new ObservationMessage("hi"), actionEntry, toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var toolResultMessage = apiRequest.Messages.Last();
        Assert.Collection(
            toolResultMessage.Content,
            block => {
                var toolResult = Assert.IsType<AnthropicToolResultBlock>(block);
                Assert.Equal("call-1", toolResult.ToolUseId);
                Assert.Equal("runner_failed", toolResult.Content);
                Assert.True(toolResult.IsError);
            },
            block => {
                var toolResult = Assert.IsType<AnthropicToolResultBlock>(block);
                Assert.Equal("call-2", toolResult.ToolUseId);
                Assert.Equal("runner_failed", toolResult.Content);
                Assert.True(toolResult.IsError);
            },
            block => Assert.Equal("Observed external state.", Assert.IsType<AnthropicTextBlock>(block).Text)
        );
    }

    [Fact]
    public void ConvertToApiRequest_OrphanToolResultsThrow() {
        var toolResults = new ToolResultsMessage(
            Content: null,
            Results: new[] {
                new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
            },
            ExecuteError: null
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("without a preceding assistant tool_use", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_EmptyObservationIsSkipped() {
        // 纯空观测不携带信息，跳过可避免向 Anthropic 发送空 text block
        // (`messages: text content blocks must contain non-whitespace text`)。
        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                new ObservationMessage(null),
                new ObservationMessage("   ")
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var only = Assert.Single(apiRequest.Messages);
        Assert.Equal("user", only.Role);
        Assert.Equal("hi", Assert.IsType<AnthropicTextBlock>(Assert.Single(only.Content)).Text);
    }

    [Fact]
    public void ConvertToApiRequest_LeadingAssistantThrows() {
        // Anthropic 要求第一条消息必须是 user；上游提供了以 Action 开头的历史应被早期拒绝，
        // 而不是静默垫一个会被 API 拒绝的空文本块。
        var actionEntry = new ActionEntry(
            Content: "hello",
            ToolCalls: Array.Empty<ParsedToolCall>(),
            Invocation: new CompletionDescriptor("provider", "spec", "model")
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { actionEntry },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("must start with a user message", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_RichActionMessagePreservesBlockOrdering() {
        var toolCall = new ParsedToolCall(
            ToolName: "search",
            ToolCallId: "call-1",
            RawArguments: new Dictionary<string, string>(),
            Arguments: new Dictionary<string, object?>(),
            ParseError: null,
            ParseWarning: null
        );

        var action = new ProjectedActionMessage(
            Blocks: new ActionBlock[] {
                new ActionBlock.Text("alpha"),
                new ActionBlock.ToolCall(toolCall),
                new ActionBlock.Text("omega")
            }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                action,
                new ToolResultsMessage(
                    Content: "done",
                    Results: new[] {
                        new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);
        var assistant = apiRequest.Messages.Single(message => message.Role == "assistant");

        Assert.Collection(
            assistant.Content,
            block => Assert.Equal("alpha", Assert.IsType<AnthropicTextBlock>(block).Text),
            block => Assert.Equal("call-1", Assert.IsType<AnthropicToolUseBlock>(block).Id),
            block => Assert.Equal("omega", Assert.IsType<AnthropicTextBlock>(block).Text)
        );
    }

    [Fact]
    public void ConvertToApiRequest_RichActionMessageRoundTripsThinkingPayload() {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new {
            type = "thinking",
            thinking = "Let me reason about the tool result.",
            signature = "sig-123"
        });

        var action = new ProjectedActionMessage(
            Blocks: new ActionBlock[] {
                new ActionBlock.Text("alpha"),
                new ActionBlock.Thinking(new CompletionDescriptor("provider", "spec", "model"), payload, "debug"),
                new ActionBlock.Text("omega")
            }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { new ObservationMessage("hi"), action },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);
        var assistant = apiRequest.Messages.Single(message => message.Role == "assistant");

        Assert.Collection(
            assistant.Content,
            block => Assert.Equal("alpha", Assert.IsType<AnthropicTextBlock>(block).Text),
            block => {
                var thinking = Assert.IsType<AnthropicThinkingBlock>(block);
                Assert.Equal("Let me reason about the tool result.", thinking.Thinking);
                Assert.Equal("sig-123", thinking.Signature);
            },
            block => Assert.Equal("omega", Assert.IsType<AnthropicTextBlock>(block).Text)
        );
    }

    [Fact]
    public void ConvertToApiRequest_InvalidThinkingPayloadFailsFast() {
        var action = new ProjectedActionMessage(
            Blocks: new ActionBlock[] {
                new ActionBlock.Thinking(
                    new CompletionDescriptor("provider", "spec", "model"),
                    System.Text.Encoding.UTF8.GetBytes("""{"type":"not-thinking","foo":1}"""),
                    null
                )
            }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { new ObservationMessage("hi"), action },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("Failed to deserialize Anthropic thinking block payload", exception.Message, StringComparison.Ordinal);
    }
}
