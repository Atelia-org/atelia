using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.SessionJournal.MemoPod.DebugApp;

namespace Atelia.SessionJournal.MemoPod.Tests.Live;

public sealed class LiveMemoRecallCompositionTests {
    [Fact]
    public async Task ProviderFreeSliceUsesDeepSeekWireUsageAndHydration() {
        using var host = new LiveMemoRecallTestHost();
        await host.CreateFrozenPodAsync(
            "synthetic customer details",
            "shipment 17 leaves Friday",
            "invoice 22 is settled"
        );
        string queryPath = host.WriteText("find shipment details");
        string secretEnvironment =
            "ATELIA_MEMOPOD_LIVE_TEST_KEY_"
            + Guid.NewGuid().ToString("N").ToUpperInvariant();
        const string secretCanary = "SECRET_LIVE_CANARY_DO_NOT_PRINT";
        Environment.SetEnvironmentVariable(
            secretEnvironment,
            secretCanary
        );
        try {
            string connectionsPath = host.WriteConnections(
                secretEnvironment
            );
            var handler = new CapturingSseHandler(
                """
                data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"recall_memos","arguments":"{\"memoIds\":[\"m1:00000001\"]}"}}]},"finish_reason":"tool_calls"}],"usage":null}

                data: {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":7,"prompt_cache_hit_tokens":80,"prompt_cache_miss_tokens":20}}

                data: [DONE]

                """
            );
            var factory = new CountingCompletionClientFactory(connection => {
                var httpClient = new HttpClient(handler) {
                    BaseAddress = new Uri(connection.BaseAddress)
                };
                var client = new DeepSeekV4ChatClient(
                    connection.ApiKey,
                    httpClient,
                    new OpenAIChatClientOptions {
                        ReasoningEffort = connection.ReasoningEffort
                    }
                );
                return new OwnedTestCompletionClient(client, httpClient);
            });
            var services = new LiveMemoRecallServices(
                static () => { },
                CompletionConnectionConfigLoader.LoadFile,
                () => factory
            );

            LiveOperatorResult result = await host.RunAsync(
                LiveArguments(
                    host,
                    connectionsPath,
                    queryPath,
                    "wire-usage"
                ),
                services
            );

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StandardError);
            Assert.DoesNotContain(
                secretCanary,
                result.StandardOutput,
                StringComparison.Ordinal
            );
            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(HttpMethod.Post, Assert.Single(handler.RequestMethods));
            Assert.Equal(
                new Uri("https://api.deepseek.com/v1/chat/completions"),
                Assert.Single(handler.RequestUris)
            );

            using JsonDocument request = JsonDocument.Parse(
                Assert.Single(handler.RequestBodies)
            );
            JsonElement root = request.RootElement;
            Assert.Equal(
                "deepseek-v4-flash",
                root.GetProperty("model").GetString()
            );
            Assert.Equal(
                "disabled",
                root.GetProperty("thinking")
                    .GetProperty("type").GetString()
            );
            Assert.True(
                root.GetProperty("stream_options")
                    .GetProperty("include_usage").GetBoolean()
            );
            Assert.False(
                root.GetProperty("parallel_tool_calls").GetBoolean()
            );
            JsonElement toolChoice = root.GetProperty("tool_choice");
            Assert.Equal(
                "function",
                toolChoice.GetProperty("type").GetString()
            );
            Assert.Equal(
                "recall_memos",
                toolChoice.GetProperty("function")
                    .GetProperty("name").GetString()
            );

            using JsonDocument evidence = JsonDocument.Parse(
                result.StandardOutput
            );
            JsonElement item = evidence.RootElement;
            Assert.Equal(
                LiveMemoRecallEvidenceSerializer.Schema,
                item.GetProperty("schema").GetString()
            );
            Assert.Equal(
                LiveMemoRecallEvidenceSerializer.FrozenPromptFormatId,
                item.GetProperty("frozenPromptFormatId").GetString()
            );
            Assert.Equal("completed", item.GetProperty("outcome").GetString());
            Assert.Equal(20, item.GetProperty("uncachedInputTokens").GetInt64());
            Assert.Equal(80, item.GetProperty("cacheReadInputTokens").GetInt64());
            Assert.Equal(7, item.GetProperty("outputTokens").GetInt64());
            Assert.Equal(
                "requested",
                item.GetProperty("promptCacheRequestStatus").GetString()
            );
            Assert.Equal(
                "unknown",
                item.GetProperty("promptCacheSupportStatus").GetString()
            );
            Assert.Equal(
                "partial",
                item.GetProperty("promptCacheObservationStatus")
                    .GetString()
            );
            Assert.Equal(1, item.GetProperty("selectedCount").GetInt32());
            Assert.Equal(
                "m1:00000001",
                Assert.Single(
                    item.GetProperty("selectedIds")
                        .EnumerateArray()
                ).GetString()
            );
            string frozenPrompt = root.GetProperty("messages")[1]
                .GetProperty("content").GetString()!;
            Assert.StartsWith(
                "{\"schema\":\""
                + LiveMemoRecallEvidenceSerializer.FrozenPromptFormatId
                + "\",\"pod_id\":\"",
                frozenPrompt,
                StringComparison.Ordinal
            );
            Assert.Equal(
                item.GetProperty("frozenPromptSha256").GetString(),
                Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(frozenPrompt)
                    )
                )
            );
            Assert.Equal(
                System.Text.Encoding.UTF8.GetByteCount(frozenPrompt),
                item.GetProperty("frozenPromptUtf8Bytes").GetInt32()
            );
        }
        finally {
            Environment.SetEnvironmentVariable(secretEnvironment, null);
        }
    }

    private static string[] LiveArguments(
        LiveMemoRecallTestHost host,
        string connectionsPath,
        string queryPath,
        string caseLabel
    ) => [
        "recall",
        "--live", "true",
        "--root", host.StoreRoot,
        "--pod", LiveMemoRecallTestHost.PodIdText,
        "--connections", connectionsPath,
        "--connection", "candidate",
        "--case", caseLabel,
        "--query-file", queryPath,
        "--max-prompt-bytes", "33554432",
        "--max-tokens", "256",
        "--delay-ms", "0"
    ];
}
