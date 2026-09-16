using System.Text.Json;
using Atelia.MdJson;
using Atelia.SessionJournal;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaStructuredInputTests {
    private static readonly DateTimeOffset Time = new(2026, 9, 15, 10, 20, 30, TimeSpan.FromHours(8));

    [Fact]
    public async Task ActualHost_PlayerContent_ReachesProviderAndSurvivesQueryUndoAndColdAudit() {
        const string action = "  我打开门。\r\n```\n{\"raw\":\"\\n\"}\n```\n";
        var factory = new CapturingFactory();
        GalateaTestHost host = GalateaTestHost.Create(factory,
            DisabledGalateaUserMessageNormalizer.Instance, deleteFilesOnDispose: false);
        try {
            GalateaHostService service = host.Factory.Services.GetRequiredService<GalateaHostService>();
            CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
            GalateaLiveTurn turn = service.StartTurn(session, action, new GalateaTurnOptions("test"),
                new GalateaSenderSnapshot("player", "player-main", "访客"));
            await service.RunTurnAsync(session, turn, CancellationToken.None);
            service.FinishTurn(session, turn);
            Assert.Equal("completed", turn.Status);

            CompletionRequest request = Assert.Single(factory.Requests);
            JsonElement system = MdJsonSerializer.Read(Assert.IsType<string>(request.PromptPrefix.SystemPrompt));
            Assert.Equal("system-instructions", system.GetProperty("kind").GetString());
            Assert.Equal("alice", system.GetProperty("bindings").GetProperty("character").GetProperty("id").GetString());
            ObservationMessage sent = Assert.Single(request.PromptPrefix.SharedContextMessages.OfType<ObservationMessage>());
            Assert.Equal(action, MdJsonSerializer.Read(Assert.IsType<string>(sent.Content)).GetProperty("action").GetProperty("text").GetString());
            var completed = Assert.Single(session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
            Assert.True(completed.ObservationContent.IsStructured);
            Assert.Equal(action, GalateaRecentTurnDisplayAdapter.Project(completed).UserText);

            // Cold readers do not receive any projector. They inspect the original semantic facts.
            await host.DisposeAsync();
            using (SessionJournalEngine reader = SessionJournalEngine.OpenReadOnly(host.SessionDirectory)) {
                var reopened = Assert.Single(reader.ReadRecentCompletedTurns().RequireSnapshot().Turns);
                Assert.Equal(completed.ObservationContent, reopened.ObservationContent);
                _ = reader.ScanCheckedAuditEvents(_ => { });
            }
            using (SessionJournalEngine writer = SessionJournalEngine.Open(host.SessionDirectory)) {
                var preparation = Assert.IsType<SessionCompletedTurnRewindPrepareResult.Prepared>(
                    writer.PrepareLatestCompletedTurnRewind(completed.TerminalAction.Address));
                Assert.True(PlayerTurnObservationClassifier.TryProject(preparation.Value.ObservationContent, out var projection));
                Assert.Equal(action, projection.RestorablePlayerText);
                Assert.IsType<SessionTurnRetractionResult.Moved>(writer.CommitPreparedCompletedTurnRewind(preparation.Value));
                Assert.Empty(writer.ReadRecentCompletedTurns().RequireSnapshot().Turns);
            }
        }
        finally {
            await host.DisposeAsync();
            if (Directory.Exists(host.RootDirectory)) { Directory.Delete(host.RootDirectory, recursive: true); }
        }
    }

    [Fact]
    public void PlayerContent_PreservesSourceAndVerbatimBody_ThroughMdJson() {
        const string body = "  我推开门。\r\n```json\n{\"nested\":\"\\n\"}\n```\n~~~~\n末尾 \n";
        SessionInputContent content = GalateaObservationContent.CreatePlayerAction(
            new GalateaSenderSnapshot("player", "player-main", "原来的名字"), body, Time);

        string rendered = GalateaInputProjector.Instance.Project(content);
        JsonElement decoded = MdJsonSerializer.Read(rendered);

        Assert.True(content.IsStructured);
        Assert.Equal("galatea.observation.v1", content.SchemaId);
        Assert.Equal(body, content.JsonValue.GetProperty("action").GetProperty("text").GetString());
        Assert.Equal(body, decoded.GetProperty("action").GetProperty("text").GetString());
        Assert.Equal("原来的名字", decoded.GetProperty("sender").GetProperty("name").GetString());
        Assert.Contains(body, rendered);
        Assert.DoesNotContain("# structure", content.JsonValue.GetRawText());
        Assert.True(GalateaObservationContent.TryReadPlayerText(content, out string restored));
        Assert.Equal(body, restored);
    }

    [Fact]
    public void PlayerAction_RejectsCharacterIdentity() {
        Assert.Throws<ArgumentException>(() => GalateaObservationContent.CreatePlayerAction(
            new GalateaSenderSnapshot("character", "g-01", "Galatea"), "hello", Time));
    }

    [Theory]
    [InlineData("{\"v\":1,\"v\":1,\"kind\":\"player-action\"}")]
    [InlineData("{\"v\":1,\"kind\":\"player-action\",\"extra\":1}")]
    public void DomainRead_RejectsDuplicateOrUnknownFields(string json) {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => GalateaObservationContent.Validate(GalateaObservationContent.V1SchemaId, document.RootElement));
    }

    [Fact]
    public void InputProjection_RejectsUnknownSchema_InsteadOfSendingJson() {
        using JsonDocument document = JsonDocument.Parse("{}");
        SessionInputContent content = SessionInputContent.Structured("unknown.domain.v1", document.RootElement);
        Assert.Throws<NotSupportedException>(() => GalateaInputProjector.Instance.Project(content));
    }

    [Fact]
    public void SystemContent_StoresSourcesAndBindings_ProjectsOnlyInMemory() {
        const string source = "${characterName}记得一段原文。\r\n```\n原文\n```";
        SessionInputContent content = GalateaSystemPromptComposer.CreateContent(
            new GalateaSenderSnapshot("character", "g-01", "${playerName}字面名字"),
            source, outboundMailEnabled: true, characterNoteSaveEnabled: false,
            "/galatea-homes/g-01", [new GalateaSenderSnapshot("character", "g-02", "另一个角色")]);
        string before = content.JsonValue.GetRawText();

        GalateaSystemInstructionContent.Validate(content.JsonValue);
        string rendered = GalateaInputProjector.Instance.Project(content);
        JsonElement decoded = MdJsonSerializer.Read(rendered);

        Assert.Equal(before, content.JsonValue.GetRawText());
        Assert.Equal(source, content.JsonValue.GetProperty("instructions")[1].GetProperty("source").GetString());
        Assert.Equal("${playerName}字面名字记得一段原文。\r\n```\n原文\n```",
            decoded.GetProperty("instructions")[1].GetProperty("source").GetString());
        Assert.Equal("/galatea-homes/g-01", decoded.GetProperty("bindings").GetProperty("homeDir").GetString());
        Assert.Contains("不是 Unix $HOME", content.JsonValue.GetProperty("instructions")[4].GetProperty("source").GetString());
    }

    [Fact]
    public void SystemContent_RejectsLegacyPlayerBinding() {
        Assert.Throws<ArgumentException>(() => GalateaSystemPromptComposer.CreateContent(
            new GalateaSenderSnapshot("character", "g-01", "Galatea"),
            "${characterName}和${playerName}", false, false, null));
    }

    [Theory]
    [InlineData("${characterName}和${characterNam}")]
    [InlineData("${characterName}和${unfinished")]
    [InlineData("context without a character binding")]
    public void SystemContent_RejectsInvalidClosedTemplateLanguage(string source) {
        Assert.Throws<InvalidDataException>(() => GalateaSystemPromptComposer.CreateContent(
            new GalateaSenderSnapshot("character", "g-01", "Galatea"), source, false, false, null));
    }

    private sealed class CapturingFactory : ICompletionClientFactory, ICompletionClient {
        internal List<CompletionRequest> Requests { get; } = [];
        public string Name => "structured-input-test";
        public string ApiSpecId => "openai-chat-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.Text("门打开了。")]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)));
        }
    }
}
