using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// Hard-kills the production server after an Observation carrying a Note
/// receipt reached the provider, before SQLite delivery acknowledgment. The
/// crash trigger is a Player HTTP request, not evidence about autonomous
/// scheduling. Prepared/Started are also committed at this checkpoint; this
/// is not an Observation-only failpoint or a power-loss durability test.
/// </summary>
[Trait("Category", "GalateaLab")]
public sealed class GalateaNoteReceiptProcessCrashTests(ITestOutputHelper output) {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalTextReceipt_ColdReconstructKeepsOriginalObservationAndFrozenPreparedBytes(bool started) {
        string root = Directory.CreateTempSubdirectory("atelia-historical-note-receipt-").FullName;
        string repository = Path.Combine(root, "session");
        const string historicalBody = "Galatea runtime 已将以下 1 条 Note 原文成功保存到默认MemoPod。\n\n"
            + "本回执只证明以下原文已保存；不承诺分类、metadata补全或召回。\n\n已保存的 Note 原文：\n"
            + GalateaNoteReceiptFixture.NoteText;
        string historicalText = PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation(
            "Historical continuation.", ActionReceiptDeliveryFixture.Timestamp, [new PlayerTurnNotice.NoteSaveReceipt(historicalBody)]));
        var client = new HistoricalNoDispatchClient();
        var connection = new CompletionConnectionConfig("test", "openai-chat", "model-a", "openai-chat/strict",
            "http://synthetic-history.invalid/", ApiKey: "synthetic-history-key");
        try {
            EventAddress head = LegacyPreparedV7Fixture.CreatePending(repository, connection, client,
                started, "historic-note-adapter", historicalText);
            SessionPreparedRequestReconstruction frozen = GalateaRecapFixture.ReadLatestPrepared(repository);
            byte[] preparedBytes;
            byte[] observationBytes;
            EventAddress observation;
            using (SessionJournalEngine first = SessionJournalEngine.OpenReadOnly(repository)) {
                var events = new List<SessionJournalAuditEvent>();
                first.ScanCheckedAuditEvents(events.Add);
                observation = Assert.Single(events, item => item.Kind == SessionEventKind.ObservationAccepted).Address;
                Assert.Equal(1, Assert.Single(events, item => item.Kind == SessionEventKind.ObservationAccepted).BodySchemaVersion);
                Assert.Equal(7, Assert.Single(events, item => item.Kind == SessionEventKind.CompletionRequestPrepared).BodySchemaVersion);
                preparedBytes = first.ReadPayloadBytes(frozen.SourcePreparedAddress!.Value);
                observationBytes = first.ReadPayloadBytes(observation);
                Assert.Equal(head, first.ReadCurrentHead());
            }
            SessionPreparedRequestReconstruction cold = GalateaRecapFixture.ReadLatestPrepared(repository);
            Assert.Equal(frozen.SourcePreparedAddress, cold.SourcePreparedAddress);
            Assert.Equal(frozen.CanonicalBytes, cold.CanonicalBytes);
            Assert.Equal(frozen.Manifest.Commitment, cold.Manifest.Commitment);
            Assert.Equal(historicalText, cold.Request.PromptPrefix.SharedContextMessages.OfType<ObservationMessage>().Last().Content);
            Assert.Contains(GalateaNoteReceiptFixture.UniqueMiddle,
                cold.Request.PromptPrefix.SharedContextMessages.OfType<ObservationMessage>().Last().Content, StringComparison.Ordinal);
            using SessionJournalEngine reopened = SessionJournalEngine.OpenReadOnly(repository);
            Assert.Equal(preparedBytes, reopened.ReadPayloadBytes(cold.SourcePreparedAddress!.Value));
            Assert.Equal(observationBytes, reopened.ReadPayloadBytes(observation));
            Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(reopened.InspectRuntimeRecoveryRequirements());
            Assert.Equal(0, client.Calls);
        }
        finally { TestDirectorySafety.DeleteOwnedTreeNoFollow(root); }
    }

    [Fact]
    public async Task ReceiptBoundWithDurableObservation_HardKill_AutomaticallyReconcilesAndCompletes() {
        await using var provider = await GalateaLabNoteReceiptResponsesServer.StartAsync();
        var clock = new GalateaLabClock();
        var seedFactory = new GalateaNoteReceiptFixture.Factory(epoch: 1);
        await using var lab = GalateaScenarioLab.Create("note-receipt-process-crash", seedFactory,
            connections: [Connection("test", GalateaLabNoteReceiptResponsesServer.MainModel, provider),
                Connection("note-helper", GalateaLabNoteReceiptResponsesServer.HelperModel, provider)],
            timeProvider: clock, reportArtifact: output.WriteLine,
            characterNoteExtractorConnectionId: "note-helper",
            autonomyCharacterIds: ["alice"], enableServerAgentHostedService: true);

        // Real Note extraction, SQLite apply, MemoPod save and valid DerivedInfo
        // settlement; only the seed's LLM boundary is deterministic in-process.
        var epoch = await GalateaNoteReceiptFixture.StartEpochAsync(lab, clock, seedFactory);
        await GalateaNoteReceiptFixture.AdvanceHeartbeatAsync(epoch);
        var pending = await GalateaNoteReceiptFixture.ReadStateAsync(epoch);
        Assert.Equal(ActionReceiptDeliveryState.Pending, pending.Receipt.State);
        Assert.Single(pending.Turns);
        Assert.Equal(1, seedFactory.SaveIntents);
        Assert.Equal(1, seedFactory.DerivedCalls);
        string memoryDirectory = epoch.Session.Character.CharacterMemoryStateDir;
        string configPath = lab.Host.ConfigPath;
        var memoryOwner = new CharacterMemoryStoreOwner("alice",
            CharacterMemorySessionComposition.CreateSessionRepositoryId(lab.SessionDirectory));
        await lab.StopAsync();

        // Only this owned, stopped synthetic instance is changed. Freeze the
        // real-process configuration before its first admission, then keep it
        // identical for restart. Main/helper endpoints were local from birth.
        JsonNode configuration = JsonNode.Parse(File.ReadAllText(configPath))!;
        foreach (JsonNode? character in configuration["characters"]!.AsArray()) { character!["autonomyIntervalMinutes"] = 0; }
        File.WriteAllText(configPath, configuration.ToJsonString());
        string configDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath)));
        provider.ExpectReceipt(pending.Receipt);

        await using (GalateaLabServerProcess first = await GalateaLabServerProcess.StartAsync(
                         lab.RootDirectory, configPath)) {
            using HttpClient http = first.CreateClient();
            await LoginAsync(http);
            using HttpResponseMessage accepted = await GalateaLabAdmission.PostFreshAsync(http,
                new ChatStreamRequest(GalateaLabNoteReceiptResponsesServer.UserMessage));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            await provider.FirstReceived.WaitAsync(Deadline);
            await first.KillAsync(); // No ApplicationStopping, runner finally or Dispose.
        }
        await provider.FirstDisconnected.WaitAsync(Deadline);
        provider.AssertBeforeRestart();

        EventAddress startedHead;
        EventAddress observationAddress;
        SessionInputContent renderedObservation;
        ActionReceiptDeliverySnapshot bound;
        using (SessionJournalEngine engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory))
        using (CharacterMemorySqliteStore store = CharacterMemorySqliteStore.OpenExisting(
                   memoryDirectory, memoryOwner)) {
            bound = Assert.IsType<ActionReceiptDeliverySnapshot>(
                store.ReadReceiptDeliveryExact(pending.Receipt.SourceActionAddress));
            Assert.Equal(ActionReceiptDeliveryState.ObservationBound, bound.State);
            Assert.Equal(pending.Receipt.FrozenBatch, bound.FrozenBatch);
            Assert.Equal(pending.Receipt.CreatedRevision, bound.CreatedRevision);
            Assert.Null(bound.ObservationAddress);
            renderedObservation = Assert.IsType<SessionInputContent>(bound.BoundInput);
            PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(renderedObservation);
            Assert.Equal(PlayerTurnObservationTriggerKind.PlayerAction, observation.TriggerKind);
            var batch = Assert.IsType<NoteReceiptBatch>(
                Assert.Single(observation.Notices.OfType<PlayerTurnNotice.ActionReceipt>()).Batch);
            Assert.Equal(ActionReceiptPreview.Create(GalateaNoteReceiptFixture.NoteText), Assert.Single(batch.Items).Preview);
            Assert.DoesNotContain(GalateaNoteReceiptFixture.UniqueMiddle,
                GalateaInputProjector.Instance.Project(renderedObservation), StringComparison.Ordinal);
            var frozen = Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(
                engine.InspectRuntimeRecoveryRequirements());
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, frozen.Phase);
            Assert.NotEqual(default, frozen.SourcePreparedAddress);
            startedHead = frozen.CapturedHead!.Value;
            var proof = Assert.IsType<SessionExpectedObservationTurnReadResult.InProgress>(
                engine.ReadView.ProveExpectedObservationTurnAtSelectedHead(
                    new SessionExpectedObservationTurnRequest(startedHead,
                        EventAddressTextCodec.Parse(bound.ExpectedSessionHead!), renderedObservation)));
            observationAddress = proof.Evidence.ObservationAddress;
            AssertSeedNoteUnchanged(store, memoryDirectory, pending);
            AssertAuditCounts(engine, observations: 2, prepared: 2, started: 0, actions: 1);
        }

        long deliveredRevision;
        provider.AssertBeforeRestart();
        provider.AuthorizeRestart();
        await using (GalateaLabServerProcess restarted = await GalateaLabServerProcess.StartAsync(
                         lab.RootDirectory, configPath)) {
            using HttpClient http = restarted.CreateClient();
            await LoginAsync(http);
            await WaitForIdleAsync(http);
            deliveredRevision = ReadAcknowledgmentWhileRunning(memoryDirectory,
                bound.SourceActionAddress, observationAddress, bound.ExpectedSessionHead!);
            Assert.True(deliveredRevision > bound.StateRevision);
            provider.AssertComplete();
            await restarted.KillAsync();
        }

        using (SessionJournalEngine engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory))
        using (CharacterMemorySqliteStore store = CharacterMemorySqliteStore.OpenExisting(
                   memoryDirectory, memoryOwner)) {
            Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
            var delivered = Assert.IsType<ActionReceiptDeliverySnapshot>(
                store.ReadReceiptDeliveryExact(bound.SourceActionAddress));
            Assert.Equal(ActionReceiptDeliveryState.Delivered, delivered.State);
            Assert.Equal(deliveredRevision, delivered.StateRevision);
            Assert.Null(delivered.FrozenBatch);
            Assert.Equal(pending.Receipt.CreatedRevision, delivered.CreatedRevision);
            Assert.Equal(EventAddressTextCodec.Format(observationAddress), delivered.ObservationAddress);
            Assert.Null(delivered.BoundInput);
            Assert.Null(store.ReadPendingReceiptDelivery());
            Assert.Null(store.ReadBoundReceiptDelivery());
            AssertSeedNoteUnchanged(store, memoryDirectory, pending);
            IReadOnlyList<SessionCompletedTurnProjection> turns = engine.ReadRecentCompletedTurns()
                .RequireSnapshot().Turns;
            Assert.Equal(2, turns.Count);
            SessionCompletedTurnProjection completed = Assert.Single(turns,
                turn => turn.ObservationAddress == observationAddress);
            Assert.Equal(renderedObservation, completed.ObservationContent);
            Assert.Equal(GalateaLabNoteReceiptResponsesServer.Answer,
                completed.RequireTerminalAction().Message.GetFlattenedText());
            int receipts = 0;
            foreach (SessionCompletedTurnProjection turn in turns) {
                PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(turn.ObservationContent);
                receipts += observation.Notices.OfType<PlayerTurnNotice.ActionReceipt>().Count();
            }
            Assert.Equal(1, receipts);
            AssertAuditCounts(engine, observations: 2, prepared: 2, started: 0, actions: 2);
        }
        Assert.Equal(configDigest, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath))));
        // Drain the external script before accepting its final counts/errors;
        // only then may the lab mark its durable evidence safe to delete.
        await provider.DisposeAsync();
        provider.AssertComplete();
        await lab.CompleteAsync();
    }

    private static CompletionConnectionConfig Connection(string id, string model,
        GalateaLabNoteReceiptResponsesServer provider) => new(id, "openai-responses", model,
        "openai-responses", provider.BaseAddress.AbsoluteUri, ApiKey: GalateaLabNoteReceiptResponsesServer.ApiKey);

    private sealed class HistoricalNoDispatchClient : ICompletionClient {
        internal int Calls { get; private set; }
        public string Name => "historical-receipt-fixture";
        public string ApiSpecId => "fixture-history-v1";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Calls++;
            throw new Xunit.Sdk.XunitException("Historical reconstruction must not dispatch provider work.");
        }
    }

    private static void AssertSeedNoteUnchanged(CharacterMemorySqliteStore store, string memoryDirectory,
        GalateaNoteReceiptFixture.DurableState pending) {
        Assert.Equal(pending.PodIdentity, store.ReadStatusSnapshot().SettledDefaultPodStateIdentity);
        Assert.Null(store.ReadStatusSnapshot().ActiveDerivedInfoSourceAction);
        Assert.Equal(pending.Note, Assert.Single(global::Atelia.MemoPod.MemoPod.Open(
            memoryDirectory, CharacterNoteDefaultPodV1.PodId).List()));
        Assert.Equal(CharacterMemoryDerivedInfoState.Applied,
            Assert.IsType<CharacterMemoryDerivedInfoWorkSnapshot>(
                store.ReadDerivedInfoWorkExact(pending.Receipt.SourceActionAddress)).State);
    }

    private static void AssertAuditCounts(SessionJournalEngine engine, int observations,
        int prepared, int started, int actions) {
        var events = new List<SessionJournalAuditEvent>();
        engine.ScanCheckedAuditEvents(events.Add);
        Assert.Equal(observations, events.Count(item => item.Kind == SessionEventKind.ObservationAccepted));
        Assert.Equal(prepared, events.Count(item => item.Kind == SessionEventKind.CompletionRequestPrepared));
        Assert.Equal(started, events.Count(item => item.Kind == SessionEventKind.CompletionAttemptStarted));
        Assert.Equal(actions, events.Count(item => item.Kind == SessionEventKind.AgentActionProduced));
        Assert.DoesNotContain(events, item => item.Kind == SessionEventKind.CompletionAttemptFailed);
    }

    private static long ReadAcknowledgmentWhileRunning(string memoryDirectory, string source,
        EventAddress observation, string expectedHead) {
        // A short, read-only diagnostic snapshot, not a second application
        // writer and not a bypass of the store's recovery protocol.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(memoryDirectory, CharacterMemorySqliteStore.DatabaseFileName),
            Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT state, state_revision, observation_address, bound_input, expected_session_head
            FROM note_receipt_delivery WHERE source_action_address = $source;
            """;
        command.Parameters.AddWithValue("$source", source);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Delivered", reader.GetString(0));
        long revision = reader.GetInt64(1);
        Assert.Equal(EventAddressTextCodec.Format(observation), reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal(expectedHead, reader.GetString(4));
        Assert.False(reader.Read());
        return revision;
    }

    private static async Task LoginAsync(HttpClient http) {
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    private static async Task WaitForIdleAsync(HttpClient http) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (true) {
            CurrentTurnDto current = Assert.IsType<CurrentTurnDto>(
                await http.GetFromJsonAsync<CurrentTurnDto>("/api/v1/characters/alice/chat/turns/current", deadline.Token));
            if (current.Status == "idle") { return; }
            Assert.Contains(current.Status, new[] { "running", "recovery-required" });
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }
}
