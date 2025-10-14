using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Provider.Anthropic.Examples;

/// <summary>
/// Anthropic Provider 集成示例。
/// </summary>
internal static class AnthropicIntegrationExample {
    private const string ProxyStrategyId = "anthropic-v1";
    private const string DefaultProxyAnthropicUrl = "http://localhost:4000/anthropic/";
    private const string DefaultProxySpecification = "messages-v1";
    private const string DefaultProxyProviderId = "llm-proxy";
    private const string DefaultProxyModel = "vscode-lm-proxy";

    /// <summary>
    /// 演示基础的对话调用。
    /// </summary>
    public static async Task SimpleConversationExample() {
        // 1. 初始化客户端
        var client = CreateProxyClient();

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
        var request = CreateProviderRequest(context);

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
        var client = CreateProxyClient();

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

        var request = CreateProviderRequest(context);

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

            var followUpRequest = CreateProviderRequest(context);

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
        var client = CreateProxyClient();

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

        var request = CreateProviderRequest(context);

        Console.WriteLine("=== LiveScreen Injection Example ===");
        await foreach (var delta in client.CallModelAsync(request, CancellationToken.None)) {
            if (delta.Kind == ModelOutputDeltaKind.Content) {
                Console.Write(delta.ContentFragment);
            }
        }
        Console.WriteLine("\n=== End ===");
    }

    private static AnthropicProviderClient CreateProxyClient() {
        var endpoint = ResolveProxyEndpoint();
        var httpClient = new HttpClient { BaseAddress = endpoint };
        return new AnthropicProviderClient(apiKey: null, httpClient: httpClient);
    }

    private static ProviderRequest CreateProviderRequest(List<IContextMessage> context)
        => new(
            StrategyId: ProxyStrategyId,
            Invocation: new ModelInvocationDescriptor(
                ResolveProviderId(),
                ResolveSpecification(),
                ResolveModel()
            ),
            Context: context,
            StubScriptName: null
        );

    private static Uri ResolveProxyEndpoint() {
        var anthropicUrl = Environment.GetEnvironmentVariable("LLM_PROXY_ANTHROPIC_URL");
        if (!string.IsNullOrWhiteSpace(anthropicUrl) && Uri.TryCreate(EnsureTrailingSlash(anthropicUrl), UriKind.Absolute, out var explicitUri)) { return explicitUri; }

        var proxyBaseUrl = Environment.GetEnvironmentVariable("LLM_PROXY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(proxyBaseUrl) && Uri.TryCreate(EnsureTrailingSlash(proxyBaseUrl), UriKind.Absolute, out var proxyBase)) { return new Uri(proxyBase, "anthropic/"); }

        return new Uri(DefaultProxyAnthropicUrl);
    }

    private static string ResolveSpecification()
        => Environment.GetEnvironmentVariable("LLM_PROXY_SPEC") ?? DefaultProxySpecification;

    private static string ResolveProviderId()
        => Environment.GetEnvironmentVariable("LLM_PROXY_PROVIDER_ID") ?? DefaultProxyProviderId;

    private static string ResolveModel()
        => Environment.GetEnvironmentVariable("LLM_PROXY_MODEL") ?? DefaultProxyModel;

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith('/') ? value : value + "/";
}
