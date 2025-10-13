using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using MemoFileProto.Agent;
using MemoFileProto.Models;
using MemoFileProto.Services;

namespace MemoFileProto;

class Program {
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly IReadOnlyDictionary<string, ConsoleColor> RoleColorMap =
        new Dictionary<string, ConsoleColor>(StringComparer.OrdinalIgnoreCase) {
            [LlmAgent.RoleSystem] = ConsoleColor.Yellow,
            [LlmAgent.RoleUser] = ConsoleColor.Green,
            [LlmAgent.RoleAssistant] = ConsoleColor.Cyan,
            [LlmAgent.RoleTool] = ConsoleColor.Magenta
        };

    private static readonly ConsoleColor
        MetaColor = ConsoleColor.DarkBlue,
        ToolCallColor = ConsoleColor.DarkMagenta;

    static async Task Main(string[] args) {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.WriteLine("=== MemoFileProto - 多轮LLM对话原型 ===");
        Console.WriteLine("使用本地OpenAI兼容端点: http://localhost:4000/openai/v1");
        Console.WriteLine("模型: vscode-lm-proxy");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine("  /system <提示词> - 设置系统提示词");
        Console.WriteLine("  /notebook [view] - 查看[Memory Notebook]");
        Console.WriteLine("  /clear - 清空对话历史");
        Console.WriteLine("  /history - 查看对话历史");
        Console.WriteLine("  /exit 或 /quit - 退出程序");
        Console.WriteLine();

        using var httpClient = CreateHttpClient();
        using var client = new OpenAIClient(httpClient);
        var agent = new LlmAgent(client);

        while (true) {
            WriteRolePrompt(LlmAgent.RoleUser, "User> ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input)) { continue; }

            if (input.StartsWith("/")) {
                if (!HandleCommand(agent, input)) { break; }
                continue;
            }

            try {
                await agent.SendAsync(
                    input,
                    onAssistantTurnStart: isFollowUp => {
                        if (isFollowUp) {
                            Console.WriteLine();
                            Console.WriteLine();
                        }
                        WriteRolePrompt(LlmAgent.RoleAssistant, "Assistant> ");
                    },
                    onContentDelta: delta => Console.Write(delta),
                    onToolCall: call => {
                        Console.WriteLine($"\n[工具调用] {call.Name} (Id: {call.Id})");
                        Console.WriteLine("[参数]\n" + FormatToolArguments(call.Arguments));
                    },
                    onToolResult: result => Console.WriteLine($"[工具结果] {result}")
                );
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[错误] 获取助手响应失败: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine();
        }

        Console.WriteLine("再见！");
    }

    private static bool HandleCommand(LlmAgent agent, string command) {
        var parts = command.Split(' ', 2);
        var cmd = parts[0].ToLower();

        switch (cmd) {
            case "/exit":
            case "/quit":
                return false;

            case "/clear":
                agent.ResetConversation();
                Console.WriteLine("对话历史已清空。");
                return true;

            case "/system":
                if (parts.Length < 2) {
                    Console.WriteLine("用法: /system <提示词>");
                    return true;
                }
                agent.SetSystemInstruction(parts[1]);
                Console.WriteLine($"系统提示词已设置为: {parts[1]}");
                return true;

            case "/history":
                PrintHistory(agent.ConversationHistory);
                return true;

            case "/notebook":
                HandleMemoryCommand(agent, parts.Length > 1 ? parts[1] : "view");
                return true;

            default:
                Console.WriteLine($"未知命令: {cmd}");
                return true;
        }
    }

    private static void HandleMemoryCommand(LlmAgent agent, string subCommand) {
        var cmd = string.IsNullOrWhiteSpace(subCommand)
            ? "view"
            : subCommand.ToLower();

        if (cmd is "view" or "v") {
            var notes = agent.MemoryNotebookSnapshot;
            Console.WriteLine("\n=== [Memory Notebook] ===");
            Console.WriteLine(notes);
            Console.WriteLine($"\n字符数: {notes.Length}");
            Console.WriteLine("====================\n");
            return;
        }

        Console.WriteLine($"未知子命令: {subCommand}");
        Console.WriteLine("当前版本仅支持查看[Memory Notebook]：/notebook 或 /notebook view");
    }

    private static void PrintHistory(IReadOnlyList<UniversalMessage> history) {
        var originalColor = Console.ForegroundColor;
        try {
            WriteSeparatorLine();

            for (int i = 0; i < history.Count; i++) {
                WriteMessage(history[i], i);
                WriteSeparatorLine();
            }
        }
        finally {
            Console.ForegroundColor = originalColor;
        }
    }

    private static void WriteMessage(UniversalMessage message, int index) {
        var roleColor = GetRoleColor(message.Role);
        WriteColoredLine(roleColor, $"[{index}] {message.Role}");

        if (message.Timestamp is { } timestamp) {
            WriteColoredLine(MetaColor, $"  Timestamp: {timestamp:o}");
        }

        if (!string.IsNullOrWhiteSpace(message.Content)) {
            Console.ResetColor();
            Console.WriteLine(message.Content);
        }

        if (message.ToolCalls is { Count: > 0 }) {
            Console.WriteLine();
            WriteColoredLine(ToolCallColor, $"  Tool Calls ({message.ToolCalls.Count}):");
            foreach (var call in message.ToolCalls) {
                WriteColoredLine(ToolCallColor, $"    - Id: {call.Id}, Name: {call.Name}");

                if (!string.IsNullOrWhiteSpace(call.Arguments)) {
                    var formatted = FormatToolArguments(call.Arguments);
                    WriteMultilineIndented(formatted, "      ");
                }
            }
        }

        if (message.ToolResults is { Count: > 0 }) {
            Console.WriteLine();
            WriteColoredLine(ToolCallColor, $"  Tool Results ({message.ToolResults.Count}):");
            foreach (var result in message.ToolResults) {
                var status = result.IsError ? " [Error]" : string.Empty;
                WriteColoredLine(
                    ToolCallColor,
                    $"    - ToolCallId: {result.ToolCallId}, Name: {result.ToolName ?? "(unknown)"}{status}"
                );

                if (!string.IsNullOrWhiteSpace(result.Content)) {
                    WriteMultilineIndented(result.Content, "      ");
                }
            }
        }
    }

    private static void WriteSeparatorLine() {
        const string separator = "=================================================="; // 50 '='
        Console.ForegroundColor = MetaColor;
        Console.WriteLine(separator);
        Console.ResetColor();
    }

    private static void WriteColoredLine(ConsoleColor color, string text) {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    private static ConsoleColor GetRoleColor(string role) {
        return RoleColorMap.TryGetValue(role, out var color)
            ? color
            : ConsoleColor.White;
    }

    private static void WriteRolePrompt(string role, string prompt) {
        var color = GetRoleColor(role);
        Console.ForegroundColor = color;
        Console.Write(prompt);
        Console.ResetColor();
    }

    private static void WriteMultilineIndented(string text, string indent) {
        if (string.IsNullOrEmpty(text)) { return; }

        var segments = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var segment in segments) {
            Console.Write(indent);
            Console.WriteLine(segment);
        }
    }

    private static HttpClient CreateHttpClient() {
        return new HttpClient {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

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
