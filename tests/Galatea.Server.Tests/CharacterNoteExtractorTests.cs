using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteExtractorTests {
    [Fact]
    public async Task CompositionFactoryUsesDisabledSingletonOrLazyPerCharacterExtractors() {
        IReadOnlyDictionary<string, GalateaUserConfig> users = new[] {
            User("alice", "Alice"),
            User("bob", "Bob"),
            User("alice-again", "Alice"),
        }.ToDictionary(static user => user.UserId, StringComparer.Ordinal);
        int getClientCallCount = 0;
        ICompletionClient GetClient() {
            Interlocked.Increment(ref getClientCallCount);
            return new QueueClient(_ => Message());
        }

        IReadOnlyDictionary<string, ICharacterNoteExtractor> disabled =
            GalateaHostService.CreateCharacterNoteExtractors(
                users,
                connection: null,
                GetClient
            );

        Assert.All(disabled.Values, extractor => Assert.Same(
            DisabledCharacterNoteExtractor.Instance,
            extractor
        ));
        Assert.Empty(await disabled["alice"].ExtractAsync(
            "visible action",
            CancellationToken.None
        ));

        IReadOnlyDictionary<string, ICharacterNoteExtractor> enabled =
            GalateaHostService.CreateCharacterNoteExtractors(
                users,
                Connection(),
                GetClient
            );

        Assert.All(enabled.Values, static extractor =>
            Assert.IsType<CharacterNoteExtractor>(extractor));
        Assert.Equal(
            enabled["alice"].ContractId,
            enabled["alice-again"].ContractId
        );
        Assert.NotEqual(
            enabled["alice"].ContractId,
            enabled["bob"].ContractId
        );
        Assert.Equal(0, Volatile.Read(ref getClientCallCount));
    }

    [Fact]
    public async Task ProductionSessionReceivesEnabledExtractorLazily() {
        var factory = new RejectingFactory();
        await using GalateaTestHost host = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            characterNoteExtractorConnectionId: "test"
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        Assert.IsType<CharacterNoteExtractor>(
            session.CharacterNoteExtractor
        );
        Assert.Matches(
            "^atelia\\.galatea\\.character-note-extractor\\.v1\\.[0-9a-f]{64}$",
            session.CharacterNoteExtractor.ContractId
        );
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task ContractPromptAndSchemaArePerCharacterAndLazy() {
        var client = new QueueClient(
            _ => Message(),
            _ => Message()
        );
        CompletionConnectionConfig connection = Connection();
        var alice = new CharacterNoteExtractor(
            new GalateaCharacterName("Alice"),
            connection,
            () => client
        );
        var aliceAgain = new CharacterNoteExtractor(
            new GalateaCharacterName("Alice"),
            connection,
            () => client
        );
        var bob = new CharacterNoteExtractor(
            new GalateaCharacterName("Bob"),
            connection,
            () => client
        );
        _ = new CharacterNoteExtractor(
            new GalateaCharacterName("ConstructionProbe"),
            connection,
            () => throw new Xunit.Sdk.XunitException(
                "Contract construction must not create the shared client."
            )
        );

        Assert.Empty(client.Requests);
        Assert.Equal(alice.ContractId, aliceAgain.ContractId);
        Assert.NotEqual(alice.ContractId, bob.ContractId);
        Assert.Matches(
            "^atelia\\.galatea\\.character-note-extractor\\.v1\\.[0-9a-f]{64}$",
            alice.ContractId
        );

        _ = await alice.ExtractAsync(
            "[Alice] only considered a note.",
            CancellationToken.None
        );
        _ = await bob.ExtractAsync(
            "[Bob] only considered a note.",
            CancellationToken.None
        );

        Assert.Equal(2, client.Requests.Count);
        CompletionRequest aliceRequest = client.Requests[0];
        CompletionRequest bobRequest = client.Requests[1];
        Assert.Contains(
            "[Alice]",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "${characterName}",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "[Bob]",
            bobRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "composite GM carrier",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "[状态摘要] cannot establish",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Ordinary thoughts, discoveries, conclusions",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Ordinary diaries, sticky notes, graffiti, mail",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "quoted or existing Notes",
            Assert.IsType<ObservationMessage>(
                Assert.Single(aliceRequest.TailMessages)
            ).Content,
            StringComparison.Ordinal
        );
        string schema = ToolSchemaTextRenderer.RenderDefinitions(
            aliceRequest.PromptPrefix.OutputContract.Tools
        );
        Assert.Contains("exactText", schema, StringComparison.Ordinal);
        Assert.Contains("evidenceQuote", schema, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "title",
            schema,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task ReturnsZeroAndPreservesOrderedGroundedIntents() {
        const string Target = """
[Galatea] I recorded my first long-term Note as "first exact note" and saved it.
[Galatea] I recorded my second long-term Note as "second exact note" and saved it.
""";
        var client = new QueueClient(
            _ => Message(),
            _ => Message(
                Tool(
                    "note-1",
                    "first exact note",
                    "[Galatea] I recorded my first long-term Note"
                ),
                Tool(
                    "note-2",
                    "second exact note",
                    "[Galatea] I recorded my second long-term Note"
                )
            )
        );
        var extractor = CreateExtractor(client);

        Assert.Empty(await extractor.ExtractAsync(
            "[Galatea] I only thought about tomorrow.",
            CancellationToken.None
        ));
        IReadOnlyList<CharacterNoteIntent> intents =
            await extractor.ExtractAsync(Target, CancellationToken.None);

        Assert.Equal(
            ["first exact note", "second exact note"],
            intents.Select(static intent => intent.ExactText)
        );
        Assert.Equal(
            [
                "[Galatea] I recorded my first long-term Note",
                "[Galatea] I recorded my second long-term Note",
            ],
            intents.Select(static intent => intent.EvidenceQuote)
        );
    }

    [Theory]
    [InlineData(InvalidIntentShape.BlankExactText)]
    [InlineData(InvalidIntentShape.BlankEvidenceQuote)]
    [InlineData(InvalidIntentShape.UngroundedExactText)]
    [InlineData(InvalidIntentShape.UngroundedEvidenceQuote)]
    [InlineData(InvalidIntentShape.OversizedExactText)]
    [InlineData(InvalidIntentShape.OversizedEvidenceQuote)]
    [InlineData(InvalidIntentShape.InvalidUtf16Arguments)]
    public async Task RejectsInvalidOrUngroundedArtifacts(
        InvalidIntentShape shape
    ) {
        const string ValidExactText = "grounded note";
        const string ValidEvidence = "recorded and saved";
        string target = $"{ValidExactText}; {ValidEvidence}";
        ActionBlock.ToolCall call = shape switch {
            InvalidIntentShape.BlankExactText =>
                Tool("invalid", " ", ValidEvidence),
            InvalidIntentShape.BlankEvidenceQuote =>
                Tool("invalid", ValidExactText, " "),
            InvalidIntentShape.UngroundedExactText =>
                Tool("invalid", "invented note", ValidEvidence),
            InvalidIntentShape.UngroundedEvidenceQuote =>
                Tool("invalid", ValidExactText, "invented evidence"),
            InvalidIntentShape.OversizedExactText => Tool(
                "invalid",
                new string(
                    'x',
                    CharacterNoteBounds.MaximumExactTextUtf8Bytes + 1
                ),
                ValidEvidence
            ),
            InvalidIntentShape.OversizedEvidenceQuote => Tool(
                "invalid",
                ValidExactText,
                new string(
                    'x',
                    CharacterNoteBounds.MaximumEvidenceQuoteUtf8Bytes + 1
                )
            ),
            InvalidIntentShape.InvalidUtf16Arguments => new(
                new RawToolCall(
                    CharacterNoteExtractor.ToolName,
                    "invalid",
                    "{\"exactText\":\"" + "\ud800"
                        + "\",\"evidenceQuote\":\"recorded and saved\"}"
                )
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        if (shape == InvalidIntentShape.OversizedExactText) {
            target = new string(
                'x',
                CharacterNoteBounds.MaximumExactTextUtf8Bytes + 1
            ) + ValidEvidence;
        }
        else if (shape == InvalidIntentShape.OversizedEvidenceQuote) {
            target = ValidExactText + new string(
                'x',
                CharacterNoteBounds.MaximumEvidenceQuoteUtf8Bytes + 1
            );
        }
        var extractor = CreateExtractor(new QueueClient(
            _ => Message(call)
        ));

        _ = await Assert.ThrowsAsync<TextExtractionException>(() =>
            extractor.ExtractAsync(target, CancellationToken.None).AsTask()
        );
    }

    [Fact]
    public async Task EnforcesBatchBoundsWithoutDeduplicating() {
        string boundaryText = new(
            'x',
            CharacterNoteBounds.MaximumExactTextUtf8Bytes
        );
        const string Evidence = "recorded and saved";
        var client = new QueueClient(
            _ => Message(Enumerable.Range(0, 17)
                .Select(index => Tool(
                    $"too-many-{index}",
                    "same note",
                    Evidence
                ))
                .ToArray()),
            _ => Message(Enumerable.Range(0, 5)
                .Select(index => Tool(
                    $"too-large-{index}",
                    boundaryText,
                    Evidence
                ))
                .ToArray()),
            _ => Message(Enumerable.Range(0, 4)
                .Select(index => Tool(
                    $"boundary-{index}",
                    boundaryText,
                    Evidence
                ))
                .ToArray())
        );
        var extractor = CreateExtractor(client);

        _ = await Assert.ThrowsAsync<TextExtractionException>(() =>
            extractor.ExtractAsync(
                "same note; " + Evidence,
                CancellationToken.None
            ).AsTask()
        );
        _ = await Assert.ThrowsAsync<TextExtractionException>(() =>
            extractor.ExtractAsync(
                boundaryText + Evidence,
                CancellationToken.None
            ).AsTask()
        );
        IReadOnlyList<CharacterNoteIntent> boundary =
            await extractor.ExtractAsync(
                boundaryText + Evidence,
                CancellationToken.None
            );

        Assert.Equal(4, boundary.Count);
        Assert.All(boundary, intent => Assert.Equal(
            boundaryText,
            intent.ExactText
        ));
    }

    private static CharacterNoteExtractor CreateExtractor(
        ICompletionClient client
    ) => new(
        new GalateaCharacterName("Galatea"),
        Connection(),
        () => client
    );

    private static CompletionConnectionConfig Connection() => new(
        "note-extractor",
        "openai-chat",
        "note-extractor-model",
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static GalateaUserConfig User(
        string userId,
        string characterName
    ) => new(
        userId,
        "pw",
        new GalateaCharacterName(characterName),
        new GalateaPlayerName("Player"),
        Path.Combine(Path.GetTempPath(), "character-note", userId),
        Path.Combine(
            Path.GetTempPath(),
            "character-note-delegation",
            userId
        ),
        GalateaSessionProvisioning.ExistingOnly,
        "system prompt"
    );

    private static ActionBlock.ToolCall Tool(
        string callId,
        string exactText,
        string evidenceQuote
    ) => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        callId,
        JsonSerializer.Serialize(new { exactText, evidenceQuote })
    ));

    private static ActionMessage Message(params ActionBlock[] blocks) =>
        new(blocks);

    public enum InvalidIntentShape {
        BlankExactText,
        BlankEvidenceQuote,
        UngroundedExactText,
        UngroundedEvidenceQuote,
        OversizedExactText,
        OversizedEvidenceQuote,
        InvalidUtf16Arguments,
    }

    private sealed class QueueClient(
        params Func<CompletionRequest, ActionMessage>[] scripts
    ) : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, ActionMessage>>
            _scripts = new(scripts);

        public string Name => "galatea-character-note-test";
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
            ActionMessage message = _scripts.Dequeue()(request);
            return Task.FromResult(new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            ));
        }
    }

    private sealed class RejectingFactory : ICompletionClientFactory {
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            Interlocked.Increment(ref _createCallCount);
            throw new Xunit.Sdk.XunitException(
                "Character Note composition must remain lazy."
            );
        }
    }
}
