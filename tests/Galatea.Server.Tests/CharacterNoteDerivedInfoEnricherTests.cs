using System.Security;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteDerivedInfoEnricherTests {
    [Fact]
    public async Task RequestUsesCanonicalTargetAndOneNestedBatchTool() {
        ScriptedClient client = CallsClient(BatchCall("""
{"items":[{"artifactOrdinal":0,"title":"钟后的钥匙","gist":"钟后藏着一把钥匙。","summary":"角色记下钥匙藏在钟后，供之后取用。"},{"artifactOrdinal":2,"title":"雨夜北门","gist":"北门只在雨夜开启。","summary":"角色记下北门的开启条件是雨夜。"}]}
"""));
        CharacterNoteDerivedInfoEnricher enricher = CreateEnricher(
            client,
            "莉亚"
        );
        var request = new CharacterNoteDerivedInfoEnrichmentRequest(
            "观察 <raw>",
            "[莉亚]\n她说：\"记下。\"",
            [
                new(0, "钥匙在钟后。"),
                new(2, "北门只在雨夜开启。"),
            ]
        );

        _ = await enricher.EnrichAsync(
            request,
            CancellationToken.None
        );

        const string expectedTarget =
            "{\"schema\":\"atelia.galatea.character-note-derived-info-target.v1\","
            + "\"observationContent\":\"观察 <raw>\","
            + "\"visibleActionText\":\"[莉亚]\\n她说：\\\"记下。\\\"\","
            + "\"targets\":["
            + "{\"artifactOrdinal\":0,\"exactText\":\"钥匙在钟后。\"},"
            + "{\"artifactOrdinal\":2,\"exactText\":\"北门只在雨夜开启。\"}]}";
        Assert.Equal(
            expectedTarget,
            CharacterNoteDerivedInfoTargetRenderer.Render(request)
        );

        CompletionRequest completionRequest = Assert.IsType<
            CompletionRequest>(client.LastRequest);
        Assert.Equal("derived-model", completionRequest.ModelId);
        Assert.Empty(
            completionRequest.PromptPrefix.SharedContextMessages
        );
        Assert.Contains(
            "long-term Notes belonging to 莉亚",
            completionRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "one-sentence impression",
            completionRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "do not distort useful text merely to satisfy punctuation heuristics",
            completionRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Equal(
            CompletionToolChoiceKind.Auto,
            completionRequest.PromptPrefix.OutputContract
                .ToolChoice.Kind
        );
        Assert.True(
            completionRequest.PromptPrefix.OutputContract
                .AllowParallelToolCalls
        );

        ToolDefinition definition = Assert.Single(
            completionRequest.PromptPrefix.OutputContract.Tools
        );
        Assert.Equal(
            CharacterNoteDerivedInfoEnricher.ToolName,
            definition.Name
        );
        ToolSchema.Object root = Assert.IsType<ToolSchema.Object>(
            definition.InputSchema
        );
        ToolSchema.Property items = Assert.Single(root.Properties);
        Assert.Equal("items", items.Name);
        Assert.True(items.IsRequired);
        ToolSchema.Array itemsArray = Assert.IsType<ToolSchema.Array>(
            items.Schema
        );
        ToolSchema.Object item = Assert.IsType<ToolSchema.Object>(
            itemsArray.ItemSchema
        );
        Assert.Equal(
            ["artifactOrdinal", "title", "gist", "summary"],
            item.Properties.Select(static property => property.Name)
        );
        Assert.All(item.Properties, static property =>
            Assert.True(property.IsRequired)
        );

        ObservationMessage envelope = Assert.IsType<ObservationMessage>(
            Assert.Single(completionRequest.TailMessages)
        );
        Assert.Contains(
            SecurityElement.Escape(expectedTarget),
            envelope.Content,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "every 莉亚 Note target",
            envelope.Content,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task MultipleMemosReturnCompleteOrderedImmutableBatch() {
        ScriptedClient client = CallsClient(BatchCall("""
{"items":[{"artifactOrdinal":1,"title":"First","gist":"First gist.","summary":"First main idea."},{"artifactOrdinal":4,"title":"Second","gist":"Second gist.","summary":"Second main idea."}]}
"""));
        CharacterNoteDerivedInfoEnricher enricher = CreateEnricher(client);

        IReadOnlyList<CharacterNoteDerivedInfo> result =
            await enricher.EnrichAsync(
                Request((1, "first exact"), (4, "second exact")),
                CancellationToken.None
            );

        Assert.Collection(
            result,
            first => Assert.Equal(
                new CharacterNoteDerivedInfo(
                    1,
                    "First",
                    "First gist.",
                    "First main idea."
                ),
                first
            ),
            second => Assert.Equal(
                new CharacterNoteDerivedInfo(
                    4,
                    "Second",
                    "Second gist.",
                    "Second main idea."
                ),
                second
            )
        );
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CharacterNoteDerivedInfo>)result)[0] = result[1]
        );
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("multiple")]
    public async Task ZeroOrMultipleBatchArtifactsAreRejected(
        string scenario
    ) {
        ActionMessage message = scenario == "zero"
            ? new ActionMessage([new ActionBlock.Text("no batch")])
            : new ActionMessage([
                new ActionBlock.ToolCall(BatchCall("""
{"items":[{"artifactOrdinal":0,"title":"T","gist":"G.","summary":"S."}]}
""", "batch-1")),
                new ActionBlock.ToolCall(BatchCall("""
{"items":[{"artifactOrdinal":0,"title":"T","gist":"G.","summary":"S."}]}
""", "batch-2")),
            ]);
        var client = new ScriptedClient((self, request, _) =>
            Task.FromResult(self.Completed(request, message))
        );

        TextExtractionException exception = await Assert.ThrowsAsync<
            TextExtractionException>(() => CreateEnricher(client)
                .EnrichAsync(Request((0, "exact")), CancellationToken.None)
                .AsTask()
        );

        Assert.Equal(
            TextExtractionFailureKind.ArtifactCaptureMismatch,
            exception.Kind
        );
    }

    public static TheoryData<string> InvalidMappings => new() {
        {
            """
{"items":[{"artifactOrdinal":0,"title":"A","gist":"A.","summary":"A."}]}
"""
        },
        {
            """
{"items":[{"artifactOrdinal":0,"title":"A","gist":"A.","summary":"A."},{"artifactOrdinal":0,"title":"B","gist":"B.","summary":"B."}]}
"""
        },
        {
            """
{"items":[{"artifactOrdinal":0,"title":"A","gist":"A.","summary":"A."},{"artifactOrdinal":1,"title":"B","gist":"B.","summary":"B."}]}
"""
        },
        {
            """
{"items":[{"artifactOrdinal":2,"title":"B","gist":"B.","summary":"B."},{"artifactOrdinal":0,"title":"A","gist":"A.","summary":"A."}]}
"""
        },
    };

    [Theory]
    [MemberData(nameof(InvalidMappings))]
    public async Task InvalidOrdinalMappingsRejectWholeBatch(
        string arguments
    ) {
        ScriptedClient client = CallsClient(BatchCall(arguments));

        TextExtractionException exception = await Assert.ThrowsAsync<
            TextExtractionException>(() => CreateEnricher(client)
                .EnrichAsync(
                    Request((0, "first"), (2, "second")),
                    CancellationToken.None
                )
                .AsTask()
        );

        Assert.Equal(
            TextExtractionFailureKind.ToolExecutionFailed,
            exception.Kind
        );
    }

    public static IEnumerable<object[]> InvalidFieldArguments() {
        yield return ["""
{"items":[{"artifactOrdinal":0,"title":null,"gist":"G.","summary":"S."}]}
"""];
        yield return ["""
{"items":[{"artifactOrdinal":0,"title":"T","gist":null,"summary":"S."}]}
"""];
        yield return ["""
{"items":[{"artifactOrdinal":0,"title":"T","gist":"G.","summary":null}]}
"""];
        yield return ["""
{"items":[{"artifactOrdinal":0,"title":" ","gist":"G.","summary":"S."}]}
"""];
        yield return ["""
{"items":[{"artifactOrdinal":0,"title":" padded ","gist":"G.","summary":"S."}]}
"""];
        yield return ["""
{"items":[{"artifactOrdinal":0,"title":"bad\nline","gist":"G.","summary":"S."}]}
"""];
        yield return [SerializeBatch(
            title: new string('t',
                MemoPodLimits.MaximumMemoTitleUtf8Bytes + 1),
            gist: "G.",
            summary: "S."
        )];
        yield return [SerializeBatch(
            title: "T",
            gist: new string('g',
                MemoPodLimits.MaximumMemoGistUtf8Bytes + 1),
            summary: "S."
        )];
        yield return [SerializeBatch(
            title: "T",
            gist: "G.",
            summary: new string('s',
                MemoPodLimits.MaximumMemoSummaryUtf8Bytes + 1)
        )];
        yield return ["""
{"items":[{"artifactOrdinal":0,"title":"\uD800","gist":"G.","summary":"S."}]}
"""];
    }

    [Theory]
    [MemberData(nameof(InvalidFieldArguments))]
    public async Task InvalidDerivedInfoFieldRejectsWholeBatch(
        string arguments
    ) {
        ScriptedClient client = CallsClient(BatchCall(arguments));

        TextExtractionException exception = await Assert.ThrowsAsync<
            TextExtractionException>(() => CreateEnricher(client)
                .EnrichAsync(Request((0, "exact")), CancellationToken.None)
                .AsTask()
        );

        Assert.Contains(
            exception.Kind,
            new[] {
                TextExtractionFailureKind.MalformedToolCall,
                TextExtractionFailureKind.ToolExecutionFailed,
            }
        );
    }

    [Fact]
    public void TargetRendererRejectsInvalidOrderingUtf8AndBounds() {
        Assert.Throws<ArgumentException>(() =>
            CharacterNoteDerivedInfoTargetRenderer.Render(
                Request((2, "second"), (1, "first"))
            )
        );
        Assert.Throws<ArgumentException>(() =>
            CharacterNoteDerivedInfoTargetRenderer.Render(
                new CharacterNoteDerivedInfoEnrichmentRequest(
                    "observation",
                    "\ud800",
                    [new(0, "exact")]
                )
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CharacterNoteDerivedInfoTargetRenderer.Render(
                Request((0, new string(
                    'x',
                    MemoPodLimits.MaximumMemoExactTextUtf8Bytes + 1
                )))
            )
        );

        string escapingHeavy = new('"', 400_000);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CharacterNoteDerivedInfoTargetRenderer.Render(
                new CharacterNoteDerivedInfoEnrichmentRequest(
                    escapingHeavy,
                    escapingHeavy,
                    [new(0, "exact")]
                )
            )
        );
    }

    [Fact]
    public async Task InvalidTargetFailsBeforeClientLookup() {
        int accessorCalls = 0;
        var client = new ScriptedClient(static (_, _, _) =>
            throw new InvalidOperationException("must not be called")
        );
        var enricher = new CharacterNoteDerivedInfoEnricher(
            new GalateaCharacterName("Aster"),
            Connection(),
            () => {
                accessorCalls++;
                return client;
            }
        );

        await Assert.ThrowsAsync<ArgumentException>(() => enricher
            .EnrichAsync(
                new CharacterNoteDerivedInfoEnrichmentRequest(
                    "observation",
                    "action",
                    [new(0, "\ud800")]
                ),
                CancellationToken.None
            )
            .AsTask()
        );

        Assert.Equal(0, accessorCalls);
    }

    [Fact]
    public async Task CancellationAndProviderFailurePreserveTextExtractorSemantics() {
        ScriptedClient neverCalled = CallsClient(BatchCall("""
{"items":[{"artifactOrdinal":0,"title":"T","gist":"G.","summary":"S."}]}
"""));
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateEnricher(neverCalled).EnrichAsync(
                Request((0, "exact")),
                alreadyCancelled.Token
            ).AsTask()
        );
        Assert.Equal(0, neverCalled.CallCount);

        var providerFailure = new InvalidOperationException(
            "provider failed"
        );
        var failing = new ScriptedClient((_, _, _) =>
            Task.FromException<CompletionResult>(providerFailure)
        );
        InvalidOperationException observed = await Assert.ThrowsAsync<
            InvalidOperationException>(() => CreateEnricher(failing)
                .EnrichAsync(Request((0, "exact")), CancellationToken.None)
                .AsTask()
        );
        Assert.Same(providerFailure, observed);

        var waiting = new ScriptedClient(async (_, _, cancellationToken) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var cancelledDuringProvider = new CancellationTokenSource();
        Task pending = CreateEnricher(waiting).EnrichAsync(
            Request((0, "exact")),
            cancelledDuringProvider.Token
        ).AsTask();
        await waiting.Entered.Task;
        cancelledDuringProvider.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pending
        );
    }

    [Fact]
    public void ContractIdIsStableAndIncludesRenderedCharacterPrompts() {
        ScriptedClient client = CallsClient(BatchCall("""
{"items":[{"artifactOrdinal":0,"title":"T","gist":"G.","summary":"S."}]}
"""));
        CharacterNoteDerivedInfoEnricher first = CreateEnricher(
            client,
            "Aster"
        );
        CharacterNoteDerivedInfoEnricher second = CreateEnricher(
            client,
            "Aster"
        );
        CharacterNoteDerivedInfoEnricher anotherCharacter =
            CreateEnricher(client, "Beryl");

        Assert.Equal(first.ContractId, second.ContractId);
        Assert.StartsWith(
            "atelia.galatea.character-note-derived-info-enricher.v1.",
            first.ContractId,
            StringComparison.Ordinal
        );
        Assert.Equal(64, first.ContractId[(first.ContractId.LastIndexOf(
            ".",
            StringComparison.Ordinal
        ) + 1)..].Length);
        Assert.NotEqual(first.ContractId, anotherCharacter.ContractId);
    }

    private static CharacterNoteDerivedInfoEnricher CreateEnricher(
        ScriptedClient client,
        string characterName = "Aster"
    ) => new(
        new GalateaCharacterName(characterName),
        Connection(),
        () => client
    );

    private static CharacterNoteDerivedInfoEnrichmentRequest Request(
        params (int Ordinal, string ExactText)[] targets
    ) => new(
        "raw observation",
        "visible action",
        targets.Select(static target =>
            new CharacterNoteDerivedInfoTarget(
                target.Ordinal,
                target.ExactText
            )
        ).ToArray()
    );

    private static CompletionConnectionConfig Connection() => new(
        "derived-info",
        "test",
        "derived-model",
        "test-v1",
        "https://example.invalid/"
    );

    private static RawToolCall BatchCall(
        string arguments,
        string callId = "batch"
    ) => new(
        CharacterNoteDerivedInfoEnricher.ToolName,
        callId,
        arguments
    );

    private static ScriptedClient CallsClient(
        params RawToolCall[] calls
    ) => new((self, request, _) => Task.FromResult(self.Completed(
        request,
        new ActionMessage(calls.Select(static call =>
            (ActionBlock)new ActionBlock.ToolCall(call)
        ).ToArray())
    )));

    private static string SerializeBatch(
        string? title,
        string? gist,
        string? summary
    ) => JsonSerializer.Serialize(new {
        items = new[] {
            new {
                artifactOrdinal = 0,
                title,
                gist,
                summary,
            },
        },
    });

    private sealed class ScriptedClient(
        Func<ScriptedClient, CompletionRequest, CancellationToken,
            Task<CompletionResult>> handler
    ) : ICompletionClient {
        public string Name => "character-note-derived-info-test";

        public string ApiSpecId => "test-v1";

        internal CompletionRequest? LastRequest { get; private set; }

        internal int CallCount { get; private set; }

        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            LastRequest = request;
            CallCount++;
            Entered.TrySetResult();
            return handler(this, request, cancellationToken);
        }

        internal CompletionResult Completed(
            CompletionRequest request,
            ActionMessage message
        ) => new(
            message,
            CompletionDescriptor.From(this, request)
        );
    }
}
