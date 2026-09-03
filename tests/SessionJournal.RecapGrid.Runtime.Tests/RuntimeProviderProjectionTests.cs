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

        AssertOpenAiChat(required);
        AssertOpenAiResponses(required);
        AssertAnthropic(required);
        AssertGemini(required);
    }

    private static void AssertOpenAiChat(CompletionRequest required) {
        OpenAIChatApiRequest requiredApi = OpenAIChatMessageConverter
            .ConvertToApiRequest(required, OpenAIChatDialects.Strict);
        Assert.Null(requiredApi.Tools);
        Assert.Null(requiredApi.ToolChoice);
        Assert.Null(requiredApi.ParallelToolCalls);
    }

    private static void AssertOpenAiResponses(CompletionRequest required) {
        var options = new OpenAIResponsesClientOptions {
            IncludeEncryptedReasoning = false
        };
        OpenAIResponsesApiRequest requiredApi = OpenAIResponsesMessageConverter
            .ConvertToApiRequest(required, options);
        Assert.Null(requiredApi.Tools);
        Assert.Null(requiredApi.ToolChoice);
    }

    private static void AssertAnthropic(CompletionRequest required) {
        AnthropicApiRequest requiredApi = AnthropicMessageConverter
            .ConvertToApiRequest(
                required,
                modelMaximumTokens: 4_096,
                enablePromptCaching: true
            );
        Assert.Null(requiredApi.Tools);
        Assert.Null(requiredApi.ToolChoice);
        string json = JsonSerializer.Serialize(requiredApi);
        Assert.Contains("cache_control", json, StringComparison.Ordinal);
    }

    private static void AssertGemini(CompletionRequest required) {
        GeminiGenerateContentRequest requiredApi = GeminiMessageConverter
            .ConvertToApiRequest(required, modelMaximumTokens: 4_096);
        Assert.Null(requiredApi.Tools);
        Assert.Null(requiredApi.ToolConfig);
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
