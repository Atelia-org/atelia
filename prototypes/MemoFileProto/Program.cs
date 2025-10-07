using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MemoFileProto.Models;
using MemoFileProto.Services;
using MemoFileProto.Tools;

namespace MemoFileProto;

class Program {
    private static readonly List<ChatMessage> _conversationHistory = new();
    private static readonly HttpClient _sharedHttpClient = CreateHttpClient();
    private static readonly OpenAIClient _client = new(_sharedHttpClient);
    private static readonly ToolManager _toolManager = new();
    private static string _systemPrompt = "你是一个有帮助的AI助手。";

    static async Task Main(string[] args) {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.WriteLine("=== MemoFileProto - 多轮LLM对话原型 ===");
        Console.WriteLine("使用本地OpenAI兼容端点: http://localhost:4000/openai/v1");
        Console.WriteLine("模型: gpt-4.1");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine("  /system <提示词> - 设置系统提示词");
        Console.WriteLine("  /clear - 清空对话历史");
        Console.WriteLine("  /history - 查看对话历史");
        Console.WriteLine("  /exit 或 /quit - 退出程序");
        Console.WriteLine();

        // 添加系统提示词到历史
        _conversationHistory.Add(
            new ChatMessage {
                Role = "system",
                Content = _systemPrompt
            }
        );

        while (true) {
            Console.Write("User> ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input)) { continue; }

            // 处理命令
            if (input.StartsWith("/")) {
                if (!HandleCommand(input)) { break; /* 退出程序 */ }
                continue;
            }

            var rollbackIndex = _conversationHistory.Count;

            // 添加用户消息到历史
            _conversationHistory.Add(
                new ChatMessage {
                    Role = "user",
                    Content = input
                }
            );

            try {
                Console.Write("Assistant> ");
                var requiresFollowUp = await ProcessAssistantResponseAsync();
                while (requiresFollowUp) {
                    Console.WriteLine();
                    Console.Write("Assistant> ");
                    requiresFollowUp = await ProcessAssistantResponseAsync();
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[错误] 获取助手响应失败: {ex.Message}");
                TrimConversationHistory(rollbackIndex);
            }

            Console.WriteLine();
            Console.WriteLine();
        }

        _client.Dispose();
        Console.WriteLine("再见！");
    }

    private static bool HandleCommand(string command) {
        var parts = command.Split(' ', 2);
        var cmd = parts[0].ToLower();

        switch (cmd) {
            case "/exit":
            case "/quit":
                return false;

            case "/clear":
                _conversationHistory.Clear();
                _conversationHistory.Add(
                    new ChatMessage {
                        Role = "system",
                        Content = _systemPrompt
                    }
                );
                Console.WriteLine("对话历史已清空。");
                return true;

            case "/system":
                if (parts.Length < 2) {
                    Console.WriteLine("用法: /system <提示词>");
                    return true;
                }
                _systemPrompt = parts[1];
                _conversationHistory.Clear();
                _conversationHistory.Add(
                    new ChatMessage {
                        Role = "system",
                        Content = _systemPrompt
                    }
                );
                Console.WriteLine($"系统提示词已设置为: {_systemPrompt}");
                return true;

            case "/history":
                Console.WriteLine("\n=== 对话历史 ===");
                for (int i = 0; i < _conversationHistory.Count; i++) {
                    var msg = _conversationHistory[i];
                    Console.WriteLine($"[{i}] {msg.Role}: {msg.Content}");
                }
                Console.WriteLine("===============\n");
                return true;

            default:
                Console.WriteLine($"未知命令: {cmd}");
                return true;
        }
    }

    private static async Task<bool> ProcessAssistantResponseAsync(CancellationToken cancellationToken = default) {
        var assistantMessage = new ChatMessage {
            Role = "assistant",
            Content = ""
        };

        var tools = _toolManager.GetToolDefinitions();
        var toolAccumulator = new ToolCallAccumulator();
        string? finishReason = null;

        // 流式获取响应
        await foreach (var delta in _client.StreamChatCompletionAsync(_conversationHistory, tools, cancellationToken)) {
            if (!string.IsNullOrEmpty(delta.Content)) {
                Console.Write(delta.Content);
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
                Console.WriteLine($"\n[工具调用] {call.Function.Name}");
                Console.WriteLine("[参数]\n" + FormatToolArguments(call.Function.Arguments));
                var toolResult = await ExecuteToolSafelyAsync(call);
                _conversationHistory.Add(
                    new ChatMessage {
                        Role = "tool",
                        ToolCallId = call.Id,
                        Content = toolResult
                    }
                );
                Console.WriteLine($"[工具结果] {toolResult}");
            }

            return true;
        }

        _conversationHistory.Add(assistantMessage);
        return false;
    }

    private static async Task<string> ExecuteToolSafelyAsync(ToolCall call) {
        try {
            return await _toolManager.ExecuteToolAsync(call.Function.Name, call.Function.Arguments);
        }
        catch (Exception ex) {
            return $"Error: {ex.Message}";
        }
    }

    private static void TrimConversationHistory(int startIndex) {
        if (startIndex < 0 || startIndex >= _conversationHistory.Count) { return; }

        _conversationHistory.RemoveRange(startIndex, _conversationHistory.Count - startIndex);

        if (_conversationHistory.Count == 0) {
            _conversationHistory.Add(
                new ChatMessage {
                    Role = "system",
                    Content = _systemPrompt
                }
            );
        }
    }

    private static HttpClient CreateHttpClient() {
        return new HttpClient {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    private static readonly JsonSerializerOptions PrettyJsonOptions = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string FormatToolArguments(string rawArguments) {
        if (string.IsNullOrWhiteSpace(rawArguments)) { return "(无参数)"; }

        try {
            using var document = JsonDocument.Parse(rawArguments);
            return JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
        }
        catch {
            return rawArguments;
        }
    }
}
