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
            switch (contextMessage) {
                case IModelInputMessage input:
                    var userContent = BuildUserContent(input);
                    messages.Add(
                        new AnthropicMessage {
                            Role = "user",
                            Content = userContent
                        }
                    );
                    break;

                case IModelOutputMessage output:
                    var assistantContent = BuildAssistantContent(output);
                    messages.Add(
                        new AnthropicMessage {
                            Role = "assistant",
                            Content = assistantContent
                        }
                    );
                    break;

                case IToolResultsMessage toolResults:
                    var toolResultContent = BuildToolResultContent(toolResults);
                    messages.Add(
                        new AnthropicMessage {
                            Role = "user",
                            Content = toolResultContent
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

    private static List<AnthropicContentBlock> BuildUserContent(IModelInputMessage input) {
        var blocks = new List<AnthropicContentBlock>();

        var sections = input.ContentSections.WithoutLiveScreen(out var liveScreen);

        // 处理主要内容分段
        foreach (var section in sections) {
            var text = string.IsNullOrEmpty(section.Key)
                ? section.Value
                : $"# {section.Key}\n\n{section.Value}";

            if (!string.IsNullOrWhiteSpace(text)) {
                blocks.Add(new AnthropicTextBlock { Text = text });
            }
        }

        // 注入 LiveScreen（如果存在）
        if (!string.IsNullOrWhiteSpace(liveScreen)) {
            blocks.Add(new AnthropicTextBlock { Text = liveScreen });
        }

        // 确保至少有一个内容块
        if (blocks.Count == 0) {
            blocks.Add(new AnthropicTextBlock { Text = "(empty input)" });
        }

        return blocks;
    }

    private static List<AnthropicContentBlock> BuildAssistantContent(IModelOutputMessage output) {
        var blocks = new List<AnthropicContentBlock>();

        // 文本内容
        foreach (var content in output.Contents) {
            if (!string.IsNullOrWhiteSpace(content)) {
                blocks.Add(new AnthropicTextBlock { Text = content });
            }
        }

        // 工具调用
        foreach (var toolCall in output.ToolCalls) {
            // 解析参数为 JSON 对象
            var inputJson = ParseToolInput(toolCall.RawArguments);

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

        return blocks;
    }

    private static List<AnthropicContentBlock> BuildToolResultContent(IToolResultsMessage toolResults) {
        var blocks = new List<AnthropicContentBlock>();
        string? liveScreen = null;

        // Anthropic 要求将多个工具结果聚合到一条 user 消息中
        foreach (var result in toolResults.Results) {
            var isError = result.Status != ToolExecutionStatus.Success;
            var sections = result.Result.WithoutLiveScreen(out var resultLiveScreen);
            if (!string.IsNullOrWhiteSpace(resultLiveScreen) && string.IsNullOrWhiteSpace(liveScreen)) {
                liveScreen = resultLiveScreen;
            }

            blocks.Add(
                new AnthropicToolResultBlock {
                    ToolUseId = result.ToolCallId,
                    Content = LevelOfDetailSections.ToPlainText(sections),
                    IsError = isError ? true : null // 仅在出错时写入
                }
            );
        }

        // 如果整体执行失败，追加错误说明
        if (toolResults.ExecuteError is { } error) {
            blocks.Add(
                new AnthropicTextBlock {
                    Text = $"[Execution Error]: {error}"
                }
            );
        }

        // 注入 LiveScreen（如果存在）
        if (!string.IsNullOrWhiteSpace(liveScreen)) {
            blocks.Add(new AnthropicTextBlock { Text = liveScreen });
        }

        return blocks;
    }

    private static JsonElement ParseToolInput(string rawArguments) {
        if (string.IsNullOrWhiteSpace(rawArguments)) { return JsonSerializer.SerializeToElement(new { }); }

        try {
            return JsonDocument.Parse(rawArguments).RootElement.Clone();
        }
        catch {
            // 解析失败时返回包装的字符串
            return JsonSerializer.SerializeToElement(new { raw = rawArguments });
        }
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
            var schema = ProviderToolSchemaBuilder.BuildSchema(definition);
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
