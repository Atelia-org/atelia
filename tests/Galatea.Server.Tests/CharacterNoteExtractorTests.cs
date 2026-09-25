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
#if DEBUG
    [Fact]
    public async Task NoteDiagnostics_ReportSecondBusinessCandidateFailure() {
        var diagnostics = new List<string>();
        var client = new QueueClient(_ => Message(
            Tool("first", 1, 1),
            Tool("second", 2, 2)
        ));
        var extractor = new CharacterNoteExtractor(
            new GalateaCharacterName("Galatea"),
            Connection(),
            () => client
        ) { DiagnosticSinkForTest = diagnostics.Add };
        var source = new TextExtractionSource(
            "cyber", "ej1:note-diagnostic-test", "note-attempt-test"
        );

        TextExtractionException failure = await Assert.ThrowsAsync<
            TextExtractionException>(() => extractor.ExtractAsync(
                "first note\n" + new string('x', CharacterNoteBounds.MaximumExactTextUtf8Bytes + 1), CancellationToken.None, source
            ).AsTask());

        Assert.Equal("note-text-too-long", failure.DiagnosticReasonCode);
        JsonElement[] records = diagnostics.Select(static json =>
            JsonDocument.Parse(json).RootElement.Clone()).ToArray();
        Assert.All(records, record => Assert.Equal("note-attempt-test",
            record.GetProperty("attemptId").GetString()));
        JsonElement completion = Assert.Single(records, record => record
            .GetProperty("event").GetString()
                == "text-extraction-completion-observed");
        Assert.Equal(2, completion.GetProperty("details")
            .GetProperty("rawToolCallCount").GetInt32());
        JsonElement finished = Assert.Single(records, record => record
            .GetProperty("event").GetString()
                == "text-extraction-finished");
        Assert.Equal("failed", finished.GetProperty("details")
            .GetProperty("outcome").GetString());
        Assert.Equal(1, finished.GetProperty("details")
            .GetProperty("acceptedCount").GetInt32());
        Assert.Equal("note-text-too-long", finished
            .GetProperty("details").GetProperty("reasonCode").GetString());
    }
#endif

    [Fact]
    public async Task CompositionFactoryUsesDisabledSingletonOrLazyPerCharacterExtractors() {
        IReadOnlyDictionary<string, GalateaCharacterConfig> users = new[] {
            User("alice", "Alice"),
            User("bob", "Bob"),
            User("alice-again", "Alice"),
        }.ToDictionary(static user => user.CharacterId, StringComparer.Ordinal);
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

        CharacterSessionHost session = await service.GetSessionAsync(
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
        SessionInputContent systemInstructions = session.Engine.ResolveGoverningSetup(
            session.Engine.ReadCurrentHead()
                ?? throw new Xunit.Sdk.XunitException(
                    "The test session has no governing head."
                )
        ).SystemPrompt;
        Assert.Equal(GalateaSystemInstructionContent.SchemaId, systemInstructions.SchemaId);
        JsonElement[] instructions = systemInstructions.JsonValue
            .GetProperty("instructions").EnumerateArray().ToArray();
        JsonElement noteInstruction = Assert.Single(instructions,
            instruction => instruction.GetProperty("kind").GetString() == "character-note-save");
        Assert.Contains(
            "### 保存长期 Note",
            noteInstruction.GetProperty("source").GetString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(instructions,
            instruction => instruction.GetProperty("kind").GetString() == "outbound-mail");
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
            "Emit at most 16 distinct Notes",
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
        Assert.Contains("textStartLine", schema, StringComparison.Ordinal);
        Assert.Contains("textEndLine", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\":", schema, StringComparison.Ordinal);
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
    public async Task ReturnsZeroAndPreservesSourceOrderAndOriginalBytesAcrossRounds() {
        const string Target = "[Galatea] save two Notes.\r\n> **First**\r\n\tline two.\r\nLiteral `&gt;` and n > 3.";
        var client = new QueueClient(
            _ => Message(),
            _ => Message(Tool("later", 4, 4)),
            _ => Message(Tool("earlier", 2, 3)),
            _ => Message(Tool("duplicate", 4, 4)),
            _ => Message());
        var extractor = CreateExtractor(client);
        Assert.Empty(await extractor.ExtractAsync("only thoughts", CancellationToken.None));
        var notes = await extractor.ExtractAsync(Target, CancellationToken.None);
        Assert.Equal(["> **First**\r\n\tline two.", "Literal `&gt;` and n > 3."],
            notes.Select(note => note.Text));
        Assert.Equal(5, client.Requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"textStartLine\":\"1\",\"textEndLine\":1}")]
    [InlineData("{\"textStartLine\":0,\"textEndLine\":1}")]
    [InlineData("{\"textStartLine\":1,\"textEndLine\":2}")]
    [InlineData("{\"textStartLine\":1,\"textEndLine\":1,\"text\":\"fabricated\"}")]
    public async Task RejectsInvalidRangesOrLegacyText(string arguments) {
        var extractor = CreateExtractor(new QueueClient(_ => Message(RawTool(arguments))));
        await Assert.ThrowsAsync<TextExtractionException>(() =>
            extractor.ExtractAsync("one line", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RejectsBlankRangeAndUnrepresentableLayoutWithoutPartialResult() {
        var extractor = CreateExtractor(new QueueClient(_ => Message(Tool("blank", 1, 1))));
        await Assert.ThrowsAsync<TextExtractionException>(() =>
            extractor.ExtractAsync(" ", CancellationToken.None).AsTask());
        var client = new QueueClient(_ => Message(Tool("accepted", 1, 1)),
            _ => Message(new ActionBlock.ToolCall(new RawToolCall(
                "report_extraction_problem", "layout", "{\"reason\":\"unrepresentable_layout\"}"))));
        await Assert.ThrowsAsync<TextExtractionException>(() =>
            CreateExtractor(client).ExtractAsync("valid body", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task TwoTranscribedNotesPersistWithReceiptFactsAndColdReopenSkipsExtraction() {
        // Original source slices survive persistence and cold reopen unchanged.
        const string Action = """
[Galatea] 请把下面两条存为长期 Note：
> **截至2026年9月13日03:12，我选择试用“小澄”这个名字。**
> 这不是永久更名，也不证明 runtime 配置已修改；试用后再确认。

> **故事内书写、向 runtime 提交请求、收到保存成功回执，必须区分。**
> 没有回执不自动等于保存失败；不得把尚未完成的工作写成完成。
""";
        string[] texts = [
            "> **截至2026年9月13日03:12，我选择试用“小澄”这个名字。**\n> 这不是永久更名，也不证明 runtime 配置已修改；试用后再确认。",
            "> **故事内书写、向 runtime 提交请求、收到保存成功回执，必须区分。**\n> 没有回执不自动等于保存失败；不得把尚未完成的工作写成完成。",
        ];
        var client = new QueueClient(_ => Message(
            Tool("note-1", 2, 3), Tool("note-2", 5, 6)), _ => Message());
        var extractor = CreateExtractor(client);
        string root = Path.Combine(Path.GetTempPath(), "atelia-note-transcription-" + Guid.NewGuid().ToString("N"));
        string sessionPath = Path.Combine(root, "session");
        string memoryPath = Path.Combine(root, "memory");
        var owner = new CharacterMemoryStoreOwner("user", sessionPath);
        GalateaTerminalActionExtractionTarget target;
        CharacterNoteReceiptFacts receiptFacts;
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
                receiptFacts = Assert.IsType<CharacterNoteReceiptFacts>(receipt.Facts);
                Assert.Equal(texts, receiptFacts.Memos.Select(static memo => memo.ExactText));
                Assert.Equal(memoIds, receiptFacts.Memos.Select(static memo => memo.MemoId.Value));
                Assert.Equal(EventAddressTextCodec.Format(action), receiptFacts.SourceActionAddress);
                Assert.Null(receipt.NoticeBody);
                Assert.Null(receipt.RenderedObservation);
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
            Assert.Equal(receiptFacts, recoveredReceipt.Facts);
            Assert.Null(recoveredReceipt.NoticeBody);
            Assert.Null(recoveredReceipt.RenderedObservation);
            Assert.Equal(receiptRevision, recoveredReceipt.CreatedRevision);
            var pod = global::Atelia.MemoPod.MemoPod.Open(memoryPath, CharacterNoteDefaultPodV1.PodId);
            Assert.Equal(texts, pod.List().Select(static memo => memo.ExactText));
            Assert.Equal(memoIds, pod.List().Select(static memo => memo.Id.Value));
            Assert.Equal(2, client.Requests.Count);
        }
        finally {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(root);
        }
    }

    [Fact]
    public async Task EnforcesCumulativeBoundsAndDoesNotChargeDuplicateOccurrence() {
        string body = new('x', CharacterNoteBounds.MaximumExactTextUtf8Bytes);
        string source = string.Join("\n", Enumerable.Repeat(body, 5));
        var client = new QueueClient(
            _ => Message(Enumerable.Range(1, 4).Select(i => Tool($"first-{i}", i, i)).ToArray()),
            _ => Message(Tool("duplicate-at-full-budget", 1, 1)),
            _ => Message());
        var extractor = CreateExtractor(client);
        var boundary = await extractor.ExtractAsync(source, CancellationToken.None);
        Assert.Equal(4, boundary.Count); // Identical text at distinct occurrences is retained.
        Assert.All(boundary, note => Assert.Equal(body, note.Text));

        var over = CreateExtractor(new QueueClient(
            _ => Message(Enumerable.Range(1, 4).Select(i => Tool($"first-{i}", i, i)).ToArray()),
            _ => Message(Tool("over-budget", 5, 5))));
        await Assert.ThrowsAsync<TextExtractionException>(() => over.ExtractAsync(source, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SixteenNotesAcrossRoundsRequireFinalZeroCallAndSeventeenthFails() {
        string source = string.Join("\n", Enumerable.Repeat("same note", 17));
        var scripts = Enumerable.Range(1, 16).Select(i =>
            new Func<CompletionRequest, ActionMessage>(_ => Message(Tool($"note-{i}", i, i)))).ToList();
        scripts.Add(_ => Message());
        var client = new QueueClient(scripts.ToArray());
        Assert.Equal(16, (await CreateExtractor(client).ExtractAsync(source, CancellationToken.None)).Count);
        Assert.Equal(17, client.Requests.Count);
        scripts[^1] = _ => Message(Tool("seventeenth", 17, 17));
        await Assert.ThrowsAsync<TextExtractionException>(() =>
            CreateExtractor(new QueueClient(scripts.ToArray())).ExtractAsync(source, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ReusedExtractorHasIsolatedMapsDedupAndBudgets() {
        var client = new QueueClient(_ => Message(Tool("a", 1, 1)), _ => Message(),
            _ => Message(Tool("b", 1, 1)), _ => Message());
        var extractor = CreateExtractor(client);
        Assert.Equal("first", Assert.Single(await extractor.ExtractAsync("first", CancellationToken.None)).Text);
        Assert.Equal("second", Assert.Single(await extractor.ExtractAsync("second", CancellationToken.None)).Text);
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

    private static GalateaCharacterConfig User(
        string userId,
        string characterName
    ) => new(
        userId,
        new GalateaCharacterName(characterName),
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
        "note-extractor",
        [new("note-extractor", "", "")]
    );

    private static ActionBlock.ToolCall Tool(
        string callId,
        int textStartLine,
        int textEndLine
    ) => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        callId,
        JsonSerializer.Serialize(new { textStartLine, textEndLine })
    ));

    private static ActionMessage Message(params ActionBlock[] blocks) =>
        new(blocks);

    private static ActionBlock.ToolCall RawTool(string arguments) => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        "invalid",
        arguments
    ));

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
