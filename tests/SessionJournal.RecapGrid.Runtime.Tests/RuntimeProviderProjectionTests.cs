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
    public async Task RealRuntimeRequests_ProjectExactV1AcrossProviders() {
        FrozenRowBatch requiredBatch = RuntimeTestFixture.Batch(
            columnCount: 2,
            toolChoice: FamilyToolChoice.Required,
            allowParallel: true
        );
        FrozenRowBatch autoBatch = RuntimeTestFixture.Batch(
            toolChoice: FamilyToolChoice.Auto,
            allowParallel: null
        );
        var invoker = new CapturingInvoker();
        RecapCompletionRoute requiredRoute = RuntimeTestFixture.Route(
            requiredBatch,
            invoker
        );
        RecapCompletionRoute autoRoute = RuntimeTestFixture.Route(
            autoBatch,
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
                : key == autoRoute.Key
                    ? new RecapCompletionRouteResolution.Bound(autoRoute)
                    : new RecapCompletionRouteResolution.Unavailable(
                        "RouteMissing",
                        "No exact route."
                    )),
            options
        );

        _ = await runtime.ExecuteAsync(requiredBatch, default);
        _ = await runtime.ExecuteAsync(autoBatch, default);

        CompletionRequest[] captured = invoker.Requests.ToArray();
        Assert.Equal(3, captured.Length);
        CompletionRequest required = captured[0];
        CompletionRequest sibling = captured[1];
        CompletionRequest auto = captured[2];
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
        Assert.True(required.PromptPrefix.OutputContract.AllowParallelToolCalls);
        Assert.Equal(
            CompletionToolChoiceKind.Auto,
            auto.PromptPrefix.OutputContract.ToolChoice.Kind
        );
        Assert.Null(auto.PromptPrefix.OutputContract.AllowParallelToolCalls);

        AssertOpenAiChat(required, auto);
        AssertOpenAiResponses(required, auto);
        AssertAnthropic(required, auto);
        AssertGemini(required, auto);
    }

    private static void AssertOpenAiChat(
        CompletionRequest required,
        CompletionRequest auto
    ) {
        OpenAIChatApiRequest requiredApi = OpenAIChatMessageConverter
            .ConvertToApiRequest(required, OpenAIChatDialects.Strict);
        OpenAIChatApiRequest autoApi = OpenAIChatMessageConverter
            .ConvertToApiRequest(auto, OpenAIChatDialects.Strict);
        Assert.True(requiredApi.ParallelToolCalls);
        Assert.Null(autoApi.ParallelToolCalls);
        Assert.Equal("submit", Assert.IsType<OpenAIChatNamedToolChoice>(
            requiredApi.ToolChoice
        ).Function.Name);
        Assert.Equal("auto", autoApi.ToolChoice);
        AssertNullableContent(
            Assert.Single(requiredApi.Tools!).Function.Parameters,
            gemini: false
        );
    }

    private static void AssertOpenAiResponses(
        CompletionRequest required,
        CompletionRequest auto
    ) {
        var options = new OpenAIResponsesClientOptions {
            IncludeEncryptedReasoning = false
        };
        OpenAIResponsesApiRequest requiredApi = OpenAIResponsesMessageConverter
            .ConvertToApiRequest(required, options);
        OpenAIResponsesApiRequest autoApi = OpenAIResponsesMessageConverter
            .ConvertToApiRequest(auto, options);
        Assert.True(requiredApi.ParallelToolCalls);
        Assert.True(autoApi.ParallelToolCalls);
        Assert.Equal("submit", Assert.IsType<OpenAIResponsesNamedToolChoice>(
            requiredApi.ToolChoice
        ).Name);
        Assert.Equal("auto", autoApi.ToolChoice);
        AssertNullableContent(
            Assert.Single(requiredApi.Tools!).Parameters,
            gemini: false
        );
    }

    private static void AssertAnthropic(
        CompletionRequest required,
        CompletionRequest auto
    ) {
        AnthropicApiRequest requiredApi = AnthropicMessageConverter
            .ConvertToApiRequest(required, enablePromptCaching: true);
        AnthropicApiRequest autoApi = AnthropicMessageConverter
            .ConvertToApiRequest(auto, enablePromptCaching: true);
        Assert.Equal("tool", requiredApi.ToolChoice!.Type);
        Assert.Equal("submit", requiredApi.ToolChoice.Name);
        Assert.False(requiredApi.ToolChoice.DisableParallelToolUse);
        Assert.Equal("auto", autoApi.ToolChoice!.Type);
        Assert.Null(autoApi.ToolChoice.DisableParallelToolUse);
        AssertNullableContent(
            Assert.Single(requiredApi.Tools!).InputSchema,
            gemini: false
        );
        string json = JsonSerializer.Serialize(requiredApi);
        Assert.Contains("cache_control", json, StringComparison.Ordinal);
    }

    private static void AssertGemini(
        CompletionRequest required,
        CompletionRequest auto
    ) {
        using JsonDocument requiredJson = Serialize(
            GeminiMessageConverter.ConvertToApiRequest(required)
        );
        using JsonDocument autoJson = Serialize(
            GeminiMessageConverter.ConvertToApiRequest(auto)
        );
        JsonElement requiredConfig = requiredJson.RootElement
            .GetProperty("toolConfig")
            .GetProperty("functionCallingConfig");
        JsonElement autoConfig = autoJson.RootElement
            .GetProperty("toolConfig")
            .GetProperty("functionCallingConfig");
        Assert.Equal("ANY", requiredConfig.GetProperty("mode").GetString());
        Assert.Equal(
            "submit",
            Assert.Single(requiredConfig.GetProperty("allowedFunctionNames")
                .EnumerateArray()).GetString()
        );
        Assert.Equal("AUTO", autoConfig.GetProperty("mode").GetString());
        JsonElement declaration = Assert.Single(requiredJson.RootElement
            .GetProperty("tools")[0]
            .GetProperty("functionDeclarations")
            .EnumerateArray());
        AssertNullableContent(
            declaration.GetProperty("parameters"),
            gemini: true
        );
    }

    private static void AssertNullableContent(
        JsonElement schema,
        bool gemini
    ) {
        JsonElement content = schema.GetProperty("properties")
            .GetProperty("content");
        if (gemini) {
            Assert.Equal("STRING", content.GetProperty("type").GetString());
            Assert.True(content.GetProperty("nullable").GetBoolean());
            return;
        }
        Assert.Equal(
            ["string", "null"],
            content.GetProperty("type").EnumerateArray()
                .Select(static value => value.GetString())
        );
    }

    private static JsonDocument Serialize<T>(T value) => JsonDocument.Parse(
        JsonSerializer.Serialize(value)
    );

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
