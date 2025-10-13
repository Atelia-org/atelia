using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Provider.Anthropic.Examples;

/// <summary>
/// Anthropic Provider 集成示例。
/// </summary>
internal static class AnthropicIntegrationExample {
    /// <summary>
    /// 演示基础的对话调用。
    /// </summary>
    public static async Task SimpleConversationExample() {
        // 1. 初始化客户端
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is required.");

        var client = new AnthropicProviderClient(apiKey);

        // 2. 构建上下文
        var context = new List<IContextMessage> {
            new SystemInstructionMessage("You are a helpful assistant that answers concisely.") {
                Timestamp = DateTimeOffset.UtcNow
            },
            new ModelInputEntry(
                new List<KeyValuePair<string, string>> {
                    new("User Query", "What is the capital of Japan?")
            }
            ) {
                Timestamp = DateTimeOffset.UtcNow
            }
        };

        // 3. 创建请求
        var request = new ProviderRequest(
            StrategyId: "anthropic-v1",
            Invocation: new ModelInvocationDescriptor(
                ProviderId: "anthropic",
                Specification: "messages-v1",
                Model: "claude-3-5-sonnet-20241022"
            ),
            Context: context,
            StubScriptName: null
        );

        // 4. 流式调用
        Console.WriteLine("=== Anthropic Response ===");
        await foreach (var delta in client.CallModelAsync(request, CancellationToken.None)) {
            switch (delta.Kind) {
                case ModelOutputDeltaKind.Content:
                    Console.Write(delta.ContentFragment);
                    break;

                case ModelOutputDeltaKind.TokenUsage:
                    Console.WriteLine($"\n\n[Token Usage]");
                    Console.WriteLine($"  Prompt: {delta.TokenUsage?.PromptTokens}");
                    Console.WriteLine($"  Completion: {delta.TokenUsage?.CompletionTokens}");
                    if (delta.TokenUsage?.CachedPromptTokens is { } cached) {
                        Console.WriteLine($"  Cached: {cached}");
                    }
                    break;

                case ModelOutputDeltaKind.ExecuteError:
                    Console.WriteLine($"\n[Error] {delta.ExecuteError}");
                    break;
            }
        }
        Console.WriteLine("\n=== End ===");
    }

    /// <summary>
    /// 演示多轮对话与工具调用（需要真实工具执行器配合）。
    /// </summary>
    public static async Task MultiTurnWithToolsExample() {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;
        var client = new AnthropicProviderClient(apiKey);

        var context = new List<IContextMessage> {
            new SystemInstructionMessage("You are an AI assistant with access to weather tools.") {
                Timestamp = DateTimeOffset.UtcNow
            },
            new ModelInputEntry(
                new List<KeyValuePair<string, string>> {
                    new("", "What's the weather in Tokyo?")
            }
            ) {
                Timestamp = DateTimeOffset.UtcNow
            }
        };

        var request = new ProviderRequest(
            StrategyId: "anthropic-v1",
            Invocation: new ModelInvocationDescriptor("anthropic", "messages-v1", "claude-3-5-sonnet-20241022"),
            Context: context,
            StubScriptName: null
        );

        Console.WriteLine("=== User: What's the weather in Tokyo? ===");

        var toolCalls = new List<ToolCallRequest>();

        await foreach (var delta in client.CallModelAsync(request, CancellationToken.None)) {
            switch (delta.Kind) {
                case ModelOutputDeltaKind.Content:
                    Console.Write(delta.ContentFragment);
                    break;

                case ModelOutputDeltaKind.ToolCallDeclared:
                    if (delta.ToolCallRequest is { } toolCall) {
                        toolCalls.Add(toolCall);
                        Console.WriteLine($"\n[Tool Call Declared]");
                        Console.WriteLine($"  Tool: {toolCall.ToolName}");
                        Console.WriteLine($"  ID: {toolCall.ToolCallId}");
                        Console.WriteLine($"  Arguments: {toolCall.RawArguments}");
                    }
                    break;
            }
        }

        // 模拟工具执行结果
        if (toolCalls.Count > 0) {
            Console.WriteLine("\n[Simulating Tool Execution...]");

            var results = new List<ToolCallResult>();
            foreach (var call in toolCalls) {
                // 这里应该调用真实的工具执行器
                var mockResult = call.ToolName switch {
                    "get_weather" => "Sunny, 18°C, humidity 65%",
                    _ => "Tool not implemented"
                };

                results.Add(
                    new ToolCallResult(
                        call.ToolName,
                        call.ToolCallId,
                        ToolExecutionStatus.Success,
                        mockResult,
                        TimeSpan.FromMilliseconds(150)
                    )
                );
            }

            // 追加工具结果并继续对话
            context.Add(
                new ToolResultsEntry(results, null) {
                    Timestamp = DateTimeOffset.UtcNow
                }
            );

            Console.WriteLine("\n=== Assistant (after tool execution): ===");

            var followUpRequest = new ProviderRequest(
                StrategyId: "anthropic-v1",
                Invocation: new ModelInvocationDescriptor("anthropic", "messages-v1", "claude-3-5-sonnet-20241022"),
                Context: context,
                StubScriptName: null
            );

            await foreach (var delta in client.CallModelAsync(followUpRequest, CancellationToken.None)) {
                if (delta.Kind == ModelOutputDeltaKind.Content) {
                    Console.Write(delta.ContentFragment);
                }
            }
        }

        Console.WriteLine("\n=== End ===");
    }

    /// <summary>
    /// 演示 LiveScreen 注入。
    /// </summary>
    public static async Task LiveScreenExample() {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;
        var client = new AnthropicProviderClient(apiKey);

        var input = new ModelInputEntry(
            new List<KeyValuePair<string, string>> {
                new("Task", "Summarize the current system state.")
        }
        ) {
            Timestamp = DateTimeOffset.UtcNow
        };

        var liveScreen = @"# [Live Screen]
## System Status
- CPU: 45%
- Memory: 8.2 GB / 16 GB
- Disk: 120 GB free

## Active Processes
- code.exe (2 instances)
- chrome.exe (5 tabs)
";

        var decoratedInput = ContextMessageLiveScreenHelper.AttachLiveScreen(input, liveScreen);

        var context = new List<IContextMessage> {
            new SystemInstructionMessage("You are a system monitoring assistant.") {
                Timestamp = DateTimeOffset.UtcNow
            },
            decoratedInput
        };

        var request = new ProviderRequest(
            StrategyId: "anthropic-v1",
            Invocation: new ModelInvocationDescriptor("anthropic", "messages-v1", "claude-3-5-sonnet-20241022"),
            Context: context,
            StubScriptName: null
        );

        Console.WriteLine("=== LiveScreen Injection Example ===");
        await foreach (var delta in client.CallModelAsync(request, CancellationToken.None)) {
            if (delta.Kind == ModelOutputDeltaKind.Content) {
                Console.Write(delta.ContentFragment);
            }
        }
        Console.WriteLine("\n=== End ===");
    }
}
