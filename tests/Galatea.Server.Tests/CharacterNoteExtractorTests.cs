using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Atelia.Testing;
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
        string finalizedPrompt = session.Engine.ResolveGoverningSetup(
            session.Engine.ReadCurrentHead()
                ?? throw new Xunit.Sdk.XunitException(
                    "The test session has no governing head."
                )
        ).SystemPrompt;
        Assert.Contains(
            "### 保存长期 Note",
            finalizedPrompt,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "### 发信给 Codex",
            finalizedPrompt,
            StringComparison.Ordinal
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
            "explicit current save requests",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "already recorded, stored, or saved does not establish a current save request",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Emit at most 16 tool calls",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "earliest qualifying Notes first",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Never truncate or summarize a Note",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "64 KiB of UTF-8 text",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "combined emitted text exceed 256 KiB",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Runtime validation is authoritative",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "need not perform exact UTF-8 byte arithmetic",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal
        );
        ObservationMessage aliceTail = Assert.IsType<ObservationMessage>(
            Assert.Single(aliceRequest.TailMessages)
        );
        Assert.Contains(
            "earlier save results",
            aliceTail.Content,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "up to 16 earliest qualifying Notes",
            aliceTail.Content,
            StringComparison.Ordinal
        );
        string schema = ToolSchemaTextRenderer.RenderDefinitions(
            aliceRequest.PromptPrefix.OutputContract.Tools
        );
        Assert.Contains("text", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("exactText", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("evidenceQuote", schema, StringComparison.Ordinal);
        Assert.Contains("64 KiB", schema, StringComparison.Ordinal);
        Assert.Contains("save request", schema, StringComparison.Ordinal);
        Assert.Contains("emit each Note separately", schema,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "title",
            schema,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            "gist",
            schema,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            "summary",
            schema,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            "category",
            schema,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            "recall",
            schema,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task ReturnsZeroAndPreservesOrderedTranscribedIntents() {
        const string Target = """
[Galatea] I submitted two long-term Notes:
> **First** note, on two
> lines.
> Literal `&gt;`, path `/notes/中文` and condition `n > 3` stay unchanged.
""";
        string[] texts = [
            "First note, on two lines.",
            "Literal `&gt;`, path `/notes/中文` and condition `n > 3` stay unchanged.",
        ];
        var client = new QueueClient(
            _ => Message(),
            _ => Message(Tool("note-1", texts[0]), Tool("note-2", texts[1]))
        );
        var extractor = CreateExtractor(client);

        Assert.Empty(await extractor.ExtractAsync(
            "[Galatea] I only thought about tomorrow.",
            CancellationToken.None
        ));
        IReadOnlyList<CharacterNoteIntent> intents =
            await extractor.ExtractAsync(Target, CancellationToken.None);

        Assert.Equal(texts, intents.Select(static intent => intent.Text));
        Assert.DoesNotContain(texts[0], Target, StringComparison.Ordinal);
        string envelope = Assert.IsType<string>(Assert.IsType<ObservationMessage>(
            Assert.Single(client.Requests[1].TailMessages)).Content);
        Assert.Contains("&amp;gt;", envelope, StringComparison.Ordinal);
        Assert.Contains("n &gt; 3", envelope, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InvalidIntentShape.BlankText)]
    [InlineData(InvalidIntentShape.MissingText)]
    [InlineData(InvalidIntentShape.NonStringText)]
    [InlineData(InvalidIntentShape.OversizedText)]
    [InlineData(InvalidIntentShape.InvalidUtf16Arguments)]
    public async Task RejectsInvalidArtifacts(InvalidIntentShape shape) {
        ActionBlock.ToolCall call = shape switch {
            InvalidIntentShape.BlankText => Tool("invalid", " "),
            InvalidIntentShape.MissingText => RawTool("{}"),
            InvalidIntentShape.NonStringText => RawTool("{\"text\":42}"),
            InvalidIntentShape.OversizedText => Tool(
                "invalid",
                new string('x', CharacterNoteBounds.MaximumExactTextUtf8Bytes + 1)
            ),
            InvalidIntentShape.InvalidUtf16Arguments =>
                RawTool("{\"text\":\"" + "\ud800" + "\"}"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var extractor = CreateExtractor(new QueueClient(_ => Message(call)));

        _ = await Assert.ThrowsAsync<TextExtractionException>(() =>
            extractor.ExtractAsync("[Galatea] I submitted a Note.", CancellationToken.None).AsTask()
        );
    }

    [Fact]
    public async Task TwoTranscribedNotesPersistWithFrozenReceiptAndColdReopenSkipsExtraction() {
        // Anonymized shape of the two-Note incident. The response faithfully
        // joins paragraphs; it is deliberately not an ordinal source substring.
        const string Action = """
[Galatea] 请把下面两条存为长期 Note：
> **截至2026年9月13日03:12，我选择试用“小澄”这个名字。**
> 这不是永久更名，也不证明 runtime 配置已修改；试用后再确认。

> **故事内书写、向 runtime 提交请求、收到保存成功回执，必须区分。**
> 没有回执不自动等于保存失败；不得把尚未完成的工作写成完成。
""";
        string[] texts = [
            "截至2026年9月13日03:12，我选择试用“小澄”这个名字。这不是永久更名，也不证明 runtime 配置已修改；试用后再确认。",
            "故事内书写、向 runtime 提交请求、收到保存成功回执，必须区分。没有回执不自动等于保存失败；不得把尚未完成的工作写成完成。",
        ];
        Assert.All(texts, text => Assert.DoesNotContain(text, Action, StringComparison.Ordinal));
        var client = new QueueClient(_ => Message(
            Tool("note-1", texts[0]), Tool("note-2", texts[1])));
        var extractor = CreateExtractor(client);
        string root = Path.Combine(Path.GetTempPath(), "atelia-note-transcription-" + Guid.NewGuid().ToString("N"));
        string sessionPath = Path.Combine(root, "session");
        string memoryPath = Path.Combine(root, "memory");
        var owner = new CharacterMemoryStoreOwner("user", sessionPath);
        GalateaTerminalActionExtractionTarget target;
        string receiptBody;
        long receiptRevision;
        string[] memoIds;
        try {
            using (var engine = SessionJournalEngine.Create(sessionPath, new SessionCreateOptions("model", "system", "surface"))) {
                using var memory = await CharacterNoteDefaultPodReconciler.CreateNewAsync(
                    memoryPath, owner,
                    new CharacterMemoryStoreBaseline(engine.ReadView.ReadPhysicalAppendFrontier(),
                        EventAddressTextCodec.FormatNullable(engine.ReadCurrentHead())),
                    extractor);
                _ = engine.AppendObservation("save two notes");
                EventAddress action = engine.AppendImportedAgentAction(
                    Message(new ActionBlock.Text(Action)),
                    new CompletionDescriptor("fixture", "test-v1", "model"));
                target = new GalateaTerminalActionExtractionTarget(action, Action);
                var applied = Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(
                    await memory.ReconcileTargetAsync(engine, target));
                Assert.Equal(texts, applied.Memos.Select(static memo => memo.ExactText));
                memoIds = applied.Memos.Select(static memo => memo.MemoId.Value).ToArray();
                CharacterNoteReceiptDeliverySnapshot receipt = Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
                    memory.ReadPendingReceiptDelivery());
                Assert.All(texts, text => Assert.Contains(text, receipt.NoticeBody, StringComparison.Ordinal));
                receiptBody = receipt.NoticeBody;
                receiptRevision = receipt.CreatedRevision;
            }

            // A changed extraction contract must never reinterpret an existing capture.
            var changedExtractor = new CharacterNoteExtractor(
                new GalateaCharacterName("Changed character name"), Connection(),
                () => throw new Xunit.Sdk.XunitException("Reopen must not invoke the extractor."));
            Assert.NotEqual(extractor.ContractId, changedExtractor.ContractId);
            using var reopenedEngine = SessionJournalEngine.Open(sessionPath);
            using var reopenedMemory = await CharacterNoteDefaultPodReconciler.OpenExistingAsync(
                memoryPath, owner, changedExtractor);
            Assert.IsType<CharacterNoteDefaultPodReconcileResult.AlreadyApplied>(
                await reopenedMemory.ReconcileTargetAsync(reopenedEngine, target));
            CharacterNoteReceiptDeliverySnapshot recoveredReceipt = Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
                reopenedMemory.ReadPendingReceiptDelivery());
            Assert.Equal(receiptBody, recoveredReceipt.NoticeBody);
            Assert.Equal(receiptRevision, recoveredReceipt.CreatedRevision);
            var pod = global::Atelia.MemoPod.MemoPod.Open(memoryPath, CharacterNoteDefaultPodV1.PodId);
            Assert.Equal(texts, pod.List().Select(static memo => memo.ExactText));
            Assert.Equal(memoIds, pod.List().Select(static memo => memo.Id.Value));
            Assert.Single(client.Requests);
        }
        finally {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(root);
        }
    }

    [Fact]
    public async Task EnforcesBatchBoundsWithoutDeduplicating() {
        string boundaryText = new(
            'x',
            CharacterNoteBounds.MaximumExactTextUtf8Bytes
        );
        const string Evidence = "completed submitting the Note request";
        var client = new QueueClient(
            _ => Message(Enumerable.Range(0, 17)
                .Select(index => Tool(
                    $"too-many-{index}",
                    "same note"
                ))
                .ToArray()),
            _ => Message(Enumerable.Range(0, 5)
                .Select(index => Tool(
                    $"too-large-{index}",
                    boundaryText
                ))
                .ToArray()),
            _ => Message(Enumerable.Range(0, 4)
                .Select(index => Tool(
                    $"boundary-{index}",
                    boundaryText
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
            intent.Text
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
        Path.Combine(
            Path.GetTempPath(),
            "character-note-memory",
            userId
        ),
        GalateaDelegateTestConfiguration.CreateHomeDirectory(Path.Combine(Path.GetTempPath(), "character-note", userId), userId),
        GalateaSessionProvisioning.ExistingOnly,
        "system prompt",
        "note-extractor"
    );

    private static ActionBlock.ToolCall Tool(
        string callId,
        string text
    ) => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        callId,
        JsonSerializer.Serialize(new { text })
    ));

    private static ActionMessage Message(params ActionBlock[] blocks) =>
        new(blocks);

    private static ActionBlock.ToolCall RawTool(string arguments) => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        "invalid",
        arguments
    ));

    public enum InvalidIntentShape {
        BlankText,
        MissingText,
        NonStringText,
        OversizedText,
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
