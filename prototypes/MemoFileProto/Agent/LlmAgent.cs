using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MemoFileProto.Models;
using MemoFileProto.Services;
using MemoFileProto.Tools;

namespace MemoFileProto.Agent;

public class LlmAgent {
    public static readonly string
        RoleSystem = "system",
        RoleAssistant = "assistant",
        RoleUser = "user",
        RoleTool = "tool";

    // 对于需要拼接的文本，尽量从外部文本源头就处理换行和Trim，这样我们内部就不用反复做换行标准化和Trim了。文本文件编辑这类需要精确保持原样的数据除外。
    public static readonly string
        // 约定各Envelope Trim首尾，因此结尾不含换行。
        EnvelopeSeparator = "\n\n",
        // 同一用"\n"换行，方便跨行搜索。
        InternalNewLine = "\n";

    internal static StringBuilder NormalizeLineEndings(StringBuilder sb) {
        return sb.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private readonly OpenAIClient _client;
    private readonly ToolManager _toolManager;
    private readonly List<ChatMessage> _conversationHistory = new();

    private string _systemInstruction;
    private string _memoryFile = "（尚无记忆）";

    public static readonly string DefaultSystemInstruction = TextToolUtilities.NormalizeLineEndings(
        @"你是一个有帮助的AI助手。

## 关于消息格式
你会收到结构化的 Markdown 格式消息，其中包含：

1. **你的记忆**：这是用第一人称（'我'）记录的长期记忆文档，包含重要的事实、偏好和历史信息。这些记忆是关于你自己的，虽然用'我'表述，但这就是你的记忆。

2. **当前时间**：每条消息创建时的时间戳。

3. **收到用户发来的消息**：用户的实际输入内容。

请根据这些结构化信息提供有帮助的回答。"
    );

    public LlmAgent(OpenAIClient client) {
        _client = client;
        _toolManager = new ToolManager();
        _toolManager.RegisterTool(
            new MemoReplaceLiteral(
                getMemory: () => _memoryFile,
                setMemory: newMemory => _memoryFile = newMemory
            )
        );

        _systemInstruction = DefaultSystemInstruction;
        ResetConversation();
    }

    public IReadOnlyList<ChatMessage> ConversationHistory => _conversationHistory;

    public string MemorySnapshot => _memoryFile;

    public string SystemInstruction => _systemInstruction;

    public void ResetConversation() {
        _conversationHistory.Clear();
        _conversationHistory.Add(
            new ChatMessage {
                Role = RoleSystem,
                Content = _systemInstruction
            }
        );
    }

    public void SetSystemInstruction(string instruction) {
        _systemInstruction = instruction;
        ResetConversation();
    }

    public async Task SendAsync(
        string userInput,
        Action<bool>? onAssistantTurnStart,
        Action<string>? onContentDelta,
        Action<ToolCall>? onToolCall,
        Action<string>? onToolResult,
        CancellationToken cancellationToken = default
    ) {
        if (string.IsNullOrWhiteSpace(userInput)) { return; }

        var rollbackIndex = _conversationHistory.Count;
        var timestamp = DateTimeOffset.Now;
        var structuredContent = BuildHistoricUserEnvelope(userInput, timestamp);

        _conversationHistory.Add(
            new ChatMessage {
                Role = RoleUser,
                Content = structuredContent,
                Timestamp = timestamp,
                RawInput = userInput
            }
        );

        try {
            var isFollowUp = false;
            bool requiresFollowUp;
            do {
                onAssistantTurnStart?.Invoke(isFollowUp);
                requiresFollowUp = await ProcessAssistantResponseAsync(onContentDelta, onToolCall, onToolResult, cancellationToken);
                if (requiresFollowUp) {
                    isFollowUp = true;
                }
            } while (requiresFollowUp);
        }
        catch {
            TrimConversationHistory(rollbackIndex);
            throw;
        }
    }

    private async Task<bool> ProcessAssistantResponseAsync(
        Action<string>? onContentDelta,
        Action<ToolCall>? onToolCall,
        Action<string>? onToolResult,
        CancellationToken cancellationToken
    ) {

        var tools = _toolManager.GetToolDefinitions();
        var toolAccumulator = new ToolCallAccumulator();
        string? finishReason = null;

        var sb = new StringBuilder();
        await foreach (var delta in _client.StreamChatCompletionAsync(BuildLiveContext(), tools, cancellationToken)) {
            if (!string.IsNullOrEmpty(delta.Content)) {
                onContentDelta?.Invoke(delta.Content);
                sb.Append(delta.Content);
            }

            if (delta.ToolCalls is { Count: > 0 }) {
                foreach (var toolDelta in delta.ToolCalls) {
                    toolAccumulator.AddDelta(toolDelta);
                }
            }

            if (!string.IsNullOrWhiteSpace(delta.FinishReason)) {
                finishReason = delta.FinishReason;
            }
        }

        var assistantMessage = new ChatMessage {
            Role = RoleAssistant,
            // 从源头就Trim
            Content = NormalizeLineEndings(sb).ToString().Trim()
        };

        var requiresToolCall = finishReason?.Equals("tool_calls", StringComparison.OrdinalIgnoreCase) == true
            && toolAccumulator.HasPendingToolCalls;

        if (requiresToolCall) {
            var toolCalls = toolAccumulator.BuildFinalCalls();
            assistantMessage.ToolCalls = toolCalls.ToList();
            _conversationHistory.Add(assistantMessage);

            bool encounteredFailure = false;

            foreach (var call in toolCalls) {
                onToolCall?.Invoke(call);

                string toolContent;
                if (encounteredFailure) {
                    toolContent = "Skipped: not executed because a previous tool call failed.";
                }
                else {
                    var executionResult = await ExecuteToolSafelyAsync(call);
                    toolContent = executionResult.Content;

                    if (!executionResult.Success) {
                        encounteredFailure = true;
                    }
                }

                _conversationHistory.Add(
                    new ChatMessage {
                        Role = RoleTool,
                        ToolCallId = call.Id,
                        Content = BuildHistoricToolEnvelope(toolContent, DateTimeOffset.Now)
                    }
                );
                onToolResult?.Invoke(toolContent);
            }

            return true;
        }

        _conversationHistory.Add(assistantMessage);
        return false;
    }

    private async Task<ToolExecutionResult> ExecuteToolSafelyAsync(ToolCall call) {
        try {
            var content = await _toolManager.ExecuteToolAsync(call.Function.Name, call.Function.Arguments);
            return new ToolExecutionResult(true, content);
        }
        catch (Exception ex) {
            return new ToolExecutionResult(false, $"Error: {ex.Message}");
        }
    }

    private readonly record struct ToolExecutionResult(bool Success, string Content);

    private List<ChatMessage> BuildLiveContext() {
        var context = new List<ChatMessage>(_conversationHistory.Count + 1);
        var hasDecoratedLastSenseMessage = false;

        for (int i = _conversationHistory.Count; --i >= 0;) {
            var msg = _conversationHistory[i];
            if (msg.Role == RoleSystem) { continue; }

            if (!hasDecoratedLastSenseMessage && (msg.Role == RoleUser || msg.Role == RoleTool)) {
                var combinedContent = BuildContentForLiveContext(msg);
                if (msg.Role == RoleUser) {
                    context.Add(
                        new ChatMessage {
                            Role = msg.Role,
                            Content = combinedContent
                        }
                    );
                }
                else {
                    context.Add(
                        new ChatMessage {
                            Role = msg.Role,
                            ToolCallId = msg.ToolCallId,
                            Content = combinedContent
                        }
                    );
                }

                hasDecoratedLastSenseMessage = true;
                continue;
            }

            context.Add(msg);
        }

        context.Add(
            new ChatMessage {
                Role = RoleSystem,
                Content = _systemInstruction
            }
        );

        context.Reverse();
        return context;
    }

    private void AppendTimestampEnvelop(StringBuilder sb, DateTimeOffset timestamp) {
        sb.Append(EnvelopeSeparator);
        sb.Append("## Timestamp:").Append(InternalNewLine);
        sb.Append(timestamp.ToString("o"));
    }

    private string _BuildHistoricEnvelope(string heading, string trimedContent, DateTimeOffset timestamp) {
        var sb = new StringBuilder();
        sb.Append(heading).Append(InternalNewLine);
        sb.Append(trimedContent);

        AppendTimestampEnvelop(sb, timestamp);

        return sb.ToString();
    }

    private string BuildHistoricUserEnvelope(string trimedContent, DateTimeOffset timestamp) {
        return _BuildHistoricEnvelope("## Received Message:", trimedContent, timestamp);
    }

    private string BuildHistoricToolEnvelope(string trimedContent, DateTimeOffset timestamp) {
        return _BuildHistoricEnvelope("## Tool Result:", trimedContent, timestamp);
    }

    private string BuildVolatileEnvelope() {
        if (string.IsNullOrEmpty(_memoryFile)) { return string.Empty; }

        var sb = new StringBuilder();
        sb.AppendLine("## 你的记忆");
        sb.AppendLine(_memoryFile);

        return sb.ToString().TrimEnd();
    }

    private string BuildContentForLiveContext(ChatMessage message) {
        var historicContent = message.Content;
        if (string.IsNullOrWhiteSpace(historicContent)) { return historicContent ?? string.Empty; }

        var volatileContent = BuildVolatileEnvelope();

        return CombineContentBlocks(historicContent, volatileContent);
    }

    private static string CombineContentBlocks(string historicEnvelope, string volatileEnvelope) {
        if (string.IsNullOrEmpty(historicEnvelope)) { return volatileEnvelope; }
        if (string.IsNullOrEmpty(volatileEnvelope)) { return historicEnvelope; }

        return string.Concat(historicEnvelope, EnvelopeSeparator, volatileEnvelope);
    }

    private void TrimConversationHistory(int startIndex) {
        if (startIndex < 0 || startIndex >= _conversationHistory.Count) { return; }

        _conversationHistory.RemoveRange(startIndex, _conversationHistory.Count - startIndex);

        if (_conversationHistory.Count == 0) {
            _conversationHistory.Add(
                new ChatMessage {
                    Role = RoleSystem,
                    Content = _systemInstruction
                }
            );
        }
    }
}
