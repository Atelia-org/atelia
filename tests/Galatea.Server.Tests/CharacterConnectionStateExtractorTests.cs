using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.Galatea.Prompts;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterConnectionStateExtractorTests {
    private const string Target = "[Galatea] 她换上裙装。\n[状态摘要] 衣着：裙装。";

    [Fact]
    public async Task ToolArtifactRequiresExactEligibleIdAndEvidenceFromAction() {
        var client = new QueueClient(
            Message(new ActionBlock.Text("diagnostic only")),
            Message(UnknownTool("explicit-unknown")),
            Message(Tool("dress", "dress", "衣着：裙装")),
            Message(Tool("null-id-with-evidence", null, "衣着：裙装")),
            Message(Tool("id-with-null-evidence", "dress", null)),
            Message(Tool("blank-trigger", "manual", "衣着：裙装")),
            Message(Tool("other-character", "glasses", "衣着：裙装")),
            Message(Tool("paraphrase", "dress", "穿着裙装")),
            Message(Tool("blank-evidence", "dress", " ")),
            Message(Tool("too-long", "dress", new string('x', 2049))),
            Message(Tool("first", "dress", "衣着：裙装"), Tool("second", "dress", "衣着：裙装")),
            Message(UnknownTool("mixed-unknown"), Tool("mixed-known", "dress", "衣着：裙装")),
            Message(UnknownTool("unknown-first"), UnknownTool("unknown-second"))
        );
        var extractor = Create(client);

        Assert.Null(await extractor.ExtractAsync(Target, CancellationToken.None));
        Assert.Null(await extractor.ExtractAsync(Target, CancellationToken.None));
        Assert.Equal(new CharacterConnectionStateMatch("dress", "衣着：裙装"),
            await extractor.ExtractAsync(Target, CancellationToken.None));
        for (int i = 0; i < 10; i++) {
            await Assert.ThrowsAsync<TextExtractionException>(async () => {
                _ = await extractor.ExtractAsync(Target, CancellationToken.None);
            });
        }
    }

    [Fact]
    public async Task PromptUsesCharacterAndEligibleOptionSnapshotWithoutCreatingClientEarly() {
        var client = new QueueClient(Message());
        int accesses = 0;
        var extractor = new CharacterConnectionStateExtractor(
            new GalateaCharacterName("Galatea"), Options(), Connection(),
            () => { accesses++; return client; });
        Assert.Equal(0, accesses);

        Assert.Null(await extractor.ExtractAsync("A < B & C", CancellationToken.None));

        Assert.Equal(1, accesses);
        CompletionRequest request = Assert.Single(client.Requests);
        Assert.Contains("[Galatea]", request.PromptPrefix.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("connectionId", request.PromptPrefix.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("dress", request.PromptPrefix.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("裙装", request.PromptPrefix.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"manual\"", request.PromptPrefix.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("absent mention of glasses", request.PromptPrefix.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("conflicts with the narrative", request.PromptPrefix.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("currently used runtime connection", request.PromptPrefix.SystemPrompt, StringComparison.Ordinal);
        string schema = ToolSchemaTextRenderer.RenderDefinitions(request.PromptPrefix.OutputContract.Tools);
        Assert.Contains("connectionId", schema, StringComparison.Ordinal);
        Assert.Contains("evidence", schema, StringComparison.Ordinal);
        Assert.Contains("2048", schema, StringComparison.Ordinal);
        Assert.Contains(CharacterConnectionStateExtractor.UnknownToolName,
            schema, StringComparison.Ordinal);
        Assert.Contains("unknown", schema, StringComparison.Ordinal);
        ObservationMessage input = Assert.IsType<ObservationMessage>(Assert.Single(request.TailMessages));
        Assert.Contains("A &lt; B &amp; C", input.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsAmbiguousOrOversizedOptions() {
        Assert.Throws<ArgumentException>(() => new CharacterConnectionStateExtractor(
            new GalateaCharacterName("Galatea"),
            [new("manual", "Manual", "")], Connection(), NeverClient));
        Assert.Throws<ArgumentException>(() => new CharacterConnectionStateExtractor(
            new GalateaCharacterName("Galatea"),
            [new("dress", "Dress", "裙装"), new("dress", "Again", "裤装")],
            Connection(), NeverClient));
        Assert.Throws<ArgumentException>(() => new CharacterConnectionStateExtractor(
            new GalateaCharacterName("Galatea"),
            [new("dress", "Dress", new string('x', 4097))],
            Connection(), NeverClient));
        Assert.Throws<ArgumentException>(() => new CharacterConnectionStateExtractor(
            new GalateaCharacterName("Galatea"),
            Enumerable.Range(0, 20).Select(i => new GalateaCharacterConnectionOption(
                $"id-{i}", "name", new string('x', 4096))).ToArray(),
            Connection(), NeverClient));
    }

    [Fact]
    public async Task DisabledExtractorReturnsNullAndHonorsCancellation() {
        ICharacterConnectionStateExtractor extractor = DisabledCharacterConnectionStateExtractor.Instance;
        Assert.Null(await extractor.ExtractAsync(Target, CancellationToken.None));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => {
            _ = await extractor.ExtractAsync(Target, source.Token);
        });
    }

    private static GalateaCharacterConnectionOption[] Options() => [
        new("dress", "裙装", "穿着裙装"),
        new("pants", "裤装", "穿着裤装"),
        new("manual", "Manual", ""),
    ];

    private static CharacterConnectionStateExtractor Create(ICompletionClient client) => new(
        new GalateaCharacterName("Galatea"), Options(), Connection(), () => client);

    private static CompletionConnectionConfig Connection() => new(
        "state-extractor", "openai-chat", "state-extractor-model",
        "openai-chat/strict", "http://localhost:8000/", ApiKey: "test-key");

    private static ICompletionClient NeverClient() => throw new Xunit.Sdk.XunitException(
        "Construction must not create the client.");

    private static ActionBlock.ToolCall Tool(string callId, string? connectionId, string? evidence) =>
        new(new RawToolCall(CharacterConnectionStateExtractor.ToolName, callId,
            JsonSerializer.Serialize(new { connectionId, evidence })));

    private static ActionBlock.ToolCall UnknownTool(string callId) =>
        new(new RawToolCall(CharacterConnectionStateExtractor.UnknownToolName,
            callId, "{}"));

    private static ActionMessage Message(params ActionBlock[] blocks) => new(blocks);

    private sealed class QueueClient(params ActionMessage[] messages) : ICompletionClient {
        private readonly Queue<ActionMessage> _messages = new(messages);

        public string Name => "character-connection-state-test";
        public string ApiSpecId => "test-v1";
        internal List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new CompletionResult(
                _messages.Dequeue(), CompletionDescriptor.From(this, request)));
        }
    }
}
