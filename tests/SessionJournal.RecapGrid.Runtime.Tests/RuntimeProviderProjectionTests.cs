using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.Gemini;
using Atelia.Completion.OpenAI;
using Atelia.SessionJournal.RecapGrid.Manager;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Runtime.Tests;

public sealed class RuntimeProviderProjectionTests {
    [Fact]
    public async Task RealRuntimeRequests_ProjectExactV3WithoutTools() {
        FrozenRowBatch requiredBatch = RuntimeTestFixture.Batch(columnCount: 2);
        var invoker = new CapturingInvoker();
        RecapCompletionRoute requiredRoute = RuntimeTestFixture.Route(
            requiredBatch,
            invoker
        );
        var options = new RecapCompletionRuntimeOptions(
            new CompletionInvocationOptions {
                PromptCacheReuseHint = PromptCacheReuseHint.ReuseExpectedSoon
            }
        );
        await using var runtime = new RecapCompletionRuntime(
            new ScriptedResolver(key => key == requiredRoute.Key
                ? new RecapCompletionRouteResolution.Bound(requiredRoute)
                : new RecapCompletionRouteResolution.Unavailable(
                    "RouteMissing",
                    "No exact route."
                )),
            options
        );

        _ = await runtime.ExecuteAsync(requiredBatch, default);
        CompletionRequest[] captured = invoker.Requests.ToArray();
        Assert.Equal(2, captured.Length);
        CompletionRequest required = captured[0];
        CompletionRequest sibling = captured[1];
        Assert.Same(required.PromptPrefix, sibling.PromptPrefix);
        Assert.NotEqual(
            ((ObservationMessage)required.TailMessages.Single()).Content,
            ((ObservationMessage)sibling.TailMessages.Single()).Content
        );
        Assert.All(invoker.Options, static value => Assert.Equal(
            PromptCacheReuseHint.ReuseExpectedSoon,
            value.PromptCacheReuseHint
        ));
        Assert.Equal(
            CompletionToolChoiceKind.ProviderDefault,
            required.PromptPrefix.OutputContract.ToolChoice.Kind
        );
        Assert.Empty(required.PromptPrefix.OutputContract.Tools);
        Assert.Null(required.PromptPrefix.OutputContract.AllowParallelToolCalls);

        await AssertOpenAiChatAsync(required, options.InvocationOptions);
        await AssertOpenAiResponsesAsync(required, options.InvocationOptions);
        await AssertAnthropicAsync(required, options.InvocationOptions);
        await AssertGeminiAsync(required, options.InvocationOptions);
    }

    private static async Task AssertOpenAiChatAsync(
        CompletionRequest required,
        CompletionInvocationOptions invocationOptions
    ) {
        JsonElement wire = await CaptureWireAsync(
            required,
            invocationOptions,
            static http => new OpenAIChatClient(null, http, OpenAIChatDialects.Strict),
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
        );
        Assert.False(wire.TryGetProperty("tools", out _));
        Assert.False(wire.TryGetProperty("tool_choice", out _));
        Assert.False(wire.TryGetProperty("parallel_tool_calls", out _));
    }

    private static async Task AssertOpenAiResponsesAsync(
        CompletionRequest required,
        CompletionInvocationOptions invocationOptions
    ) {
        JsonElement wire = await CaptureWireAsync(
            required,
            invocationOptions,
            static http => new OpenAIResponsesClient(null, http, new OpenAIResponsesClientOptions {
                IncludeEncryptedReasoning = false
            }),
            "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n"
        );
        Assert.False(wire.TryGetProperty("tools", out _));
        Assert.False(wire.TryGetProperty("tool_choice", out _));
    }

    private static async Task AssertAnthropicAsync(
        CompletionRequest required,
        CompletionInvocationOptions invocationOptions
    ) {
        JsonElement wire = await CaptureWireAsync(
            required,
            invocationOptions,
            static http => new AnthropicClient(null, http, enablePromptCaching: true),
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{}}\n\n"
                + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
            "{\"max_tokens\":4096}"
        );
        Assert.False(wire.TryGetProperty("tools", out _));
        Assert.False(wire.TryGetProperty("tool_choice", out _));
        Assert.Contains("cache_control", wire.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task AssertGeminiAsync(
        CompletionRequest required,
        CompletionInvocationOptions invocationOptions
    ) {
        JsonElement wire = await CaptureWireAsync(
            required,
            invocationOptions,
            static http => new GeminiClient(null, http),
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[]},\"finishReason\":\"STOP\"}]}\n\n",
            "{\"outputTokenLimit\":4096}"
        );
        Assert.False(wire.TryGetProperty("tools", out _));
        Assert.False(wire.TryGetProperty("toolConfig", out _));
    }

    // Exercise the shipped public clients with the real Runtime request. Only
    // provider HTTP responses are controlled; this is an offline wire contract test.
    private static async Task<JsonElement> CaptureWireAsync(
        CompletionRequest required,
        CompletionInvocationOptions invocationOptions,
        Func<HttpClient, ICompletionClient> createClient,
        string responseBody,
        string? modelMetadata = null
    ) {
        using var handler = new CapturingHttpHandler(responseBody, modelMetadata);
        using var http = new HttpClient(handler) {
            BaseAddress = new Uri("https://provider.invalid/")
        };
        ICompletionClient client = createClient(http);
        CompletionResult result = await client.StreamCompletionAsync(
            required, invocationOptions, observer: null, CancellationToken.None
        );
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(handler.PostBodies));
        return document.RootElement.Clone();
    }

    private sealed class CapturingHttpHandler(
        string responseBody,
        string? modelMetadata
    ) : HttpMessageHandler {
        internal List<string> PostBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            if (request.Method == HttpMethod.Get) {
                Assert.NotNull(modelMetadata);
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(modelMetadata, Encoding.UTF8, "application/json")
                };
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.NotNull(request.Content);
            PostBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class CapturingInvoker : IRecapCompletionInvoker {
        internal Queue<CompletionRequest> Requests { get; } = [];
        internal Queue<CompletionInvocationOptions> Options { get; } = [];

        public string ProviderId => "test-provider";
        public string ApiSpecId => "test-api-v1";

        public ValueTask<CompletionResult> InvokeAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CancellationToken cancellationToken
        ) {
            _ = cancellationToken;
            Requests.Enqueue(request);
            Options.Enqueue(invocationOptions);
            return ValueTask.FromResult(RuntimeTestFixture.Updated(
                request,
                this
            ));
        }
    }
}
