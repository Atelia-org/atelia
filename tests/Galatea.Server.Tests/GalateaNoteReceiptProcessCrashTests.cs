using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
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

    [Fact]
    public async Task ReceiptBoundWithDurableObservation_HardKill_ReconcilesBeforeAuthorizedCompletion() {
        await using var provider = await GalateaLabNoteReceiptResponsesServer.StartAsync();
        var clock = new GalateaLabClock();
        var seedFactory = new GalateaNoteReceiptFixture.Factory(epoch: 1);
        await using var lab = GalateaScenarioLab.Create("note-receipt-process-crash", seedFactory,
            connections: [Connection("test", GalateaLabNoteReceiptResponsesServer.MainModel, provider),
                Connection("note-helper", GalateaLabNoteReceiptResponsesServer.HelperModel, provider)],
            timeProvider: clock, reportArtifact: output.WriteLine,
            characterNoteExtractorConnectionId: "note-helper",
            serverAgentUserIds: ["alice"], enableServerAgentHostedService: true);

        // Real Note extraction, SQLite apply, MemoPod save and valid DerivedInfo
        // settlement; only the seed's LLM boundary is deterministic in-process.
        var epoch = await GalateaNoteReceiptFixture.StartEpochAsync(lab, clock, seedFactory);
        await GalateaNoteReceiptFixture.AdvanceHeartbeatAsync(epoch);
        var pending = await GalateaNoteReceiptFixture.ReadStateAsync(epoch);
        Assert.Equal(CharacterNoteReceiptDeliveryState.Pending, pending.Receipt.State);
        Assert.Single(pending.Turns);
        Assert.Equal(1, seedFactory.SaveIntents);
        Assert.Equal(1, seedFactory.DerivedCalls);
        string memoryDirectory = epoch.Session.User.CharacterMemoryStateDir;
        string configPath = lab.Host.ConfigPath;
        var memoryOwner = new CharacterMemoryStoreOwner("alice",
            CharacterMemorySessionComposition.CreateSessionRepositoryId(lab.SessionDirectory));
        await lab.StopAsync();

        // Only this owned, stopped synthetic instance is changed. Freeze the
        // real-process configuration before its first admission, then keep it
        // identical for restart. Main/helper endpoints were local from birth.
        JsonNode configuration = JsonNode.Parse(File.ReadAllText(configPath))!;
        configuration["serverAgentUserIds"] = new JsonArray();
        File.WriteAllText(configPath, configuration.ToJsonString());
        string configDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath)));
        provider.ExpectReceipt(pending.Receipt.NoticeBody);

        await using (GalateaLabServerProcess first = await GalateaLabServerProcess.StartAsync(
                         lab.RootDirectory, configPath)) {
            using HttpClient http = first.CreateClient();
            await LoginAsync(http);
            using HttpResponseMessage accepted = await http.PostAsJsonAsync("/api/v1/chat/turns",
                new ChatStreamRequest(GalateaLabNoteReceiptResponsesServer.UserMessage));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            await provider.FirstReceived.WaitAsync(Deadline);
            await first.KillAsync(); // No ApplicationStopping, runner finally or Dispose.
        }
        await provider.FirstDisconnected.WaitAsync(Deadline);
        provider.AssertBeforeRestart();

        EventAddress startedHead;
        EventAddress observationAddress;
        string renderedObservation;
        CharacterNoteReceiptDeliverySnapshot bound;
        using (SessionJournalEngine engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory))
        using (CharacterMemorySqliteStore store = CharacterMemorySqliteStore.OpenExisting(
                   memoryDirectory, memoryOwner)) {
            bound = Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
                store.ReadReceiptDeliveryExact(pending.Receipt.SourceActionAddress));
            Assert.Equal(CharacterNoteReceiptDeliveryState.ObservationBound, bound.State);
            Assert.Equal(pending.Receipt.NoticeBody, bound.NoticeBody);
            Assert.Equal(pending.Receipt.CreatedRevision, bound.CreatedRevision);
            Assert.Null(bound.ObservationAddress);
            renderedObservation = Assert.IsType<string>(bound.RenderedObservation);
            Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(renderedObservation, out var observation));
            Assert.Equal(PlayerTurnObservationTriggerKind.PlayerAction, observation.TriggerKind);
            Assert.Equal(pending.Receipt.NoticeBody,
                Assert.Single(observation.Notices.OfType<PlayerTurnNotice.NoteSaveReceipt>()).Body);
            var frozen = Assert.IsType<SessionRuntimeRecoveryRequirements.FrozenCompletionRequired>(
                engine.InspectRuntimeRecoveryRequirements());
            Assert.Equal(SessionExecutionPhase.AwaitingCompletion, frozen.Phase);
            Assert.Equal(SessionDurableDispatchState.StartedOutcomeUncertain, frozen.DispatchState);
            startedHead = frozen.CapturedHead!.Value;
            var proof = Assert.IsType<SessionExpectedObservationTurnReadResult.InProgress>(
                engine.ReadView.ProveExpectedObservationTurnAtSelectedHead(
                    new SessionExpectedObservationTurnRequest(startedHead,
                        EventAddressTextCodec.Parse(bound.ExpectedSessionHead!), renderedObservation)));
            observationAddress = proof.Evidence.ObservationAddress;
            AssertSeedNoteUnchanged(store, memoryDirectory, pending);
            AssertAuditCounts(engine, observations: 2, prepared: 2, started: 2, actions: 1);
        }

        long deliveredRevision;
        await using (GalateaLabServerProcess restarted = await GalateaLabServerProcess.StartAsync(
                         lab.RootDirectory, configPath)) {
            using HttpClient http = restarted.CreateClient();
            await LoginAsync(http);
            // This request awaits production attach/reconcile; no polling of an
            // arbitrary SQLite timing window is needed. Completion stays uncertain.
            CurrentTurnDto current = Assert.IsType<CurrentTurnDto>(
                await http.GetFromJsonAsync<CurrentTurnDto>("/api/v1/chat/turns/current"));
            Assert.Equal("recovery-required", current.Status);
            Assert.True(current.RestartRequired);
            Assert.Equal(EventAddressTextCodec.Format(startedHead), current.RecoveryHead);
            deliveredRevision = ReadAcknowledgmentWhileRunning(memoryDirectory,
                bound.SourceActionAddress, observationAddress, bound.ExpectedSessionHead!);
            Assert.True(deliveredRevision > bound.StateRevision);
            provider.AssertBeforeRestart();

            using (HttpResponseMessage refused = await http.PostAsJsonAsync("/api/v1/chat/turns/resume",
                       new ResumeTurnRequest(EventAddressTextCodec.Format(startedHead)))) {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                using JsonDocument problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
                Assert.Equal("uncertain-completion-restart-required",
                    problem.RootElement.GetProperty("code").GetString());
            }
            provider.AssertBeforeRestart();
            provider.AuthorizeRestart();
            using HttpResponseMessage accepted = await http.PostAsJsonAsync("/api/v1/chat/turns/resume",
                new ResumeTurnRequest(EventAddressTextCodec.Format(startedHead),
                    RestartUncertainCompletion: true));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var turn = Assert.IsType<StartTurnResponseDto>(
                await accepted.Content.ReadFromJsonAsync<StartTurnResponseDto>());
            using HttpResponseMessage stream = await http.GetAsync($"/api/v1/chat/turns/{turn.TurnId}/events");
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
            await WaitForIdleAsync(http);
            provider.AssertComplete();
            await restarted.KillAsync();
        }

        using (SessionJournalEngine engine = SessionJournalEngine.OpenReadOnly(lab.SessionDirectory))
        using (CharacterMemorySqliteStore store = CharacterMemorySqliteStore.OpenExisting(
                   memoryDirectory, memoryOwner)) {
            Assert.Equal(SessionExecutionPhase.Idle, engine.InspectExecutionBoundary().Phase);
            var delivered = Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
                store.ReadReceiptDeliveryExact(bound.SourceActionAddress));
            Assert.Equal(CharacterNoteReceiptDeliveryState.Delivered, delivered.State);
            Assert.Equal(deliveredRevision, delivered.StateRevision);
            Assert.Equal(pending.Receipt.NoticeBody, delivered.NoticeBody);
            Assert.Equal(pending.Receipt.CreatedRevision, delivered.CreatedRevision);
            Assert.Equal(EventAddressTextCodec.Format(observationAddress), delivered.ObservationAddress);
            Assert.Null(delivered.RenderedObservation);
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
                completed.TerminalAction.Message.GetFlattenedText());
            int receipts = 0;
            foreach (SessionCompletedTurnProjection turn in turns) {
                Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(turn.ObservationContent, out var observation));
                receipts += observation.Notices.OfType<PlayerTurnNotice.NoteSaveReceipt>().Count();
            }
            Assert.Equal(1, receipts);
            AssertAuditCounts(engine, observations: 2, prepared: 2, started: 3, actions: 2);
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
            SELECT state, state_revision, observation_address, rendered_observation, expected_session_head
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
                await http.GetFromJsonAsync<CurrentTurnDto>("/api/v1/chat/turns/current", deadline.Token));
            if (current.Status != "running") {
                Assert.Equal("idle", current.Status);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }
}
