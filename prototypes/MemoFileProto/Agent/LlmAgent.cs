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
    private readonly OpenAIClient _client;
    private readonly ToolManager _toolManager;
    private readonly List<ChatMessage> _conversationHistory = new();

    private string _systemInstruction;
    private string _memoryFile = "（尚无记忆）";

    public const string DefaultSystemInstruction = @"你是一个有帮助的AI助手。

## 关于消息格式
你会收到结构化的 Markdown 格式消息，其中包含：

1. **你的记忆**：这是用第一人称（'我'）记录的长期记忆文档，包含重要的事实、偏好和历史信息。这些记忆是关于你自己的，虽然用'我'表述，但这就是你的记忆。

2. **当前时间**：每条消息创建时的时间戳。

3. **收到用户发来的消息**：用户的实际输入内容。

请根据这些结构化信息提供有帮助的回答。";

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
                Role = "system",
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
        var timestamp = DateTime.Now;
        var structuredContent = BuildHistoricUserEnvelope(userInput, timestamp);

        _conversationHistory.Add(
            new ChatMessage {
                Role = "user",
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
        var assistantMessage = new ChatMessage {
            Role = "assistant",
            Content = string.Empty
        };

        var tools = _toolManager.GetToolDefinitions();
        var toolAccumulator = new ToolCallAccumulator();
        string? finishReason = null;
        var context = BuildContext();

        await foreach (var delta in _client.StreamChatCompletionAsync(context, tools, cancellationToken)) {
            if (!string.IsNullOrEmpty(delta.Content)) {
                onContentDelta?.Invoke(delta.Content);
                assistantMessage.Content += delta.Content;
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

        var requiresToolCall = finishReason?.Equals("tool_calls", StringComparison.OrdinalIgnoreCase) == true
            && toolAccumulator.HasPendingToolCalls;

        if (requiresToolCall) {
            var toolCalls = toolAccumulator.BuildFinalCalls();
            assistantMessage.ToolCalls = toolCalls.ToList();
            _conversationHistory.Add(assistantMessage);

            foreach (var call in toolCalls) {
                onToolCall?.Invoke(call);
                var toolResult = await ExecuteToolSafelyAsync(call);
                _conversationHistory.Add(
                    new ChatMessage {
                        Role = "tool",
                        ToolCallId = call.Id,
                        Content = toolResult
                    }
                );
                onToolResult?.Invoke(toolResult);
            }

            return true;
        }

        _conversationHistory.Add(assistantMessage);
        return false;
    }

    private async Task<string> ExecuteToolSafelyAsync(ToolCall call) {
        try {
            return await _toolManager.ExecuteToolAsync(call.Function.Name, call.Function.Arguments);
        }
        catch (Exception ex) {
            return $"Error: {ex.Message}";
        }
    }

    private List<ChatMessage> BuildContext() {
        var context = new List<ChatMessage> {
            new ChatMessage {
                Role = "system",
                Content = _systemInstruction
            }
        };

        var lastUserIndex = GetLastUserMessageIndex();

        for (int i = 0; i < _conversationHistory.Count; i++) {
            var msg = _conversationHistory[i];
            if (msg.Role == "system") { continue; }

            if (msg.Role == "user" && i == lastUserIndex) {
                var combinedContent = BuildUserContentForContext(msg);
                context.Add(
                    new ChatMessage {
                        Role = "user",
                        Content = combinedContent
                    }
                );
                continue;
            }

            context.Add(
                new ChatMessage {
                    Role = msg.Role,
                    Content = msg.Content,
                    ToolCalls = msg.ToolCalls
                }
            );
        }

        return context;
    }

    private string BuildHistoricUserEnvelope(string rawInput, DateTime timestamp) {
        var sb = new StringBuilder();

        sb.AppendLine("## 当前时间");
        sb.AppendLine(timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();

        sb.AppendLine("## 收到用户发来的消息");
        sb.AppendLine(rawInput);

        return sb.ToString().TrimEnd();
    }

    private string BuildVolatileUserEnvelope(string rawInput, DateTime timestamp) {
        _ = rawInput;
        _ = timestamp;
        if (string.IsNullOrEmpty(_memoryFile)) { return string.Empty; }

        var sb = new StringBuilder();
        sb.AppendLine("## 你的记忆");
        sb.AppendLine(_memoryFile);

        return sb.ToString().TrimEnd();
    }

    private string BuildUserContentForContext(ChatMessage message) {
        var historicContent = message.Content;
        if (string.IsNullOrWhiteSpace(historicContent)) { return historicContent ?? string.Empty; }

        var rawInput = message.RawInput ?? message.Content;
        var timestamp = message.Timestamp ?? DateTime.Now;
        var volatileContent = BuildVolatileUserEnvelope(rawInput, timestamp);

        return CombineContentBlocks(volatileContent, historicContent);
    }

    private static string CombineContentBlocks(string? volatileBlock, string? historicBlock) {
        var historic = historicBlock?.TrimEnd();
        var volatileSection = volatileBlock?.TrimEnd();

        if (string.IsNullOrEmpty(volatileSection)) { return historicBlock ?? string.Empty; }
        if (string.IsNullOrEmpty(historic)) { return volatileSection ?? string.Empty; }

        return string.Join("\n\n", new[] { volatileSection, historic }).TrimEnd();
    }

    private int GetLastUserMessageIndex() {
        for (int i = _conversationHistory.Count - 1; i >= 0; i--) {
            if (_conversationHistory[i].Role == "user") { return i; }
        }

        return -1;
    }

    private void TrimConversationHistory(int startIndex) {
        if (startIndex < 0 || startIndex >= _conversationHistory.Count) { return; }

        _conversationHistory.RemoveRange(startIndex, _conversationHistory.Count - startIndex);

        if (_conversationHistory.Count == 0) {
            _conversationHistory.Add(
                new ChatMessage {
                    Role = "system",
                    Content = _systemInstruction
                }
            );
        }
    }
}
