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
    public async Task RealRuntimeRequests_ProjectOrRejectExactV1AcrossProviders() {
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
            CompletionToolChoiceKind.RequiredNamed,
            required.PromptPrefix.OutputContract.ToolChoice.Kind
        );
        Assert.False(required.PromptPrefix.OutputContract.AllowParallelToolCalls);

        AssertOpenAiChat(required);
        AssertOpenAiResponses(required);
        AssertAnthropic(required);
        AssertGemini(required);
    }

    private static void AssertOpenAiChat(CompletionRequest required) {
        OpenAIChatApiRequest requiredApi = OpenAIChatMessageConverter
            .ConvertToApiRequest(required, OpenAIChatDialects.Strict);
        Assert.False(requiredApi.ParallelToolCalls);
        Assert.Equal("submit", Assert.IsType<OpenAIChatNamedToolChoice>(
            requiredApi.ToolChoice
        ).Function.Name);
        AssertNullableContent(
            Assert.Single(requiredApi.Tools!).Function.Parameters
        );
    }

    private static void AssertOpenAiResponses(CompletionRequest required) {
        var options = new OpenAIResponsesClientOptions {
            IncludeEncryptedReasoning = false
        };
        OpenAIResponsesApiRequest requiredApi = OpenAIResponsesMessageConverter
            .ConvertToApiRequest(required, options);
        Assert.False(requiredApi.ParallelToolCalls);
        Assert.Equal("submit", Assert.IsType<OpenAIResponsesNamedToolChoice>(
            requiredApi.ToolChoice
        ).Name);
        AssertNullableContent(
            Assert.Single(requiredApi.Tools!).Parameters
        );
    }

    private static void AssertAnthropic(CompletionRequest required) {
        AnthropicApiRequest requiredApi = AnthropicMessageConverter
            .ConvertToApiRequest(required, enablePromptCaching: true);
        Assert.Equal("tool", requiredApi.ToolChoice!.Type);
        Assert.Equal("submit", requiredApi.ToolChoice.Name);
        Assert.True(requiredApi.ToolChoice.DisableParallelToolUse);
        AssertNullableContent(
            Assert.Single(requiredApi.Tools!).InputSchema
        );
        string json = JsonSerializer.Serialize(requiredApi);
        Assert.Contains("cache_control", json, StringComparison.Ordinal);
    }

    private static void AssertGemini(CompletionRequest required) {
        _ = Assert.Throws<NotSupportedException>(() =>
            GeminiMessageConverter.ConvertToApiRequest(required)
        );
    }

    private static void AssertNullableContent(JsonElement schema) {
        JsonElement content = schema.GetProperty("properties")
            .GetProperty("content");
        Assert.Equal(
            ["string", "null"],
            content.GetProperty("type").EnumerateArray()
                .Select(static value => value.GetString())
        );
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
