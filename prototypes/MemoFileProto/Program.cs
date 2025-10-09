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

    static async Task Main(string[] args) {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.WriteLine("=== MemoFileProto - 多轮LLM对话原型 ===");
        Console.WriteLine("使用本地OpenAI兼容端点: http://localhost:4000/openai/v1");
        Console.WriteLine("模型: vscode-lm-proxy");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine("  /system <提示词> - 设置系统提示词");
        Console.WriteLine("  /memory [view] - 查看记忆文档");
        Console.WriteLine("  /clear - 清空对话历史");
        Console.WriteLine("  /history - 查看对话历史");
        Console.WriteLine("  /exit 或 /quit - 退出程序");
        Console.WriteLine();

        using var httpClient = CreateHttpClient();
        using var client = new OpenAIClient(httpClient);
        var agent = new LlmAgent(client);

        while (true) {
            Console.Write("User> ");
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
                        Console.Write("Assistant> ");
                    },
                    onContentDelta: delta => Console.Write(delta),
                    onToolCall: call => {
                        Console.WriteLine($"\n[工具调用] {call.Function.Name}");
                        Console.WriteLine("[参数]\n" + FormatToolArguments(call.Function.Arguments));
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

            case "/memory":
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
            var memory = agent.MemorySnapshot;
            Console.WriteLine("\n=== 你的记忆文档 ===");
            Console.WriteLine(memory);
            Console.WriteLine($"\n字符数: {memory.Length}");
            Console.WriteLine("====================\n");
            return;
        }

        Console.WriteLine($"未知子命令: {subCommand}");
        Console.WriteLine("当前版本仅支持查看记忆：/memory 或 /memory view");
    }

    private static void PrintHistory(IReadOnlyList<ChatMessage> history) {
        Console.WriteLine("\n=== 对话历史 ===");
        for (int i = 0; i < history.Count; i++) {
            var msg = history[i];
            Console.WriteLine($"[{i}] {msg.Role}: {msg.Content}");
        }
        Console.WriteLine("===============\n");
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
