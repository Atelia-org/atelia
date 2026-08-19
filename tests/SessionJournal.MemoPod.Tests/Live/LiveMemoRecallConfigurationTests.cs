using System.Net.Http;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.MemoPod.DebugApp;

namespace Atelia.SessionJournal.MemoPod.Tests.Live;

public sealed class LiveMemoRecallConfigurationTests {
    [Fact]
    public void RoutePolicyRequiresExactDeepSeekCandidate() {
        LiveMemoRecallRoutePolicy.Validate(ValidConnection());

        CompletionConnectionConfig[] invalid = [
            ValidConnection() with { Kind = "OpenAI-Chat" },
            ValidConnection() with { ModelId = "deepseek-v4" },
            ValidConnection() with {
                CompletionSurfaceId = "openai-chat/strict"
            },
            ValidConnection() with {
                ReasoningEffort = CompletionReasoningEffort.ProviderDefault
            },
            ValidConnection() with { ApiKeyEnv = null },
            ValidConnection() with { ApiKeyEnv = " " },
            ValidConnection() with { ApiKey = null },
            ValidConnection() with { ApiKey = " " },
            ValidConnection() with {
                BaseAddress = "http://api.deepseek.com/"
            },
            ValidConnection() with {
                BaseAddress = "https://user@api.deepseek.com/"
            },
            ValidConnection() with {
                BaseAddress = "https://api.deepseek.com/?canary=1"
            },
            ValidConnection() with {
                BaseAddress = "https://api.deepseek.com/#canary"
            },
            ValidConnection() with {
                BaseAddress = "https://api.deepseek.com/v1/"
            },
            ValidConnection() with {
                BaseAddress = "https://api.deepseek.com:444/"
            },
            ValidConnection() with {
                BaseAddress = "https://endpoint-canary.invalid/"
            }
        ];

        foreach (CompletionConnectionConfig connection in invalid) {
            Assert.Throws<LiveMemoRecallConfigurationException>(
                () => LiveMemoRecallRoutePolicy.Validate(connection)
            );
        }
    }

    [Fact]
    public async Task UnknownExactConnectionNeverFallsBackOrConstructsClient() {
        using var host = new LiveMemoRecallTestHost();
        await host.CreateFrozenPodAsync(memos: ["synthetic memo"]);
        string environmentVariable = UniqueEnvironmentName();
        Environment.SetEnvironmentVariable(environmentVariable, "test-key");
        try {
            string connectionsPath = host.WriteConnections(
                environmentVariable,
                connectionId: "default-route",
                defaultConnectionId: "default-route"
            );
            var factory = new CountingCompletionClientFactory(
                static _ => new ScriptedLiveCompletionClient()
            );
            var services = Services(
                CompletionConnectionConfigLoader.LoadFile,
                factory
            );

            LiveOperatorResult result = await host.RunAsync(
                Arguments(
                    host,
                    connectionsPath,
                    connectionId: "missing-route",
                    queryPaths: [host.WriteText("query")]
                ),
                services
            );

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Equal("error=live-config\n", result.StandardError);
            Assert.Equal(0, factory.CreateCount);
        }
        finally {
            Environment.SetEnvironmentVariable(environmentVariable, null);
        }
    }

    [Fact]
    public async Task InvalidRouteIsRejectedBeforeClientConstruction() {
        using var host = new LiveMemoRecallTestHost();
        await host.CreateFrozenPodAsync(memos: ["synthetic memo"]);
        const string endpointCanary = "endpoint-canary.invalid";
        var factory = new CountingCompletionClientFactory(
            static _ => new ScriptedLiveCompletionClient()
        );
        CompletionConnectionsFileConfig config = Config(
            ValidConnection() with {
                BaseAddress = $"https://{endpointCanary}/"
            }
        );
        var services = Services(_ => config, factory);

        LiveOperatorResult result = await host.RunAsync(
            Arguments(
                host,
                "config-path-canary",
                "candidate",
                [host.WriteText("query")]
            ),
            services
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("error=live-config\n", result.StandardError);
        Assert.DoesNotContain(
            endpointCanary,
            result.StandardError,
            StringComparison.Ordinal
        );
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task MissingPodIsRejectedBeforeSecretBearingClientConstruction() {
        using var host = new LiveMemoRecallTestHost();
        var factory = new CountingCompletionClientFactory(
            static _ => new ScriptedLiveCompletionClient()
        );
        var services = Services(_ => Config(ValidConnection()), factory);

        LiveOperatorResult result = await host.RunAsync(
            Arguments(
                host,
                "unused-connections",
                "candidate",
                [host.WriteText("query")]
            ),
            services
        );

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task FakeRecallDoesNotTouchLiveGateConfigOrFactory() {
        using var host = new LiveMemoRecallTestHost();
        await host.CreateFrozenPodAsync(memos: ["synthetic memo"]);
        int touches = 0;
        var services = new LiveMemoRecallServices(
            () => touches++,
            _ => {
                touches++;
                throw new InvalidOperationException("must not load");
            },
            () => {
                touches++;
                throw new InvalidOperationException("must not create");
            }
        );

        LiveOperatorResult result = await host.RunAsync([
            "recall",
            "--root", host.StoreRoot,
            "--pod", LiveMemoRecallTestHost.PodIdText,
            "--query-file", host.WriteText("query"),
            "--fake-return-id", "m1:00000001"
        ], services);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Equal(0, touches);
        using JsonDocument report = JsonDocument.Parse(
            result.StandardOutput
        );
        Assert.Equal(
            "recall",
            report.RootElement.GetProperty("command").GetString()
        );
    }

    [Fact]
    public async Task LiveCliBoundsAndModeSeparationFailBeforeServices() {
        using var host = new LiveMemoRecallTestHost();
        await host.CreateFrozenPodAsync(memos: ["synthetic memo"]);
        string query = host.WriteText("query");
        string[] baseline = Arguments(
            host,
            "unused-connections",
            "candidate",
            [query]
        );
        int serviceTouches = 0;
        var services = new LiveMemoRecallServices(
            () => serviceTouches++,
            _ => throw new InvalidOperationException(),
            () => throw new InvalidOperationException()
        );
        var invalidCases = new List<string[]> {
            RemoveOption(baseline, "--query-file"),
            ReplaceOption(baseline, "--live", "false"),
            ReplaceOption(baseline, "--case", "Uppercase"),
            ReplaceOption(baseline, "--case", new string('a', 65)),
            AddOption(baseline, "--fake-return-id", "m1:00000001"),
            AddOption(baseline, "--max-prompt-bytes", "0"),
            AddOption(baseline, "--max-prompt-bytes", "33554433"),
            AddOption(baseline, "--max-tokens", "0"),
            AddOption(baseline, "--max-tokens", "4097"),
            AddOption(baseline, "--delay-ms", "-1"),
            AddOption(baseline, "--delay-ms", "30001")
        };
        string[] nineQueries = baseline;
        for (int index = 1; index < 9; index++) {
            nineQueries = AddOption(
                nineQueries,
                "--query-file",
                query
            );
        }
        invalidCases.Add(nineQueries);

        foreach (string[] args in invalidCases) {
            LiveOperatorResult result = await host.RunAsync(
                args,
                services
            );
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Equal("error=syntax\n", result.StandardError);
        }
        Assert.Equal(0, serviceTouches);
    }

    [Fact]
    public async Task EightQueriesProduceEightExplicitCallsAndEvidenceLines() {
        using var host = new LiveMemoRecallTestHost();
        await host.CreateFrozenPodAsync(memos: ["synthetic memo"]);
        var client = new ScriptedLiveCompletionClient();
        var factory = new CountingCompletionClientFactory(_ => client);
        var services = Services(_ => Config(ValidConnection()), factory);
        string[] queryPaths = Enumerable.Range(1, 8)
            .Select(index => host.WriteText($"query-{index}"))
            .ToArray();

        LiveOperatorResult result = await host.RunAsync(
            Arguments(
                host,
                "unused-connections",
                "candidate",
                queryPaths,
                delayMilliseconds: 0
            ),
            services
        );

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(8, client.InvocationCount);
        string[] lines = result.StandardOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries
        );
        Assert.Equal(8, lines.Length);
        for (int index = 0; index < lines.Length; index++) {
            using JsonDocument evidence = JsonDocument.Parse(lines[index]);
            Assert.Equal(
                index + 1,
                evidence.RootElement.GetProperty("callIndex").GetInt32()
            );
            Assert.Equal(
                "completed",
                evidence.RootElement.GetProperty("outcome").GetString()
            );
        }
    }

    [Fact]
    public async Task ProviderFailureEvidenceAndDiagnosticsStayContentFree() {
        using var host = new LiveMemoRecallTestHost();
        await host.CreateFrozenPodAsync(memos: ["synthetic memo"]);
        const string secretCanary = "SECRET_PROVIDER_EXCEPTION_CANARY";
        var client = new ScriptedLiveCompletionClient(
            failure: new HttpRequestException(
                $"provider failed with {secretCanary}"
            )
        );
        var factory = new CountingCompletionClientFactory(_ => client);
        var services = Services(_ => Config(
            ValidConnection() with { ApiKey = secretCanary }
        ), factory);

        LiveOperatorResult result = await host.RunAsync(
            Arguments(
                host,
                "config-path-canary",
                "candidate",
                [host.WriteText("query-content-canary")]
            ),
            services
        );

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("error=recall-provider\n", result.StandardError);
        Assert.DoesNotContain(secretCanary, result.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(secretCanary, result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("query-content-canary", result.StandardOutput,
            StringComparison.Ordinal);
        using JsonDocument evidence = JsonDocument.Parse(
            result.StandardOutput
        );
        JsonElement item = evidence.RootElement;
        Assert.Equal("failed", item.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null,
            item.GetProperty("uncachedInputTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            item.GetProperty("selectedCount").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            item.GetProperty("selectedIds").ValueKind);
    }

    [Fact]
    public void EvidenceSerializerPreservesNullsAndHasExactContentFreeShape() {
        var evidence = new LiveMemoRecallEvidence(
            LiveMemoRecallEvidenceSerializer.Schema,
            "case-1",
            1,
            "candidate",
            "openai-chat",
            "deepseek-v4-flash",
            "openai-chat/deepseek-v4",
            "api.deepseek.com",
            "openai-chat-v1",
            LiveMemoRecallTestHost.PodIdText,
            2,
            LiveMemoRecallEvidenceSerializer.FrozenPromptFormatId,
            new string('a', 64),
            123,
            7,
            64,
            1024,
            256,
            0,
            42,
            "failed",
            "requested",
            "unknown",
            "unavailable",
            null,
            null,
            null,
            null,
            null,
            null
        );

        string json = LiveMemoRecallEvidenceSerializer.Serialize(evidence);
        using JsonDocument document = JsonDocument.Parse(json);
        string[] actualNames = document.RootElement
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        Assert.Equal([
            "schema", "caseLabel", "callIndex", "connectionId", "kind",
            "modelId", "completionSurfaceId", "clientName", "apiSpecId",
            "podId", "activeMemoCount", "frozenPromptFormatId",
            "frozenPromptSha256",
            "frozenPromptUtf8Bytes", "queryUtf8Bytes", "maxResults",
            "maxPromptUtf8Bytes", "maxTokens", "delayMilliseconds",
            "elapsedMilliseconds", "outcome", "promptCacheRequestStatus",
            "promptCacheSupportStatus", "promptCacheObservationStatus",
            "uncachedInputTokens", "cacheCreationInputTokens",
            "cacheReadInputTokens", "outputTokens", "selectedCount",
            "selectedIds"
        ], actualNames);
        Assert.Equal(JsonValueKind.Null,
            document.RootElement.GetProperty("uncachedInputTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            document.RootElement.GetProperty("selectedIds").ValueKind);
        Assert.DoesNotContain("topic", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query-content", json,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exact_text", json,
            StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() =>
            LiveMemoRecallEvidenceSerializer.Serialize(
                evidence with { Outcome = "Completed" }
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            LiveMemoRecallEvidenceSerializer.Serialize(
                evidence with { FrozenPromptFormatId = "future-format" }
            )
        );
    }

    private static CompletionConnectionConfig ValidConnection() => new(
        "candidate",
        "openai-chat",
        "deepseek-v4-flash",
        "openai-chat/deepseek-v4",
        "https://api.deepseek.com/",
        ApiKey: "resolved-test-key",
        ApiKeyEnv: "DEEPSEEK_API_KEY",
        ReasoningEffort: CompletionReasoningEffort.Disabled
    );

    private static CompletionConnectionsFileConfig Config(
        CompletionConnectionConfig connection
    ) => new([connection], connection.Id);

    private static LiveMemoRecallServices Services(
        Func<string, CompletionConnectionsFileConfig> loader,
        ICompletionClientFactory factory
    ) => new(
        static () => { },
        loader,
        () => factory
    );

    private static string[] Arguments(
        LiveMemoRecallTestHost host,
        string connectionsPath,
        string connectionId,
        IReadOnlyList<string> queryPaths,
        int? delayMilliseconds = null
    ) {
        var args = new List<string> {
            "recall",
            "--live", "true",
            "--root", host.StoreRoot,
            "--pod", LiveMemoRecallTestHost.PodIdText,
            "--connections", connectionsPath,
            "--connection", connectionId,
            "--case", "test-case"
        };
        foreach (string queryPath in queryPaths) {
            args.Add("--query-file");
            args.Add(queryPath);
        }
        if (delayMilliseconds is not null) {
            args.Add("--delay-ms");
            args.Add(delayMilliseconds.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ));
        }
        return args.ToArray();
    }

    private static string UniqueEnvironmentName()
        => "ATELIA_MEMOPOD_CONFIG_TEST_"
            + Guid.NewGuid().ToString("N").ToUpperInvariant();

    private static string[] AddOption(
        IReadOnlyList<string> source,
        string option,
        string value
    ) => [.. source, option, value];

    private static string[] ReplaceOption(
        IReadOnlyList<string> source,
        string option,
        string value
    ) {
        string[] result = source.ToArray();
        int index = Array.IndexOf(result, option);
        Assert.True(index >= 0);
        result[index + 1] = value;
        return result;
    }

    private static string[] RemoveOption(
        IReadOnlyList<string> source,
        string option
    ) {
        var result = source.ToList();
        int index = result.IndexOf(option);
        Assert.True(index >= 0);
        result.RemoveRange(index, 2);
        return result.ToArray();
    }
}
