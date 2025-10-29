using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Provider.Anthropic;

/// <summary>
/// 将通用的 IContextMessage 上下文转换为 Anthropic Messages API 所需的格式。
/// </summary>
internal static class AnthropicMessageConverter {
    private const string DebugCategory = "Provider";

    public static AnthropicApiRequest ConvertToApiRequest(LlmRequest request) {
        var messages = new List<AnthropicMessage>();

        foreach (var contextMessage in request.Context) {
            var blocks = new List<AnthropicContentBlock>();
            switch (contextMessage) {
                case ModelInputMessage input:
                    if (contextMessage is ToolResultsMessage toolResults) {
                        BuildToolResultsOwnContent(toolResults, blocks);
                    }
                    BuildModelInputOwnContent(input, blocks);
                    messages.Add(
                        new AnthropicMessage {
                            Role = "user",
                            Content = blocks
                        }
                    );
                    break;

                case IModelOutputMessage output:
                    BuildAssistantContent(output, blocks);
                    messages.Add(
                        new AnthropicMessage {
                            Role = "assistant",
                            Content = blocks
                        }
                    );
                    break;
            }
        }

        // 确保消息序列符合 Anthropic 的交错约定
        NormalizeMessageSequence(messages);

        var apiRequest = new AnthropicApiRequest {
            Model = request.ModelId,
            MaxTokens = 4096, // 可配置
            Messages = messages,
            System = string.IsNullOrWhiteSpace(request.SystemInstruction) ? null : request.SystemInstruction,
            Stream = true,
            Tools = BuildToolDefinitions(request.Tools)
        };

        DebugUtil.Print(
            DebugCategory,
            $"[Anthropic] Converted {request.Context.Count} context messages to {messages.Count} API messages, tools={apiRequest.Tools?.Count ?? 0}"
        );
        return apiRequest;
    }

    private static void BuildToolResultsOwnContent(ToolResultsMessage toolResults, List<AnthropicContentBlock> blocks) {
        // Anthropic 要求将多个工具结果聚合到一条 user 消息中
        foreach (var result in toolResults.Results) {
            var isError = result.Status != ToolExecutionStatus.Success;

            blocks.Add(
                new AnthropicToolResultBlock {
                    ToolUseId = result.ToolCallId,
                    Content = result.Result,
                    IsError = isError ? true : null // 仅在出错时写入
                }
            );
        }

        // 如果整体执行失败，追加错误说明
        if (!string.IsNullOrEmpty(toolResults.ExecuteError)) {
            blocks.Add(
                new AnthropicTextBlock {
                    Text = $"[Execution Error]: {toolResults.ExecuteError}"
                }
            );
        }
    }

    private static void BuildModelInputOwnContent(ModelInputMessage input, List<AnthropicContentBlock> blocks) {
        // 处理事件内容分段
        {
            var events = input.Notifications;
            if (!string.IsNullOrWhiteSpace(events)) {
                blocks.Add(new AnthropicTextBlock { Text = events });
            }
        }

        // 处理状态内容分段
        {
            var states = input.Windows;
            if (!string.IsNullOrWhiteSpace(states)) {
                blocks.Add(new AnthropicTextBlock { Text = states });
            }
        }

        // 确保至少有一个内容块
        if (blocks.Count == 0) {
            blocks.Add(new AnthropicTextBlock { Text = "(empty input)" });
        }
    }

    private static void BuildAssistantContent(IModelOutputMessage output, List<AnthropicContentBlock> blocks) {
        // 文本内容
        var content = output.Contents;
        if (!string.IsNullOrWhiteSpace(content)) {
            blocks.Add(new AnthropicTextBlock { Text = content });
        }

        // 工具调用
        foreach (var toolCall in output.ToolCalls) {
            var inputJson = BuildToolInput(toolCall);

            blocks.Add(
                new AnthropicToolUseBlock {
                    Id = toolCall.ToolCallId,
                    Name = toolCall.ToolName,
                    Input = inputJson
                }
            );
        }

        // 确保至少有一个内容块
        if (blocks.Count == 0) {
            blocks.Add(new AnthropicTextBlock { Text = string.Empty });
        }
    }

    private static JsonElement BuildToolInput(ToolCallRequest toolCall) {
        return JsonSerializer.SerializeToElement(toolCall.Arguments);
    }

    /// <summary>
    /// 确保消息序列符合 Anthropic 的交错约定：user ↔ assistant。
    /// 连续的相同角色消息会被合并。
    /// </summary>
    private static void NormalizeMessageSequence(List<AnthropicMessage> messages) {
        if (messages.Count == 0) { return; }

        var normalized = new List<AnthropicMessage>();
        AnthropicMessage? pending = null;

        foreach (var message in messages) {
            if (pending is null) {
                pending = message;
                continue;
            }

            if (pending.Role == message.Role) {
                // 合并相同角色的连续消息
                pending.Content.AddRange(message.Content);
            }
            else {
                normalized.Add(pending);
                pending = message;
            }
        }

        if (pending is not null) {
            normalized.Add(pending);
        }

        messages.Clear();
        messages.AddRange(normalized);

        // Anthropic 要求第一条消息必须是 user
        if (messages.Count > 0 && messages[0].Role != "user") {
            messages.Insert(0,
                new AnthropicMessage {
                    Role = "user",
                    Content = new List<AnthropicContentBlock> {
                        new AnthropicTextBlock { Text = "(context start)" }
                }
                }
            );
        }

        DebugUtil.Print(DebugCategory, $"[Anthropic] Normalized to {messages.Count} messages");
    }

    private static List<AnthropicTool>? BuildToolDefinitions(ImmutableArray<ToolDefinition> tools) {
        if (tools.IsDefaultOrEmpty) { return null; }

        var list = new List<AnthropicTool>(tools.Length);
        foreach (var definition in tools) {
            var schema = JsonToolSchemaBuilder.BuildSchema(definition);
            list.Add(
                new AnthropicTool {
                    Name = definition.Name,
                    Description = definition.Description,
                    InputSchema = schema
                }
            );
        }

        return list;
    }
}
