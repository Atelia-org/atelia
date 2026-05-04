using System;
using System.Net.Http;
using System.Text;
using Atelia.Diagnostics;
using Atelia.Agent;
using Atelia.Agent.Core;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;

namespace Atelia.LiveContextProto;

internal static class Program {
    private const string DebugCategory = "History";
    private const string DefaultLocalModelId = "Qwen3.5-27b-GPTQ-Int4";
    // 历史 Anthropic 路径备忘：
    // private const string DefaultModelId = "vscode-lm-proxy";
    // private const string LocalLlmEndpoint = "http://localhost:4000/anthropic/";
    // private const string DefaultProfileName = "anthropic-v1";
    private const string LocalLlmEndpoint = "http://localhost:8000/";
    private const string GoldenLogPathEnvVar = "ATELIA_COMPLETION_GOLDEN_LOG";
    private const string ReplayLogPathEnvVar = "ATELIA_COMPLETION_REPLAY_LOG";

    public static int Main(string[] args) {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        DebugUtil.Info(DebugCategory, "LiveContextProto bootstrap starting");
        var agent = new CharacterAgent();

        // 旧 Anthropic 路径暂保留为参考，待长期稳定后清理：
        // var anthropicClient = new AnthropicClient(apiKey: null, baseAddress: new Uri(LocalLlmEndpoint));
        // var defaultProfile = new LlmProfile(anthropicClient, DefaultModelId, "LocaQwen-anthropic-v1", 64_000u);
        // var loop = new ConsoleTui(agent, defaultProfile);

        HttpClient? ownedHttpClient = null;
        LlmProfile defaultProfile;
        var deepSeekApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(deepSeekApiKey)) {
            var deepSeekV4Client = new DeepSeekV4ChatClient(
                apiKey: deepSeekApiKey,
                baseAddress: new Uri("https://api.deepseek.com/")
            );
            defaultProfile = new LlmProfile(deepSeekV4Client, "deepseek-v4-flash", "Player", 8000u);
            Console.WriteLine("[startup] profile: deepseek-v4-flash (DEEPSEEK_API_KEY detected)");
            DebugUtil.Info(DebugCategory, "LiveContextProto profile=deepseek-v4-flash");
        }
        else {
            var transport = CompletionHttpTransportFactory.CreateFromEnvironmentVariables(
                new Uri(EnsureTrailingSlash(LocalLlmEndpoint)),
                GoldenLogPathEnvVar,
                ReplayLogPathEnvVar
            );
            ownedHttpClient = transport.HttpClient;
            PrintTransportStartup(transport);

            var localClient = new OpenAIChatClient(
                apiKey: null,
                httpClient: ownedHttpClient,
                dialect: OpenAIChatDialects.SgLangCompatible,
                options: OpenAIChatClientOptions.QwenThinkingDisabled()
            );
            defaultProfile = new LlmProfile(localClient, DefaultLocalModelId, "LocaQwen-oai-chat", 8000u);
            Console.WriteLine($"[startup] profile: {DefaultLocalModelId} @ {LocalLlmEndpoint}");
            DebugUtil.Info(DebugCategory, $"LiveContextProto profile={DefaultLocalModelId}");
        }

        using var httpClientLifetime = ownedHttpClient;

        var loop = new ConsoleTui(agent, defaultProfile);

        loop.Run();

        DebugUtil.Info(DebugCategory, "LiveContextProto shutdown");
        return 0;
    }

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith('/') ? value : value + "/";

    private static void PrintTransportStartup(CompletionHttpTransportSetup transport) {
        ArgumentNullException.ThrowIfNull(transport);
        var recordValue = Environment.GetEnvironmentVariable(GoldenLogPathEnvVar) ?? "<unset>";
        var replayValue = Environment.GetEnvironmentVariable(ReplayLogPathEnvVar) ?? "<unset>";

        Console.WriteLine($"[startup] {transport.Describe()}");
        Console.WriteLine($"[startup] env: {GoldenLogPathEnvVar}={recordValue}, {ReplayLogPathEnvVar}={replayValue}");

        DebugUtil.Info(DebugCategory, transport.Describe());
        if (transport.ArtifactPath is not null) {
            DebugUtil.Info(DebugCategory, $"LiveContextProto transport artifact={transport.ArtifactPath}");
        }
    }
}
