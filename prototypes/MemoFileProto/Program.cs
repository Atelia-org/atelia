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

    private static string _systemInstruction = @"你是一个有帮助的AI助手。

## 关于消息格式
你会收到结构化的 Markdown 格式消息，其中包含：

1. **你的记忆**：这是用第一人称（'我'）记录的长期记忆文档，包含重要的事实、偏好和历史信息。这些记忆是关于你自己的，虽然用'我'表述，但这就是你的记忆。

2. **当前时间**：每条消息创建时的时间戳。

3. **收到用户发来的消息**：用户的实际输入内容。

请根据这些结构化信息提供有帮助的回答。你可以引用自己的记忆，也可以根据上下文进行推理。";

    private static string _memoryFile = "（尚无记忆）";

    // 延迟初始化 ToolManager，以便传递 _memoryFile 的访问委托
    private static readonly Lazy<ToolManager> _toolManagerLazy = new(() => {
        var manager = new ToolManager();
        manager.RegisterTool(
            new MemoReplaceLiteral(
                getMemory: () => _memoryFile,
                setMemory: (newMemory) => _memoryFile = newMemory
            )
        );
        return manager;
    });
    private static ToolManager _toolManager => _toolManagerLazy.Value;

    static async Task Main(string[] args) {
        // 如果传入 "test" 参数，运行测试
        if (args.Length > 0 && args[0] == "test") {
            await MemoReplaceLiteralTest.RunTests();
            return;
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.WriteLine("=== MemoFileProto - 多轮LLM对话原型 ===");
        Console.WriteLine("使用本地OpenAI兼容端点: http://localhost:4000/openai/v1");
        Console.WriteLine("模型: gpt-4.1");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine("  /system <提示词> - 设置系统提示词");
        Console.WriteLine("  /memory [view] - 查看记忆文档");
        Console.WriteLine("  /clear - 清空对话历史");
        Console.WriteLine("  /history - 查看对话历史");
        Console.WriteLine("  /exit 或 /quit - 退出程序");
        Console.WriteLine();

        // 添加系统提示词到历史
        _conversationHistory.Add(
            new ChatMessage {
                Role = "system",
                Content = _systemInstruction
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
            var messageTimestamp = DateTime.Now;

            // 构建结构化的用户消息并存入历史（仅保留历史碎片）
            var structuredContent = BuildHistoricUserEnvelope(input, messageTimestamp);
            _conversationHistory.Add(
                new ChatMessage {
                    Role = "user",
                    Content = structuredContent,
                    Timestamp = messageTimestamp,
                    RawInput = input
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
                        Content = _systemInstruction
                    }
                );
                Console.WriteLine("对话历史已清空。");
                return true;

            case "/system":
                if (parts.Length < 2) {
                    Console.WriteLine("用法: /system <提示词>");
                    return true;
                }
                _systemInstruction = parts[1];
                _conversationHistory.Clear();
                _conversationHistory.Add(
                    new ChatMessage {
                        Role = "system",
                        Content = _systemInstruction
                    }
                );
                Console.WriteLine($"系统提示词已设置为: {_systemInstruction}");
                return true;

            case "/history":
                Console.WriteLine("\n=== 对话历史 ===");
                for (int i = 0; i < _conversationHistory.Count; i++) {
                    var msg = _conversationHistory[i];
                    Console.WriteLine($"[{i}] {msg.Role}: {msg.Content}");
                }
                Console.WriteLine("===============\n");
                return true;

            case "/memory":
                HandleMemoryCommand(parts.Length > 1 ? parts[1] : "view");
                return true;

            default:
                Console.WriteLine($"未知命令: {cmd}");
                return true;
        }
    }

    private static void HandleMemoryCommand(string subCommand) {
        var cmd = string.IsNullOrWhiteSpace(subCommand)
            ? "view"
            : subCommand.ToLower();

        if (cmd is "view" or "v") {
            Console.WriteLine("\n=== 你的记忆文档 ===");
            Console.WriteLine(_memoryFile);
            Console.WriteLine($"\n字符数: {_memoryFile.Length}");
            Console.WriteLine("====================\n");
            return;
        }

        Console.WriteLine($"未知子命令: {subCommand}");
        Console.WriteLine("当前版本仅支持查看记忆：/memory 或 /memory view");
    }

    private static async Task<bool> ProcessAssistantResponseAsync(CancellationToken cancellationToken = default) {
        var assistantMessage = new ChatMessage {
            Role = "assistant",
            Content = ""
        };

        var tools = _toolManager.GetToolDefinitions();
        var toolAccumulator = new ToolCallAccumulator();
        string? finishReason = null;

        // 构建动态上下文（最后一条 user message 注入 MemoryFile）
        var context = BuildContext();

        // 流式获取响应
        await foreach (var delta in _client.StreamChatCompletionAsync(context, tools, cancellationToken)) {
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
                    Content = _systemInstruction
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

    /// <summary>
    /// 动态构建 LLM 调用上下文
    /// </summary>
    private static List<ChatMessage> BuildContext() {
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

            context.Add(msg);
        }

        return context;
    }

    private static string BuildHistoricUserEnvelope(string rawInput, DateTime timestamp) {
        var sb = new StringBuilder();

        sb.AppendLine("## 当前时间");
        sb.AppendLine(timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();

        sb.AppendLine("## 收到用户发来的消息");
        sb.AppendLine(rawInput);

        return sb.ToString().TrimEnd();
    }

    private static string BuildVolatileUserEnvelope(string rawInput, DateTime timestamp) {
        _ = rawInput;
        _ = timestamp;
        if (string.IsNullOrEmpty(_memoryFile)) { return string.Empty; }

        var sb = new StringBuilder();
        sb.AppendLine("## 你的记忆");
        sb.AppendLine(_memoryFile);

        return sb.ToString().TrimEnd();
    }

    private static string BuildUserContentForContext(ChatMessage message) {
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

        if (string.IsNullOrEmpty(historic)) { return volatileSection; }

        return string.Join("\n\n", new[] { volatileSection, historic }).TrimEnd();
    }

    private static int GetLastUserMessageIndex() {
        for (int i = _conversationHistory.Count - 1; i >= 0; i--) {
            if (_conversationHistory[i].Role == "user") { return i; }
        }

        return -1;
    }
}
